"""Shared, stdlib-only Authoring session client. All game calls use ExperimentRunner."""
from __future__ import annotations

import datetime as dt
from contextlib import contextmanager
import gzip
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import time
import uuid

ROOT = next(p for p in Path(__file__).resolve().parents if (p / "Tools/Game").is_dir())  # this repository, or the public mirror, which keeps Tools/Game at its root too
# A copied tooling folder holds game_process.py, game_profile.py and safe_delete.py beside these tools; the repository keeps
# them under Tools/.
sys.path[:0] = [str(Path(__file__).resolve().parent), str(ROOT / "Tools/Game")]
import game_process as host  # noqa: E402
import game_profile  # noqa: E402
import rec_segments  # noqa: E402


def load_tool(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


transport = load_tool("run-experiment")


def encoded(value):
    return (json.dumps(value, ensure_ascii=False, allow_nan=False, indent=2) + "\n").encode("utf-8")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def document(path, maximum=4 * 1024 * 1024):
    value = transport.read_json(Path(path), maximum)[0]
    encoded(value)  # JSON's NaN/Infinity extensions are not accepted inputs.
    return value


def plain(path):
    path = Path(os.path.abspath(path))
    game_profile.require_plain_path(path)
    return path


def collect_drive_records(rows, recorder, folder, result):
    """Resolve compact step receipts to complete, byte-locked native action records."""
    receipt_fields = {"format", "schemaVersion", "operation", "code", "status", "commitState",
                      "record", "sha256", "session", "worldEpoch", "recordError", "summary"}
    record_fields = {"format", "schemaVersion", "record", "session", "worldEpoch", "operation", "code", "status",
                     "commitState", "nativePath", "request", "before", "after", "samples", "error"}
    operations = {"drive.player.teleport", "drive.player.unseat", "drive.scan.read", "drive.extraction.complete", "drive.model.shoot", "drive.variables.read"}
    for row in rows:
        if row.get("kind") != "step":
            continue
        body = row.get("body", {})
        detail = body.get("detail", "")
        if "forge.development-drive-receipt" not in detail and not any(operation in detail for operation in operations):
            continue
        start = detail.find("{")
        if start < 0:
            raise ValueError("驱动动作记录回执缺少 JSON")
        receipt = json.loads(detail[start:], object_pairs_hook=transport.unique_object)
        name = receipt.get("record")
        if (set(receipt) != receipt_fields or receipt["format"] != "forge.development-drive-receipt" or receipt["schemaVersion"] != "1.0.0"
                or receipt["operation"] not in operations or receipt["status"] not in ("observed", "failed", "aborted")
                or receipt["commitState"] not in ("confirmed", "unknown", "none")
                or body.get("ok") != (receipt["status"] == "observed")
                or receipt["session"] != result["runRecorderSession"]
                or type(receipt["worldEpoch"]) is not int or receipt["worldEpoch"] != result["runWorldEpoch"]
                or receipt["recordError"] is not None
                or not isinstance(name, str) or not re.fullmatch(r"authoring-drive-[0-9a-f]{32}\.forge", name)
                or not isinstance(receipt["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", receipt["sha256"])):
            raise ValueError("驱动动作记录回执的格式、身份或落盘状态不符")
        # The shared recorder queues the document before the step, so a missing file after the step
        # was drained is a failure, never a readback default or an invitation to rerun the action.
        value, raw = transport.read_json(plain(Path(recorder) / name), 8 * 1024 * 1024)
        encoded(value)  # Reject non-finite JSON extensions before accepting evidence.
        if (not isinstance(value, dict) or set(value) != record_fields
                or value["format"] != "forge.development-drive-record" or value["schemaVersion"] != "1.0.0"
                or digest(raw) != receipt["sha256"]
                or any(value[key] != receipt[key] for key in ("record", "session", "worldEpoch", "operation", "code", "status", "commitState"))):
            raise ValueError("驱动动作记录的格式、身份或 SHA-256 不符")
        destination = plain(Path(folder) / name)
        if destination.exists():
            if destination.read_bytes() != raw:
                raise ValueError("驱动动作证据目标已有不同内容，保留原字节")
        else:
            with destination.open("xb") as output:
                output.write(raw)


def output_root(path):
    """In the repository every drive output stays under artifacts/work/drivesession; a copied tooling folder writes where told."""
    path = plain(Path(path).absolute())
    if host.IN_REPOSITORY:
        path.relative_to(ROOT / "artifacts/work/drivesession")
    return path


def engine_installed(bep):
    """Forge Engine is optional: without it the drive enters vanilla and MTFO levels and skips its Forge-only reads."""
    return (Path(bep) / "plugins/NAinfini-ForgeEngine").is_dir()


class FileLease:
    """Durable byte/key receipts. Displaced QA bytes are moved to evidence, never deleted."""
    def __init__(self, bep, work):
        self.bep, self.work = plain(bep), plain(work)
        self.work.mkdir(parents=True, exist_ok=True)
        self.receipt = self.work / "lease.forge"
        self.rows = []
        self.trees = []
        if self.receipt.exists():
            saved = document(self.receipt, 256 * 1024 * 1024)
            if set(saved) != {"format", "schemaVersion", "bepinexRoot", "files", "trees"} or saved["format"] != "forge.drive-file-lease" or saved["schemaVersion"] != "1.0.0" or saved["bepinexRoot"] != str(self.bep):
                raise ValueError("租约身份或版本不匹配")
            self.rows = saved["files"]
            self.trees = saved["trees"]
            for row in self.rows:
                self.path(row["relative"])
            for tree in self.trees:
                self.path(tree["relative"])

    def path(self, relative):
        if not isinstance(relative, str) or "\\" in relative or any(x in ("", ".", "..") for x in relative.split("/")):
            raise ValueError("租约必须使用规范相对路径")
        path = plain(self.bep / relative)
        path.relative_to(self.bep)
        return path

    def save(self):
        host.atomic_json(self.receipt, {"format": "forge.drive-file-lease", "schemaVersion": "1.0.0",
            "bepinexRoot": str(self.bep), "files": self.rows, "trees": self.trees})

    def new_tree(self, relative):
        path = self.path(relative)
        if path.exists() or any(tree["relative"] == relative for tree in self.trees):
            raise ValueError("本轮专有目录已存在，拒绝接管")
        self.trees.append({"relative": relative, "archive": "tree-" + uuid.uuid4().hex, "restored": False})
        self.save()

    def track(self, relative, allowed=()):
        path = self.path(relative)
        row = next((row for row in self.rows if row["relative"] == relative), None)
        if row is None:
            original = path.read_bytes() if path.exists() else None
            row = {"relative": relative, "original": original.hex() if original is not None else None,
                   "allowed": list(allowed), "config": [], "restored": False}
            if original is not None:
                row["allowed"].append(digest(original))
            self.rows.append(row)
        row["allowed"] = sorted(set(row["allowed"]) | set(allowed))
        self.save()
        return path, row

    def write(self, relative, data):
        path, row = self.track(relative)
        if path.exists() and digest(path.read_bytes()) not in row["allowed"]:
            raise ValueError("租约文件被其他写入方更改：" + relative)
        row["allowed"] = sorted(set(row["allowed"]) | {digest(data)})
        self.save()  # Crash recovery exists before any profile write.
        path.parent.mkdir(parents=True, exist_ok=True)
        game_profile.atomic_bytes(path, data)

    def config(self, name, values):
        relative = "config/" + name
        path, row = self.track(relative)
        original = path.read_bytes().decode("utf-8") if path.exists() else ""
        text = original
        row["config"] = [{"section": section, "key": key, "before": host.cfg_value(original, section, key), "leased": value}
                         for section, key, value in values]
        for item in row["config"]:
            text = host.cfg_edit(text, item["section"], item["key"], item["leased"])
        self.write(relative, text.encode("utf-8"))

    def restore(self):
        failures = []
        for index, row in reversed(list(enumerate(self.rows))):
            if row["restored"]:
                continue
            try:
                path = self.path(row["relative"])
                original = bytes.fromhex(row["original"]) if row["original"] is not None else None
                current = path.read_bytes() if path.exists() else None
                desired = original
                recovery = row.get("restoring")
                if recovery:
                    desired = bytes.fromhex(recovery["desired"]) if recovery["desired"] is not None else None
                    if current in (None, desired):
                        if current is None and desired is not None:
                            path.parent.mkdir(parents=True, exist_ok=True)
                            game_profile.atomic_bytes(path, desired)
                        if (path.read_bytes() if path.exists() else None) != desired:
                            raise ValueError("恢复还原事务后的字节读回不符")
                        row["restored"] = True
                        self.save()
                        continue
                    if digest(current) != recovery["fromSha256"]:
                        raise ValueError("还原期间文件被其他写入方修改")
                if not recovery and row["config"] and current is not None and digest(current) not in row["allowed"]:
                    text = current.decode("utf-8")
                    for item in row["config"]:
                        if host.cfg_value(text, item["section"], item["key"]) != item["leased"]:
                            raise ValueError("租用配置键已被其他写入方修改")
                    for item in row["config"]:
                        text = host.cfg_edit(text, item["section"], item["key"], item["before"])
                    baseline = (original or b"").decode("utf-8")
                    desired = original if sorted(host.cfg_entries(text)) == sorted(host.cfg_entries(baseline)) else text.encode("utf-8")
                elif not recovery and current != original and (current is None or digest(current) not in row["allowed"]):
                    raise ValueError("文件缺失或内容锁冲突；保留当前字节与租约")
                if current != desired:
                    row["restoring"] = {"desired": desired.hex() if desired is not None else None,
                                        "fromSha256": digest(current) if current is not None else None}
                    self.save()
                    if current is not None:
                        displaced = self.work / "displaced" / str(index) / str(time.time_ns()) / path.name
                        displaced.parent.mkdir(parents=True, exist_ok=True)
                        path.rename(displaced)
                    if desired is not None:
                        path.parent.mkdir(parents=True, exist_ok=True)
                        game_profile.atomic_bytes(path, desired)
                if (path.read_bytes() if path.exists() else None) != desired:
                    raise ValueError("还原字节读回不一致")
                row["restored"] = True
                self.save()
            except (OSError, ValueError, RuntimeError) as error:
                failures.append(row["relative"] + ": " + str(error))
        if failures:
            raise RuntimeError("清理失败：" + "; ".join(failures))
        for tree in self.trees:
            if tree["restored"]:
                continue
            path, archive = self.path(tree["relative"]), plain(self.work / tree["archive"])
            archive.relative_to(self.work)
            if path.exists():
                list(game_profile.plain_tree(path))
                path.rename(archive)  # Includes native swap receipts; nothing in the QA profile is deleted.
            tree["restored"] = True
            self.save()


class ExperimentFailed(RuntimeError):
    def __init__(self, result):
        self.result = result
        super().__init__(result["commandId"] + ": " + result["status"] + ": " + result["reason"])


# The game refused a request before running it because its world or recorder session moved after the driver read them
# (creating or leaving a lobby, a level build, a recorder renewal). Nothing ran, so the driver may read the new identity and ask again.
STALE_REJECTIONS = ("request identity changed", "request context changed before admission")
# A read the world outran mid-run; a readiness poll reads again instead of failing.
STALE_READS = ("experiment.read-world-changed", "experiment.read-session-changed")


EVIDENCE_STALL_SECONDS = 15  # A recording that has not grown for this long while rows are missing is stuck, not slow.


class IdentityMoved(ValueError):
    """The world or recorder session moved before the game ran the request; no command ran."""


class Client:
    def __init__(self, bep, work, identity=None, timeout=180):
        self.bep, self.work = plain(bep), plain(work)
        self.identity, self.timeout = identity, timeout
        self.lease = FileLease(self.bep, self.work / "command-lease")
        self.offsets = {}
        self.command_file = "ForgeDevelopment/commands/drivesession-" + uuid.uuid4().hex + ".forge"
        self._started = time.monotonic()
        self._last_status = None
        self.echo = False  # Set by drive_cli: every trace row is also printed to stderr so a long wait is visible while it runs.
        self.deadline = None  # Absolute monotonic bound of a multi-command flow such as enter_level; None means per-command limits only.

    def trace(self, kind, **values):
        # External driver diagnostics: no work, callbacks or per-frame logging on the game's CPU budget.
        self.work.mkdir(parents=True, exist_ok=True)
        row = {"format": "forge.development-drive-timing", "schemaVersion": "1.0.0", "kind": kind,
               "atMs": round((time.monotonic() - self._started) * 1000, 3),
               "monotonicSeconds": transport.monotonic_seconds(), **values}
        with (self.work / "driver-timing.jsonl").open("ab") as stream:
            stream.write(json.dumps(row, ensure_ascii=False, allow_nan=False).encode("utf-8") + b"\n")
        if self.echo:
            print(json.dumps({"trace": kind, **{k: v for k, v in values.items() if k != "reasons"}}, ensure_ascii=False), file=sys.stderr, flush=True)

    @contextmanager
    def phase(self, name, **values):
        started = time.monotonic()
        reads_before = transport.READ_RETRIES.copy()
        try:
            yield values
        except BaseException as error:
            values.update(error=str(error), outcome="failed")
            raise
        else:
            values["outcome"] = "completed"
        finally:
            values["readRetries"] = {key: count - reads_before[key] for key, count in transport.READ_RETRIES.items()}
            self.trace(name, elapsedMs=round((time.monotonic() - started) * 1000, 3), **values)

    def observe_status(self, value):
        if value is None:
            return
        state = {key: value.get(key) for key in ("processId", "instance", "worldEpoch", "recorderSession",
                                                "gameState", "activeCommand", "stopped")}
        if state != self._last_status:
            self.trace("status", **state)
            self._last_status = state

    def recorder_root(self, session):
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,160}", session):
            raise ValueError("原始记录会话名非法")
        return plain(self.bep / "ForgeReports" / ("rec-" + session))

    def snapshot_offsets(self, status):
        # Only the frozen pre-command recorder can contain earlier runs of this command.
        # A recorder started by this command has no earlier run and is read from offset zero.
        # Offsets are into a segment's logical bytes. A packed segment is closed and wholly before this command, so it
        # is marked read (None) without touching it; only the open plain segment can still grow, and only it is checked
        # here (evidence() checks every file it opens). Checking every packed file's whole path took 4-9 s on a session of
        # 248 segments and ate the console's 30 s submission budget.
        self.offsets = {}
        session = status["recorderSession"]
        if session:
            for path in rec_segments.segments(self.recorder_root(session)):
                if not path.exists():
                    self.offsets[path] = None
                    continue
                plain(path)
                try:
                    self.offsets[path] = path.stat().st_size
                except FileNotFoundError:
                    self.offsets[path] = None

    def status(self, *, observing=False, deadline=None):
        deadline = min(time.monotonic() + 300, deadline) if deadline is not None else time.monotonic() + 300
        started, retries, slept = time.monotonic(), 0, 0.0
        reads_before = transport.READ_RETRIES.copy()
        reasons = {}
        try:
            while True:
                try:
                    value = transport.status_at(self.bep / "ForgeDevelopment/experiments/control",
                                                **({"idle": False, "maximum_age": None} if observing else {}))
                    self.observe_status(value)
                    break
                except transport.StatusNotReady as error:
                    self.observe_status(error.status)
                    retries += 1
                    reasons[str(error)] = reasons.get(str(error), 0) + 1
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise TimeoutError("status-ready-timeout: " + str(error)) from error
                    delay = min(.1, remaining)
                    time.sleep(delay)
                    slept += delay
        except BaseException as error:
            self.trace("status-refused", error=str(error))
            raise
        finally:
            read_retries = {key: count - reads_before[key] for key, count in transport.READ_RETRIES.items()}
            if retries or any(read_retries.values()):
                self.trace("status-wait", elapsedMs=round((time.monotonic() - started) * 1000, 3),
                           retries=retries, reasons=reasons, readRetries=read_retries, pollSleepMs=round(slept * 1000, 3))
        if self.identity and any(value[key] != self.identity[key] for key in ("processId", "instance")):
            raise ValueError("游戏进程或实例已更换，拒绝接管")
        return value

    def execute(self, command_id, world=None, *, deadline=None):
        deadline = deadline if deadline is not None else self.deadline
        # A caller that pinned a world must not follow it; otherwise a request the world outran is asked again under the new identity.
        retry_until = min(time.monotonic() + 60, deadline) if deadline is not None else time.monotonic() + 60
        while True:
            try:
                with self.phase("command", command=command_id):
                    return self._execute(command_id, world, deadline=deadline)
            except (transport.ContextChanged, IdentityMoved) as error:
                if world is not None or time.monotonic() >= retry_until:
                    raise
                self.trace("identity-moved", command=command_id, reason=str(error))

    def _execute(self, command_id, world=None, *, deadline=None):
        ready_deadline = min(time.monotonic() + 300, deadline) if deadline is not None else time.monotonic() + 300
        with self.phase("status-admission", command=command_id):
            before = self.status(deadline=ready_deadline)
        if world is not None and before["worldEpoch"] != world:
            raise ValueError("本场世界已改变；拒绝沿用旧输入")
        # Save offsets before submission; repeated shipped IDs in one second remain distinguishable.
        with self.phase("recorder-offsets", command=command_id) as timing:
            self.snapshot_offsets(before)
            timing.update(session=before["recorderSession"], files=len(self.offsets))
        last_reason = "game status did not become ready before the deadline"
        with self.phase("request-publication", command=command_id, retries=0, pollSleepMs=0) as timing:
            while True:
                if time.monotonic() >= ready_deadline:
                    raise TimeoutError("submit-ready-timeout: " + last_reason)
                try:
                    control, request = transport.submit(self.bep, command_id, expected=before)
                    timing["request"] = request["id"]
                    break
                except transport.StatusNotReady as error:
                    timing["retries"] += 1
                    last_reason = timing["lastReason"] = str(error)
                    self.observe_status(error.status)
                    remaining = ready_deadline - time.monotonic()
                    if remaining <= 0:
                        raise TimeoutError("submit-ready-timeout: " + str(error)) from error
                    delay = min(.1, remaining)
                    time.sleep(delay)
                    timing["pollSleepMs"] += delay * 1000
                    current = self.status(deadline=ready_deadline)
                    if any(current[key] != before[key] for key in ("processId", "instance")):
                        raise ValueError("等待提交期间游戏进程已改变；未发出请求")
                    if any(current[key] != before[key] for key in ("worldEpoch", "recorderSession")):
                        raise IdentityMoved("等待提交期间世界或记录会话已改变；未发出请求")
                    before = current
        if any(request[key] != before[key] for key in ("processId", "instance", "worldEpoch", "recorderSession")):
            raise ValueError("提交期间游戏身份改变；请求保留，不重发")
        folder = self.work / "requests" / request["id"]
        folder.mkdir(parents=True)
        (folder / "request.forge").write_bytes(encoded(request))
        deadline = min(time.monotonic() + self.timeout, deadline) if deadline is not None else time.monotonic() + self.timeout
        result_path = control / "results" / (request["id"] + ".forge")
        result_started, polls = time.monotonic(), 0
        while time.monotonic() < deadline:
            polls += 1
            if result_path.exists():
                result, raw = transport.result_at(result_path, request, include_raw=True)
                (folder / "result.forge").write_bytes(raw)
                self.trace("result-received", command=command_id, request=request["id"], polls=polls,
                           elapsedMs=round((time.monotonic() - result_started) * 1000, 3), status=result["status"])
                if result["status"] == "Rejected" and result["reason"] in STALE_REJECTIONS:
                    raise IdentityMoved(command_id + ": " + result["reason"] + "（未执行）")
                if result["status"] != "Passed":
                    raise ExperimentFailed(result)
                with self.phase("evidence", command=command_id, request=request["id"]) as timing:
                    rows = self.evidence(result, folder)
                    timing["nativeMs"] = next((row["body"].get("ms") for row in rows if row.get("kind") == "result"), None)
                print(json.dumps({"command": command_id, "status": result["status"], "request": request["id"],
                                  "world": result["runWorldEpoch"], "evidenceScope": result["evidenceScope"]}, ensure_ascii=False), flush=True)
                return result, rows
            time.sleep(.1)
        raise TimeoutError("实验超时；请求及历史保留，不重发：" + request["id"])

    def evidence(self, result, folder):
        session = result["runRecorderSession"]
        root = self.recorder_root(session)
        rows = []
        # The result file is published once the rows are queued; the recorder writes them behind whatever it queued
        # earlier (a datablock snapshot of several MB at selection). Wait while the recording still grows, give up after
        # EVIDENCE_STALL_SECONDS without growth or the command timeout.
        hard = time.monotonic() + self.timeout
        progress, grown = None, time.monotonic()
        while True:
            paths = rec_segments.segments(root)
            try:
                mark = (len(paths), rec_segments.physical(paths[-1]).stat().st_size) if paths else None
            except FileNotFoundError:
                mark = None  # The open segment was being packed; the next poll sees the packed file.
            if mark != progress:
                progress, grown = mark, time.monotonic()
            for path in paths:
                offset = self.offsets.get(path, 0)
                if offset is None:
                    continue
                plain(rec_segments.physical(path))
                with rec_segments.open_segment(path) as stream:
                    packed = isinstance(stream, gzip.GzipFile)
                    first = stream.readline(1024 * 1024 + 1)
                    if not first.endswith(b"\n"):
                        continue  # The header of a just-opened segment is not on disk yet.
                    shared = rec_segments.header(first)
                    offset = max(offset, len(first))
                    stream.seek(offset - 1)
                    if offset and stream.read(1) != b"\n":
                        # The pre-command size may be inside a record still being written.
                        # Keep that offset until its newline exists; never parse a suffix as JSON.
                        skipped = stream.readline(1024 * 1024 + 1)
                        if len(skipped) > 1024 * 1024:
                            raise ValueError("原始记录超过预算")
                        if not skipped.endswith(b"\n"):
                            continue
                    while True:
                        start = stream.tell()
                        line = stream.readline(1024 * 1024 + 1)
                        if len(line) > 1024 * 1024:
                            raise ValueError("原始记录超过预算")
                        if not line or not line.endswith(b"\n"):
                            # A packed segment never grows again: once read to its end it is done.
                            self.offsets[path] = None if packed and not line else start
                            break
                        row = rec_segments.complete(json.loads(line, object_pairs_hook=transport.unique_object), shared)
                        self.offsets[path] = stream.tell()
                        if row.get("channel") == "exp" and row.get("body", {}).get("command") == result["commandId"]:
                            if row.get("session") != session:
                                raise ValueError("原始记录身份冲突")
                            rows.append(row)
            if any(row.get("kind") == "cleanup" for row in rows):
                break
            now = time.monotonic()
            if now - grown >= EVIDENCE_STALL_SECONDS or now >= hard:
                found = sorted({row.get("kind") for row in rows})
                raise ValueError("执行后原始记录读回超时：" + result["commandId"] + " 已有 " + (",".join(found) or "无")
                                 + " 行，缺 cleanup；记录" + ("已 " + format(now - grown, ".0f") + " 秒没有增长" if now < hard
                                                            else "仍在增长但达到命令超时 " + format(self.timeout, ".0f") + " 秒"))
            time.sleep(.1)
        (folder / "steps.jsonl").write_bytes(b"".join(encoded(row).replace(b"\n", b" ") + b"\n" for row in rows))
        terminal = [row for row in rows if row.get("kind") == "result"]
        cleanup = [row for row in rows if row.get("kind") == "cleanup"]
        commands = [row for row in rows if row.get("kind") == "command"]
        steps = [row for row in rows if row.get("kind") == "step"]
        runs = {row["body"].get("run") for row in rows}
        expected_status = result["status"].lower()
        if (len(commands) != 1 or not steps or len(runs) != 1 or None in runs
                or len(terminal) != 1 or terminal[0]["body"].get("status") != expected_status
                or len(cleanup) != 1 or not cleanup[0]["body"].get("objects")
                or "failures:" in cleanup[0]["body"].get("objects", "")):
            raise ValueError("执行后原始结果/清理读回丢失或失败")
        for row in rows:
            detail = row["body"].get("detail", "")
            if row.get("kind") == "step" and ((expected_status == "passed" and not row["body"].get("ok")) or detail.endswith("…")):
                raise ValueError("原生步骤失败或取证被截断")
        collect_drive_records(rows, root, folder, result)
        return rows

    def command(self, target, steps, world=None, host_role="any", timeout=180):
        command_id = "ds-" + uuid.uuid4().hex
        value = {"id": command_id, "title": "本次作者会话操作", "target": target, "host": host_role,
                 "timeoutSeconds": timeout, "steps": steps}
        with self.phase("command-write", command=command_id):
            self.lease.write(self.command_file, encoded(value))
        return self.execute(command_id, world)

    def read(self, target, paths, world=None):
        _, rows = self.command(target, [{"read": paths}], world)
        readings = [row["body"]["detail"].split(" :: ", 1)[1] for row in rows
                    if row.get("kind") == "step" and row["body"].get("step") == "read"]
        if len(readings) != 1:
            raise ValueError("原生读取缺失")
        return readings[0]

    def query(self, target, steps, world=None):
        """Last native read, for a getter/result chain; never a terminal message approximation."""
        _, rows = self.command(target, steps, world)
        readings = [row["body"]["detail"].split(" :: ", 1)[1] for row in rows
                    if row.get("kind") == "step" and row["body"].get("step") == "read"]
        if not readings:
            raise ValueError("执行后的原生读回缺失")
        return readings[-1]

    def call(self, target, method, args=(), via=None, timeout=180):
        steps = ([{"call": via}, {"target": "result", "call": method, "args": list(args)}] if via
                 else [{"call": method, "args": list(args)}])
        _, rows = self.command(target, steps, timeout=timeout)
        calls = [row["body"]["detail"].split(" -> ", 1)[1] for row in rows
                 if row.get("kind") == "step" and row["body"].get("step") == "call"]
        if not calls:
            raise ValueError("原生调用读回缺失")
        return calls[-1]

    def close(self):
        self.lease.restore()


DRIVE = "static:ForgeDevelopment.Native.AuthoringGameDrive"


STEP_SECONDS = 180  # The auto test's budget for its menu, lobby and select stages; a stuck gate fails here, not after the whole session budget.
WAIT_REPORT_SECONDS = 15
DROP_ATTEMPTS = 12  # one press plus up to eleven repeats, each after DROP_RETRY_SECONDS in the lobby
DROP_READBACK_SECONDS = 20
DROP_RETRY_SECONDS = 10
MENU = "static:ForgeDevelopment.Native.MenuDrive"


def native_lobby_state(client):
    """Native readback of the menu, lobby and slot facts; a failed readback is itself the report."""
    saved = client.deadline
    client.deadline = time.monotonic() + 30  # The flow's own bound has usually expired by now; the diagnosis gets its own.
    try:
        return client.call(MENU, "LobbyState")
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        return "游戏状态读回失败：" + str(error)
    finally:
        client.deadline = saved


def wait_until(client, predicate, seconds, label, describe=None):
    """Poll a native readback until it is true. A command that merely ran proves nothing; the predicate must read the
    game state it waits for. On timeout the error names the step and, through describe, what the game reports."""
    started = time.monotonic()
    deadline = started + seconds
    polls, stale, busy, checks, slept = 0, 0, 0, 0, 0.0
    ready = False
    next_report = started + WAIT_REPORT_SECONDS
    try:
        while time.monotonic() < deadline:
            status = client.status(observing=True, deadline=deadline)
            if time.monotonic() >= next_report:  # A long wait states what it waits for and what the game reports, queryable while it runs.
                client.trace("waiting", label=label, elapsedMs=round((time.monotonic() - started) * 1000, 3),
                             gameState=status["gameState"], activeCommand=status["activeCommand"] or None)
                next_report += WAIT_REPORT_SECONDS
            polls += 1
            fresh = transport.status_age(status) <= 3
            stale += int(not fresh)
            busy += int(bool(status["activeCommand"]))
            # During generation the native thread can be blocked. A stale heartbeat is a wait, never permission
            # to submit a request. Submission retains the transport's original three-second freshness gate.
            if fresh and not status["activeCommand"]:
                checks += 1
                try:
                    met = predicate()
                except ExperimentFailed as error:
                    # The world the readback named was replaced mid-read (a lobby being created, a level building): not yet.
                    if not any(reason in error.result["reason"] for reason in STALE_READS):
                        raise
                    client.trace("readback-world-moved", label=label, reason=error.result["reason"][:200])
                    met = False
                if met:
                    ready = True
                    return
            delay = min(.5, max(0, deadline - time.monotonic()))
            time.sleep(delay)
            slept += delay
        detail = describe() if describe else ""
        raise TimeoutError(label + "超时（" + format(seconds, ".0f") + " 秒，轮询 " + str(checks) + " 次均未满足）"
                           + ("：游戏读回 " + detail if detail else ""))
    finally:
        client.trace("readiness", label=label, elapsedMs=round((time.monotonic() - started) * 1000, 3),
                     ready=ready, polls=polls, stale=stale, busy=busy, predicateChecks=checks,
                     pollSleepMs=round(slept * 1000, 3))


def landing(client, rundown, tier, ordinal, timeout):
    wait_until(client, lambda: client.status()["gameState"] == "InLevel"
        and client.call(MENU, "LandingReady") == "true", timeout, "关卡落地",
        lambda: "gameState=" + str(client.status()["gameState"]) + "；" + native_lobby_state(client))
    if client.call(MENU, "ActiveMatches", [rundown, tier, ordinal]) != "true":
        raise ValueError("落地关卡身份不匹配")
    def playable():
        paths = ["Alive", "IsEnabled", "PlayerCharacterController.enabled",
                 "PlayerCharacterController.gameObject.activeInHierarchy",
                 "PlayerCharacterController.m_characterController.enabled",
                 "PlayerCharacterController.m_characterController.gameObject.activeInHierarchy", "Locomotion.m_currentStateEnum"]
        reading = dict(field.split("=", 1) for field in client.read("localPlayer", paths).split("; "))
        return all(reading[path] == "true" for path in paths[:-1]) and reading[paths[-1]].split("/")[-1] in ("Stand", "Crouch")
    wait_until(client, playable, timeout, "本地玩家离座并可操作")


def return_to_lobby(client, wait=None):
    """The native return from a level or its result screen to the lobby, then the floor-cleanup readback. `wait(predicate, label)`
    bounds the wait (enter_level passes its own step); by default it is the drive's STEP_SECONDS."""
    wait = wait or (lambda predicate, label: wait_until(client, predicate, STEP_SECONDS, label))
    client.execute("reload-drive-return-lobby")
    wait(lambda: client.status()["gameState"] == "Lobby" and "floorPresent=False" in client.call(DRIVE, "Lifecycle"), "原生退关与楼层清理")
    if engine_installed(client.bep):
        client.execute("reload-lifecycle-counts")  # the Forge kernel's world and queue sizes after the cleanup
    else:
        client.trace("lifecycle-counts-skipped", reason="forge-engine-not-installed")


def enter_level(client, rundown, tier, ordinal, timeout, bot_slots="[]"):
    """Drives the private lobby to a landed level with the auto test's lobby gates (MenuDrive.LobbyPending
    is the same roster check, including the level's declared bot slots, passed as the JSON array of its bot-slots.forge
    rows). tier is the native eRundownTier value, 1 for A. timeout bounds the whole entry, and every lobby step is
    additionally bounded by STEP_SECONDS."""
    started = time.monotonic()
    total = started + timeout
    client.deadline = total
    client.trace("drop-start", rundown=rundown, tier=tier, ordinal=ordinal)

    def remaining(label):
        left = total - time.monotonic()
        if left <= 0:
            raise TimeoutError("进关总超时（" + format(timeout, ".0f") + " 秒）：尚未完成「" + label + "」；游戏读回 " + native_lobby_state(client))
        return left

    def step(predicate, label, describe=None, bounded=True):
        left = remaining(label)
        wait_until(client, predicate, min(STEP_SECONDS, left) if bounded else left, label,
                   describe or (lambda: native_lobby_state(client)))

    try:
        state = client.status()["gameState"]
        # The result screen quits through the same native return as the level (the expedition stays active until the quit).
        if state in ("InLevel", "ExpeditionSuccess", "ExpeditionFail"):
            return_to_lobby(client, lambda predicate, label: step(predicate, label, bounded=False))
            state = "Lobby"
        # A lobby keeps the rundown it was created with; another rundown's level needs a new lobby.
        if state == "Lobby" and client.call(MENU, "LobbyRundownIs", [rundown]) == "false":
            client.execute("reload-drive-leave-lobby")
            step(lambda: client.status()["gameState"] == "NoLobby"
                and client.read("static:SNetwork.SNet", ["IsInLobby"]) == "IsInLobby=false", "离开大厅以换 Rundown")
            state = "NoLobby"
        if state != "Lobby":
            step(lambda: client.call(DRIVE, "StartupReady", via="Menu") == "true", "原生启动菜单")
            client.call(DRIVE, "CreatePrivateLobby", [rundown], via="Menu")
        step(lambda: client.status()["gameState"] == "Lobby"
            and client.read("static:SNetwork.SNet", ["IsMaster", "IsInLobby"]) == "IsMaster=true; IsInLobby=true", "私有大厅")
        client.call(DRIVE, "SelectExpedition", ["authoring-session", rundown, tier, ordinal], via="Menu")
        selected = time.monotonic()
        client.trace("selection-issued", elapsedMs=round((selected - started) * 1000, 3))
        expected = "期望选择 " + str(rundown) + "/Local/" + str(tier) + "/" + str(ordinal) + "；实际 "
        step(lambda: client.call(MENU, "ActiveMatches", [rundown, tier, ordinal]) == "true"
            and client.read("static:CellMenu.CM_PageLoadout", ["Current.m_readyButton.m_collider.enabled"]).endswith("=true"),
            "选关与准备按钮", lambda: expected + native_lobby_state(client))
        client.trace("loadout-ready", elapsedMs=round((time.monotonic() - selected) * 1000, 3))
        pending = []
        def roster():
            pending[:] = [client.call(MENU, "LobbyPending", [rundown, tier, ordinal, bot_slots])]
            return pending[0] == "ready"
        step(roster, "房间名册与机器人槽位", lambda: (pending[0] if pending else "无读回") + "；" + native_lobby_state(client))
        client.call("static:CellMenu.CM_PageLoadout", "OnReadyButtonClicked", via="Current")
        step(lambda: client.read("static:GameStateManager", ["IsReady"]) == "IsReady=true"
            and client.call("static:GameStateManager", "IsEveryoneReady", [False]) == "true"
            and client.read("static:CellMenu.CM_PageLoadout", ["Current.m_dropButton.m_collider.enabled"]).endswith("=true"),
            "全员准备与下落按钮")
        # The game only leaves the lobby once every player in the hub (a bot the slot closing just kicked included) reports the
        # InSessionHub_ReadyToGenerate mode; an earlier press only logs "some players arent ready". Wait for that fact, and when the
        # lobby is still there a few seconds after a press, press again: the press itself returns at once, so its readback gets a short limit.
        try:
            wait_until(client, lambda: client.call(MENU, "StartPending") == "ready", 25, "全员会话模式就绪（InSessionHub_ReadyToGenerate）",
                       lambda: client.call(MENU, "StartPending") + "；" + native_lobby_state(client))
        except TimeoutError as error:  # the repeated press below is the fallback; the unmet fact is on the trace
            client.trace("start-pending-unmet", reason=str(error)[:300])
        for attempt in range(1, DROP_ATTEMPTS + 1):
            try:
                client.call("static:CellMenu.CM_PageLoadout", "EXT_StartExpedition", via="Current", timeout=DROP_READBACK_SECONDS)
                break
            except (TimeoutError, RuntimeError, ValueError) as error:
                try: state = client.status(observing=True)["gameState"]
                except (OSError, ValueError, TimeoutError): state = "unreadable"  # a blocked native thread is the level being generated
                if state != "Lobby": break
                if attempt == DROP_ATTEMPTS: raise
                client.trace("drop-retry", attempt=attempt, reason=str(error)[:200])
                time.sleep(DROP_RETRY_SECONDS)
        requested = time.monotonic()
        client.trace("drop-issued", elapsedMs=round((requested - selected) * 1000, 3))
        landing(client, rundown, tier, ordinal, remaining("关卡落地"))
        client.trace("player-ready", elapsedMs=round((time.monotonic() - requested) * 1000, 3),
                     totalElapsedMs=round((time.monotonic() - started) * 1000, 3))
    finally:
        client.deadline = None

def inventory(client, destination):
    status = client.status()
    world = status["worldEpoch"]
    if status["gameState"] != "InLevel":
        raise ValueError("运行态清点需要已落地关卡")
    # One read action resolves each set once, keeping its #index identities aligned.
    value = {"format": "forge.drive-inventory", "schemaVersion": "1.0.0", "processId": status["processId"],
             "instance": status["instance"], "worldEpoch": world, "recorderSession": status["recorderSession"],
             "utc": dt.datetime.now(dt.timezone.utc).isoformat(), "evidenceScope": "native-readback-only"}
    value["player"] = client.read("localPlayer", ["GlobalID", "Position", "DimensionIndex", "Alive"], world)
    value["enemies"] = client.read("allOfType:Enemies.EnemyAgent", ["GlobalID", "EnemyDataID", "Position", "DimensionIndex", "Alive"], world)
    value["doors"] = client.read("allOfType:LevelGeneration.LG_Gate", ["name", "Type", "transform.position", "transform.rotation"], world)
    if client.status()["worldEpoch"] != world:
        raise ValueError("清点途中世界已变化")
    host.atomic_json(destination, value)
    return value


def from_session(path, work):
    value = document(path)
    if value.get("format") != "forge.drive-session" or value.get("schemaVersion") != "1.0.0" or value.get("phase") != "ready":
        raise ValueError("会话尚未落地、已结束或版本不受支持")
    return Client(Path(value["bepinexRoot"]), work, value), value