"""Build the ForgeEmote Thunderstore zip from the Release build output; never publishes.

    python Mods/ForgeEmote/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>

The build copies the shipped definition file, its lang folder, the clips and the icons next to the DLL
(the clips and icons are local media); the zip carries that folder as it is. Then check the whole release set with Mods/Build/verify_release.py.
"""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "Build"))
import package_common


METADATA = ("manifest.json", "icon.png", "README.md", "DEVELOPERS.md", "CHANGELOG.md", "LICENSE", "NOTICE")


def extra(folder: Path, built: Path) -> dict[str, Path]:
    content = {"ForgeEmote/" + path.relative_to(built / "ForgeEmote").as_posix(): path for path in sorted((built / "ForgeEmote").rglob("*")) if path.is_file()}
    if "ForgeEmote/emotes.emote.forge" not in content or not any(name.startswith("ForgeEmote/Clips/") for name in content):
        raise ValueError(str(built / "ForgeEmote") + " lacks the definition file or the clips; run the Release build with the local media")
    return {**package_common.lang_files(folder), **content}


if __name__ == "__main__":
    raise SystemExit(package_common.main("ForgeEmote", ("ForgeEmote.dll",), extra, METADATA))
