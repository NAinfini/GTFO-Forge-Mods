"""Turn the delivered visor crack drawing into Assets/hud/visor-cracks.png (white lines on transparency); never edits the source.

The drawing (white lines on black) is <review>/assets/U3-裂纹.webp; its brightness becomes the alpha. Assets/hud/*.png stays out of Git;
package_release.py ships it in hud/ and refuses to build without it. Needs Pillow. Run from any directory:
    python Mods/ForgeOrdnance_Frames/tools/import_visor_cracks.py [--from <review folder>]
"""
import argparse
from pathlib import Path
import subprocess
import sys

from PIL import Image

PACKAGE = Path(__file__).resolve().parents[1]
OUT = PACKAGE / "Assets/hud/visor-cracks.png"
SOURCE = "assets/U3-裂纹.webp"


def main_checkout():
    common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=PACKAGE,
                            capture_output=True, text=True, check=True).stdout.strip()
    return Path(common).parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--from", dest="source", type=Path, default=None)
    args = parser.parse_args()
    path = (args.source or main_checkout() / "Source/mods/frames-ui-review") / SOURCE
    if not path.is_file():
        sys.exit("import_visor_cracks: " + str(path) + " is not delivered; packaging refuses to build without the cracks")
    with Image.open(path) as image:
        alpha = image.convert("L")
    white = Image.new("RGBA", alpha.size, (255, 255, 255, 0))
    white.putalpha(alpha)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    white.save(OUT, optimize=True)
    print("Wrote " + str(OUT) + ".")


if __name__ == "__main__":
    main()
