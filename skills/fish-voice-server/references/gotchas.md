# Gotchas (symptom → cause → fix)

1. **sgl-omni won't import: protobuf errors.** descript-audiotools pins protobuf down to 3.19; SGLang-Omni's
   generated code needs ≥ 6.31. → Install `protobuf>=6.31.1,<7` last in the image.
2. **Attention backend errors on Drama 3.** It runs on triton only; SGLang-Omni picks it from
   `examples/configs/drama3_tts.yaml`. → Use that config; pin the SGLang-Omni commit you tested (`SGL_REF`).
3. **Import-time CUDA errors.** SGLang's GPU libraries look for a CUDA toolkit when imported. → Build on
   `nvidia/cuda:13.0.3-devel-ubuntu24.04` and set `CUDA_HOME`.
4. **`pip install speechbrain` breaks the server.** Its dependencies replace the image's torch. → Install with
   `--no-deps` plus `hyperpyyaml`, `ruamel.yaml==0.18.6` (0.19 breaks hyperpyyaml: `'Loader' object has no
   attribute 'max_depth'`), `ruamel.yaml.clib`, `joblib`, `sentencepiece`.
5. **Out of GPU memory for scoring models.** Drama 3 + its KV cache take ~75 of the H100's 81.5 GB; ~6.5 GB is
   left. ECAPA (117 MiB), WavLM-base-plus-sv and Whisper base together need ~1.5 GB: fine on the GPU, and ~10x
   faster than on the CPU.
6. **The first line in a voice takes ~9 s.** The server encodes a reference clip the first time it sees it. →
   Put voices in the volume; the server encodes them all at startup (`prepare_voices`). Send clips by URL only
   as a fallback.
7. **A new voice isn't found by name.** Voices are read at startup. → Restart the server after uploading
   (`modal app stop npcvoice-drama`; the next request starts a fresh container).
8. **Some takes run 47.6 s.** The take hit `max_new_tokens = 1024` (21.5 tokens per second of audio). → For
   streaming, cap the line length you send (the GTA mod caps replies at 45 words); for pre-rendered lines use
   `best_of`, which caps each take near the longest the words could take.
9. **best_of is slow on a crying or screaming line (76 s).** Whisper hallucinates hundreds of words on garbage
   audio. → Cap the ASR's `max_new_tokens` at about twice the line's word count.
10. **After `modal deploy`, requests still hit the old code.** A warm old container keeps serving. → Stop it
    (`modal container list`, `modal container stop --yes <id>`), and expect "Server has lost track of input" for
    a request it was holding.
11. **A request through a cold start returns 303 after 150 s.** Modal redirects long requests to a result URL.
    → Follow redirects (`httpx ... follow_redirects=True`; urllib does it for you).
12. **A keep-warm job dies when your laptop's client exits.** A `.remote()` call from `modal run --detach` is
    cancelled with its client, and a `.spawn()`ed one ends with the ephemeral app. → Deploy the keep-warm app
    and spawn on the deployed function (`server/keepwarm.py` does).
13. **`modal` won't start on Windows ("blocked by Application Control").** Smart App Control blocks the
    unsigned `modal.exe` launcher. → Run `python -m modal` with the same Python.
14. **Measured "improvements" vanish.** Ten lines × two runs swing by ±0.04 voice match between identical
    runs. → See the `voice-likeness-eval` skill: compare per line, run more, report every run.
