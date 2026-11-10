"""Build a ForgeAudio audio folder from the recorded source list.

The owner recordings are read only.  Each source is prepared by ForgeAudio's ``cue_render`` (lead-in removed, declick fades, loudness normalised; loops
joined with an equal-power crossfade) and then encoded to Vorbis by ForgeAudio's converter.  Original recordings never enter the package.

Every cue lists its ``layers``; each layer has the ``sources`` that form its variant pool and the layer's own AudioKit settings
(``offsetMs``, ``gainDb``, ``fadeInMs``, ``fadeOutMs``, ``distance``...).  A source is a recorded file section (``startSeconds``,
``seconds``) and, for a cue with the ``exact`` rule, an optional ``gainDb`` that aligns a variant to its reference.  Cue fields
``loop``, ``leadIn`` and ``exact`` are source rules (see cue_render.py); ``bus``, ``maxSeconds``, ``space`` and the rest are
AudioKit cue settings that AudioKit applies when it loads the folder.

Output follows the AudioKit folder convention: ``<cue>.ogg`` for a one-layer, one-source cue, ``<cue>-01.ogg``... for a one-layer
cue with variants, ``<cue>-l<layer>-01.ogg``... for a cue with several layers, and ``cues.forge`` (``{"buses": ..., "cues": ...}``)
with every cue's layers and settings.  Stale ``.ogg`` files in the output folder are removed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile

REPO = Path(__file__).resolve().parents[3]
AUDIOKIT_TOOLS = REPO / "Mods/ForgeAudio/tools"
AUDIOKIT_CONVERTER = AUDIOKIT_TOOLS / "convert_audio.py"
sys.path.insert(0, str(AUDIOKIT_TOOLS))
import cue_render  # noqa: E402

RATE = cue_render.RATE
MAX_SECONDS = cue_render.MAX_SECONDS
StageError = cue_render.RenderError
run = cue_render.run


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def render_rules(cue: dict[str, object]) -> dict[str, object]:
    """The cue_render rules of one sources.forge cue."""
    rules: dict[str, object] = {"loop": bool(cue.get("loop"))}
    for key in ("leadIn", "exact"):
        if key in cue:
            rules[key] = cue[key]
    return rules


def validate_document(document: dict[str, object]) -> None:
    if document.get("schemaVersion") != "1.0.0":
        raise StageError("sources schemaVersion must be 1.0.0")
    cues = document.get("cues")
    if not isinstance(cues, list) or not cues:
        raise StageError("sources must contain a non-empty cues array")
    buses = document.get("buses", {})
    if not isinstance(buses, dict):
        raise StageError("buses must be an object")
    for name, bus in buses.items():
        if not isinstance(bus, dict) or bus.get("category") not in CATEGORIES:
            raise StageError("bus " + name + " needs a category: one of " + ", ".join(CATEGORIES))
        duck = bus.get("duck")
        if duck is not None and (not isinstance(duck, dict) or not duck.get("categories")
                                 or any(category not in CATEGORIES for category in duck["categories"])):
            raise StageError("bus " + name + " duck.categories must list categories: " + ", ".join(CATEGORIES))
    identifiers: set[str] = set()
    for cue in cues:
        if not isinstance(cue, dict) or not isinstance(cue.get("id"), str):
            raise StageError("every cue must have a string id")
        if cue["id"] in identifiers:
            raise StageError("duplicate cue " + cue["id"])
        identifiers.add(cue["id"])
        try:
            cue_render.validate_rules(render_rules(cue))
        except StageError as error:
            raise StageError(cue["id"] + " " + str(error)) from error
        if "maxSeconds" in cue and not (isinstance(cue["maxSeconds"], (int, float)) and 0.05 <= cue["maxSeconds"] <= 60 and not cue.get("loop")):
            raise StageError(cue["id"] + " maxSeconds must be between 0.05 and 60 and is for one-shots only")
        if "category" in cue:
            raise StageError(cue["id"] + " has a category: the category belongs to its bus")
        if cue.get("bus") not in buses:
            raise StageError(cue["id"] + " needs a bus from buses (got " + str(cue.get("bus")) + ")")
        layers = cue.get("layers")
        if not isinstance(layers, list) or not layers:
            raise StageError(cue["id"] + " has no layers")
        if cue.get("loop") and len(layers) != 1:
            raise StageError(cue["id"] + " is a loop and must have exactly one layer")
        exact = bool(cue.get("exact"))
        required = ("file", "sha256", "pack", "licence") + (("seconds",) if exact else ("targetLufs",))
        for layer in layers:
            if not isinstance(layer, dict):
                raise StageError(cue["id"] + " has a non-object layer")
            unknown = sorted(set(layer) - set(LAYER_SETTINGS) - {"sources"})
            if unknown:
                raise StageError(cue["id"] + " layer has unknown field " + ", ".join(unknown))
            sources = layer.get("sources")
            if not isinstance(sources, list) or not sources:
                raise StageError(cue["id"] + " has a layer without sources")
            for source in sources:
                if not isinstance(source, dict):
                    raise StageError(cue["id"] + " has a non-object source")
                if any(not source.get(field) and source.get(field) != 0 for field in required):
                    raise StageError(cue["id"] + " source lacks " + "/".join(required))
                if "gainDb" in source and not exact:
                    raise StageError(cue["id"] + " source gainDb needs the exact rule")


# ForgeAudio's global categories: every bus names one, and a bus ducks categories.
CATEGORIES = ("ui", "weapon", "explosion", "debris", "ambience", "voice", "vehicle", "device", "music")
# sources.forge cue fields that are AudioKit cue settings, with their cues.forge names.
SETTINGS = ("gain", "loop", "priority", "maxVoices", "maxPerEmitter", "minInterval", "environment", "maxSeconds", "bus")
# A layer's own AudioKit settings (sources.forge ``layers`` entries, beside the layer's ``sources``).
LAYER_SETTINGS = ("offsetMs", "gain", "gainDb", "range", "fadeInMs", "fadeOutMs", "pitch", "distance", "lowpassHz")
PLACE = {"2d": "2d", "3d": "auto"}


def cue_settings(cue: dict[str, object], clips: list[list[str]]) -> dict[str, object]:
    """The cues.forge entry for one sources.forge cue; ``clips`` lists the staged clip files of each of its layers."""
    settings: dict[str, object] = {}
    for key in SETTINGS:
        if key in cue:
            settings[key] = cue[key]
    if "pitchRange" in cue:
        low, high = cue["pitchRange"]
        settings["pitch"] = low if low == high else [low, high]
    space = dict(cue.get("space", {}))
    if "distance" in cue:
        space = {"near": cue["distance"]["near"], "far": cue["distance"]["far"]} | space
    if space:
        settings["space"] = space
    if "mix" in cue:
        settings["place"] = {side: PLACE[cue["mix"][side]["spatial"]] for side in ("self", "other") if side in cue["mix"]}
    layers = []
    for layer, layer_clips in zip(cue["layers"], clips):
        entry: dict[str, object] = {"clips": layer_clips}
        for key in LAYER_SETTINGS:
            if key in layer:
                entry[key] = layer[key]
        layers.append(entry)
    settings["layers"] = layers
    return settings

def write_json(path: Path, value: object) -> None:
    descriptor = json.dumps(value, indent=2, ensure_ascii=False) + "\n"
    handle, temporary_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as target:
            target.write(descriptor)
        os.replace(temporary_name, path)
    finally:
        if os.path.exists(temporary_name):
            os.remove(temporary_name)


def stage(sources_path: Path, library_root: Path, output_root: Path, quality: float) -> dict[str, object]:
    document = json.loads(sources_path.read_text(encoding="utf-8"))
    validate_document(document)
    library_root = library_root.resolve()
    output_root.mkdir(parents=True, exist_ok=True)
    clips_of: dict[str, list[list[str]]] = {}
    written: set[str] = set()
    receipts = []
    source_hashes: dict[Path, str] = {}  # a recording is hashed once however many sections of it are staged
    with tempfile.TemporaryDirectory(prefix="forge-ordnance-audio-") as temporary_name:
        temporary = Path(temporary_name)
        for cue in document["cues"]:
            rules = render_rules(cue)
            max_seconds = cue_render.EXACT_MAX_SECONDS + 0.1 if rules.get("exact") else MAX_SECONDS
            layered = len(cue["layers"]) > 1
            clips_of[cue["id"]] = []
            for layer_number, layer in enumerate(cue["layers"], 1):
                clips = []
                for index, source_entry in enumerate(layer["sources"], 1):
                    source = (library_root / source_entry["file"]).resolve()
                    try:
                        source.relative_to(library_root)
                    except ValueError as error:
                        raise StageError(cue["id"] + " source escapes the library root") from error
                    if not source.is_file():
                        raise StageError("missing source " + str(source))
                    if source not in source_hashes:
                        source_hashes[source] = sha256(source)
                    source_hash = source_hashes[source]
                    if source_hash != source_entry["sha256"]:
                        raise StageError(cue["id"] + " source hash differs: " + str(source))

                    prepared = temporary / f"{len(receipts):03d}-prepared.wav"
                    encoded = temporary / f"{len(receipts):03d}.ogg"
                    evidence = temporary / f"{len(receipts):03d}.json"
                    samples, info = cue_render.render_source(
                        source, float(source_entry.get("startSeconds", 0.0)), source_entry.get("seconds"), rules,
                        float(source_entry.get("targetLufs", cue_render.DEFAULT_LUFS)), float(source_entry.get("gainDb", 0.0)),
                        cue_render.lift_target(rules, source_entry, layered))
                    cue_render.write_pcm(prepared, samples)
                    run([sys.executable, str(AUDIOKIT_CONVERTER), "convert", str(prepared), str(encoded), "--evidence", str(evidence),
                         "--quality", str(quality), "--max-seconds", str(max_seconds)], "ForgeAudio Vorbis conversion")
                    if layered:
                        name = f"{cue['id']}-l{layer_number}-{index:02d}.ogg"
                    else:
                        name = cue["id"] + ".ogg" if len(layer["sources"]) == 1 else f"{cue['id']}-{index:02d}.ogg"
                    destination = output_root / name
                    os.replace(encoded, destination)
                    written.add(name)
                    clips.append(name)
                    receipts.append({
                        "cue": cue["id"],
                        "clip": name,
                        "sha256": sha256(destination),
                        "seconds": round(info.seconds, 6),
                        "bytes": destination.stat().st_size,
                        "sourceSha256": source_hash,
                        "gainDb": round(info.gain_db, 3),
                    })
                clips_of[cue["id"]].append(clips)

    for stale in output_root.glob("*.ogg"):
        if stale.name not in written:
            stale.unlink()
    settings = {cue["id"]: cue_settings(cue, clips_of[cue["id"]]) for cue in document["cues"]}
    settings_path = output_root / "cues.forge"
    write_json(settings_path, {"buses": document.get("buses", {}), "cues": settings})
    return {"settings": str(settings_path), "cues": len(settings), "clips": receipts}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources", type=Path, required=True)
    parser.add_argument("--library", type=Path, default=Path(r"F:\音效库"))
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--quality", type=float, default=4.0)
    args = parser.parse_args()
    try:
        receipt = stage(args.sources, args.library, args.out, args.quality)
        print(json.dumps(receipt, indent=2, ensure_ascii=False))
        return 0
    except (OSError, StageError, json.JSONDecodeError) as error:
        print("stage_audio: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
