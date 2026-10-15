"""`npctalk say` and `health` against a stand-in server that speaks the same protocol as server/drama3.py."""
import json
import threading
import wave
from http.server import BaseHTTPRequestHandler, HTTPServer

import pytest

from npctalk import cli

RATE = 44100


class FakeVoiceServer(BaseHTTPRequestHandler):
    seen = []

    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path == "/health":
            body = b'{"ok": true, "model": "fake", "voices": 1}'
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.send_error(404)

    def do_POST(self):
        req = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        FakeVoiceServer.seen.append((self.headers.get("Authorization"), req))
        if self.headers.get("Authorization") != "Bearer secret":
            self.send_error(401)
            return
        pcm = b"\x01\x00" * RATE  # one second of near-silence, sent in chunks like a stream
        self.send_response(200)
        self.send_header("X-Sample-Rate", str(RATE))
        self.send_header("Content-Length", str(len(pcm)))
        self.end_headers()
        for i in range(0, len(pcm), 8192):
            self.wfile.write(pcm[i:i + 8192])


@pytest.fixture()
def server(monkeypatch):
    httpd = HTTPServer(("127.0.0.1", 0), FakeVoiceServer)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    monkeypatch.setenv("NPCVOICE_URL", f"http://127.0.0.1:{httpd.server_port}")
    monkeypatch.setenv("NPCVOICE_TOKEN", "secret")
    FakeVoiceServer.seen.clear()
    yield httpd
    httpd.shutdown()


def test_health(server, capsys):
    assert cli.main(["health"]) == 0
    assert '"ok": true' in capsys.readouterr().out


def test_say_writes_the_streamed_audio(server, tmp_path, capsys):
    out = tmp_path / "line.wav"
    assert cli.main(["say", "[angry] Get out of my face!", "--voice", "Trevor/clone", "--best-of", "4", "--out", str(out)]) == 0
    with wave.open(str(out)) as w:
        assert (w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()) == (1, 2, RATE, RATE)
    auth, req = FakeVoiceServer.seen[-1]
    assert req == {"text": "[angry] Get out of my face!", "voice": "Trevor/clone", "best_of": 4}
    assert "1.0s of audio" in capsys.readouterr().out


def test_say_reports_a_bad_token(server, monkeypatch, tmp_path, capsys):
    monkeypatch.setenv("NPCVOICE_TOKEN", "wrong")
    assert cli.main(["say", "hi", "--voice", "X/clone", "--out", str(tmp_path / "x.wav")]) == 1
    assert "401" in capsys.readouterr().out
