#!/usr/bin/env python3
"""启动一关并等待外部实验；stop、Ctrl+C 或任一步失败后关闭本驱动的游戏并还原测试档。

关卡二选一：--package <已安装的 Forge 地图包目录> --level A1，或 --rundown <Rundown 数据块 ID> --level A1（原版与 MTFO
Rundown，不需要 Forge Engine 或任何 Forge 地图包）。只要求档里装了 ForgeCore 与 ForgeDevelopment。"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid

import installed_pack
from drive_session import (ROOT, Client, ExperimentFailed, FileLease, digest, document, encoded, engine_installed, enter_level,
                           from_session, game_profile, host, inventory, output_root, plain, transport)

DEV_CFG = "NAinfini.ForgeDevelopment.cfg"
FORGE_LOAD_FAILURE = re.compile(r"\[(?:Error|Fatal)\s*:\s*BepInEx\] (?:Could not load|Error loading) \[Forge|\[Warning:\s*BepInEx\] Skipping \[Forge"
                                r"|\[Error\s*:\s*Forge Development\] development\.startup-failed")


def forge_load_failure(log, launched):
    """BepInEx lines saying a Forge plugin was refused or skipped (missing or older dependency such as GTFO-API, a load
    exception), or ForgeDevelopment's own startup failure (an SDK fingerprint that does not match the installed
    ForgeEngine), in this launch's log, else None. Such a plugin never opens the experiment entry, so waiting on it only
    burns the whole startup timeout in silence."""
    try:
        if not log.is_file() or log.stat().st_mtime < launched:
            return None
        lines = [line.strip() for line in log.read_text(encoding="utf-8", errors="replace").splitlines()
                 if FORGE_LOAD_FAILURE.search(line)]
    except OSError:
        return None  # An unreadable game log yields no load-failure text; the read error is not reported.
    return " | ".join(lines) or None


def enable_observations(client, owned, deadline):
    for command in ("door-size-enable-observation", "v1t-enable-observations"):
        reason = "observation was not enabled before the startup deadline"
        while True:
            if not owned.alive():
                raise RuntimeError("游戏在观察配置就绪前退出")
            if time.monotonic() >= deadline:
                raise TimeoutError("trace-install-timeout: " + command + ": " + reason)
            try:
                client.execute(command, deadline=deadline)
                break
            except ExperimentFailed as error:
                # Only this explicit pre-installation refusal is safe to resubmit. Failed/unknown
                # execution, missing evidence and cleanup failures must retain their original outcome.
                if error.result["status"] != "Failed" or "Trace installation is still pending" not in error.result["reason"]:
                    raise
                client.evidence(error.result, client.work / "requests" / error.result["id"])
                reason = str(error)
                time.sleep(min(3, max(0, deadline - time.monotonic())))


def bot_slots_json(plugin, rundown, tier, ordinal):
    """The level's declared bot rows from the package's forge/bot-slots.forge as a JSON array, the roster the game-side
    lobby gate (LobbyGate.LobbyRosterPending) waits for. No file or no entry for the level is a solo lobby; a malformed or repeated entry is refused."""
    path = plugin / "forge/bot-slots.forge"
    if not path.is_file():
        return "[]"
    value = document(path)
    levels = value.get("levels") if isinstance(value, dict) else None
    if not isinstance(levels, list):
        raise ValueError("bot-slots.forge 缺少 levels 数组")
    key = f"{rundown}:{'ABCDE'[tier - 1]}:{ordinal}"
    rows = [row for row in levels if isinstance(row, dict) and row.get("level") == key]
    if len(rows) > 1:
        raise ValueError("bot-slots.forge 中关卡名册重复：" + key)
    slots = rows[0].get("slots", []) if rows else []
    if not isinstance(slots, list) or not all(isinstance(slot, dict) for slot in slots):
        raise ValueError("bot-slots.forge 中的槽位格式无效：" + key)
    return json.dumps(slots, ensure_ascii=False, separators=(",", ":"))

def selected_level(plugin, level):
    files = installed_pack.installed_files(plugin)
    identity = installed_pack.installed_package_id(plugin)
    declarations = installed_pack.installed_level_declarations(plugin, files, identity)
    if not re.fullmatch(r"[A-E][1-9][0-9]*", level):
        raise ValueError("--level 需要 A1 形式的关卡标号")
    matches = [row for row in declarations if row["itemId"].endswith("-" + level)]
    if len(matches) != 1:
        raise ValueError("包内没有唯一且启用的指定关卡")
    number = matches[0]["itemId"].rsplit("/", 1)[1].rsplit("-", 1)[0]
    return {"packageId": identity, "contentSha256": installed_pack.content_sha256(files), "files": dict(files),
            "pluginDirectory": plugin.name,
            "packageFiles": game_profile.installed_package_files(plugin),
            "rundown": int(number), "tier": "ABCDE".index(level[0]) + 1, "ordinal": int(level[1:]) - 1, "level": level,
            "botSlots": bot_slots_json(plugin, int(number), "ABCDE".index(level[0]) + 1, int(level[1:]) - 1)}


def vanilla_level(rundown, level):
    """A level of any Rundown the game loads (its own or an MTFO one) by its data block ID and A1-style label; no package."""
    if not re.fullmatch(r"[A-E][1-9][0-9]*", level):
        raise ValueError("--level 需要 A1 形式的关卡标号")
    if rundown <= 0:
        raise ValueError("--rundown 需要正整数的 Rundown 数据块 ID")
    return {"rundown": rundown, "tier": "ABCDE".index(level[0]) + 1, "ordinal": int(level[1:]) - 1, "level": level, "botSlots": "[]"}


def probe_files(bep):
    # The repository's probes, or outside it those the installed ForgeDevelopment package ships.
    probes = (ROOT / "Mods/ForgeDevelopment/probes" if host.IN_REPOSITORY
              else bep / "plugins/NAinfini-ForgeDevelopment/ForgeDevelopment/probes")
    sources = {}
    for name in ("authoring-game-drive", "door-size-clearance", "input-drive", "state-readbacks", "v1-alltest"):
        relative = "ForgeDevelopment/commands/" + name + ".forge"
        sources[relative] = (probes / "commands" / (name + ".forge")).read_bytes()
    for name in ("author-door-size-clearance", "author-y15-layer-fields"):
        relative = "ForgeDevelopment/trace/" + name + ".forge"
        sources[relative] = (probes / "trace" / (name + ".forge")).read_bytes()
    # Validate the resulting identities, including user files; never hide duplicate commands/profiles.
    seen = set()
    for kind in ("commands", "trace"):
        merged = {"ForgeDevelopment/" + kind + "/" + p.name: p.read_bytes()
                  for p in (bep / "ForgeDevelopment" / kind).glob("*.forge")}
        merged.update({k: v for k, v in sources.items() if k.startswith("ForgeDevelopment/" + kind + "/")})
        for relative, raw in merged.items():
            value = json.loads(raw, object_pairs_hook=transport.unique_object)
            rows = value if isinstance(value, list) else [value]
            for row in rows:
                identity = row.get("id") if kind == "commands" else row.get("profile", Path(relative).stem)
                if not isinstance(identity, str) or not identity or (kind, identity) in seen:
                    raise ValueError("命令或观察配置身份缺失/重复：" + relative)
                seen.add((kind, identity))
    return sources


def session_start(args):
    if os.name != "nt":
        raise ValueError("会话启动只支持 Windows Steam/r2modman")
    if args.lock:
        os.environ["FORGE_GAME_LOCK"] = str(Path(args.lock).absolute())
    if (args.package is None) == (args.rundown is None):
        raise ValueError("选关需要 --package 或 --rundown 二者之一")
    profile = game_profile.require_profile(args.profile)
    bep, game = plain(profile / "BepInEx"), plain(host.game_root(args.game))
    plugin = None
    if args.package is not None:
        if not re.fullmatch(r"[A-Za-z0-9_]+-[A-Za-z0-9_]+", args.package):
            raise ValueError("--package 需要精确的已安装插件目录名")
        plugin = plain(bep / "plugins" / args.package)
        list(game_profile.plain_tree(plugin))
        selected = selected_level(plugin, args.level.upper())
    else:
        selected = vanilla_level(args.rundown, args.level.upper())
    engine = engine_installed(bep)
    if not engine:
        print("档里没有 Forge Engine：不设 Authoring 模式，Forge 地图读取不可用。", flush=True)
    sources = probe_files(bep)
    steam = host.steam_path(args.steam)
    for path in (steam, game / "GTFO.exe", game / "winhttp.dll", profile / "winhttp.dll",
                 profile / "dotnet/coreclr.dll", bep / "core/BepInEx.Unity.IL2CPP.dll",
                 # The mod manager installs a package as plugins/<team>-<package>/<Name>/ (Mods/Release/README.md).
                 bep / "plugins/NAinfini-ForgeCore/ForgeCore/ForgeCore.dll",
                 bep / "plugins/NAinfini-ForgeDevelopment/ForgeDevelopment/ForgeDevelopment.Native.dll"):
        if not plain(path).is_file():
            raise FileNotFoundError(path)
    if not (profile / ".doorstop_version").read_text(encoding="utf-8").strip().startswith("4."):
        raise ValueError("启动命令需要 Doorstop 4")
    host.wait_for_game_slot()
    work = output_root(args.work_dir)
    work.mkdir(parents=True, exist_ok=False)
    state = {"format": "forge.drive-session", "schemaVersion": "1.0.0", "id": str(uuid.uuid4()),
             "phase": "starting", "bepinexRoot": str(bep), "workDir": str(work), "selection": selected,
             "evidenceScope": "session-control-only", "status": "Running"}
    host.atomic_json(work / "session.forge", state)
    lease = FileLease(bep, work / "profile-lease")
    client, owned = None, None
    launched = False
    failure = None
    stop_requested = False
    cleanup_errors = []
    old_recordings = {p.name for p in (bep / "ForgeReports").glob("rec-*")}
    started = transport.monotonic_seconds()
    with host.profile_lock(bep):
        try:
            host.wait_for_game_slot()
            # This lane leases diagnostics only. Package installation remains the existing QA preparation step.
            if engine:
                lease.config("NAinfini.ForgeEngine.cfg", [("Runtime", "Mode", "Authoring")])
            lease.config(DEV_CFG, [("Recorder", "Enabled", "true"),
                ("Recorder", "TraceEnabled", "true"), ("Recorder", "TraceAllProfiles", "false"),
                ("Trace Profiles", "everything", "false"),
                *[("Trace Profiles", name, "true") for name in ("author-door-size-clearance", "door-terminal", "player", "enemy",
                                                               "author-y15-layer-fields")],
                ("Performance Diagnostics", "EnablePerformanceLogging", "true" if args.perf else "false"),
                ("DropMeasurement", "CaptureWorkEnabled", "true")])
            for relative, raw in sources.items():
                lease.write(relative, raw)
            host.atomic_json(work / "inputs.forge", {"selection": selected,
                "diagnostics": {k: digest(v) for k, v in sources.items()},
                "dlls": {str(p.relative_to(bep)): host.sha256(p) for p in sorted((bep / "plugins").glob("*/*/*.dll"))}})
            # Copy the old rotating logs, preserving their QA originals.
            for name in ("LogOutput.log", "ErrorLog.log"):
                source = bep / name
                if source.exists():
                    target = work / "previous-logs" / name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, target)
            if plugin and game_profile.installed_drift(plugin, selected["packageFiles"])["changed"]:
                raise ValueError("启动前安装包字节已变更")
            launched_wall = time.time()
            subprocess.Popen(host.launch_command(steam, profile))
            launched = True
            deadline = time.monotonic() + min(120, args.startup_timeout)
            while time.monotonic() < deadline:
                pids = host.game_pids()
                if pids:
                    if len(pids) != 1:
                        raise RuntimeError("启动期间出现多个 GTFO；拒绝接管")
                    owned = host.OwnedGame(pids[0], game, profile)
                    break
                time.sleep(.5)
            if owned is None and launched:
                raise TimeoutError("等待本次 GTFO 进程超时")
            client = Client(bep, work / "launcher-commands", timeout=args.command_timeout)
            deadline = time.monotonic() + args.startup_timeout
            next_log_check = 0
            while time.monotonic() < deadline:
                if not owned.alive():
                    raise RuntimeError("游戏在实验入口就绪前退出")
                if time.monotonic() >= next_log_check:
                    next_log_check = time.monotonic() + 5
                    refused = forge_load_failure(bep / "LogOutput.log", launched_wall)
                    if refused:
                        raise RuntimeError("Forge 插件未被 BepInEx 加载，实验入口不会出现：" + refused)
                control = bep / "ForgeDevelopment/experiments/control/status.forge"
                if control.exists():
                    # Do not admit an old status file just because the path exists.
                    candidate = document(control)
                    if candidate.get("processId") == owned.pid:
                        transport.status_age(candidate)  # Reject a missing, non-finite or future counter.
                        if candidate["monotonicSeconds"] >= started:
                            native = client.status(deadline=deadline)
                            state.update({key: native[key] for key in ("processId", "instance")})
                            client.identity = state
                            host.atomic_json(work / "session.forge", state)
                            break
                time.sleep(.5)
            else:
                raise TimeoutError("本进程的原生实验入口未就绪")
            enable_observations(client, owned, deadline)
            enter_level(client, selected["rundown"], selected["tier"], selected["ordinal"], args.startup_timeout, selected["botSlots"])
            native = client.status()
            state.update({"phase": "ready", "worldEpoch": native["worldEpoch"], "recorderSession": native["recorderSession"],
                          "commandTool": str(Path(__file__).with_name("run-experiment.py")),
                          "readyUtc": native["utc"]})
            state["player"] = client.read("localPlayer", ["GlobalID", "Position", "DimensionIndex", "Alive"], native["worldEpoch"])
            host.atomic_json(work / "session.forge", state)
            print("已落地并保持运行：" + str(work / "session.forge"), flush=True)
            deadline = time.monotonic() + args.session_timeout
            while True:
                if not owned.alive():
                    raise RuntimeError("常驻会话中的游戏意外退出")
                stop = work / "stop.forge"
                if stop.exists():
                    if document(stop) != {"session": state["id"]}:
                        raise ValueError("停止请求会话身份不匹配")
                    stop_requested = True
                    client.status()  # Busy or stale transport is a failed shutdown, not a successful stop.
                    if client.call("static:ForgeDevelopment.Native.ExperimentObjects", "Count") != "0":
                        raise ValueError("结束时仍有实验对象")
                    # Counted over the whole process: a budget renewal opens a new session with zero counts.
                    state["recorder"] = {name: int(client.call("static:ForgeDevelopment.Native.RecSession", name))
                                         for name in ("DroppedInProcess", "QueueRefusalsInProcess")}
                    drift = (game_profile.installed_drift(plugin, selected["packageFiles"]) if plugin
                             else {"unmanaged": [], "changed": []})
                    state["unmanagedPackageFiles"] = drift["unmanaged"]  # Written by other mods: listed, never a failure.
                    host.atomic_json(work / "session.forge", state)
                    if state["recorder"]["DroppedInProcess"]:
                        raise ValueError("原始记录发生丢失：" + str(state["recorder"]["DroppedInProcess"]) + " 条")
                    if drift["changed"]:
                        raise ValueError("结束时安装包未恢复原字节：" + ", ".join(drift["changed"][:20]))
                    state["status"] = "Stopped"
                    break
                if time.monotonic() >= deadline:
                    raise TimeoutError("常驻会话达到总超时")
                time.sleep(.5)
        except KeyboardInterrupt:
            stop_requested = True  # Ctrl+C is an explicit player stop, never an item failure.
            state["status"] = "Stopped"
        except Exception as error:
            failure = str(error) or type(error).__name__
            state.update({"phase": "failed", "status": "Fail", "failure": failure, "cleanup": "Deferred"})
            host.atomic_json(work / "session.forge", state)
            print("会话操作失败：" + failure, file=sys.stderr, flush=True)
            # A failure closes the game this session launched and restores the profile like a stop; holding it would
            # keep the game slot and profile lock until someone notices.
            stop_requested = True
        finally:
            # A same-process level switch may have published a newer selection and world.
            try:
                current = document(work / "session.forge")
                if any(current.get(key) != state.get(key) for key in ("id", "processId", "instance")):
                    raise ValueError("收尾时会话身份已改变")
                state.update({key: current[key] for key in ("selection", "worldEpoch", "recorderSession", "player") if key in current})
            except (OSError, ValueError, TimeoutError) as error:
                cleanup_errors.append("收尾身份：" + str(error))
            state["phase"] = "closing"
            try:
                host.atomic_json(work / "session.forge", state)
            except OSError as error:
                cleanup_errors.append("收尾状态：" + str(error))
            # A validated stop request, Ctrl+C or a failure closes the owned game.
            if owned is not None and stop_requested:
                try:
                    state["gameExit"] = owned.close()
                except (OSError, RuntimeError) as error:
                    cleanup_errors.append(str(error))
            # Never restore files while a game may still be using them: the game this session closed is waited out of the
            # process list (it lingers there while it tears down); a game still there after that keeps the lease, named for recover.
            try:
                host.wait_for_game_slot(timeout=host.EXIT_WAIT_SECONDS if owned is not None and stop_requested else 0)
                game_stopped = True
            except (OSError, ValueError, RuntimeError) as error:
                game_stopped = False
                cleanup_errors.append(str(error) + "；游戏退出后用 run-drive-session.py recover --bepinex-root " + str(bep)
                                      + " --lease-dir " + str(work / "profile-lease") + " 还原租约")
            if game_stopped:
                for restore in ([client.close] if client else []) + [lease.restore]:
                    try:
                        restore()
                    except (OSError, ValueError, RuntimeError) as error:
                        cleanup_errors.append(str(error))
            try:
                for source in (bep / "ForgeReports").glob("rec-*"):
                    if source.is_dir() and source.name not in old_recordings:
                        list(game_profile.plain_tree(source))
                        shutil.copytree(source, work / "recordings" / source.name)
                for name in ("LogOutput.log", "ErrorLog.log"):
                    if (bep / name).exists():
                        shutil.copy2(bep / name, work / name)
                for source in (bep / "forge-logs").glob("*.jsonl"):
                    target = work / "forge-logs" / source.name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, target)
            except (OSError, RuntimeError) as error:
                cleanup_errors.append("取证归档：" + str(error))
            state.update({"phase": "closed" if game_stopped else "failed", "status": "Fail" if failure or cleanup_errors else state["status"],
                          "failure": failure, "cleanupErrors": cleanup_errors,
                          "cleanup": "Fail" if cleanup_errors else "Passed", "finishedUtc": time.time()})
            host.atomic_json(work / "session.forge", state)
    return 1 if state["status"] == "Fail" else 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest="mode", required=True)
    start = modes.add_parser("start")
    start.add_argument("--package", help="已安装的 Forge 地图包插件目录名（team-Name）")
    start.add_argument("--rundown", type=int, help="不用地图包时：Rundown 数据块 ID（原版或 MTFO）")
    start.add_argument("--level", default="A3")
    start.add_argument("--profile", type=Path, default=game_profile.DEFAULT_PROFILE, help="r2modman 档目录")
    start.add_argument("--game", type=Path, help="GTFO 安装目录；不填从 Steam 注册表和库清单找")
    start.add_argument("--steam")
    start.add_argument("--lock", type=Path, help="游戏锁文件；不填用 FORGE_GAME_LOCK，仓库内默认主检出的 artifacts/work/game-lock.txt")
    start.add_argument("--work-dir", type=Path, required=True)
    start.add_argument("--startup-timeout", type=float, default=600)
    start.add_argument("--session-timeout", type=float, default=7200)
    start.add_argument("--command-timeout", type=float, default=180)
    start.add_argument("--perf", action="store_true", help="开 ForgeDevelopment 性能日志（BepInEx/PerformanceLogs，帧时间分位与主线程分配；用 analyze_performance.py 读）")
    for name in ("inspect", "command", "stop"):
        mode = modes.add_parser(name)
        mode.add_argument("--session", required=True, type=Path)
        if name != "stop":
            mode.add_argument("--work-dir", type=Path, required=True)
        if name == "command":
            mode.add_argument("--command", required=True)
    recover = modes.add_parser("recover", help="确认游戏已退出后重放指定持久租约；不启动游戏")
    recover.add_argument("--bepinex-root", required=True, type=Path)
    recover.add_argument("--lease-dir", required=True, type=Path)
    recover.add_argument("--lock", type=Path, help="游戏锁文件（同 start）")
    args = parser.parse_args(argv)
    try:
        if args.mode == "start":
            if not all(0 < getattr(args, key) <= 14400 for key in ("startup_timeout", "session_timeout", "command_timeout")):
                raise ValueError("超时需要 0..14400 秒之间的有限数值")
            return session_start(args)
        if args.mode == "recover":
            if args.lock:
                os.environ["FORGE_GAME_LOCK"] = str(Path(args.lock).absolute())
            bep = plain(args.bepinex_root)
            game_profile.require_profile(bep.parent)
            host.wait_for_game_slot()
            with host.profile_lock(bep):
                FileLease(bep, output_root(args.lease_dir)).restore()
            return 0
        state = document(args.session)
        if args.mode == "stop":
            if state.get("format") != "forge.drive-session" or state.get("phase") not in ("starting", "ready", "failed"):
                raise ValueError("没有可停止的常驻会话")
            host.atomic_json(output_root(state["workDir"]) / "stop.forge", {"session": state["id"]})
            return 0
        work = output_root(args.work_dir)
        work.mkdir(parents=True, exist_ok=False)
        client, state = from_session(args.session, work)
        try:
            if args.mode == "inspect":
                print(json.dumps(inventory(client, work / "inventory.forge"), ensure_ascii=False))
            else:
                client.execute(args.command)
        finally:
            client.close()
        return 0
    except (OSError, ValueError, RuntimeError, TimeoutError, KeyError, TypeError) as error:
        print("作者会话失败：" + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
