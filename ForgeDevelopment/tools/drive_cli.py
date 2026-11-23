"""Command-line takeover of a running Authoring session: the debugging console and the runner of test scripts.

It attaches to the session file written by run-drive-session.py start, through the same Client (status, call, command)
and the same MenuDrive readbacks as the scripted flows. Nothing here is a second transport. One JSON line per result on
stdout; trace rows of a long wait go to stderr while it runs. Failures exit 1 with the reason on stderr.

    drive_cli.py --session S fire 6 at=s1            one console command (every command of test_script.VERBS; the subcommands below keep their own syntax)
    drive_cli.py --session S run Mods/ForgeDevelopment/scripts/combat-basics.test    a whole script; exit 0 all passed, 1 a failure, 2 not run
    drive_cli.py --session S replay <session folder>             the recorded lines again on the live game
    drive_cli.py replay <session folder> --recorded [--script changed.test]    the recorded answers judged again, no game

Every run writes its folder under <work-dir>/console/ (commands.jsonl, one folder per command, run.json, run.md); see console_run.py."""
from __future__ import annotations

import argparse
import collections
import datetime as dt
import json
from pathlib import Path
import re
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
import drive_session as ds
import rec_segments
import console_commands as cc
import console_run
import test_script as ts

WAIT_DEFAULT = 30  # Seconds one call may wait for the game to accept a command; the caller chooses, there is no hidden budget.
PLAYER_PATHS = ["Alive", "Position", "DimensionIndex"]


def emit(value):
    print(json.dumps(value, ensure_ascii=False, allow_nan=False), flush=True)


def bounded(client, seconds):
    client.deadline = time.monotonic() + seconds
    return client


def snapshot(client):
    """Game state without waiting: a stale heartbeat or running command is reported, never waited out."""
    try:
        status = client.status(observing=True, deadline=time.monotonic() + 5)
    except (OSError, ValueError, TimeoutError) as error:
        return {"reachable": False, "reason": str(error)}
    age = ds.transport.status_age(status)
    value = {"reachable": True, "gameState": status["gameState"], "ageSeconds": round(age, 1), "fresh": age <= 3,
             "activeCommand": status["activeCommand"] or None, "worldEpoch": status["worldEpoch"],
             "recorderSession": status["recorderSession"] or None, "processId": status["processId"]}
    if not value["fresh"]:
        value["note"] = "心跳已过期：游戏主线程可能在加载或卡住；稍后再查，不要提交命令"
    elif value["activeCommand"]:
        value["note"] = "正在执行命令 " + value["activeCommand"] + "；执行完才能提交新命令"
    return value


def idle(value):
    return value["reachable"] and value["fresh"] and not value["activeCommand"]


def cmd_status(client, state, args):
    value = snapshot(client)
    if idle(value) and value["gameState"] != "Offline":
        bounded(client, args.wait)
        value["lobby"] = ds.native_lobby_state(client)
        if value["gameState"] == "InLevel":
            value["player"] = client.read("localPlayer", PLAYER_PATHS)
    return value


def cmd_lobby(client, state, args):
    return {"lobby": bounded(client, args.wait).call(ds.MENU, "LobbyState")}


def cmd_drop(client, state, args):
    client.echo = True
    ds.enter_level(client, args.rundown, args.tier, args.ordinal, args.timeout, args.bots)
    return {"landed": True, "world": client.status()["worldEpoch"]}


def literal(token):
    try:
        return json.loads(token)
    except ValueError:
        return token


