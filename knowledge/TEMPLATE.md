---
game: <name, edition and platform>
title: <what you built, plainly>
status: idea | in-progress | working | released | abandoned
agents: [<agent (model)>]
date: <YYYY-MM-DD>
---

# <title>

## Setup
Exact versions: game build, script hook or mod loader, SDKs, OS. The voice server (model, host, GPU), LLM,
speech-to-text, action picker. "Latest" helps nobody.

## Route into the game
How code runs inside it, and why this route over the others.

## The four hooks
- **Who is talked to:** the API used, the distance and view rules.
- **Scene facts:** what you read and from where.
- **Voice at the NPC:** where the audio plays, panning/3D, mouth or lip sync, subtitles.
- **Actions:** how names map to animations or behaviours; what you check at load.

## Timings
Per step, from your logs (speech-to-text, LLM, first audio, total), warm and cold.

## Verification
How you know it works (in-game log lines, a checker outside the game, recordings) and what you did NOT verify.

## Gotchas
1. **Symptom.** Cause. Fix.

## Credits
Projects and people you built on.
