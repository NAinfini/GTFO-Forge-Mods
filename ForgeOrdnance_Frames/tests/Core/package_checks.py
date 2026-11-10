"""Offline packaging regressions; synthetic bytes never claim to be a Unity bundle."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("frames_package", Path(__file__).parents[2] / "tools/package_release.py")
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)


class PackageChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        paths = {
            "REPO": self.root, "PACKAGE": self.root / "Mods/ForgeOrdnance_Frames",
            "OUT": self.root / "release",
            "DLL": self.root / "build/bin/ForgeOrdnanceFrames/release/ForgeOrdnanceFrames.dll",
        }
        self.build = self.root / "assets/frames-build"
        paths.update(BUNDLE=paths["PACKAGE"] / "Assets/Models/forge-frames.bundle",
                     RECEIPT=paths["PACKAGE"] / "Assets/Models/forge-frames.receipt.json", ICONS=paths["PACKAGE"] / "Assets/icons",
                     AUDIO=paths["PACKAGE"] / "Assets/Audio", MODELS_LICENCE=paths["PACKAGE"] / "Assets/Models/LICENSE.forge")
        self.patch = patch.multiple(package, **{key: str(value) for key, value in paths.items()})
        self.patch.start()
        self.addCleanup(self.patch.stop)
        manifest = {"name": "ForgeOrdnance_Frames", "version_number": "0.0.1", "website_url": "https://example.org",
                    "description": "Frames", "dependencies": ["NAinfini-ForgeAudio-0.0.1"]}
        self.write(paths["PACKAGE"] / "manifest.json", json.dumps(manifest))
        self.write(paths["PACKAGE"] / "Native/Plugin.cs", 'internal const string Version = "0.0.1";')
        png = b"\x89PNG\r\n\x1a\n" + b"\x00\x00\x00\rIHDR" + struct.pack(">II", 256, 256) + b"\x08\x06\x00\x00\x00" + bytes(4)
        for name in ("icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE"):
            self.write(paths["PACKAGE"] / name, png if name.endswith(".png") else name)
        self.definitions = [{"Id": "frame" + str(i), "Icon": str(i) + ".png", "Payload": {"Model": "Frames:frame-m" + str(i)}}
                            for i in range(15, 19)]
        self.write(paths["PACKAGE"] / "Definitions/frames.forge", json.dumps(self.definitions))
        for entry in self.definitions:
            self.write(paths["ICONS"] / entry["Icon"], png)
        self.write(paths["DLL"], b"synthetic dll")
        self.write(paths["BUNDLE"], b"synthetic bundle")
        self.write(paths["AUDIO"] / "exo.test-a.ogg", b"synthetic ogg")
        self.write(paths["AUDIO"] / "cues.forge", json.dumps({"buses": {}, "cues": {"exo.test": {"layers": [{"clips": ["exo.test-a.ogg"]}]}}}))
        self.write(paths["AUDIO"] / "LICENSE.forge", "audio licence")
        self.write(paths["MODELS_LICENCE"], "models licence")
        # The cockpit art the plugin loads, every picture at 1x and @2x, as tools/import_hud_art.ps1 leaves it.
        for name in package.HUD_ART:
            for art in (name, name[:-4] + "@2x.png"):
                self.write(paths["PACKAGE"] / "Assets/hud" / art, png)
        # The visor's crack drawing, one file (tools/import_visor_cracks.py).
        self.write(paths["PACKAGE"] / "Assets/hud/visor-cracks.png", png)
        for folder in ("lang", "Definitions/lang"):
            for language in ("en.forge", "zh.forge"):
                self.write(paths["PACKAGE"] / folder / language, "{}")
        self.write(paths["PACKAGE"] / "tools/build_assets.ps1", "build source")
        self.write(self.root / "cut/M15_cut.draft.blend", b"cut source")
        self.write(paths["PACKAGE"] / "tools/props.json", "{}")
        # the Blender add-on the build bakes with (Tools/Blender/hinge_tool) is an input too
        self.write(self.root / "Tools/Blender/hinge_tool/bake.py", "bake source")
        self.inputs = [str(path.relative_to(self.root)).replace("\\", "/") for path in (
            paths["PACKAGE"] / "Definitions/frames.forge", paths["PACKAGE"] / "tools/build_assets.ps1",
            paths["PACKAGE"] / "tools/props.json", self.root / "Tools/Blender/hinge_tool/bake.py")]
        self.receipt = {"format": 3, "bundleBytes": paths["BUNDLE"].stat().st_size,
                        "bundleSha256": package.sha256(paths["BUNDLE"]),
                        "models": [entry["Payload"]["Model"].split(":", 1)[1] for entry in self.definitions],
                        "roots": {"cut": str(self.root / "cut"), "pilot": str(self.root / "pilot"), "props": str(self.root / "props")},
                        "inputs": {name: package.text_sha256(self.root / name) for name in self.inputs},
                        "sources": {"cut/M15_cut.draft.blend": package.sha256(self.root / "cut/M15_cut.draft.blend")}}
        self.save_receipt()
        self.output = paths["OUT"] / "NAinfini-ForgeOrdnance_Frames-0.0.1.zip"
        self.write(self.output, b"previous release")

    @staticmethod
    def write(path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value.encode() if isinstance(value, str) else value)

    def save_receipt(self):
        self.write(Path(package.RECEIPT), json.dumps(self.receipt))

    def run_package(self):
        with contextlib.redirect_stdout(io.StringIO()), patch.object(sys, "argv", ["package_release.py", "--artifacts-path", "build", "--output", package.OUT]):
            package.main()

    def assert_preserved(self):
        self.assertEqual(self.output.read_bytes(), b"previous release")
        self.assertEqual(list(self.output.parent.glob("*.tmp")), [])

    def test_missing_receipt(self):
        Path(package.RECEIPT).unlink()
        with self.assertRaisesRegex(SystemExit, "missing bundle receipt"):
            self.run_package()
        self.assert_preserved()




    def test_replaced_bundle(self):
        Path(package.BUNDLE).write_bytes(b"unverified replacement")
        with self.assertRaisesRegex(SystemExit, "verified build receipt"):
            self.run_package()
        self.assert_preserved()

    def test_changed_source(self):
        # the same script checked out with CRLF is the same input; one changed line is not
        script = self.root / self.inputs[1]
        script.write_bytes(b"build source\r\n")
        self.receipt["inputs"][self.inputs[1]] = package.text_sha256(script)
        self.save_receipt()
        script.write_bytes(b"build source\n")
        self.run_package()
        self.write(self.output, b"previous release")
        script.write_bytes(b"build source changed\r\n")
        with self.assertRaisesRegex(SystemExit, "input changed"):
            self.run_package()
        self.assert_preserved()






    def test_success_is_complete_and_deterministic(self):
        self.run_package()
        first = self.output.read_bytes()
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(len(archive.namelist()), 22 + 2 * len(package.HUD_ART))
            self.assertIn("hud/skill-rockets@2x.png", archive.namelist())
            self.assertIn("hud/visor-cracks.png", archive.namelist())
            # The words: the cockpit's in lang/, the suits' names and descriptions where ForgeOrdnance reads them.
            self.assertIn("lang/zh.forge", archive.namelist())
            self.assertIn("ForgeOrdnance/lang/zh.forge", archive.namelist())
            self.assertEqual(archive.read("LICENSE"), b"LICENSE")
            self.assertEqual(archive.read("models/LICENSE.forge"), b"models licence")
            self.assertEqual(archive.read("forge-frames.bundle"), b"synthetic bundle")
            self.assertEqual(archive.read("audio/LICENSE.forge"), b"audio licence")
            # ForgeOrdnance reads the definitions from ForgeOrdnance/ beside the DLL: without them the whole exosuit category is gone.
            self.assertEqual(archive.read("ForgeOrdnance/frames.forge"), (Path(package.PACKAGE) / "Definitions/frames.forge").read_bytes())
        self.run_package()
        self.assertEqual(self.output.read_bytes(), first)


if __name__ == "__main__":
    unittest.main()
