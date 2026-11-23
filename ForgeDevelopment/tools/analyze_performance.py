"""Stream ForgeDevelopment performance logs (forgeperformance-1/2); other formats are refused.

Usage: python Mods/ForgeDevelopment/tools/analyze_performance.py SESSION.log --output report.md
Three sequential passes retain bounded histograms, context keys and marker windows,
never the log's rows. Timestamps stay in the log's timezone. Standard library only.
"""
import argparse
from collections import Counter, deque
from datetime import datetime
import heapq
import math
from pathlib import Path
import re

SCHEMAS = ("forgeperformance-1", "forgeperformance-2")
BUCKET_LIMIT = 3350
MAX_RUNS, MAX_CONTEXTS, DETAIL_CAPACITY = 256, 8192, 2048
PHASES = (
    ("敌人更新", "enemy_update_ms"), ("敌人固定更新", "enemy_fixed_ms"),
    ("敌人主机更新", "enemy_master_ms"), ("敌人主机固定更新", "enemy_master_fixed_ms"),
    ("寻路阶段", "navigation_ms"), ("物理主步", "physics_ms"), ("物理后处理", "physics_late_ms"),
    ("动画开始阶段", "animation_begin_ms"), ("动画完成阶段", "animation_end_ms"),
    ("渲染器更新", "renderers_ms"), ("渲染提交阶段", "render_submission_ms"),
    ("脚本更新", "behaviour_update_ms"), ("脚本固定更新", "behaviour_fixed_ms"),
    ("脚本后期更新", "behaviour_late_ms"), ("显式 GC", "gc_explicit_ms"),
    ("初始化叶工作合计", "initialization_ms"), ("早期更新叶工作合计", "early_update_ms"),
    ("预更新其余叶工作合计", "pre_update_other_ms"), ("后期更新其余叶工作合计", "post_late_update_other_ms"),
)
CLOCKS = (("CPU 主线程", "cpu_main_ms"), ("等待 GPU／呈现", "present_wait_ms"), ("其他", "other_ms"))
COMPARISON_FIELDS = (("帧时间", "frame_ms"), *CLOCKS[:2], *PHASES)
SCENE_FIELDS = ("area_uid", "area_size", "node_registered_enemies", "close_enemies", "aggressive_enemies",
                "fog_density", "fog_altitude", "player_y", "fog_relative_height")
MEASURED_FIELDS = ("frame_ms", *(key for _, key in CLOCKS), *(key for _, key in PHASES),
                   "collector_previous_frame_ms", "observer_ms", "main_managed_allocated_bytes",
                   "il2cpp_used_bytes", "il2cpp_heap_bytes", *SCENE_FIELDS)


def number(row, key, default=0.0):
    try:
        value = float(row.get(key, default))
        return value if math.isfinite(value) else default
    except (TypeError, ValueError):
        return default


def observed(row, key):
    return number(row, key, None)


def timestamp(value):
    value = re.sub(r"\.(\d+)", lambda m: "." + m[1][:6].ljust(6, "0"), value)
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def log_seconds(row):
    return row["_seconds"] if "_seconds" in row else timestamp(row["time"]).timestamp()


class LogSource:
    """Reopenable stream: malformed counts describe one pass, not three."""
    def __init__(self, path):
        self.path, self.malformed = Path(path), 0
        # Read the open handle's end position, not Explorer/directory metadata
        # which Windows can leave at zero while the game keeps the file open.
        with self.path.open("rb") as stream:
            self.size = stream.seek(0, 2)

    def __iter__(self):
        self.malformed = 0
        remaining = self.size
        with self.path.open("rb") as stream:
            while remaining and (raw := stream.readline(min(remaining, 1_048_577))):
                remaining -= len(raw)
                line = raw.decode("utf-8-sig", errors="replace")
                if len(raw) > 1_048_576:
                    while remaining and not raw.endswith(b"\n"):
                        raw = stream.readline(min(remaining, 1_048_576)); remaining -= len(raw)
                    self.malformed += 1
                    continue
                if not raw.endswith(b"\n"):
                    self.malformed += 1
                    continue
                parts = line.rstrip("\r\n").split("\t")
                try:
                    seconds = timestamp(parts[0]).timestamp()
                except ValueError:
                    self.malformed += 1
                    continue
                row = dict(part.split("=", 1) for part in parts[1:] if "=" in part)
                if "type" not in row:
                    self.malformed += 1
                    continue
                row["time"], row["_seconds"] = parts[0], seconds
                yield row


