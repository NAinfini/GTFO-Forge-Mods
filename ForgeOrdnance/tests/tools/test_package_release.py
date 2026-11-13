"""Offline packaging checks on a synthetic package; the bundle bytes never claim to be a real Unity build."""
import importlib.util
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import zipfile

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("package_release", Path(__file__).parents[2] / "tools/package_release.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
package_common = module.package_common

PNG = b"\x89PNG\r\n\x1a\n" + b"\x00\x00\x00\rIHDR" + struct.pack(">II", 256, 256) + b"\x08\x06\x00\x00\x00" + bytes(4)


def carrier_profile(pod_name="drop-pod", parts=None, surfaces=("drop-pod/drop-pod",), prefabs=module.CARRIER_PREFABS):
    names = sorted(module.POD_PARTS) if parts is None else parts
    pod = {"name": pod_name, "renderers": [{"name": name, "surfaces": list(surfaces)} for name in names]}
    return {"prefabs": [pod if name == "drop-pod" else {"name": name, "renderers": []} for name in prefabs]}


class PackageTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.folder = self.root / "Mods/ForgeOrdnance"
        self.assets = self.folder / "Assets"
        manifest = {"name": "ForgeOrdnance", "version_number": "0.0.1", "website_url": "https://example.org",
                    "description": "test package", "dependencies": ["NAinfini-ForgeCore-0.0.1"]}
        self.write(self.folder / "manifest.json", json.dumps(manifest))
        self.write(self.folder / "Plugin.cs", 'internal const string Version = "0.0.1";')
        for name in ("icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE"):
            self.write(self.folder / name, PNG if name.endswith(".png") else name)
        for words in ("lang", "Arsenal/Definitions/lang", "Frames/Definitions/lang"):
            for language in ("en", "zh"):
                self.write(self.folder / words / (language + ".forge"), json.dumps({"key": language}))
        self.write(self.root / "build/bin/ForgeOrdnance/release/ForgeOrdnance.dll", b"MZ synthetic dll")
        for part in ("Carrier", "Arsenal", "Frames"):
            self.write(self.assets / part / "Models/LICENSE.forge", part + " models licence")
            self.write(self.assets / part / "Audio/LICENSE.forge", part + " audio licence")
            self.write(self.assets / part / "Audio/sources.forge", "{}")
        # Carrier: the bundle, its Unity receipt and the build log with the verified marker.
        self.carrier = self.assets / "Carrier/Models/forge-ordnance.bundle"
        self.write(self.carrier, b"UnityFS carrier")
        self.write_receipt(self.carrier, "forge-ordnance.manifest.json", carrier_profile())
        self.log = self.assets / "Carrier/Models/unity-build.log"
        self.write(self.log, "ORDNANCE_BUNDLE_VERIFIED forge-ordnance.bundle " + str(self.carrier.stat().st_size) + " bytes " + module.sha256(self.carrier))
        for name in ("ui.click.ogg", "boom-01.ogg", "boom-02.ogg"):
            self.write(self.assets / "Carrier/Audio" / name, b"OggStest")
        self.write(self.assets / "Carrier/Audio/cues.forge", json.dumps({"buses": {}, "cues": {
            "ui.click": {"gain": 0.5}, "boom": {"layers": [{"clips": ["boom-01.ogg", "boom-02.ogg"]}]}}}))
        # Arsenal: the bundle with its receipt, one definition with its icon.
        self.arsenal = self.assets / "Arsenal/Models/forge-ordnance-arsenal.bundle"
        self.write(self.arsenal, b"UnityFS arsenal")
        self.write_receipt(self.arsenal, "forge-ordnance-arsenal.manifest.json", {"prefabs": [{"name": name} for name in module.ARSENAL_PREFABS]})
        self.write(self.folder / "Arsenal/Definitions/arsenal.forge", json.dumps([{"Id": "arsenal.test", "Icon": "strike.png"}]))
        self.write(self.assets / "Arsenal/icons/strike.png", PNG)
        self.write(self.assets / "Arsenal/Audio/test.cue.ogg", b"OggStest")
        self.write(self.assets / "Arsenal/Audio/cues.forge", json.dumps({"buses": {}, "cues": {"test.cue": {"gain": 0.8}}}))
        # Frames: four definitions with icons, the cockpit art at 1x and @2x with the visor drawing, and the receipt that pins the build inputs.
        definitions = [{"Id": "frame" + str(i), "Icon": str(i) + ".png", "Payload": {"Model": "Frames:frame-m" + str(i)}} for i in range(15, 19)]
        self.write(self.folder / "Frames/Definitions/frames.forge", json.dumps(definitions))
        for entry in definitions:
            self.write(self.assets / "Frames/icons" / entry["Icon"], PNG)
        for name in module.HUD_ART + [name[:-4] + "@2x.png" for name in module.HUD_ART] + ["visor-cracks.png"]:
            self.write(self.assets / "Frames/hud" / name, PNG)
        self.write(self.assets / "Frames/Audio/exo.test-a.ogg", b"OggStest")
        self.write(self.assets / "Frames/Audio/cues.forge", json.dumps({"buses": {}, "cues": {"exo.test": {"layers": [{"clips": ["exo.test-a.ogg"]}]}}}))
        self.frames = self.assets / "Frames/Models/forge-frames.bundle"
        self.write(self.frames, b"UnityFS frames")
        self.script = self.folder / "tools/Frames/build_assets.ps1"
        self.write(self.script, "build source")
        self.write(self.root / "Tools/Blender/hinge_tool/bake.py", "bake source")  # the Blender add-on the build bakes with is an input too
        self.write(self.root / "cut/M15_cut.draft.blend", b"cut source")
        inputs = ["Mods/ForgeOrdnance/Frames/Definitions/frames.forge", "Mods/ForgeOrdnance/tools/Frames/build_assets.ps1", "Tools/Blender/hinge_tool/bake.py"]
        self.receipt = {"format": 3, "bundleBytes": self.frames.stat().st_size, "bundleSha256": module.sha256(self.frames),
                        "models": [entry["Payload"]["Model"].split(":", 1)[1] for entry in definitions],
                        "roots": {"cut": str(self.root / "cut"), "pilot": str(self.root / "pilot"), "props": str(self.root / "props")},
                        "inputs": {name: module.text_sha256(self.root / name) for name in inputs},
                        "sources": {"cut/M15_cut.draft.blend": module.sha256(self.root / "cut/M15_cut.draft.blend")}}
        self.frames_receipt = self.assets / "Frames/Models/forge-frames.receipt.json"
        self.save_receipt()
        self.output = self.root / "release/NAinfini-ForgeOrdnance-0.0.1.zip"
        self.write(self.output, b"previous release")

    @staticmethod
    def write(path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value.encode() if isinstance(value, str) else value)

    def write_receipt(self, bundle, name, profile):
        self.write(bundle.with_name(name), json.dumps({"bundle": bundle.name, "bytes": bundle.stat().st_size, "sha256": module.sha256(bundle), "profile": profile}))

    def save_receipt(self):
        self.write(self.frames_receipt, json.dumps(self.receipt))

    def run_package(self):
        build = self.root / "build"
        package_common.package_library(self.folder, "ForgeOrdnance", ("ForgeOrdnance.dll",), build, self.output.parent, module.METADATA,
                                       module.extra(self.folder, build / "bin/ForgeOrdnance/release"))

    def assert_rejected(self, message):
        """The gate refuses, and the earlier zip stays as it was with no half-written file beside it."""
        with self.assertRaisesRegex(ValueError, message):
            self.run_package()
        self.assertEqual(self.output.read_bytes(), b"previous release")
        self.assertEqual(list(self.output.parent.glob("*.tmp")), [])

    def test_the_one_current_pod_passes_and_a_second_look_or_an_older_pod_is_rejected(self):
        module.check_pod(carrier_profile())
        for bad in (carrier_profile(pod_name="drop-pod-b"), carrier_profile(parts=sorted(module.POD_PARTS - {"door_4"})),
                    carrier_profile(parts=sorted(module.POD_PARTS) + ["light_strip"]), carrier_profile(surfaces=("drop-pod/ArsenalPod_steel",))):
            with self.assertRaises(ValueError):
                module.check_pod(bad)
        both = carrier_profile()
        both["prefabs"].append({"name": "supply-pod", "renderers": []})
        with self.assertRaises(ValueError):
            module.check_pod(both)

    def test_a_stale_bundle_without_a_prefab_the_plugin_loads_is_rejected(self):
        self.write_receipt(self.carrier, "forge-ordnance.manifest.json", carrier_profile(prefabs=module.CARRIER_PREFABS[:-1]))
        self.assert_rejected("Carrier bundle lacks the prefabs ordnance-fx")
        self.write_receipt(self.carrier, "forge-ordnance.manifest.json", carrier_profile())
        self.write_receipt(self.arsenal, "forge-ordnance-arsenal.manifest.json", {"prefabs": [{"name": name} for name in module.ARSENAL_PREFABS[1:]]})
        self.assert_rejected("Arsenal bundle lacks the prefabs shell ")

    def test_a_bundle_without_its_unity_receipt_is_rejected(self):
        self.arsenal.with_name("forge-ordnance-arsenal.manifest.json").unlink()
        self.assert_rejected("missing .*forge-ordnance-arsenal.manifest.json")

    def test_a_bundle_changed_after_its_build_is_rejected(self):
        self.arsenal.write_bytes(b"UnityFS staled!")  # same length as the receipted bundle; only the hash differs
        self.assert_rejected("does not match its Unity build receipt")

    def test_a_carrier_bundle_the_kept_build_log_did_not_verify_is_rejected(self):
        self.write(self.log, "ORDNANCE_BUNDLE_VERIFIED forge-ordnance.bundle 1 bytes 00")
        self.assert_rejected("no matching successful-build marker")

    def test_frames_bundle_without_its_receipt_is_rejected(self):
        self.frames_receipt.unlink()
        self.assert_rejected("missing bundle receipt")

    def test_frames_bundle_replaced_after_its_build_is_rejected(self):
        self.frames.write_bytes(b"UnityFS unverified replacement")
        self.assert_rejected("verified build receipt")

    def test_frames_build_input_changed_since_the_build_is_rejected(self):
        # the same script checked out with CRLF is the same input; one changed line is not
        self.script.write_bytes(b"build source\r\n")
        self.receipt["inputs"]["Mods/ForgeOrdnance/tools/Frames/build_assets.ps1"] = module.text_sha256(self.script)
        self.save_receipt()
        self.script.write_bytes(b"build source\n")
        self.run_package()
        self.write(self.output, b"previous release")
        self.script.write_bytes(b"build source changed\r\n")
        self.assert_rejected("input changed")

    def test_missing_clip_is_rejected(self):
        (self.assets / "Carrier/Audio/boom-02.ogg").unlink()
        self.assert_rejected("missing audio file")

    def test_stray_ogg_is_rejected(self):
        self.write(self.assets / "Arsenal/Audio/0123abcd.ogg", b"OggStest")
        self.assert_rejected("not named by cues.forge")

    def test_success_is_complete_laid_out_by_part_and_deterministic(self):
        self.run_package()
        first = self.output.read_bytes()
        with zipfile.ZipFile(self.output) as archive:
            names = archive.namelist()
            self.assertEqual(names, sorted(names))
            # Every part's Audio/ holds a sources.forge (setUp); none is packed.
            carrier = ["Carrier/" + name for name in ("forge-ordnance.bundle", "models/LICENSE.forge", "audio/cues.forge",
                                                      "audio/LICENSE.forge", "audio/ui.click.ogg", "audio/boom-01.ogg", "audio/boom-02.ogg")]
            arsenal = ["Arsenal/" + name for name in ("forge-ordnance-arsenal.bundle", "models/LICENSE.forge", "audio/cues.forge",
                                                      "audio/LICENSE.forge", "audio/test.cue.ogg", "ForgeOrdnance/arsenal.forge",
                                                      "ForgeOrdnance/lang/en.forge", "ForgeOrdnance/lang/zh.forge", "ForgeOrdnance/strike.png")]
            frames = ["Frames/" + name for name in ["forge-frames.bundle", "models/LICENSE.forge", "audio/cues.forge", "audio/LICENSE.forge",
                                                    "audio/exo.test-a.ogg", "ForgeOrdnance/frames.forge", "ForgeOrdnance/lang/en.forge",
                                                    "ForgeOrdnance/lang/zh.forge", "hud/visor-cracks.png"]
                      + ["ForgeOrdnance/" + str(i) + ".png" for i in range(15, 19)]
                      + ["hud/" + art for name in module.HUD_ART for art in (name, name[:-4] + "@2x.png")]]
            root = ["ForgeOrdnance.dll", "lang/en.forge", "lang/zh.forge", *module.METADATA]
            self.assertEqual(sorted(names), sorted(root + carrier + arsenal + frames))
            self.assertEqual(archive.read("ForgeOrdnance.dll"), b"MZ synthetic dll")
            self.assertEqual(archive.read("Frames/models/LICENSE.forge"), b"Frames models licence")
            # The carrier reads definitions from every ForgeOrdnance/ folder under the plugin: without them the whole category is gone.
            self.assertEqual(archive.read("Frames/ForgeOrdnance/frames.forge"), (self.folder / "Frames/Definitions/frames.forge").read_bytes())
        self.write(self.output, b"old package")
        self.run_package()
        self.assertEqual(self.output.read_bytes(), first)
        self.assertEqual(list(self.output.parent.glob("*.tmp")), [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
