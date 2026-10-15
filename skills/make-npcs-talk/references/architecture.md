# Architecture: the GTA V example, file by file

The example (`examples/gta-v/mod`) is a ScriptHookVDotNet v3 script (C#, .NET Framework 4.8) for GTA V's story
mode, plus a voice server on Modal. Read it as a template: the game-free files carry over to any game; the
game files are what you rewrite.

## Game-free (build outside the game; `tools/npcvoice-check` compiles these)
| File | What it does |
|---|---|
| `Dialogue.cs` | The LLM side of a character: shared rules, the system prompt (rules + persona + a random sample of the character's own lines + scene), regenerating when the player gave an order, and cleaning the output (refusals, name prefixes, slur rules, 45-word cap) |
| `Llm.cs` | OpenAI-compatible chat client (Cerebras by default), reasoning off |
| `Tts.cs` | Voice engines behind one interface: `ModalTts` (your Fish Audio server, streamed), `FishTts` (fal realtime WebSocket), `MiniMaxTts`, `LuxTts`; mood folders from `[tags]` |
| `VoiceService.cs` | One line at a time: speak, cancel, fall back to a second engine while the GPU wakes up, keep-warm pings |
| `SpeechPlayer.cs` | Streamed 16-bit PCM in from the network thread; volume, pan and mouth level read by the game thread |
| `SpeechToText.cs`, `FalClient.cs`, `MsgPack.cs`, `Net.cs` | fal speech-to-text, the fal realtime WebSocket, HTTP with TLS 1.2+ (GTA5.exe would default to TLS 1.0) |
| `OpenMic.cs` | Always-on mic with voice activity detection against the room's noise floor |
| `VoiceLibrary.cs` | Voice folders (clone.wav, ref_0..2, moods), uploads for engines that take a URL, per-NPC stable picks from pools |
| `Jev.cs`, `ActionCatalog.cs` | Action choice and order detection with TypeSafe's Jev; per-action thresholds |
| `Log.cs` | One log line per step, with timings |

## Game (rewrite these for another game)
| File | What it does |
|---|---|
| `NpcVoiceScript.cs` | The script: settings, keys, every-frame tick (target scan every 150 ms, drawing, mic state), the exchange pipeline, ambient lines |
| `Characters.cs` | Personas by ped model hash, scene facts from game natives (`SceneOf`) |
| `NpcActions.cs` | Action names → animations, tasks and clothing swaps; checks anim dictionaries at load |
| `Visits.cs` | Hotkeys that take you to the main characters' homes with their families spawned |
| `DevMode.cs` | F9: a ring of characters to talk to, a menu, target marker, timings, F10 hides the panels |
| `Director.cs`, `DirectorClips.cs` | Scripted scenes and gameplay-style clips for videos |

## Server (`server/`)
| File | What it does |
|---|---|
| `drama3.py` | Fish Audio Drama 3 on an H100 via SGLang-Omni; `POST /speak` streams PCM; `best_of` picks the best of N takes |
| `s2pro.py` | Fish Audio S2 Pro, same API; optional fine-tuned voices on the same GPU |
| `keepwarm.py` | Keeps a server up for a set time without a game |
| `fal/fal_app.py` | S2 Pro on fal with a realtime WebSocket (alternative host) |
| `finetune/` | Dataset prep, LoRA fine-tune and the scorer used for every comparison |

## One exchange, step by step (what the log shows)
```
open mic 3.6s peak 0.141 -> him                       mic heard 3.6 s of speech, target found
talk open mic 3.6s stt 0.74s llm 0.44s | you: ... | him: [sarcastic] Yeah, real deep. ...
speak [Marine/clone] npc: [sarcastic] Yeah, real deep. What else you gotta tell me, genius?
action pick: none 54% (no), confidence 0.51, jev 0.09s
timing him: stt 0.74s llm 0.44s tts 0.37s total 1.57s
tts modal [Marine/clone] first audio ok, done 0.86s
```
The voice server is called with the voice's name (`Marine/clone`), which it prepared at startup, so no clip
travels with the request. A reference URL is the fallback and costs ~9 s the first time the server sees it.