def read_log(path):
    # The counter becomes available after consumption. Preserve the calling
    # convention report(*read_log(path)), without materializing a row list.
    return LogSource(path), 0


def bucket(value):
    absolute = abs(value)
    if absolute <= 1:
        index = math.ceil(absolute * 100)
    elif absolute <= 128:
        index = 100 + math.ceil((absolute - 1) * 10)
    elif absolute <= 2048:
        index = 1370 + math.ceil(absolute - 128)
    else:
        index = 3290 + math.ceil(math.log2(absolute / 2048))
    if index > BUCKET_LIMIT:
        raise ValueError("measurement exceeds finite histogram bounds")
    return -index if value < 0 else index


def bucket_value(index):
    absolute = abs(index)
    value = absolute / 100 if absolute <= 100 else 1 + (absolute - 100) / 10 if absolute <= 1370 else \
        128 + absolute - 1370 if absolute <= 3290 else 2048 * 2 ** (absolute - 3290)
    return -value if index < 0 else value


class Stats:
    def __init__(self, scale=1):
        self.scale, self.hist = scale, Counter()
        self.count, self.total = 0, 0.0
        self.minimum, self.maximum, self.first, self.last = math.inf, -math.inf, None, None

    def add(self, value, weight=1):
        if value is None or not math.isfinite(value):
            return
        index = bucket(value * self.scale)
        self.hist[index] += weight
        if not self.count:
            self.first = value
        self.count += weight
        self.total += value * weight
        self.minimum, self.maximum, self.last = min(self.minimum, value), max(self.maximum, value), value

    def merge(self, row, key):
        packed = row.get(key + "_stats")
        if packed is None:
            return
        fields = packed.split(",")
        if len(fields) not in (4, 6):
            raise ValueError(f"invalid {key} aggregate tuple")
        count = float(fields[0])
        if not count:
            return
        total, low, high = map(float, fields[1:4])
        if count < 0 or not count.is_integer() or not all(math.isfinite(v) for v in (total, low, high)) or low > high:
            raise ValueError(f"invalid {key} aggregate at {row.get('time')}")
        hist = row.get(key + "_hist")
        if hist is not None:
            scale = number(row, key + "_hist_scale", self.scale)
            if scale != self.scale:
                raise ValueError(f"unsupported {key} histogram scale {scale}")
            counts = Counter()
            index = 0
            for pair in hist.split(","):
                delta, weight = map(int, pair.split(":"))
                index += delta
                if abs(index) > BUCKET_LIMIT or weight <= 0 or index in counts:
                    raise ValueError(f"invalid {key} histogram")
                counts[index] = weight
            if sum(counts.values()) != count:
                raise ValueError(f"{key} histogram count does not match aggregate")
            self.hist.update(counts)
        if not self.count:
            self.first = number({"v": fields[4]}, "v", None) if len(fields) == 6 else None
        self.last = number({"v": fields[5]}, "v", None) if len(fields) == 6 else None
        self.count += int(count)
        self.total += total
        self.minimum, self.maximum = min(self.minimum, low), max(self.maximum, high)

    def quantile(self, fraction):
        count = sum(self.hist.values())
        if count != self.count or not count:
            return None
        rank = math.ceil(count * fraction)
        for index in sorted(self.hist):
            rank -= self.hist[index]
            if rank <= 0:
                return bucket_value(index) / self.scale

    @property
    def mean(self):
        return self.total / self.count if self.count else None


class RawFrameStats(Stats):
    """Fixed 0.001 ms bins for raw v1 frame statistics, not raw value storage.

    Above 2048 ms, fixed power-of-two bins bound even pathological inputs.
    Phase histograms use the same compact precision as v2.
    """
    def add(self, value, weight=1):
        if value is None or not math.isfinite(value) or value < 0:
            return
        index = round(value * 1000) if value <= 2048 else 2_048_000 + math.ceil(math.log2(value / 2048))
        if index > 2_048_060:
            raise ValueError("raw frame exceeds finite histogram bounds")
        self.hist[index] += weight
        if not self.count:
            self.first = value
        self.count += weight; self.total += value * weight
        self.minimum, self.maximum, self.last = min(self.minimum, value), max(self.maximum, value), value

    def quantile(self, fraction):
        if not self.count:
            return None
        rank = math.ceil(self.count * fraction)
        for index in sorted(self.hist):
            rank -= self.hist[index]
            if rank <= 0:
                return index / 1000 if index <= 2_048_000 else 2048 * 2 ** (index - 2_048_000)


