"""The one recursive delete for repository Python tools. A path is judged by its normalized spelling, never by what a
link resolves to; links found inside the target are removed as links and never entered. The same rules are in
Tools/safe-delete.ts and Tools/safe-delete.ps1. Self-test: python Tools/safe_delete.py"""
import os
import re
import shutil
import stat
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPARSE_POINT = 0x400


def _key(value):
    return os.path.normcase(value)


def _within(root, target):
    root, target = _key(root), _key(target)
    return target == root or target.startswith(root.rstrip(os.sep) + os.sep)


def _normal(value, label):
    value = os.fspath(value)
    if re.match(r"^[\\/]{2}[?.][\\/]", value):
        raise ValueError(f"safe_delete: a device-prefixed {label} path is not accepted: {value}")
    return os.path.abspath(value)


def _io(path):
    """The spelling used for file system calls: long paths work on Windows without the system long-path setting."""
    if os.name != "nt" or path.startswith("\\\\?\\"):
        return path
    return "\\\\?\\UNC\\" + path[2:] if path.startswith("\\\\") else "\\\\?\\" + path


def _is_link(path):
    info = os.lstat(_io(path))
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & REPARSE_POINT)


def _remove_link(path):
    try:
        os.rmdir(_io(path))
    except OSError:
        os.unlink(_io(path))


def _main_checkout():
    marker = os.path.join(REPO, ".git")
    if not os.path.isfile(marker):
        return None
    with open(marker, encoding="utf-8") as stream:
        match = re.search(r"^gitdir:\s*(.+)$", stream.read(), re.M)
    return os.path.abspath(os.path.join(match.group(1).strip(), "..", "..", "..")) if match else None


def protected_paths():
    """Paths that no authorized root may be or contain (so none can be deleted, nor any directory above them)."""
    bases = [REPO] + ([_main_checkout()] if _main_checkout() else [])
    paths = list(bases)
    for base in bases:
        paths += [os.path.join(base, "node_modules", ".pnpm"), os.path.join(base, "Web", "public"), os.path.join(base, "Source"), os.path.join(base, ".local", "deploy")]
    paths += [os.environ[name] for name in ("FORGE_ASSETS_ROOT", "FORGE_SOURCE_ROOT") if os.environ.get(name)]
    return [os.path.abspath(path) for path in paths]


def _sweep(directory, remove):
    found, pending = 0, [directory]
    while pending:
        current = pending.pop()
        with os.scandir(_io(current)) as entries:
            names = [entry.name for entry in entries]
        for name in names:
            child = os.path.join(current, name)
            if _is_link(child):
                found += 1
                if remove:
                    _remove_link(child)
            elif os.path.isdir(_io(child)):
                pending.append(child)
    return found


def _writable_then_retry(function, path, _error):
    os.chmod(path, stat.S_IWRITE)
    function(path)


def remove(target, root):
    """Removes target (file, directory tree or link) only if it lies within root, the authorized root the caller
    names. A link as the target, or inside it, is removed itself and never followed. A missing target is no error."""
    absolute, authorized = _normal(target, "target"), _normal(root, "root")
    for guarded in protected_paths():
        if _within(authorized, guarded):
            raise ValueError(f"safe_delete: the authorized root {authorized} is or contains the protected path {guarded}")
    if not _within(authorized, absolute):
        raise ValueError(f"safe_delete: {absolute} is outside the authorized root {authorized}")
    if not os.path.lexists(_io(authorized)):
        return
    if _is_link(authorized):
        raise ValueError(f"safe_delete: the authorized root is a link: {authorized}")
    walked = authorized
    between = [] if _key(absolute) == _key(authorized) else os.path.relpath(os.path.dirname(absolute), authorized).split(os.sep)
    for part in [p for p in between if p and p != "."]:
        walked = os.path.join(walked, part)
        if not os.path.lexists(_io(walked)):
            return
        if _is_link(walked):
            raise ValueError(f"safe_delete: a link lies between the root and the target: {walked}")
    if not os.path.lexists(_io(absolute)):
        return
    if _is_link(absolute):
        _remove_link(absolute)
        return
    if os.path.isdir(_io(absolute)):
        _sweep(absolute, True)
        if _sweep(absolute, False):
            raise ValueError(f"safe_delete: links remain below {absolute}; nothing else was deleted")
        shutil.rmtree(_io(absolute), onerror=_writable_then_retry)
    else:
        os.chmod(_io(absolute), stat.S_IWRITE)
        os.unlink(_io(absolute))


def _self_test():
    base = tempfile.mkdtemp(prefix="forge-safe-delete-")
    try:
        outside, work = os.path.join(base, "outside"), os.path.join(base, "work")
        os.makedirs(os.path.join(outside, "keep"))
        open(os.path.join(outside, "keep", "file.txt"), "w").close()
        deep = os.path.join(work, *["segment-" + "x" * 20] * 14)
        os.makedirs(_io(deep))
        open(_io(os.path.join(deep, "leaf.txt")), "w").close()
        link = os.path.join(work, "link")
        if os.name == "nt":
            import subprocess
            subprocess.run(["cmd", "/c", "mklink", "/J", link, outside], check=True, capture_output=True)
        else:
            os.symlink(outside, link)
        remove(work, work)
        assert os.path.exists(os.path.join(outside, "keep", "file.txt")), "link target was deleted"
        assert not os.path.exists(work), "work tree remains"
        os.makedirs(work)
        try:
            remove(outside, work)
        except ValueError:
            pass
        else:
            raise AssertionError("target outside the root was accepted")
        try:
            remove(REPO, REPO)
        except ValueError:
            pass
        else:
            raise AssertionError("the repository root was accepted")
        print("safe_delete self-test passed")
    finally:
        remove(base, base)


if __name__ == "__main__":
    sys.exit(_self_test())
