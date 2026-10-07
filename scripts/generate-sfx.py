"""Generate the project's four synthesized cues without external audio samples.

Run: python3 scripts/generate-sfx.py [output-directory]
"""
import math
from pathlib import Path
import random
import struct
import sys
import wave

RATE = 44100
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1] / 'Assets/Audio/SFX'
OUT.mkdir(parents=True, exist_ok=True)


def tone(t, start, end, duration):
    phase = 2 * math.pi * (start * t + (end - start) * t * t / (2 * duration))
    return (math.sin(phase) + .22 * math.sin(3 * phase)) / 1.22


def make(name, duration, peak, voice):
    rng = random.Random(160)
    samples = []
    for i in range(round(duration * RATE)):
        t = i / RATE
        envelope = min(1, t / .003) * max(0, 1 - t / duration) ** 1.1
        samples.append(envelope * voice(t, rng))
    scale = peak / max(abs(x) for x in samples)
    pcm = [round(x * scale * 32767) for x in samples]
    pcm[0] = pcm[-1] = 0
    with wave.open(str(OUT / (name + '.wav')), 'wb') as f:
        f.setparams((1, 2, RATE, 0, 'NONE', 'not compressed'))
        f.writeframes(struct.pack('<' + 'h' * len(pcm), *pcm))


make('player_jump', .28, .25, lambda t, r: tone(t, 240, 920, .28))
make('player_land', .06, .10, lambda t, r: .7 * tone(t, 140, 45, .06) + .3 * r.uniform(-1, 1))
make('player_dash', .24, .125, lambda t, r: .65 * r.uniform(-1, 1) + .35 * tone(t, 800, 120, .24))
make('control_swap', .42, .25, lambda t, r: .7 * math.sin(2 * math.pi * 480 * t) + .3 * math.sin(2 * math.pi * 720 * t))