def scale_for(key):
    return 100 if key in ("fog_density", "collector_previous_frame_ms", "observer_ms") else 1


class Measurements:
    def __init__(self):
        self.metrics = {}
        self.complete, self.complete_wall, self.complete_cpu, self.complete_wait = 0, 0.0, 0.0, 0.0
        self.thresholds, self.marks = Counter(), Counter()
        self.events = Counter()
        self.first_time = self.last_time = "unavailable"

    def metric(self, key):
        if key not in self.metrics:
            self.metrics[key] = Stats(scale_for(key))
        return self.metrics[key]

    @property
    def count(self):
        return self.metric("frame_ms").count

    def add(self, row):
        summary = row["type"] == "frame_summary"
        if not summary and not self.count:
            self.metrics["frame_ms"] = RawFrameStats()
        if self.first_time == "unavailable":
            self.first_time = row.get("time", "unavailable")
        self.last_time = row.get("time", "unavailable")
        for key in MEASURED_FIELDS:
            if summary:
                self.metric(key).merge(row, key)
            else:
                value = observed(row, key)
                if value is not None and (value >= 0 or key in ("fog_relative_height", "fog_altitude", "player_y")):
                    self.metric(key).add(value)
        if summary:
            for key in ("hitch_frames", "ge33_frames", "ge50_frames", "ge100_frames"):
                self.thresholds[key] += int(number(row, key))
            for mark in (1, 2, 4, 8, 16, 32):
                self.marks[mark] += int(number(row, f"mark_{mark}_frames"))
            if "complete_frames" in row:
                self.partition(row, metrics=False)
        else:
            wall = number(row, "frame_ms")
            for limit in (33, 50, 100):
                self.thresholds[f"ge{limit}_frames"] += wall >= limit
            for mark in (1, 2, 4, 8, 16, 32):
                self.marks[mark] += bool(int(number(row, "marks")) & mark)
            if complete_frame(row):
                self.complete += 1
                self.complete_wall += wall
                self.complete_cpu += number(row, "cpu_main_ms")
                self.complete_wait += number(row, "present_wait_ms")
        for key in ("spawn_groups", "wave_starts"):
            self.events[key] += int(number(row, key))

    def partition(self, row, metrics=True):
        if metrics:
            for _, key in CLOCKS:
                self.metric(key).merge(row, key)
        self.complete += int(number(row, "complete_frames"))
        self.complete_wall += number(row, "complete_wall_ms")
        self.complete_cpu += number(row, "complete_cpu_ms")
        self.complete_wait += number(row, "complete_wait_ms")


def complete_frame(row):
    values = [observed(row, key) for key in ("cpu_main_ms", "present_wait_ms", "other_ms", "frame_ms")]
    return (row.get("partition_status") == "samples" and all(v is not None and v >= 0 for v in values)
            and abs(sum(values[:3]) - values[3]) <= .002)


def side_decision(frames):
    if not isinstance(frames, Measurements):
        measured = Measurements()
        for row in frames:
            measured.add(row)
        frames = measured
    if not frames.complete_wall or frames.complete < .9 * frames.count:
        return "分解覆盖不足，无法可靠判断瓶颈侧"
    shares = [frames.complete_cpu / frames.complete_wall, frames.complete_wait / frames.complete_wall]
    shares.append(1 - sum(shares))
    if shares[0] >= .5 and shares[1] < .25:
        result = "主线程 CPU 侧占时为主（推断）"
    elif shares[1] >= .5:
        result = "等待 GPU／呈现侧占时为主（推断，需排除同步／帧率限制）"
    elif shares[2] >= .5:
        result = "其他时间为主，现有测点不能归因 CPU／GPU"
    else:
        result = "混合耗时，没有单一主导侧"
    return result + "；主线程／等待／其他占比 " + "/".join(f"{v:.1%}" for v in shares)


def display(value, precision=3):
    return f"{value:.{precision}f}" if value is not None and math.isfinite(value) else "unavailable"


