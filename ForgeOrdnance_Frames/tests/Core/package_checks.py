"""Offline packaging regressions; synthetic bytes never claim to be a Unity bundle."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("frames_package", Path(__file__).parents[2] / "tools/package_release.py")
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)
ASSET_SCRIPT = Path(__file__).parents[2] / "tools/build_assets.ps1"


class PackageChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        paths = {
            "REPO": self.root, "PACKAGE": self.root / "Mods/ForgeOrdnance_Frames",
            "OUT": self.root / "release",
            "DLL": self.root / "build/ForgeOrdnanceFrames.dll",
        }
        self.build = self.root / "assets/frames-build"
        paths.update(BUNDLE=paths["PACKAGE"] / "Assets/Models/forge-frames.bundle",
                     RECEIPT=paths["PACKAGE"] / "Assets/Models/forge-frames.receipt.json", ICONS=paths["PACKAGE"] / "Assets/icons",
                     AUDIO=paths["PACKAGE"] / "Assets/audio", MODELS_LICENCE=paths["PACKAGE"] / "Assets/Models/License.txt")
        self.patch = patch.multiple(package, MODELS=str(paths["PACKAGE"] / "Assets/Models"), **{key: str(value) for key, value in paths.items()})
        self.patch.start()
        self.addCleanup(self.patch.stop)
        manifest = {"name": "ForgeOrdnance_Frames", "version_number": "1.0.0", "website_url": "https://example.org",
                    "description": "Frames", "dependencies": ["NAinfini-ForgeAudio-0.1.0"]}
        self.write(paths["PACKAGE"] / "manifest.json", json.dumps(manifest))
        png = b"\x89PNG\r\n\x1a\n" + b"\0" * 8 + struct.pack(">II", 256, 256)
        for name in ("icon.png", "README.md", "CHANGELOG.md", "LICENSE"):
            self.write(paths["PACKAGE"] / name, png if name.endswith(".png") else name)
        self.definitions = [{"Id": "frame" + str(i), "Icon": str(i) + ".png", "Payload": {"Model": "Frames:frame-m" + str(i)}}
                            for i in range(15, 19)]
        self.write(paths["PACKAGE"] / "Definitions/frames.json", json.dumps(self.definitions))
        for entry in self.definitions:
            self.write(paths["ICONS"] / entry["Icon"], png)
        self.write(paths["DLL"], b"synthetic dll")
        self.write(paths["BUNDLE"], b"synthetic bundle")
        self.write(paths["AUDIO"] / "exo.test-a.ogg", b"synthetic ogg")
        self.write(paths["AUDIO"] / "cues.json", json.dumps({"buses": {}, "cues": {"exo.test": {"layers": [{"clips": ["exo.test-a.ogg"]}]}}}))
        self.write(paths["AUDIO"] / "LICENSE.txt", "audio licence")
        self.write(paths["MODELS_LICENCE"], "models licence")
        # The cockpit art the plugin loads, every picture at 1x and @2x, as tools/import_hud_art.ps1 leaves it.
        for name in package.HUD_ART:
            for art in (name, name[:-4] + "@2x.png"):
                self.write(paths["PACKAGE"] / "Assets/hud" / art, png)
        # The visor's crack drawing, one file (tools/import_visor_cracks.py).
        self.write(paths["PACKAGE"] / "Assets/hud/visor-cracks.png", png)
        for folder in ("lang", "Definitions/lang"):
            for language in ("en.json", "zh.json"):
                self.write(paths["PACKAGE"] / folder / language, "{}")
        self.write(paths["PACKAGE"] / "tools/build_assets.ps1", "build source")
        self.write(self.root / "cut/M15_cut.draft.blend", b"cut source")
        self.write(paths["PACKAGE"] / "tools/props.json", "{}")
        self.inputs = [str(path.relative_to(self.root)).replace("\\", "/") for path in (
            paths["PACKAGE"] / "Definitions/frames.json", paths["PACKAGE"] / "tools/build_assets.ps1",
            paths["PACKAGE"] / "tools/props.json")]
        self.receipt = {"format": 2, "bundleBytes": paths["BUNDLE"].stat().st_size,
                        "bundleSha256": package.sha256(paths["BUNDLE"]),
                        "models": [entry["Payload"]["Model"].split(":", 1)[1] for entry in self.definitions],
                        "roots": {"cut": str(self.root / "cut"), "pilot": str(self.root / "pilot"), "props": str(self.root / "props")},
                        "inputs": {name: package.sha256(self.root / name) for name in self.inputs},
                        "sources": {"cut/M15_cut.draft.blend": package.sha256(self.root / "cut/M15_cut.draft.blend")}}
        self.save_receipt()
        self.output = paths["OUT"] / "NAinfini-ForgeOrdnance_Frames-1.0.0.zip"
        self.write(self.output, b"previous release")

    @staticmethod
    def write(path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value.encode() if isinstance(value, str) else value)

    def save_receipt(self):
        self.write(Path(package.RECEIPT), json.dumps(self.receipt))

    def run_package(self):
        with contextlib.redirect_stdout(io.StringIO()):
            package.main()

    def assert_preserved(self):
        self.assertEqual(self.output.read_bytes(), b"previous release")
        self.assertEqual(list(self.output.parent.glob("*.tmp")), [])

    def test_missing_receipt(self):
        Path(package.RECEIPT).unlink()
        with self.assertRaisesRegex(SystemExit, "missing bundle receipt"):
            self.run_package()
        self.assert_preserved()

    def test_subset_build_invalidates_old_receipt_before_tools(self):
        stale = self.build / "out/forge-frames.receipt.json"
        self.write(stale, "{}")
        result = subprocess.run(["pwsh", "-NoProfile", "-File", str(ASSET_SCRIPT), "-Build", str(self.build),
                                 "-Models", "M15"], capture_output=True, text=True, encoding="utf-8", errors="replace")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Subset builds cannot ship", result.stderr)
        self.assertFalse(stale.exists())
        self.assert_preserved()

    def test_failed_full_build_invalidates_old_receipt(self):
        stale = self.build / "out/forge-frames.receipt.json"
        self.write(stale, "{}")
        result = subprocess.run(["pwsh", "-NoProfile", "-File", str(ASSET_SCRIPT), "-Build", str(self.build),
                                 "-Unity", str(self.root / "missing-unity.exe")], capture_output=True, text=True, encoding="utf-8", errors="replace")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Missing required input", result.stderr)
        self.assertFalse(stale.exists())
        self.assert_preserved()

    def test_subset_receipt(self):
        self.receipt["models"] = ["frame-m15"]
        self.save_receipt()
        with self.assertRaisesRegex(SystemExit, "shipped definitions"):
            self.run_package()
        self.assert_preserved()

    def test_replaced_bundle(self):
        Path(package.BUNDLE).write_bytes(b"unverified replacement")
        with self.assertRaisesRegex(SystemExit, "verified build receipt"):
            self.run_package()
        self.assert_preserved()

    def test_changed_source(self):
        (self.root / self.inputs[1]).write_text("changed source")
        with self.assertRaisesRegex(SystemExit, "input changed"):
            self.run_package()
        self.assert_preserved()

    def test_changed_model_source(self):
        (self.root / "cut/M15_cut.draft.blend").write_bytes(b"edited cut")
        with self.assertRaisesRegex(SystemExit, "input changed: cut/M15"):
            self.run_package()
        self.assert_preserved()

    def test_incomplete_receipt(self):
        self.receipt["inputs"] = {}
        self.save_receipt()
        with self.assertRaisesRegex(SystemExit, "source hashes"):
            self.run_package()
        self.assert_preserved()

    def test_missing_dll(self):
        Path(package.DLL).unlink()
        with self.assertRaisesRegex(SystemExit, "missing .*dll"):
            self.run_package()
        self.assert_preserved()

    def test_missing_audio_is_rejected(self):
        next(Path(package.AUDIO).glob("*.ogg")).unlink()
        with self.assertRaisesRegex(SystemExit, "missing audio file"):
            self.run_package()
        self.assert_preserved()

    def test_write_failure(self):
        original = zipfile.ZipFile.writestr
        writes = 0
        def interrupted(archive, *args, **kwargs):
            nonlocal writes
            writes += 1
            if writes == 3:
                raise OSError("simulated source/disk failure")
            return original(archive, *args, **kwargs)
        with patch.object(zipfile.ZipFile, "writestr", interrupted):
            with self.assertRaisesRegex(OSError, "simulated"):
                self.run_package()
        self.assert_preserved()

    def test_success_is_complete_and_deterministic(self):
        self.run_package()
        first = self.output.read_bytes()
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(len(archive.namelist()), 21 + 2 * len(package.HUD_ART))
            self.assertIn("hud/skill-rockets@2x.png", archive.namelist())
            self.assertIn("hud/visor-cracks.png", archive.namelist())
            # The words: the cockpit's in lang/, the suits' names and descriptions where ForgeOrdnance reads them.
            self.assertIn("lang/zh.json", archive.namelist())
            self.assertIn("ForgeOrdnance/lang/zh.json", archive.namelist())
            self.assertEqual(archive.read("LICENSE"), b"LICENSE")
            self.assertEqual(archive.read("models/LICENSE.txt"), b"models licence")
            self.assertEqual(archive.read("forge-frames.bundle"), b"synthetic bundle")
            self.assertEqual(archive.read("audio/LICENSE.txt"), b"audio licence")
            # ForgeOrdnance reads the definitions from ForgeOrdnance/ beside the DLL: without them the whole exosuit category is gone.
            self.assertEqual(archive.read("ForgeOrdnance/frames.json"), (Path(package.PACKAGE) / "Definitions/frames.json").read_bytes())
        self.run_package()
        self.assertEqual(self.output.read_bytes(), first)


PACKAGE_ROOT = Path(__file__).parents[2]
# The profile the owner tests with; FRAMES_PLUGIN_DIR names another installed plugin folder.
DEFAULT_PLUGIN = Path.home() / "AppData/Roaming/r2modmanPlus-local/GTFO/profiles/Forge-Ordnance-Test/BepInEx/plugins/NAinfini-ForgeOrdnance_Frames"


class InstalledPluginChecks(unittest.TestCase):
    """Local-resource layer: an installed Frames plugin folder must carry the tracked definitions and audio settings byte for byte
    (a DLL from this source with older definitions rejects every suit on a field it no longer knows). Skipped, never
    passed, when no installed folder is present."""

    def setUp(self):
        import os
        self.plugin = Path(os.environ.get("FRAMES_PLUGIN_DIR", str(DEFAULT_PLUGIN)))
        if not (self.plugin / "ForgeOrdnanceFrames.dll").is_file():
            self.skipTest("needs local resources, not run: no installed Frames plugin at " + str(self.plugin))

    def assert_same(self, installed, source):
        self.assertTrue(installed.is_file(), "missing in the installed plugin: " + str(installed))
        self.assertEqual(package.sha256(installed), package.sha256(source),
                         str(installed) + " differs from " + str(source) + "; install the whole package, not only the DLL and bundle")

    def test_installed_definitions_match_source(self):
        self.assert_same(self.plugin / "ForgeOrdnance/frames.json", PACKAGE_ROOT / "Definitions/frames.json")

    def test_installed_audio_settings_match_source(self):
        self.assert_same(self.plugin / "audio/cues.json", PACKAGE_ROOT / "Assets/audio/cues.json")


if __name__ == "__main__":
    unittest.main()
