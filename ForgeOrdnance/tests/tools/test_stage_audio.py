import array
import importlib.util
import math
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import wave


spec = importlib.util.spec_from_file_location("stage_audio", Path(__file__).parents[2] / "tools/stage_audio.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
render = module.cue_render


def tone(seconds, level=6000):
    return array.array("h", (round(level * math.sin(index * 2 * math.pi / 480)) for index in range(round(render.RATE * seconds))))


class StageAudioTests(unittest.TestCase):
    def test_one_shot_starts_ten_milliseconds_before_the_first_transient(self):
        quiet = array.array("h", [0] * (render.RATE // 2))
        clip, lead = render.render(quiet + tone(1.0), {})
        self.assertLess(len(clip) / render.RATE, 1.1)
        self.assertEqual((clip[0], clip[-1]), (0, 0))
        self.assertGreater(max(clip), 5000)
        self.assertGreater(lead, 0.0)

    def test_lead_in_flag_keeps_the_build_up(self):
        build = array.array("h", (round(300 * math.sin(i * 0.05)) for i in range(render.RATE // 2)))
        body = build + tone(0.5)
        kept, _ = render.render(body, {"leadIn": True})
        cut, lead = render.render(body, {})
        self.assertGreater(len(kept), len(cut))
        self.assertGreater(lead, 0.2)

    def test_fade_out_is_proportional_within_bounds(self):
        self.assertEqual(render.fade_out_seconds(0.1), render.FADE_OUT_MIN_SECONDS)
        self.assertAlmostEqual(render.fade_out_seconds(0.5), 0.06)
        self.assertEqual(render.fade_out_seconds(5.0), render.FADE_OUT_MAX_SECONDS)

    def test_loop_crossfade_is_bounded_and_joins_close_to_start(self):
        samples = tone(2.0)
        result, _ = render.render(samples, {"loop": True})
        self.assertLess(len(result), len(samples))
        self.assertLessEqual(len(result) / render.RATE, render.MAX_SECONDS)
        self.assertLess(abs(result[-1] - result[0]), 500)

    def test_unknown_source_rule_is_rejected(self):
        with self.assertRaises(render.RenderError):
            render.validate_rules({"maxSeconds": 1})

    def test_linear_normalisation_obeys_true_peak_ceiling(self):
        result, gain = render.normalise(array.array("h", [1000, -1000, 500]), -40, -10, -18)
        self.assertAlmostEqual(gain, 8.5)
        self.assertEqual(result[0], round(1000 * 10 ** (8.5 / 20)))

    def test_cue_settings_write_layers_and_audiokit_fields(self):
        cue = {"id": "weapon.fire", "gain": 0.9, "pitchRange": [0.97, 1.03], "loop": False,
               "distance": {"near": 3, "far": 40}, "priority": 120, "maxVoices": 6, "maxPerEmitter": 2, "minInterval": 0.5,
               "environment": True, "maxSeconds": 0.3, "bus": "weapon",
               "mix": {"self": {"spatial": "2d"}, "other": {"spatial": "3d"}}, "layers": [{"sources": []}]}
        single = module.cue_settings(cue, [["weapon.fire.ogg"]])
        self.assertEqual(single, {"gain": 0.9, "loop": False, "priority": 120, "maxVoices": 6,
                                  "maxPerEmitter": 2, "minInterval": 0.5, "environment": True, "maxSeconds": 0.3,
                                  "bus": "weapon", "pitch": [0.97, 1.03], "space": {"near": 3, "far": 40},
                                  "place": {"self": "2d", "other": "auto"}, "layers": [{"clips": ["weapon.fire.ogg"]}]})


    def test_layers_keep_their_own_clips_and_settings(self):
        cue = {"id": "boom", "layers": [{"sources": [], "distance": {"max": 40}, "gainDb": -3},
                                        {"sources": [], "offsetMs": 880, "fadeOutMs": 80, "distance": {"min": 40}, "lowpassHz": 2000}]}
        layers = module.cue_settings(cue, [["boom-l1-01.ogg", "boom-l1-02.ogg"], ["boom-l2-01.ogg"]])["layers"]
        self.assertEqual(layers[0], {"clips": ["boom-l1-01.ogg", "boom-l1-02.ogg"], "gainDb": -3, "distance": {"max": 40}})
        self.assertEqual(layers[1], {"clips": ["boom-l2-01.ogg"], "offsetMs": 880, "fadeOutMs": 80, "distance": {"min": 40}, "lowpassHz": 2000})

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

    def test_exact_cue_needs_section_length_and_loop_one_layer(self):
        loose = {"file": "a.wav", "sha256": "x", "pack": "p", "licence": "l"}
        for cue, message in (
                ({"exact": True, "layers": [{"sources": [loose]}]}, "seconds"),
                ({"exact": True, "loop": True, "layers": [{"sources": [loose | {"seconds": 1}]}] * 2}, "one layer"),
                ({"layers": [{"sources": [loose | {"targetLufs": -18, "gainDb": 1}]}]}, "exact"),
                ({"layers": [{"sources": [loose | {"targetLufs": -18}], "pitch": 1, "bogus": 1}]}, "bogus")):
            with self.assertRaisesRegex(module.StageError, message):
                module.validate_document(self.document([{"id": "a", "bus": "b"} | cue]))
        module.validate_document(self.document([{"id": "a", "bus": "b", "exact": True, "layers": [{"sources": [loose | {"seconds": 1, "gainDb": 2}]}]}]))

    def test_exact_section_is_kept_as_cut_with_only_the_recorded_gain(self):
        with self.assertRaises(render.RenderError):
            render.validate_rules({"exact": True, "leadIn": True})
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "dual.wav"
            with wave.open(str(path), "wb") as audio:
                audio.setnchannels(2)
                audio.setsampwidth(2)
                audio.setframerate(render.RATE)
                audio.writeframes(struct.pack("<hh", 0, 0) * 4800 + struct.pack("<hh", 16384, 16384) * 4800)
            samples, info = render.render_source(path, 0.0, 0.2, {"exact": True}, gain_db=-6.0)
        self.assertEqual(len(samples), 9600, "no trimming: the leading silence stays")
        self.assertEqual(samples[0], 0)
        self.assertAlmostEqual(samples[-1] / 32768, 0.5 * 10 ** (-6 / 20), places=2, msg="dual mono is averaged, not summed, then the gain applies")
        self.assertAlmostEqual(info.gain_db, -6.0, places=2)

    def test_lift_raises_a_quiet_section_without_passing_the_ceiling_and_never_lowers(self):
        import math
        tone = array.array("h", (round(0.02 * 32768 * math.sin(i * 0.3)) for i in range(render.RATE)))
        loud = array.array("h", (round(0.8 * 32768 * math.sin(i * 0.3)) for i in range(render.RATE)))
        lifted, gain = render.lift(tone, -14.0)
        self.assertGreater(gain, 11.0)
        self.assertLessEqual(max(abs(v) for v in lifted) / 32768, render.LIFT_CEILING + 0.02)
        same, none = render.lift(loud, -30.0)
        self.assertEqual((none, same), (0.0, loud))
        self.assertEqual(render.lift_target({"exact": True}, {}), render.ONE_SHOT_LUFS)
        self.assertEqual(render.lift_target({"exact": True}, {}, layered=True), render.LAYERED_LUFS)
        self.assertIsNone(render.lift_target({"exact": True, "loop": True}, {}))
        self.assertIsNone(render.lift_target({"exact": True}, {"targetLufs": False}))
        self.assertEqual(render.lift_target({"exact": True, "loop": True}, {"targetLufs": -20}), -20.0)
        self.assertIsNone(render.lift_target({}, {"targetLufs": -20}))


if __name__ == "__main__":
    unittest.main(verbosity=2)
