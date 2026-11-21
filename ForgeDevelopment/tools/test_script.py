"""The console's test script: `*.test` text, one command per line, parsed and judged without the game.

    # comment                          a whole line, or the end of a line
    level 1 1 1                        a command, written as on the command line of drive_cli.py
    spawn Striker 1 at=front:8 as=s1   key=value options, --flags, and as=NAME keeps the answer as $NAME
    expect enemy $s1.id health < $s1.health
    wait 1s | wait 30f | wait until state ammo clip == 30 timeout=5s
    stop-on-fail                       from this line on, the first failure ends the run

A syntax error rejects the whole file before the game is touched and names the line. `expect` and `wait until` read a value
(`player Position.y`, `state ammo clip`, `enemy <id|$name> health`, `read <target> <path>`, `$name.path`) and compare it
(`== != < <= > >=`, `~0.05` with a tolerance, `contains`, `!contains`); `expect log contains "text"` looks at the log lines written since the
previous command started (those written after it returned included), `--all` at the whole session. Executing the lines is console_commands.py; this file holds the grammar and the
comparison, so a broken script and a wrong expectation can both be checked offline."""
from __future__ import annotations

from dataclasses import dataclass, field
import hashlib
from pathlib import Path
import re

TOKEN = re.compile(r'"(?:[^"\\]|\\.)*"|\S+')
COMPARATORS = ("==", "!=", "<=", ">=", "<", ">", "contains", "!contains")
TOLERANCE = re.compile(r"~([0-9]+(?:\.[0-9]+)?)$")
DURATION = re.compile(r"([0-9]+(?:\.[0-9]+)?)(s|f)$")
NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]*$")
READERS = ("player", "state", "enemy", "enemies", "alarms", "read", "log")
STATES = ("ammo", "stamina", "fog", "intel", "hud", "lifecycle", "variables")

# verb -> (fewest positionals, most positionals or None, option keys, flags). Every command of drive_cli.py's console is here.
VERBS = {
    "status": (0, 0, (), ()), "level": (0, 3, ("seed", "timeout", "bots"), ()), "leave": (0, 0, (), ()), "reenter": (0, 0, ("timeout",), ()),
    "quit": (0, 0, (), ()),
    "teleport": (0, 4, ("dim", "face", "zone", "area", "exit", "offset", "room"), ()), "look": (1, 3, (), ("--ads",)), "aim": (1, 3, (), ("--ads",)),
    "fire": (0, 1, ("at",), ("--direct",)), "melee": (0, 0, ("charge", "at"), ("--direct",)),
    "wield": (1, 1, (), ()), "reload": (0, 0, (), ("--direct",)), "throw": (0, 0, ("charge", "at"), ("--direct",)),
    "move": (1, 5, (), ()), "jump": (0, 0, (), ("--direct",)), "crouch": (0, 0, (), ("--direct",)), "stand": (0, 0, (), ("--direct",)),
    "interact": (0, 3, ("hold", "at", "name", "radius"), ()), "press": (1, 2, (), ()), "hold": (1, 2, (), ()), "release": (0, 1, (), ()), "key": (1, 2, (), ()),
    "god": (1, 1, (), ()), "give": (1, 2, (), ()), "ammo": (1, 1, (), ()), "down": (0, 0, (), ()), "revive": (0, 0, (), ()),
    "spawn": (1, 2, ("at", "mode"), ()), "enemies": (0, 1, (), ()), "clear": (1, 1, (), ()), "hit": (1, None, (), ()), "limbs": (1, None, (), ()),
    "follow": (1, None, (), ()), "door": (2, 2, (), ()), "noise": (1, 1, (), ()), "event": (1, 1, (), ()), "checkpoint": (1, 1, (), ()),
    "scan": (1, 2, (), ()), "extract": (1, 2, (), ()), "nav": (2, 2, (), ()), "model-shot": (1, 1, (), ()), "alarms": (0, 1, (), ()),
    "recorder": (0, 0, (), ()), "terminal": (1, 2, (), ()), "toon": (1, 2, (), ()), "picker": (2, 2, (), ()),
    "player": (0, None, (), ()), "read": (2, None, (), ()), "state": (1, 2, (), ()), "log": (0, 0, ("tail", "grep"), ()),
    "screenshot": (0, 1, (), ()), "call": (2, None, (), ()), "step": (1, 1, ("target", "host"), ()),
}
CONTROL = ("wait", "expect", "stop-on-fail")


