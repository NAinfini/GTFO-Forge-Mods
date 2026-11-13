"""Builds the Thunderstore zip of ForgeOrdnance. Nothing is published.

Run from the repository root after the Release build and the three parts' art, audio and icon builds:
    dotnet build Mods/ForgeOrdnance/ForgeOrdnance.csproj -c Release --artifacts-path artifacts/dotnet ...
    pwsh Mods/ForgeOrdnance/tools/Carrier/build_assets.ps1
    pwsh Mods/ForgeOrdnance/tools/Carrier/build_audio.ps1
    pwsh Mods/ForgeOrdnance/tools/Arsenal/build_assets.ps1
    pwsh Mods/ForgeOrdnance/tools/Arsenal/build_audio.ps1
    pwsh Mods/ForgeOrdnance/tools/Arsenal/build_icons.ps1
    pwsh Mods/ForgeOrdnance/tools/Frames/build_assets.ps1
    pwsh Mods/ForgeOrdnance/tools/Frames/build_icons.ps1
    pwsh Mods/ForgeOrdnance/tools/Frames/import_hud_art.ps1
    python Mods/ForgeOrdnance/tools/Frames/import_visor_cracks.py
    python Mods/ForgeOrdnance/tools/Frames/prepare_audio.py
    python Mods/ForgeOrdnance/tools/package_release.py --artifacts-path artifacts/dotnet --output artifacts/work/ordnance-release

The zip holds ForgeOrdnance.dll, the package metadata and the plugin's words (lang/) at its root, and one folder per part
(Carrier/, Arsenal/, Frames/) with the part's bundle, the models' attribution (models/LICENSE.forge), its ForgeAudio folder
(audio/) and its effect library. Arsenal and Frames also carry their definitions with the words and icons in <part>/ForgeOrdnance/
(the folder name the carrier finds definitions by), and Frames its cockpit art (hud/). r2modman installs it all into
BepInEx/plugins/NAinfini-ForgeOrdnance/. Fixed timestamps: the same inputs give the same bytes. The manifest is checked against
Thunderstore's rules and every bundle against its build receipt before packaging.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Build"))
import package_common  # noqa: E402

NAME = "ForgeOrdnance"
METADATA = ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE")

POD_PARTS = {"body", "cradle", "door_1", "door_2", "door_3", "door_4", "interior", "shelf_1", "shelf_2"}
# Surfaces of the older pods the current one replaced; the bundle must never carry them again.
REMOVED_POD_SURFACES = ("Container", "Cage", "ArsenalPod_", "M02_Pod", "Pod_InnerSteel")
# Every prefab the plugin loads by name; a bundle built before one existed is stale.
# Carrier: OrdnanceAssets, PodImpactEffects, OrdnanceFx.
CARRIER_PREFABS = ("beacon", "drop-pod", "turret-shell", "pod-impact", "ordnance-fx")
# Arsenal: ArsenalArt (the masters and models of tools/Arsenal/build_assets.ps1).
ARSENAL_PREFABS = (
    "shell", "shell-precision", "shell-hunter", "shell-incendiary", "bomblet", "rocket", "grenade-40", "gas-canister", "stasis-emitter",
    "mine", "glue-mine", "recoilless", "turret-burst", "turret-sniper", "turret-shotgun", "dispenser", "radar", "floodlight",
    "heavy-mg", "tracking-rifle", "anti-materiel-rifle", "grenade-launcher",
)
# The cockpit art the plugin loads (Frames/Native/FrameHudArt.cs), each also as name@2x.png.
HUD_ART = [name + ".png" for name in (
    "skill-charge", "skill-siege", "skill-scan", "skill-rockets", "skill-lamp", "skill-exit", "skill-carry",
    "shield", "heat", "objective", "teammate", "arrow-around",
    "mark-frame-m15", "mark-frame-m16", "mark-frame-m17", "mark-frame-m18",
    "damage-front", "damage-back", "damage-left", "damage-right")]

sha256 = package_common.sha256_file


def media(folder, *parts):
    """A local media file under the package's Assets/ (bundle, receipt, icon): this checkout's file, else the main checkout's."""
    return package_common.media_path("/".join((folder.name, "Assets") + parts), folder.parents[1])


def text_sha256(path):
    """A tracked text input's hash as Git stores it (CRLF read as LF): a Windows checkout and the repository give the same script one hash."""
    with open(path, "rb") as source:
        return hashlib.sha256(source.read().replace(b"\r\n", b"\n")).hexdigest()


def check_plugin_version(folder):
    """The plugin's own Version must be the manifest's (package_common.package_files checks the release table's)."""
    version = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))["version_number"]
    plugin = re.search(r'internal const string Version = "([^"]+)"', (folder / "Plugin.cs").read_text(encoding="utf-8"))
    if plugin is None or plugin.group(1) != version:
        raise ValueError("Plugin.cs Version " + (plugin.group(1) if plugin else "is missing") + " differs from manifest version " + version)


