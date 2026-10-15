"""Talk to your Fish Audio voice server (server/drama3.py or server/s2pro.py), the same way a game does.

  npctalk health
  npctalk say "Ayy, what's up, homie?" --voice LamarDavis/clone --out hi.wav
  npctalk say "[angry] Get out of my face!" --voice Trevor/clone --best-of 8 --out trevor.wav
  npctalk say "Hello there." --reference-url https://.../me.wav --reference-text "What I say in me.wav."

The server's URL comes from --url or NPCVOICE_URL (https://<workspace>--npcvoice-drama.modal.run, as
`modal deploy` printed it), the token from NPCVOICE_TOKEN (the npcvoice-auth Modal secret). `say` prints
how long the first audio took and how long the line is, and writes a 16-bit mono WAV.

A cold server (scaled to zero) takes a minute or two to answer; `health` waits for it, so it doubles as a
wake-up call before you play.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request
import wave


def register(sub):
    p = sub.add_parser("health", help="is the voice server up? (wakes a cold one)", description=__doc__,
                       formatter_class=_raw())
    p.add_argument("--url", help="server URL (default: NPCVOICE_URL)")
    p.set_defaults(run=run_health)

    p = sub.add_parser("say", help="speak a line in a voice and save it as WAV", description=__doc__,
                       formatter_class=_raw())
    p.add_argument("text", help='the line; [tags] like "[angry]" or "[laughing]" steer the delivery')
    p.add_argument("--voice", help='a voice prepared on the server, e.g. "LamarDavis/clone"')
    p.add_argument("--reference-url", help="or clone from this clip (URL) ...")
    p.add_argument("--reference-text", help="... and its transcript")
    p.add_argument("--best-of", type=int, default=1,
                   help="render this many takes and keep the most alike one that says the line (drama3.py, 1-8)")
    p.add_argument("--out", default="npctalk.wav", help="where to write the WAV (default: npctalk.wav)")
    p.add_argument("--url", help="server URL (default: NPCVOICE_URL)")
    p.set_defaults(run=run_say)


def _raw():
    import argparse
    return argparse.RawDescriptionHelpFormatter


def server_url(url=None):
    url = url or os.environ.get("NPCVOICE_URL", "")
    if not url:
        sys.exit("no server URL: pass --url or set NPCVOICE_URL (https://<workspace>--npcvoice-drama.modal.run)")
    return url if url.startswith("http") else "https://" + url


def run_health(args):
    url = server_url(args.url).rstrip("/") + "/health"
    start = time.time()
    try:
        with urllib.request.urlopen(url, timeout=900) as r:  # a cold start answers once the model is up
            body = r.read().decode(errors="replace")
    except urllib.error.URLError as e:
        print(f"{url}: {e}")
        return 1
    print(f"{body}  ({time.time() - start:.1f}s)")
    return 0


def speak(url, body, token, timeout=600):
    """POST /speak and collect the streamed PCM: (pcm bytes, sample rate, seconds to first audio, headers)."""
    req = urllib.request.Request(url.rstrip("/") + "/speak", data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"})
    start = time.time()
    first = None
    chunks = []
    with urllib.request.urlopen(req, timeout=timeout) as r:
        rate = int(r.headers.get("X-Sample-Rate") or 44100)
        headers = dict(r.headers)
        while True:
            chunk = r.read(4096)
            if not chunk:
                break
            if first is None:
                first = time.time() - start
            chunks.append(chunk)
    return b"".join(chunks), rate, first, headers


def write_wav(path, pcm, rate):
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm[: len(pcm) // 2 * 2])


def run_say(args):
    token = os.environ.get("NPCVOICE_TOKEN", "")
    if not token:
        sys.exit("set NPCVOICE_TOKEN (the value in your npcvoice-auth Modal secret)")
    if not args.voice and not (args.reference_url and args.reference_text):
        sys.exit("pass --voice, or --reference-url with --reference-text")
    body = {"text": args.text}
    if args.voice:
        body["voice"] = args.voice
    if args.reference_url:
        body["reference_audio_url"], body["reference_text"] = args.reference_url, args.reference_text
    if args.best_of > 1:
        body["best_of"] = args.best_of
    try:
        pcm, rate, first, headers = speak(server_url(args.url), body, token)
    except urllib.error.HTTPError as e:
        print(f"server answered {e.code}: {e.read().decode(errors='replace')[:300]}")
        return 1
    write_wav(args.out, pcm, rate)
    seconds = len(pcm) / 2 / rate
    print(f"{args.out}: {seconds:.1f}s of audio, first audio after {first or 0:.2f}s")
    takes = headers.get("X-Takes") or headers.get("x-takes")
    if takes:
        print(f"takes (ECAPA/WavLM likeness, word error, length; * = kept): {takes}")
    return 0
