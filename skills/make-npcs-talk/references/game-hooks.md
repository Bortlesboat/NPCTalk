# The four hooks, per engine

What each engine gives you for: (1) who is being talked to, (2) scene facts, (3) playing a voice at an NPC,
(4) actions. **Only GTA V is built and verified in this repo.** The rest are starting points: check the
engine's current modding docs and mark your field note honestly.

## GTA V (story mode), ScriptHookV + ScriptHookVDotNet v3 (verified)
- **Run code:** C# script DLL in `<GTA V>/scripts/`, .NET Framework 4.8, compiled against the ScriptHookVDotNet3
  NuGet package (runtime from the installed SHVDN; we used a 3.7 nightly). Reload with the SHVDN reload key
  (set `ReloadKeyBinding=Insert` in `ScriptHookVDotNet.ini`).
- **Who:** `World.GetNearbyPeds(player, 12f)`, filter `IsHuman && !IsDead`, score by distance and
  `Vector3.Dot(direction, GameplayCamera.Direction)`. Named characters by `Model.Hash`.
- **Scene:** `World.GetZoneLocalizedName`, street name natives, `World.CurrentTimeOfDay`, `World.Weather`,
  the player's vehicle, weapon, wanted level, health.
- **Voice:** the game won't play your audio; play it on the PC (NAudio), pan by `Screen.WorldToScreen`, fade by
  distance, open the mouth with `PLAY_FACIAL_ANIM` (`mp_facial` / `mic_chatter`) while the audio level is high
  and back to `facials@gen_male@base` when quiet, and show the line with `Screen.ShowSubtitle`.
- **Actions:** `REQUEST_ANIM_DICT` + `TaskPlayAnim`, tasks (`FleeFrom`, `Combat`), component variations for
  clothing. Check every animation exists at load.
- **Gotchas:** ScriptHookVDotNet's ini reader cuts values at `//` (write URLs without `https://`); GTA5.exe makes
  .NET default to TLS 1.0 (enable TLS 1.2 at startup); the game pauses when it loses focus (scripts stop
  ticking too); `SendInput` scan codes reach its menus, `PostMessage` doesn't; Windows Smart App Control can
  block a new unsigned DLL build (test-load it with `[Reflection.Assembly]::LoadFile` before installing, and
  keep the last build that loaded). Never launch GTA Online with ScriptHookV installed.

## Unity games (Mono or IL2CPP), via BepInEx or MelonLoader (not verified here)
- **Who:** find NPC components in the scene (`FindObjectsOfType<...>` once, then cache), distance and
  `Vector3.Dot(camera.forward, dir)`.
- **Scene:** scene name, the game's own time/weather managers (read them from a decompile of
  `Assembly-CSharp`, never ship the decompile).
- **Voice:** an `AudioSource` on the NPC with `spatialBlend = 1` gives you 3D sound for free; stream PCM into an
  `AudioClip.Create(..., stream: true)` with a PCM reader callback. Subtitles through the game's UI or IMGUI.
- **Actions:** the NPC's `Animator` triggers, or the game's AI state machine.
- **The game-free C# files** (`Dialogue.cs`, `Llm.cs`, `Tts.cs`, ...) port with small changes: replace NAudio
  playback with the AudioSource; keep networking off the main thread.

## Unreal games, via UE4SS (Lua or C++ mods) (not verified here)
- **Who / scene:** UE4SS's object lookups (`FindAllOf`) for pawns of the NPC class; world settings for time.
- **Voice:** simplest is playing on the PC (like GTA V) and panning yourself; in-engine audio needs a C++ mod
  that feeds a procedural sound wave.
- **Actions:** call the NPC's existing Blueprint functions or montages.
- **Network calls:** do them in a C++ mod or a sidecar process; Lua mods can talk to a local sidecar over a
  socket.

## Bethesda games (Skyrim SE/AE, Fallout 4), via SKSE/F4SE plugins (not verified here)
- **Who:** the crosshair reference or the actors in the player's cell, by distance.
- **Voice:** the game's lip sync wants `.lip` files; playing on the PC with your own mouth animation (or a lip
  sync framework mod) is the realtime route. Subtitles through the game's subtitle menu.
- **Actions:** idles and packages via Papyrus or the plugin API.

## Minecraft (Java), via Fabric or NeoForge (not verified here)
- **Who:** entities in an AABB around the player, crosshair target.
- **Voice:** OpenAL through the game's sound engine or a positional sound you stream; or play on the PC.
- **Actions:** entity goals, animations of your own entities.

## Your own game
Expose the four hooks as an API on your NPC class and call the voice server directly; see
`examples/gta-v/mod/Tts.cs` (`ModalTts`) for the client and `npctalk/server.py` for the protocol in Python.

## A sidecar process, for any engine
If the game's scripting can't do HTTP or audio, run the game-free half as a separate process (the
`npcvoice-check` console app is most of one) and talk to it over a local socket: the game sends
`{who, scene, said}` and receives `{line, action}` while the sidecar plays the voice. Bind to `127.0.0.1`
only.
