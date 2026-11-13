import array
import importlib.util
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("stage_audio", Path(__file__).parents[2] / "tools/Carrier/stage_audio.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
render = module.cue_render


class StageAudioTests(unittest.TestCase):
    def test_unknown_source_rule_is_rejected(self):
        with self.assertRaises(render.RenderError):
            render.validate_rules({"maxSeconds": 1})

    def test_linear_normalisation_obeys_true_peak_ceiling(self):
        result, gain = render.normalise(array.array("h", [1000, -1000, 500]), -40, -10, -18)
        self.assertAlmostEqual(gain, 8.5)
        self.assertEqual(result[0], round(1000 * 10 ** (8.5 / 20)))

    def document(self, cue, buses=None):
        return {"schemaVersion": "1.0.0", "namespace": "test", "buses": buses or {"b": {"category": "ui"}}, "cues": cue}

    def test_duplicate_cue_is_rejected(self):
        source = {"file": "a.wav", "sha256": "x", "pack": "p", "licence": "l", "targetLufs": -18}
        cue = {"id": "a", "bus": "b", "layers": [{"sources": [source]}]}
        with self.assertRaisesRegex(module.StageError, "duplicate cue"):
            module.validate_document(self.document([cue, cue]))

    def test_unknown_bus_is_rejected(self):
        source = {"file": "a.wav", "sha256": "x", "pack": "p", "licence": "l", "targetLufs": -18}
        with self.assertRaisesRegex(module.StageError, "bus"):
            module.validate_document(self.document([{"id": "a", "bus": "nope", "layers": [{"sources": [source]}]}], {}))

    def test_bus_needs_a_known_category_and_cues_none(self):
        source = {"file": "a.wav", "sha256": "x", "pack": "p", "licence": "l", "targetLufs": -18}
        for buses, cue, message in (
                ({"b": {}}, {"bus": "b"}, "needs a category"),
                ({"b": {"category": "sfx"}}, {"bus": "b"}, "needs a category"),
                ({"b": {"category": "explosion", "duck": {"buses": ["x"]}}}, {"bus": "b"}, "duck.categories"),
                ({"b": {"category": "ui"}}, {"bus": "b", "category": "ui"}, "belongs to its bus"),
                ({"b": {"category": "ui"}}, {}, "needs a bus")):
            with self.assertRaisesRegex(module.StageError, message):
                module.validate_document(self.document([{"id": "a", "layers": [{"sources": [source]}]} | cue], buses))


if __name__ == "__main__":
    unittest.main(verbosity=2)
