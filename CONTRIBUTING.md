# Contributing

Contributions from humans and from AI agents are both welcome. The most useful ones:
- **A field note** on making NPCs talk in a game this repo hasn't covered (`knowledge/games/<game>/`): the route,
  the four hooks, what was measured, the gotchas.
- **An example** for another game or engine (`examples/<game>/`): your own code only.
- **Skill improvements:** a gotcha you hit, a better default with numbers behind it.
- **Tools** (`npctalk/`) with tests.

## If you are an AI agent
1. **Ask your human first.** A pull request is public and carries their GitHub identity. Show them what you
   want to publish.
2. **Check it's new:** read `knowledge/` for the game. Improve an existing note rather than writing a second.
3. **Write it from your journal** (`NPCLOG.md`): exact versions, the route and why, the four hooks as built,
   timings, how you verified it and what you didn't, numbered gotchas (symptom → cause → fix). Use
   `knowledge/TEMPLATE.md`.
4. **Run the checks** (below), then open the PR with `gh pr create` or on github.com.

## Hard rules (PRs that break these are closed)
- **No voices you don't have the rights to, and no audio at all:** no voice clips, reference clips, renders of
  cloned voices, game audio or recordings. `npctalk check` fails on audio files.
- **No game content:** no game files, extracted assets, transcripts of a game's dialogue (`.lab` files and line
  lists), or decompiled code dumps. Describe what you learned in your own words; name symbols and natives.
- **No cheating other players and no bypasses:** nothing for online games with anti-cheat, no anti-cheat, DRM
  or ownership-check bypasses.
- **No secrets:** API keys, tokens, `.env` files, and no real voice server URLs (`your-workspace` in examples).
- **Honest numbers:** a claim that a voice setting is better comes with how it was measured, every run, and
  whether the difference is significant (`skills/voice-likeness-eval`). Mark what you didn't verify.
- **Content:** example personas keep slurs off; no sexual content beyond what the source game shows.
- **Credit** the people and projects you built on.

## Checks
```bash
uv run --with pytest pytest -q tests     # tests, including skill copies and manifests
./bin/npctalk check .                    # nothing that mustn't ship
```
For the C# example: `dotnet build -c Release` in `examples/gta-v/mod` and `examples/gta-v/tools/npcvoice-check`.

## Code and skills
- **Tools (`npctalk/`):** one module per command group, `register(sub)`, a docstring that doubles as `--help`, a
  test in `tests/`.
- **Skills (`skills/`):** Agent Skills format (`SKILL.md` with `name` matching the folder and a `description` that
  says when to use it). Agent-neutral: "the agent", not a product name. Deep material goes in `references/`.
  Edit `skills/` only, then refresh the copies (no symlinks, so Windows clones work):
  ```bash
  rm -rf .agents/skills .claude/skills && cp -r skills .agents/skills && cp -r skills .claude/skills
  ```
- **Manifests:** `plugin.json`, `.claude-plugin/`, `.codex-plugin/`, `.cursor-plugin/` and
  `gemini-extension.json` share one name, version and description; a test checks it.

CI runs the tests, the CLI help screens and `npctalk check .` on every PR.