def cmd_call(client, state, args):
    """A static method by name; --until then polls another method of the same type (no arguments) until its answer starts with the text."""
    value = {"result": bounded(client, args.wait).call(args.target, args.method, [literal(a) for a in args.args])}
    if str(value["result"]).strip().lower() == "false" and not getattr(args, "allow_false", False):
        raise ValueError("call.returned-false: " + args.target + " " + args.method + " answered False, so it did not do what it names "
                         "(a query that may answer no takes --allow-false)")
    if args.until:
        method, expected = args.until
        reading = []
        def answered():
            reading[:] = [client.call(args.target, method)]
            if str(reading[0]) == "":
                # Nothing to wait for: an empty answer is the game saying it publishes no such reading (the Engine's generation progress
                # is empty when Map published none), and no later poll will change that.
                raise ValueError("call.empty-answer: " + method + " answered an empty text, so there is no state to wait for " + repr(expected))
            return str(reading[0]).startswith(expected)
        client.deadline = time.monotonic() + args.timeout + args.wait
        ds.wait_until(client, answered, args.timeout, method + " 等到 " + expected, lambda: reading[0] if reading else "无读回")
        value["until"] = reading[0]
    return value


def cmd_read(client, state, args):
    return {"result": bounded(client, args.wait).read(args.target, args.paths)}


def cmd_step(client, state, args):
    """Run drive steps (the same JSON as a command file's steps). A bare list uses --target."""
    document = json.loads(Path(args.json[1:]).read_text("utf-8") if args.json.startswith("@") else args.json)
    target, steps = (document["target"], document["steps"]) if isinstance(document, dict) else (args.target, document)
    result, rows = bounded(client, args.wait).command(target, steps, host_role=args.host)
    return {"status": result["status"], "steps": [{key: row["body"].get(key) for key in ("step", "ok", "detail")}
                                                 for row in rows if row.get("kind") == "step"]}


def tail(path, count):
    with path.open("rb") as stream:
        stream.seek(0, 2)
        stream.seek(max(0, stream.tell() - 512 * 1024))
        return stream.read().decode("utf-8", "replace").splitlines()[-count:]


def cmd_log(client, state, args):
    bep = Path(state["bepinexRoot"])
    value = {"bepinex": tail(bep / "LogOutput.log", args.tail)}
    reports = [path for session in (bep / "ForgeReports").glob("rec-*") if session.is_dir() for path in rec_segments.segments(session)]
    if reports:
        newest = max(reports, key=lambda path: rec_segments.physical(path).stat().st_mtime)
        rows = collections.deque(maxlen=args.tail)
        for _, line, shared in rec_segments.lines(newest):
            try:
                rows.append(json.dumps(rec_segments.complete(json.loads(line), shared or {}), ensure_ascii=False))
            except (ValueError, AttributeError):
                rows.append(line.decode("utf-8", "replace").rstrip("\n"))  # The live segment's last line may be partial.
        value["forgeReport"] = {"file": str(rec_segments.physical(newest)), "lines": list(rows)}
    return value


def cmd_screenshot(client, state, args):
    """The game's own screenshot (ScreenCapture, asked by name), so a window behind others or the console never gets in the way."""
    value = snapshot(client)
    if not value["reachable"]:
        raise ValueError("游戏不可达：" + value["reason"])
    destination = client.work / ("screenshot-" + dt.datetime.now().strftime("%H%M%S%f") + ".png")
    bounded(client, args.wait).call("static:ForgeDevelopment.Native.Screenshot", "Capture", [str(destination)])
    last = -1
    for _ in range(100):
        time.sleep(0.2)
        size = destination.stat().st_size if destination.exists() else 0
        if size and size == last:
            return {"screenshot": str(destination)}
        last = size
    raise ValueError("截图失败：游戏没有写出 " + str(destination))

def cmd_quit(client, state, args):
    ds.host.atomic_json(ds.output_root(state["workDir"]) / "stop.forge", {"session": state["id"]})
    return {"stopRequested": True}


def natives(client, state, wait):
    """The verbs of the console that are this file's own commands above, as functions of a parsed script line."""
    ns = argparse.Namespace

    def log(line):
        value = cmd_log(client, state, ns(tail=int(line.options.get("tail", 40))))
        grep = line.options.get("grep")
        return value if not grep else {key: [row for row in rows if grep in row] if isinstance(rows, list) else rows for key, rows in value.items()}

    return {"status": lambda line: cmd_status(client, state, ns(wait=wait)), "log": log,
            "call": lambda line: cmd_call(client, state, ns(wait=wait, target=line.args[0], method=line.args[1], args=line.args[2:], until=None, timeout=300)),
            "step": lambda line: cmd_step(client, state, ns(wait=wait, json=line.args[0], target=line.options.get("target", ds.DRIVE), host=line.options.get("host", "any"))),
            "screenshot": lambda line: cmd_screenshot(client, state, ns(wait=wait)), "quit": lambda line: cmd_quit(client, state, ns())}


