"""Fish Audio S2 Pro on Modal, served through SGLang-Omni, for the NpcVoice mod's realtime voices.

    modal run server/s2pro.py::download_weights   # once: ~10 GB of weights into the npcvoice-models volume
    modal deploy server/s2pro.py                  # https://<workspace>--npcvoice-fish.modal.run

    modal volume put npcvoice-models <dir> /voices   # the mod's reference clips (see README)

Endpoints:
    POST /speak    {"text", "voice"} or {"text", "reference_audio_url", "reference_text"} -> 16-bit
                   mono PCM at 44.1 kHz, streamed while it is generated (X-Sample-Rate header). The text
                   may start with a Fish delivery tag such as [angry]. "voice" names a clip in the
                   volume's /voices folder without .wav, e.g. "Trevor/clone" or "Trevor/angry/ref_0".
    GET  /health   no auth; 200 once the model server is up and every voice is prepared.

Fine-tuned voices (server/finetune/train.py export): FINETUNES maps a voice folder to a model in the volume, e.g.
"LamarDavis" -> /models/lamar-s2pro. Each one that exists gets its own sgl-omni engine on the same H100
(the engines split GPU memory), and that character's voices are spoken by it; everyone else by S2 Pro.

S2-Pro encodes a reference clip into codes on the CPU the first time it sees it (10-40 s). Every clip
in /voices is encoded while the container starts, before it takes traffic, so even a fresh container
answers in ~1 s; the container gets CPU_CORES so that goes quickly.

Auth: "Authorization: Bearer <NPCVOICE_TOKEN>" from the npcvoice-auth secret
(`modal secret create npcvoice-auth NPCVOICE_TOKEN=...`); the mod reads the same value from the
NPCVOICE_TOKEN environment variable.

Same serving stack as server/fal/fal_app.py: SGLang-Omni keeps S2 Pro loaded (CUDA graphs, paged KV cache,
continuous batching) on an H100; published H200 figure RTF 0.34 at batch size 1. MIN_CONTAINERS keeps
one H100 running at all times so the voices never cold-start (billed around the clock; set it to 0 to
let the GPU stop SCALEDOWN seconds after the last request).
"""
import hmac
import os
import subprocess
import tempfile
import time
from pathlib import Path

import modal

APP_NAME = "npcvoice-fish"
SGL = "/sglang-omni"
CKPT = "/models/s2-pro"
PORT = 8000
SAMPLE_RATE = 44100   # S2 Pro codec output rate
SCALEDOWN = 300
MIN_CONTAINERS = 1   # always-on H100; 0 = scale to zero when idle (2-3 min cold start)
CPU_CORES = 8.0      # reference encoding runs on the CPU
VOICES = Path("/models/voices")
FINETUNES = {"LamarDavis": "/models/lamar-s2pro"}  # voice folder -> fine-tuned model (server/finetune/train.py export)

# CUDA devel base: sglang's GPU libraries look for a CUDA toolkit at import time. 13.0.3 matches the
# cuda-toolkit pin of the torch sglang-omni installs (see server/fal/fal_app.py).
image = (
    modal.Image.from_registry("nvidia/cuda:13.0.3-devel-ubuntu24.04", add_python="3.12")
    .env({"DEBIAN_FRONTEND": "noninteractive", "CUDA_HOME": "/usr/local/cuda"})
    .apt_install("git", "curl", "ffmpeg", "libsndfile1", "build-essential", "clang")  # clang: cdifflib (nemo-text-processing)
    .pip_install("uv")
    .run_commands(
        f"git clone https://github.com/sgl-project/sglang-omni {SGL}",
        f"cd {SGL} && uv pip install --system --prerelease=allow -e .",
        # S2 Pro's bundled Fish code imports packages sglang-omni doesn't declare; audiotools pins
        # protobuf down to 3.19, so restore a modern protobuf last (same as server/fal/fal_app.py).
        "uv pip install --system descript-audio-codec descript-audiotools hydra-core omegaconf librosa "
        "soundfile huggingface_hub httpx fastapi",
        "uv pip install --system 'protobuf>=5,<6'",
    )
)

app = modal.App(APP_NAME)
models = modal.Volume.from_name("npcvoice-models", create_if_missing=True)


@app.function(image=modal.Image.debian_slim(python_version="3.12").pip_install("huggingface_hub"),
              volumes={"/models": models}, timeout=3600)
