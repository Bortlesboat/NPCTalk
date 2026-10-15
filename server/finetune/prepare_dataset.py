"""Stage 1: turn a raw recording of one character into a Fish Speech training set.

raw audio -> Demucs vocals -> Whisper word timings -> 2-15 s clips
-> drop clips that don't sound like the main speaker -> loudness-normalised .wav + .lab

    modal run server/finetune/prepare_dataset.py --audio data/raw/<speaker>/<recording>.wav --speaker <speaker>
"""
import json
from pathlib import Path

import modal

app = modal.App("npcvoice-prepare")
vol = modal.Volume.from_name("npcvoice-train", create_if_missing=True)
VOL = Path("/vol")

image = (
    modal.Image.debian_slim(python_version="3.11")
    .apt_install("ffmpeg")
    .pip_install(
        "torch==2.5.1",
        "torchaudio==2.5.1",
        "transformers==4.46.3",
        "accelerate",
        "demucs==4.0.1",
        "speechbrain==1.0.3",
        "pyloudnorm==0.1.1",
        "soundfile",
        "numpy<2",
    )
)

SR = 44100
MIN_S, MAX_S = 2.0, 15.0   # target clip length
HARD_MIN_S = 1.0           # keep short barks ("Damn!") only if nothing to merge them with
SPLIT_GAP = 0.6            # split at pauses this long once the clip is >= MIN_S
FORCE_GAP = 1.5            # always split at pauses this long
PAD = 0.1                  # silence kept on each side of a clip
TARGET_LUFS = -23.0


def chunk_words(words):
    """Group (text, start, end) words into clips that end at natural pauses."""
    chunks, cur = [], []
    for w in words:
        if cur:
            gap = w[1] - cur[-1][2]
            dur = cur[-1][2] - cur[0][1]
            too_long = w[2] - cur[0][1] > MAX_S
            if too_long or gap > FORCE_GAP or (gap > SPLIT_GAP and dur >= MIN_S):
                chunks.append(cur)
                cur = []
        cur.append(w)
    if cur:
        chunks.append(cur)
    return [c for c in chunks if HARD_MIN_S <= c[-1][2] - c[0][1] <= MAX_S]


