"""Stage 2: LoRA fine-tune of Fish Audio S2 Pro on one character's prepared dataset (server/finetune/prepare_dataset.py), then
pick a checkpoint by measuring it against zero-shot S2 Pro, and export it for the voice server.

    modal run server/finetune/train.py --speaker lamar                                   # tokenise + train
    modal run server/finetune/train.py::compare --speaker lamar --steps 250,500,1000     # merge those, score vs zero-shot
    modal run server/finetune/train.py::export --speaker lamar --step 500                # /models/lamar-s2pro for server/s2pro.py

Fish recommends the earliest checkpoint that sounds right (later ones overfit), and warns that
fine-tuning an RL-aligned model can degrade it, so `compare` scores every candidate and the base model
on the same lines with the same reference clip: speaker similarity to the character's real clips
(ECAPA) and how intelligible it is (Whisper word error rate). Samples land in data/finetune/<speaker>/.
"""
import json
import threading
from pathlib import Path

import modal

from fish_common import CKPT, REPO, VOLUMES, fish_image, models, require, run, vol

app = modal.App("npcvoice-train")

LORA = "r_8_alpha_16"

# New lines in the character's style (not from the game) for comparing checkpoints.
TEST_LINES = {
    "lamar": [
        "Ayy, what's up, homie? You looking like a broke-ass busta today, for real.",
        "Man, I told you I got this. Trust me, dog, this the best idea I ever had.",
        "[laughing] Nah, nah, you trippin'! That ain't how it went down and you know it.",
        "[angry] Get your punk ass outta my face before I do something stupid, man!",
        "Yo, check it, I got a new rap. It's about my grandma's dog. It's deep, homie.",
        "You see this jacket? Limited edition. Only twelve of these in the whole city, dog.",
        "[whispering] Yo, keep it down, homie. Them Ballas posted up right around the corner.",
        "Franklin, you remember when we was kids and you ate that whole pack of crayons? I still got the pictures, dog.",
        "[excited] Ayy, we rich, homie! We rich! I'm finna buy my grandma a jet ski!",
        "[sigh] Man, sometimes I think about just leaving Los Santos, you know? Start over somewhere quiet.",
    ],
}


def project(speaker: str) -> str:
    return f"{speaker}_s2pro_chat"  # (_s2pro_lora: the first run, S1-style samples; it speaks garbage)


def fish_key(hf_key: str) -> str:
    """S2 Pro's safetensors names -> fish-speech's module names (fish_speech.models.text2semantic.llama)."""
    if hf_key.startswith("text_model.model."):
        return hf_key[len("text_model.model."):]
    if hf_key.startswith("audio_decoder."):
        rest = hf_key[len("audio_decoder."):]
        return rest if rest.startswith("codebook_embeddings.") else "fast_" + rest
    return hf_key


@app.function(image=fish_image, volumes=VOLUMES, timeout=3600)
def download_weights():
    from huggingface_hub import snapshot_download

    if not Path(CKPT, "codec.pth").exists():
        snapshot_download("fishaudio/s2-pro", local_dir=CKPT)
        models.commit()
    return sorted(p.name for p in Path(CKPT).iterdir())


