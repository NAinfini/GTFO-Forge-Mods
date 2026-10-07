"""Import the energy weapon recordings as embedded PCM variants.

python import_audio.py --source <recordings folder> --qa <report.json>
Files are named `<weapon>-<event>-<version>.wav` and the cue is `<weapon>-<event>`; loops end in `-loop`.
Its cues.md lists every cue with its length; that table is the only length source.
One-shots keep full level for the table length, then up to TAIL more of their natural decay, faded out:
that tail is what overlaps the next sound (a beam's start under its loop, its stop over the loop's fade).
Cues listed under 1 s start just before their main attack (a quiet swell or a lone click would
delay the shot); longer cues start at their first sound, keeping a rise whole.
Loops keep their steady part, lose slow level drift (a swell would pulse every cycle) and become
seamless with an equal-power crossfade. Only cues present in the folder are replaced; the
other cues keep their current variant until their recording arrives.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import wave
from pathlib import Path

import numpy as np


MOD = Path(__file__).resolve().parents[1]
OUT = MOD / "Assets/Audio"
VARIANTS = OUT / "Variants"
RATE = 48000
TAIL = 0.3
CUES = re.compile(r'(?:Clip|Loop)\("([a-z][a-z0-9-]*)"')
# Listed recordings that are a layer of another cue's loop rather than a cue of their own: they play mixed together.
LAYERS = {"disc-rotor-loop": "disc-fly-loop"}

ROW = re.compile(r"^\|\s*([a-z][a-z0-9-]*)\s*\|\s*([0-9.]+)\s*seconds?(,\s*loop)?\s*\|")
FILE = re.compile(r"^([a-z][a-z0-9-]*)-([0-9]+)\.wav$")


def cue_table(table: Path) -> dict[str, tuple[float, bool]]:
    rows = {}
    for line in table.read_text(encoding="utf-8").splitlines():
        match = ROW.match(line)
        if match: rows[match[1]] = (float(match[2]), match[3] is not None)
    if not rows: raise ValueError(f"No cue rows in {table}")
    return rows


def read_wave(path: Path) -> np.ndarray:
    with wave.open(str(path), "rb") as wav:
        if wav.getframerate() != RATE or wav.getsampwidth() != 2 or wav.getnchannels() not in (1, 2):
            raise ValueError(f"Expected 48 kHz PCM16 mono/stereo: {path}")
        data = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2").astype(np.float32)
        return data.reshape(-1, wav.getnchannels()).mean(axis=1) / 32768.0


def envelope(data: np.ndarray, window: int) -> np.ndarray:
    energy = np.cumsum(np.r_[0.0, data.astype(np.float64) ** 2])
    rms = np.sqrt(np.maximum(0.0, (energy[window:] - energy[:-window]) / window))
    return np.r_[rms, np.full(window - 1, rms[-1])]


def one_shot(data: np.ndarray, seconds: float) -> tuple[np.ndarray, dict]:
    peak = float(np.max(np.abs(data)))
    level = envelope(data, round(0.02 * RATE))
    if seconds < 1.0:
        # Main attack: the first 30 ms during which the forward 20 ms level stays within -14 dB of its loudest point.
        # A lone click before a gap never holds that level, so it cannot delay the body of the shot, while steady
        # sound such as a flame's sputter does. The forward window already rises before the attack; 5 ms more keeps
        # its first transient whole.
        held_level = np.convolve(level > float(np.max(level)) * 0.2, np.ones(round(0.03 * RATE), dtype=int), "valid")
        start = max(0, int(np.argmax(held_level == round(0.03 * RATE))) - round(0.005 * RATE))
    else:
        start = max(0, int(np.argmax(np.abs(data) > peak * 0.01)) - round(0.002 * RATE))  # first sound, -40 dB
    audible = np.flatnonzero(level > peak * 0.003)  # -50 dB: the natural end of the tail
    natural = int(audible[-1]) + round(0.02 * RATE) if len(audible) else len(data)
    held = start + round(seconds * RATE)
    limit = held + round(TAIL * RATE)
    end = min(natural, limit, len(data))
    data = data[start:end].copy()
    # Transients inside the table length stay untouched; only the decay after it fades.
    fade = max(end - held, min(round(0.03 * RATE), len(data) // 8))
    data[-fade:] *= np.linspace(1.0, 0.0, fade, dtype=np.float32) ** 2
    rise = round(0.002 * RATE)
    data[:rise] *= np.linspace(0.0, 1.0, rise, dtype=np.float32)
    return data, {"trimStartSeconds": round(start / RATE, 4), "naturalSeconds": round((natural - start) / RATE, 4),
                  "cutInsideTail": natural > limit}


def steady(data: np.ndarray) -> tuple[np.ndarray, int]:
    # Recordings swell in and fade out at their edges; keep the steady part (within 10 dB of the median level).
    window = round(0.02 * RATE)
    level = envelope(data, window)
    kept = np.flatnonzero(level > np.median(level) * 10.0 ** (-10.0 / 20.0))
    start, end = int(kept[0]), int(kept[-1]) + window
    if end - start < RATE // 2: raise ValueError("Loop recording has under 0.5 s of steady sound")
    return data[start:end].copy(), start


def loop(layers: list[np.ndarray]) -> tuple[np.ndarray, dict]:
    parts = [steady(layer) for layer in layers]
    # Layers sound together at equal level over their common length, then loop as one recording.
    length = min(len(part) for part, _ in parts)
    data = sum(part[:length] / max(1e-9, float(np.sqrt(np.mean(part * part)))) for part, _ in parts).astype(np.float32)
    start = parts[0][1]
    # A loop must not swell or sag each cycle: divide out the slow (0.5 s) level drift, keeping fast texture.
    window = round(0.5 * RATE)
    slow = np.roll(envelope(data, window), window // 2)  # centred moving RMS
    data = (data * np.clip(np.median(slow) / np.maximum(slow, 1e-9), 0.25, 4.0)).astype(np.float32)
    fade = min(round(0.25 * RATE), len(data) // 5)
    # The tail crossfades into the head with equal power (uncorrelated noise keeps its level); the new last
    # sample is followed in the source by the new first sample's tail partner, so the wrap is continuous.
    weight = np.linspace(0.0, 1.0, fade, endpoint=False, dtype=np.float32)
    head, tail = data[:fade].copy(), data[-fade:]
    data = data[:-fade].copy()
    data[:fade] = tail * np.sqrt(1.0 - weight) + head * np.sqrt(weight)
    return data, {"trimStartSeconds": round(start / RATE, 4), "crossfadeSeconds": round(fade / RATE, 4)}


def process(layers: list[np.ndarray], seconds: float, looping: bool) -> tuple[bytes, dict]:
    if not looping and len(layers) != 1: raise ValueError("Only loops take layers")
    data, shape = loop(layers) if looping else one_shot(layers[0], seconds)
    rms = float(np.sqrt(np.mean(data * data)))
    peak = float(np.max(np.abs(data)))
    if rms < 1e-5: raise ValueError("Silent recording")
    gain = min(10.0 ** ((-20.0 if looping else -18.0) / 20.0) / rms, 0.84 / peak, 4.0)
    pcm = np.round(np.clip(data * gain, -1.0, 1.0) * 32767.0).astype("<i2")
    out = pcm.astype(np.float32) / 32768.0
    return pcm.tobytes(), {
        "seconds": round(len(pcm) / RATE, 4), "gain": round(gain, 5),
        "rms": round(float(np.sqrt(np.mean(out * out))), 5), "peak": round(float(np.max(np.abs(out))), 5),
        "loopJoinJump": round(float(abs(out[-1] - out[0])), 5) if looping else None, **shape}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", required=True, type=Path, help="folder with the recordings and cues.md")
    ap.add_argument("--qa", required=True, type=Path, help="per-cue level report to update")
    opt = ap.parse_args()
    source, qa_path = opt.source.resolve(), opt.qa.resolve()
    table = cue_table(source / "cues.md")
    recordings: dict[str, dict[int, Path]] = {}
    for path in sorted(source.glob("*.wav")):
        match = FILE.match(path.name)
        if not match or match[1] not in table: raise ValueError(f"Not a listed cue recording: {path.name}")
        recordings.setdefault(match[1], {})[int(match[2])] = path
    sound = (MOD / "Presentation/EnergySound.cs").read_text(encoding="utf-8")
    runtime = CUES.findall(sound[sound.index("internal static readonly Cue[] Cues"):])
    known = set(runtime)
    manifest_path = OUT / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    outputs = {entry["cue"]: entry for entry in manifest["outputs"] if entry["cue"] in known}
    for entry in outputs.values(): entry.pop("source", None)
    qa = json.loads(qa_path.read_text(encoding="utf-8")) if qa_path.is_file() else {}
    groups: dict[str, list[str]] = {}
    for listed in sorted(recordings): groups.setdefault(LAYERS.get(listed, listed), []).append(listed)
    for cue, members in sorted(groups.items()):
        if cue not in known: raise ValueError(f"Runtime has no cue {cue}")
        if cue not in recordings: raise ValueError(f"{cue} has a layer but no recording of its own yet")
        sources = []
        for member in [cue] + [m for m in members if m != cue]:
            versions = recordings[member]
            if len(versions) != 1: raise ValueError(f"{member} has versions {sorted(versions)}; keep one selected recording")
            sources.append(next(iter(versions.values())))
        seconds, looping = table[cue]
        data, metrics = process([read_wave(source) for source in sources], seconds, looping)
        output = f"{cue}.bin"
        (VARIANTS / output).write_bytes(data)
        outputs[cue] = {"cue": cue, "file": f"Variants/{output}", "sources": [f"energy-weapons/{source.name}" for source in sources],
                        "tableSeconds": seconds, "loop": looping,
                        "sourceSha256": [hashlib.sha256(source.read_bytes()).hexdigest() for source in sources],
                        "sha256": hashlib.sha256(data).hexdigest(), "seconds": metrics["seconds"]}
        qa[cue] = metrics
        print(f"{cue:16} {'+'.join(source.stem for source in sources)} {metrics['seconds']:.3f}s gain {metrics['gain']:.2f} peak {metrics['peak']:.3f}"
              + (f" join {metrics['loopJoinJump']:.4f}" if looping else
                 f" natural {metrics['naturalSeconds']:.3f}s{' (cut, faded)' if metrics['cutInsideTail'] else ''}"))
    missing = [cue for cue in runtime if cue not in outputs]
    if missing: raise ValueError(f"Runtime cues without a recording: {missing}")
    ordered = [outputs[cue] for cue in runtime]
    names = {entry["file"].split("/")[-1] for entry in ordered}
    for old in VARIANTS.glob("*.bin"):
        if old.name not in names: old.unlink()
    (OUT / "variants.tsv").write_text("".join(f"{e['cue']}\t{e['file'].split('/')[-1]}\n" for e in ordered), encoding="utf-8")
    old = [e["cue"] for e in ordered if "sources" not in e]
    manifest = {
        "format": "signed little-endian PCM16 mono 48000 Hz, one recording per runtime cue",
        "generator": "tools/import_audio.py",
        "source": "original recordings (not published); cues listed in cues.md",
        "rights": "Original audio for Forge Weapon Energy Lab experimental; MIT, copyright 2026 NAinfini",
        "pendingFromOldLibrary": old,
        "outputs": ordered,
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    qa_path.parent.mkdir(parents=True, exist_ok=True)
    qa_path.write_text(json.dumps(qa, indent=2) + "\n", encoding="utf-8")
    print(f"{len(recordings)} of {len(table)} listed cue recordings imported")


if __name__ == "__main__": main()