def check_pod(profile):
    """The bundle holds exactly one drop pod, never a second look or an older pod."""
    pods = [prefab for prefab in profile.get("prefabs", []) if "pod" in prefab["name"].lower() and prefab["name"] != "pod-impact"]
    if [prefab["name"] for prefab in pods] != ["drop-pod"]:
        raise ValueError("the bundle must hold exactly one drop pod prefab named drop-pod, found " + ", ".join(prefab["name"] for prefab in pods))
    parts = {renderer["name"] for renderer in pods[0]["renderers"]}
    if not POD_PARTS <= parts or "light_strip" in parts:
        raise ValueError("drop-pod parts are wrong: missing " + ", ".join(sorted(POD_PARTS - parts)) + (", obsolete light_strip" if "light_strip" in parts else ""))
    surfaces = {surface.split("/", 1)[1] for renderer in pods[0]["renderers"] for surface in renderer["surfaces"]}
    removed = sorted(name for name in surfaces if name.startswith(REMOVED_POD_SURFACES))
    if removed:
        raise ValueError("drop-pod uses surfaces of an older pod: " + ", ".join(removed))


def check_prefabs(profile, required, part):
    names = {prefab["name"] for prefab in profile.get("prefabs", [])}
    missing = [name for name in required if name not in names]
    if missing:
        raise ValueError("the " + part + " bundle lacks the prefabs " + ", ".join(missing) + " the plugin loads by name; rebuild it with tools/" + part + "/build_assets.ps1")


def bundle_profile(bundle, receipt):
    """The profile of a bundle OrdnanceBundleBuild made (Carrier, Arsenal), once the bundle is the file its Unity build receipt names."""
    for path in (bundle, receipt):
        if not path.is_file():
            raise ValueError("missing " + str(path) + " (run the part's tools/build_assets.ps1 successfully)")
    built = json.loads(receipt.read_text(encoding="utf-8"))
    if built.get("bundle") != bundle.name or built.get("bytes") != bundle.stat().st_size or built.get("sha256") != sha256(bundle):
        raise ValueError("bundle does not match its Unity build receipt: " + str(receipt))
    return built.get("profile", {})


def check_build_log(bundle, build_log):
    """The Unity log kept beside the bundle must hold the marker of the run that verified exactly this file."""
    if not build_log.is_file():
        raise ValueError("missing " + str(build_log))
    marker = "ORDNANCE_BUNDLE_VERIFIED " + bundle.name + " " + str(bundle.stat().st_size) + " bytes " + sha256(bundle)
    if marker not in build_log.read_text(encoding="utf-8", errors="replace"):
        raise ValueError("bundle has no matching successful-build marker in " + str(build_log))


def check_frames_bundle(folder, bundle, receipt, definitions):
    """The Frames bundle is the file tools/Frames/build_assets.ps1 verified, built from the definitions, tools and model sources as they are now."""
    repo = folder.parents[1]
    if not receipt.is_file():
        raise ValueError("missing bundle receipt (rebuild with tools/Frames/build_assets.ps1)")
    built = json.loads(receipt.read_text(encoding="utf-8"))
    expected = {entry["Payload"]["Model"].split(":", 1)[1] for entry in definitions}
    if built.get("format") != 3 or set(built.get("models", [])) != expected:
        raise ValueError("bundle receipt does not cover the shipped definitions")
    if not bundle.is_file() or built.get("bundleBytes") != bundle.stat().st_size or built.get("bundleSha256") != sha256(bundle):
        raise ValueError("bundle does not match its verified build receipt")
    inputs = built.get("inputs", {})
    required = {"Mods/" + NAME + "/Frames/Definitions/frames.forge"}
    required.update("Mods/" + NAME + "/tools/Frames/" + name for name in os.listdir(folder / "tools" / "Frames") if name.endswith((".cs", ".ps1", ".py")))
    required.update("Tools/Blender/hinge_tool/" + name for name in os.listdir(repo / "Tools" / "Blender" / "hinge_tool") if name.endswith((".ps1", ".py")))
    sources = built.get("sources", {})
    roots = built.get("roots", {})
    if not required.issubset(inputs) or not any(name.startswith("cut/") for name in sources) or set(roots) != {"cut", "pilot", "props"}:
        raise ValueError("bundle receipt lacks source hashes")
    for name, digest in inputs.items():
        path = repo / name
        if not path.is_file() or text_sha256(path) != digest:
            raise ValueError("bundle build input changed: " + name + " (rebuild assets)")
    for name, digest in sources.items():
        root, _, relative = name.partition("/")
        path = os.path.join(roots.get(root, ""), relative)
        if not os.path.isfile(path) or sha256(path) != digest:
            raise ValueError("bundle build input changed: " + name + " (rebuild assets)")


def audio_files(folder, part):
    """The part's ForgeAudio folder: cues.forge, exactly the Ogg clips it names, and the licence record. The source record
    (sources.forge) is a build input the plugin never reads: it stays in the repository."""
    return package_common.media_audio_files(folder / "Assets" / part / "Audio", ("LICENSE.forge",), folder.parents[1])


