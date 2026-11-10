"""Build the ForgeAudio Thunderstore zip from the Release build output; never publishes.

    python Mods/ForgeAudio/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>

Then check the whole release set with Mods/Build/verify_release.py.
"""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "Build"))
import package_common


def extra(folder: Path, built: Path) -> dict[str, Path]:
    vendor = folder / "vendor" / "NVorbis"
    return {"NVorbis.dll": built / "NVorbis.dll", "NVorbis.LICENSE": vendor / "LICENSE", "NVorbis.NOTICE.md": vendor / "NOTICE.md"}


if __name__ == "__main__":
    raise SystemExit(package_common.main("ForgeAudio", ("ForgeAudio.dll",), extra))
