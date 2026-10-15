"""Keeps the voice server (drama3.py or s2pro.py) up for a while without a game running, e.g. for a demo day:

    modal deploy server/keepwarm.py                                              # once
    modal run server/keepwarm.py --url https://<workspace>--npcvoice-drama.modal.run --hours 12

Pings the server's /health every minute from a small CPU container in Modal (so it doesn't depend on your PC
being on), then stops; the GPU scales to zero SCALEDOWN seconds after the last ping. The run belongs to the
deployed app, not to the client that started it: a call from `modal run --detach` is cancelled when its client
exits, and a spawned one ends with the ephemeral app. Stop it early by cancelling the call (its id is printed)
in the Modal dashboard.

A GPU kept up costs its hourly price for the whole time (an H100 for 12 hours is roughly $45-50 on Modal).
"""
import time

import modal

app = modal.App("npcvoice-keepwarm")


@app.function(image=modal.Image.debian_slim().pip_install("httpx"), timeout=24 * 3600, cpu=0.25)
def keep_warm(url: str, hours: float = 12, every: int = 60):
    import httpx

    health = url.rstrip("/") + "/health"
    end = time.time() + hours * 3600
    while time.time() < end:
        try:
            # a cold start answers once the model is up; past 150 s Modal redirects to where the answer will be
            r = httpx.get(health, timeout=900, follow_redirects=True)
            print(time.strftime("%H:%M:%S"), r.status_code, r.text[:80], flush=True)
        except httpx.HTTPError as e:
            print(time.strftime("%H:%M:%S"), "failed:", e, flush=True)
        time.sleep(every)
    print(f"done after {hours} h", flush=True)


@app.local_entrypoint()
def main(url: str, hours: float = 12):
    call = modal.Function.from_name(app.name, "keep_warm").spawn(url=url, hours=hours)
    print(f"keeping {url} up for {hours} h: {call.object_id}")
