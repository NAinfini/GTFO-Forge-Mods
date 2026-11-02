"""Build the ForgeVisual Thunderstore zip from the Release build output; never publishes.

    python Mods/ForgeVisual/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>

Then check the whole release set with Mods/Build/verify_release.py.
"""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "Build"))
import package_common

if __name__ == "__main__":
    raise SystemExit(package_common.main("ForgeVisual", ("ForgeVisual.dll",)))
