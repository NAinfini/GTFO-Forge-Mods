#!/usr/bin/env python3
"""Submit one installed Authoring experiment to an already-running game; stdlib only."""
import argparse
import datetime as dt
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import sys
import time
import uuid


# Use the counter read by .NET Stopwatch, including on Python versions whose
# perf_counter has a process-local origin. UTC is evidence only, never a gate.
if os.name == "nt":
    import ctypes
    _kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    _qpc = _kernel32.QueryPerformanceCounter
    _qpf = _kernel32.QueryPerformanceFrequency
    for _function in (_qpc, _qpf):
        _function.argtypes = [ctypes.POINTER(ctypes.c_longlong)]
        _function.restype = ctypes.c_int
    _frequency = ctypes.c_longlong()
    if not _qpf(ctypes.byref(_frequency)) or _frequency.value <= 0:
        raise ctypes.WinError(ctypes.get_last_error())

    def monotonic_seconds():
        counter = ctypes.c_longlong()
        if not _qpc(ctypes.byref(counter)):
            raise ctypes.WinError(ctypes.get_last_error())
        return counter.value / _frequency.value
else:
    monotonic_seconds = time.perf_counter

READ_RETRIES = {"sharing": 0, "json": 0}


def physical(path):
    path = Path(os.path.abspath(path))
    for item in [*reversed(path.parents), path]:
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("linked or reparse transport path")
    return path


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON field: " + key)
        result[key] = value
    return result


class StatusNotReady(ValueError):
    """A pre-publication freshness/idle gate; no request has been written."""
    def __init__(self, message, status=None):
        super().__init__(message)
        self.status = status


class ContextChanged(ValueError):
    """The game's world or recorder session moved between the caller's status read and publication; no request was written."""


def read_json(path, maximum=16384):
    for attempt in range(8):
        try:
            physical(path)
            with open(path, "rb") as stream:
                raw = stream.read(maximum + 1)
        except OSError as error:
            winerror = getattr(error, "winerror", None)
            # Windows _wopen can report a sharing lock as PermissionError without a winerror.
            if not isinstance(error, PermissionError) and winerror not in (32, 33):
                raise
            if attempt == 7:
                if winerror in (32, 33):
                    raise TimeoutError("transport-file-read-timeout: " + str(path) + ": " + str(error)) from error
                raise
            READ_RETRIES["sharing"] += 1
            time.sleep(.25)
            continue
        if len(raw) > maximum:
            raise ValueError("transport file exceeds byte budget")
        try:
            return json.loads(raw, object_pairs_hook=unique_object), raw
        except (json.JSONDecodeError, UnicodeDecodeError) as error:
            if attempt == 7:
                raise TimeoutError("transport-json-read-timeout: " + str(path) + ": " + str(error)) from error
            READ_RETRIES["json"] += 1
            time.sleep(.25)


def utc(value):
    if not isinstance(value, str):
        raise ValueError("timestamp must be a UTC string")
    # C# round-trip timestamps carry seven fractional digits; Python 3.10 accepts microseconds.
    value = re.sub(r"(\.\d{6})\d+(?=Z$|[+-]\d{2}:\d{2}$)", r"\1", value)
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None or parsed.utcoffset() != dt.timedelta(0):
        raise ValueError("timestamp must be UTC")
    return parsed


def status_age(value):
    written = value.get("monotonicSeconds")
    try:
        finite = type(written) in (int, float) and math.isfinite(float(written))
    except OverflowError:
        finite = False
    if not finite:
        raise ValueError("invalid status monotonic timestamp")
    age = monotonic_seconds() - written
    if age < -1:
        raise ValueError("game status monotonic timestamp is in the future")
    return age


def status_at(control, *, idle=True, maximum_age=3):
    value, _ = read_json(control / "status.forge")
    required = {"format", "schemaVersion", "processId", "instance", "utc", "monotonicSeconds", "worldEpoch", "recorderSession", "gameState", "activeCommand", "stopped"}
    if not isinstance(value, dict) or set(value) != required or value["format"] != "forge.development-experiment-status" or value["schemaVersion"] != "1.0.0":
        raise ValueError("unsupported status schema; regenerate with the current Development tools")
    if type(value["processId"]) is not int or value["processId"] <= 0 or type(value["worldEpoch"]) is not int:
        raise ValueError("invalid status process or world")
    if not isinstance(value["instance"], str) or str(uuid.UUID(value["instance"])) != value["instance"]:
        raise ValueError("invalid instance UUID")
    if type(value["stopped"]) is not bool or any(not isinstance(value[key], str) for key in ("recorderSession", "gameState", "activeCommand")):
        raise ValueError("invalid status fields")
    utc(value["utc"])
    age = status_age(value)
    if value["stopped"]:
        raise ValueError("game status is stopped")
    if (maximum_age is not None and age > maximum_age) or (idle and value["activeCommand"]):
        raise StatusNotReady("game status is stale, stopped, or busy", value)
    return value


