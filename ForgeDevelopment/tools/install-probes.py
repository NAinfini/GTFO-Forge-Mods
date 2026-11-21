#!/usr/bin/env python3
"""Preview or explicitly install shipped JSON into the directories the plugin loads.

Updating the plugin does not replace existing config. Run without --apply first;
--replace-existing explicitly permits replacing conflicting shipped names and
backs up their exact old bytes. Unrelated user files are never removed.
"""
from __future__ import annotations

import argparse
import hashlib
import runpy
from types import SimpleNamespace
import json
from pathlib import Path
import stat
import re
import sys
import tempfile
from development_paths import development_root


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def unlinked(path: Path) -> None:
    for part in (path, *path.parents):
        attributes = part.lstat().st_file_attributes if part.exists() and hasattr(part.lstat(), "st_file_attributes") else 0
        if part.is_symlink() or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0):
            raise ValueError(f"refusing symlink: {part}")


RECEIPT_FORMAT = "forge-probe-install"
# The receipt written before the format dropped its ".v1" suffix: a tool product, not user data.
OLD_RECEIPT_FORMAT = "forge-probe-install.v1"


def recycle(path: Path) -> None:
    """Send one file to the Windows Recycle Bin (recoverable), never a permanent delete."""
    import ctypes
    from ctypes import wintypes

    class FileOperation(ctypes.Structure):
        _fields_ = [("hwnd", wintypes.HWND), ("wFunc", wintypes.UINT), ("pFrom", wintypes.LPCWSTR), ("pTo", wintypes.LPCWSTR),
                    ("fFlags", ctypes.c_uint16), ("fAnyOperationsAborted", wintypes.BOOL), ("hNameMappings", ctypes.c_void_p),
                    ("lpszProgressTitle", wintypes.LPCWSTR)]

    # FO_DELETE with FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT; pFrom is double-NUL terminated.
    operation = FileOperation(None, 3, str(path.absolute()) + "\0", None, 0x40 | 0x10 | 0x400 | 0x4, False, None, None)
    if ctypes.windll.shell32.SHFileOperationW(ctypes.byref(operation)) or operation.fAnyOperationsAborted or path.exists():
        raise ValueError(f"could not move to the Recycle Bin: {path}")


def read_json(path: Path) -> tuple[bytes, object]:
    unlinked(path)
    data = path.read_bytes()
    return data, json.loads(data.decode("utf-8-sig"))


def inventory(root: Path) -> dict[str, bytes]:
    files: dict[str, bytes] = {}
    for folder in ("trace", "commands"):
        directory = root / folder
        unlinked(directory)
        if not directory.is_dir():
            raise ValueError(f"missing {directory}")
        paths = json_paths(directory)
        if not paths:
            raise ValueError(f"no JSON files in {directory}")
        for path in paths:
            data, _ = read_json(path)
            files[f"{folder}/{path.name}"] = data
    return files


def json_paths(directory: Path) -> list[Path]:
    paths = sorted(path for path in directory.iterdir() if path.suffix.lower() == ".forge") if directory.exists() else []
    folded: dict[str, Path] = {}
    for path in paths:
        if path.name.casefold() in folded:
            raise ValueError(f"Windows filename collision: {folded[path.name.casefold()]}, {path}")
        folded[path.name.casefold()] = path
    return paths


def check_identities(files: dict[str, bytes]) -> None:
    # Preserve custom files, but do not silently introduce duplicate profile or
    # command identities into the same directories the production loaders read.
    seen: dict[tuple[str, str], str] = {}
    for name, data in sorted(files.items()):
        value = json.loads(data.decode("utf-8-sig"))
        folder = name.split("/", 1)[0]
        if folder == "trace":
            if not isinstance(value, dict):
                raise ValueError(f"invalid trace profile: {name}")
            # RecTracer.Parse falls back to the filename when profile is omitted
            # or is not a string, and permits default/empty entry configurations.
            profile = value.get("profile")
            identities = [profile if isinstance(profile, str) else Path(name).stem]
        else:
            rows = value if isinstance(value, list) else [value]
            if not rows or any(not isinstance(row, dict) or not isinstance(row.get("steps"), list) for row in rows):
                raise ValueError(f"invalid command file: {name}")
            identities = [row.get("id") for row in rows]
        for identity in identities:
            if not isinstance(identity, str) or (folder == "commands" and not identity.strip()):
                raise ValueError(f"missing {folder} identity: {name}")
            key = (folder, identity)
            if key in seen:
                raise ValueError(f"duplicate {folder} identity {identity}: {seen[key]}, {name}")
            seen[key] = name


