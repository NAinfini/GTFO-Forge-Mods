"""Cut a PresentMon capture by level or by probe mark and print the frame statistics of each stretch.

python Mods/ForgePerformance/tools/analyze_play.py <presentmon.csv> <log>... [--process GTFO.exe] [--minimum 20] [--marks]

PresentMon (2.x) must have run with --qpc_time, so its rows and the game's log lines share one clock. The logs are the BepInEx
LogOutput.log of a session played with SummaryLog on (type=performance_state lines: one stretch per stay in a level, from the
change to InLevel to the next change) and/or ForgePerformance probe logs (type=perf_probe_mark lines: with --marks one stretch
per mark, up to the next mark). For each stretch: frames, average fps, 1% low (the average rate of the slowest 1% of
frames), frame time p50/p95/p99, and the medians of PresentMon's CPU busy, GPU busy and displayed time where the capture has them.
What cost the time inside a stretch comes from a probe session: analyze_samples.py with the same labels.
"""
import argparse
import csv
import re
import statistics

TIME = ["CPUStartQPC", "CPUStartQPCTime", "QPCTime", "TimeInQPC", "CPUStartTime"]
FRAME = ["FrameTime", "MsBetweenPresents", "msBetweenPresents", "MsBetweenAppStart"]
EXTRA = [("cpu busy ms", ["CPUBusy", "MsCPUBusy"]), ("cpu wait ms", ["CPUWait", "MsCPUWait"]), ("gpu busy ms", ["GPUBusy", "MsGPUBusy"]),
         ("gpu time ms", ["GPUTime", "MsGPUTime"]), ("displayed ms", ["DisplayedTime", "MsBetweenDisplayChange"])]


def field(line, key):
    match = re.search(r"(?:^|\t|\s)" + key + r"=([^\t\r\n]*)", line)
    return match.group(1) if match else ""


def stretches(logs, marks):
    found = []
    for path in logs:
        opened, last = None, None
        for line in open(path, encoding="utf-8", errors="replace"):
            if "type=performance_state" in line:
                qpc = int(field(line, "qpc"))
                if opened:
                    found.append((opened[0], qpc, opened[1]))
                    opened = None
                if field(line, "to") == "InLevel":
                    opened = (qpc, field(line, "level") + " @ " + field(line, "utc")[11:19])
            elif marks and "type=perf_probe_mark" in line:
                qpc = int(field(line, "qpc"))
                if last:
                    found.append((last[0], qpc, last[1]))
                last = (qpc, field(line, "label") + " @ " + field(line, "utc")[11:19])
        if opened:
            found.append((opened[0], None, opened[1] + " (log ends in the level)"))
        if last:
            found.append((last[0], None, last[1] + " (to the end of the capture)"))
    return sorted(found, key=lambda s: s[0])


def column(header, names):
    return next((name for name in names if name in header), None)


def number(text):
    try:
        return float(text)
    except ValueError:
        return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("capture")
    parser.add_argument("logs", nargs="+")
    parser.add_argument("--process", default="GTFO.exe")
    parser.add_argument("--minimum", type=float, default=20, help="shortest stretch reported, seconds")
    parser.add_argument("--marks", action="store_true")
    parser.add_argument("--frequency", type=float, default=1e7, help="QPC ticks per second (10 MHz on current Windows)")
    args = parser.parse_args()

    parts = stretches(args.logs, args.marks)
    rows = []
    with open(args.capture, encoding="utf-8", errors="replace", newline="") as handle:
        reader = csv.DictReader(handle)
        header = reader.fieldnames or []
        time, frame = column(header, TIME), column(header, FRAME)
        if not time or not frame:
            raise SystemExit("no QPC time or frame time column in " + args.capture + "; columns: " + ", ".join(header))
        extras = [(label, column(header, names)) for label, names in EXTRA if column(header, names)]
        kinds = "FrameType" if "FrameType" in header else None
        for row in reader:
            if row.get("Application", args.process).lower() != args.process.lower():
                continue
            if kinds and row[kinds] not in ("Application", "NA", ""):
                continue  # generated frames are counted through the displayed time, not as game frames
            stamp, value = number(row[time]), number(row[frame])
            if stamp is None or value is None:
                continue
            rows.append((stamp, value, [number(row[name]) for _, name in extras]))
    rows.sort(key=lambda r: r[0])
    print("{} game frames of {} in the capture; time column {}, frame column {}".format(len(rows), args.process, time, frame))
    print("| stretch | seconds | frames | fps | 1% low | p50 ms | p95 ms | p99 ms | " + " | ".join(label for label, _ in extras) + " |")
    print("|---|---|---|---|---|---|---|---|" + "---|" * len(extras))
    for start, end, label in parts:
        inside = [r for r in rows if r[0] >= start and (end is None or r[0] < end)]
        if len(inside) < 100:
            continue
        seconds = (inside[-1][0] - inside[0][0]) / args.frequency
        if seconds < args.minimum:
            continue
        times = sorted(r[1] for r in inside)
        slowest = times[-max(1, len(times) // 100):]
        cells = []
        for index in range(len(extras)):
            values = [r[2][index] for r in inside if r[2][index] is not None]
            cells.append("{:.2f}".format(statistics.median(values)) if values else "-")
        pick = lambda q: times[min(len(times) - 1, int(len(times) * q))]
        print("| {} | {:.0f} | {} | {:.1f} | {:.1f} | {:.2f} | {:.2f} | {:.2f} | {} |".format(
            label, seconds, len(times), 1000 * len(times) / sum(times), 1000 * len(slowest) / sum(slowest), pick(0.5), pick(0.95), pick(0.99),
            " | ".join(cells)))


if __name__ == "__main__":
    main()
