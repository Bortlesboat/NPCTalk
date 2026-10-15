# Fine-tuning S2 Pro on one character

The one thing that beat every zero-shot setting: a LoRA fine-tune of S2 Pro on one character scored 0.785
voice match against 0.707 for S2 Pro's best zero-shot clip (Lamar, 10 lines). It needs a few hundred clean clips
of the character and an H100 for an hour, and it is training on someone's voice, so the rights question is
sharper than for a reference clip.

| Model | Voice match | Word error |
|---|---|---|
| S2 Pro zero-shot, the mod's clone clip | 0.680 | 6.1% |
| S2 Pro zero-shot, its best single clip | 0.707 | 5.6% |
| **Fine-tune, step 250** | **0.785** | 7.5% |
| Fine-tune, step 500 | 0.731 | 6.3% |

Later checkpoints overfit, as Fish warns: pick the earliest one that sounds right, by measurement.

## Steps
```bash
modal run server/finetune/prepare_dataset.py --audio data/raw/<speaker>/<recording>.wav --speaker <speaker>
modal run server/finetune/train.py --speaker <speaker>                       # tokenise + LoRA (r8, α16, lr 1e-4)
modal run server/finetune/train.py::compare --speaker <speaker> --steps 250,500,1000
modal run server/finetune/train.py::export --speaker <speaker> --step 250   # -> /models/<speaker>-s2pro
```
Then add the voice folder to `FINETUNES` in `server/s2pro.py` (`{"LamarDavis": "/models/lamar-s2pro"}`): the server
starts a second SGLang-Omni engine for it on the same H100 and routes that character's voices to it.

`prepare_dataset.py`: Demucs vocal isolation → Whisper large-v3 word timings → 2-15 s clips at pauses → drop
clips whose ECAPA embedding doesn't match the main speaker → -23 LUFS → `.wav` + `.lab` pairs, plus 3 reference
clips. `train.py` holds the test lines (write your own for your character) and the scorer used for every
comparison in this repo.

## Two bugs you must fix first (fish-speech@bdd39477)
fish-speech's trainer trains S2 Pro the way it trained S1, and the fine-tune then speaks garbage. Both fixes
are in `server/finetune/fish_finetune.py`:
1. **Embeddings.** At inference S2 Pro scales the embeddings of audio positions by 1/sqrt(num_codebooks + 1)
   (`scale_codebook_embeddings`); the training forward doesn't. Patch `BaseTransformer.embed` to scale the
   same way (idempotently: Hydra imports the module twice).
2. **Prompt layout.** Training samples use S1's layout ("Speak out the provided text." `<|speaker:user|>`...)
   while S2 Pro is prompted as a chat: a system turn with a reference clip of the voice, a user turn with the
   text, an assistant turn with the codes. `S2ChatIterableDataset` builds that, with another clip of the same
   speaker as the reference.

Also: run `fish_speech/train.py` through `runpy` (Hydra's config search breaks when imported), and encode
conversations with `_build_content_sequence().encode` (`Conversation.encode` doesn't take `max_length`).