def console_folder(state, name=""):
    stamp = dt.datetime.now().strftime("%Y%m%dT%H%M%S%f")
    return ds.output_root(Path(state["workDir"]) / "console" / (stamp + ("-" + name if name else "")))


def run_lines(args, script, folder_name="", isolate=True):
    """Runs a parsed script on the live session; the run's document, and its folder, come back. `isolate` gives the session its baseline
    before and after the lines (console_run.Run.reset); one typed command keeps what it did."""
    state = ds.document(args.session)
    if state.get("format") != "forge.drive-session" or state.get("phase") != "ready":
        raise ValueError("会话尚未落地、已结束或版本不受支持（phase=" + str(state.get("phase")) + "）")
    folder = console_folder(state, folder_name)
    folder.mkdir(parents=True)
    client, state = ds.from_session(args.session, folder / "client")
    try:
        console = cc.Console(client, state, natives(client, state, args.wait), session_path=args.session)
        return console_run.Run(script, console, folder, state, isolate=isolate).run(), folder
    finally:
        client.close()


def cmd_run(args):
    script = ts.parse_file(args.script)
    document, folder = run_lines(args, script, Path(args.script).stem)
    emit({"run": str(folder / "run.json"), "summary": document["summary"]})
    for row in document["rows"]:
        if not row["ok"]:
            print(console_run.explain(row), file=sys.stderr)
    return 1 if document["summary"]["failed"] else 0


def cmd_replay(args):
    source = Path(args.folder)
    entries = [json.loads(row) for row in (source / "commands.jsonl").read_text("utf-8").splitlines() if row.strip()]
    script = ts.parse_file(args.script) if args.script else ts.parse("\n".join(entry["text"] for entry in entries), str(source))
    if args.recorded:
        folder = source.parent / (source.name + "-rejudged-" + dt.datetime.now().strftime("%H%M%S"))
        document = console_run.Run(script, console_run.RecordedConsole(source), folder).run()
    else:
        document, folder = run_lines(args, script, "replay")
    emit({"run": str(folder / "run.json"), "summary": document["summary"]})
    return 1 if document["summary"]["failed"] else 0


def cmd_console(args, line):
    """One console command, run like a one-line script so it leaves its result and log in a folder."""
    document, folder = run_lines(args, ts.Script([line], "", ""), line.verb, isolate=False)
    row = document["rows"][0]
    emit({"ok": row["ok"], "actual": row["actual"], "detail": row["detail"], "folder": str(folder)})
    return 0 if row["ok"] else 1


def split_console(argv):
    """The console line of an argv whose first word is a verb of the console that has no argparse subcommand of its own, else None."""
    rest, skip = [], False
    for index, word in enumerate(argv):
        if skip:
            skip = False
        elif word in ("--session", "--wait"):
            skip = True
        elif not word.startswith("--"):
            rest = argv[index:]
            break
    return rest if rest and rest[0] in ts.VERBS and rest[0] not in HANDLERS else None


