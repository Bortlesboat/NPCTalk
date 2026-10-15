# Rules, and why

## Where it runs
- **Single-player, or servers the user runs.** A mod that injects code into an online game protected by
  anti-cheat gets accounts banned and spoils the game for others. For GTA V: story mode only, never launch GTA
  Online with ScriptHookV or these files installed.
- **No bypasses.** Nothing that gets around anti-cheat, DRM or ownership checks.

## Voices
- **Clone only voices the user has the rights to:** their own, an actor who agreed in writing, a voice from a
  licensed library, or a generated one. A game's characters are voiced by real people; their voices are theirs.
  An experiment in a private single-player build is not a release.
- **Never publish clones or their sources:** voice clips, reference clips, renders of cloned voices, game audio,
  or transcripts of a game's dialogue. `npctalk check` fails on audio files and `.lab` transcripts.
- **Demo videos** carry the cloned voices too. Publishing one is the human's decision, about their own content,
  with their eyes on it.
- **Say it's AI.** A post or release with cloned or generated voices says so.

## Content
- NPCs can swear if the game's characters do; that's tone, not a problem.
- Racial slurs are replaced by default, from everyone. A persona can opt in with `slurs: yes` only for a
  character who uses the word in the source material, the way they use it there; it's off in every example in
  this repo. Insults about someone's race (including "ape" or "monkey" jabs) and anti-gay slurs are blocked
  for everyone (`Dialogue.cs`).
- No sexual content involving nudity: undressing actions stop at underwear.

## Secrets and money
- Keys live in environment variables (`FAL_KEY`, `CEREBRAS_API_KEY`, `TYPESAFE_API_KEY`, `NPCVOICE_TOKEN`),
  never in files that get committed. `npctalk check` looks for the common formats.
- **A server URL can be a secret.** The voice server's `/health` needs no token, and a request wakes the GPU,
  so anyone with the URL can run up the user's bill. Use `your-workspace` in examples.
- A GPU left up costs its hourly price. Say what a keep-warm costs before starting one (an H100 for 12 hours
  is about $45-50 on Modal).

## The user's machine
- **Ask before** sending keys and clicks into a running game (focus changes pause games and can land
  keystrokes in the wrong window), installing files into a game folder, or publishing anything (repos, PRs,
  videos, posts).
- **Keep the last working build** before installing a new one; Windows Smart App Control can refuse a new
  unsigned DLL, and the game then runs with no mod at all.
- Kill processes by PID, never by name.
