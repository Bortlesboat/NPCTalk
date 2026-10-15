"""Fish Audio Drama 3 on Modal, served through SGLang-Omni: the NpcVoice mod's voices, cloned zero-shot from the
same reference clips as the S2 Pro server (server/s2pro.py), behind the same API, so the mod switches engines by
pointing [Modal] Url at this app.

    modal run server/drama3.py::download_weights   # once: ~40 GB (fishaudio/drama-3, FP8 experts) into npcvoice-models
    modal deploy server/drama3.py                  # https://<workspace>--npcvoice-drama.modal.run

Endpoints (as server/s2pro.py):
    POST /speak    {"text", "voice"} or {"text", "reference_audio_url", "reference_text"} -> 16-bit mono PCM at
                   44.1 kHz, streamed while it is generated (X-Sample-Rate header). "voice" names a clip in the
                   volume's /voices folder without .wav, e.g. "LamarDavis/clone". Event tags such as [laughs] or
                   [sighs] go in the text where they should happen. "best_of": N renders N takes at once and
                   returns, of those that say the line, the one that sounds most like the voice's reference
                   clips, in one piece (for audio made ahead, like the director's clips; the X-Takes header
                   has each take's ECAPA / WavLM likeness, word error and length).
    GET  /health   no auth; 200 once the model server is up and every voice is prepared.

Drama 3 (fishaudio/drama-3): Fish Audio's multi-character model, a 35B mixture of experts with 3B active,
zero-shot voice cloning from a reference clip + transcript (each reference at most 60 s), ~90 ms to first audio
on an H200 (sglang-omni cookbook). Weights are 37 GB, so it gets the whole H100. Attention runs on triton only
(sglang-omni picks it for this model).

Auth: "Authorization: Bearer <NPCVOICE_TOKEN>" from the npcvoice-auth secret, like server/s2pro.py.

MIN_CONTAINERS = 0: the GPU stops SCALEDOWN seconds after the last request. While the game runs the mod pings it
every [Tts] KeepWarmSeconds, so it stays up exactly while someone plays.
"""
import hmac
import os
import subprocess
import tempfile
import threading
import time
from pathlib import Path

import modal

APP_NAME = "npcvoice-drama"
SGL = "/sglang-omni"
SGL_REF = "549dd1b9e84d834a4d49a7c91d496db454859e80"  # sglang-omni main with Drama 3 support (2026-10-15)
CKPT = "/models/drama-3"
PORT = 8000
SAMPLE_RATE = 44100   # Drama 3 codec output rate
FRAMES_PER_SECOND = SAMPLE_RATE / 2048  # codec frames (= generated tokens) per second of audio: 1024 tokens is 47.6 s
SCALEDOWN = 300
MIN_CONTAINERS = 0
CPU_CORES = 8.0
VOICES = Path("/models/voices")
# A voice speaks from every clip in its folder (e.g. LamarDavis/clone + ref_0..2, ~50 s), not just the one named:
# Drama 3 takes several references of one speaker ("[Reference 1] ... [Reference 2] ..."), all speaker 0.
MULTI_REFERENCE = False   # tested on Lamar: no better than one clip (0.641 vs 0.637 voice match)
# Tuned on Lamar's 10 test lines (server/finetune/train.py; voice match = ECAPA similarity to 60 of his real clips):
# - sampling: one take here averages 0.660 over 10 runs, vs 0.643 at Drama 3's defaults (1.0 / 0.9); temperature
#   0.5 or 0.3 is worse (0.609, 0.628) and rambles more
# - reference: the voice's ~30 s clone clip beats each of its single 6-10 s clips (0.680 vs 0.610-0.630)
SAMPLING = {"temperature": 0.7, "top_p": 0.8}
# best_of N: about 1 take in 10 rambles on to the token limit or trails off into noises, so a take only counts if
# the ASR hears the line in it and it's no longer than the words could take. Of those, the one most like the
# reference by two speaker models: ECAPA (what server/finetune/train.py scores with, so its picks are checked by WavLM, which
# doesn't know the scorer), each scaled across the takes. Simulated over 100 takes (server/finetune/train.py pick_study): 0.721
# at 8 takes, 0.704 at 4, vs 0.660 for one; WavLM alone picks 0.706 / 0.692, and the best of 8 by the real clips
# themselves is 0.732.
SPEAKER_MODEL = "microsoft/wavlm-base-plus-sv"
ECAPA_MODEL = "speechbrain/spkrec-ecapa-voxceleb"
ECAPA_DIR = "/ecapa"
ASR_MODEL = "openai/whisper-base.en"

