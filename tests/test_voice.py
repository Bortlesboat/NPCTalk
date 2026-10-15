"""The line splitter and candidate picker behind `npctalk voice make`, on synthetic speech-like audio."""
import numpy as np

from npctalk.voice import SR, Candidate, candidates, find_lines, judge


def bursts(lengths, gap=0.6, level=0.1):
    """Tone bursts (stand-ins for spoken lines) separated by silence."""
    parts = [np.zeros(int(SR * gap), dtype=np.int16)]
    for seconds in lengths:
        t = np.arange(int(SR * seconds)) / SR
        parts.append((np.sin(2 * np.pi * 180 * t) * level * 32767).astype(np.int16))
        parts.append(np.zeros(int(SR * gap), dtype=np.int16))
    return np.concatenate(parts)


def test_lines_are_split_at_pauses():
    lines = find_lines(bursts([2.0, 1.5, 3.0]))
    assert len(lines) == 3
    assert [round(l.end - l.start, 1) for l in lines] == [2.0, 1.5, 3.0]


def test_short_pauses_dont_split():
    lines = find_lines(bursts([1.0, 1.0], gap=0.15))
    assert len(lines) == 1


def test_candidates_are_6_to_11_seconds_and_dont_overlap():
    lines = find_lines(bursts([2.5] * 20))
    cands = candidates(lines)
    assert cands
    for c in cands:
        assert 6 <= c.dur <= 11
    spans = sorted((c.lines[0].start, c.lines[-1].end) for c in cands)
    for (s1, e1), (s2, e2) in zip(spans, spans[1:]):
        assert s2 >= e1


def test_judge_drops_screams():
    scream = Candidate(lines=[], dur=8.0, score=0.0, text="AAAAH! Huh!")
    talk = Candidate(lines=[], dur=8.0, score=0.0, text="Man, I told you I got this, trust me dog, this the best idea I ever had.")
    assert judge(scream).score < -50
    assert judge(talk).score > -50
