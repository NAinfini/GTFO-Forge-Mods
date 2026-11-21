"""Running a test script on a session and writing what happened: one folder per run.

    <work-dir>/console/<start time>/
        commands.jsonl        every script line as run (text, kind, times, and what an expect or wait read); `replay` runs these lines again
        NNNN-<verb>/          one command's result.json and log.txt (the log lines before it, then the ones it added)
        run.json, run.md      the result, format forge.console-run 1.0.0, and the same for a person to read

The folder lives under artifacts and is never committed. A recorded session can also be judged again without the game
(`replay --recorded`): the commands answer what they answered then, and every expect is evaluated anew against what it read then, so a
changed or wrong expectation is caught by the same code that judged it live."""
from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
import time

import console_commands as cc
import test_script as ts

FORMAT, VERSION = "forge.console-run", "1.0.0"
READING_LIMIT = 4000


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="milliseconds")


def text_of(value, limit=600):
    text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, sort_keys=True)
    return text if len(text) <= limit else text[:limit] + "…"


def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", "utf-8")


class RecordedConsole:
    """The console of a recorded session: commands answer what they answered then, and a reading answers what it read then."""
    class Log:
        def mark(self):
            return 0

        def lines(self, start, end=None):
            return []

        def before(self, start, count=20):
            return []

    def __init__(self, folder):
        self.folder = Path(folder)
        self.entries = [json.loads(row) for row in (self.folder / "commands.jsonl").read_text("utf-8").splitlines() if row.strip()]
        self.log = self.Log()
        self.results = [entry for entry in self.entries if entry["kind"] == "command"]
        self.readings = [entry for entry in self.entries if entry["kind"] in ("expect", "wait")]
        self.next_result = self.next_reading = 0

    def execute(self, line):
        entry = self.results[self.next_result]
        self.next_result += 1
        if entry["text"] != line.text:
            raise ValueError("录制的第 " + str(self.next_result) + " 条指令是“" + entry["text"] + "”，脚本里是“" + line.text + "”")
        recorded = json.loads((self.folder / entry["folder"] / "result.json").read_text("utf-8"))
        return cc.Result(recorded["ok"], recorded["values"], error=recorded.get("error", ""))

    def value_of(self, reader, variables, log_lines):
        if reader[0].startswith("$"):
            name, _, path = reader[0][1:].partition(".")
            return ts.lookup(variables.get(name, {}), path), None
        entry = self.readings[self.next_reading]
        self.next_reading += 1
        return entry.get("reading"), None

    def run(self, steps, timeout=180):
        return []


def package_rows(state):
    selection = state.get("selection") or {}
    plugin = Path(state["bepinexRoot"]) / "plugins" / selection.get("pluginDirectory", "")
    version = ""
    try:
        version = json.loads((plugin / "manifest.json").read_text("utf-8-sig")).get("version_number", "")
    except (OSError, ValueError):
        pass
    return [{"id": selection.get("packageId", ""), "version": version, "sha256": selection.get("contentSha256", "")}] if selection.get("packageId") else []


