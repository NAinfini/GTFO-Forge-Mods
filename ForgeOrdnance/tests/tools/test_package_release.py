import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("package_release", Path(__file__).parents[2] / "tools/package_release.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class AudioPackageTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.addCleanup(temporary.cleanup)
        self.package = Path(temporary.name)
        self.audio = self.package / "Assets/Audio"
        self.audio.mkdir(parents=True)
        for name in ("ui.click.ogg", "boom-01.ogg", "boom-02.ogg"):
            (self.audio / name).write_bytes(b"OggStest")
        (self.audio / "cues.json").write_text(json.dumps({"buses": {}, "cues": {
            "ui.click": {"gain": 0.5},
            "boom": {"layers": [{"clips": ["boom-01.ogg", "boom-02.ogg"]}]},
        }}), encoding="utf-8")
        (self.audio / "sources.json").write_text("{}", encoding="utf-8")
        (self.audio / "License.txt").write_text("licensed", encoding="utf-8")

    def test_audio_folder_ships_cues_and_every_named_clip(self):
        files = module.audio_files(str(self.package))
        self.assertEqual(sorted(files), ["audio/License.txt", "audio/boom-01.ogg", "audio/boom-02.ogg", "audio/cues.json",
                                         "audio/sources.json", "audio/ui.click.ogg"])

    def test_missing_clip_is_rejected(self):
        (self.audio / "boom-02.ogg").unlink()
        with self.assertRaises(SystemExit):
            module.audio_files(str(self.package))

    def test_stray_ogg_is_rejected(self):
        (self.audio / "0123abcd.ogg").write_bytes(b"OggStest")
        with self.assertRaises(SystemExit):
            module.audio_files(str(self.package))


def profile(pod_name="drop-pod", parts=None, surfaces=("drop-pod/M02_Pod", "drop-pod/Pod_InnerSteel")):
    names = sorted(module.POD_PARTS) if parts is None else parts
    return {"prefabs": [{"name": "beacon", "renderers": [{"name": "M01", "surfaces": ["beacon/M01_mat"]}]},
                        {"name": pod_name, "renderers": [{"name": name, "surfaces": list(surfaces)} for name in names]},
                        {"name": "pod-impact", "renderers": []}]}


class DropPodTests(unittest.TestCase):
    def test_the_hexagonal_pod_passes(self):
        module.check_pod(profile())

    def test_a_second_pod_look_is_rejected(self):
        second = profile()
        second["prefabs"].append({"name": "drop-pod-square", "renderers": []})
        with self.assertRaises(SystemExit):
            module.check_pod(second)

    def test_the_removed_square_pod_is_rejected(self):
        with self.assertRaises(SystemExit):
            module.check_pod(profile(surfaces=("drop-pod/M02_Pod", "drop-pod/Container")))
        with self.assertRaises(SystemExit):
            module.check_pod(profile(surfaces=("drop-pod/Container", "drop-pod/Cage")))

    def test_obsolete_or_missing_parts_are_rejected(self):
        with self.assertRaises(SystemExit):
            module.check_pod(profile(parts=sorted(module.POD_PARTS) + ["light_strip"]))
        with self.assertRaises(SystemExit):
            module.check_pod(profile(parts=["body", "door_1"]))


class RequiredPrefabTests(unittest.TestCase):
    def full(self):
        return {"prefabs": [{"name": name} for name in module.REQUIRED_PREFABS]}

    def test_a_complete_bundle_passes(self):
        module.check_required_prefabs(self.full())

    def test_a_stale_bundle_without_the_effect_prefab_is_rejected(self):
        stale = self.full()
        stale["prefabs"] = [p for p in stale["prefabs"] if p["name"] != "ordnance-fx"]
        with self.assertRaises(SystemExit):
            module.check_required_prefabs(stale)


class NoSecondPodSourceTests(unittest.TestCase):
    """No build script, definition or licence may point at the removed POD model or add another pod look."""

    ROOT = Path(__file__).parents[3]

    def sources(self):
        for package in ("ForgeOrdnance", "ForgeOrdnance_Arsenal"):
            for path in (self.ROOT / package).rglob("*"):
                parts = set(path.relative_to(self.ROOT).parts)
                if path.is_file() and path.suffix in {".ps1", ".py", ".json", ".txt", ".cs"} and not parts & {"obj", "bin", "__pycache__", "tests"}:
                    yield path

    def test_nothing_references_the_removed_pod(self):
        needles = ("POD/POD.blend", "josuer3d", "Bio-Hazard", "bio-hazard-containment")
        hits = [str(path.relative_to(self.ROOT)) for path in self.sources() if any(n in path.read_text(encoding="utf-8", errors="replace") for n in needles)]
        self.assertEqual(hits, [])

    def test_the_build_exports_one_pod_prefab(self):
        script = (self.ROOT / "ForgeOrdnance/tools/build_assets.ps1").read_text(encoding="utf-8")
        self.assertEqual(script.count("'drop-pod'"), 1)
        self.assertNotIn("POD.blend", script)

    def test_definitions_choose_no_pod_model(self):
        for path in (self.ROOT / "ForgeOrdnance_Arsenal/ForgeOrdnance").glob("*.json"):
            keys = []

            def collect(node):
                if isinstance(node, dict):
                    for key, value in node.items():
                        keys.append(key.lower())
                        collect(value)
                elif isinstance(node, list):
                    for value in node:
                        collect(value)

            collect(json.loads(path.read_text(encoding="utf-8")))
            self.assertEqual([key for key in keys if "pod" in key], [], path.name)
            for entry in json.loads(path.read_text(encoding="utf-8")):
                self.assertNotIn("pod", str(entry.get("Payload", {}).get("Model", "")).lower(), path.name)


if __name__ == "__main__":
    unittest.main(verbosity=2)