def comparison(lines, label, baseline, exposed):
    lines += ["", f"### {label}", ""]
    if baseline.count < 30 or exposed.count < 30:
        lines.append(f"unavailable：可比较样本不足（基线 {baseline.count}，目标 {exposed.count}；每组至少 30 帧）。")
        return
    lines += [f"基线 {baseline.count} 帧，目标 {exposed.count} 帧。分项为包含内部工作的墙钟时间，嵌套项不可相加。", "",
              "| 分项 | 基线平均 ms | 目标平均 ms | 平均增量 ms | 基线 P95 ms | 目标 P95 ms | 有效样本 基线／目标 |",
              "|---|---:|---:|---:|---:|---:|---:|"]
    increases = []
    for name, key in COMPARISON_FIELDS:
        a, b = baseline.metric(key), exposed.metric(key)
        if not a.count or not b.count:
            lines.append(f"| {name} | unavailable | unavailable | unavailable | unavailable | unavailable | {a.count}/{b.count} |")
            continue
        delta = b.mean - a.mean
        lines.append(f"| {name} | {a.mean:.3f} | {b.mean:.3f} | {delta:+.3f} | {display(a.quantile(.95))} | {display(b.quantile(.95))} | {a.count}/{b.count} |")
        if (name, key) in PHASES and a.count >= .9 * baseline.count and b.count >= .9 * exposed.count and delta > .1:
            increases.append((delta, name))
    rising = sorted(increases, reverse=True)[:4]
    lines.append("观测到增量较大的分项：" + ("、".join(f"{name} +{delta:.3f} ms" for delta, name in rising) if rising else "没有覆盖充分且增量超过 0.1 ms 的分项") + "。这只说明同步变化，不能确认因果。")


class MarkerWindows:
    def __init__(self):
        self.history, self.pending, self.best = deque(maxlen=DETAIL_CAPACITY), None, []
        self.latest, self.last_marker, self.attempt = -math.inf, -math.inf, None
        self.serial, self.evicted = 0, 0

    def finish(self):
        if self.pending is None:
            return
        attempt, row, before, after, _ = self.pending
        delta = after.metric("frame_ms").quantile(.95) - before.metric("frame_ms").quantile(.95) if min(before.count, after.count) >= 30 else -math.inf
        self.serial += 1
        heapq.heappush(self.best, (delta, self.serial, attempt, row, before, after))
        if len(self.best) > 8:
            heapq.heappop(self.best)
        self.pending = None

    def add(self, row):
        attempt, stamp = row.get("attempt", "unavailable"), log_seconds(row)
        if self.attempt != attempt:
            self.finish(); self.history.clear(); self.last_marker = self.latest = -math.inf; self.attempt = attempt
        self.latest = max(self.latest, stamp)
        if self.pending and self.latest >= self.pending[4] + 3:
            self.finish()
        if self.pending and self.pending[4] <= stamp < self.pending[4] + 3:
            self.pending[3].add(row)
        while self.history and self.history[0][0] < self.latest - 3:
            self.history.popleft()
        # v2 writes historical frames when a marker arrives. Arrival order may
        # differ from frame order, so select by timestamps at the marker itself.
        if stamp >= self.latest - 3:
            if len(self.history) == DETAIL_CAPACITY:
                self.evicted += 1
            self.history.append((stamp, row))
        if int(number(row, "marks")) and stamp - self.last_marker >= 3:
            self.finish(); self.last_marker = stamp
            before, after = Measurements(), Measurements()
            for previous_stamp, previous in self.history:
                if stamp - 3 <= previous_stamp < stamp:
                    before.add(previous)
            after.add(row)
            self.pending = (attempt, row, before, after, stamp)


def measured_rows(source):
    """At most one pending v2 window, joined to its partition and memory row."""
    pending = None
    for row in source:
        kind, schema = row["type"], row.get("measurement_schema")
        if kind == "frame_sample" and schema == SCHEMAS[0] and valid_frame(row):
            yield row
        elif kind == "frame_summary" and schema == SCHEMAS[1]:
            if pending is not None:
                yield pending
            pending = dict(row)
        elif pending is not None and kind in ("frame_partition", "memory_summary") and schema == SCHEMAS[1]:
            if any(row.get(key) != pending.get(key) for key in ("state", "attempt", "frame")):
                raise ValueError(f"summary/partition/memory identity mismatch: {[(key, pending.get(key), row.get(key)) for key in ('state', 'attempt', 'frame')]}")
            pending.update({key: value for key, value in row.items() if key not in ("type", "time", "window_start", "_seconds")})
    if pending is not None:
        yield pending


def valid_frame(row):
    return number(row, "frame_ms", -1) > 0


def scene_range(row, key):
    if row["type"] != "frame_summary":
        value = observed(row, key)
        return (value, value) if value is not None else None
    fields = row.get(key + "_stats", "0,unavailable,unavailable,unavailable").split(",")
    if float(fields[0]) != number(row, "samples"):
        return None
    low, high = (number({"v": v}, "v", None) for v in fields[2:4])
    return (low, high) if low is not None and high is not None else None


