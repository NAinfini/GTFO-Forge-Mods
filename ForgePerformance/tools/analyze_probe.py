"""Summarise a ForgePerformance probe log (BepInEx/PerformanceLogs/ForgePerformance_*.log) per label.

    python Mods/ForgePerformance/tools/analyze_probe.py LOG [--output summary.md]

Each `type=perf_probe` line is one window. Windows are grouped by the label the last `Mark` set. Per label the table gives
the mean of frame time (average, p95, worst), main- and render-thread CPU time per frame and enemy count, and per probed
point the calls and milliseconds per second of game time, the worst single call and the most calls in one frame.
A point with no calls in a label is left out; nothing is filled in for it.

Labels named <item>-A-<n> and <item>-B-<n> are A/B segments of equal length (tools/crowd-ab.test). Per segment only windows with the
segment's final switches count, the first of them (the switch settling) and windows under 5 frames are dropped and the median
of the rest is taken. Each B segment is compared with the mean of the A segments before and after it, so a steady drift
cancels; the item is "consistent" when every such difference has the same sign. With --forgedev, ForgeDevelopment's
per-second frame_summary and frame_partition lines inside a segment's time span add its phases (ms per frame);
a second with fewer than 8 frames received is left out.
"""
import argparse
import re
import statistics
import sys
from collections import OrderedDict, defaultdict
from datetime import datetime, timedelta


def parse(path):
    windows = []
    for raw in open(path, encoding='utf-8', errors='replace'):
        fields = dict(part.split('=', 1) for part in raw.rstrip('\n').split('\t') if '=' in part)
        if fields.get('type') == 'perf_probe':
            windows.append(fields)
    return windows