@app.function(image=fish_image, gpu="H100", volumes=VOLUMES, timeout=6 * 3600)
def train(speaker: str, max_steps: int = 1000, save_every: int = 250):
    require("tools/vqgan/extract_vq.py", "tools/llama/build_dataset.py", "fish_speech/train.py")
    data, protos = f"/vol/dataset/{speaker}", f"/vol/protos/{speaker}"

    # Lightning writes to results/<project>; point that at the volume so checkpoints persist.
    Path("/vol/results").mkdir(exist_ok=True)
    link = Path(REPO, "results")
    if not link.exists():
        link.symlink_to("/vol/results")

    stop = threading.Event()

    def commit_periodically():
        while not stop.wait(300):
            vol.commit()

    threading.Thread(target=commit_periodically, daemon=True).start()

    if not any(Path(protos).glob("*.proto*")):
        # 1. Audio -> codec tokens (.npy next to each wav).
        run(["python", "tools/vqgan/extract_vq.py", data, "--num-workers", "1", "--batch-size", "16",
             "--config-name", "modded_dac_vq", "--checkpoint-path", f"{CKPT}/codec.pth"])
        # 2. Pack text + tokens into protobuf shards.
        run(["python", "tools/llama/build_dataset.py", "--input", data, "--output", protos,
             "--text-extension", ".lab", "--num-workers", "8"])
    # 3. LoRA fine-tune, through fish_finetune (S2 Pro's embedding scale and chat prompt layout; see there).
    #    S2 Pro ships a Hugging Face tokenizer (tokenizer.json), not tokenizer.tiktoken.
    run(["python", "-m", "fish_finetune", "--config-name", "text2semantic_finetune",
         f"project={project(speaker)}", f"pretrained_ckpt_path={CKPT}", f"tokenizer.model_path={CKPT}",
         f"train_dataset.proto_files=[{protos}]", f"val_dataset.proto_files=[{protos}]",
         f"trainer.max_steps={max_steps}", f"trainer.val_check_interval={save_every}",
         f"+lora@model.model.lora_config={LORA}"])

    stop.set()
    vol.commit()
    return sorted(p.name for p in Path(f"/vol/results/{project(speaker)}/checkpoints").glob("*.ckpt"))


@app.function(image=fish_image, volumes=VOLUMES, memory=98304, cpu=8, timeout=3600)
def merge(speaker: str, step: int) -> str:
    """LoRA checkpoint -> full model in fish-speech's format (model.pth), for comparing and exporting."""
    require("tools/llama/merge_lora.py")
    out = f"/vol/merged/{project(speaker)}-step{step}"
    if not Path(out, "model.pth").exists():
        run(["python", "tools/llama/merge_lora.py", "--lora-config", LORA, "--base-weight", CKPT,
             "--lora-weight", f"/vol/results/{project(speaker)}/checkpoints/step_{step:09d}.ckpt",
             "--output", out])
        vol.commit()
    return out


class Scorer:
    """How close audio sounds to the character (ECAPA cosine to the centroid of 60 of their real clips) and how
    intelligible it is (Whisper large-v3 word error rate). Shared by every model in a comparison."""

    def __init__(self, speaker: str, device: str = "cuda"):
        import random
        import re

        import numpy as np
        import soundfile as sf
        import torch
        from speechbrain.inference.speaker import EncoderClassifier
        from transformers import pipeline

        self.device, self.np, self.torch, self.re = device, np, torch, re
        self.ecapa = EncoderClassifier.from_hparams("speechbrain/spkrec-ecapa-voxceleb", savedir="/tmp/ecapa",
                                                    run_opts={"device": device})
        clips = sorted(p for p in Path(f"/vol/dataset/{speaker}").glob("*.wav"))
        random.Random(0).shuffle(clips)
        self.held_out = clips[:60]
        centroid = np.mean([self.embed(*sf.read(p)) for p in clips[:60]], axis=0)
        self.centroid = centroid / np.linalg.norm(centroid)
        self.real_similarity = round(float(np.mean([self.embed(*sf.read(p)) @ self.centroid for p in clips[60:90]])), 4)
        self.asr = pipeline("automatic-speech-recognition", model="openai/whisper-large-v3",
                            torch_dtype=torch.float16, device=device)

    def words(self, text: str) -> str:
        return " ".join(self.re.sub(r"[^a-z' ]", " ", self.re.sub(r"\[[^\]]*\]", "", text.lower())).split())

    def embed(self, y, sr):
        import torchaudio.functional as AF

        if y.ndim > 1:
            y = y.mean(axis=1)
        x = AF.resample(self.torch.as_tensor(y, dtype=self.torch.float32), sr, 16000).unsqueeze(0).to(self.device)
        e = self.ecapa.encode_batch(x).squeeze().cpu().numpy()
        return e / self.np.linalg.norm(e)

    def score(self, audio, sr: int, text: str) -> dict:
        import jiwer
        import torchaudio.functional as AF

        mono = audio.mean(axis=1) if audio.ndim > 1 else audio
        a16 = AF.resample(self.torch.from_numpy(mono.astype("float32")), sr, 16000).numpy()[:16000 * 29]
        heard = self.asr({"raw": a16, "sampling_rate": 16000}, generate_kwargs={"language": "en"})["text"].strip()
        return {"heard": heard, "similarity": round(float(self.embed(mono, sr) @ self.centroid), 4),
                "wer": round(jiwer.wer(self.words(text), self.words(heard) or "-"), 3),
                "seconds": round(len(mono) / sr, 2), "real_similarity": self.real_similarity}