class Run:
    def __init__(self, script, console, folder, state=None, label=""):
        self.script, self.console, self.folder = script, console, Path(folder)
        self.state = state or {}
        self.variables, self.rows, self.stop = {}, [], False
        self.seq = 0
        self.commands = self.folder / "commands.jsonl"

    def note(self, entry):
        with self.commands.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(entry, ensure_ascii=False, sort_keys=True) + "\n")

    def run(self):
        self.folder.mkdir(parents=True, exist_ok=True)
        started = utc()
        session_mark = command_mark = self.console.log.mark()
        for line in self.script.lines:
            if self.stop and self.rows and not self.rows[-1]["ok"]:
                break
            begun, clock = utc(), time.monotonic()
            entry = {"line": line.number, "text": line.text, "kind": "command" if line.kind == "command" else line.kind, "startedAt": begun}
            if line.kind == "stop-on-fail":
                self.stop = True
                entry["ended"] = utc()
                self.note(entry)
                continue
            row = {"line": line.number, "text": line.text, "kind": line.kind, "ok": True, "actual": "", "expected": "", "detail": "", "log": "", "screenshot": ""}
            try:
                if line.kind == "command":
                    command_mark = self.command(line, row, entry)
                elif line.kind == "expect":
                    self.expect(line, row, entry, session_mark, command_mark)
                else:
                    self.wait(line, row, entry, session_mark, command_mark)
            except (KeyError, ValueError, IndexError) as error:
                row.update(ok=False, detail=row["detail"] or str(error) or type(error).__name__)
            except Exception as error:  # 会话本身出错（超时、实验失败、读写失败）：这一行记失败，后面不再跑，结果照样写出
                row.update(ok=False, detail=type(error).__name__ + ": " + str(error))
                self.stop = True
            row["ms"] = round((time.monotonic() - clock) * 1000)
            entry["endedAt"] = utc()
            entry["ok"] = row["ok"]
            self.note(entry)
            self.rows.append(row)
        return self.finish(started)

    def command(self, line, row, entry):
        mark = self.console.log.mark()
        result = self.console.execute(line)
        added = self.console.log.lines(mark)
        self.seq += 1
        folder = "{:04d}-{}".format(self.seq, line.verb)
        (self.folder / folder).mkdir(exist_ok=True)
        (self.folder / folder / "log.txt").write_text("\n".join(self.console.log.before(mark)) + "\n--- 指令开始 ---\n"
                                                      + ("（日志读不到或被重写）" if added is None else "\n".join(added)) + "\n", "utf-8")
        write_json(self.folder / folder / "result.json", {"seq": self.seq, "line": line.number, "text": line.text, "ok": result.ok, "values": result.values,
                                                          "error": result.error})
        entry["folder"] = folder
        row.update(ok=result.ok, actual=text_of(result.values), detail=result.error, log=folder + "/log.txt")
        shot = result.values.get("result")
        if isinstance(shot, dict) and "screenshot" in shot:
            row["screenshot"] = shot["screenshot"]
        if line.name and result.ok:
            self.variables[line.name] = result.values
        return mark

    def reading(self, line, session_mark, command_mark):
        condition = line.condition
        reader = condition.reader[:1] + [ts.expand(token, self.variables) for token in condition.reader[1:]]  # the first word is a verb or the variable itself
        # Read the log now, not when the command returned: a line the game writes a frame later still belongs to that command.
        log_lines = self.console.log.lines(session_mark if line.every else command_mark)
        actual, _ = self.console.value_of(reader, self.variables, log_lines)
        return actual, ts.expand(condition.value, self.variables)

    def expect(self, line, row, entry, session_mark, command_mark):
        actual, expected = self.reading(line, session_mark, command_mark)
        entry["reading"] = text_of(actual, READING_LIMIT) if actual is not None else None
        row.update(actual=text_of(actual if actual is not None else "（读不到）"), expected=line.condition.comparator + " " + expected,
                   ok=ts.judge(actual, line.condition.comparator, line.condition.tolerance, expected))
        if not row["ok"]:
            row["detail"] = "读到 " + text_of(actual, 200) + "，要求 " + row["expected"]

    def wait(self, line, row, entry, session_mark, command_mark):
        if line.amount:
            amount, unit = line.amount
            row["expected"] = text_of(amount) + unit
            if unit == "s":
                if not isinstance(self.console, RecordedConsole):
                    time.sleep(amount)
            else:
                self.console.run([cc.frames(int(amount))])
            return
        deadline = time.monotonic() + line.timeout
        while True:
            actual, expected = self.reading(line, session_mark, command_mark)
            ok = ts.judge(actual, line.condition.comparator, line.condition.tolerance, expected)
            if ok or time.monotonic() >= deadline or isinstance(self.console, RecordedConsole):
                break
            time.sleep(0.5)
        entry["reading"] = text_of(actual, READING_LIMIT) if actual is not None else None
        row.update(actual=text_of(actual if actual is not None else "（读不到）"), expected=line.condition.comparator + " " + expected, ok=ok)
        if not ok:
            row["detail"] = "等了 " + text_of(line.timeout) + " 秒仍是 " + text_of(actual, 200)

    def finish(self, started):
        rows = self.rows
        summary = {"commands": sum(row["kind"] == "command" for row in rows), "expects": sum(row["kind"] == "expect" for row in rows),
                   "passed": sum(row["ok"] for row in rows), "failed": sum(not row["ok"] for row in rows)}
        state = self.state
        recorder = state.get("recorderSession") or ""
        document = {"format": FORMAT, "version": VERSION,
                    "script": {"path": self.script.path, "sha256": self.script.sha256, "lines": len(self.script.lines)},
                    "session": {"processId": state.get("processId"), "workDir": state.get("workDir", ""),
                                "recorder": {"session": recorder, "path": str(Path(state["bepinexRoot"]) / "ForgeReports" / ("rec-" + recorder)) if recorder else ""}},
                    "packages": package_rows(state) if state else [], "startedAt": started, "endedAt": utc(), "rows": rows, "summary": summary}
        write_json(self.folder / "run.json", document)
        (self.folder / "run.md").write_text(markdown(document), "utf-8")
        return document


def markdown(document):
    summary = document["summary"]
    out = ["# 脚本运行结果", "", "脚本 " + (document["script"]["path"] or "（回放）") + "，共 " + str(summary["commands"]) + " 条指令、" + str(summary["expects"])
           + " 条断言，通过 " + str(summary["passed"]) + "，失败 " + str(summary["failed"]) + "。", "",
           "| 行 | 内容 | 结果 | 实际 | 要求 |", "| --- | --- | --- | --- | --- |"]
    for row in document["rows"]:
        cell = lambda value: text_of(value, 120).replace("|", "\\|").replace("\n", " ")
        out.append("| {} | {} | {} | {} | {} |".format(row["line"], cell(row["text"]), "通过" if row["ok"] else "失败", cell(row["actual"] or row["detail"]), cell(row["expected"])))
    return "\n".join(out) + "\n"
