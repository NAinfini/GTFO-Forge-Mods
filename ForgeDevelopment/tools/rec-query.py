#!/usr/bin/env python3
"""Query a ForgeDevelopment recorder session offline.

The recorder writes one JSONL record per line into a session directory (BepInEx/ForgeReports/rec-*), split into
segments that are packed to .jsonl.gz once closed (see rec_segments.py). This tool reads those sessions, filters them
and prints a table or JSON.
It never opens the game, never writes into the game's directory and never needs a third-party package.

Examples:
    python Mods/ForgeDevelopment/tools/rec-query.py BepInEx/ForgeReports/rec-20260916T101500Z-host-0
    python Mods/ForgeDevelopment/tools/rec-query.py <session> --channel tracer --method Open --limit 20
    python Mods/ForgeDevelopment/tools/rec-query.py <session> --around 12.5 --window 5 --json
    python Mods/ForgeDevelopment/tools/rec-query.py <session> --list-channels --list-kinds
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
from typing import Any, Iterable, Iterator

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rec_segments  # noqa: E402


def session_files(session: str) -> list[str]:
    """Every segment of a session, in segment order. A directory may also be a parent holding several sessions."""
    if os.path.isfile(session):
        return [session]
    files = [str(path) for path in rec_segments.segments(session)]
    if not files:
        raise SystemExit(f"no JSONL segments under {session} (a session the game is still starting has none yet: retry once it has written its first segment)")
    return files


def live_state(path: str) -> str:
    """Why a plain (unpacked) segment may still be growing: "" for a packed or finished one, else what to expect of its tail. The recorder
    holds the one open segment as plain JSONL and packs it when it closes, so a plain segment can be mid-write; its last line then has no
    newline yet, and a segment created a moment ago may not hold even its header line."""
    plain = rec_segments.physical(path)
    if plain.name.endswith(".gz") or not plain.exists():
        return ""
    with plain.open("rb") as stream:
        head = stream.read(64 * 1024)
        stream.seek(0, 2)
        size = stream.tell()
        stream.seek(max(0, size - 1))
        last = stream.read(1)
    if not head:
        return "empty"
    if b"\n" not in head and size <= len(head):
        return "header-pending"
    return "" if last == b"\n" else "partial-tail"


def records(files: Iterable[str]) -> Iterator[dict[str, Any]]:
    """Yields every record of every segment. A line that is not an object is reported and skipped: a truncated
    session is exactly the case this tool exists for, and refusing to read the rest of it would hide the answer. A segment the game is still
    writing (plain, last line unfinished) is read up to its last complete line and says so: that is the state of a recording in progress,
    not damage."""
    for path in files:
        live = live_state(path)
        if live in ("empty", "header-pending"):
            print(f"{os.path.basename(path)}: recording in progress, the segment header is not written yet; no records in it so far", file=sys.stderr)
            continue
        for number, line, shared in rec_segments.lines(path):
            if shared is None:
                print(f"{os.path.basename(path)}: unreadable segment header", file=sys.stderr)
                break
            if live == "partial-tail" and not line.endswith(b"\n"):
                print(f"{os.path.basename(path)}:{number}: recording in progress, last line is still being written; read up to the line before it", file=sys.stderr)
                continue
            if not line.strip():
                continue
            try:
                value = json.loads(line)
            except (json.JSONDecodeError, UnicodeDecodeError) as error:
                print(f"{os.path.basename(path)}:{number}: unreadable record: {error}", file=sys.stderr)
                continue
            if not isinstance(value, dict):
                print(f"{os.path.basename(path)}:{number}: not a record object", file=sys.stderr)
                continue
            value = rec_segments.complete(value, shared)
            body = value.get("body")
            if isinstance(body, dict) and "document" in body:
                name = body["document"]
                try:
                    if not isinstance(name, str) or not re.fullmatch(r"snapshot-row-\d+\.forge", name):
                        raise ValueError("invalid snapshot document name")
                    target = os.path.join(os.path.dirname(path), name)
                    if os.path.islink(target):
                        raise ValueError("snapshot document must be a physical file")
                    with open(target, encoding="utf-8") as document:
                        preserved = json.load(document)
                    if not isinstance(preserved, dict) or any(preserved.get(key) != value.get(key) for key in ("session", "seq", "channel", "kind", "worldEpoch")):
                        raise ValueError("snapshot document identity mismatch")
                    value = preserved
                except (OSError, ValueError) as error:
                    print(f"{os.path.basename(path)}:{number}: unreadable snapshot document: {error}", file=sys.stderr)
                    value["body"]["$error"] = str(error)
            yield value


def typed_field_problem(field: Any) -> str | None:
    """A legitimate null is a complete read, but errors and loss are never values."""
    kinds = {"boolean", "number", "string", "reference", "collection", "color", "enum", "unknown"}
    if not isinstance(field, dict) or not isinstance(field.get("type"), str) or field["type"] not in kinds:
        return "invalid typed field type"
    statuses = {"value", "null", "error", "truncated", "unsupported"}
    if "value" not in field or not isinstance(field.get("status"), str) or field["status"] not in statuses:
        return "invalid typed field status/value"
    status, value = field["status"], field["value"]
    if status == "null":
        return None if value is None else "non-null typed null"
    if status != "value":
        return f"incomplete typed field ({status})" if value is None else f"non-null typed {status}"
    kind = field["type"]
    number = lambda item: type(item) is int or (type(item) is float and math.isfinite(item))
    valid = ((kind == "boolean" and type(value) is bool)
             or (kind == "number" and number(value))
             or (kind in {"string", "reference"} and isinstance(value, str))
             or (kind == "collection" and type(value) is int and value >= 0)
             or (kind == "color" and isinstance(value, list) and len(value) == 4 and all(number(item) for item in value))
             or (kind == "enum" and isinstance(value, dict) and set(value) == {"name", "number"}
                 and isinstance(value["name"], str) and type(value["number"]) is int))
    return None if valid else "invalid typed field value"


def native_readback_problem(item: Any, scope: str = "", epoch: int | None = None) -> str | None:
    """Keep the readback status and its typed value from contradicting each other."""
    if not isinstance(item, dict) or item.get("kind") != "nativeReadback":
        return None
    fields, typed = item.get("fields"), item.get("typedFields")
    required = ("readbackId", "source", "root", "path", "status", "observedFrame")
    if (not isinstance(fields, dict) or any(key not in fields for key in required)
            or any(not isinstance(fields[key], str) for key in required)
            or not fields["readbackId"] or not fields["source"] or not fields["path"]
            or not re.fullmatch(r"-?\d+", fields["observedFrame"])):
        return "invalid native readback identity"
    statuses = {"value", "explicit-null", "null-before-leaf", "getter-error", "unsupported-leaf", "truncated",
                "native-unavailable", "cast-failed", "type-not-applicable", "collection-complete",
                "collection-incomplete", "collection-error", "collection-dispose-failed", "root-unavailable",
                "root-read-failed", "root-dispose-failed", "root-count"}
    status = fields["status"]
    if status not in statuses or not isinstance(typed, dict) or not isinstance(typed.get("value"), dict):
        return "invalid native readback status/value"
    value = typed["value"]
    issue = typed_field_problem(value)
    expected = {"value": "value", "explicit-null": "null", "collection-complete": "value", "root-count": "value",
                "unsupported-leaf": "unsupported", "type-not-applicable": "unsupported", "truncated": "truncated"}
    typed_status = expected.get(status, "error")
    expected_issue = None if typed_status in {"value", "null"} else f"incomplete typed field ({typed_status})"
    if value.get("status") != typed_status or issue != expected_issue:
        return "native readback status contradicts its typed value"
    if status in {"collection-complete", "root-count"} and value.get("type") != "collection":
        return "native readback count is not typed as a collection"
    if scope.startswith("binding/"):
        binding = scope.removeprefix("binding/")
        extras = ("binding", "eventId", "phase", "kernelTick", "kernelWorldEpoch")
        if (any(key not in fields for key in extras) or fields["binding"] != binding or fields["phase"] != "after"
                or not isinstance(fields["eventId"], str) or not fields["eventId"]
                or not isinstance(fields["kernelTick"], str) or not re.fullmatch(r"-?\d+", fields["kernelTick"])
                or not isinstance(fields["kernelWorldEpoch"], str) or not re.fullmatch(r"-?\d+", fields["kernelWorldEpoch"])
                or epoch is not None and int(fields["kernelWorldEpoch"]) != epoch
                or (fields["path"] != "root-count" and "queryInputs" not in fields)
                or (fields["path"] == "root-count" and "queryInputs" in fields)
                or ("queryInputs" in fields and fields["queryInputs"] is not None
                    and not isinstance(fields["queryInputs"], str))):
            return "invalid binding readback context"
    elif scope and any(key in fields for key in ("binding", "eventId", "phase", "kernelTick", "kernelWorldEpoch", "queryInputs")):
        return "binding readback context outside its binding scope"
    return None


def valid_observation(value: Any, session: str, epoch: int, scope: str, reason: str) -> bool:
    if not isinstance(value, dict):
        return False
    strings = ("requestId", "observationId", "specHash", "session")
    integers = ("sourceWorldEpoch", "captureGeneration", "invocationId", "triggerFrame", "triggerTick")
    if (value.get("schemaVersion") != "1.0.0"
            or any(not isinstance(value.get(key), str) or not value[key].strip() for key in strings)
            or any(type(value.get(key)) is not int or value[key] < 0 for key in integers)
            or value["session"] != session or value["sourceWorldEpoch"] != epoch
            or value.get("frameBasis") != "last-capture-monitor-read"
            or not isinstance(value.get("run"), str) or not isinstance(value.get("step"), str)):
        return False
    target = value.get("target")
    if (not isinstance(target, dict)
            or any(not isinstance(target.get(key), str) or not target[key].strip() for key in ("owner", "token"))
            or type(target.get("generation")) is not int or target["generation"] <= 0
            or type(target.get("worldEpoch")) is not int or target["worldEpoch"] != epoch):
        return False
    correlation = ("entryId", "installationId", "signature")
    if any(not isinstance(value.get(key), str) for key in correlation):
        return False
    if scope.startswith("binding/"):
        binding = scope.removeprefix("binding/")
        return (bool(binding) and reason == "binding-readback"
                and value["observationId"] == "native-readback:" + binding
                and value["entryId"] == "" and value["installationId"] == ""
                and value["signature"] == binding and value["invocationId"] == 0)
    if scope != "observation/" + value["observationId"] or reason != "observation":
        return False
    return (all(value[key] for key in correlation) and value["invocationId"] > 0
            or all(value[key] == "" for key in correlation) and value["invocationId"] == 0)


def snapshot_problems(rows: Iterable[dict[str, Any]]) -> list[str]:
    """Only one coherent, fully accounted acquisition may supply verification evidence."""
    batches = {}
    problems = []
    terminal_keys = {"terminalStatus", "fieldCompleteness", "coverageCompleteness", "temporalConsistency"}
    terminal_states = {"completed", "cancelled", "disabled", "queue-full", "error", "invalid",
                       "world-changed", "session-changed", "generation-changed", "installation-changed",
                       "timed-out", "target-unresolved", "target-replaced", "truncated"}
    for row in rows:
        body = body_of(row)
        if row.get("channel") != "state":
            continue
        if "snapshotId" not in body and row.get("kind") not in ("snapshot", "datablocks", "layout") and "observation" not in body:
            continue
        session, identity = row.get("session"), body.get("snapshotId")
        if not isinstance(session, str) or not session or type(identity) is not int or identity <= 0:
            problems.append("snapshot: invalid session or snapshot identity")
            continue
        key = (session, identity)
        label = f"snapshot {key}: "
        scope, reason, epoch = body.get("scope"), body.get("reason"), body.get("worldEpoch")
        if (not isinstance(scope, str) or not scope or not isinstance(reason, str) or not reason
                or type(epoch) is not int or epoch < 0 or type(row.get("worldEpoch")) is not int
                or epoch != row.get("worldEpoch")):
            problems.append(label + "missing or inconsistent scope/reason/world epoch")
        observation = body.get("observation")
        try:
            metadata = json.dumps([scope, reason, epoch, row.get("worldEpoch"), observation], sort_keys=True, allow_nan=False)
        except (ValueError, TypeError):
            problems.append(label + "unreadable snapshot metadata")
            metadata = None
        batch = batches.setdefault(key, {"metadata": metadata, "count": None, "parts": set(), "finals": [], "rows": 0})
        if batch["metadata"] != metadata:
            problems.append(label + "mixed scope/reason/world epoch or observation metadata")
        count, part, final = body.get("parts"), body.get("part"), body.get("complete")
        if (type(part) is not int or part < 0 or type(final) is not bool
                or ("parts" in body and (type(count) is not int or count <= part))):
            problems.append(label + "invalid part identity")
            continue
        if (count is not None and batch["count"] not in (None, count)) or part in batch["parts"]:
            problems.append(label + "inconsistent or duplicate parts")
        if count is not None:
            batch["count"] = count
        batch["parts"].add(part)
        acquisition = body.get("acquisitionComplete")
        if type(acquisition) is not bool:
            problems.append(label + "missing acquisition state")
        elif not acquisition:
            problems.append(label + "acquisition incomplete")
        sections = body.get("sections")
        actual_rows = 0
        if not isinstance(sections, dict):
            problems.append(label + "missing or unreadable sections")
            sections = {}
        containers = list(sections.values())
        if "items" in body:
            items = body["items"]
            containers.append({"items": items, "rows": len(items) if isinstance(items, list) else None})
        for section in containers:
            if not isinstance(section, dict) or not isinstance(section.get("items"), list):
                problems.append(label + "unreadable section items")
                continue
            items = section["items"]
            actual_rows += len(items)
            if type(section.get("rows")) is not int or section["rows"] != len(items):
                problems.append(label + "section row count mismatch")
            for item in items:
                if not isinstance(item, dict):
                    problems.append(label + "unreadable row")
                    continue
                if "typedFields" in item:
                    fields = item["typedFields"]
                    if not isinstance(fields, dict):
                        problems.append(label + "unreadable typed fields")
                    else:
                        for value in fields.values():
                            issue = typed_field_problem(value)
                            if issue:
                                problems.append(label + issue)
                issue = native_readback_problem(item, scope, epoch)
                if issue:
                    problems.append(label + issue)
        if type(body.get("rows")) is not int or body["rows"] != actual_rows:
            problems.append(label + "batch row count mismatch")
        batch["rows"] += actual_rows
        total = body.get("totalRows")
        if type(total) is not int or total < 0 or (not final and total != actual_rows):
            problems.append(label + "total row count mismatch")
        if final:
            batch["finals"].append((part, count, total))
            if count is None or part != count - 1:
                problems.append(label + "invalid end receipt")
            status = body.get("terminalStatus")
            field_state, coverage_state = body.get("fieldCompleteness"), body.get("coverageCompleteness")
            temporal_state = body.get("temporalConsistency")
            if status not in terminal_states:
                problems.append(label + "unknown terminal status")
            elif status == "completed":
                if acquisition is not True or field_state != "complete" or coverage_state != "complete" or temporal_state != "interval":
                    problems.append(label + "completed capture has an invalid end receipt")
            else:
                problems.append(label + f"capture ended {status}")
                if acquisition is not False or field_state != "incomplete" or coverage_state != "incomplete" or temporal_state != "interval":
                    problems.append(label + "failed capture has an invalid end receipt")
        elif terminal_keys.intersection(body):
            problems.append(label + "premature end receipt")
        if ("observation" in body or isinstance(scope, str)
                and (scope.startswith("observation/") or scope.startswith("binding/"))):
            if not valid_observation(observation, session, epoch, scope, reason):
                problems.append(label + "invalid observation metadata")
    for key, batch in batches.items():
        count = batch["count"]
        if count is None or len(batch["parts"]) != count or any(part >= count for part in batch["parts"]) or len(batch["finals"]) != 1:
            problems.append(f"snapshot {key}: missing parts or missing/duplicate end receipt")
        elif batch["finals"][0][2] != batch["rows"]:
            problems.append(f"snapshot {key}: final total row count mismatch")
    return sorted(set(problems))


def read_index(session: str) -> dict[str, Any] | None:
    if not os.path.isdir(session):
        return None
    path = os.path.join(session, "index.forge")
    if not os.path.exists(path):
        return None
    with open(path, "rt", encoding="utf-8") as handle:
        return json.load(handle)


def body_of(record: dict[str, Any]) -> dict[str, Any]:
    body = record.get("body")
    return body if isinstance(body, dict) else {}


def text_of(record: dict[str, Any], key: str) -> str:
    value = body_of(record).get(key)
    return value if isinstance(value, str) else ""


def matches(record: dict[str, Any], args: argparse.Namespace) -> bool:
    if args.channel and record.get("channel") not in args.channel:
        return False
    if args.kind and record.get("kind") not in args.kind:
        return False
    if args.role and record.get("role") != args.role:
        return False
    body = body_of(record)
    if args.type and args.type.lower() not in str(body.get("type", "")).lower():
        return False
    if args.method and args.method.lower() not in str(body.get("method", "")).lower():
        return False
    if args.contains:
        line = json.dumps(record, ensure_ascii=False)
        if args.contains.lower() not in line.lower():
            return False
    if args.since is not None or args.until is not None:
        snet = record.get("snetTime")
        snapped = snet if isinstance(snet, (int, float)) else None
        if snapped is None:
            return False
        if args.since is not None and snapped < args.since:
            return False
        if args.until is not None and snapped > args.until:
            return False
    if args.from_ms is not None or args.to_ms is not None:
        stamp = record.get("t")
        if not isinstance(stamp, (int, float)):
            return False
        if args.from_ms is not None and stamp < args.from_ms:
            return False
        if args.to_ms is not None and stamp > args.to_ms:
            return False
    return True


def bookmarks(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [row for row in rows if row.get("channel") == "mark"]


def around(rows: list[dict[str, Any]], bookmark: dict[str, Any], window: float) -> list[dict[str, Any]]:
    """Records within `window` seconds of a bookmark. The comparison uses snetTime when both carry one, because a
    bookmark is taken by a player and the machine clock is not the game's clock; it falls back to the process clock."""
    snap, stamp = bookmark.get("snetTime"), bookmark.get("t")
    picked = []
    for row in rows:
        other_snap, other_stamp = row.get("snetTime"), row.get("t")
        if isinstance(snap, (int, float)) and isinstance(other_snap, (int, float)):
            if abs(other_snap - snap) <= window:
                picked.append(row)
        elif isinstance(stamp, (int, float)) and isinstance(other_stamp, (int, float)):
            if abs(other_stamp - stamp) <= window * 1000:
                picked.append(row)
    return picked


