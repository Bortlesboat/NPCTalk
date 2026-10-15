"""Fish Audio S2 Pro voice cloning on fal, served through SGLang-Omni (optimised path).

    fal run server/fal/fal_app.py::FishTTS        # temporary URL for testing
    fal deploy server/fal/fal_app.py::FishTTS     # persistent endpoint

Endpoints:
    POST /           one line -> WAV file (batch generation)
    WS   /realtime   one line per message -> streamed PCM chunks (live use, e.g. realtime_client.py)

Why this is fast: SGLang-Omni keeps S2 Pro loaded and serves it like an LLM
(CUDA graphs, paged KV cache, continuous batching), instead of a fresh process per line.
Published H200 figure at batch size 1: RTF 0.34, i.e. ~3x faster than real time.

GPU: H200 first, H100 as fallback. Token-by-token decoding is memory-bandwidth bound,
and the H200 has ~40% more bandwidth (4.8 vs 3.4 TB/s) plus more room for batched KV cache.

Needs `fal secrets set HF_TOKEN=...`. Local Python must be 3.12 to match the image.
"""
import os
import subprocess
import tempfile
import time
from pathlib import Path
from typing import AsyncIterator

import fal
from fal.container import ContainerImage
from fal.toolkit import Audio, download_file
from pydantic import BaseModel, Field

SGL = "/sglang-omni"
CKPT = "/data/checkpoints/s2-pro"
PORT = 8000
SAMPLE_RATE = 44100   # S2 Pro codec output rate

# CUDA devel base: sglang's GPU libraries (DeepGEMM, DeepEP, ...) look for a CUDA toolkit
# at import time. 13.0.3 matches the cuda-toolkit pin of the torch 2.13 sglang-omni installs.
DOCKERFILE = f"""
FROM nvidia/cuda:13.0.3-devel-ubuntu24.04
ENV DEBIAN_FRONTEND=noninteractive CUDA_HOME=/usr/local/cuda
RUN apt-get update && apt-get install -y python3 python3-dev python3-pip python-is-python3 \\
        curl git ffmpeg libsndfile1 build-essential \\
    && rm -rf /var/lib/apt/lists/*
RUN pip install --break-system-packages uv
RUN git clone https://github.com/sgl-project/sglang-omni {SGL} \\
    && cd {SGL} && uv pip install --system --break-system-packages --prerelease=allow -e .
# S2 Pro's bundled Fish code imports packages sglang-omni doesn't declare (dac, audiotools,
# hydra, omegaconf, librosa). audiotools pins protobuf down to 3.19, so restore a modern
# protobuf last (same issue as on Modal).
RUN uv pip install --system --break-system-packages descript-audio-codec descript-audiotools \\
        hydra-core omegaconf librosa soundfile huggingface_hub httpx \\
    && uv pip install --system --break-system-packages 'protobuf>=5,<6'
"""


class TTSInput(BaseModel):
    text: str = Field(description="Line to speak. May start with a delivery tag such as [angry].")
    reference_audio_url: str = Field(description="6-12 s clean clip of the target voice.")
    reference_text: str = Field(description="Transcript of the reference clip.")


class TTSOutput(BaseModel):
    audio: Audio
    generation_seconds: float


class AudioChunk(BaseModel):
    audio: bytes = Field(default=b"", description="Raw 16-bit little-endian mono PCM.")
    sample_rate: int = SAMPLE_RATE
    done: bool = False
    first_chunk_seconds: float | None = None
    total_seconds: float | None = None