# Same base as server/s2pro.py: sglang's GPU libraries look for a CUDA toolkit at import time.
image = (
    modal.Image.from_registry("nvidia/cuda:13.0.3-devel-ubuntu24.04", add_python="3.12")
    .env({"DEBIAN_FRONTEND": "noninteractive", "CUDA_HOME": "/usr/local/cuda", "HF_HUB_ENABLE_HF_TRANSFER": "1"})
    .apt_install("git", "curl", "ffmpeg", "libsndfile1", "build-essential", "clang")
    .pip_install("uv")
    .run_commands(
        f"git clone https://github.com/sgl-project/sglang-omni {SGL}",
        f"cd {SGL} && git checkout {SGL_REF} && uv pip install --system --prerelease=allow -e '.[fishaudio]'",
        "uv pip install --system huggingface_hub hf_transfer httpx fastapi soundfile",
        # descript-audiotools pins protobuf down to 3.19; restore a modern one last.
        "uv pip install --system 'protobuf>=6.31.1,<7'",  # sglang-omni's generated protos need >= 6.31
        f"python -c \"import scipy.signal; from transformers import AutoFeatureExtractor, WavLMForXVector as M; "
        f"M.from_pretrained('{SPEAKER_MODEL}'); AutoFeatureExtractor.from_pretrained('{SPEAKER_MODEL}'); "
        f"from transformers import pipeline; pipeline('automatic-speech-recognition', model='{ASR_MODEL}')\"",
        # speechbrain without its dependencies (they'd replace sglang's torch); ruamel.yaml 0.19 breaks hyperpyyaml
        "uv pip install --system --no-deps speechbrain==1.1.2 hyperpyyaml 'ruamel.yaml==0.18.6' ruamel.yaml.clib joblib sentencepiece",
        f"python -c \"from speechbrain.inference.speaker import EncoderClassifier as E; E.from_hparams('{ECAPA_MODEL}', savedir='{ECAPA_DIR}')\"",
    )
)

app = modal.App(APP_NAME)
models = modal.Volume.from_name("npcvoice-models", create_if_missing=True)


@app.function(image=image, volumes={"/models": models}, timeout=3600, cpu=8)
def download_weights():
    from huggingface_hub import snapshot_download

    snapshot_download("fishaudio/drama-3", local_dir=CKPT)
    models.commit()
    print(sorted(p.name for p in Path(CKPT).iterdir()))