def parser():
    top = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    top.add_argument("--session", type=Path, help="run-drive-session.py start 写出的 session.forge")
    top.add_argument("--wait", type=float, default=WAIT_DEFAULT, help="单次调用等游戏接受命令的秒数")
    modes = top.add_subparsers(dest="mode", required=True)
    modes.add_parser("status", help="游戏状态、心跳、大厅与玩家读回；立即返回")
    modes.add_parser("lobby", help="菜单、大厅与槽位的原生读回")
    drop = modes.add_parser("drop", help="从菜单或大厅进到指定关卡并落地；各阶段进度实时输出到 stderr")
    drop.add_argument("rundown", type=int)
    drop.add_argument("tier", type=int, help="原生 eRundownTier，1 为 A")
    drop.add_argument("ordinal", type=int)
    drop.add_argument("--bots", default="[]", help="机器人槽位 JSON 数组")
    drop.add_argument("--timeout", type=float, default=600, help="整段进关的总秒数")
    call = modes.add_parser("call", help="调用静态方法：call static:Type Method [JSON 参数...]")
    call.add_argument("target")
    call.add_argument("method")
    call.add_argument("args", nargs="*")
    call.add_argument("--allow-false", action="store_true", help="这个调用本来就是问“是不是”：回答 False 不算失败（默认 False 就是失败）")
    call.add_argument("--until", nargs=2, metavar=("STATE_METHOD", "PREFIX"), help="再轮询同一类型的一个无参方法，直到回答以 PREFIX 开头；回答是空文本时立刻报错而不是等到超时")
    call.add_argument("--timeout", type=float, default=300, help="--until 等待的秒数")
    read = modes.add_parser("read", help="原生读回：read localPlayer Alive Position")
    read.add_argument("target")
    read.add_argument("paths", nargs="+")
    step = modes.add_parser("step", help="执行 drive 步骤 JSON（数组或 {target, steps}；@文件 读取）")
    step.add_argument("json")
    step.add_argument("--target", default=ds.DRIVE)
    step.add_argument("--host", default="any")
    log = modes.add_parser("log", help="BepInEx 与 ForgeReports 最新行")
    log.add_argument("--tail", type=int, default=40)
    modes.add_parser("screenshot", help="截取游戏窗口为 PNG")
    modes.add_parser("quit", help="请求常驻会话关闭游戏")
    run = modes.add_parser("run", help="跑一份 .test 脚本；退出码 0 全过、1 有失败、2 没跑起来")
    run.add_argument("script", type=Path)
    replay = modes.add_parser("replay", help="重放一份会话文件夹：默认在游戏里原样再跑；--recorded 不进游戏，按录下的回答重新判断")
    replay.add_argument("folder", type=Path)
    replay.add_argument("--recorded", action="store_true")
    replay.add_argument("--script", type=Path, help="用这份脚本代替录下的文本（改过的 expect 会被重新判断）")
    return top


HANDLERS = {"status": cmd_status, "lobby": cmd_lobby, "drop": cmd_drop, "call": cmd_call, "read": cmd_read,
            "step": cmd_step, "log": cmd_log, "screenshot": cmd_screenshot, "quit": cmd_quit}


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    try:
        line = split_console(argv)
        if line is not None:
            args = parser().parse_args(argv[:len(argv) - len(line)] + ["status"])  # the words before the verb are only the global options
            if args.session is None:
                raise ValueError("需要 --session")
            return cmd_console(args, ts.parse_line(1, " ".join(word if not re.search(r"\s", word) else '"' + word.replace('"', '\\"') + '"' for word in line), set()))
        args = parser().parse_args(argv)
        if args.mode == "replay" and args.recorded:
            return cmd_replay(args)
        if args.session is None:
            raise ValueError("需要 --session")
        if args.mode == "run":
            return cmd_run(args)
        if args.mode == "replay":
            return cmd_replay(args)
        state = ds.document(args.session)
        if state.get("format") != "forge.drive-session" or state.get("phase") != "ready":
            raise ValueError("会话尚未落地、已结束或版本不受支持（phase=" + str(state.get("phase")) + "）")
        work = ds.output_root(Path(state["workDir"]) / "cli" / dt.datetime.now().strftime("%Y%m%dT%H%M%S%f"))
        work.mkdir(parents=True)
        client, state = ds.from_session(args.session, work)
        try:
            emit(HANDLERS[args.mode](client, state, args))
        finally:
            client.close()
        return 0
    except (OSError, ValueError, RuntimeError, TimeoutError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print("接管失败：" + str(error), file=sys.stderr)
        return 2 if isinstance(error, (ts.ScriptError, OSError)) or "会话" in str(error) else 1


if __name__ == "__main__":
    sys.exit(main())
