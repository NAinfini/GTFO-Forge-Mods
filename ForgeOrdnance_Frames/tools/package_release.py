"""Builds the Thunderstore zip of ForgeOrdnance_Frames into artifacts/work/ordnance-release/. Nothing is published.

Run from the repository root after the Release build, the art build and the icon build:
    dotnet build Mods/ForgeOrdnance_Frames/ForgeOrdnanceFrames.csproj -c Release --artifacts-path artifacts/dotnet ...
    pwsh Mods/ForgeOrdnance_Frames/tools/build_assets.ps1
    pwsh Mods/ForgeOrdnance_Frames/tools/build_icons.ps1
    python Mods/ForgeOrdnance_Frames/tools/package_release.py

The zip holds manifest.json, icon.png, README.md, CHANGELOG.md, ForgeOrdnanceFrames.dll and forge-frames.bundle at its root,
the models' attribution (Assets/Models/LICENSE.forge) as models/LICENSE.forge, the ForgeAudio audio folder (cues.forge,
licence, Ogg files) under audio/, the words (lang/), the cockpit art (Assets/hud/ as hud/, all of it), and the definitions
(Definitions/frames.forge) with their icons and words in ForgeOrdnance/ (r2modman installs it all into
BepInEx/plugins/NAinfini-ForgeOrdnance_Frames/, where ForgeOrdnance finds the folder). Fixed timestamps: the same inputs give
the same bytes. The manifest is checked against Thunderstore's rules first.
"""
import argparse
from pathlib import Path
import hashlib
import json
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Build"))
import package_common  # noqa: E402
from package_common import media_file, media_names, media_path, vfx_files  # noqa: E402  (one per-file media rule for every package)

REPO = str(package_common.REPO)
PACKAGE = os.path.join(REPO, "Mods", "ForgeOrdnance_Frames")
# Local media (cockpit art, icons, Ogg files) is read per file: this checkout first, else the main checkout (package_common.media_file).
OUT = os.path.join(REPO, "artifacts", "work", "ordnance-release")
DLL = os.path.join(REPO, "artifacts", "dotnet", "bin", "ForgeOrdnanceFrames", "release", "ForgeOrdnanceFrames.dll")
BUNDLE = str(media_path("ForgeOrdnance_Frames/Assets/Models/forge-frames.bundle", REPO))
RECEIPT = str(media_path("ForgeOrdnance_Frames/Assets/Models/forge-frames.receipt.json", REPO))
ICONS = os.path.join(PACKAGE, "Assets", "icons")
AUDIO = os.path.join(PACKAGE, "Assets", "Audio")
MODELS_LICENCE = os.path.join(PACKAGE, "Assets", "Models", "LICENSE.forge")
# The cockpit art the plugin loads (Native/FrameHudArt.cs), each also as name@2x.png.
HUD_ART = [name + ".png" for name in (
    "skill-charge", "skill-siege", "skill-scan", "skill-rockets", "skill-lamp", "skill-exit", "skill-carry",
    "shield", "heat", "objective", "teammate", "arrow-around",
    "mark-frame-m15", "mark-frame-m16", "mark-frame-m17", "mark-frame-m18",
    "damage-front", "damage-back", "damage-left", "damage-right")]


def fail(message):
    sys.exit("package_release: " + message)


sha256 = package_common.sha256_file


def text_sha256(path):
    """A tracked text input's hash as Git stores it (CRLF read as LF): a Windows checkout and the repository give the same script one hash."""
    with open(path, "rb") as source:
        return hashlib.sha256(source.read().replace(b"\r\n", b"\n")).hexdigest()


def check_plugin_version():
    """The plugin's own Version must be the manifest's (package_common.package_files checks the project's)."""
    with open(os.path.join(PACKAGE, "manifest.json"), encoding="utf-8") as source:
        version = json.load(source)["version_number"]
    with open(os.path.join(PACKAGE, "Native", "Plugin.cs"), encoding="utf-8") as source:
        plugin = re.search(r'internal const string Version = "([^"]+)"', source.read())
    if plugin is None or plugin.group(1) != version:
        fail("Native/Plugin.cs Version " + (plugin.group(1) if plugin else "is missing") + " differs from manifest version " + version)


