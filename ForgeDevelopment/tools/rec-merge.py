#!/usr/bin/env python3
"""Merge recorder sessions from several machines and report what never reached the host.

Two machines running the authoring build write one session each. Both record the same facts twice: once because the
fact happened locally and once because the network delivered it. This tool aligns the sessions on `snetTime` (the
player's own synchronized session time, written into every record), groups the records that describe one fact, and
prints the pairs a machine recorded but its peer never did — which is the question "the client saw it happen, did the
host ever receive it?".

Pairing rules are optional data: --rules names a tab-separated table with `pointId` and `mergeRule` columns. A rule names the
record selection and the fields that identify one event, for example
`trace:tracer/call/LevelGeneration.LG_SecurityDoor.OnSyncDoorStatusChange#door=fields.m_serialNumber`. Without rules, records
pair on channel/kind/type/method.

Examples:
    python Mods/ForgeDevelopment/tools/rec-merge.py host=rec-...-host-0 client=rec-...-client-1 --same-run
    python Mods/ForgeDevelopment/tools/rec-merge.py host=<dir> client=<dir> --same-run --rules <rules.tsv>
    python Mods/ForgeDevelopment/tools/rec-merge.py host=<dir> client=<dir> --same-run --json > merged.forge
    python Mods/ForgeDevelopment/tools/rec-merge.py host=<dir> client=<dir> --same-run --counts forge/kernel --counts tracer/call/GameEvent
    python Mods/ForgeDevelopment/tools/rec-merge.py host=<dir> client=<dir> --same-run --field enemy.health --field scanRoute.positions
"""

from __future__ import annotations

import argparse
import contextlib
import csv
import hashlib
import math
import re
import json
import io
import os
import sys
from typing import Any, Iterable

