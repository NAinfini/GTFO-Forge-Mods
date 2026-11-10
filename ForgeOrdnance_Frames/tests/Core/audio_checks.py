"""Offline checks for the Frames audio source contract."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


SCRIPT = Path(__file__).parents[2] / "tools/prepare_audio.py"
SPEC = importlib.util.spec_from_file_location("frames_audio", SCRIPT)
audio = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audio)

PACKAGE = Path(__file__).parents[2]


def recorded_document() -> dict:
    return json.loads((PACKAGE / "Assets/Audio/sources.forge").read_text(encoding="utf-8"))


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


if __name__ == "__main__":
    unittest.main(verbosity=2)