class FishTTS(fal.App):
    image = ContainerImage.from_dockerfile_str(DOCKERFILE)
    machine_type = ["GPU-H200", "GPU-H100"]
    keep_alive = 300          # stay warm 5 min after the last request
    min_concurrency = 0       # set to 1 for the live in-game version (always warm, always billed)
    max_concurrency = 2
    max_multiplexing = 8      # SGLang batches concurrent requests on one GPU
    startup_timeout = 1800    # first boot downloads ~10 GB of weights into /data

    def setup(self):
        from huggingface_hub import snapshot_download

        if not Path(CKPT, "codec.pth").exists():
            snapshot_download("fishaudio/s2-pro", local_dir=CKPT, token=os.environ.get("HF_TOKEN"))

        self.ref_dir = Path(tempfile.mkdtemp())
        self.server = subprocess.Popen(
            ["sgl-omni", "serve", "--model-path", CKPT,
             "--config", f"{SGL}/examples/configs/s2pro_tts.yaml",
             "--port", str(PORT), "--allowed-local-media-path", str(self.ref_dir)],
            cwd=SGL,
            # DeepGEMM JIT-compiles FP8 kernels; S2 Pro is bf16 and doesn't use it.
            env={**os.environ, "SGLANG_ENABLE_JIT_DEEPGEMM": "0"},
        )
        self.wait_until_ready()
        self.refs = {}   # reference url -> local path
        self.warm_up()

    def warm_up(self):
        """The first generation pays ~20 s of kernel compilation and CUDA graph capture;
        pay it during setup instead of on the first real request."""
        import httpx

        try:
            httpx.post(f"http://127.0.0.1:{PORT}/v1/audio/speech", timeout=300, json={
                "input": "Warming up.", "voice": "default", "response_format": "wav",
                "max_new_tokens": 128,
            })
        except httpx.HTTPError as e:
            print(f"warm-up request failed (first real request will be slow): {e}")

    def wait_until_ready(self, timeout=1500):
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

    def teardown(self):
        self.server.terminate()

    def speech_request(self, req: TTSInput, **extra) -> dict:
        if req.reference_audio_url not in self.refs:
            self.refs[req.reference_audio_url] = download_file(req.reference_audio_url, target_dir=self.ref_dir)
        return {
            "input": req.text,
            "ref_audio": str(self.refs[req.reference_audio_url]),
            "ref_text": req.reference_text,
            "max_new_tokens": 1024,
            **extra,
        }

    @fal.endpoint("/")
    async def tts(self, req: TTSInput) -> TTSOutput:
        import httpx

        start = time.time()
        async with httpx.AsyncClient(timeout=120) as client:
            resp = await client.post(f"http://127.0.0.1:{PORT}/v1/audio/speech",
                                     json=self.speech_request(req, response_format="wav"))
        resp.raise_for_status()
        return TTSOutput(
            audio=Audio.from_bytes(resp.content, content_type="audio/wav", file_name="line.wav"),
            generation_seconds=round(time.time() - start, 2),
        )

    # Bidirectional: one WebSocket session carries many lines (server-streaming mode closes
    # after a single input). buffering=64 keeps PCM chunks from overflowing fal's outgoing
    # queue, which would otherwise send them out of order.
    @fal.realtime("/realtime", buffering=64)
    async def realtime(self, requests: AsyncIterator[TTSInput]) -> AsyncIterator[AudioChunk]:
        async for req in requests:
            async for chunk in self.stream_line(req):
                yield chunk

    async def stream_line(self, req: TTSInput) -> AsyncIterator[AudioChunk]:
        """Stream PCM as SGLang-Omni produces it, so playback can start before the line is done."""
        import httpx

        start = time.time()
        first = None
        async with httpx.AsyncClient(timeout=120) as client:
            async with client.stream("POST", f"http://127.0.0.1:{PORT}/v1/audio/speech",
                                     json=self.speech_request(req, response_format="pcm", stream=True)) as resp:
                resp.raise_for_status()
                async for chunk in resp.aiter_bytes(8192):
                    if first is None:
                        first = round(time.time() - start, 3)
                    yield AudioChunk(audio=chunk, first_chunk_seconds=first)
        yield AudioChunk(done=True, first_chunk_seconds=first, total_seconds=round(time.time() - start, 3))
