"""Fine-tuning S2 Pro with fish-speech's trainer, with two fixes for S2 Pro (fish-speech@FISH_REF trains it
the way it trained S1, and the result speaks garbage):

1. Embeddings: at inference S2 Pro scales the embeddings of audio positions by 1/sqrt(num_codebooks + 1)
   (scale_codebook_embeddings, BaseTransformer.forward_generate); the training forward (embed) doesn't.
2. Prompt: training samples use S1's layout ("Speak out the provided text." <|speaker:user|> ...), while
   S2 Pro is prompted as a chat (fish_speech.models.text2semantic.inference.generate_long): a system turn
   holding a reference clip of the voice, a user turn with the text, an assistant voice turn with the codes.
   S2ChatIterableDataset builds exactly that, with another clip of the same speaker as the reference.

Run as `python -m fish_finetune <fish_speech/train.py hydra overrides>` from the fish-speech checkout.
"""
import math
import random
import sys

import torch

from fish_speech.conversation import Conversation, Message
from fish_speech.content_sequence import TextPart, VQPart
from fish_speech.datasets import semantic as fsd
from fish_speech.models.text2semantic.llama import BaseTransformer

_embed = getattr(BaseTransformer.embed, "unscaled", BaseTransformer.embed)  # hydra imports this module again


def _scaled_embed(self, inp):
    x = _embed(self, inp)
    if getattr(self.config, "scale_codebook_embeddings", False):
        audio = (inp[:, 0] >= self.config.semantic_begin_id) & (inp[:, 0] <= self.config.semantic_end_id)
        x = torch.where(audio.unsqueeze(-1), x / math.sqrt(self.config.num_codebooks + 1), x)
    return x


_scaled_embed.unscaled = _embed
BaseTransformer.embed = _scaled_embed

REFERENCE_PROB = 0.85  # sometimes no reference, like generate_long without a prompt


def _codes(sentence) -> torch.Tensor:
    return torch.tensor([s.values for s in sentence.semantics], dtype=torch.int32)


class S2ChatIterableDataset(fsd.AutoTextSemanticInstructionIterableDataset):
    """One conversation per sample, laid out like S2 Pro inference: reference in the system turn, then 1-3
    user/assistant turns from the same speaker. Loss only on the assistant's codes and its end token."""

    def augment(self):
        if self.groups is None:
            self.init_mock_data_server()
        group = random.choices(self.groups, weights=self.group_weights, k=1)[0]
        sentences = list(group.sentences)
        if not sentences:
            return None
        turns = random.sample(sentences, k=min(len(sentences), random.randint(1, 3)))
        others = [s for s in sentences if all(s is not t for t in turns)]

        if others and random.random() < REFERENCE_PROB:
            ref = random.choice(others)
            system = [
                TextPart(text="convert the provided text to speech reference to the following:\n\nText:\n", cal_loss=False),
                TextPart(text=f"<|speaker:0|>{fsd.clean_text(random.choice(ref.texts))}", cal_loss=False),
                TextPart(text="\n\nSpeech:\n", cal_loss=False),
                VQPart(codes=_codes(ref), cal_loss=False),
            ]
        else:
            system = [TextPart(text="convert the provided text to speech", cal_loss=False)]

        messages = [Message(role="system", parts=system, cal_loss=False, add_im_start=True, add_im_end=True)]
        for t in turns:
            messages.append(Message(role="user", parts=[TextPart(text=fsd.clean_text(random.choice(t.texts)), cal_loss=False)],
                                    cal_loss=False, add_im_start=True, add_im_end=True))
            messages.append(Message(role="assistant", parts=[VQPart(codes=_codes(t), cal_loss=True)],
                                    cal_loss=True, modality="voice", add_im_start=True, add_im_end=True))

        # Conversation.encode passes max_length, which ContentSequence.encode doesn't take at this commit.
        encoded = Conversation(messages=messages)._build_content_sequence().encode(self.tokenizer)
        num_codebooks = len(turns[0].semantics) if self.num_codebooks is None else self.num_codebooks

        tokens = torch.zeros((num_codebooks + 1, len(encoded.tokens)), dtype=torch.int)
        tokens[0] = encoded.tokens
        vq = torch.cat([p.to(tokens.device) for p in encoded.vq_parts], dim=1)
        tokens[1:, encoded.vq_mask_tokens] = vq

        labels = torch.full((num_codebooks + 1, len(encoded.labels)), -100, dtype=torch.int)
        labels[0, :] = encoded.labels
        labels[1:, encoded.vq_mask_labels] = vq  # the reference's codes too, but its text labels are -100, so no loss
        labels[1:, -1:] = fsd.CODEBOOK_PAD_TOKEN_ID
        return {"tokens": tokens.long(), "labels": labels.long()}


if __name__ == "__main__":
    import runpy

    # As a script, like `python fish_speech/train.py`: hydra finds its configs/ next to the file. The patch
    # above stays in effect (same process); hydra imports this module again by name for the dataset class.
    target = "fish_finetune.S2ChatIterableDataset"
    sys.argv = ["fish_speech/train.py", *sys.argv[1:], f"train_dataset._target_={target}", f"val_dataset._target_={target}"]
    runpy.run_path("fish_speech/train.py", run_name="__main__")
