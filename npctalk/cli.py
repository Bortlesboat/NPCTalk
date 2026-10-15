"""npctalk: tools for making NPCs talk with Fish Audio.

  npctalk health                                   is the voice server up?
  npctalk say "Ayy, what's up?" --voice LamarDavis/clone --out hi.wav
  npctalk voice make --name Narrator --audio lines.wav
  npctalk voice upload voices/Narrator
  npctalk check .                                  nothing that mustn't ship (audio, transcripts, keys)

Every group has --help with examples.
"""
import argparse
import sys

from . import __version__, check, server, voice


def main(argv=None):
    parser = argparse.ArgumentParser(prog="npctalk", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=f"npctalk {__version__}")
    sub = parser.add_subparsers(dest="command", metavar="<command>")
    for module in (server, voice, check):
        module.register(sub)
    args = parser.parse_args(argv)
    if not getattr(args, "run", None):
        parser.print_help()
        return 0
    return args.run(args) or 0


if __name__ == "__main__":
    sys.exit(main())