def command_hash(root, command_id):
    directory = physical(root / "ForgeDevelopment" / "commands")
    matches = []
    for path in sorted(directory.glob("*.forge")):
        value, raw = read_json(path, 4 * 1024 * 1024)
        commands = value if isinstance(value, list) else [value]
        for command in commands:
            if isinstance(command, dict) and command.get("id") == command_id:
                matches.append(hashlib.sha256(raw).hexdigest())
    if len(matches) != 1:
        raise ValueError("command must identify exactly one installed command")
    return matches[0]


def submit(root, command_id, *, expected=None):
    if not isinstance(command_id, str) or not command_id.strip() or len(command_id) > 256:
        raise ValueError("invalid command ID")
    root = physical(root)
    control = physical(root / "ForgeDevelopment" / "experiments" / "control")
    digest = command_hash(root, command_id)
    status = status_at(control)
    if expected is not None and any(status[key] != expected[key] for key in ("processId", "instance", "worldEpoch", "recorderSession")):
        raise ContextChanged("game context changed before submission; no request was published")
    request = {"format": "forge.development-experiment-request", "schemaVersion": "1.0.0",
               "id": str(uuid.uuid4()), "processId": status["processId"], "instance": status["instance"],
               "worldEpoch": status["worldEpoch"], "recorderSession": status["recorderSession"],
               "commandId": command_id, "commandSha256": digest,
               "expiresMonotonicSeconds": monotonic_seconds() + 60}
    path = control / "request.forge"
    if os.path.lexists(path):
        raise ValueError("a request already exists; refusing to overwrite it")
    raw = json.dumps(request, separators=(",", ":")).encode("utf-8")
    if len(raw) > 16384:
        raise ValueError("request exceeds byte budget")
    temporary = control / (".request-" + request["id"] + ".tmp")
    with open(temporary, "xb") as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())
    physical(control)
    # Windows rename is atomic and refuses an existing destination. POSIX rename
    # overwrites, so use an atomic no-overwrite hard-link publish there.
    if os.name == "nt":
        os.rename(temporary, path)
    else:
        os.link(temporary, path)
        temporary.unlink()
    return control, request


def result_at(path, request, *, include_raw=False):
    result, raw = read_json(path)
    required = {"format", "schemaVersion", "id", "processId", "instance", "commandId", "commandSha256", "worldEpoch", "recorderSession", "runWorldEpoch", "runRecorderSession", "status", "reason", "evidenceScope", "utc"}
    if not isinstance(result, dict) or set(result) != required or result.get("format") != "forge.development-experiment-result" or result.get("schemaVersion") != "1.0.0":
        raise ValueError("unsupported result schema; regenerate with the current Development tools")
    for key in ("id", "processId", "instance", "commandId", "commandSha256", "worldEpoch", "recorderSession"):
        if result.get(key) != request[key]:
            raise ValueError("result identity mismatch: " + key)
    if result.get("evidenceScope") != "command-execution-only" or result.get("status") not in ("Passed", "Failed", "Aborted", "Rejected"):
        raise ValueError("invalid result evidence or terminal status")
    if type(result.get("runWorldEpoch")) is not int or not isinstance(result.get("runRecorderSession"), str):
        raise ValueError("missing real execution context")
    if result["status"] == "Passed" and (result["runWorldEpoch"] != request["worldEpoch"] or not result["runRecorderSession"] or
                                         (request["recorderSession"] and result["runRecorderSession"] != request["recorderSession"])):
        raise ValueError("Passed result has invalid real execution context")
    utc(result["utc"])
    return (result, raw) if include_raw else result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bepinex-root", required=True, type=Path)
    parser.add_argument("--command", required=True)
    parser.add_argument("--timeout", type=float, default=180)
    args = parser.parse_args(argv)
    try:
        if not 0 < args.timeout <= 14400:
            raise ValueError("timeout must be between 0 and 14400 seconds")
        control, request = submit(args.bepinex_root, args.command)
        print(json.dumps({"requestId": request["id"], "status": "submitted"}), flush=True)
        deadline = time.monotonic() + args.timeout
        path = control / "results" / (request["id"] + ".forge")
        while time.monotonic() < deadline:
            if os.path.lexists(path):
                result = result_at(path, request)
                print(json.dumps(result, ensure_ascii=False))
                return 0 if result["status"] == "Passed" else 2
            time.sleep(min(0.25, max(0, deadline - time.monotonic())))
        raise TimeoutError("request timed out; preserved request/history; game was not stopped and request was not resent")
    except (OSError, ValueError, KeyError, TypeError, TimeoutError) as error:
        print("experiment request failed: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