def run(args: argparse.Namespace) -> dict:
    # Do not resolve away a symlink before checking it.
    source = Path(args.probes_root).absolute()
    bep = Path(args.bepinex_root).absolute()
    unlinked(source)
    unlinked(bep)
    if not bep.is_dir() or bep.name.lower() != "bepinex":
        raise ValueError("--bepinex-root must name the existing BepInEx directory")
    target = development_root(bep)
    unlinked(target)
    package_tool = Path(__file__).absolute().with_name("verify-probe-package.py")
    unlinked(package_tool)
    # run_path compiles in memory. An importlib loader would write __pycache__
    # inside this sealed package when the operator omits Python's optional -B flag.
    package = SimpleNamespace(**runpy.run_path(str(package_tool)))
    package_receipt = package.verify(source.parent)
    if source.name != "probes":
        raise ValueError("--probes-root must be the verified package's probes directory")
    shipped = inventory(source)
    receipt_path = target / "probe-install-receipt.forge"
    unlinked(receipt_path)
    previous = {}
    old_receipt = False
    if receipt_path.exists():
        receipt = package.read_json(receipt_path)
        old_receipt = isinstance(receipt, dict) and receipt.get("format") == OLD_RECEIPT_FORMAT
    if receipt_path.exists() and not old_receipt:
        if not isinstance(receipt, dict) or receipt.get("format") != RECEIPT_FORMAT or not isinstance(receipt.get("files"), dict):
            raise ValueError("invalid existing install receipt; preserve it and resolve manually")
        previous = receipt["files"]
        for name, value in previous.items():
            if not re.fullmatch(r"(?:trace|commands)/[^/\\]+\.forge", name) or not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value):
                raise ValueError("invalid file identity in existing install receipt")
            package.relative_name(name)
    merged: dict[str, bytes] = {}
    for folder in ("trace", "commands"):
        directory = target / folder
        unlinked(directory)
        for path in json_paths(directory):
            merged[f"{folder}/{path.name}"] = read_json(path)[0]
    folded = {name.casefold(): name for name in merged}
    for name in shipped:
        if name.casefold() in folded and folded[name.casefold()] != name:
            raise ValueError(f"Windows filename collision between shipped and config: {name}, {folded[name.casefold()]}")
    changes = []
    for name, data in sorted(shipped.items()):
        path = target / name
        unlinked(path)
        old = path.read_bytes() if path.exists() else None
        action = "same" if old == data else "create" if old is None else "replace"
        changes.append({"path": str(path), "relative": name, "action": action,
                        "sourceSha256": digest(data), "previousSha256": digest(old) if old is not None else None})
        merged[name] = data
    retired = []
    for name, expected in sorted(previous.items()):
        if name in shipped:
            continue
        path = target / name
        unlinked(path)
        if not path.exists():
            continue
        current = digest(path.read_bytes())
        retired.append({"relative": name, "path": str(path), "previousSha256": expected,
                        "currentSha256": current, "action": "retire" if current == expected else "retired-customized"})
        # Check the projected post-upgrade set, never count a retiring command as
        # a collision with its replacement. Modified old files are not removed.
        if current == expected:
            merged.pop(name, None)
    if args.check and retired:
        raise ValueError("retired installed resources remain: " + ", ".join(row["relative"] for row in retired))
    if args.apply and any(row["action"] == "retired-customized" for row in retired):
        raise ValueError("retired files contain user edits; move or resolve them manually before upgrading: "
                         + ", ".join(row["relative"] for row in retired if row["action"] == "retired-customized"))
    if args.apply and retired and not args.prune_retired:
        raise ValueError("retired installed resources require explicit --prune-retired; matching old bytes will be backed up: "
                         + ", ".join(row["relative"] for row in retired))
    check_identities(merged)
    replacements = [row for row in changes if row["action"] == "replace"]
    if args.check:
        differences = [row["relative"] for row in changes if row["action"] != "same"]
        if differences:
            raise ValueError("loaded config differs from shipped JSON: " + ", ".join(differences))
    if args.apply and replacements and not args.replace_existing:
        raise ValueError("existing config differs; preview then explicitly use --replace-existing (old bytes are backed up): "
                         + ", ".join(row["relative"] for row in replacements))
    backup = None
    if args.apply:
        target.mkdir(parents=True, exist_ok=True)
        if replacements or retired:
            backup = Path(tempfile.mkdtemp(prefix="probe-backup-", dir=target))
            for row in replacements:
                saved = backup / row["relative"]
                saved.parent.mkdir(parents=True, exist_ok=True)
                saved.write_bytes((target / row["relative"]).read_bytes())
            for row in retired:
                path = target / row["relative"]
                if digest(path.read_bytes()) != row["previousSha256"]:
                    raise ValueError(f"retired file changed during install; preserved: {row['relative']}")
                saved = backup / row["relative"]
                saved.parent.mkdir(parents=True, exist_ok=True)
                path.replace(saved)
        for row in changes:
            if row["action"] == "same":
                continue
            path = target / row["relative"]
            path.parent.mkdir(parents=True, exist_ok=True)
            # One atomic file replacement; old conflicting bytes are already saved.
            with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".probe-", delete=False) as stream:
                temporary = Path(stream.name)
                stream.write(shipped[row["relative"]])
            try:
                temporary.replace(path)
            finally:
                temporary.unlink(missing_ok=True)
        for row in changes:
            if digest((target / row["relative"]).read_bytes()) != row["sourceSha256"]:
                raise ValueError(f"installed hash mismatch: {row['relative']}")
        if old_receipt:
            recycle(receipt_path)
        receipt = {"format": RECEIPT_FORMAT, "packageManifestSha256": package_receipt["manifestSha256"],
                   "files": {name: digest(data) for name, data in sorted(shipped.items())}}
        with tempfile.NamedTemporaryFile(dir=target, prefix=".probe-receipt-", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write((json.dumps(receipt, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
        try:
            temporary.replace(receipt_path)
        finally:
            temporary.unlink(missing_ok=True)
    unmanaged = sorted(name for name in merged if name not in shipped and name not in previous)
    return {"mode": "check" if args.check else "apply" if args.apply else "preview",
            "loadedTraceDirectory": str(target / "trace"), "loadedCommandDirectory": str(target / "commands"),
            "backupDirectory": str(backup) if backup else None,
            "restartRequired": bool(args.apply), "gameLoadVerified": False, "files": changes,
            "retired": retired, "unmanagedFiles": unmanaged, "packageVerified": package_receipt,
            "oldReceipt": "recycled" if old_receipt and args.apply else "found" if old_receipt else None,
            "retirementEvidence": "previous-install-receipt" if previous else "no-previous-receipt; unrelated files preserved"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--probes-root", default=str(Path(__file__).absolute().parent.parent / "probes"))
    parser.add_argument("--bepinex-root", required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--apply", action="store_true")
    mode.add_argument("--check", action="store_true")
    parser.add_argument("--replace-existing", action="store_true")
    parser.add_argument("--prune-retired", action="store_true", help="Back up/remove unmodified old resources identified by the previous install receipt")
    args = parser.parse_args()
    if (args.replace_existing or args.prune_retired) and not args.apply:
        parser.error("--replace-existing/--prune-retired require --apply")
    try:
        print(json.dumps(run(args), ensure_ascii=False, indent=2))
        return 0
    except (OSError, ValueError) as error:
        print(f"[fail] {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
