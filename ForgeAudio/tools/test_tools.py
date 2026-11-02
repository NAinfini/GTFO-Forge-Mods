from __future__ import annotations

import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import struct
import tempfile
import unittest
from unittest import mock
import wave


TOOLS = Path(__file__).resolve().parent


def load(name: str):
    spec = importlib.util.spec_from_file_location(name, TOOLS / (name + ".py"))
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


convert_audio = load("convert_audio")
package_release = load("package_release")


def write_wav(path: Path) -> None:
    with wave.open(str(path), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(8000)
        audio.writeframes(b"\x00\x00\x00\x10\x00\xf0" * 400)


def write_package(folder: Path, dependencies: list[str] = (package_release.BEPINEX_DEPENDENCY, "NAinfini-ForgeCore-0.1.0"), version: str = "1.0.0") -> None:
    folder.mkdir()
    (folder / "manifest.json").write_text(
        json.dumps(
            {
                "name": "ForgeAudio",
                "version_number": version,
                "website_url": "https://github.com/NAinfini/GTFO-Forge-Mods",
                "description": "Plain-audio playback for GTFO mods.",
                "dependencies": dependencies,
            }
        ),
        encoding="utf-8",
    )
    vendor = folder / "vendor" / "NVorbis"
    vendor.mkdir(parents=True)
    (vendor / "LICENSE").write_text("MIT test license", encoding="utf-8")
    (vendor / "NOTICE.md").write_text("test provenance", encoding="utf-8")
    (folder.parent / "NVorbis.dll").write_bytes(b"test decoder")
    (folder / "README.md").write_text("readme\n", encoding="utf-8")
    (folder / "DEVELOPERS.md").write_text("public API\n", encoding="utf-8")
    (folder / "CHANGELOG.md").write_text("changes\n", encoding="utf-8")
    (folder / "LICENSE").write_text("MIT test license\n", encoding="utf-8")
    (folder / "web-manifest.json").write_text('{"library": "ForgeAudio"}\n', encoding="utf-8")
    (folder / "ForgeAudio.csproj").write_text(
        "<Project><PropertyGroup><Version>" + version + "</Version></PropertyGroup></Project>", encoding="utf-8"
    )
    (folder / "icon.png").write_bytes(
        b"\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDR" + struct.pack(">II", 256, 256) + b"\x08\x06\x00\x00\x00\x00\x00\x00\x00"
    )


class ConvertChecks(unittest.TestCase):
    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "ffmpeg and ffprobe are required")
    def test_convert_wav_to_vorbis_with_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, output, evidence = root / "source.wav", root / "result.ogg", root / "result.json"
            write_wav(source)
            with contextlib.redirect_stdout(io.StringIO()):
                code = convert_audio.main(
                    ["convert", str(source), str(output), "--evidence", str(evidence), "--timeout", "20"]
                )
            self.assertEqual(code, 0)
            self.assertEqual(output.read_bytes()[:4], b"OggS")
            receipt = json.loads(evidence.read_text(encoding="utf-8"))
            self.assertEqual(receipt["input"]["codec"], "pcm_s16le")
            self.assertEqual(receipt["output"]["codec"], "vorbis")
            self.assertEqual(receipt["output"]["sha256"], hashlib.sha256(output.read_bytes()).hexdigest())
            self.assertFalse(receipt["settings"]["normalization"])
            self.assertFalse(receipt["settings"]["truncated"])

    def test_existing_output_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, output = root / "source.wav", root / "result.ogg"
            write_wav(source)
            output.write_bytes(b"keep")
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(convert_audio.main(["convert", str(source), str(output)]), 2)
            self.assertEqual(output.read_bytes(), b"keep")

    def test_extension_must_match_detected_codec(self):
        metadata = {"codec": "mp3", "durationSeconds": 1.0, "sampleRate": 48000, "channels": 1}
        with self.assertRaisesRegex(convert_audio.ToolError, "PCM or ADPCM"):
            convert_audio.validate_source(Path("false.wav"), metadata, 60, 2, 48000, 1_000_000)

    def test_each_resource_limit_is_enforced(self):
        base = {"codec": "vorbis", "durationSeconds": 1.0, "sampleRate": 48000, "channels": 1}
        cases = (
            ({**base, "durationSeconds": 61}, (60, 2, 48000, 10_000_000), "max-seconds"),
            ({**base, "channels": 3}, (60, 2, 48000, 10_000_000), "max-channels"),
            ({**base, "sampleRate": 7999}, (60, 2, 48000, 10_000_000), "runtime minimum"),
            ({**base, "sampleRate": 96000}, (60, 2, 48000, 10_000_000), "max-sample-rate"),
            (base, (60, 2, 48000, 100), "max-decoded-bytes"),
        )
        for metadata, limits, error in cases:
            with self.subTest(error=error), self.assertRaisesRegex(convert_audio.ToolError, error):
                convert_audio.validate_source(Path("source.ogg"), metadata, *limits)

    def test_author_limits_cannot_relax_runtime_limits(self):
        cases = (
            ("--max-seconds", "3601", "3600"),
            ("--max-channels", "3", "limit of 2"),
            ("--max-sample-rate", "192001", "192000"),
            ("--max-output-bytes", str(64 * 1024 * 1024 + 1), "67108864"),
            ("--max-decoded-bytes", str(512 * 1024 * 1024 + 1), "536870912"),
        )
        for option, value, error in cases:
            with self.subTest(option=option):
                args = convert_audio.parser().parse_args(["convert", "in.wav", "out.ogg", option, value])
                with self.assertRaisesRegex(convert_audio.ToolError, error):
                    convert_audio.validate_limits(args)

    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "ffmpeg and ffprobe are required")
    def test_compressed_output_limit_leaves_no_partial_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, output = root / "source.wav", root / "result.ogg"
            write_wav(source)
            with contextlib.redirect_stderr(io.StringIO()):
                code = convert_audio.main(
                    ["convert", str(source), str(output), "--max-output-bytes", "1", "--timeout", "20"]
                )
            self.assertEqual(code, 2)
            self.assertFalse(output.exists())

    def test_atomic_publish_rolls_back_own_output_and_preserves_racing_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output_temp, receipt_temp = root / "output.tmp", root / "receipt.tmp"
            output, receipt = root / "output.ogg", root / "receipt.json"
            output_temp.write_bytes(b"our output")
            receipt_temp.write_bytes(b"our receipt")
            real_link = convert_audio.os.link

            def race(source, target):
                if Path(target) == receipt:
                    receipt.write_bytes(b"other process")
                    raise FileExistsError(str(receipt))
                real_link(source, target)

            with mock.patch.object(convert_audio.os, "link", side_effect=race):
                with self.assertRaisesRegex(convert_audio.ToolError, "created concurrently"):
                    convert_audio.publish_without_replacing(
                        [(output_temp, output, "output"), (receipt_temp, receipt, "evidence")]
                    )
            self.assertFalse(output.exists())
            self.assertEqual(receipt.read_bytes(), b"other process")

    def test_validate_forwards_folder_and_exit_code(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            audio, project = root / "audio", root / "Validate.csproj"
            audio.mkdir()
            project.write_text("<Project />", encoding="utf-8")
            completed = mock.Mock(returncode=7)
            with mock.patch.object(convert_audio, "VALIDATOR", project), mock.patch.object(
                convert_audio.subprocess, "run", return_value=completed
            ) as invoked:
                self.assertEqual(convert_audio.validate(argparse.Namespace(folder=audio)), 7)
            command = invoked.call_args.args[0]
            self.assertEqual(command[command.index("-c") + 1], "Release")
            self.assertIn("--disable-build-servers", command)
            self.assertEqual(command[command.index("--artifacts-path") + 1], str(convert_audio.VALIDATOR_ARTIFACTS))
            self.assertEqual(command[-1], str(audio.resolve()))

    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe") and shutil.which("dotnet"), "ffmpeg, ffprobe and dotnet are required")
    def test_folder_conversion_is_loadable_by_the_runtime(self):
        """The author path end to end: a folder of WAVs becomes audio/<name>.ogg that the runtime decoder accepts."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sources, audio = root / "sounds", root / "audio"
            (sources / "ui").mkdir(parents=True)
            write_wav(sources / "shot.wav")
            write_wav(sources / "ui" / "click.wav")
            (sources / "notes.txt").write_text("ignored", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(convert_audio.main(["convert", str(sources), str(audio), "--timeout", "20"]), 0)
            self.assertEqual(sorted(p.relative_to(audio).as_posix() for p in audio.rglob("*.ogg")), ["shot.ogg", "ui/click.ogg"])
            first = (audio / "shot.ogg").read_bytes()
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(convert_audio.main(["convert", str(sources), str(audio), "--timeout", "20"]), 0)
            report = json.loads(output.getvalue())
            self.assertEqual((report["converted"], sorted(report["kept"])), ([], ["shot.ogg", "ui/click.ogg"]))
            self.assertEqual((audio / "shot.ogg").read_bytes(), first)
            # The author names each cue's bus and each bus's global category.
            (audio / "cues.json").write_text(json.dumps({
                "buses": {"gun": {"category": "weapon"}, "menu": {"category": "ui"}},
                "cues": {"shot": {"bus": "gun"}, "ui/click": {"bus": "menu"}},
            }), encoding="utf-8")
            completed = convert_audio.subprocess.run(
                ["dotnet", "run", "--project", str(convert_audio.VALIDATOR), "-c", "Release", "--disable-build-servers",
                 "--artifacts-path", str(convert_audio.VALIDATOR_ARTIFACTS), "--", str(audio)],
                capture_output=True, text=True, encoding="utf-8", errors="replace", check=False,
            )
            self.assertEqual(completed.returncode, 0, completed.stderr + completed.stdout)
            result = json.loads(completed.stdout.strip().splitlines()[-1])
            self.assertEqual((result["status"], result["cues"]), ("valid", ["shot", "ui/click"]))

    def test_folder_conversion_reports_each_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sources = root / "sounds"
            sources.mkdir()
            (sources / "broken.wav").write_bytes(b"not audio")
            with mock.patch.object(convert_audio, "convert_file", side_effect=convert_audio.ToolError("bad input")):
                with self.assertRaisesRegex(convert_audio.ToolError, "broken.wav.*bad input"):
                    convert_audio.convert(convert_audio.parser().parse_args(["convert", str(sources), str(root / "audio")]))


class PackageChecks(unittest.TestCase):
    def test_missing_icon_reports_brand_gap(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory) / "package"
            write_package(folder)
            (folder / "icon.png").unlink()
            with self.assertRaisesRegex(ValueError, "dedicated 256 x 256"):
                package_release.check_package(folder)

    def test_only_bepinex_and_forgecore_dependencies_are_accepted(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory) / "package"
            write_package(folder, [package_release.BEPINEX_DEPENDENCY, "NAinfini-ForgeCore-0.1.0", "NAinfini-ForgeUI-0.1.0"])
            with self.assertRaisesRegex(ValueError, "only BepInEx and ForgeCore"):
                package_release.check_package(folder)

    def test_package_is_deterministic_and_contains_only_release_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder, output, dll = root / "package", root / "out", root / "ForgeAudio.dll"
            write_package(folder)
            dll.write_bytes(b"MZtest assembly")
            with mock.patch.object(package_release, "assembly_version", return_value="1.0.0"):
                with contextlib.redirect_stdout(io.StringIO()):
                    first = package_release.package(folder, dll, output)
                digest = hashlib.sha256(first.read_bytes()).digest()
                with contextlib.redirect_stdout(io.StringIO()):
                    second = package_release.package(folder, dll, output)
            self.assertEqual(digest, hashlib.sha256(second.read_bytes()).digest())
            with package_release.zipfile.ZipFile(second) as archive:
                self.assertEqual(archive.read("web-manifest.json"), (folder / "web-manifest.json").read_bytes())
                self.assertEqual(archive.read("NVorbis.dll"), (root / "NVorbis.dll").read_bytes())
                self.assertEqual(archive.read("NVorbis.LICENSE"), (folder / "vendor" / "NVorbis" / "LICENSE").read_bytes())
                self.assertEqual(
                    sorted(archive.namelist()),
                    [
                        "CHANGELOG.md",
                        "DEVELOPERS.md",
                        "ForgeAudio.dll",
                        "LICENSE",
                        "NVorbis.LICENSE",
                        "NVorbis.NOTICE.md",
                        "NVorbis.dll",
                        "README.md",
                        "icon.png",
                        "manifest.json",
                        "web-manifest.json",
                    ],
                )

    def test_missing_decoder_refuses_package(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder, dll = root / "package", root / "ForgeAudio.dll"
            write_package(folder)
            dll.write_bytes(b"MZtest assembly")
            (root / "NVorbis.dll").unlink()
            with mock.patch.object(package_release, "assembly_version", return_value="1.0.0"):
                with self.assertRaises(FileNotFoundError):
                    package_release.package(folder, dll, root / "out")

    def test_project_and_dll_must_match_manifest_version(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder, dll = root / "package", root / "ForgeAudio.dll"
            write_package(folder)
            dll.write_bytes(b"MZtest assembly")
            with mock.patch.object(package_release, "assembly_version", return_value="1.0.1"):
                with self.assertRaisesRegex(ValueError, "version mismatch"):
                    package_release.package(folder, dll, root / "out")

    def test_project_version_must_be_unambiguous(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory) / "project.csproj"
            project.write_text(
                "<Project><PropertyGroup><Version>1.0.0</Version><Version>1.0.1</Version></PropertyGroup></Project>",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "unambiguous"):
                package_release.project_version(project)


if __name__ == "__main__":
    unittest.main(verbosity=2)
