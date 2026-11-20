"""Summarise a PresentMon 2 CSV for one process: frame time, CPU busy and wait, GPU busy and wait, present mode.

python Mods/ForgePerformance/tools/presentmon_summary.py <presentmon.csv> [<from ms> <to ms>]

The optional window is in PresentMon's TimeInMs, milliseconds since the capture started.
"""
import csv
import statistics
import sys


def number(text):
    try:
        return float(text)
    except (TypeError, ValueError):
        return None


def main():
    rows = list(csv.DictReader(open(sys.argv[1], encoding='utf-8', newline='')))
    if len(sys.argv) > 3:
        low, high = float(sys.argv[2]), float(sys.argv[3])
        rows = [r for r in rows if low <= (number(r['TimeInMs']) or -1) <= high]
    if not rows:
        print('no frames')
        return 1
    span = number(rows[-1]['TimeInMs']) or 0
    span -= number(rows[0]['TimeInMs']) or 0
    print('frames {}  span {:.1f} s  average fps {:.1f}  present mode {}  sync interval {}'.format(
        len(rows), span / 1000, len(rows) / (span / 1000) if span else 0,
        statistics.mode(r['PresentMode'] for r in rows), statistics.mode(r['SyncInterval'] for r in rows)))
    print('| metric | mean ms | p50 ms | p95 ms | p99 ms |')
    print('|---|---|---|---|---|')
    for key in ('MsBetweenPresents', 'MsBetweenDisplayChange', 'MsCPUBusy', 'MsCPUWait', 'MsGPUBusy', 'MsGPUWait', 'MsGPUTime',
                'MsGPULatency', 'MsInPresentAPI', 'MsUntilDisplayed'):
        values = sorted(v for v in (number(r.get(key)) for r in rows) if v is not None)
        if not values:
            continue
        pick = lambda q: values[min(len(values) - 1, int(q * len(values)))]
        print('| {} | {:.2f} | {:.2f} | {:.2f} | {:.2f} |'.format(key, statistics.fmean(values), pick(.5), pick(.95), pick(.99)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
