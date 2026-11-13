"""Build the ForgeReplay Thunderstore zip from the Release build output; never publishes and never installs.

    python Mods/ForgeReplay/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>

Checks the manifest's dependencies (BepInExPack_GTFO 3.2.4, GTFO_API 0.6.1 and ForgeCore at the version its own manifest
declares), that the manifest and release.json versions agree and that lang/en.forge and lang/zh.forge hold the same keys,
then writes NAinfini-ForgeReplay-<version>.zip. Mods/Build/verify_release.py, run on a folder holding this zip and the
ForgeCore zip, checks the DLL against the build and its assembly version against the manifest.
"""
from pathlib import Path
import json
import sys

MODS = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(MODS / "Build"))
import package_common

NAME = "ForgeReplay"
FOLDER = MODS / NAME
EXTERNAL = ("BepInEx-BepInExPack_GTFO-3.2.4", "GTFOModding-GTFO_API-0.6.1")
LIBRARIES = ("ForgeCore",)
LANGUAGES = ("en", "zh")
METADATA = ("rating.forge", "manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE") + tuple("lang/" + code + ".forge" for code in LANGUAGES)


def expected_dependencies() -> list[str]:
    libraries = []
    for library in LIBRARIES:
        manifest = json.loads((MODS / library / "manifest.json").read_text(encoding="utf-8"))
        libraries.append(package_common.TEAM + "-" + library + "-" + manifest["version_number"])
    return list(EXTERNAL) + libraries


def check() -> None:
    manifest = json.loads((FOLDER / "manifest.json").read_text(encoding="utf-8"))
    expected = expected_dependencies()
    if manifest.get("dependencies") != expected:
        raise ValueError("manifest dependencies must be exactly " + ", ".join(expected) + "; found " + str(manifest.get("dependencies")))
    table = package_common.release_version(MODS, NAME)
    if table != manifest["version_number"]:
        raise ValueError("release.json version " + str(table) + " differs from manifest " + manifest["version_number"])
    keys = {}
    for code in LANGUAGES:
        texts = json.loads((FOLDER / "lang" / (code + ".forge")).read_text(encoding="utf-8"))
        if not isinstance(texts, dict) or not texts or any(not isinstance(value, str) for value in texts.values()):
            raise ValueError("lang/" + code + ".forge must be a non-empty object of key to text")
        keys[code] = set(texts)
    missing = (keys["en"] - keys["zh"]) | (keys["zh"] - keys["en"])
    if missing:
        raise ValueError("lang/en.forge and lang/zh.forge differ in keys: " + ", ".join(sorted(missing)))


def main() -> int:
    parser = package_common.argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--artifacts-path", type=Path, required=True, help="the dotnet --artifacts-path of the Release build")
    parser.add_argument("--output", type=Path, required=True, help="folder that receives the zip")
    args = parser.parse_args()
    artifacts = args.artifacts_path if args.artifacts_path.is_absolute() else Path.cwd() / args.artifacts_path
    output = args.output if args.output.is_absolute() else Path.cwd() / args.output
    try:
        check()
        package_common.package_library(FOLDER, NAME, (NAME + ".dll",), artifacts, output, METADATA)
        return 0
    except (OSError, ValueError, KeyError) as error:
        print("package_release: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