@app.function(image=fish_image, gpu="H100", volumes=VOLUMES, timeout=1800)
def score_wavs(speaker: str, items: list[dict]) -> list[dict]:
    """Score audio made elsewhere (e.g. ElevenLabs) exactly like evaluate() scores S2 Pro: items are
    {"model", "line", "text", "wav"} with wav bytes; returns them with the scores added."""
    import io

    import soundfile as sf

    scorer = Scorer(speaker)
    out = []
    for item in items:
        audio, sr = sf.read(io.BytesIO(item["wav"]))
        out.append({**item, **scorer.score(audio, sr, item["text"])})
        print(item["model"], item["line"], out[-1]["similarity"], out[-1]["wer"], out[-1]["heard"], flush=True)
    return out


@app.function(image=fish_image, gpu="H100", volumes=VOLUMES, timeout=3600)
def pick_study(speaker: str, ref: str, items: list[dict]) -> list[dict]:
    """How server/drama3.py's best_of should pick a take: each take scored by two speaker models (ECAPA, which the
    comparisons use, and WavLM, which best_of uses) against the reference clip it was cloned from (all a picker
    can know) and against the character's held-out real clips (what counts), plus word error and length."""
    import io

    import numpy as np
    import soundfile as sf
    import torch
    import torchaudio.functional as AF
    from transformers import AutoFeatureExtractor, WavLMForXVector

    scorer = Scorer(speaker)
    features = AutoFeatureExtractor.from_pretrained("microsoft/wavlm-base-plus-sv")
    sv = WavLMForXVector.from_pretrained("microsoft/wavlm-base-plus-sv").to("cuda").eval()

    def wavlm(y, sr):
        if y.ndim > 1:
            y = y.mean(axis=1)
        x = AF.resample(torch.as_tensor(y, dtype=torch.float32), sr, 16000).numpy()[:16000 * 30]
        with torch.no_grad():
            e = sv(**{k: v.to("cuda") for k, v in features(x, sampling_rate=16000, return_tensors="pt").items()}).embeddings[0]
        e = e.cpu().numpy()
        return e / np.linalg.norm(e)

    unit = lambda v: v / np.linalg.norm(v)
    wavlm_real = unit(np.mean([wavlm(*sf.read(p)) for p in scorer.held_out], axis=0))
    ecapa_ref, wavlm_ref = scorer.embed(*sf.read(ref)), wavlm(*sf.read(ref))
    out = []
    for item in items:
        audio, sr = sf.read(io.BytesIO(item.pop("wav")))
        e, w = scorer.embed(audio, sr), wavlm(audio, sr)
        out.append({**item, **scorer.score(audio, sr, item["text"]), "ecapa_ref": round(float(e @ ecapa_ref), 4),
                    "wavlm_real": round(float(w @ wavlm_real), 4), "wavlm_ref": round(float(w @ wavlm_ref), 4)})
    return out


