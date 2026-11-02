"""Offline checks for the Frames audio source contract."""
import importlib.util
import json
from pathlib import Path
import re
import tempfile
import unittest


SCRIPT = Path(__file__).parents[2] / "tools/prepare_audio.py"
SPEC = importlib.util.spec_from_file_location("frames_audio", SCRIPT)
audio = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audio)

PACKAGE = Path(__file__).parents[2]


def recorded_document() -> dict:
    return json.loads((PACKAGE / "Assets/audio/sources.json").read_text(encoding="utf-8"))


def recorded_sources() -> list[dict]:
    return recorded_document()["sources"]


class AudioChecks(unittest.TestCase):
    def test_changed_source_file_is_refused(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as directory:
            clip = Path(directory) / "clip.wav"
            clip.write_bytes(b"clip")
            sources = [{"cue": "exo.jump", "path": str(clip), "sha256": audio.digest(clip)}]
            audio.verify_sources(sources)
            clip.write_bytes(b"changed")
            with self.assertRaisesRegex(RuntimeError, "changed since"):
                audio.verify_sources(sources)

    def test_every_source_is_fully_recorded(self):
        sources = recorded_sources()
        ids = [source["id"] for source in sources]
        self.assertEqual(len(ids), len(set(ids)), "source ids are unique")
        for source in sources:
            for field in ("id", "cue", "layer", "path", "pack", "license", "sha256", "duration"):
                self.assertTrue(source.get(field), source.get("id", "?") + " lacks " + field)
            self.assertTrue(source["id"].startswith(audio.PREFIX + source["cue"] + "-"), source["id"] + " is named after its cue")

    # Where the next footfall of each footstep layer's recording lands (found by onset analysis of the <sound library> file):
    # a footstep keeps its whole tail but must end before it, or the step sounds twice ("pa-pa") for every game step.
    NEXT_FOOTFALL = {1: 2.16, 2: 18.26, 3: 14.18, 4: 14.37}

    def test_a_footstep_is_one_footfall_with_its_whole_tail(self):
        document = recorded_document()
        steps = [source for source in document["sources"] if source["cue"] == "exo.footstep"]
        self.assertEqual(len(steps), 4)
        for source in steps:
            end = source["start"] + source["duration"]
            self.assertLessEqual(end, self.NEXT_FOOTFALL[source["layer"]], source["id"] + " must end before the recording's next footfall")
        self.assertGreaterEqual(max(source["duration"] for source in steps), 0.9, "the main layers keep the whole decay, not a cut-off")
        for layer in document["cues"]["exo.footstep"]["layers"]:
            self.assertGreater(layer["fadeOutMs"], 0, "a layer ends in a fade, not a hard cut")

    def test_footsteps_overlap_without_cutting_each_other(self):
        # A step sounds up to StepSeconds and steps come at most every MinStepGap: the step bus holds that many at once.
        gait = (PACKAGE / "Core/Gait.cs").read_text(encoding="utf-8")
        gap = float(re.search(r"MinStepGap = ([0-9.]+)f", gait).group(1))
        seconds = float(re.search(r"StepSeconds = ([0-9.]+)f", gait).group(1))
        sounding = int(re.search(r"MaxStepsSounding = (\d+)", gait).group(1))
        longest = max(source["duration"] for source in recorded_sources() if source["cue"] == "exo.footstep") * 1.04
        self.assertLessEqual(longest, seconds)
        self.assertGreaterEqual(sounding, -(-seconds // gap))
        cue = audio.cue_table(recorded_document())["exo.footstep"]
        self.assertEqual(audio.BUSES[cue["bus"]]["seats"], sounding)
        self.assertGreaterEqual(cue["maxPerEmitter"], sounding)
    def test_cues_match_the_sound_table(self):
        table = re.findall(r'FrameSound\.\w+, "((?:exo|ui)\.[a-z_]+)"', (PACKAGE / "Core/FrameSounds.cs").read_text(encoding="utf-8"))
        document = json.loads((PACKAGE / "Assets/audio/cues.json").read_text(encoding="utf-8"))
        cues = document["cues"]
        self.assertEqual(cues, audio.cue_table(recorded_document()), "the tracked cues.json is what prepare_audio writes")
        self.assertEqual(document["buses"], audio.BUSES, "the tracked buses are what prepare_audio writes")
        for name, settings in cues.items():
            self.assertIn(settings["bus"], document["buses"], name + " plays on a declared bus")
        self.assertEqual(sorted(table), sorted(cues), "every table sound has a cue and every cue has a sound")
        for name, settings in cues.items():
            self.assertEqual(settings.get("loop", False), "maxSeconds" not in settings, name + ": one-shots are capped, loops are not")
        self.assertEqual(cues["exo.armor_hit"]["minInterval"], .12, "armour hits are rate limited")
        for gone in ("exo.enter", "exo.exit", "exo.eject", "ui.hold_charge", "ui.denied",
                     "exo.latch_pop", "exo.steam_vent"):
            self.assertNotIn(gone, cues)
        recorded = {source["cue"] for source in recorded_sources()}
        self.assertEqual(recorded, set(cues), "no source is recorded for a cue that does not exist")

    def test_no_gain_is_out_of_ForgeAudio_range(self):
        # ForgeAudio rejects a cue whose gainDb is outside -60..+12 (InvalidSettings), so the whole cue would not load in game.
        def out_of_range(node, where):
            if isinstance(node, dict):
                for key, value in node.items():
                    if key == "gainDb" and isinstance(value, (int, float)) and not -60 <= value <= 12:
                        yield f"{where} gainDb {value}"
                    yield from out_of_range(value, f"{where}/{key}")
            elif isinstance(node, list):
                for index, value in enumerate(node):
                    yield from out_of_range(value, f"{where}/{index}")
        bad = []
        for path in sorted(PACKAGE.parent.glob("*/Assets/[Aa]udio/*.json")):
            bad += list(out_of_range(json.loads(path.read_text(encoding="utf-8")), path.parent.parent.parent.name + "/" + path.name))
        self.assertEqual(bad, [], "gainDb stays within -60..+12")

    def test_suit_cue_has_explicit_mix(self):
        name, settings = audio.cue("exo.test", "sfx", 1, False, 1, 2, 3, gap=.5)
        self.assertEqual((name, settings["place"], settings["minInterval"]), ("exo.test", {"self": "2d", "other": "auto"}, .5))
        self.assertEqual(audio.asset_file("forgeordnance.frames:exo.test-a"), "exo.test-a.ogg")


if __name__ == "__main__":
    unittest.main(verbosity=2)
