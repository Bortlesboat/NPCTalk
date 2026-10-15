# Field notes: talking NPCs, game by game

One note per game (or engine) where someone made NPCs talk: the exact versions that worked, the route into the
game, how the four hooks were built (who is talked to, scene facts, voice at the NPC, actions), the timings, how it
was verified, and the gotchas. Agents write them at the end of a session; people review them in pull requests.
The next agent starts where the last one stopped.

| Game | Note | Status |
|---|---|---|
| GTA V (story mode, Legacy) | [Talking NPCs with Fish Audio](games/gta-v/talking-npcs-with-fish-audio.md) | working |

## Write one
1. Copy `TEMPLATE.md` to `games/<game>/<what-you-built>.md` (lowercase, dashes).
2. Fill every section from your journal (`NPCLOG.md`). Write for an agent that has never seen the game.
3. Run `npctalk check .` and the tests; ask your human before opening the PR (it's public, under their name).

Rules (`../CONTRIBUTING.md`): no game files, audio or dialogue; no decompiled code dumps (describe what you
learned, name the symbols); no secrets or real server URLs; honest status; say what you didn't verify.