@app.function(image=fish_image, gpu="H100", volumes=VOLUMES, timeout=3600)
def evaluate(speaker: str, model_dirs: list[str], seed: int = 7, ref: str = "") -> list[dict]:
    """The same test lines from each model, with the same reference clip; scored and returned as wavs."""
    import io

    import soundfile as sf
    import torch

    from fish_speech.models.text2semantic.inference import (decode_to_audio, encode_audio, generate_long,
                                                            init_model, load_codec_model)

    device = "cuda"
    codec = load_codec_model(f"{CKPT}/codec.pth", device)
    ref = Path(ref or f"/vol/refs/{speaker}/ref_0.wav")  # a clip + .lab transcript; default: server/finetune/prepare_dataset.py's reference
    ref_codes = encode_audio(ref, codec, device).cpu()
    ref_text = ref.with_suffix(".lab").read_text().strip()

    scorer = Scorer(speaker, device)

    results = []
    for model_dir in model_dirs:
        model, decode_one_token = init_model(Path(model_dir), device, torch.bfloat16)
        with torch.device(device):
            model.setup_caches(max_batch_size=1, max_seq_len=model.config.max_seq_len,
                               dtype=next(model.parameters()).dtype)
        for i, text in enumerate(TEST_LINES[speaker]):
            torch.manual_seed(seed + i)
            codes = []
            for r in generate_long(model=model, device=device, decode_one_token=decode_one_token, text=text,
                                   max_new_tokens=600, temperature=0.8, top_p=0.8,  # ~28 s: a runaway shows, then stops
                                   prompt_text=[ref_text], prompt_tokens=[ref_codes]):
                if r.action == "sample":
                    codes.append(r.codes)
                elif r.action == "next":
                    break
            audio = decode_to_audio(torch.cat(codes, dim=1).to(device), codec).float().cpu().numpy()
            wav = io.BytesIO()
            sf.write(wav, audio, codec.sample_rate, format="WAV")
            results.append({"model": Path(model_dir).name, "line": i, "text": text,
                            **scorer.score(audio, codec.sample_rate, text), "wav": wav.getvalue()})
            r = results[-1]
            print(r["model"], i, r["similarity"], r["wer"], r["heard"], flush=True)
        del model
        torch.cuda.empty_cache()
    return results


@app.function(image=fish_image, volumes=VOLUMES, memory=65536, cpu=8, timeout=3600)
def export_model(speaker: str, step: int, name: str = "") -> str:
    """A merged checkpoint back into S2 Pro's own layout (sharded safetensors + config, tokenizer, codec),
    which the voice server (sgl-omni) loads like the base model."""
    import shutil

    import torch
    from safetensors.torch import load_file, save_file

    merged = torch.load(f"/vol/merged/{project(speaker)}-step{step}/model.pth", map_location="cpu", mmap=True)
    out = Path(f"/models/{name or speaker + '-s2pro'}")
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir(parents=True)
    for f in Path(CKPT).iterdir():
        if f.is_file() and not f.name.endswith(".safetensors"):
            shutil.copy2(f, out / f.name)

    index = json.loads(Path(CKPT, "model.safetensors.index.json").read_text())
    shards: dict[str, list[str]] = {}
    for key, shard in index["weight_map"].items():
        shards.setdefault(shard, []).append(key)
    changed, missing = 0, []
    for shard, keys in shards.items():
        base = load_file(f"{CKPT}/{shard}")
        tensors = {}
        for key in keys:
            t = merged.get(fish_key(key))
            if t is None or t.shape != base[key].shape:
                missing.append(key)
                tensors[key] = base[key]
                continue
            t = t.to(base[key].dtype).contiguous()
            changed += int(not torch.equal(t, base[key]))
            tensors[key] = t
        save_file(tensors, str(out / shard), metadata={"format": "pt"})
    (out / "FINETUNE.json").write_text(json.dumps({"speaker": speaker, "step": step, "lora": LORA,
                                                   "changed_tensors": changed, "kept_from_base": missing}, indent=1))
    models.commit()
    return f"{out}: {changed} tensors changed, {len(missing)} kept from the base model"


@app.local_entrypoint()
def main(speaker: str = "lamar", max_steps: int = 1000, save_every: int = 250):
    print("weights:", download_weights.remote())
    print("checkpoints:", train.remote(speaker, max_steps, save_every))


