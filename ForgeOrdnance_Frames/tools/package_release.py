"""Builds the Thunderstore zip of ForgeOrdnance_Frames into artifacts/work/ordnance-release/. Nothing is published.

Run from the repository root after the Release build, the art build and the icon build:
    dotnet build Mods/ForgeOrdnance_Frames/ForgeOrdnanceFrames.csproj -c Release --artifacts-path artifacts/dotnet ...
    pwsh Mods/ForgeOrdnance_Frames/tools/build_assets.ps1
    pwsh Mods/ForgeOrdnance_Frames/tools/build_icons.ps1
    python Mods/ForgeOrdnance_Frames/tools/package_release.py

The zip holds manifest.json, icon.png, README.md, CHANGELOG.md, ForgeOrdnanceFrames.dll and forge-frames.bundle at its root,
the models' attribution (Assets/Models/License.txt) as models/LICENSE.txt, the ForgeAudio audio folder (cues.json,
licence, Ogg files) under audio/, the words (lang/), the cockpit art (Assets/hud/ as hud/, all of it), and the definitions
(Definitions/frames.json) with their icons and words in ForgeOrdnance/ (r2modman installs it all into
BepInEx/plugins/NAinfini-ForgeOrdnance_Frames/, where ForgeOrdnance finds the folder). Fixed timestamps: the same inputs give
the same bytes. The manifest is checked against Thunderstore's rules first.
"""
import hashlib
import json
import os
import re
import struct
import sys
import tempfile
import zipfile

REPO = os.getcwd()
PACKAGE = os.path.join(REPO, "Mods", "ForgeOrdnance_Frames")
# Local media (cockpit art, icons, Ogg files) and build outputs live in the main checkout; a worktree points at them with
# FORGE_MODS_ROOT (the Mods folder) and FORGE_ARTIFACTS_ROOT (the artifacts folder). Without them everything is read beside the repository.
MEDIA = os.path.join(os.environ.get("FORGE_MODS_ROOT") or os.path.join(REPO, "Mods"), "ForgeOrdnance_Frames")
ARTIFACTS = os.environ.get("FORGE_ARTIFACTS_ROOT") or os.path.join(REPO, "artifacts")
OUT = os.path.join(ARTIFACTS, "work", "ordnance-release")
DLL = os.path.join(REPO, "artifacts", "dotnet", "bin", "ForgeOrdnanceFrames", "release", "ForgeOrdnanceFrames.dll")
MODELS = os.path.join(MEDIA, "Assets", "Models")
BUNDLE = os.path.join(MODELS, "forge-frames.bundle")
RECEIPT = os.path.join(MODELS, "forge-frames.receipt.json")
ICONS = os.path.join(MEDIA, "Assets", "icons")
AUDIO = os.path.join(MEDIA, "Assets", "audio")
MODELS_LICENCE = os.path.join(PACKAGE, "Assets", "Models", "License.txt")
TEAM = "NAinfini"
# The cockpit art the plugin loads (Native/FrameHudArt.cs), each also as name@2x.png.
HUD_ART = [name + ".png" for name in (
    "skill-charge", "skill-siege", "skill-scan", "skill-rockets", "skill-lamp", "skill-exit", "skill-carry",
    "shield", "heat", "objective", "teammate", "arrow-around",
    "mark-frame-m15", "mark-frame-m16", "mark-frame-m17", "mark-frame-m18",
    "damage-front", "damage-back", "damage-left", "damage-right")]


def fail(message):
    sys.exit("package_release: " + message)


def png_size(path):
    with open(path, "rb") as image:
        head = image.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        fail(path + " is not a PNG")
    return struct.unpack(">II", head[16:24])


def check_manifest():
    with open(os.path.join(PACKAGE, "manifest.json"), encoding="utf-8") as source:
        manifest = json.load(source)
    for key in ("name", "version_number", "website_url", "description", "dependencies"):
        if key not in manifest:
            fail("manifest.json lacks " + key)
    if not re.fullmatch(r"[A-Za-z0-9_]+", manifest["name"]):
        fail("the name may hold only letters, digits and _")
    if not re.fullmatch(r"\d+\.\d+\.\d+", manifest["version_number"]):
        fail("version_number must be major.minor.patch")
    if len(manifest["description"]) > 250:
        fail("the description is longer than 250 characters")
    for dependency in manifest["dependencies"]:
        if not re.fullmatch(r"[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+", dependency):
            fail("dependency '" + dependency + "' is not Team-Name-1.2.3")
    if png_size(os.path.join(PACKAGE, "icon.png")) != (256, 256):
        fail("icon.png must be 256 x 256")
    return manifest


def sha256(path):
    with open(path, "rb") as source:
        return hashlib.sha256(source.read()).hexdigest()


