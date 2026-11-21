"""Reads ForgeDevelopment recorder segments (BepInEx/ForgeReports/rec-*/rec-<session>-NNN.jsonl[.gz]).

Each segment starts with a header line {"v": "forge.rec", "segment": {...}} holding the context fields its records
share. A record line carries only what differs from the header, and names a header field it does not have in
"$absent". complete() rebuilds the record exactly as the recorder produced it. The open segment is plain JSONL that
grows while the game runs; once closed it is packed to .jsonl.gz and the plain file is removed, so a segment is
addressed by its logical .jsonl path and read from whichever file exists. Offsets are always into the logical
(unpacked) bytes.
"""

from __future__ import annotations

import gzip
import json
from pathlib import Path
from typing import Any, BinaryIO, Iterator

VERSION = "forge.rec"
ABSENT = "$absent"


def segments(directory: str | Path) -> list[Path]:
    """The logical segment paths of one session, in segment order, whether plain, packed or both."""
    names = set()
    if not Path(directory).is_dir():
        return []  # A session the game has not created yet has no segments.
    for path in Path(directory).iterdir():
        if path.name.endswith(".jsonl.gz"):
            names.add(path.name[:-3])
        elif path.name.endswith(".jsonl"):
            names.add(path.name)
    return [Path(directory) / name for name in sorted(names)]


def physical(path: str | Path) -> Path:
    """The file that holds a logical segment now: the plain file while it exists, else the packed one."""
    path = Path(path)
    if path.name.endswith(".gz"):
        return path
    return path if path.exists() else path.with_name(path.name + ".gz")


def open_segment(path: str | Path) -> BinaryIO:
    """A binary stream of the logical bytes. A plain file removed after packing is read from its packed copy."""
    path = Path(path)
    if path.name.endswith(".gz"):
        return gzip.open(path, "rb")
    try:
        return path.open("rb")
    except FileNotFoundError:
        return gzip.open(path.with_name(path.name + ".gz"), "rb")


def header(line: bytes | str) -> dict[str, Any]:
    """The shared fields of a segment's header line; raises ValueError for anything else."""
    value = json.loads(line)
    if not isinstance(value, dict) or value.get("v") != VERSION or not isinstance(value.get("segment"), dict) or "seq" in value:
        raise ValueError("segment header missing")
    return value["segment"]


def complete(row: dict[str, Any], shared: dict[str, Any]) -> dict[str, Any]:
    """The record as the recorder produced it: version, the segment's shared fields, then the line's own."""
    absent = row.get(ABSENT, ())
    value = {"v": VERSION}
    value.update((key, item) for key, item in shared.items() if key not in absent)
    value.update((key, item) for key, item in row.items() if key != ABSENT)
    return value


def read_header(path: str | Path) -> dict[str, Any]:
    with open_segment(path) as stream:
        return header(stream.readline())


def lines(path: str | Path) -> Iterator[tuple[int, bytes, dict[str, Any] | None]]:
    """(line number, raw record line, shared fields) for every record line after the header. The shared fields are
    None when the header itself is unreadable; the raw line is passed on so a caller can report it."""
    with open_segment(path) as stream:
        first = stream.readline()
        try:
            shared = header(first) if first else None
        except ValueError:
            shared = None
        for number, line in enumerate(stream, 2):
            yield number, line, shared


def records(path: str | Path) -> Iterator[dict[str, Any]]:
    """Every complete record of one segment; a line that is not a JSON object raises ValueError."""
    for number, line, shared in lines(path):
        if shared is None:
            raise ValueError(f"{Path(path).name}: segment header missing")
        if not line.strip():
            continue
        row = json.loads(line)
        if not isinstance(row, dict):
            raise ValueError(f"{Path(path).name}:{number}: not a record object")
        yield complete(row, shared)


def encode(record: dict[str, Any]) -> bytes:
    """One complete record as a standalone JSONL line, for an archive that must stand without its segment header."""
    return json.dumps(record, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