def report(speaker: str, results: list[dict]) -> str:
    """Side by side: each test line from every model, with what Whisper heard and the scores."""
    import html

    names = list(dict.fromkeys(r["model"] for r in results))
    label = lambda n: ("Untrained (S2 Pro, zero-shot)" if n == "s2-pro" else "Fish Audio Drama 3 (zero-shot, the mod's reference)" if n == "drama3"
                       else "Fish Audio Drama 3 (zero-shot, all 4 of the voice's clips)" if n == "drama3multi"
                       else "Fish Audio Drama 3 (zero-shot, temperature 0.7, top_p 0.8)" if n == "drama3cool"
                       else f"Fish Audio Drama 3 (zero-shot, best of {n[10]} takes{' that say the line' if 'g' in n[11:] else ''})" if n.startswith("drama3best")
                       else "S2 Pro (zero-shot, the mod's reference)" if n == "s2-pro-modref"
                       else "ElevenLabs (instant clone, eleven_v3)"
                       if n == "elevenlabs" else f"Trained ({n.replace('_', ' ')})")
    avg = lambda n, k: sum(r[k] for r in results if r["model"] == n) / sum(1 for r in results if r["model"] == n)
    head = "".join(f"<th>{html.escape(label(n))}<br><small>voice match {avg(n, 'similarity'):.3f} · word error {avg(n, 'wer'):.0%}</small></th>" for n in names)
    rows = []
    for line in sorted({r["line"] for r in results}):
        text = next(r["text"] for r in results if r["line"] == line)
        cells = []
        for n in names:
            r = next(r for r in results if r["model"] == n and r["line"] == line)
            cells.append(f'<td><audio controls preload="none" src="{n}_{line}.wav"></audio>'
                         f'<div class="heard">heard: {html.escape(r["heard"] or "(nothing)")}</div>'
                         f'<div class="score">voice match {r["similarity"]:.3f} · word error {r["wer"]:.0%} · {r["seconds"]:.1f}s</div></td>')
        rows.append(f"<tr><td class='line'><b>{line + 1}.</b> {html.escape(text)}</td>{''.join(cells)}</tr>")
    real = results[0]["real_similarity"]
    return f"""<!doctype html><meta charset="utf-8"><title>{speaker} voice: trained vs untrained</title>
<style>body{{font:15px system-ui,sans-serif;margin:24px;background:#111;color:#eee}}table{{border-collapse:collapse;width:100%}}
td,th{{border:1px solid #333;padding:10px;vertical-align:top;text-align:left}}th{{background:#1d1d1d}}.line{{width:30%}}
audio{{width:100%}}.heard{{margin-top:6px;color:#bbb}}.score{{color:#8a8;font-size:13px;margin-top:4px}}small{{color:#9a9;font-weight:normal}}</style>
<h2>{speaker.title()}: trained vs untrained</h2>
<p>Same reference clip, same seed, same settings. Voice match = how close it sounds to {speaker.title()}'s real clips
(ECAPA similarity; real clips score {real:.3f}). Word error = how much of the line Whisper got wrong.</p>
<table><tr><th>Line</th>{head}</tr>{''.join(rows)}</table>"""


@app.local_entrypoint()
def compare(speaker: str = "lamar", steps: str = "250,500,1000"):
    dirs = [CKPT] + list(merge.map([speaker] * len(steps.split(",")), [int(s) for s in steps.split(",")]))
    results = evaluate.remote(speaker, dirs)
    out = Path("data/finetune") / speaker
    out.mkdir(parents=True, exist_ok=True)
    for r in results:
        Path(out, f"{r['model']}_{r['line']}.wav").write_bytes(r.pop("wav"))
    Path(out, "scores.json").write_text(json.dumps(results, indent=1))
    Path(out, "compare.html").write_text(report(speaker, results), encoding="utf-8")
    print(f"\nreal clips vs the character's centroid: {results[0]['real_similarity']}")
    for name in dict.fromkeys(r["model"] for r in results):
        rows = [r for r in results if r["model"] == name]
        sim = sum(r["similarity"] for r in rows) / len(rows)
        wer = sum(r["wer"] for r in rows) / len(rows)
        print(f"{name:24s} similarity {sim:.3f}   word error rate {wer:.3f}")