def check_bundle(definitions):
    if not os.path.isfile(RECEIPT):
        fail("missing bundle receipt (rebuild with tools/build_assets.ps1)")
    with open(RECEIPT, encoding="utf-8") as source:
        receipt = json.load(source)
    expected = {entry["Payload"]["Model"].split(":", 1)[1] for entry in definitions}
    if receipt.get("format") != 2 or set(receipt.get("models", [])) != expected:
        fail("bundle receipt does not cover the shipped definitions")
    if not os.path.isfile(BUNDLE) or receipt.get("bundleBytes") != os.path.getsize(BUNDLE) or receipt.get("bundleSha256") != sha256(BUNDLE):
        fail("bundle does not match its verified build receipt")
    inputs = receipt.get("inputs", {})
    required = {"Mods/ForgeOrdnance_Frames/Definitions/frames.json"}
    required.update("Mods/ForgeOrdnance_Frames/tools/" + name for name in os.listdir(os.path.join(PACKAGE, "tools"))
                    if name.endswith((".cs", ".ps1", ".py")))
    sources = receipt.get("sources", {})
    roots = receipt.get("roots", {})
    if not required.issubset(inputs) or not any(name.startswith("cut/") for name in sources) or set(roots) != {"cut", "pilot", "props"}:
        fail("bundle receipt lacks source hashes")
    for name, digest in inputs.items():
        path = os.path.join(REPO, name)
        if not os.path.isfile(path) or sha256(path) != digest:
            fail("bundle build input changed: " + name + " (rebuild assets)")
    for name, digest in sources.items():
        root, _, relative = name.partition("/")
        path = os.path.join(roots.get(root, ""), relative)
        if not os.path.isfile(path) or sha256(path) != digest:
            fail("bundle build input changed: " + name + " (rebuild assets)")


def main():
    if not os.path.isdir(PACKAGE):
        fail("run from the repository root")
    manifest = check_manifest()
    files = {name: os.path.join(PACKAGE, name) for name in ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE")}
    files["ForgeOrdnanceFrames.dll"] = DLL
    files["forge-frames.bundle"] = BUNDLE
    if not os.path.isfile(MODELS_LICENCE):
        fail("missing the models' attribution (Assets/Models/License.txt)")
    files["models/LICENSE.txt"] = MODELS_LICENCE
    cue_settings = os.path.join(AUDIO, "cues.json")
    licence = os.path.join(AUDIO, "LICENSE.txt")
    if not os.path.isfile(cue_settings) or not os.path.isfile(licence):
        fail("missing prepared audio cues.json or licence (run tools/prepare_audio.py)")
    files["audio/cues.json"] = cue_settings
    files["audio/LICENSE.txt"] = licence
    with open(cue_settings, encoding="utf-8") as source:
        cues = json.load(source)
    for clip in sorted({clip for entry in cues["cues"].values() for layer in entry["layers"] for clip in layer["clips"]}):
        audio = os.path.join(AUDIO, clip)
        if not re.fullmatch(r"[a-z0-9_.-]+\.ogg", clip) or not os.path.isfile(audio):
            fail("missing audio file " + clip + " (run tools/prepare_audio.py)")
        files["audio/" + clip] = audio
    definitions = os.path.join(PACKAGE, "Definitions", "frames.json")
    files["ForgeOrdnance/frames.json"] = definitions
    with open(definitions, encoding="utf-8") as source:
        entries = json.load(source)
    check_bundle(entries)
    for entry in entries:
        icon = entry.get("Icon")
        if not icon:
            fail(entry["Id"] + " has no Icon")
        source = os.path.join(ICONS, icon)
        if not os.path.isfile(source):
            fail("missing icon " + source + " (run tools/build_icons.ps1)")
        if png_size(source) != (256, 256):
            fail(source + " is not 256 x 256")
        files["ForgeOrdnance/" + icon] = source
    # The words: Frames' own (lang/) and the definitions' names and descriptions (ForgeOrdnance/lang/, where ForgeOrdnance reads them).
    for folder, target in (("lang", "lang/"), (os.path.join("Definitions", "lang"), "ForgeOrdnance/lang/")):
        for language in ("en.json", "zh.json"):
            files[target + language] = os.path.join(PACKAGE, folder, language)
    # The cockpit art (tools/import_hud_art.ps1), every file at 1x and @2x: the plugin draws nothing in place of a missing one.
    art = os.path.join(os.path.dirname(ICONS), "hud")
    shipped = sorted(name for name in os.listdir(art) if name.endswith(".png")) if os.path.isdir(art) else []
    for name in shipped:
        if not re.fullmatch(r"((skill|mark|damage)-[a-z0-9-]+|shield|heat|objective|teammate|arrow-around)(@2x)?\.png|visor-cracks\.png", name):
            fail("unexpected cockpit art " + name)
        files["hud/" + name] = os.path.join(art, name)
    # The visor's crack drawing is one file (no @2x): the visor reads it as a plain picture.
    missing = sorted(set(HUD_ART + [name[:-4] + "@2x.png" for name in HUD_ART] + ["visor-cracks.png"]) - set(shipped))
    if missing:
        fail("cockpit art missing from Assets/hud/ (run tools/import_hud_art.ps1): " + ", ".join(missing))
    for source in files.values():
        if not os.path.isfile(source):
            fail("missing " + source)
    os.makedirs(OUT, exist_ok=True)
    name = TEAM + "-" + manifest["name"] + "-" + manifest["version_number"] + ".zip"
    path = os.path.join(OUT, name)
    descriptor, temporary = tempfile.mkstemp(prefix=name + ".", suffix=".tmp", dir=OUT)
    os.close(descriptor)
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as archive:
            for entry, source in sorted(files.items()):
                info = zipfile.ZipInfo(entry, date_time=(2026, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                with open(source, "rb") as data:
                    archive.writestr(info, data.read())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    digest = sha256(path)
    print(name + ": " + str(os.path.getsize(path)) + " bytes, sha256 " + digest + ", " + str(len(files)) + " files")


if __name__ == "__main__":
    main()