def check_bundle(definitions):
    if not os.path.isfile(RECEIPT):
        fail("missing bundle receipt (rebuild with tools/build_assets.ps1)")
    with open(RECEIPT, encoding="utf-8") as source:
        receipt = json.load(source)
    expected = {entry["Payload"]["Model"].split(":", 1)[1] for entry in definitions}
    if receipt.get("format") != 3 or set(receipt.get("models", [])) != expected:
        fail("bundle receipt does not cover the shipped definitions")
    if not os.path.isfile(BUNDLE) or receipt.get("bundleBytes") != os.path.getsize(BUNDLE) or receipt.get("bundleSha256") != sha256(BUNDLE):
        fail("bundle does not match its verified build receipt")
    inputs = receipt.get("inputs", {})
    required = {"Mods/ForgeOrdnance_Frames/Definitions/frames.forge"}
    required.update("Mods/ForgeOrdnance_Frames/tools/" + name for name in os.listdir(os.path.join(PACKAGE, "tools"))
                    if name.endswith((".cs", ".ps1", ".py")) and not name.startswith("package_"))
    required.update("Tools/Blender/hinge_tool/" + name for name in os.listdir(os.path.join(REPO, "Tools", "Blender", "hinge_tool"))
                    if name.endswith((".ps1", ".py")))
    sources = receipt.get("sources", {})
    roots = receipt.get("roots", {})
    if not required.issubset(inputs) or not any(name.startswith("cut/") for name in sources) or set(roots) != {"cut", "pilot", "props"}:
        fail("bundle receipt lacks source hashes")
    for name, digest in inputs.items():
        path = os.path.join(REPO, name)
        if not os.path.isfile(path) or text_sha256(path) != digest:
            fail("bundle build input changed: " + name + " (rebuild assets)")
    for name, digest in sources.items():
        root, _, relative = name.partition("/")
        path = os.path.join(roots.get(root, ""), relative)
        if not os.path.isfile(path) or sha256(path) != digest:
            fail("bundle build input changed: " + name + " (rebuild assets)")


def main():
    global DLL, OUT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts-path", default="artifacts/dotnet", help="dotnet build artifacts root")
    parser.add_argument("--output", help="folder that receives the zip")
    args = parser.parse_args()
    DLL = os.path.join(REPO, args.artifacts_path, "bin", "ForgeOrdnanceFrames", "release", "ForgeOrdnanceFrames.dll")
    OUT = os.path.abspath(args.output) if args.output else OUT
    if not os.path.isdir(PACKAGE):
        fail("run from the repository root")
    check_plugin_version()
    files = {name: os.path.join(PACKAGE, name) for name in ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE")}
    files["ForgeOrdnanceFrames.dll"] = DLL
    files["forge-frames.bundle"] = BUNDLE
    files.update(vfx_files("ForgeOrdnance_Frames", "frames", REPO))
    if not os.path.isfile(MODELS_LICENCE):
        fail("missing the models' attribution (Assets/Models/LICENSE.forge)")
    files["models/LICENSE.forge"] = MODELS_LICENCE
    try:
        files.update({entry: str(path) for entry, path in package_common.media_audio_files(AUDIO, ("LICENSE.forge",)).items()})
    except ValueError as error:
        fail(str(error) + " (run tools/prepare_audio.py)")
    definitions = os.path.join(PACKAGE, "Definitions", "frames.forge")
    files["ForgeOrdnance/frames.forge"] = definitions
    with open(definitions, encoding="utf-8") as source:
        entries = json.load(source)
    check_bundle(entries)
    for entry in entries:
        icon = entry.get("Icon")
        if not icon:
            fail(entry["Id"] + " has no Icon")
        source = str(media_file(os.path.join(ICONS, icon)))
        if not os.path.isfile(source):
            fail("missing icon " + source + " (run tools/build_icons.ps1)")
        if package_common.png_size(source) != (256, 256):
            fail(source + " is not 256 x 256")
        files["ForgeOrdnance/" + icon] = source
    # The words: Frames' own (lang/) and the definitions' names and descriptions (ForgeOrdnance/lang/, where ForgeOrdnance reads them).
    try:
        files.update({entry: str(path) for entry, path in package_common.lang_files(Path(PACKAGE)).items()})
        files.update({entry: str(path) for entry, path in package_common.lang_files(Path(PACKAGE) / "Definitions", "ForgeOrdnance/lang").items()})
    except ValueError as error:
        fail(str(error))
    # The cockpit art (tools/import_hud_art.ps1), every file at 1x and @2x: the plugin draws nothing in place of a missing one.
    art = os.path.join(os.path.dirname(ICONS), "hud")
    shipped = sorted(name for name in media_names(art) if name.endswith(".png"))
    for name in shipped:
        if not re.fullmatch(r"((skill|mark|damage)-[a-z0-9-]+|shield|heat|objective|teammate|arrow-around)(@2x)?\.png|visor-cracks\.png", name):
            fail("unexpected cockpit art " + name)
        files["hud/" + name] = str(media_file(os.path.join(art, name)))
    # The visor's crack drawing is one file (no @2x): the visor reads it as a plain picture.
    missing = sorted(set(HUD_ART + [name[:-4] + "@2x.png" for name in HUD_ART] + ["visor-cracks.png"]) - set(shipped))
    if missing:
        fail("cockpit art missing from Assets/hud/ (run tools/import_hud_art.ps1): " + ", ".join(missing))
    try:
        package_common.package_files(Path(PACKAGE), "ForgeOrdnance_Frames", files, Path(OUT))
    except (OSError, ValueError) as error:
        fail(str(error))


if __name__ == "__main__":
    main()
