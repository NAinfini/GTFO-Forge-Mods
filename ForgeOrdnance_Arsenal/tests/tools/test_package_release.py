import importlib.util
import hashlib
import json
import struct
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock


spec = importlib.util.spec_from_file_location("package_release", Path(__file__).parents[2] / "tools/package_release.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PackageReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.package = root / "package"
        self.output = root / "output"
        self.icons = root / "icons"
        self.build = root / "build"
        self.bundle = root / "forge-ordnance-arsenal.bundle"
        self.bundle_manifest = root / "forge-ordnance-arsenal.manifest.json"
        (self.package / "ForgeOrdnance").mkdir(parents=True)
        audio = self.package / "Assets/Audio"
        audio.mkdir(parents=True)
        self.icons.mkdir()
        dll = self.build / "bin/ForgeOrdnanceArsenal/release/ForgeOrdnanceArsenal.dll"
        dll.parent.mkdir(parents=True)
        dll.write_bytes(b"dll")
        self.bundle.write_bytes(b"bundle")
        self.write_bundle_receipt()
        manifest = {
            "name": "ForgeOrdnance_Arsenal",
            "version_number": "1.0.0",
            "website_url": "https://example.invalid",
            "description": "test package",
            "dependencies": [],
        }
        (self.package / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        for name in ("README.md", "CHANGELOG.md", "LICENSE"):
            (self.package / name).write_text(name, encoding="utf-8")
        png = b"\x89PNG\r\n\x1a\n" + b"\x00\x00\x00\rIHDR" + struct.pack(">II", 256, 256)
        (self.package / "icon.png").write_bytes(png)
        (self.icons / "strike.png").write_bytes(png)
        (self.package / "ForgeOrdnance/arsenal.json").write_text(
            json.dumps([{"Id": "arsenal.test", "Icon": "strike.png"}]), encoding="utf-8"
        )
        for folder in (self.package / "lang", self.package / "ForgeOrdnance/lang"):
            folder.mkdir(parents=True)
            for language in ("en", "zh"):
                (folder / (language + ".json")).write_text(json.dumps({"key": language}), encoding="utf-8")
        (audio / "test.cue.ogg").write_bytes(b"OggStest audio")
        (audio / "cues.json").write_text(json.dumps({"buses": {}, "cues": {"test.cue": {"gain": 0.8}}}), encoding="utf-8")
        (audio / "sources.json").write_text("{}", encoding="utf-8")
        (audio / "License.txt").write_text("licensed", encoding="utf-8")
        (self.package / "Assets/Models").mkdir(parents=True)
        (self.package / "Assets/Models/License.txt").write_text("credited", encoding="utf-8")
        module.PACKAGE = str(self.package)
        module.OUT = str(self.output)
        module.BUNDLE = str(self.bundle)
        module.BUNDLE_MANIFEST = str(self.bundle_manifest)
        module.ICONS = str(self.icons)
        self.final = self.output / "NAinfini-ForgeOrdnance_Arsenal-1.0.0.zip"

    def write_bundle_receipt(self):
        self.bundle_manifest.write_text(json.dumps({
            "bundle": self.bundle.name,
            "bytes": self.bundle.stat().st_size,
            "sha256": hashlib.sha256(self.bundle.read_bytes()).hexdigest(),
        }), encoding="utf-8")

    def temporary_zips(self):
        return list(self.output.glob("*.tmp")) if self.output.exists() else []

    def test_missing_input_preserves_existing_package(self):
        self.output.mkdir()
        self.final.write_bytes(b"previous valid package")
        (self.build / "bin/ForgeOrdnanceArsenal/release/ForgeOrdnanceArsenal.dll").unlink()
        with self.assertRaises(SystemExit):
            module.build_package(str(self.build))
        self.assertEqual(self.final.read_bytes(), b"previous valid package")
        self.assertEqual(self.temporary_zips(), [])

    def test_zip_write_failure_preserves_existing_package_and_cleans_temporary(self):
        self.output.mkdir()
        self.final.write_bytes(b"previous valid package")
        with mock.patch.object(module.zipfile.ZipFile, "writestr", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                module.build_package(str(self.build))
        self.assertEqual(self.final.read_bytes(), b"previous valid package")
        self.assertEqual(self.temporary_zips(), [])

    def test_missing_build_receipt_preserves_existing_package(self):
        self.output.mkdir()
        self.final.write_bytes(b"previous valid package")
        self.bundle_manifest.unlink()
        with self.assertRaises(SystemExit):
            module.build_package(str(self.build))
        self.assertEqual(self.final.read_bytes(), b"previous valid package")

    def test_bundle_changed_after_build_is_rejected(self):
        self.bundle.write_bytes(b"staled")  # same length as the receipted bundle; only the hash differs
        with self.assertRaises(SystemExit):
            module.build_package(str(self.build))
        self.assertFalse(self.final.exists())

    def test_missing_language_file_is_rejected(self):
        (self.package / "ForgeOrdnance/lang/zh.json").unlink()
        with self.assertRaises(SystemExit):
            module.build_package(str(self.build))
        self.assertFalse(self.final.exists())

    def test_missing_audio_file_is_rejected(self):
        (self.package / "Assets/Audio/test.cue.ogg").unlink()
        with self.assertRaises(SystemExit):
            module.build_package(str(self.build))
        self.assertFalse(self.final.exists())

    def test_success_atomically_replaces_with_deterministic_package(self):
        module.build_package(str(self.build))
        first = self.final.read_bytes()
        self.final.write_bytes(b"old package")
        module.build_package(str(self.build))
        self.assertEqual(self.final.read_bytes(), first)
        self.assertEqual(self.temporary_zips(), [])
        with zipfile.ZipFile(self.final) as package:
            self.assertEqual(package.namelist(), sorted(package.namelist()))
            self.assertEqual(package.read("ForgeOrdnanceArsenal.dll"), b"dll")
            self.assertIn("audio/cues.json", package.namelist())
            self.assertIn("lang/zh.json", package.namelist())
            self.assertIn("ForgeOrdnance/lang/en.json", package.namelist())
            self.assertEqual(len([name for name in package.namelist() if name.endswith(".ogg")]), 1)


if __name__ == "__main__":
    unittest.main()
