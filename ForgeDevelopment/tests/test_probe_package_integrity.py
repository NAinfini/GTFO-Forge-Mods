"""Offline integrity checks on extracted synthetic packages; no Native/game claims."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from probe_package_fixture import package, populate, seal


class ProbePackageIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="probe-package-integrity-")
        self.root = Path(self.temp.name) / "package"
        populate(self.root)
        (self.root / "probes/trace/a.forge").write_text('{"profile":"a","entries":[]}', encoding="utf-8", newline="\n")
        (self.root / "probes/commands/a.forge").write_text('[{"id":"a","steps":[]}]', encoding="utf-8", newline="\n")
        (self.root / "probes/trace/extra.forge").write_text('{"profile":"extra","entries":[]}', encoding="utf-8", newline="\n")
        seal(self.root)

    def tearDown(self):
        self.temp.cleanup()

    def invoke(self):
        cwd = Path(self.temp.name) / "unrelated"
        cwd.mkdir(exist_ok=True)
        return subprocess.run([sys.executable, "-B", str(self.root / "tools/verify-probe-package.py")], cwd=cwd,
                              text=True, capture_output=True)



    def test_every_resource_missing_or_modified_is_rejected(self):
        for path in sorted((self.root / "probes").rglob("*")):
            if not path.is_file():
                continue
            data = path.read_bytes()
            with self.subTest(path=path.name, action="delete"):
                path.unlink()
                self.assertNotEqual(self.invoke().returncode, 0)
                path.write_bytes(data)
            with self.subTest(path=path.name, action="change"):
                path.write_bytes(data + b" ")
                self.assertNotEqual(self.invoke().returncode, 0)
                path.write_bytes(data)

    def test_unexpected_resource_is_rejected(self):
        (self.root / "probes/trace/unknown.forge").write_text("{}", encoding="utf-8", newline="\n")
        self.assertIn("file set differs", self.invoke().stderr)

    def test_tool_corruption_is_rejected(self):
        target = self.root / "tools/rec-query.py"
        target.write_bytes(target.read_bytes() + b"# changed\n")
        self.assertIn("hash mismatch", self.invoke().stderr)

    def test_symlink_is_rejected(self):
        target = self.root / "probes/trace/extra.forge"
        data = target.read_bytes()
        target.unlink()
        outside = Path(self.temp.name) / "outside.forge"
        outside.write_bytes(data)
        try:
            target.symlink_to(outside)
        except (OSError, NotImplementedError) as error:
            if not isinstance(error, NotImplementedError) and getattr(error, "winerror", None) != 1314:  # only a missing symlink privilege skips; anything else fails the test
                raise
            self.skipTest(str(error))
        self.assertIn("symlink", self.invoke().stderr)

if __name__ == "__main__":
    unittest.main()