def download_weights():
    from huggingface_hub import snapshot_download

    snapshot_download("fishaudio/s2-pro", local_dir=CKPT)
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
    memory=32768,
    timeout=600,
    startup_timeout=2400,
)
@modal.concurrent(max_inputs=8)
class FishTTS:
    @modal.enter()
    def start(self):
        if not Path(CKPT, "codec.pth").exists():
            from huggingface_hub import snapshot_download

            snapshot_download("fishaudio/s2-pro", local_dir=CKPT)
            models.commit()

        self.ref_dir = Path(tempfile.mkdtemp())
        self.refs = {}    # reference url -> local path
        self.voices = {}  # "Trevor/clone" -> (local path, transcript)
        # One engine per model: S2 Pro on PORT, each fine-tune that's in the volume on the next port.
        tuned = {voice: path for voice, path in FINETUNES.items() if Path(path, "FINETUNE.json").exists()}
        self.ports = {voice: PORT + 1 + i for i, voice in enumerate(tuned)}
        share = 0.85 if not tuned else round(0.84 / (1 + len(tuned)), 2)
        self.servers = []
        for model, port in [(CKPT, PORT)] + [(tuned[v], self.ports[v]) for v in tuned]:
            self.servers.append(subprocess.Popen(
                ["sgl-omni", "serve", "--model-path", model,
                 "--config", f"{SGL}/examples/configs/s2pro_tts.yaml",
                 "--port", str(port), "--allowed-local-media-path", str(self.ref_dir),
                 "--mem-fraction-static", str(share)],
                cwd=SGL,
                # DeepGEMM JIT-compiles FP8 kernels; S2 Pro is bf16 and doesn't use it.
                env={**os.environ, "SGLANG_ENABLE_JIT_DEEPGEMM": "0"},
            ))
            self.wait_until_ready(port)  # one at a time: each sizes its memory pool from what's free
            self.warm_up(port)
        print(f"engines: S2 Pro on {PORT}" + "".join(f", {v} ({tuned[v]}) on {p}" for v, p in self.ports.items()), flush=True)
        self.prepare_voices()

    def store(self, data: bytes) -> str:
        """Clips live under their content hash, so a clip sent by URL and the same clip from /voices
        share one path and one encoding."""
        import hashlib

        path = self.ref_dir / f"{hashlib.sha256(data).hexdigest()[:24]}.wav"
        if not path.exists():
            path.write_bytes(data)
        return str(path)

    def port_for(self, voice: str | None) -> int:
        """The engine for this voice: its character's fine-tune if there is one, else S2 Pro."""
        return self.ports.get((voice or "").split("/")[0], PORT)

    def prepare_voices(self):
        """Encode every clip in /voices now so no conversation pays for it."""
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
                httpx.post(f"http://127.0.0.1:{self.port_for(name)}/v1/audio/speech", timeout=600, json={
                    "input": "Yeah.", "ref_audio": path, "ref_text": text,
                    "response_format": "wav", "max_new_tokens": 64,
                }).raise_for_status()
                return f"{name} {time.time() - start:.1f}s"
            except httpx.HTTPError as e:
                return f"{name} FAILED {e}"

        start = time.time()
        with ThreadPoolExecutor(4) as pool:
            for line in pool.map(encode, self.voices.items()):
                print("prepared", line, flush=True)
        print(f"prepared {len(self.voices)} voices in {time.time() - start:.0f}s", flush=True)

    def wait_until_ready(self, port=PORT, timeout=1500):
        import httpx

        deadline = time.time() + timeout
        server = self.servers[-1]
        while time.time() < deadline:
            if server.poll() is not None:
                raise RuntimeError(f"sgl-omni on {port} exited with code {server.returncode}")
            for path in ("/health", "/v1/models"):
                try:
                    if httpx.get(f"http://127.0.0.1:{port}{path}", timeout=2).status_code == 200:
                        return
                except httpx.HTTPError:
                    pass
            time.sleep(3)
        raise TimeoutError("sgl-omni did not become ready")

    def warm_up(self, port=PORT):
        """The first generation pays ~20 s of kernel compilation and CUDA graph capture; pay it here."""
        import httpx

        try:
            httpx.post(f"http://127.0.0.1:{port}/v1/audio/speech", timeout=300, json={
                "input": "Warming up.", "voice": "default", "response_format": "wav", "max_new_tokens": 128,
            })
        except httpx.HTTPError as e:
            print(f"warm-up request failed (first real request will be slow): {e}")

    @modal.exit()
    def stop(self):
        for server in self.servers:
            server.terminate()

    async def reference(self, url: str) -> str:
        """Clips sent by URL are downloaded once per URL (the mod re-uploads them every session)."""
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
        from fastapi.responses import StreamingResponse
        from pydantic import BaseModel

        api = FastAPI()
        token = os.environ["NPCVOICE_TOKEN"]

        class Speak(BaseModel):
            text: str
            voice: str | None = None
            reference_audio_url: str | None = None
            reference_text: str | None = None

        @api.get("/health")
        async def health():
            return {"ok": True, "voices": len(self.voices), "finetuned": sorted(self.ports)}

        @api.post("/speak")
        async def speak(req: Speak, authorization: str = Header("")):
            if not hmac.compare_digest(authorization.encode(), f"Bearer {token}".encode()):
                raise HTTPException(401, "bad token")
            if req.voice in self.voices:
                ref_audio, ref_text = self.voices[req.voice]
            elif req.reference_audio_url and req.reference_text:
                ref_audio, ref_text = await self.reference(req.reference_audio_url), req.reference_text
            else:
                raise HTTPException(400, f"unknown voice {req.voice!r} and no reference_audio_url")
            body = {
                "input": req.text,
                "ref_audio": ref_audio,
                "ref_text": ref_text,
                "max_new_tokens": 1024,
                "response_format": "pcm",
                "stream": True,
            }
            client = httpx.AsyncClient(timeout=120)
            upstream = await client.send(client.build_request("POST", f"http://127.0.0.1:{self.port_for(req.voice)}/v1/audio/speech", json=body), stream=True)
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