class ScriptError(ValueError):
    def __init__(self, line, message):
        super().__init__("第 " + str(line) + " 行：" + message)
        self.line, self.message = line, message


class Tok(str):
    """A token; `quoted` marks text written in double quotes, which is never an option, a flag or a comparator."""
    quoted = False


@dataclass
class Condition:
    reader: list
    comparator: str
    tolerance: float | None
    value: str


@dataclass
class Line:
    number: int
    text: str
    kind: str  # command | expect | wait | stop-on-fail
    verb: str = ""
    args: list = field(default_factory=list)
    options: dict = field(default_factory=dict)
    flags: tuple = ()
    name: str = ""
    condition: Condition | None = None
    amount: tuple | None = None  # wait <n>s|<n>f, as (number, unit)
    timeout: float = 10.0
    every: bool = False  # expect log ... --all


@dataclass
class Script:
    lines: list
    sha256: str = ""
    path: str = ""


def tokens(number, text):
    out = []
    for raw in TOKEN.findall(text):
        if raw.startswith("#"):
            break
        token = Tok(raw[1:-1].replace('\\"', '"') if raw.startswith('"') and raw.endswith('"') and len(raw) >= 2 else raw)
        token.quoted = raw.startswith('"')
        out.append(token)
    return out


def split_condition(number, parts, what):
    for index, part in enumerate(parts):
        if index == 0 or part.quoted:
            continue
        tolerance = TOLERANCE.match(part)
        if part in COMPARATORS or tolerance:
            reader, value = parts[:index], parts[index + 1:]
            if not reader or len(value) != 1:
                raise ScriptError(number, what + " 需要“读取 比较符 值”三段，值只有一个（含空格的值请加双引号）")
            if reader[0] not in READERS and not reader[0].startswith("$"):
                raise ScriptError(number, what + " 的读取只能是 " + "、".join(READERS) + " 或 $变量.路径，不是 " + reader[0])
            if reader[0] == "state" and (len(reader) < 3 or reader[1] not in STATES):
                raise ScriptError(number, "state 读取需要 state <" + "|".join(STATES) + "> <字段>")
            if reader[0] == "log" and part not in ("contains", "!contains"):
                raise ScriptError(number, "log 只能用 contains 或 !contains 判断")
            return Condition(list(reader), "~" if tolerance else str(part), float(tolerance.group(1)) if tolerance else None, str(value[0]))
    raise ScriptError(number, what + " 缺少比较符（" + " ".join(COMPARATORS) + " ~容差）")