def stable(row, key, transform=lambda v: v):
    values = scene_range(row, key)
    if values is None:
        return None
    low, high = map(transform, values)
    return low if low == high else None


class ContextComparison:
    def __init__(self, kind, run):
        self.kind, self.groups, self.common = kind, (set(), set()), set()
        self.baseline, self.target = Measurements(), Measurements()
        self.excluded = 0
        self.low = self.high = None
        if kind != "room":
            metric = run.metric(kind)
            if metric.count and metric.maximum - metric.minimum >= .000001:
                self.low, self.high = metric.quantile(.25), metric.quantile(.75)
                if self.high <= self.low:
                    self.high = metric.maximum

    def classify(self, row):
        enemies = stable(row, "aggressive_enemies", lambda v: int(v) // 10)
        if enemies is None:
            return None
        if self.kind == "room":
            size = stable(row, "area_size")
            density = stable(row, "fog_density", lambda v: round(v, 3))
            if size not in (1, 2, 3, 4, 5) or density is None:
                return None
            return (size in (3, 4), (density, enemies))
        if self.low is None:
            return None
        limits = scene_range(row, self.kind)
        area = stable(row, "area_uid", int)
        if limits is None or area is None:
            return None
        scale = scale_for(self.kind)
        low, high = bucket(self.low * scale), bucket(self.high * scale)
        a, b = (bucket(value * scale) for value in limits)
        if b <= low:
            side = self.kind != "fog_relative_height"
        elif a >= high:
            side = self.kind == "fog_relative_height"
        else:
            return None
        context = (area, enemies)
        if self.kind == "fog_relative_height":
            density = stable(row, "fog_density", lambda v: round(v, 3))
            if density is None:
                return None
            context += (density,)
        return (not side, context)


def contexts(source, runs):
    comparisons = {attempt: [ContextComparison(kind, run) for kind in ("room", "fog_density", "fog_relative_height")]
                   for attempt, run in runs.items()}
    for row in measured_rows(source):
        if row.get("state") != "InLevel":
            continue
        for item in comparisons.get(row.get("attempt", "unavailable"), ()):
            selected = item.classify(row)
            if selected is None:
                item.excluded += int(number(row, "samples", 1))
            else:
                side, key = selected
                item.groups[side].add(key)
                if len(item.groups[side]) > MAX_CONTEXTS:
                    raise ValueError("too many scene contexts; split the log into sessions")
    for items in comparisons.values():
        for item in items:
            item.common = item.groups[0] & item.groups[1]
            item.groups = None
    for row in measured_rows(source):
        if row.get("state") != "InLevel":
            continue
        for item in comparisons.get(row.get("attempt", "unavailable"), ()):
            selected = item.classify(row)
            if selected is not None and selected[1] in item.common:
                (item.target if selected[0] else item.baseline).add(row)
    return comparisons


class Analysis:
    def __init__(self, rows):
        self.states, self.runs, self.sessions, self.points, self.schemas = {}, {}, [], {}, set()
        self.windows, self.slowest, self.drops, self.history_evicted = MarkerWindows(), [], 0, 0
        self.ended, self.serial = False, 0
        for row in rows:
            kind, schema = row["type"], row.get("measurement_schema")
            if kind == "session":
                if len(self.sessions) >= 64:
                    raise ValueError("too many sessions; split the input log")
                self.sessions.append(row)
            if schema in SCHEMAS:
                self.schemas.add(schema)
            self.drops = max(self.drops, int(number(row, "dropped_frames", number(row, "dropped_frames_before"))))
            self.history_evicted = max(self.history_evicted, int(number(row, "history_evicted_frames")))
            self.ended |= kind == "session_end"
            if kind == "timing_point":
                if len(self.points) >= 64 and row.get("point") not in self.points:
                    raise ValueError("too many timing points")
                self.points[row.get("point")] = row
            sample = kind == "frame_sample" and schema in SCHEMAS and valid_frame(row)
            summary = kind == "frame_summary" and schema == SCHEMAS[1]
            partition = kind in ("frame_partition", "memory_summary") and schema == SCHEMAS[1]
            if sample and row.get("state") == "InLevel":
                self.windows.add(row)
                self.serial += 1
                heapq.heappush(self.slowest, (number(row, "frame_ms"), self.serial, row))
                if len(self.slowest) > 8:
                    heapq.heappop(self.slowest)
            if (sample and schema == SCHEMAS[0]) or summary or partition or (kind == "hitch" and SCHEMAS[1] not in self.schemas):
                for group in self.groups(row):
                    if sample or summary:
                        group.add(row)
                    elif kind == "frame_partition":
                        group.partition(row)
                    elif kind == "memory_summary":
                        for key in ("il2cpp_used_bytes", "il2cpp_heap_bytes"):
                            group.metric(key).merge(row, key)
                    elif kind == "hitch":
                        group.thresholds["hitch_frames"] += 1
        self.windows.finish()
        self.malformed = getattr(rows, "malformed", 0)
        self.contexts = contexts(rows, self.runs) if self.runs else {}

    def groups(self, row):
        state = row.get("state", "unavailable")
        if state not in self.states:
            if len(self.states) >= 64:
                raise ValueError("too many game states")
            self.states[state] = Measurements()
        yield self.states[state]
        if state == "InLevel":
            attempt = row.get("attempt", "unavailable")
            if attempt not in self.runs:
                if len(self.runs) >= MAX_RUNS:
                    raise ValueError("too many level attempts; split the log")
                self.runs[attempt] = Measurements()
            yield self.runs[attempt]

    def frame_stats(self):
        def values(group):
            frame = group.metric("frame_ms")
            return dict(frames=frame.count, total_ms=frame.total, average_fps=1000 * frame.count / frame.total if frame.total else None,
                        percentiles_ms={str(q): frame.quantile(q) for q in (.5, .95, .99, .999)}, maximum_ms=frame.maximum if frame.count else None,
                        thresholds=dict(group.thresholds), complete_frames=group.complete)
        return {"states": {key: values(group) for key, group in self.states.items()}, "runs": {key: values(group) for key, group in self.runs.items()}}

    def report(self):
        if not self.schemas:
            raise ValueError(f"not a current performance log ({', '.join(SCHEMAS)}); record it again with the current ForgeDevelopment")
        versions = sorted({r["plugin_version"] for r in self.sessions if "plugin_version" in r})
        lines = ["# 原版 GTFO 性能分析", "", f"插件版本：{', '.join(versions) or 'unavailable'}。格式：{', '.join(sorted(self.schemas))}；无效／截断行：{self.malformed}。", "",
                 "流式合并直方图后计算整局分位数。v1 原始帧时间保留 0.001 ms 精度（≤2048 ms）。v2 与分项的上界桶误差：≤1 ms 为 0.01 ms，≤128 ms 为 0.1 ms，≤2048 ms 为 1 ms；更大值按二倍上界。采样／回调开销再除以 100。0 有独立桶。平均值、最大值和阈值计数保留实测精度；缺失值不填 0。", "",
                 "主线程 CPU 来自 session 指明的线程时钟；校准周期计数是执行时间估计，须核对实测分辨率与校准状态。呈现等待是指定图形入口墙钟扣除 CPU，包含入口内的调度等待。等待不等于 GPU 执行，其他时间含调度、其他等待及未覆盖工作。", "",
                 "| 局次 | 帧数 | 平均 FPS | 帧时间 P50/P95/P99/P99.9 ms | 最大帧 ms | 完整分解 |", "|---|---:|---:|---|---:|---:|"]
        for attempt, run in self.runs.items():
            frame = run.metric("frame_ms")
            q = "/".join(display(frame.quantile(f)) for f in (.5, .95, .99, .999))
            lines.append(f"| {attempt} | {frame.count} | {display(1000 * frame.count / frame.total if frame.total else None, 1)} | {q} | {display(frame.maximum)} | {run.complete}/{frame.count} |")
        lines += ["", "## 按游戏状态统计", "", "| 状态 | 帧数 | 平均 FPS | P50/P95/P99/P99.9 ms | ≥卡顿阈值／≥33／≥50／≥100 ms |", "|---|---:|---:|---|---|"]
        for session in self.sessions:
            if session.get("hitch_rule") == "max_floor_and_previous_frame_median;state_change_resets_history;current_frame_excluded":
                lines[-2:-2] = [f"卡顿按每帧动态阈值计数：max({display(observed(session, 'hitch_floor_ms'))} ms，此前最多 {session.get('hitch_history_frames', 'unavailable')} 个有效帧中位数 × {display(observed(session, 'hitch_median_multiplier'))})；当前帧不参与阈值，游戏状态变化重置。标记窗口的完整帧仍保留。", ""]
        for state, group in self.states.items():
            frame = group.metric("frame_ms")
            q = "/".join(display(frame.quantile(f)) for f in (.5, .95, .99, .999))
            counts = "/".join(str(group.thresholds[k]) for k in ("hitch_frames", "ge33_frames", "ge50_frames", "ge100_frames"))
            lines.append(f"| {state} | {group.count} | {display(1000 * group.count / frame.total if frame.total else None, 1)} | {q} | {counts} |")
        for heading, fields in (("每局 CPU 主线程／等待 GPU 或呈现／其他", CLOCKS), ("原版分项计时", PHASES)):
            lines += ["", f"## {heading}", "", "分项是包含内部工作的墙钟时间，脚本含敌人／刷怪，渲染提交可能含呈现等待；不可相加。新增阶段合计只覆盖已观测的叶函数工作，不含父阶段派发；预更新排除已有寻路点，后期更新排除已有渲染器与渲染提交点，其余包含画布、粒子和音频，也可能含等待。", "",
                      "| 局次 | 分项 | 有效帧数 | P50 ms | P95 ms | P99 ms | 平均 ms | 最大 ms |", "|---|---|---:|---:|---:|---:|---:|---:|---:|"]
            for attempt, run in self.runs.items():
                for name, key in fields:
                    metric = run.metric(key)
                    q = " | ".join(display(metric.quantile(f)) for f in (.5, .95, .99))
                    lines.append(f"| {attempt} | {name} | {metric.count} | {q} | {display(metric.mean)} | {display(metric.maximum)} |")
            if fields == CLOCKS:
                lines += ["", *(f"第 {attempt} 局：{side_decision(run)}。" for attempt, run in self.runs.items())]
        for point in self.points.values():
            if point.get("status") == "unavailable":
                lines.append(f"- {point.get('point')} unavailable：{point.get('reason', 'missing_reason')}。")
            elif point.get("coverage") == "partial_leaf_work":
                lines.append(f"- {point.get('point')} 仅覆盖部分叶函数：{point.get('missing_entries', 'missing_reason')}；此合计不能代表整个阶段。")
        lines += ["", "## 刷怪潮／警报前后", "", "以真实刷怪、警报或 F9 标记比较前后各 3 秒；调用不证明刷怪成功。相隔不足 3 秒的标记合并，列出 P95 增量最大的 8 个。"]
        if not self.windows.best:
            lines.append("unavailable：没有有效的刷怪／警报标记与前后逐帧样本。")
        for _, _, attempt, row, before, after in sorted(self.windows.best, reverse=True):
            comparison(lines, f"第 {attempt} 局 {row['time']}（marks={row.get('marks')}）", before, after)
        lines += ["", "## 大房间与雾", "", "仅比较共有房间／雾／攻击敌人数分组。v2 以整秒汇总加权：跨房间、雾阈值或敌人数分组的窗口排除，不把最后一个场景值套到全部帧。密度／高度按桶求四分位；分组不能消除视角、灯光、路径和战斗差异。"]
        for attempt, items in self.contexts.items():
            for item in items:
                if item.kind == "room":
                    label = "大房间（匹配上下文）"
                else:
                    name = "雾密度" if item.kind == "fog_density" else "相对雾平面高度"
                    if item.low is None:
                        lines += ["", f"第 {attempt} 局{name}比较 unavailable：字段缺失或没有不同值的样本。"]
                        continue
                    direction = f"{item.high:g} → {item.low:g}" if item.kind == "fog_relative_height" else f"{item.low:g} → {item.high:g}"
                    label = f"{name} {direction}（匹配上下文）"
                comparison(lines, f"第 {attempt} 局 {label}", item.baseline, item.target)
                lines.append(f"上下文不完整、混合或处于阈值之间而排除：{item.excluded} 帧；其他无共有分组的帧也不参与比较。")
                if item.kind == "fog_relative_height":
                    lines.append("高度只由原版玩家 Y 减雾设置高度得到；没有光线穿雾长度或着色器耗时证据，不能把低处直接宣称为 GPU 雾负载。")
        lines += ["", "## 卡顿与采集自身", "", "以下列出已保留详细帧中最慢的 8 帧；新格式的普通帧不保留身份，整局最大帧时间见上方汇总。", "",
                  "| 时间 | 帧号 | 帧 ms | 该帧最大已测分项 |", "|---|---:|---:|---|"]
        for wall, _, row in sorted(self.slowest, reverse=True):
            points = [(observed(row, key), name) for name, key in PHASES if observed(row, key) is not None]
            biggest = max(points) if points else None
            detail = f"{biggest[1]} {biggest[0]:.3f} ms" if biggest else "unavailable"
            lines.append(f"| {row['time']} | {row.get('frame', 'unavailable')} | {wall:.3f} | {detail} |")
        for attempt, run in self.runs.items():
            for key, label in (("collector_previous_frame_ms", "采样主体（前一帧）"), ("observer_ms", "回调内计时开销")):
                metric = run.metric(key)
                lines.append(f"- 第 {attempt} 局{label}：平均 {display(metric.mean, 6)} ms；P95 {display(metric.quantile(.95), 6)} ms。不含全部原生／托管边界成本。")
            allocated, used = run.metric("main_managed_allocated_bytes"), run.metric("il2cpp_used_bytes")
            if allocated.count:
                lines.append(f"- 第 {attempt} 局游戏主线程 .NET 分配：累计 {allocated.total:.0f} B，最大单帧 {allocated.maximum:.0f} B。含其他代码；不代表 IL2CPP 堆分配。累计分配不是存活内存或泄漏量；此计数器没有调用栈，不能按插件归属。")
            if used.count:
                lines.append(f"- 第 {attempt} 局原版 IL2CPP 已用堆：{display(used.first / 1048576 if used.first is not None else None, 2)} → {display(used.last / 1048576 if used.last is not None else None, 2)} MiB，峰值 {used.maximum / 1048576:.2f} MiB；不提供自动 GC 停顿。")
            lines.append(f"- 第 {attempt} 局标记帧数量（波次开始／刷怪组／波次结束／警报开始／警报结束／F9）：" + "/".join(str(run.marks[k]) for k in (1, 2, 4, 8, 16, 32)) + "。同一帧可含多个标记，刷怪组数可大于标记帧数。")
            lines.append(f"- 第 {attempt} 局入口调用：刷怪组 {run.events['spawn_groups']} 次，波次开始 {run.events['wave_starts']} 次。")
            if run.events['wave_starts'] == 0:
                lines.append(f"- 第 {attempt} 局未观察到本机波次开始；不能据此断言没有怪潮。主机专用入口、已有补丁与网络覆盖须分别核对。")
        lines += [f"- 完整帧队列丢弃：{self.drops}；被丢弃帧的分项与标记同样缺失。", f"- 标记历史容量提前淘汰：写入器 {self.history_evicted} 帧，分析器 {self.windows.evicted} 帧；窗口可能不完整。",
                  "- .NET GC0/1/2 计数与原版 IL2CPP 垃圾回收分开，不能将 .NET 次数当作原版 GC 次数。",
                  "- 完整自动／增量 GC 耗时、GPU 执行、渲染线程帧耗时、活跃刚体、布娃娃和可见渲染器：unavailable；session 记录原因。显式 GC 是子集，0 不能证明没有其他 GC。"]
        for session in self.sessions:
            for key in ("scene_fields", "game_entries", "present_wait", "native_memory", "manual_key", "cpu_source", "cpu_clock_status",
                        "cpu_cycles_per_second", "cpu_resolution_ns", "cpu_calibration_relative_spread", "present_wait_source", "marker_entries", "marker_unavailable", "wave_marker_coverage", "phase_alignment", "observer_cost_coverage"):
                lines.append(f"- {key}：{session.get(key, 'unavailable')}。")
            if "GetThreadTimes" in session.get("cpu_source", ""):
                lines.append("- 此日志使用 GetThreadTimes，未提供实测 CPU 时钟分辨率；100 ns 是计数单位，不等于采样精度，不能据此可靠分解短帧。")
            lines.append(f"- 垂直同步／帧率上限：{session.get('vsync', 'unavailable')}／{session.get('target_fps', 'unavailable')}；等待时间不能自动证明 GPU 算力不足。")
        if not self.ended:
            lines.append("- 缺少 session_end：日志可能仍在写入或退出未完成。")
        if not self.runs:
            lines.append("没有有效关卡样本，无法判断帧率或瓶颈。")
        return "\n".join(lines) + "\n"


def report(rows, malformed=0):
    result = Analysis(rows)
    result.malformed += malformed
    return result.report()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = report(*read_log(args.log))
    if args.output:
        args.output.write_text(result, encoding="utf-8")
    else:
        print(result)


if __name__ == "__main__":
    main()
