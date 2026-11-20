"""Turn the native probe's stack samples into per-label tables of where the main and render threads spend wall time.

python Mods/ForgePerformance/tools/analyze_samples.py <session folder> <probe log> --output report.md [--symbols <cache dir>]
    [--focus <text>]... [--hitches] [--top 40]

The session folder holds samples.bin, modules.tsv, il2cpp-methods.tsv and jit.tsv (ProbeNative). Labels are the probe log's mark
lines (their qpc). GameAssembly frames are named from the game's own method entries (nearest entry at or below the address;
the il2cpp runtime inside GameAssembly has no entries, so a frame far from any entry is shown as GameAssembly+rva), the mods'
compiled code from jit.tsv, every other module through dbghelp with Unity's and Microsoft's public symbol servers.
A sample is one millisecond of wall time of its thread, running or waiting; a wait shows as its ntdll frame.
"""
import argparse
import bisect
import collections
import ctypes
import ctypes.wintypes as wt
import os
from pathlib import Path
import struct

DEBUGGERS = r"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64"
PLUGIN_PREFIXES = ("Forge", "GTFO", "MTFO", "CConsole", "NVorbis")


class Dbghelp:
    def __init__(self, cache):
        os.add_dll_directory(DEBUGGERS)
        self.lib = ctypes.WinDLL(os.path.join(DEBUGGERS, "dbghelp.dll"))
        self.handle = wt.HANDLE(0x4650)
        self.lib.SymSetOptions(0x2 | 0x4 | 0x10)  # UNDNAME | DEFERRED_LOADS | LOAD_LINES off
        path = "srv*{0}*https://symbolserver.unity3d.com/;srv*{0}*https://msdl.microsoft.com/download/symbols".format(cache)
        self.lib.SymInitializeW.argtypes = [wt.HANDLE, wt.LPCWSTR, wt.BOOL]
        if not self.lib.SymInitializeW(self.handle, path, False):
            raise OSError("SymInitialize failed")
        self.lib.SymLoadModuleExW.argtypes = [wt.HANDLE, wt.HANDLE, wt.LPCWSTR, wt.LPCWSTR, ctypes.c_uint64, wt.DWORD, ctypes.c_void_p, wt.DWORD]
        self.lib.SymLoadModuleExW.restype = ctypes.c_uint64
        self.lib.SymFromAddrW.argtypes = [wt.HANDLE, ctypes.c_uint64, ctypes.POINTER(ctypes.c_uint64), ctypes.c_void_p]
        self.buffer = ctypes.create_string_buffer(88 + 2 * 2048)
        self.cache = {}

    def load(self, path, base, size):
        self.lib.SymLoadModuleExW(self.handle, None, path, None, base, size, None, 0)

    def name(self, address):
        if address in self.cache:
            return self.cache[address]
        struct.pack_into("<I", self.buffer, 0, 88)
        struct.pack_into("<I", self.buffer, 80, 2048)
        displacement = ctypes.c_uint64()
        name = None
        if self.lib.SymFromAddrW(self.handle, address, ctypes.byref(displacement), self.buffer):
            length = struct.unpack_from("<I", self.buffer, 76)[0]
            name = self.buffer.raw[84:84 + 2 * length].decode("utf-16-le", "replace")
        self.cache[address] = name
        return name


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("folder", type=Path)
    parser.add_argument("log", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--symbols", type=Path)
    parser.add_argument("--focus", action="append", default=[])
    parser.add_argument("--hitches", action="store_true")
    parser.add_argument("--mods", action="store_true")
    parser.add_argument("--top", type=int, default=40)
    parser.add_argument("--labels", default="")
    args = parser.parse_args()
    cache = args.symbols or args.folder.parent / "symcache"
    cache.mkdir(parents=True, exist_ok=True)

    modules = []
    for row in (args.folder / "modules.tsv").read_bytes().decode("utf-16-le", "replace").splitlines():
        base, size, _, _, path = row.split("\t", 4)
        modules.append((int(base, 16), int(size, 16), path))
    modules.sort()
    bases = [m[0] for m in modules]
    game = next(m for m in modules if m[2].lower().endswith("gameassembly.dll"))
    methods = []
    for row in (args.folder / "il2cpp-methods.tsv").read_text("utf-8", "replace").splitlines():
        address, _, name = row.split("\t", 2)
        methods.append((int(address, 16), name))
    methods.sort()
    # Shared bodies: keep the first name and say how many share it.
    entries, names = [], []
    for address, name in methods:
        if entries and entries[-1] == address:
            if not names[-1].endswith(" (+shared)"):
                names[-1] += " (+shared)"
            continue
        entries.append(address); names.append(name)
    jit = []
    path = args.folder / "jit.tsv"
    if path.exists():
        for row in path.read_text("utf-8", "replace").splitlines():
            parts = row.split("\t", 2)
            if len(parts) == 3 and parts[1].isdigit():
                jit.append((int(parts[0], 16), int(parts[1]), parts[2]))
    jit.sort()
    jit_starts = [j[0] for j in jit]
    helper = Dbghelp(cache)
    loaded = set()
    resolved = {}

    def resolve(address):
        hit = resolved.get(address)
        if hit is not None:
            return hit
        i = bisect.bisect_right(bases, address) - 1
        if i >= 0 and address < modules[i][0] + modules[i][1]:
            base, size, path = modules[i]
            short = os.path.basename(path)
            if base == game[0]:
                k = bisect.bisect_right(entries, address) - 1
                if k >= 0 and address - entries[k] < 0x8000:
                    text = names[k]
                else:
                    text = "GameAssembly+0x{:x}".format(address - base)
            else:
                if base not in loaded:
                    helper.load(path, base, size); loaded.add(base)
                name = helper.name(address)
                text = short + "!" + (name or "?")
        else:
            k = bisect.bisect_right(jit_starts, address) - 1
            if k >= 0 and address < jit[k][0] + jit[k][1]:
                text = "[mod] " + jit[k][2]
            else:
                text = "[jit/unknown]"
        resolved[address] = text
        return text

    marks, hitches = [], []
    for row in args.log.read_text("utf-8", "replace").splitlines():
        fields = dict(part.split("=", 1) for part in row.split("\t") if "=" in part)
        if fields.get("type") == "perf_probe_mark" and "qpc" in fields:
            marks.append((int(fields["qpc"]), fields["label"]))
        elif fields.get("type") == "perf_probe_hitch":
            hitches.append((int(fields["qpc"]), float(fields["ms"]), fields.get("label", "")))
    data = (args.folder / "samples.bin").read_bytes()
    magic, frequency, threads, _ = struct.unpack_from("<QQQQ", data, 0)
    offset = 32
    samples = []  # (qpc, thread, frames)
    while offset + 12 <= len(data):
        stamp, thread, count = struct.unpack_from("<QHH", data, offset)
        offset += 12
        if offset + 8 * count > len(data):
            break
        samples.append((stamp, thread, struct.unpack_from("<%dQ" % count, data, offset)))
        offset += 8 * count
    stamps = [s[0] for s in samples]
    out = ["# Stack samples", "", "{} samples, {:.0f} s, {} threads (0 = main, 1 = render). One sample = 1 ms of the thread's wall time at the sampling rate actually reached.".format(
        len(samples), (stamps[-1] - stamps[0]) / frequency if samples else 0, threads), ""]
    wanted = [w for w in args.labels.split(",") if w]

    def table(rows, label, thread):
        total = len(rows)
        if not total:
            return
        exclusive, inclusive = collections.Counter(), collections.Counter()
        for _, _, frames in rows:
            texts = [resolve(a) for a in frames]
            exclusive[texts[0]] += 1
            for text in set(texts):
                inclusive[text] += 1
        out.append("## {} / thread {}: {} samples".format(label, "main" if thread == 0 else "render", total))
        out.append("")
        out.append("| exclusive ms/s | function | | inclusive ms/s | function |")
        out.append("|---|---|---|---|---|")
        a, b = exclusive.most_common(args.top), inclusive.most_common(args.top)
        for i in range(max(len(a), len(b))):
            left = "{:.1f} | {}".format(a[i][1] * 1000 / total, a[i][0][:110]) if i < len(a) else " | "
            right = "{:.1f} | {}".format(b[i][1] * 1000 / total, b[i][0][:110]) if i < len(b) else " | "
            out.append("| " + left + " | | " + right + " |")
        out.append("")
        # by module of the innermost frame
        by_module = collections.Counter()
        for name, count in exclusive.items():
            by_module[name.split("!")[0] if "!" in name else ("GameAssembly (game code)" if not name.startswith("[") and not name.startswith("GameAssembly+") else name.split(" ")[0])] += count
        out.append("Innermost frame by module: " + ", ".join("{} {:.1f}".format(k, v * 1000 / total) for k, v in by_module.most_common(14)))
        out.append("")
        if args.mods and thread == 0:
            # A sample belongs to a plugin when a frame of that plugin's own code is on its stack; the outermost such frame names
            # the entry. Game code the plugin calls is included; the original of a method a plugin patched is not under a plugin
            # frame (it hangs under the patch wrapper), so it is not counted.
            by_plugin, by_entry, wrappers = collections.Counter(), collections.Counter(), 0
            for _, _, frames in rows:
                texts = [resolve(a) for a in frames]
                entry = None
                for text in reversed(texts):
                    if text.startswith("[mod] ") and text[6:].split(".")[0].startswith(PLUGIN_PREFIXES):
                        entry = text[6:]
                        break
                if entry:
                    by_plugin[entry.split(".")[0]] += 1
                    by_entry[entry] += 1
                elif texts[0].startswith(("[mod] dynamicClass", "[jit/unknown]", "coreclr.dll", "[mod] System", "[mod] Il2CppInterop")):
                    wrappers += 1
            out.append("Plugins on this thread, ms/s under their own frames: " + ", ".join("{} {:.1f}".format(k, v * 1000 / total) for k, v in by_plugin.most_common(20))
                       + ". Patch wrappers, stubs and the managed runtime with no plugin frame: {:.1f}.".format(wrappers * 1000 / total))
            out.append("")
            out.append("Plugin entries: " + "; ".join("{} {:.1f}".format(k[:100], v * 1000 / total) for k, v in by_entry.most_common(45)))
            out.append("")
        for focus in args.focus:
            callers, callees, hits = collections.Counter(), collections.Counter(), 0
            chains = collections.Counter()
            for _, _, frames in rows:
                texts = [resolve(a) for a in frames]
                for i, text in enumerate(texts):
                    if focus in text:
                        hits += 1
                        callees[texts[i - 1] if i else "(self)"] += 1
                        callers[texts[i + 1] if i + 1 < len(texts) else "(root)"] += 1
                        chains[" < ".join(t[:60] for t in texts[i + 1:i + 9])] += 1
                        break
            if hits:
                out.append("Focus `{}`: {:.1f} ms/s. Callees: {}. Callers: {}".format(focus, hits * 1000 / total,
                    "; ".join("{} {:.1f}".format(k[:90], v * 1000 / total) for k, v in callees.most_common(12)),
                    "; ".join("{} {:.1f}".format(k[:90], v * 1000 / total) for k, v in callers.most_common(8))))
                for chain, count in chains.most_common(6):
                    out.append("    - {:.1f} ms/s via {}".format(count * 1000 / total, chain))
                out.append("")

    for index, (start, label) in enumerate(marks):
        if wanted and label not in wanted:
            continue
        end = marks[index + 1][0] if index + 1 < len(marks) else (stamps[-1] if stamps else start)
        lo, hi = bisect.bisect_left(stamps, start), bisect.bisect_left(stamps, end)
        window = samples[lo:hi]
        for thread in range(threads):
            table([s for s in window if s[1] == thread], "{} ({:.1f} s)".format(label, (end - start) / frequency), thread)
    if args.hitches:
        out.append("## Long frames")
        out.append("")
        for stamp, ms, label in sorted(hitches, key=lambda h: -h[1])[:60]:
            lo, hi = bisect.bisect_left(stamps, stamp - int(ms * frequency / 1000)), bisect.bisect_left(stamps, stamp)
            rows = [s for s in samples[lo:hi] if s[1] == 0]
            inclusive = collections.Counter()
            for _, _, frames in rows:
                for text in set(resolve(a) for a in frames):
                    inclusive[text] += 1
            deepest = collections.Counter(resolve(s[2][0]) for s in rows)
            out.append("- {:.0f} ms at qpc {} ({}), {} samples. Innermost: {}. Inclusive: {}".format(ms, stamp, label, len(rows),
                "; ".join("{} {}".format(k[:70], v) for k, v in deepest.most_common(4)),
                "; ".join("{} {}".format(k[:70], v) for k, v in inclusive.most_common(40)[6:22])))
        out.append("")
    args.output.write_text("\n".join(out), "utf-8")
    print("written", args.output, "samples", len(samples))


if __name__ == "__main__":
    main()
