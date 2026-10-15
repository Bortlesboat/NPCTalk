import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]


def run(*args):
    return subprocess.run([sys.executable, "-m", "npctalk", *args], cwd=ROOT, capture_output=True, text=True)


def test_top_level_help_lists_every_command():
    out = run("--help")
    assert out.returncode == 0
    for command in ("health", "say", "voice", "check"):
        assert command in out.stdout


@pytest.mark.parametrize("args", [["health", "--help"], ["say", "--help"], ["voice", "--help"],
                                  ["voice", "make", "--help"], ["voice", "upload", "--help"], ["check", "--help"]])
def test_help_screens(args):
    out = run(*args)
    assert out.returncode == 0, out.stderr
    assert "npctalk" in out.stdout


def test_no_command_prints_help():
    out = run()
    assert out.returncode == 0
    assert "Every group has --help" in out.stdout