def summarize(record: dict[str, Any]) -> str:
    body = body_of(record)
    parts = []
    for key in ("profile", "type", "method", "code", "message", "label", "reason", "scope", "path", "status",
                "terminalStatus", "fieldCompleteness", "coverageCompleteness", "count", "dropped"):
        value = body.get(key)
        if value not in (None, ""):
            parts.append(f"{key}={value}")
    for key in ("args", "result"):
        if key in body:
            parts.append(f"{key}={json.dumps(body[key], ensure_ascii=False)[:80]}")
    return " ".join(parts)


def print_table(rows: list[dict[str, Any]], width: int) -> None:
    header = f"{'seq':>7}  {'t(ms)':>8}  {'snet':>8}  {'frame':>7}  {'channel':<8}  {'kind':<14}  detail"
    print(header)
    print("-" * len(header))
    for row in rows:
        snet = row.get("snetTime")
        detail = summarize(row)
        if len(detail) > width:
            detail = detail[: width - 1] + "…"
        print(
            f"{row.get('seq', 0):>7}  {row.get('t', 0):>8}  "
            f"{(f'{snet:.3f}' if isinstance(snet, (int, float)) else '-'):>8}  "
            f"{row.get('frame', '-'):>7}  {str(row.get('channel', ''))[:8]:<8}  {str(row.get('kind', ''))[:14]:<14}  {detail}"
        )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Query a Forge recorder session.")
    parser.add_argument("session", help="session directory (rec-*) or one JSONL/JSONL.GZ segment")
    parser.add_argument("--channel", action="append", help="channel to keep; repeatable")
    parser.add_argument("--kind", action="append", help="record kind to keep; repeatable")
    parser.add_argument("--type", help="substring of the traced type name")
    parser.add_argument("--method", help="substring of the traced method name")
    parser.add_argument("--role", help="host or client")
    parser.add_argument("--contains", help="substring anywhere in the record")
    parser.add_argument("--since", type=float, help="keep records with snetTime at or after this value")
    parser.add_argument("--until", type=float, help="keep records with snetTime at or before this value")
    parser.add_argument("--from-ms", type=float, help="keep records with process milliseconds at or after this value")
    parser.add_argument("--to-ms", type=float, help="keep records with process milliseconds at or before this value")
    parser.add_argument("--around", type=float, help="keep records within --window seconds of the bookmark with this snetTime")
    parser.add_argument("--window", type=float, default=5.0, help="seconds around --around (default 5)")
    parser.add_argument("--limit", type=int, default=0, help="print at most this many records (0 means all)")
    parser.add_argument("--width", type=int, default=120, help="maximum detail column width in the table")
    parser.add_argument("--json", action="store_true", help="print the selected records as one JSON array")
    parser.add_argument("--list-channels", action="store_true", help="count records per channel and exit")
    parser.add_argument("--list-kinds", action="store_true", help="count records per channel/kind and exit")
    parser.add_argument("--index", action="store_true", help="print the session's index.forge and exit")
    args = parser.parse_args(argv)

    if args.index:
        index = read_index(args.session)
        if index is None:
            print(f"no index.forge under {args.session} (a recording in progress may not have written its index yet; the records themselves are readable without it)", file=sys.stderr)
            return 2
        print(json.dumps(index, indent=2, ensure_ascii=False))
        return 0

    rows = list(records(session_files(args.session)))

    if args.list_channels or args.list_kinds:
        counts: dict[str, int] = {}
        for row in rows:
            key = str(row.get("channel", "")) if args.list_channels else f"{row.get('channel', '')}/{row.get('kind', '')}"
            counts[key] = counts.get(key, 0) + 1
        for key in sorted(counts):
            print(f"{counts[key]:>8}  {key}")
        print(f"{len(rows):>8}  total")
        return 0

    selected = [row for row in rows if matches(row, args)]

    if args.around is not None:
        marks = [row for row in rows if row.get("channel") == "mark"]
        if not marks:
            print("no bookmark in this session", file=sys.stderr)
            return 2
        nearest = min(marks, key=lambda row: abs(float(row.get("snetTime") or row.get("t") or 0) - args.around))
        selected = around(selected or rows, nearest, args.window)
        print(f"# around bookmark {text_of(nearest, 'label')!r} on {nearest.get('role')} at "
              f"snetTime={nearest.get('snetTime')} t={nearest.get('t')}ms", file=sys.stderr)

    if args.limit:
        selected = selected[: args.limit]

    if args.json:
        print(json.dumps(selected, ensure_ascii=False, indent=2))
    else:
        print_table(selected, args.width)
        print(f"{len(selected)} of {len(rows)} records")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