import importlib.util

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load_query_module():
    """rec-query.py owns the session reading rules; importing it keeps one implementation of them."""
    path = os.path.join(_HERE, "rec-query.py")
    spec = importlib.util.spec_from_file_location("rec_query", path)
    if spec is None or spec.loader is None:
        raise SystemExit(f"cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


query = _load_query_module()

PAIRING_COLUMN = "mergeRule"
DEFAULT_WINDOW = 1.0


def complete_key_value(value: Any) -> bool:
    """A key cannot depend on absent, truncated, cyclic or failed observations.

    RecReflect may place markers inside arrays/dictionaries, not only at the key's
    root. Fail closed on every nested marker, including normal object $type tags:
    callers should choose explicit stable fields rather than opaque object dumps.
    """
    if value is None:
        return False
    if isinstance(value, float):
        return math.isfinite(value)
    if isinstance(value, dict):
        return all(isinstance(key, str) and not key.startswith("$") and complete_key_value(item)
                   for key, item in value.items())
    if isinstance(value, list):
        return all(complete_key_value(item) for item in value)
    return isinstance(value, (str, int, bool))


class Rule:
    """One pairing rule.

    `select` is `channel/kind` or `channel/kind/type.method`. Each key is a `name=body.path` pair: the name is how the
    report spells it, the path is where the value is read from the record's body. A record that does not carry every
    path is reported as unpairable rather than silently merged with the wrong partner.
    """

    def __init__(self, point: str, select: str, keys: list[tuple[str, str]], window: float) -> None:
        self.point = point
        self.select = select
        self.keys = keys
        self.window = window

    @property
    def names(self) -> list[str]:
        return [name for name, _ in self.keys]

    @property
    def channel(self) -> str:
        return self.select.split("/", 1)[0]

    @property
    def kind(self) -> str:
        parts = self.select.split("/", 1)
        return parts[1] if len(parts) > 1 else ""

    def applies(self, record: dict[str, Any]) -> bool:
        if record.get("channel") != self.channel:
            return False
        kind = self.kind
        if not kind:
            return True
        if "/" in kind:
            wanted_kind, wanted_subject = kind.split("/", 1)
            if record.get("kind") != wanted_kind:
                return False
            # RecTracer writes the profile's (possibly wildcard) pattern to type,
            # and the actual patched native class to declaring.
            recorded_type = query.text_of(record, 'declaring') or query.text_of(record, 'type')
            subject = f"{recorded_type}.{query.text_of(record, 'method')}"
            return subject == wanted_subject
        return record.get("kind") == kind

    def key_of(self, record: dict[str, Any]) -> tuple | None:
        body = query.body_of(record)
        values = []
        for _, path in self.keys:
            value: Any = body
            for step in path.split("."):
                if not isinstance(value, dict) or step not in value:
                    return None
                value = value[step]
            if not complete_key_value(value):
                return None
            # A scalar key stays the value itself, so `key=[3]` reads as the state it names; anything structured is
            # compared as its canonical JSON.
            values.append(value if isinstance(value, (str, int, float, bool)) or value is None
                          else json.dumps(value, sort_keys=True, ensure_ascii=False))
        return tuple(values)


def parse_rule(point: str, text: str) -> Rule | None:
    """`trace:<select>#<name>=<body.path>,<name>=<body.path>[;window=<seconds>]`. A key
    without a `=` names the body field of the same name. Anything else is not a pairing rule and is reported by the
    caller rather than guessed at."""
    text = text.strip()
    if not text or text.startswith("manual:"):
        return None
    if text.startswith("trace:"):
        text = text[len("trace:"):]
    window = DEFAULT_WINDOW
    if ";window=" in text:
        text, _, tail = text.partition(";window=")
        try:
            window = float(tail)
        except ValueError:
            return None
    if not math.isfinite(window) or window <= 0:
        return None
    select, _, key_text = text.partition("#")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.+`-]+)?", select):
        return None
    if not key_text.strip():
        # A selection without a key is not a pairing rule: it would pair every record the selection matched with
        # every other one, which is the opposite of what a merge is asked for.
        return None
    keys: list[tuple[str, str]] = []
    for part in key_text.split(","):
        part = part.strip()
        if not part:
            continue
        name, separator, path = part.partition("=")
        path = path if separator else name
        if not re.fullmatch(r"[A-Za-z_]\w*", name) or not re.fullmatch(r"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*", path):
            return None
        if any(existing == name for existing, _ in keys):
            return None
        keys.append((name, path))
    if not keys:
        return None
    return Rule(point, select.strip(), keys, window)


def load_rules(path: str) -> list[Rule]:
    """Load an explicit rules table. Empty/manual rows are legitimate; a missing file, missing columns or an unreadable rule refuses."""
    rules: list[Rule] = []
    problems: list[str] = []
    with open(path, "rt", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle, delimiter="\t")
        if not reader.fieldnames or not {"pointId", PAIRING_COLUMN}.issubset(reader.fieldnames):
            problems.append(f"{path}: rules table needs pointId and {PAIRING_COLUMN} columns")
        else:
            seen: set[str] = set()
            for number, row in enumerate(reader, 2):
                if None in row or any(value is None for value in row.values()):
                    problems.append(f"{path}:{number}: malformed TSV row")
                    continue
                point = row["pointId"]
                if not point or point in seen:
                    problems.append(f"{path}:{number}: missing or duplicate pointId {point!r}")
                    continue
                seen.add(point)
                raw = row[PAIRING_COLUMN].strip()
                if not raw or raw.startswith("manual:"):
                    continue
                rule = parse_rule(point, raw)
                if rule is None:
                    problems.append(f"{point}: unreadable pairing rule {raw!r}; mark human instructions manual:")
                else:
                    rules.append(rule)
    if problems:
        raise ValueError("; ".join(problems))
    return rules


def fallback_key(record: dict[str, Any]) -> tuple:
    """What a record without a rule pairs on: channel, kind, traced subject, method and code. The subject is the class
    a traced call really ran on (`declaring`), not the profile's type pattern, so one wildcard entry still separates
    the classes it matched."""
    body = query.body_of(record)
    return (
        str(record.get("channel", "")),
        str(record.get("kind", "")),
        str(body.get("declaring") or body.get("type", "")),
        str(body.get("method", "")),
        str(body.get("code", "")),
    )


def label_of(key: tuple) -> str:
    """The key as a path, empty parts left out: `tracer/call/LG_SecurityDoor/OnOpen`."""
    return "/".join(part for part in key if part)


def record_role(record: dict[str, Any]) -> str:
    value = record.get("role")
    return value if isinstance(value, str) and value in {"host", "client"} else "unknown"


def role_history(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Record role transitions, including unknown intervals; never infer from the session label."""
    history = []
    for row in rows:
        role = record_role(row)
        if not history or history[-1]["role"] != role:
            history.append({"role": role, "seq": row.get("seq"), "snetTime": row.get("snetTime")})
    return history


def role_of(rows: list[dict[str, Any]]) -> str:
    """Session summary only. Individual facts always use their own recorded role."""
    roles = {record_role(row) for row in rows}
    return next(iter(roles)) if len(roles) == 1 else "mixed" if roles else "unknown"


def merge(sessions: dict[str, list[dict[str, Any]]], rules: list[Rule], tolerance: float,
          paths: dict[str, str] | None = None, *, same_run_confirmed: bool = False,
          indexes: dict[str, Any] | None = None,
          input_problems: dict[str, list[str]] | None = None) -> dict[str, Any]:
    # The caller names a machine; only its own argument carries the directory the index has to be read from.
    paths = {name: (paths or {}).get(name, name) for name in sessions}
    roles = {name: role_of(rows) for name, rows in sessions.items()}
    raw_sessions = sessions
    incomplete = input_problems is not None and any(input_problems.values())
    if incomplete:
        sessions = {name: [] for name in sessions}
    pairs: list[dict[str, Any]] = []
    unpairable: list[dict[str, Any]] = []

    for rule in rules:
        names = list(sessions)
        # Each unordered pair of sessions is walked once, in the order the caller named them, so a fact both
        # machines recorded produces one row instead of the same row twice.
        contexts = {name: {(record.get("level"), record.get("worldEpoch")) for record in rows
                           if rule.applies(record) and valid_level(record.get("level"))
                           and valid_epoch(record.get("worldEpoch"))}
                    for name, rows in sessions.items()}

        def alignment_problem(name: str, record: dict[str, Any]) -> str | None:
            if not same_run_confirmed:
                return "same multiplayer run was not confirmed; this pairing path does not establish shared run identity from records"
            if not valid_level(record.get("level")):
                return "record has no usable expedition level identity"
            if not valid_epoch(record.get("worldEpoch")):
                return "record has no usable local world epoch"
            if len(contexts[name]) != 1:
                return "input spans multiple levels/local world epochs; prefilter one run and world per machine"
            if not valid_time(record.get("snetTime")):
                return "record has no finite positive synchronized time"
            return None

        for index, left_name in enumerate(names):
            for right_name in names[index + 1:]:
                paired: set[tuple[str, Any]] = set()
                ambiguous: set[tuple[str, Any]] = set()
                for record in sessions[left_name]:
                    if not rule.applies(record) or (left_name, record.get("seq")) in paired | ambiguous:
                        continue
                    problem = alignment_problem(left_name, record)
                    if problem:
                        unpairable.append({"rule": rule.point, "session": left_name, "seq": record.get("seq"), "reason": problem})
                        continue
                    key = rule.key_of(record)
                    if key is None:
                        unpairable.append({"rule": rule.point, "session": left_name, "seq": record.get("seq"), "reason": "record lacks a pairing key"})
                        continue
                    candidates = [candidate for candidate in sessions[right_name]
                                  if (right_name, candidate.get("seq")) not in paired and rule.applies(candidate)
                                  and not alignment_problem(right_name, candidate) and rule.key_of(candidate) == key
                                  and within(record, candidate, rule.window)]
                    found = candidates[0] if len(candidates) == 1 else None
                    reverse = [] if found is None else [candidate for candidate in sessions[left_name]
                        if (left_name, candidate.get("seq")) not in paired and rule.applies(candidate)
                        and not alignment_problem(left_name, candidate) and rule.key_of(candidate) == key
                        and within(candidate, found, rule.window)]
                    if len(candidates) > 1 or len(reverse) > 1:
                        # Mark the entire connected candidate component on both machines.
                        # Otherwise a peer of an ambiguous event would later be misreported
                        # as a fact recorded on only one machine.
                        pending = [(left_name, record)]
                        while pending:
                            machine, candidate = pending.pop()
                            identity = (machine, candidate.get("seq"))
                            if identity in ambiguous:
                                continue
                            ambiguous.add(identity)
                            unpairable.append({"rule": rule.point, "session": machine, "seq": candidate.get("seq"),
                                               "reason": "ambiguous repeated key within synchronized-time window; no unique peer event"})
                            peer = right_name if machine == left_name else left_name
                            pending.extend((peer, other) for other in sessions[peer]
                                if (peer, other.get("seq")) not in paired | ambiguous and rule.applies(other)
                                and not alignment_problem(peer, other) and rule.key_of(other) == key
                                and within(candidate, other, rule.window))
                        continue
                    if found is not None:
                        paired.add((left_name, record.get("seq")))
                        paired.add((right_name, found.get("seq")))
                    pairs.append({
                        "rule": rule.point,
                        "key": list(key),
                        "left": {"session": left_name, "role": record_role(record), "seq": record.get("seq"), "snetTime": record.get("snetTime")},
                        "right": None if found is None else {
                            "session": right_name, "role": record_role(found), "seq": found.get("seq"), "snetTime": found.get("snetTime")},
                        "deltaSnet": None if found is None else delta(record, found),
                    })
                # The right side's own unpaired records are rows too: a fact only the second machine recorded would
                # otherwise be invisible, which is the one answer this tool exists to give.
                for record in sessions[right_name]:
                    if not rule.applies(record) or (right_name, record.get("seq")) in paired | ambiguous:
                        continue
                    problem = alignment_problem(right_name, record)
                    if problem:
                        unpairable.append({"rule": rule.point, "session": right_name, "seq": record.get("seq"), "reason": problem})
                        continue
                    key = rule.key_of(record)
                    if key is None:
                        unpairable.append({"rule": rule.point, "session": right_name, "seq": record.get("seq"), "reason": "record lacks a pairing key"})
                        continue
                    pairs.append({
                        "rule": rule.point,
                        "key": list(key),
                        "left": {"session": right_name, "role": record_role(record), "seq": record.get("seq"), "snetTime": record.get("snetTime")},
                        "right": None,
                        "deltaSnet": None,
                    })

    buckets = fallback_buckets(sessions)
    unmatched = unpaired_by_fallback(buckets)
    if indexes is None:
        indexes = {name: (query.read_index(path) if os.path.isdir(path) else None) for name, path in paths.items()}
    return {
        "captureComplete": not incomplete if input_problems is not None else None,
        "sessions": [
            {"name": name, "role": roles.get(name), "initialRole": record_role(rows[0]) if rows else "unknown",
             "roleHistory": role_history(rows), "records": len(rows), "index": indexes[name]}
            for name, rows in raw_sessions.items()
        ],
        "pairingScope": {"sameRunConfirmed": same_run_confirmed,
                         "sharedRunIdentityEstablished": False,
                         "requirements": "same actual multiplayer run, same recorded level, finite positive snetTime, one local world epoch per machine",
                         "limitations": "This pairing path does not establish shared run identity from records. Level alone cannot distinguish repeated plays; --same-run confirms operator prefiltering. Local epoch values are never compared across machines."},
        "rules": [{"point": rule.point, "select": rule.select, "keys": rule.names, "window": rule.window} for rule in rules],
        "pairs": pairs,
        "clientOnly": [entry for entry in unmatched if entry["role"] == "client"],
        "hostOnly": [entry for entry in unmatched if entry["role"] == "host"],
        "unpairable": unpairable,
        "counts": [
            {"key": list(key), "label": label_of(key), "host": counts.get("host", 0), "client": counts.get("client", 0)}
            for key, counts in sorted(buckets.items())
        ],
    }


def valid_time(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value > 0


def valid_level(value: Any) -> bool:
    return isinstance(value, str) and value == value.strip() and value not in {"", "unavailable", "unknown"}


def valid_epoch(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def within(left: dict[str, Any], right: dict[str, Any], window: float) -> bool:
    """Equal real level + finite synchronized time are necessary, never a shared run id."""
    left_time, right_time = left.get("snetTime"), right.get("snetTime")
    return (valid_level(left.get("level")) and left.get("level") == right.get("level")
            and valid_time(left_time) and valid_time(right_time)
            and abs(left_time - right_time) <= max(window, 0.0))


def delta(left: dict[str, Any], right: dict[str, Any]) -> float | None:
    left_time, right_time = left.get("snetTime"), right.get("snetTime")
    return round(right_time - left_time, 3) if valid_time(left_time) and valid_time(right_time) else None


def fallback_buckets(sessions: dict[str, list[dict[str, Any]]]) -> dict[tuple, dict[str, Any]]:
    """Count each record under its actual role, including migration within one file.
    Unknown roles are not attributed to either machine role by inference.
    """
    seen: dict[tuple, dict[str, Any]] = {}
    for name, rows in sessions.items():
        for record in rows:
            role = record_role(record)
            if role == "unknown":
                continue
            if record.get("channel") in ("session", "log"):
                continue
            key = fallback_key(record)
            bucket = seen.setdefault(key, {})
            bucket[role] = bucket.get(role, 0) + 1
            bucket.setdefault(role + "Session", name)
            bucket.setdefault(role + "Seq", record.get("seq", 0))
    return seen


def unpaired_by_fallback(buckets: dict[tuple, dict[str, Any]]) -> list[dict[str, Any]]:
    """The buckets whose host and client counts differ. This is the "client saw it, host did not" answer for a fact
    whose pairing rule has not been written yet."""
    result = []
    for key, counts in sorted(buckets.items()):
        host, client = counts.get("host", 0), counts.get("client", 0)
        if host == client:
            continue
        role = "client" if client > host else "host"
        result.append({
            "key": list(key),
            "host": host,
            "client": client,
            "role": role,
            "session": counts.get(role + "Session", "unknown"),
            "sampleSeq": counts.get(role + "Seq", 0),
            "delta": client - host,
        })
    return result


def state_items(body: dict[str, Any]) -> Iterable[dict[str, Any]]:
    """The rows of one state record: a snapshot's `sections.<name>.items` or a change record's `items`."""
    sections = body.get("sections")
    if isinstance(sections, dict):
        for section in sections.values():
            if isinstance(section, dict) and isinstance(section.get("items"), list):
                for item in section["items"]:
                    if isinstance(item, dict):
                        yield item
    items = body.get("items")
    if isinstance(items, list):
        for item in items:
            if isinstance(item, dict):
                yield item


def complete_field_value(value: Any) -> bool:
    """Reject unreadable/truncated observations, including CaptureWorld's string caps."""
    if not complete_key_value(value):
        return False
    if isinstance(value, str):
        return not (value in {"?", "no-kernel", "unresolved", "unreadable", "collected", "unknown"}
                    or value.startswith(("$", "error:", "unreadable:", "unresolved:")) or value.endswith("…")
                    or re.search(r";\+\d*$", value))
    if isinstance(value, dict):
        return all(complete_field_value(item) for item in value.values())
    if isinstance(value, list):
        return all(complete_field_value(item) for item in value)
    return True


def incomplete_state_body(body: dict[str, Any]) -> bool:
    """Do not let unreadable/skipped containers disappear beside readable rows."""
    if (not body or body.get("skipped") or body.get("notes") or body.get("budgetExhausted")
            or any(key.startswith("$") for key in body)):
        return True
    containers = []
    if "sections" in body:
        if not isinstance(body["sections"], dict):
            return True
        containers.extend(body["sections"].values())
    if "items" in body:
        containers.append({"items": body["items"]})
    if not containers:
        return True
    for section in containers:
        if not isinstance(section, dict) or not isinstance(section.get("items"), list):
            return True
        if any(not isinstance(item, dict) or not isinstance(item.get("kind"), str)
               or any(key.startswith("$") for key in item) for item in section["items"]):
            return True
        if "rows" in section and (type(section["rows"]) is not int or section["rows"] != len(section["items"])):
            return True
        for item in section["items"]:
            if "typedFields" in item:
                fields = item["typedFields"]
                if not isinstance(fields, dict) or any(query.typed_field_problem(value) for value in fields.values()):
                    return True
            if query.native_readback_problem(item):
                return True
    return False


def field_timelines(sessions: dict[str, list[dict[str, Any]]], kind: str, field: str, *,
                    same_run_confirmed: bool = False,
                    input_problems: dict[str, list[str]] | None = None) -> list[dict[str, Any]]:
    """Diagnostic value sequences, not cross-peer object or gameplay adjudication.

    Raw ids only organize candidates. Each machine's level/local epoch has its own
    timeline; epoch numbers are never equated across peers. Missing observations
    remain visible, and cannot turn equal unknown values into equal evidence.
    """
    timelines: dict[str, dict[str, Any]] = {}
    common = [f"{name}: {problem}" for name, problems in (input_problems or {}).items() for problem in problems]
    if not same_run_confirmed:
        common.append("same multiplayer run was not confirmed; prefilter one run and world per machine and use --same-run")
    if len(sessions) < 2:
        common.append("at least two input machines are required")
    levels = set()
    for name, rows in sessions.items():
        common.extend(f"{name}: {problem}" for problem in query.snapshot_problems(rows))
        contexts = {(record["level"], record["worldEpoch"]) for record in rows
                    if valid_level(record.get("level")) and valid_epoch(record.get("worldEpoch"))}
        if len(contexts) > 1:
            common.append(f"{name}: input spans multiple levels/local world epochs; prefilter one world")
        elif not contexts:
            common.append(f"{name}: no usable level/local world epoch context")
        else:
            levels.add(next(iter(contexts))[0])
        for record in rows:
            if record.get("channel") != "state":
                continue
            body = query.body_of(record)
            # Snapshot batches are judged as a whole above. Their terminal receipt deliberately has no rows and may
            # carry informational notes, both of which the legacy single-record check treats as incomplete.
            if "snapshotId" not in body and incomplete_state_body(body):
                common.append(f"{name} seq={record.get('seq')}: incomplete state capture (skipped/notes/budget or unreadable body)")
            for item in state_items(body):
                if item.get("kind") != kind:
                    continue
                identity = item.get("id")
                key = json.dumps(identity, sort_keys=True, ensure_ascii=False)
                entry = timelines.setdefault(key, {"id": identity, "machines": {machine: [] for machine in sessions},
                                                   "reasons": []})
                problems = entry["reasons"]
                if not isinstance(identity, str) or not identity or "?" in identity or not complete_field_value(identity):
                    problems.append(f"{name} seq={record.get('seq')}: missing or unreadable row id")
                level, epoch, time = record.get("level"), record.get("worldEpoch"), record.get("snetTime")
                if not valid_level(level) or not valid_epoch(epoch) or not valid_time(time):
                    problems.append(f"{name} seq={record.get('seq')}: unusable level/local world epoch/synchronized time")
                segments = entry["machines"][name]
                segment = next((part for part in segments if part["level"] == level and part["worldEpoch"] == epoch), None)
                if segment is None:
                    segment = {"level": level, "worldEpoch": epoch, "timeline": []}
                    segments.append(segment)
                fields = item.get("typedFields") if "typedFields" in item else item.get("fields")
                present = isinstance(fields, dict) and field in fields
                value = fields[field] if present else None
                if "typedFields" in item:
                    readable = present and query.typed_field_problem(value) is None and value["status"] == "value"
                    value = value.get("value") if isinstance(value, dict) else None
                else:
                    readable = present and complete_field_value(value)
                if not readable:
                    problems.append(f"{name} seq={record.get('seq')}: "
                                    + ("missing field" if not present else "unreadable or truncated field value"))
                series = segment["timeline"]
                if not series or json.dumps(series[-1][1], sort_keys=True) != json.dumps(value, sort_keys=True):
                    series.append([time, value])
    if len(levels) > 1:
        common.append("input machines describe different expedition levels")
    if not timelines:
        timelines["null"] = {"id": None, "machines": {name: [] for name in sessions}, "reasons": []}
    result = [entry for _, entry in sorted(timelines.items())]
    for entry in result:
        problems = entry["reasons"]
        problems.extend(common)
        sequences = []
        for name, segments in entry["machines"].items():
            if not segments:
                problems.append(f"{name}: no matching row for {kind}.{field}")
            elif len(segments) == 1:
                sequences.append(json.dumps([value for _, value in segments[0]["timeline"]], sort_keys=True))
            else:
                problems.append(f"{name}: row spans multiple levels/local world epochs")
        entry["observedSequencesEqual"] = (all(sequence == sequences[0] for sequence in sequences)
                                             if not problems and len(sequences) == len(sessions) else None)
        entry["status"] = "inconclusive"
        entry["gameplayEvidence"] = "unproven-object-identity"
        problems.append("raw row ids do not establish cross-peer object identity; value sequences do not prove gameplay or checkpoint success")
        entry["reasons"] = list(dict.fromkeys(problems))
    return result


def read_session_input(path: str) -> tuple[list[dict[str, Any]], Any, list[str]]:
    """Keep readable records, but carry parsing and capture-loss warnings into field results."""
    rows: list[dict[str, Any]] = []
    errors = io.StringIO()
    with contextlib.redirect_stderr(errors):
        try:
            rows.extend(query.records(query.session_files(path)))
        except (OSError, UnicodeError, EOFError, SystemExit) as error:
            print(f"unreadable session: {error}", file=sys.stderr)
    problems = errors.getvalue().splitlines()
    problems.extend(query.snapshot_problems(rows))
    index = None
    try:
        index = query.read_index(path)
        if index is not None and not isinstance(index, dict):
            raise ValueError("index is not an object")
    except (OSError, UnicodeError, ValueError) as error:
        problems.append(f"unreadable session index: {error}")
        index = None
    if index is None:
        problems.append("session index unavailable; capture completeness is unknown")
    if isinstance(index, dict):
        if (type(index.get("dropped")) is not int or index["dropped"] != 0
                or type(index.get("queueRefusals")) is not int or index["queueRefusals"] != 0 or index.get("budgetReached") is not False):
            problems.append("session index reports dropped records, reached budget, or unknown capture loss")
        persistence = index.get("persistence", {})
        if not isinstance(persistence, dict) or persistence.get("outcome") != "Completed" or persistence.get("errors") != []:
            problems.append("session persistence incomplete or unavailable")
        report = index.get("patches", {})
        if not isinstance(report, dict):
            report = {}
        budget = report.get("traceBudget", {})
        if (not isinstance(budget, dict) or budget.get("incomplete") is not False
                or any(type(budget.get(field)) is not int or budget[field] != 0
                       for field in ("dropped", "overrunFrames"))):
            problems.append("trace budget incomplete, dropped records, overrun frames, or unknown integrity")
        installation = report.get("installation", {})
        if (not isinstance(installation, dict) or installation.get("complete") is not True
                or installation.get("discoveryPending") is not False):
            problems.append("trace installation or discovery incomplete")
        run_id = index.get("runId")
        if ("runId" not in index or "runId" not in report or run_id != report.get("runId")
                or run_id is not None and (not isinstance(run_id, str) or not run_id.strip()) or report.get("errors") != []
                or report.get("observationCallbackFailures") != 0 or report.get("skipped") != 0
                or report.get("nativeCoverage") != []):
            problems.append("trace identity mismatch, failed observations or incomplete coverage")
        patches = report.get("patches")
        if not isinstance(patches, list) or any(not isinstance(patch, dict)
                or any(type(patch.get(field)) is not int or patch[field] != 0 for field in ("rateDropped", "budgetDropped", "failures"))
                or patch.get("cleanupPending") is not False for patch in patches):
            problems.append("trace patches missing, dropped records, failed or awaiting cleanup")
        count = index.get("records")
        if "records" in index and (type(count) is not int or count < 0):
            problems.append("session index has an unreadable record count")
        elif isinstance(count, int) and count > len(rows):
            problems.append(f"session index records={count} exceeds readable records={len(rows)}; input may be truncated")
    for row in rows:
        body = query.body_of(row)
        if row.get("channel") == "session" and (body.get("dropped") or body.get("budgetReached")):
            problems.append(f"seq={row.get('seq')}: session reports dropped records or reached budget")
    return rows, index, problems


def parse_session_argument(value: str) -> tuple[str, str]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("expected name=directory")
    name, _, path = value.partition("=")
    if not name or not path:
        raise argparse.ArgumentTypeError("expected name=directory")
    return name, path


def _file_hash(path: str) -> str:
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Merge Forge recorder sessions and report what never arrived.")
    parser.add_argument("sessions", nargs="*", type=parse_session_argument,
                        help="name=session-directory; name the machine, e.g. host=... client=...")
    parser.add_argument("--rules", help="tab-separated table with pointId and mergeRule columns; without it records pair on channel/kind/type/method")
    parser.add_argument("--json", action="store_true", help="print the merged result as JSON")
    parser.add_argument("--same-run", action="store_true",
                        help="Confirm all inputs were prefiltered to the same multiplayer run and one level/world per machine; this pairing path does not establish a shared run id from records")
    parser.add_argument("--limit", type=int, default=40, help="maximum rows printed per section")
    parser.add_argument("--counts", action="append", metavar="PREFIX",
                        help="print both machines' record counts for every key whose label starts with PREFIX, "
                             "e.g. forge/kernel or tracer/call/ChainedPuzzles.CP_Bioscan_Core; repeatable")
    parser.add_argument("--field", action="append", metavar="KIND.FIELD",
                        help="diagnostic state-field timelines per raw row id/local context; never proves cross-peer object identity or gameplay success; repeatable")
    args = parser.parse_args(argv)

    try:
        rules = load_rules(args.rules) if args.rules else []
    except (OSError, ValueError, csv.Error) as error:
        print("pairing rules validation failed: " + str(error), file=sys.stderr)
        return 2
    rules_status = {"mode": "rules" if args.rules else "generic", "path": os.path.abspath(args.rules) if args.rules else None,
                    "sha256": _file_hash(args.rules) if args.rules else None, "rules": len(rules)}
    if not args.sessions:
        parser.error("give name=session-directory")
    if args.rules and not args.same_run:
        parser.error("rule pairing requires --same-run after confirming and prefiltering the same multiplayer run and one level/world per machine")

    sessions: dict[str, list[dict[str, Any]]] = {}
    paths: dict[str, str] = {}
    indexes: dict[str, Any] = {}
    input_problems: dict[str, list[str]] = {}
    for name, path in args.sessions:
        if name in sessions:
            parser.error(f"duplicate machine name {name!r}; every input needs a distinct name")
        sessions[name], indexes[name], input_problems[name] = read_session_input(path)
        paths[name] = path
        for problem in input_problems[name]:
            print(f"{name}: {problem}", file=sys.stderr)
        if not sessions[name]:
            print(f"{name}: {path} holds no records", file=sys.stderr)

    result = merge(sessions, rules if args.same_run else [], DEFAULT_WINDOW, paths,
                   same_run_confirmed=args.same_run, indexes=indexes, input_problems=input_problems)
    result["inputProblems"] = input_problems
    result["pairingStatus"] = {**rules_status, "sameRunConfirmed": args.same_run,
                                "rulePairingReady": bool(rules) and args.same_run and result["captureComplete"]}
    fields: dict[str, list[dict[str, Any]]] = {}
    for spec in args.field or []:
        kind, _, name = spec.partition(".")
        if not kind or not name:
            parser.error(f"--field expects KIND.FIELD, got {spec!r}")
        fields[spec] = field_timelines(sessions, kind, name, same_run_confirmed=args.same_run,
                                      input_problems=input_problems)
    result["fields"] = fields

    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0 if result["captureComplete"] else 1

    print("pairing scope: " + result["pairingScope"]["limitations"])
    for session in result["sessions"]:
        index = session.get("index") or {}
        print(f"session {session['name']}: role={session['role']} initialRole={session['initialRole']} records={session['records']} "
              f"dropped={index.get('dropped', '?')} budgetReached={index.get('budgetReached', '?')}")
    print(f"pairing rules loaded: {len(rules)}")

    paired = [pair for pair in result["pairs"] if pair["right"]]
    missing = [pair for pair in result["pairs"] if not pair["right"]]
    print(f"\npairs matched on both machines: {len(paired)}")
    for pair in paired[: args.limit]:
        print(f"  {pair['rule']} key={pair['key']} "
              f"{pair['left']['role']}#{pair['left']['seq']} <-> {pair['right']['role']}#{pair['right']['seq']} "
              f"delta={pair['deltaSnet']}")
    print(f"\nrecorded by one machine only: {len(missing)}")
    for pair in missing[: args.limit]:
        print(f"  {pair['rule']} key={pair['key']} only {pair['left']['role']} seq={pair['left']['seq']} "
              f"snetTime={pair['left']['snetTime']}")

    print(f"\nclient facts the host never recorded (no rule): {len(result['clientOnly'])}")
    for entry in result["clientOnly"][: args.limit]:
        print(f"  {entry['key']} client={entry['client']} host={entry['host']} sampleSeq={entry['sampleSeq']}")
    print(f"host facts the client never recorded (no rule): {len(result['hostOnly'])}")
    for entry in result["hostOnly"][: args.limit]:
        print(f"  {entry['key']} host={entry['host']} client={entry['client']} sampleSeq={entry['sampleSeq']}")
    if result["unpairable"]:
        print(f"\nunpairable records: {len(result['unpairable'])}")
        for entry in result["unpairable"][: args.limit]:
            print(f"  {entry['rule']} session={entry['session']} seq={entry['seq']}: {entry['reason']}")
    for prefix in args.counts or []:
        selected = [entry for entry in result["counts"] if entry["label"].startswith(prefix)]
        print(f"\nrecord counts under {prefix}: {len(selected)}")
        for entry in selected[: args.limit]:
            print(f"  {entry['label']} host={entry['host']} client={entry['client']}")
    for spec, rows in fields.items():
        print(f"\nfield {spec}: {len(rows)} diagnostic rows; gameplay status inconclusive (object identity unproven)")
        for row in rows[: args.limit]:
            print(f"  {row['id']} observedSequencesEqual={row['observedSequencesEqual']}")
            for name, segments in row["machines"].items():
                if not segments:
                    print(f"    {name}: no matching row")
                for part in segments:
                    timeline = ",".join(str(value) for _, value in part["timeline"])
                    print(f"    {name} level={part['level']} worldEpoch={part['worldEpoch']}: {timeline}")
            for reason in row["reasons"]:
                print("    ! " + reason)
    return 0 if result["captureComplete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
