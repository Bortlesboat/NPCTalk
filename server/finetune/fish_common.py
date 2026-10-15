"""Shared Modal pieces for the Fish Audio S2 Pro fine-tune (prepare_dataset.py, train.py).

Everything here is named npcvoice-* (apps, volumes, secrets), so it stays apart from the rest of your Modal workspace.
  npcvoice-train   datasets, reference clips, LoRA runs, merged checkpoints
  npcvoice-models  S2 Pro weights (also used by the voice server, server/s2pro.py) and exported fine-tunes
"""
import subprocess
from pathlib import Path

import modal

vol = modal.Volume.from_name("npcvoice-train", create_if_missing=True)
models = modal.Volume.from_name("npcvoice-models", create_if_missing=True)
VOLUMES = {"/vol": vol, "/models": models}

REPO = "/root/fish-speech"
# fish-speech main as of the first S2 Pro fine-tune; bump deliberately.
FISH_REF = "bdd39477922a89b904c6f2c7397f2a9bbcc60a0c"
CKPT = "/models/s2-pro"

fish_image = (
    # Fish's cu128 extra pulls PyTorch wheels that bundle CUDA, so a slim base is enough.
    modal.Image.debian_slim(python_version="3.11")
    .apt_install("git", "ffmpeg", "portaudio19-dev", "libsox-dev", "build-essential", "clang")
    .run_commands(
        "pip install --upgrade pip",
        f"git clone https://github.com/fishaudio/fish-speech {REPO}",
        f"cd {REPO} && git checkout {FISH_REF} && pip install -e '.[cu128]'",
        # descript-audiotools drags protobuf down to 3.19; Modal needs >=5, Fish allows <6.
        "pip install 'protobuf>=5,<6'",
    )
    .pip_install("huggingface_hub[cli]", "speechbrain==1.0.3", "jiwer", "transformers")
    .workdir(REPO)
    .add_local_python_source("fish_common", "fish_finetune")
)


def run(cmd, cwd=REPO):
    print("+", " ".join(cmd), flush=True)
    subprocess.run(cmd, check=True, cwd=cwd)


def require(*rel_paths):
    """Fish Speech moves scripts between releases; fail early with a clear message."""
    missing = [p for p in rel_paths if not Path(REPO, p).exists()]
    if missing:
        raise FileNotFoundError(f"Not found in fish-speech@{FISH_REF}: {missing}. "
                                "Check the repo's current docs and update the paths.")