@app.cls(
    image=image,
    gpu="H100",
    volumes={"/models": models},
    secrets=[modal.Secret.from_name("npcvoice-auth")],
    scaledown_window=SCALEDOWN,
    min_containers=MIN_CONTAINERS,
    cpu=CPU_CORES,
    memory=65536,
    timeout=600,
    startup_timeout=2400,
)
@modal.concurrent(max_inputs=8)
class DramaTTS:
    @modal.enter()
    def start(self):
        if not Path(CKPT, "config.json").exists():
            from huggingface_hub import snapshot_download

            snapshot_download("fishaudio/drama-3", local_dir=CKPT)
            models.commit()

        self.ref_dir = Path(tempfile.mkdtemp())
        self.refs = {}    # reference url -> local path
        self.voices = {}  # "Trevor/clone" -> (local path, transcript)
        started = time.time()
        self.server = subprocess.Popen(
            ["sgl-omni", "serve", "--model-path", CKPT,
             "--config", f"{SGL}/examples/configs/drama3_tts.yaml",
             "--port", str(PORT), "--allowed-local-media-path", str(self.ref_dir)],
            cwd=SGL,
            env={**os.environ, "SGLANG_ENABLE_JIT_DEEPGEMM": "0"},
        )
        self.wait_until_ready()
        print(f"drama-3 up in {time.time() - started:.0f}s", flush=True)
        self.warm_up()
        self.prepare_voices()
        self.load_speaker_model()

    def store(self, data: bytes) -> str:
        """Clips live under their content hash, so a clip sent by URL and the same clip from /voices share one
        path (and one encoding in the server's reference cache)."""
        import hashlib

        path = self.ref_dir / f"{hashlib.sha256(data).hexdigest()[:24]}.wav"
        if not path.exists():
            path.write_bytes(data)
        return str(path)

    def body(self, text: str, ref_audio: str, ref_text: str, **extra) -> dict:
        return {"input": text, "references": [{"audio_path": ref_audio, "text": ref_text}], **extra}

    def references(self, voice: str, multi: bool) -> list[dict]:
        """The named clip, or every clip in its folder (moods have their own folders)."""
        path, text = self.voices[voice]
        if not multi:
            return [{"audio_path": path, "text": text}]
        folder = voice.rsplit("/", 1)[0]
        return [{"audio_path": p, "text": t} for name, (p, t) in sorted(self.voices.items())
                if name.rsplit("/", 1)[0] == folder]

    def prepare_voices(self):
        """Say a word in every clip in /voices now, so no conversation waits for its reference to be encoded."""
        import httpx
        from concurrent.futures import ThreadPoolExecutor

        for wav in sorted(VOICES.rglob("*.wav")) if VOICES.exists() else []:
            lab = wav.with_suffix(".lab")
            if lab.exists():
                name = wav.relative_to(VOICES).with_suffix("").as_posix()
                self.voices[name] = (self.store(wav.read_bytes()), lab.read_text().strip())

        def encode(item):
            name, (path, text) = item
            start = time.time()
            try:
                httpx.post(f"http://127.0.0.1:{PORT}/v1/audio/speech", timeout=600,
                           json=self.body("Yeah.", path, text, response_format="wav", max_new_tokens=64)).raise_for_status()
                return f"{name} {time.time() - start:.1f}s"
            except httpx.HTTPError as e:
                return f"{name} FAILED {e}"

        start = time.time()
        with ThreadPoolExecutor(4) as pool:
            for line in pool.map(encode, self.voices.items()):
                print("prepared", line, flush=True)
        print(f"prepared {len(self.voices)} voices in {time.time() - start:.0f}s", flush=True)

    def load_speaker_model(self):
        """For best_of: the speaker models and the ASR (on the GPU: they need ~1.5 GB of the ~6 GB sgl-omni
        leaves free), and each voice folder's clips embedded once, as what its takes are compared with."""
        import numpy as np
        import soundfile as sf
        from speechbrain.inference.speaker import EncoderClassifier
        from transformers import AutoFeatureExtractor, WavLMForXVector, pipeline

        self.gpu = threading.Lock()  # one take scored at a time
        self.asr = pipeline("automatic-speech-recognition", model=ASR_MODEL, device="cuda:0")
        self.features = AutoFeatureExtractor.from_pretrained(SPEAKER_MODEL)
        self.wavlm = WavLMForXVector.from_pretrained(SPEAKER_MODEL).to("cuda:0").eval()
        self.ecapa = EncoderClassifier.from_hparams(ECAPA_MODEL, savedir=ECAPA_DIR, run_opts={"device": "cuda:0"})
        self.likeness = {}  # voice folder or reference path -> (ECAPA, WavLM) unit embeddings
        folders = {}
        for name, (path, _) in self.voices.items():
            folders.setdefault(name.rsplit("/", 1)[0], []).append(path)
        for folder, paths in folders.items():
            self.likeness[folder] = self.centroid([sf.read(p) for p in paths])
        self.word_error(np.zeros(16000, dtype="float32"), 16000, "warm up")  # first ASR call loads lazily
        print(f"speaker models ready, {len(self.likeness)} voice folders", flush=True)

    @staticmethod
    def to16k(audio, sr: int):
        import math

        from scipy.signal import resample_poly

        if audio.ndim > 1:
            audio = audio.mean(axis=1)
        g = math.gcd(16000, sr)
        return resample_poly(audio.astype("float32"), 16000 // g, sr // g)[:16000 * 30]

    @staticmethod
    def words(text: str) -> list[str]:
        import re

        return re.sub(r"[^a-z' ]", " ", re.sub(r"\[[^\]]*\]", " ", text.lower())).split()

    def word_error(self, audio, sr: int, text: str) -> float:
        """Word error rate of what the ASR hears against the line (edit distance over words)."""
        said = self.words(text)
        # A garbled take makes the ASR hallucinate on and on; it only needs to hear about as many words as the line
        heard = self.words(self.asr({"raw": self.to16k(audio, sr), "sampling_rate": 16000},
                                    generate_kwargs={"max_new_tokens": 2 * len(said) + 10})["text"])
        row = list(range(len(heard) + 1))
        for i, w in enumerate(said, 1):
            prev, row[0] = row[0], i
            for j, h in enumerate(heard, 1):
                prev, row[j] = row[j], min(row[j] + 1, row[j - 1] + 1, prev + (w != h))
        return row[-1] / max(1, len(said))

    def embed(self, audio, sr: int):
        """(ECAPA, WavLM) unit speaker embeddings."""
        import numpy as np
        import torch

        x = self.to16k(audio, sr)
        with torch.no_grad():
            e = self.ecapa.encode_batch(torch.from_numpy(x).unsqueeze(0).to("cuda:0")).squeeze().cpu().numpy()
            inputs = {k: v.to("cuda:0") for k, v in self.features(x, sampling_rate=16000, return_tensors="pt").items()}
            w = self.wavlm(**inputs).embeddings[0].cpu().numpy()
        return e / np.linalg.norm(e), w / np.linalg.norm(w)

    def centroid(self, clips):
        import numpy as np

        pairs = [self.embed(a, sr) for a, sr in clips]
        unit = lambda v: v / np.linalg.norm(v)
        return unit(np.mean([p[0] for p in pairs], axis=0)), unit(np.mean([p[1] for p in pairs], axis=0))

    def target(self, voice: str | None, refs: list[dict]):
        import soundfile as sf

        folder = voice.rsplit("/", 1)[0] if voice else None
        if folder in self.likeness:
            return self.likeness[folder]
        key = refs[0]["audio_path"]
        if key not in self.likeness:
            self.likeness[key] = self.centroid([sf.read(r["audio_path"]) for r in refs])
        return self.likeness[key]

    async def best_take(self, body: dict, n: int, target) -> tuple[bytes, list[str], int]:
        """n takes at once; of those that say the line (no longer than the words could take: a long take has
        rambled or trailed off into noises; and heard nearly as well as the clearest take), the one most like
        the reference by both speaker models, each scaled across the takes. If none passes, the clearest one."""
        import asyncio
        import io

        import httpx
        import numpy as np
        import soundfile as sf

        longest = 1.5 + 0.45 * len(self.words(body["input"]))  # ~2.2 words a second, plus room for a [laugh]
        # A take that rambles runs to the token limit; stop it just past the longest a real one can be (so it
        # still fails the length check) instead of waiting for 47 s of it.
        cap = int((longest + 1.0) * FRAMES_PER_SECOND) + 8
        async with httpx.AsyncClient(timeout=180) as client:
            async def one():
                r = await client.post(f"http://127.0.0.1:{PORT}/v1/audio/speech",
                                      json={**body, "stream": False, "response_format": "wav", "max_new_tokens": cap})
                r.raise_for_status()
                return sf.read(io.BytesIO(r.content))
            takes = await asyncio.gather(*(one() for _ in range(n)))

        def score(audio, sr):
            with self.gpu:
                e, w = self.embed(audio, sr)
                return float(e @ target[0]), float(w @ target[1]), self.word_error(audio, sr, body["input"])

        ecapa, wavlm, errors = zip(*[await asyncio.to_thread(score, a, sr) for a, sr in takes])
        seconds = [len(a) / sr for a, sr in takes]
        clearest = min(errors)
        says_it = [i for i in range(n) if seconds[i] <= longest and errors[i] <= min(0.5, clearest + 0.1)]
        if says_it:
            z = lambda v: (np.array([v[i] for i in says_it]) - np.mean([v[i] for i in says_it])) / (np.std([v[i] for i in says_it]) or 1)
            best = says_it[int(np.argmax(z(ecapa) + z(wavlm)))]
        else:
            best = min(range(n), key=lambda i: (errors[i], seconds[i]))
        audio = takes[best][0]
        audio = audio.mean(axis=1) if audio.ndim > 1 else audio
        notes = [f"{ecapa[i]:.3f}/{wavlm[i]:.3f}/{errors[i]:.2f}/{seconds[i]:.1f}s{'*' if i == best else ''}" for i in range(n)]
        return (np.clip(audio, -1, 1) * 32767).astype("<i2").tobytes(), notes, best

    def wait_until_ready(self, timeout=2000):
        import httpx

        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.server.poll() is not None:
                raise RuntimeError(f"sgl-omni exited with code {self.server.returncode}")
            for path in ("/health", "/v1/models"):
                try:
                    if httpx.get(f"http://127.0.0.1:{PORT}{path}", timeout=2).status_code == 200:
                        return
                except httpx.HTTPError:
                    pass
            time.sleep(3)
        raise TimeoutError("sgl-omni did not become ready")

    def warm_up(self):
        """The first generations pay for kernel compilation and graph capture; pay it here."""
        import httpx

        for _ in range(2):
            try:
                httpx.post(f"http://127.0.0.1:{PORT}/v1/audio/speech", timeout=600,
                           json={"input": "Warming up the voice engine.", "response_format": "wav", "max_new_tokens": 128})
            except httpx.HTTPError as e:
                print(f"warm-up request failed (first real request will be slow): {e}")

    @modal.exit()
    def stop(self):
        self.server.terminate()

    async def reference(self, url: str) -> str:
        import httpx

        if url not in self.refs:
            async with httpx.AsyncClient(timeout=60, follow_redirects=True) as client:
                resp = await client.get(url)
                resp.raise_for_status()
            self.refs[url] = self.store(resp.content)
        return self.refs[url]

    @modal.asgi_app(label=APP_NAME)
    def web(self):
        import httpx
        from fastapi import FastAPI, Header, HTTPException
        from fastapi.responses import Response, StreamingResponse
        from pydantic import BaseModel

        api = FastAPI()
        token = os.environ["NPCVOICE_TOKEN"]

        class Speak(BaseModel):
            text: str
            voice: str | None = None
            reference_audio_url: str | None = None
            reference_text: str | None = None
            multi: bool | None = None  # every clip in the voice's folder as references (default MULTI_REFERENCE)
            temperature: float | None = None
            top_p: float | None = None
            best_of: int = 1  # takes to pick from; more than 1 returns the whole line at once instead of streaming

        @api.get("/health")
        async def health():
            return {"ok": True, "model": "drama-3", "voices": len(self.voices)}

        @api.post("/speak")
        async def speak(req: Speak, authorization: str = Header("")):
            if not hmac.compare_digest(authorization.encode(), f"Bearer {token}".encode()):
                raise HTTPException(401, "bad token")
            if req.voice in self.voices:
                refs = self.references(req.voice, MULTI_REFERENCE if req.multi is None else req.multi)
            elif req.reference_audio_url and req.reference_text:
                refs = [{"audio_path": await self.reference(req.reference_audio_url), "text": req.reference_text}]
            else:
                raise HTTPException(400, f"unknown voice {req.voice!r} and no reference_audio_url")
            body = {"input": req.text, "references": refs, "max_new_tokens": 1024, "response_format": "pcm", "stream": True, **SAMPLING}
            body.update({k: v for k, v in (("temperature", req.temperature), ("top_p", req.top_p)) if v is not None})
            if req.best_of > 1:
                try:
                    pcm, notes, best = await self.best_take(body, min(req.best_of, 8), self.target(req.voice, refs))
                except httpx.HTTPError as e:
                    raise HTTPException(502, f"sgl-omni: {e}")
                takes = " ".join(notes)  # ECAPA/WavLM likeness, word error, length per take; * = kept
                print(f"best of {len(notes)} [{req.voice}] {takes} | {req.text[:60]}", flush=True)
                return Response(pcm, media_type="application/octet-stream",
                                headers={"X-Sample-Rate": str(SAMPLE_RATE), "X-Takes": takes, "Cache-Control": "no-store"})
            client = httpx.AsyncClient(timeout=120)
            upstream = await client.send(client.build_request("POST", f"http://127.0.0.1:{PORT}/v1/audio/speech", json=body), stream=True)
            if upstream.status_code != 200:
                detail = (await upstream.aread()).decode(errors="replace")[:500]
                await upstream.aclose()
                await client.aclose()
                raise HTTPException(502, f"sgl-omni {upstream.status_code}: {detail}")

            async def pcm():
                try:
                    async for chunk in upstream.aiter_bytes(4096):
                        yield chunk
                finally:
                    await upstream.aclose()
                    await client.aclose()

            return StreamingResponse(pcm(), media_type="application/octet-stream",
                                     headers={"X-Sample-Rate": str(SAMPLE_RATE), "Cache-Control": "no-store"})

        return api