def number(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def mean(values):
    values = [v for v in values if v is not None]
    return sum(values) / len(values) if values else None


def summarise(windows):
    groups = OrderedDict()
    for window in windows:
        groups.setdefault(window.get('label', 'none'), []).append(window)
    out = []
    for label, rows in groups.items():
        seconds = sum(number(r.get('frames')) * number(r.get('frame_ms_avg')) / 1000 for r in rows
                      if number(r.get('frames')) and number(r.get('frame_ms_avg')))
        head = OrderedDict(label=label, windows=len(rows), seconds=round(seconds, 1),
                           switches=rows[-1].get('switches', ''), host=rows[-1].get('host', ''))
        for key in ('frame_ms_avg', 'frame_ms_p95', 'frame_ms_max', 'main_cpu_ms', 'render_cpu_ms', 'enemies',
                    'far_skins', 'throttled_syncs', 'gc0'):
            value = mean(number(r.get(key)) for r in rows)
            head[key] = None if value is None else round(value, 3)
        points = defaultdict(lambda: {'n': 0.0, 'ms': 0.0, 'max': 0.0, 'peak': 0.0, 'timed': False})
        for r in rows:
            for key, value in r.items():
                if key.endswith('_n'):
                    name = key[:-2]
                    p = points[name]
                    p['n'] += number(value) or 0
                    p['peak'] = max(p['peak'], number(r.get(name + '_peak')) or 0)
                    if name + '_ms' in r:
                        p['timed'] = True
                        p['ms'] += number(r[name + '_ms']) or 0
                        p['max'] = max(p['max'], number(r.get(name + '_max_ms')) or 0)
        reach = sum(number(r.get('reachable_enemies')) or 0 for r in rows)
        out.append((head, points, seconds, reach))
    return out


MIN_SAMPLES = 8
SEGMENT = re.compile(r'^(?P<item>.+)-(?P<side>[AB])-(?P<n>\d+)$')
PROBE_KEYS = ('frame_ms_avg', 'frame_ms_p95', 'main_cpu_ms', 'render_cpu_ms')
FORGEDEV_KEYS = ('cpu_main', 'present_wait', 'other', 'behaviour_update', 'behaviour_late', 'behaviour_fixed', 'physics',
                 'animation_begin', 'animation_end', 'render_submission', 'renderers', 'navigation', 'enemy_update',
                 'enemy_fixed', 'enemy_master', 'enemy_master_fixed', 'post_late_update_other')


def utc(text):
    return datetime.strptime(text[:23].rstrip('Z'), '%Y-%m-%dT%H:%M:%S.%f')


def parse_forgedev(path):
    """(time, {phase: ms per frame}) per second; frame_summary and frame_partition of the same second are merged."""
    seconds = OrderedDict()
    for raw in open(path, encoding='utf-8', errors='replace'):
        if 'type=frame_summary' not in raw and 'type=frame_partition' not in raw:
            continue
        parts = raw.rstrip('\n').split('\t')
        row = seconds.setdefault(parts[0], {})
        for part in parts[1:]:
            key, _, value = part.partition('=')
            if key.endswith('_ms_stats'):
                stats = value.split(',')
                count, total = number(stats[0]), number(stats[1]) if len(stats) > 1 else None
                if count and total is not None:
                    row[key[:-len('_ms_stats')]] = total / count
                    row.setdefault('_samples', count)
    # A second in which the collector received only a few frames (a hitch, or samples it dropped) does not describe frames.
    return [(utc(stamp), row) for stamp, row in seconds.items() if row.get('_samples', 0) >= MIN_SAMPLES]


def segments(windows):
    out = OrderedDict()
    for window in windows:
        match = SEGMENT.match(window.get('label', ''))
        if match:
            out.setdefault((match['item'], match['side'], int(match['n'])), []).append(window)
    return out


def segment_values(rows, forgedev):
    # Only windows with the segment's final switch state count: one console command switches one option, so a segment that
    # switches several opens with a window per command.
    final = rows[-1].get('switches') if rows else None
    kept = [r for r in rows if (number(r.get('frames')) or 0) >= 5 and r.get('switches') == final][1:]
    values = {}
    for key in PROBE_KEYS:
        series = [number(r.get(key)) for r in kept if number(r.get(key)) is not None]
        if series:
            values[key] = statistics.median(series)
    for key in {k[:-3] for r in kept for k in r if k.endswith('_ms') and not k.endswith('_max_ms') and k[:-3] + '_n' in r}:
        # Point milliseconds per second of the window, so windows of different length compare.
        series = [number(r.get(key + '_ms')) * 1000 / (number(r['frames']) * number(r['frame_ms_avg']))
                  for r in kept if number(r.get(key + '_ms')) is not None and number(r.get('frame_ms_avg'))]
        if series:
            values['point:' + key] = statistics.median(series)
    if forgedev and kept and 'utc' in kept[0]:
        start = utc(kept[0]['utc']) - timedelta(seconds=1)
        end = utc(kept[-1]['utc'])
        inside = [row for stamp, row in forgedev if start < stamp <= end]
        for key in FORGEDEV_KEYS:
            series = [row[key] for row in inside if key in row]
            if series:
                values['fd:' + key] = statistics.median(series)
    values['enemies'] = statistics.median([number(r.get('enemies')) or 0 for r in kept]) if kept else None
    # Share of frames the game window had focus.
    focus = [number(r.get('foreground_frames')) / number(r['frames']) for r in kept if number(r.get('foreground_frames')) is not None]
    if focus:
        values['foreground'] = mean(focus)
    values['windows'] = len(kept)
    return values


def compare(windows, forgedev):
    by_item = OrderedDict()
    for (item, side, n), rows in segments(windows).items():
        by_item.setdefault(item, {'A': {}, 'B': {}})[side][n] = segment_values(rows, forgedev)
    results = OrderedDict()
    for item, sides in by_item.items():
        keys = sorted({k for side in sides.values() for seg in side.values() for k in seg if k not in ('windows',)})
        rows = OrderedDict()
        for key in keys:
            diffs, a_values, b_values = [], [], []
            for n, b in sorted(sides['B'].items()):
                around = [sides['A'][m][key] for m in (n, n + 1) if m in sides['A'] and key in sides['A'][m]]
                if key in b and around:
                    diffs.append(b[key] - sum(around) / len(around))
                    b_values.append(b[key])
            a_values = [seg[key] for seg in sides['A'].values() if key in seg]
            if not diffs:
                continue
            a_median = statistics.median(a_values) if a_values else None
            same = all(d > 0 for d in diffs) or all(d < 0 for d in diffs)
            rows[key] = OrderedDict(a=a_median, b=statistics.median(b_values), diff=statistics.median(diffs),
                                    pct=None if not a_median else 100 * statistics.median(diffs) / a_median,
                                    diffs=diffs, consistent=same and len(diffs) >= 3)
        results[item] = (len(sides['A']), len(sides['B']), rows)
    return results


def render_compare(results):
    lines = ['', '# A/B segments', '',
             'B minus the mean of the A segments around it; median over the B segments. "yes" = every difference has the same sign (3 or more pairs).']
    for item, (a_count, b_count, rows) in results.items():
        lines += ['', '## ' + item + ' ({} A, {} B)'.format(a_count, b_count), '',
                  '| measure | A median | B median | B-A median | % | consistent | each B-A |', '|---|---|---|---|---|---|---|']
        order = sorted(rows.items(), key=lambda kv: (not kv[0] in PROBE_KEYS + ('enemies',), not kv[0].startswith('fd:'), -abs(kv[1]['diff'])))
        for key, r in order:
            if key.startswith('point:') and abs(r['a'] or 0) < 0.05 and abs(r['b']) < 0.05:
                continue
            lines.append('| {} | {} | {:.3f} | {:+.3f} | {} | {} | {} |'.format(
                key, '-' if r['a'] is None else '{:.3f}'.format(r['a']), r['b'], r['diff'],
                '-' if r['pct'] is None else '{:+.1f}'.format(r['pct']), 'yes' if r['consistent'] else 'no',
                ' '.join('{:+.2f}'.format(d) for d in r['diffs'])))
    return '\n'.join(lines) + '\n'


def render_breakdown(windows, forgedev):
    """Where the main thread goes, per label that is not an A/B segment: ForgeDevelopment phases and probed points,
    medians of the windows, in ms per frame."""
    groups = OrderedDict()
    for window in windows:
        if not SEGMENT.match(window.get('label', '')):
            groups.setdefault(window.get('label', 'none'), []).append(window)
    lines = ['', '# Main-thread split per label (ms per frame, window medians)']
    for label, rows in groups.items():
        values = segment_values(rows, forgedev)
        frame = values.get('frame_ms_avg')
        if not frame or values['windows'] < 3:
            continue
        lines += ['', '## {} ({} windows, frame {:.2f} ms, main CPU {} ms, foreground {}, enemies {})'.format(
            label, values['windows'], frame, '{:.2f}'.format(values['main_cpu_ms']) if 'main_cpu_ms' in values else '-',
            '{:.0%}'.format(values['foreground']) if 'foreground' in values else '-', values['enemies']),
            '', '| source | ms per frame | share of frame |', '|---|---|---|']
        # Probed points are ms per second; a second holds 1000/frame frames.
        entries = [(k, v) for k, v in values.items() if k.startswith('fd:')]
        entries += [(k, v * frame / 1000) for k, v in values.items() if k.startswith('point:')]
        for key, value in sorted(entries, key=lambda kv: -kv[1]):
            if value >= 0.05:
                lines.append('| {} | {:.2f} | {:.0f}% |'.format(key, value, 100 * value / frame))
    return '\n'.join(lines) + '\n'


def render(summary):
    lines = ['# ForgePerformance probe summary', '']
    lines.append('| label | s | host | frame avg ms | p95 ms | worst ms | main CPU ms/frame | render CPU ms/frame | enemies | switches |')
    lines.append('|---|---|---|---|---|---|---|---|---|---|')
    for head, _, _, _ in summary:
        lines.append('| {label} | {seconds} | {host} | {frame_ms_avg} | {frame_ms_p95} | {frame_ms_max} | {main_cpu_ms} | {render_cpu_ms} | {enemies} | {switches} |'.format(**head))
    for head, points, seconds, reach in summary:
        lines += ['', '## ' + head['label'], '']
        lines.append('| point | calls/s | ms/s | worst call ms | most calls in a frame |')
        lines.append('|---|---|---|---|---|')
        per = seconds if seconds > 0 else 1
        for name, p in sorted(points.items(), key=lambda item: -item[1]['ms']):
            lines.append('| {} | {:.1f} | {} | {} | {:.0f} |'.format(
                name, p['n'] / per, '{:.3f}'.format(p['ms'] / per) if p['timed'] else 'count only',
                '{:.3f}'.format(p['max']) if p['timed'] else '-', p['peak']))
        if reach:
            lines.append('')
            lines.append('Enemies walked by reachable-enemy queries: {:.0f} ({:.1f}/s).'.format(reach, reach / per))
    return '\n'.join(lines) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('log')
    parser.add_argument('--output')
    parser.add_argument('--forgedev', help='ForgeDevelopment performance log of the same session (BepInEx/PerformanceLogs/ForgeDevelopment_*.log)')
    args = parser.parse_args()
    windows = parse(args.log)
    if not windows:
        print('no perf_probe lines in ' + args.log, file=sys.stderr)
        return 2
    text = render(summarise(windows))
    forgedev = parse_forgedev(args.forgedev) if args.forgedev else None
    text += render_breakdown(windows, forgedev)
    results = compare(windows, forgedev)
    if results:
        text += render_compare(results)
    if args.output:
        open(args.output, 'w', encoding='utf-8').write(text)
    else:
        sys.stdout.write(text)
    return 0


if __name__ == '__main__':
    sys.exit(main())