@app.function(image=image, gpu="L40S", volumes={"/vol": vol}, timeout=3 * 3600, memory=32768)
def prepare(raw_name: str, speaker: str, sim_threshold: float = 0.55):
    import shutil
    import subprocess

    import numpy as np
    import pyloudnorm as pyln
    import soundfile as sf
    import torch
    import torchaudio.functional as AF
    from speechbrain.inference.speaker import EncoderClassifier
    from transformers import pipeline

    work = Path("/tmp/work")
    work.mkdir(exist_ok=True)
    wav = work / "input.wav"
    subprocess.run(["ffmpeg", "-y", "-i", str(VOL / "raw" / raw_name), "-ac", "1", "-ar", str(SR), str(wav)], check=True)

    # 1. Strip music and sound effects, keep the voice.
    subprocess.run(["python", "-m", "demucs", "--two-stems=vocals", "-n", "htdemucs", "-o", str(work), str(wav)], check=True)
    audio, sr = sf.read(work / "htdemucs" / "input" / "vocals.wav")
    if audio.ndim > 1:
        audio = audio.mean(axis=1)
    audio_t = torch.from_numpy(audio).float()
    a16 = AF.resample(audio_t, sr, 16000).numpy()

    # 2. Transcribe with word timestamps.
    asr = pipeline("automatic-speech-recognition", model="openai/whisper-large-v3",
                   torch_dtype=torch.float16, device="cuda")
    out = asr({"raw": a16, "sampling_rate": 16000}, return_timestamps="word",
              chunk_length_s=30, batch_size=4, generate_kwargs={"language": "en"})
    words = [(c["text"], c["timestamp"][0], c["timestamp"][1])
             for c in out["chunks"] if c["timestamp"][0] is not None and c["timestamp"][1] is not None]
    chunks = chunk_words(words)

    # 3. Cut clips.
    clips = []
    for c in chunks:
        start = max(0.0, c[0][1] - PAD)
        end = min(len(audio) / sr, c[-1][2] + PAD)
        text = "".join(w[0] for w in c).strip()
        if text:
            clips.append({"start": start, "end": end, "text": text,
                          "audio": audio[int(start * sr):int(end * sr)]})

    # 4. Keep only the dominant speaker: compare every clip to the centroid of the
    #    most self-similar half, so other characters and crowd lines drop out.
    enc = EncoderClassifier.from_hparams("speechbrain/spkrec-ecapa-voxceleb",
                                         savedir="/tmp/ecapa", run_opts={"device": "cuda"})
    embs = []
    for clip in clips:
        x = AF.resample(torch.from_numpy(clip["audio"]).float(), sr, 16000).unsqueeze(0).cuda()
        embs.append(enc.encode_batch(x).squeeze().cpu().numpy())
    E = np.stack(embs)
    E /= np.linalg.norm(E, axis=1, keepdims=True)
    centroid = np.median(E, axis=0)
    sims = E @ (centroid / np.linalg.norm(centroid))
    centroid = E[sims >= np.median(sims)].mean(axis=0)
    sims = E @ (centroid / np.linalg.norm(centroid))

    # 5. Loudness-normalise and write wav + .lab pairs (the layout Fish Speech expects).
    out_dir = VOL / "dataset" / speaker
    ref_dir = VOL / "refs" / speaker
    for d in (out_dir, ref_dir):
        shutil.rmtree(d, ignore_errors=True)
        d.mkdir(parents=True)
    meter = pyln.Meter(sr)
    manifest, kept = [], []
    for i, (clip, sim) in enumerate(zip(clips, sims)):
        dur = clip["end"] - clip["start"]
        row = {"id": f"{i:04d}", "start": round(clip["start"], 2), "dur": round(dur, 2),
               "text": clip["text"], "speaker_sim": round(float(sim), 3), "kept": bool(sim >= sim_threshold)}
        manifest.append(row)
        if not row["kept"]:
            continue
        y = pyln.normalize.loudness(clip["audio"], meter.integrated_loudness(clip["audio"]), TARGET_LUFS)
        peak = np.abs(y).max()
        if peak > 0.99:
            y = y * (0.99 / peak)
        sf.write(out_dir / f"{row['id']}.wav", y, sr)
        (out_dir / f"{row['id']}.lab").write_text(row["text"])
        kept.append((row, y))

    # Reference clips for inference: the most "on-voice" clips of 6-12 s.
    refs = sorted((k for k in kept if 6 <= k[0]["dur"] <= 12), key=lambda k: -k[0]["speaker_sim"])[:3]
    for n, (row, y) in enumerate(refs):
        sf.write(ref_dir / f"ref_{n}.wav", y, sr)
        (ref_dir / f"ref_{n}.lab").write_text(row["text"])

    with open(VOL / "dataset" / f"{speaker}_manifest.jsonl", "w") as f:
        for row in manifest:
            f.write(json.dumps(row) + "\n")
    vol.commit()

    kept_min = sum(r["dur"] for r, _ in kept) / 60
    total_min = sum(r["dur"] for r in manifest) / 60
    return (f"{len(kept)}/{len(manifest)} clips kept, {kept_min:.1f} of {total_min:.1f} min "
            f"(speaker threshold {sim_threshold}), {len(refs)} reference clips")


@app.local_entrypoint()
def main(audio: str, speaker: str = "franklin", sim_threshold: float = 0.55):
    p = Path(audio)
    with vol.batch_upload(force=True) as batch:
        batch.put_file(str(p), f"/raw/{p.name}")
    print(prepare.remote(p.name, speaker, sim_threshold))
    print(f"Inspect: modal volume get npcvoice-train dataset/{speaker}_manifest.jsonl .")
