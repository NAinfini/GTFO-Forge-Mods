"""Tiny, explicitly synthetic packages for integrity/installer tests, not game evidence."""
import importlib.util
from pathlib import Path
import shutil

TOOLS = Path(__file__).absolute().parents[1] / "tools"
spec = importlib.util.spec_from_file_location("probe_package_test_subject", TOOLS / "verify-probe-package.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


def populate(root: Path) -> None:
    for folder in ("tools", "probes/commands", "probes/trace"):
        (root / folder).mkdir(parents=True, exist_ok=True)
    for name in ("development_paths.py", "install-probes.py", "verify-probe-package.py"):
        shutil.copyfile(TOOLS / name, root / "tools" / name)
    for name in ("rec-merge.py", "rec-query.py"):
        (root / "tools" / name).write_text("# Integrity fixture placeholder; no recorder execution.\n", encoding="utf-8", newline="\n")
    (root / "probes/capture-readbacks.forge").write_text('{"format":"forge-capture-readbacks.v1"}\n', encoding="utf-8", newline="\n")


def seal(root: Path) -> None:
    (root / package.MANIFEST).unlink(missing_ok=True)
    package.create_manifest(root)


if __name__ == "__main__":
    import sys
    root = Path(sys.argv[1]).absolute()
    if root.exists():
        raise SystemExit("Fixture destination must not exist")
    populate(root)
    (root / "probes/trace/fixture.forge").write_text('{"profile":"fixture","entries":[]}', encoding="utf-8", newline="\n")
    (root / "probes/commands/fixture.forge").write_text('[{"id":"fixture","steps":[]}]', encoding="utf-8", newline="\n")
    seal(root)
    print("Synthetic integrity fixture only; no game acceptance")