def parse_line(number, text, defined):
    parts = tokens(number, text)
    if not parts:
        return None
    verb = str(parts[0])
    if verb == "stop-on-fail":
        if len(parts) != 1:
            raise ScriptError(number, "stop-on-fail 不带参数")
        return Line(number, text.strip(), "stop-on-fail", verb)
    if verb == "wait":
        rest = parts[1:]
        if rest and rest[0] == "until":
            timeout, body = 10.0, []
            for part in rest[1:]:
                match = re.fullmatch(r"timeout=([0-9]+(?:\.[0-9]+)?)s", str(part)) if not part.quoted else None
                if match:
                    timeout = float(match.group(1))
                else:
                    body.append(part)
            line = Line(number, text.strip(), "wait", verb, condition=split_condition(number, body, "wait until"), timeout=timeout)
        else:
            match = DURATION.match(str(rest[0])) if len(rest) == 1 else None
            if not match:
                raise ScriptError(number, "wait 需要 <n>s、<n>f 或 until <读取> <比较符> <值> timeout=<n>s")
            line = Line(number, text.strip(), "wait", verb, amount=(float(match.group(1)), match.group(2)))
        check_variables(number, line, defined)
        return line
    if verb == "expect":
        every = any(part == "--all" and not part.quoted for part in parts)
        body = [part for part in parts[1:] if not (part == "--all" and not part.quoted)]
        line = Line(number, text.strip(), "expect", verb, condition=split_condition(number, body, "expect"), every=every)
        check_variables(number, line, defined)
        return line
    if verb not in VERBS:
        raise ScriptError(number, "未知指令 " + verb)
    fewest, most, allowed, allowed_flags = VERBS[verb]
    args, options, flags, name = [], {}, [], ""
    for part in parts[1:]:
        if part.quoted:
            args.append(str(part))
        elif part.startswith("--"):
            if part not in allowed_flags:
                raise ScriptError(number, verb + " 不接受 " + part)
            flags.append(str(part))
        elif "=" in part and re.match(r"[A-Za-z_][A-Za-z0-9_-]*=", part):
            key, value = part.split("=", 1)
            if key == "as":
                if not NAME.match(value):
                    raise ScriptError(number, "as= 的变量名只能用字母、数字和下划线")
                name = value
            elif key not in allowed:
                raise ScriptError(number, verb + " 不接受 " + key + "=")
            else:
                options[key] = value
        else:
            args.append(str(part))
    if len(args) < fewest or most is not None and len(args) > most:
        raise ScriptError(number, verb + " 的参数个数不对（" + str(len(args)) + "）")
    line = Line(number, text.strip(), "command", verb, args, options, tuple(flags), name)
    check_variables(number, line, defined)
    return line


def variables_in(line):
    texts = list(line.args) + list(line.options.values())
    if line.condition:
        texts += line.condition.reader + [line.condition.value]
    return [match for text in texts for match in re.findall(r"\$([A-Za-z_][A-Za-z0-9_]*)", text)]


def check_variables(number, line, defined):
    for name in variables_in(line):
        if name not in defined:
            raise ScriptError(number, "变量 $" + name + " 在这一行之前没有用 as= 定义")


def parse(text, path=""):
    lines, defined = [], set()
    for number, raw in enumerate(text.splitlines(), 1):
        line = parse_line(number, raw, defined)
        if line is None:
            continue
        lines.append(line)
        if line.name:
            defined.add(line.name)
    return Script(lines, hashlib.sha256(text.encode("utf-8")).hexdigest(), path)


def parse_file(path):
    return parse(Path(path).read_text("utf-8"), str(path))


# ---- comparison -------------------------------------------------------------------------------------------------------

def number(value):
    try:
        return float(str(value))
    except ValueError:
        return None


def judge(actual, comparator, tolerance, expected):
    """True when `actual` stands in the relation to `expected`. Numbers compare as numbers, everything else as text; a missing actual
    (None) fails every relation, negated ones included: a value that could not be read never counts as a pass."""
    if actual is None:
        return False
    if comparator in ("contains", "!contains"):
        found = str(expected) in str(actual)
        return found if comparator == "contains" else not found
    left, right = number(actual), number(expected)
    if comparator == "~":
        return left is not None and right is not None and abs(left - right) <= tolerance
    if left is None or right is None:
        left, right = str(actual).lower(), str(expected).lower()
        if comparator in ("==", "!="):
            return (left == right) == (comparator == "==")
        return False
    return {"==": left == right, "!=": left != right, "<": left < right, "<=": left <= right, ">": left > right, ">=": left >= right}[comparator]


def lookup(values, path):
    """`path` in a result's key=value dict: the key itself, or a component .x .y .z of a comma-separated vector value."""
    if path in values:
        return values[path]
    base, _, part = path.rpartition(".")
    if base in values and part in ("x", "y", "z"):
        pieces = str(values[base]).split(",")
        if len(pieces) == 3:
            return pieces["xyz".index(part)]
    return None


def expand(text, variables):
    """`$name.path` becomes the kept value; a text with no $ is returned unchanged."""
    def one(match):
        found = lookup(variables.get(match.group(1), {}), match.group(2) or "")
        if found is None:
            raise KeyError(match.group(0))
        return str(found)
    return re.sub(r"\$([A-Za-z_][A-Za-z0-9_]*)(?:\.([A-Za-z0-9_.\[\]]+))?", one, text)
