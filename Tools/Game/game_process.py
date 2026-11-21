"""Generic helpers for tools that drive the installed game: atomic file writes, BepInEx cfg edits, the profile lock,
the single game slot, an owned game process handle and the Steam launch command. No level-capture, pack-test or
development knowledge lives here; each driver supplies its own paths and entries."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time

APP_ID = "493520"
# Run from this repository (Tools/Game beside Tools/local-inputs.ts), or from a copied tooling folder anywhere else, where no
# git checkout, artifacts folder or repository rule applies.
IN_REPOSITORY = (Path(__file__).resolve().parents[1] / "local-inputs.ts").is_file()
FILE_WRITE_ATTEMPTS = 40
FILE_WRITE_RETRY_SECONDS = .1
EXIT_WAIT_SECONDS = 180  # how long a driver waits for the game it closed to end and leave the process list

def transient_file_lock(error):
    return isinstance(error, PermissionError) or getattr(error, "winerror", None) in (5, 32, 33)


def retry_file_lock(operation, attempts=FILE_WRITE_ATTEMPTS, delay=FILE_WRITE_RETRY_SECONDS):
    """Run operation(); a transient Windows sharing violation is retried up to attempts times, any other error
    and the last failure are raised. The one bounded retry every tool uses for reads, writes and replaces."""
    for attempt in range(attempts):
        try:
            return operation()
        except OSError as error:
            if attempt == attempts - 1 or not transient_file_lock(error):
                raise
            time.sleep(delay)


def profiles_root():
    """The r2modman profiles every game tool installs into; FORGE_GAME_PROFILES_ROOT points it elsewhere (tests)."""
    return Path(os.environ.get("FORGE_GAME_PROFILES_ROOT") or Path(os.environ.get("APPDATA", "")) / "r2modmanPlus-local/GTFO/profiles")


def main_checkout():
    """The main checkout of this repository (a secondary worktree finds it through its .git file)."""
    here = Path(__file__).resolve().parent
    common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=here,
                            capture_output=True, text=True, check=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout.strip()
    return Path(common).parent


def shared_work_dir():
    """artifacts/work of the main checkout, shared by every worktree."""
    return main_checkout() / "artifacts/work"


def checkout_input(root, relative):
    """A repository input to read: this checkout's own file (`root` is its top), else the main checkout's (read only), as
    Tools/local-inputs.ts resolves it."""
    own = Path(root) / relative
    return own if own.exists() else main_checkout() / relative


def game_lock_path():
    """FORGE_GAME_LOCK (a driver's --lock sets it), else artifacts/work/game-lock.txt of the main checkout, shared by every
    worktree. Outside the repository there is no shared default, so the lock must be named."""
    if os.environ.get("FORGE_GAME_LOCK"):
        return Path(os.environ["FORGE_GAME_LOCK"])
    if not IN_REPOSITORY:
        raise RuntimeError("仓库外运行需要指定游戏锁文件：--lock <path> 或环境变量 FORGE_GAME_LOCK")
    return shared_work_dir() / "game-lock.txt"


def holds_game_lock():
    token = os.environ.get("FORGE_GAME_LOCK_TOKEN")
    try:
        return bool(token) and ("token=" + token) in game_lock_path().read_text(encoding="utf-8")
    except OSError:
        return False


@contextlib.contextmanager
def game_lock(owner, wait=True, poll_seconds=15, pids_source=None):
    """The one machine-wide game lock: taken when the lock file is absent and no GTFO runs, held for the whole install, run and
    restore. Child processes inherit FORGE_GAME_LOCK_TOKEN, so a driver started under the lock (or a nested driver) reuses it."""
    if holds_game_lock():
        yield
        return
    import uuid
    path, token = game_lock_path(), uuid.uuid4().hex
    path.parent.mkdir(parents=True, exist_ok=True)
    while True:
        if not (pids_source or game_pids)():
            try:
                with path.open("x", encoding="utf-8") as stream:
                    stream.write(f"{owner} {time.strftime('%Y-%m-%dT%H:%M:%S')} pid={os.getpid()} token={token}")
                break
            except FileExistsError:
                # An empty file is a lock released by the older write-empty protocol: free, so it is removed and taken next round.
                with contextlib.suppress(OSError):
                    if path.stat().st_size == 0 and time.time() - path.stat().st_mtime > 2:
                        path.unlink()
                        continue
        if not wait:
            held = path.read_text(encoding="utf-8", errors="replace") if path.exists() else "GTFO 正在运行"
            raise RuntimeError("游戏锁被占用，未修改配置：" + held)
        print("等待游戏锁或现有 GTFO 退出；不接管、不关闭别人的游戏。", flush=True)
        time.sleep(poll_seconds)
    os.environ["FORGE_GAME_LOCK_TOKEN"] = token
    try:
        yield
    finally:
        os.environ.pop("FORGE_GAME_LOCK_TOKEN", None)
        if ("token=" + token) in path.read_text(encoding="utf-8", errors="replace"):
            path.unlink()


def require_game_lock(path):
    """Every write through these helpers into a game profile needs the game lock: a lane outside it cannot change shared QA config."""
    try:
        Path(path).absolute().relative_to(profiles_root().absolute())
    except ValueError:
        return
    if not holds_game_lock():
        raise PermissionError("游戏锁之外不能修改游戏配置档（先用 game_process.game_lock 取锁）：" + str(path))


def replace_file(source, destination):
    require_game_lock(destination)
    retry_file_lock(lambda: source.replace(destination))


def atomic_json(path, value):
    require_game_lock(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    def write():
        with temporary.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
    retry_file_lock(write)
    replace_file(temporary, path)


def cfg_entries(text):
    """Read BepInEx section/key/value entries without layout; retain duplicate entries."""
    text = text.removeprefix("\ufeff")
    section = None
    for line in text.splitlines():
        match = re.fullmatch(r"\s*\[([^]]+)\]\s*", line)
        if match:
            section = match[1]
        elif section is not None:
            match = re.match(r"\s*([^#;=]+?)\s*=\s*(.*?)\s*$", line)
            if match:
                yield section, match[1], match[2]


def cfg_value(text, section, key):
    return next((value for entry_section, entry_key, value in cfg_entries(text)
                 if (entry_section, entry_key) == (section, key)), None)


def cfg_edit(text, section, key, value):
    """Change only one BepInEx entry, preserving comments and all unrelated sections/entries."""
    if text.startswith("\ufeff"):
        return "\ufeff" + cfg_edit(text[1:], section, key, value)
    newline = "\r\n" if "\r\n" in text else "\n"
    lines = text.splitlines(keepends=True)
    start = next((i for i, line in enumerate(lines) if line.strip() == f"[{section}]"), None)
    if start is None:
        if value is None:
            return text
        return text + (newline if text and not text.endswith("\n") else "") + f"{newline}[{section}]{newline}{key} = {value}{newline}"
    end = next((i for i in range(start + 1, len(lines)) if re.fullmatch(r"\s*\[([^]]+)\]\s*", lines[i].strip())), len(lines))
    for i in range(start + 1, end):
        match = re.match(r"\s*([^#;=]+?)\s*=", lines[i])
        if match and match[1] == key:
            lines[i:i+1] = [] if value is None else [f"{key} = {value}{newline}"]
            return "".join(lines)
    if value is not None:
        if end and not lines[end - 1].endswith("\n"):
            lines[end - 1] += newline
        lines.insert(end, f"{key} = {value}{newline}")
    return "".join(lines)


@contextlib.contextmanager
def profile_lock(bep):
    # A driver that locks its profile holds the machine-wide game lock too (or reuses its caller's), and fails at once when another lane has it.
    with game_lock("profile " + Path(bep).parent.name, wait=False):
        with _profile_file_lock(bep):
            yield


@contextlib.contextmanager
def _profile_file_lock(bep):
    import msvcrt
    path = bep / "ForgeReports/driver.lock"
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as stream:
        if stream.tell() == 0:
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError("已有驱动正在使用这个 profile") from error
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def game_pids():
    result = subprocess.run(["powershell", "-NoProfile", "-Command",
                            "[System.Diagnostics.Process]::GetProcessesByName('GTFO') | ForEach-Object { $_.Id }"],
                            capture_output=True, text=True, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    return [int(line) for line in result.stdout.splitlines() if line.strip()]


def wait_for_game_slot(pids_source=None, timeout=0):
    """An existing game belongs to its player. Refuse before config, install or launch. With `timeout`, a driver closing its own
    game waits that many seconds for the process to leave the process list (a terminated GTFO lingers there while it tears down)."""
    deadline = time.monotonic() + timeout
    while pids := (pids_source or game_pids)():
        if time.monotonic() >= deadline:
            raise RuntimeError("GTFO 已在运行，未修改配置、接管或启动另一实例：" + str(pids))
        time.sleep(1)


class OwnedGame:
    """Keep a Windows process handle: termination cannot hit a recycled PID or an unrelated preexisting game."""
    def __init__(self, pid, game_root, profile=None):
        import ctypes
        from ctypes import wintypes
        self.pid = pid
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.api.OpenProcess.restype = wintypes.HANDLE
        self.api.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self.api.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.api.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]
        self.handle = self.api.OpenProcess(0x100000 | 0x1000 | 1, False, pid)
        if not self.handle:
            raise OSError(ctypes.get_last_error(), "无法打开 GTFO 进程")
        buffer = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(buffer))
        if not self.api.QueryFullProcessImageNameW(self.handle, 0, buffer, ctypes.byref(size)):
            self.api.CloseHandle(self.handle)
            raise OSError(ctypes.get_last_error(), "无法核对 GTFO 安装路径")
        if Path(buffer.value).resolve() != (game_root / "GTFO.exe").resolve():
            self.api.CloseHandle(self.handle)
            raise RuntimeError(f"检测到另一安装目录的 GTFO，未控制它：{buffer.value}")
        if profile is not None:
            try:
                result = subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"(Get-CimInstance Win32_Process -Filter 'ProcessId = {int(pid)}').CommandLine"],
                    capture_output=True, text=True, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
                expected = str(profile / "BepInEx/core/BepInEx.Unity.IL2CPP.dll").replace("/", "\\").casefold()
                if expected not in result.stdout.replace("/", "\\").casefold():
                    raise RuntimeError(f"PID {pid} 不属于本次 QA profile；未接管或关闭")
            except BaseException:
                self.api.CloseHandle(self.handle)
                raise

    def alive(self):
        return self.api.WaitForSingleObject(self.handle, 0) == 0x102

    def exit_code(self):
        import ctypes
        from ctypes import wintypes
        self.api.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        code = wintypes.DWORD()
        if not self.api.GetExitCodeProcess(self.handle, ctypes.byref(code)):
            raise OSError(ctypes.get_last_error(), "无法读取 GTFO 退出原因")
        return code.value

    def close(self):
        """Terminates the game unless it already exited, then waits on the handle until the process has really ended. A GTFO
        that is quitting or terminated can take minutes to tear down; a second kill does nothing more (taskkill then answers
        "no running instance"), so the wait is the only thing that settles it."""
        try:
            terminated = self.alive()
            if terminated and not self.api.TerminateProcess(self.handle, 1) and self.alive():
                raise OSError("无法终止本驱动启动的 GTFO")
            if self.api.WaitForSingleObject(self.handle, EXIT_WAIT_SECONDS * 1000) != 0:
                raise RuntimeError(f"GTFO 终止后 {EXIT_WAIT_SECONDS} 秒仍未退出（PID {self.pid}）")
            return {"exitCode": self.exit_code(), "termination": "driver" if terminated else "process-exit"}
        finally:
            self.api.CloseHandle(self.handle)


def steam_path(explicit):
    if explicit:
        return Path(explicit).resolve()
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
        return Path(winreg.QueryValueEx(key, "SteamExe")[0]).resolve()


def game_root(explicit=None):
    """The GTFO install: the given folder, else the Steam library that holds the game, read from the Steam registry key, its
    libraryfolders.vdf and the game's appmanifest."""
    if explicit:
        return Path(explicit).resolve()
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
        steam = Path(winreg.QueryValueEx(key, "SteamPath")[0])
    libraries = [steam]
    folders = steam / "steamapps/libraryfolders.vdf"
    if folders.is_file():
        libraries += [Path(value.replace("\\\\", "\\")) for value in re.findall(r'"path"\s+"([^"]+)"', folders.read_text(encoding="utf-8", errors="replace"))]
    for library in libraries:
        manifest = library / "steamapps" / ("appmanifest_" + APP_ID + ".acf")
        if manifest.is_file():
            folder = re.search(r'"installdir"\s+"([^"]+)"', manifest.read_text(encoding="utf-8", errors="replace"))
            if folder and (library / "steamapps/common" / folder[1] / "GTFO.exe").is_file():
                return (library / "steamapps/common" / folder[1]).resolve()
    raise FileNotFoundError("Steam 库里找不到 GTFO（appmanifest_" + APP_ID + ".acf）；用 --game 指定安装目录")


def launch_command(steam, profile):
    return [str(steam), "-applaunch", APP_ID,
            "--doorstop-enabled", "true", "--doorstop-target-assembly", str(profile / "BepInEx/core/BepInEx.Unity.IL2CPP.dll"),
            "--doorstop-clr-runtime-coreclr-path", str(profile / "dotnet/coreclr.dll"),
            "--doorstop-clr-corlib-dir", str(profile / "dotnet")]


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest() if hasattr(hashlib, "file_digest") else hashlib.sha256(stream.read()).hexdigest()
