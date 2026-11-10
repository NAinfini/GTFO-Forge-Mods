"""Build the ForgeUI Thunderstore zip from the Release build output; never publishes.

    python Mods/ForgeUI/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>

The fonts come from tools/fonts/build_fonts.ps1. Then check the whole release set with Mods/Build/verify_release.py.
"""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "Build"))
import package_common

FONTS = ("ForgeUI-fonts.bundle", "OFL-Rajdhani.forge", "OFL-GlowSans.forge")


def extra(folder: Path, built: Path) -> dict[str, Path]:
    return {**package_common.lang_files(folder), **{"fonts/" + name: package_common.media_file(folder / "Assets" / "fonts" / name) for name in FONTS}}


if __name__ == "__main__":
    raise SystemExit(package_common.main("ForgeUI", ("ForgeUI.dll",), extra))
