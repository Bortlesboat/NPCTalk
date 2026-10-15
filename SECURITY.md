# Security

## Reporting a vulnerability
Please report security problems privately, not in a public issue or pull request: use **Report a vulnerability**
on the repository's **Security** tab. If that button isn't there, open an issue that asks for a private contact
and leave the details out. Fixes land on `main`.

## What's in scope
- **The voice servers** (`server/drama3.py`, `server/s2pro.py`, `server/fal/`). On Modal, `POST /speak` requires
  the bearer token from the `npcvoice-auth` secret and compares it in constant time; `GET /health` is open by
  design (and wakes the GPU, which is why a deployment's URL should stay private). The fal app is behind fal's own
  key authentication. A way to make the server speak, read
  files, or fetch URLs without the token is in scope, and so is a way to make it fetch something it shouldn't
  through `reference_audio_url` with the token.
- **Secrets.** The CLI and the GTA V example read keys from environment variables (`FAL_KEY`,
  `CEREBRAS_API_KEY`, `TYPESAFE_API_KEY`, `NPCVOICE_TOKEN`). A way for them to leak one, or a common key format
  `npctalk check` misses, is in scope.
- **The GTA V example's local input tools** (`examples/gta-v/tools/*.ps1`) send keys to the game window. Input that
  reaches a different window is in scope.

Out of scope: bugs in the services and tools this calls (Modal, fal, Cerebras, TypeSafe, SGLang-Omni, Fish Audio's
models, ScriptHookV, ScriptHookVDotNet), which please report upstream, and requests to get past a game's
anti-cheat, DRM or ownership checks, which this project doesn't do.
