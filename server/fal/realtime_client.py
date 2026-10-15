"""Talk to the FishTTS /realtime WebSocket: send lines, receive streamed PCM, save WAVs, report latency.

    python realtime_client.py <app-id-or-url> "[angry] Get out the car, man!" ["another line" ...]
"""
import sys
import wave
from pathlib import Path

import fal_client

app_id, lines = sys.argv[1], sys.argv[2:]
ref = Path("data/refs/franklin/ref_0.wav")
ref_url = fal_client.upload_file(str(ref))
ref_text = ref.with_suffix(".lab").read_text()

out_dir = Path("outputs/realtime")
out_dir.mkdir(parents=True, exist_ok=True)

# One WebSocket session for every line, like the game mod would keep open.
with fal_client.realtime(app_id) as conn:
    for i, text in enumerate(lines):
        conn.send({"text": text, "reference_audio_url": ref_url, "reference_text": ref_text})
        pcm, rate = bytearray(), 44100
        while True:
            msg = conn.recv()
            if msg is None:
                continue
            pcm += msg.get("audio", b"")
            rate = msg.get("sample_rate", rate)
            if msg.get("done"):
                break
        path = out_dir / f"{i:02d}_SYNTHETIC.wav"
        with wave.open(str(path), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(rate)
            w.writeframes(bytes(pcm))
        audio_s = len(pcm) / 2 / rate
        print(f"{path}: first audio {msg['first_chunk_seconds']}s, total {msg['total_seconds']}s "
              f"for {audio_s:.1f}s of speech (RTF {msg['total_seconds'] / max(audio_s, 1e-6):.2f})  <- {text}")
