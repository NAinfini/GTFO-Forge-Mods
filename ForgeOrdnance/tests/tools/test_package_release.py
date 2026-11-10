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
        (self.audio / "cues.forge").write_text(json.dumps({"buses": {}, "cues": {
            "ui.click": {"gain": 0.5},
            "boom": {"layers": [{"clips": ["boom-01.ogg", "boom-02.ogg"]}]},
        }}), encoding="utf-8")
        (self.audio / "sources.forge").write_text("{}", encoding="utf-8")
        (self.audio / "LICENSE.forge").write_text("licensed", encoding="utf-8")

    def test_audio_folder_ships_cues_and_every_named_clip(self):
        files = module.audio_files(str(self.package))
        self.assertEqual(sorted(files), ["audio/LICENSE.forge", "audio/boom-01.ogg", "audio/boom-02.ogg", "audio/cues.forge",
                                         "audio/sources.forge", "audio/ui.click.ogg"])

    def test_missing_clip_is_rejected(self):
        (self.audio / "boom-02.ogg").unlink()
        with self.assertRaises(SystemExit):
            module.audio_files(str(self.package))

    def test_stray_ogg_is_rejected(self):
        (self.audio / "0123abcd.ogg").write_bytes(b"OggStest")
        with self.assertRaises(SystemExit):
            module.audio_files(str(self.package))


def profile(pod_name="drop-pod", parts=None, surfaces=("drop-pod/drop-pod",)):
    names = sorted(module.POD_PARTS) if parts is None else parts
    return {"prefabs": [{"name": "beacon", "renderers": [{"name": "beacon", "surfaces": ["beacon/beacon"]}]},
                        {"name": pod_name, "renderers": [{"name": name, "surfaces": list(surfaces)} for name in names]},
                        {"name": "pod-impact", "renderers": []}]}


class BundleGateTests(unittest.TestCase):
    def full(self):
        return {"prefabs": [{"name": name} for name in module.REQUIRED_PREFABS]}

    def test_a_stale_bundle_without_the_effect_prefab_is_rejected(self):
        stale = self.full()
        stale["prefabs"] = [p for p in stale["prefabs"] if p["name"] != "ordnance-fx"]
        with self.assertRaises(SystemExit):
            module.check_required_prefabs(stale)

    def test_the_one_current_pod_passes_and_a_second_look_or_an_older_pod_is_rejected(self):
        module.check_pod(profile())
        for bad in (profile(pod_name="drop-pod-b"), profile(parts=sorted(module.POD_PARTS - {"door_4"})),
                    profile(parts=sorted(module.POD_PARTS) + ["light_strip"]), profile(surfaces=("drop-pod/ArsenalPod_steel",))):
            with self.assertRaises(SystemExit):
                module.check_pod(bad)
        both = profile()
        both["prefabs"].append({"name": "supply-pod", "renderers": []})
        with self.assertRaises(SystemExit):
            module.check_pod(both)


if __name__ == "__main__":
    unittest.main(verbosity=2)
