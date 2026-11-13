"""Build the local ForgeAudio audio folder (one <asset>.ogg per recorded source, and cues.forge) from sources.forge.

sources.forge records each cue's ``maxSeconds`` and layers under ``cues`` and each recorded section under ``sources``: ``layer`` is the
1-based layer whose variant pool it belongs to, ``start`` and ``duration`` are the section, ``gainDb`` aligns a variant to its reference."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


PACKAGE = Path(__file__).resolve().parents[2]
REPO = PACKAGE.parents[1]
OUTPUT = PACKAGE / "Assets" / "Frames" / "Audio"
SOURCES = OUTPUT / "sources.forge"
RECORDED_LIBRARY = "F:/音效库/"
AUDIOKIT_TOOLS = REPO / "Mods" / "ForgeCore" / "Audio" / "tools"
CONVERTER = AUDIOKIT_TOOLS / "convert_audio.py"
sys.path.insert(0, str(AUDIOKIT_TOOLS))
import cue_render  # noqa: E402
PREFIX = "forgeordnance.frames:"


def run(command: list[str]) -> None:
    result = subprocess.run(command, text=True, encoding="utf-8", errors="replace", capture_output=True)
    if result.returncode:
        raise RuntimeError((result.stderr or result.stdout).strip())


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_sources(sources: list[dict]) -> None:
    """Every recorded source file must still have the recorded SHA-256."""
    digests: dict[str, str] = {}  # a recording is hashed once however many sections of it are recorded
    for source in sources:
        if source["path"] not in digests:
            digests[source["path"]] = digest(Path(source["path"]))
        if digests[source["path"]] != source["sha256"]:
            raise RuntimeError("source file changed since it was recorded: " + source["path"])

def preprocess(source: dict, loop: bool, layered: bool, target: Path) -> None:
    """The recorded section exactly as cut: no trimming or fades (the cue's layers set fades and gain).  ``gainDb`` aligns a
    variant to the section it varies, a loop is joined with ForgeAudio's equal-power crossfade, and a quiet one-shot is lifted
    toward the kit's one-shot loudness (``targetLufs`` in the source overrides it, ``false`` keeps the level)."""
    rules = {"exact": True, "loop": loop}
    samples, _ = cue_render.render_source(Path(source["path"]), float(source.get("start", 0)), float(source["duration"]),
                                          rules, gain_db=float(source.get("gainDb", 0)),
                                          lift_lufs=cue_render.lift_target(rules, source, layered))
    cue_render.write_pcm(target, samples)

def asset_file(source_id: str) -> str:
    """The audio file of one recorded source: its id without the package prefix."""
    return source_id[len(PREFIX):] + ".ogg"


# Buses hold the sounds of one event chain of one emitter: a new sound on a full bus fades the weakest holder out in 60 ms
# (seats 1: mutually exclusive) unless the holder has the higher priority. Each bus maps to a ForgeAudio global category
# (its voice limit and volume are shared with every mod). Alerts and hits duck the vehicle category (servos, steps and
# mechanisms of every suit and drop pod, of any mod) by 6 dB while they play.
# The opening and closing make three sounds in all (PlateTimeline), all from the suit itself: the unseal rings on while the slide
# and the closing latch come, so mech gives each of them a seat.
MECH_SEATS = 3
# A footstep keeps its whole tail (up to 1 s), so steps overlap: a sprint's fastest step (Gait.MinStepGap, 0.2 s) leaves at most 5 alive at once.
# Their own bus keeps a step from taking another's seat (the 2-seat gait bus faded the earlier step out in 60 ms).
STEP_SEATS = 5
BUSES = {
    "gait": {"category": "vehicle", "seats": 2}, "step": {"category": "vehicle", "seats": STEP_SEATS}, "state": {"category": "vehicle", "seats": 3},
    "mech": {"category": "vehicle", "seats": MECH_SEATS},
    "voice": {"category": "voice", "seats": 1}, "ui": {"category": "ui", "seats": 1}, "pilot": {"category": "vehicle", "seats": 1},
    "hit": {"category": "explosion", "seats": 2, "duck": {"categories": ["vehicle"], "db": 6}},
    "alert": {"category": "ui", "seats": 2, "duck": {"categories": ["vehicle"], "db": 6}},
    "servo": {"category": "vehicle"},
}


def cue(cue_id: str, bus: str, gain: float, loop: bool, near: float, far: float,
        priority: int, voices: int = 8, per_emitter: int = 1, gap: float = 0) -> tuple[str, dict]:
    """A suit sound heard in 3D by others and in 2D by its own pilot."""
    settings = {"gain": gain, "bus": bus,
                "pitch": [0.96, 1.04] if cue_id == "exo.footstep" else 1.0, "loop": loop,
                "space": {"near": near, "far": far}, "place": {"self": "2d", "other": "auto"},
                "priority": priority, "maxVoices": voices, "maxPerEmitter": per_emitter, "environment": True}
    if gap:
        settings["minInterval"] = gap
    return cue_id, settings


def local_cue(cue_id: str, bus: str, gain: float, priority: int, per_emitter: int = 1,
              gap: float = 0, pitch: float = 1.0) -> tuple[str, dict]:
    """A sound only the local pilot hears: placed in 2D, so no distance, room or spatial mix applies."""
    settings = {"gain": gain, "bus": bus, "space": {"near": 1, "far": 10}, "place": "2d",
                "priority": priority, "maxPerEmitter": per_emitter, "environment": False}
    if pitch != 1.0:
        settings["pitch"] = pitch
    if gap:
        settings["minInterval"] = gap
    return cue_id, settings


def settings_table() -> dict[str, dict]:
    """Every cue of FrameSounds.Table with its settings (no layers); gap is the minimum time between two plays."""
    return dict([
        # 0.40 = -8 dB (0.56 lowered by a further 3 dB): the walking step sits under the landing and the suit's servos.
        cue("exo.footstep", "step", 0.25, False, 4, 30, 55, 24, STEP_SEATS),
        cue("exo.landing", "gait", 1.1, False, 15, 80, 150),
        cue("exo.land", "gait", 1, False, 5, 40, 120, 8, 2, .25),
        cue("exo.visor_boot", "state", 0.94, False, 2, 15, 150),
        cue("exo.voice_ready", "voice", 1, False, 3, 25, 210),
        cue("exo.servo_loop", "servo", 0.6, True, 3, 30, 80, 8),
        cue("exo.damage_alarm", "alert", 0.94, False, 2, 15, 220),
        cue("exo.destroyed", "alert", 1, False, 10, 80, 190),
        cue("exo.boost", "servo", 1, True, 5, 40, 140),
        cue("exo.shield_up", "state", 0.88, False, 4, 30, 140),
        cue("exo.shield_hit", "hit", 0.88, False, 3, 25, 135, 16, 4),
        cue("exo.shield_down", "state", 1, False, 6, 40, 175),
        cue("exo.rocket", "state", 1, False, 5, 50, 145),
        cue("exo.plate_slide", "mech", 0.88, False, 4, 35, 125, gap=.1),
        cue("exo.hatch_close", "mech", 1, False, 4, 35, 135, gap=.1),
        cue("exo.unseal", "mech", 1, False, 4, 35, 130, gap=.1),
        cue("exo.power_down", "state", 1, False, 3, 25, 120, gap=.5),
        cue("exo.boost_end", "state", 0.88, False, 3, 25, 130, gap=.5),
        cue("exo.siege_on", "state", 1, False, 6, 50, 145, gap=1),
        cue("exo.siege_off", "state", 1, False, 6, 45, 140, gap=1),
        # The deploy's ratchet clicks, from the start of the deployment until it locks (siege_on) or is cancelled.
        cue("exo.siege_deploy", "state", 0.9, False, 5, 40, 138, 8, 2, gap=.3),
        cue("exo.breach_hit", "hit", 1, False, 6, 60, 180, 8, 2, gap=.15),
        cue("exo.shove_hit", "hit", 1, False, 5, 45, 175, 8, 2, gap=.12),
        cue("exo.armor_hit", "hit", 1, False, 3, 30, 160, 16, 2, gap=.12),
        # A rocket's blast, on its own emitter at the impact point: heard across the room, every impact of a salvo its own voice.
        cue("exo.rocket_blast", "hit", 1, False, 8, 90, 170, 12),
        local_cue("exo.hud_section", "ui", 0.88, 200, 5, gap=.1),
        local_cue("exo.jump", "pilot", 1, 150, gap=.25),
        local_cue("exo.reload_start", "pilot", 0.88, 150, gap=.3),
        local_cue("exo.reload_done", "pilot", 0.88, 150, gap=.3),
        local_cue("exo.dry_fire", "pilot", 0.88, 150, gap=.15),
        # The planted gun's bolt, one per shot (a rapid string of them, each cutting the one before): the dry-fire click's clicks without its beep,
        # raised (pitch 1.35) and quieter (0.88 x 0.55) than walking fire so siege fire sounds faster and harder.
        local_cue("exo.siege_bolt", "pilot", 0.484, 150, per_emitter=2, gap=.1, pitch=1.35),
        local_cue("ui.hold_complete", "ui", 0.88, 200, gap=.3),
        local_cue("ui.hold_cancel", "ui", 0.88, 200, gap=.15),
    ])


LAYER_SETTINGS = ("offsetMs", "gainDb", "fadeInMs", "fadeOutMs", "distance")


def cue_table(document: dict) -> dict[str, dict]:
    """The settings table with each cue's layers from ``document["cues"]`` (one-shots: ``maxSeconds``; every layer: its own
    AudioKit settings) and, per layer, the sources recorded for it in recorded order as its variant pool.
    A cue or layer without a source is an error; a one-shot is capped at its ``maxSeconds`` by AudioKit."""
    table = settings_table()
    recorded = document["cues"]
    sources = document["sources"]
    unknown = sorted(({source["cue"] for source in sources} | set(recorded)) - set(table))
    if unknown:
        raise RuntimeError("sources for cues that do not exist: " + ", ".join(unknown))
    for cue_id, settings in table.items():
        if cue_id not in recorded:
            raise RuntimeError("cue " + cue_id + " has no recorded layers")
        layers = []
        for number, layer in enumerate(recorded[cue_id]["layers"], 1):
            extra = sorted(set(layer) - set(LAYER_SETTINGS))
            if extra:
                raise RuntimeError(f"cue {cue_id} layer {number} has unknown field " + ", ".join(extra))
            clips = [asset_file(source["id"]) for source in sources if source["cue"] == cue_id and source["layer"] == number]
            if not clips:
                raise RuntimeError(f"cue {cue_id} layer {number} has no recorded source")
            layers.append({"clips": clips} | layer)
        if settings.get("loop"):
            if len(layers) != 1 or "maxSeconds" in recorded[cue_id]:
                raise RuntimeError("cue " + cue_id + " is a loop: one layer and no maxSeconds")
        else:
            settings["maxSeconds"] = recorded[cue_id]["maxSeconds"]
        settings["layers"] = layers
        if sum(source["cue"] == cue_id for source in sources) != sum(len(layer["clips"]) for layer in layers):
            raise RuntimeError("cue " + cue_id + " has sources for layers it does not declare")
    return table


def write_cues(output: Path, document: dict) -> int:
    cues = cue_table(document)
    text = json.dumps({"buses": BUSES, "cues": cues}, ensure_ascii=False, indent=2) + "\n"
    temporary = output / "cues.forge.tmp"
    temporary.write_text(text, encoding="utf-8", newline="\n")
    os.replace(temporary, output / "cues.forge")
    return len(cues)

def build(library: Path | None) -> None:
    """``library`` is a copy of the sound library (same relative layout) read instead of the recorded F:/音效库 paths."""
    document = json.loads(SOURCES.read_text(encoding="utf-8"))
    sources = document["sources"]
    if library is not None:
        for source in sources:
            if not source["path"].startswith(RECORDED_LIBRARY):
                raise RuntimeError("source is outside the recorded library: " + source["path"])
            source["path"] = str(library / source["path"][len(RECORDED_LIBRARY):])
    verify_sources(sources)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    written: set[str] = set()
    with tempfile.TemporaryDirectory(prefix="forge-frames-audio-") as directory:
        temporary = Path(directory)
        for index, source in enumerate(sources):
            original = Path(source["path"])
            if not original.is_file():
                raise RuntimeError("missing source: " + str(original))
            wave = temporary / f"{index}.wav"
            ogg = temporary / f"{index}.ogg"
            evidence = temporary / f"{index}.audio.json"
            preprocess(source, bool(settings_table()[source["cue"]].get("loop")), len(document["cues"][source["cue"]]["layers"]) > 1, wave)
            run([sys.executable, str(CONVERTER), "convert", str(wave), str(ogg), "--evidence", str(evidence),
                 "--quality", "5", "--max-seconds", str(cue_render.EXACT_MAX_SECONDS + 0.1)])
            name = asset_file(source["id"])
            os.replace(ogg, OUTPUT / name)
            written.add(name)

    for stale in OUTPUT.glob("*.ogg"):
        if stale.name not in written:
            stale.unlink()
    print(f"Prepared {len(sources)} Ogg files for {write_cues(OUTPUT, document)} cues in {OUTPUT}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--library", type=Path, help="a copy of the sound library to read instead of F:/音效库")
    args = parser.parse_args()
    try:
        build(args.library)
        return 0
    except (OSError, RuntimeError, KeyError, ValueError, json.JSONDecodeError) as error:
        print("prepare_audio: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