def definition_files(folder, part, name):
    """<part>/Definitions/<name> with its words and the 256 x 256 icon of every entry, as the ForgeOrdnance/ folder the carrier reads; and the entries."""
    source = folder / part / "Definitions" / name
    if not source.is_file():
        raise ValueError("missing " + str(source))
    files = {"ForgeOrdnance/" + name: source}
    files.update(package_common.lang_files(folder / part / "Definitions", "ForgeOrdnance/lang"))
    entries = json.loads(source.read_text(encoding="utf-8"))
    for entry in entries:
        icon = entry.get("Icon")
        if not icon:
            raise ValueError(entry["Id"] + " has no Icon")
        picture = media(folder, part, "icons", icon)
        if not picture.is_file():
            raise ValueError("missing icon " + str(picture) + " (run tools/" + part + "/build_icons.ps1)")
        if package_common.png_size(picture) != (256, 256):
            raise ValueError(str(picture) + " is not 256 x 256")
        files["ForgeOrdnance/" + icon] = picture
    return files, entries


def carrier_files(folder):
    # The verified bundle, its Unity receipt and build log stay in the package (tools/Carrier/build_assets.ps1 copies them there).
    bundle = media(folder, "Carrier", "Models", "forge-ordnance.bundle")
    profile = bundle_profile(bundle, media(folder, "Carrier", "Models", "forge-ordnance.manifest.json"))
    check_pod(profile)
    check_prefabs(profile, CARRIER_PREFABS, "Carrier")
    check_build_log(bundle, media(folder, "Carrier", "Models", "unity-build.log"))
    files = {"forge-ordnance.bundle": bundle, "models/LICENSE.forge": folder / "Assets/Carrier/Models/LICENSE.forge"}
    files.update(audio_files(folder, "Carrier"))
    files.update(package_common.vfx_files(NAME, "ordnance", folder.parents[1], "Assets/Carrier/Vfx"))
    return files


def arsenal_files(folder):
    bundle = media(folder, "Arsenal", "Models", "forge-ordnance-arsenal.bundle")
    check_prefabs(bundle_profile(bundle, media(folder, "Arsenal", "Models", "forge-ordnance-arsenal.manifest.json")), ARSENAL_PREFABS, "Arsenal")
    files = {"forge-ordnance-arsenal.bundle": bundle, "models/LICENSE.forge": folder / "Assets/Arsenal/Models/LICENSE.forge"}
    files.update(audio_files(folder, "Arsenal"))
    files.update(package_common.vfx_files(NAME, "arsenal", folder.parents[1], "Assets/Arsenal/Vfx"))
    files.update(definition_files(folder, "Arsenal", "arsenal.forge")[0])
    return files


def frames_files(folder):
    bundle = media(folder, "Frames", "Models", "forge-frames.bundle")
    files = {"forge-frames.bundle": bundle, "models/LICENSE.forge": folder / "Assets/Frames/Models/LICENSE.forge"}
    files.update(audio_files(folder, "Frames"))
    files.update(package_common.vfx_files(NAME, "frames", folder.parents[1], "Assets/Frames/Vfx"))
    definitions, entries = definition_files(folder, "Frames", "frames.forge")
    check_frames_bundle(folder, bundle, media(folder, "Frames", "Models", "forge-frames.receipt.json"), entries)
    files.update(definitions)
    # The cockpit art (tools/Frames/import_hud_art.ps1), every file at 1x and @2x: the plugin draws nothing in place of a missing one.
    art = folder / "Assets" / "Frames" / "hud"
    shipped = sorted(name for name in package_common.media_names(art, folder.parents[1]) if name.endswith(".png"))
    for name in shipped:
        if not re.fullmatch(r"((skill|mark|damage)-[a-z0-9-]+|shield|heat|objective|teammate|arrow-around)(@2x)?\.png|visor-cracks\.png", name):
            raise ValueError("unexpected cockpit art " + name)
        files["hud/" + name] = package_common.media_file(art / name, folder.parents[1])
    # The visor's crack drawing is one file (no @2x): the visor reads it as a plain picture.
    missing = sorted(set(HUD_ART + [name[:-4] + "@2x.png" for name in HUD_ART] + ["visor-cracks.png"]) - set(shipped))
    if missing:
        raise ValueError("cockpit art missing from Assets/Frames/hud/ (run tools/Frames/import_hud_art.ps1): " + ", ".join(missing))
    return files


def extra(folder, built):
    """Everything beyond the DLL and the metadata: the plugin's words, then each part's own entries under <part>/."""
    check_plugin_version(folder)
    files = dict(package_common.lang_files(folder))
    for part, table in (("Carrier", carrier_files(folder)), ("Arsenal", arsenal_files(folder)), ("Frames", frames_files(folder))):
        files.update({part + "/" + entry: path for entry, path in table.items()})
    return files


if __name__ == "__main__":
    raise SystemExit(package_common.main(NAME, (NAME + ".dll",), extra, METADATA))