@app.local_entrypoint()
def score_extra(speaker: str = "lamar", model: str = "elevenlabs"):
    """Add <model>_<line>.wav files in data/finetune/<speaker>/ (made by another engine, e.g.
    tools/elevenlabs_clone.ts) to the comparison: scored like the rest, then compare.html is rebuilt. Several
    models at once: --model a,b,c."""
    out = Path("data/finetune") / speaker
    models = model.split(",")
    results = [r for r in json.loads(Path(out, "scores.json").read_text()) if r["model"] not in models]
    lines = {r["line"]: r["text"] for r in results}
    items = [{"model": m, "line": i, "text": t, "wav": Path(out, f"{m}_{i}.wav").read_bytes()}
             for m in models for i, t in sorted(lines.items()) if Path(out, f"{m}_{i}.wav").exists()]
    scored = score_wavs.remote(speaker, items)
    for r in scored:
        r.pop("wav")
    results += scored
    Path(out, "scores.json").write_text(json.dumps(results, indent=1))
    Path(out, "compare.html").write_text(report(speaker, results), encoding="utf-8")
    for name in dict.fromkeys(r["model"] for r in results):
        rows = [r for r in results if r["model"] == name]
        print(f"{name:28s} similarity {sum(r['similarity'] for r in rows) / len(rows):.3f}   "
              f"word error rate {sum(r['wer'] for r in rows) / len(rows):.3f}")


@app.local_entrypoint()
def pick_study_run(speaker: str = "lamar", models: str = "", ref: str = "/models/voices/LamarDavis/clone.wav"):
    """Every take of <models> (comma-separated; <model>_<line>.wav in data/finetune/<speaker>/) through
    pick_study, into data/finetune/<speaker>/pick_study.json."""
    out = Path("data/finetune") / speaker
    lines = dict(json.loads(Path(out, "lines.json").read_text()))
    items = [{"model": m, "line": int(i), "text": t, "wav": Path(out, f"{m}_{i}.wav").read_bytes()}
             for m in models.split(",") for i, t in lines.items() if Path(out, f"{m}_{i}.wav").exists()]
    results = pick_study.remote(speaker, ref, items)
    Path(out, "pick_study.json").write_text(json.dumps(results, indent=1))
    print(f"{len(results)} takes scored")


@app.local_entrypoint()
def compare_ref(speaker: str = "lamar", ref: str = "/models/voices/LamarDavis/clone.wav", name: str = "s2-pro-modref"):
    """Base S2 Pro with another reference clip (default: the mod's own reference for the character, which the
    Drama 3 server uses too), added to the comparison next to the other models."""
    out = Path("data/finetune") / speaker
    results = [r for r in json.loads(Path(out, "scores.json").read_text()) if r["model"] != name]
    for r in evaluate.remote(speaker, [CKPT], ref=ref):
        r["model"] = name
        Path(out, f"{name}_{r['line']}.wav").write_bytes(r.pop("wav"))
        results.append(r)
    Path(out, "scores.json").write_text(json.dumps(results, indent=1))
    Path(out, "compare.html").write_text(report(speaker, results), encoding="utf-8")
    for n in dict.fromkeys(r["model"] for r in results):
        rows = [r for r in results if r["model"] == n]
        print(f"{n:28s} similarity {sum(r['similarity'] for r in rows) / len(rows):.3f}   "
              f"word error rate {sum(r['wer'] for r in rows) / len(rows):.3f}")


@app.local_entrypoint()
def diagnose(speaker: str = "lamar", step: int = 250):
    """A checkpoint in S2 Pro's own layout (not where the server looks) through the same scoring:
    tells a bad fine-tune apart from a bad reload of fish-speech's merged format."""
    print(export_model.remote(speaker, step, f"{speaker}-s2pro-test"))
    results = evaluate.remote(speaker, [f"/models/{speaker}-s2pro-test"])
    for r in results:
        print(r["line"], r["similarity"], r["wer"], r["seconds"], r["heard"])


@app.local_entrypoint()
def export(speaker: str = "lamar", step: int = 500):
    print(export_model.remote(speaker, step))
