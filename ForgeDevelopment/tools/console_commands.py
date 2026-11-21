"""The console's commands: each parsed line of test_script.py becomes one ExperimentRunner command on the running Authoring session.

It uses the session client of drive_session.py (one status, call, read and command channel); the game side is the existing drive
entries plus ForgeDevelopment.Native.PlayerActions. A player's fight and movement press the game's own input (HeldInputLease, a fresh
press edge held for a few frames) unless the line carries --direct, which calls the native method instead. An input is held and released
inside one experiment command, because the lease ends with the command that took it. Every command answers a Result whose `values` are
the key=value facts the game reported; `as=NAME` keeps them for `$NAME.key` in later lines."""
from __future__ import annotations

from dataclasses import dataclass, field
import json
from pathlib import Path
import re
import time

import drive_session as ds
import test_script as ts

NATIVE = "static:ForgeDevelopment.Native."
INPUT, ACTIONS, FIRE, STATE, TERMINAL = "GameInputDrive", "PlayerActions", "WeaponFireProbe", "StateReadbackDrive", "TerminalDrive"
TERMINAL_SETTLE = 1.5  # seconds a typed command gets to print before the screen is read (the game prints with per-line delays)
SLOTS = {"primary": "GearStandard", "secondary": "GearSpecial", "tool": "GearClass", "melee": "GearMelee", "pack": "ResourcePack",
         "consumable": "Consumable"}
PLAYER_PATHS = ["Alive", "Position", "DimensionIndex", "Damage.Health"]
FRAMES_PRESS, FRAMES_GAP = 3, 10  # one trigger pull: the button down for three frames, then ten frames before the next
WALK_SECONDS = 0.5
WALK_SPEED = 1.6  # metres per second, the game's walk
REACHED = 0.6
# The game selects only interactions whose collider lies in a sphere around the camera (PlayerInteraction.FixedUpdate:
# OverlapSphereNonAlloc at the camera with m_searchRadius 1.2 m, plus up to 0.5 m while looking up or down), then the one nearest the
# screen centre. So the eye walks to within REACH of the interaction's point in three dimensions, short of 1.2 m so the collider is
# inside the sphere wherever its surface lies; a mod claim (2.4 m from the eye) is inside that too.
REACH = 1.0
EYE_HEIGHT = 1.5  # metres above the player's feet: `move to` looks at the goal's x/z at this height
# A found interaction comes with the place to stand (PlayerActions.StandFor: walkable, eye within reach of its collider, and the look
# at it meets it first, so a terminal is used from its front); the player walks onto that place this closely.
STAND_REACHED = 0.35
# After the look the game settles its selection over a few frames (the camera turns, the sphere is refilled at the next physics step);
# the console reads it until it holds, for at most this many seconds.
SELECT_SECONDS = 3
POINT_RADIUS = 2.0  # `interact x y z` takes the game's interaction nearest the point within this many metres


@dataclass
class Result:
    ok: bool
    values: dict = field(default_factory=dict)
    text: str = ""
    error: str = ""


def call(cls, method, *args):
    return {"target": NATIVE + cls, "call": method, "args": list(args)}


def drive(operation, *args):
    return {"target": ds.DRIVE, "call": operation, "args": list(args)}


def pause(seconds):
    return {"wait": {"seconds": seconds}}


def frames(count):
    return {"wait": {"frames": count}}


def pairs(text):
    """`a=1; b=two` as a dict; a text without `=` is kept whole under `text`."""
    values = {}
    for part in str(text).split("; "):
        key, sep, value = part.partition("=")
        if sep and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.\[\]]*", key.strip()):
            values[key.strip()] = value
    return values or {"text": str(text)}


def summary(receipt):
    """The bounded `summary` a drive receipt carries (a teleport's landing, the variables read), or {} when it has none."""
    try:
        return json.loads(receipt).get("summary") or {}
    except (ValueError, AttributeError):
        return {}


def number(text, default):
    try:
        return float(text)
    except (TypeError, ValueError):
        return default


def duration(text, default):
    """`2s` or `0.5` seconds."""
    return number(str(text).rstrip("s"), default) if text is not None else default


class LogTail:
    """The BepInEx log of the running game: what a command added to it, and the lines before it for context."""
    def __init__(self, path):
        self.path = Path(path)

    def mark(self):
        try:
            return self.path.stat().st_size
        except OSError:
            return 0

    def lines(self, start, end=None):
        """The lines added since `start`, or None when the log cannot be read or has become shorter than `start` (rewritten, for
        example by a restart): lines that cannot be read are never reported as no lines."""
        try:
            with self.path.open("rb") as stream:
                if self.path.stat().st_size < start:
                    return None
                stream.seek(start)
                data = stream.read() if end is None else stream.read(max(0, end - start))
        except OSError:
            return None
        return data.decode("utf-8", "replace").splitlines()

    def before(self, start, count=20):
        try:
            with self.path.open("rb") as stream:
                stream.seek(max(0, start - 64 * 1024))
                data = stream.read(min(start, 64 * 1024))
        except OSError:
            return []
        return data.decode("utf-8", "replace").splitlines()[-count:]


class Console:
    """Runs parsed lines against a session. `native` maps a verb to a function (line) -> dict for the verbs drive_cli.py already
    implements (status, call, read, step, log, screenshot, quit)."""
    def __init__(self, client, state, native=None):
        self.client, self.state = client, state
        self.native = native or {}
        self.level_choice = dict(state.get("selection") or {})
        self.log = LogTail(Path(state["bepinexRoot"]) / "LogOutput.log")
        self.handlers = {
            "level": self.level, "leave": self.leave, "reenter": self.reenter, "teleport": self.teleport, "look": self.look, "aim": self.look,
            "fire": self.fire, "melee": self.melee, "wield": self.wield, "reload": self.reload, "throw": self.throw, "move": self.move,
            "jump": self.posture, "crouch": self.posture, "stand": self.posture, "interact": self.interact, "press": self.press,
            "hold": self.press, "release": self.release, "key": self.key, "god": self.god, "give": self.give, "ammo": self.ammo,
            "down": self.plain, "revive": self.plain, "spawn": self.spawn, "enemies": self.enemies, "clear": self.clear,
            "hit": self.enemy_probe, "limbs": self.enemy_probe, "follow": self.enemy_probe, "door": self.door, "noise": self.noise,
            "event": self.event, "checkpoint": self.checkpoint, "scan": self.scan, "extract": self.extract, "nav": self.nav,
            "model-shot": self.model_shot, "alarms": self.alarms, "recorder": self.recorder, "player": self.player, "read": self.read,
            "state": self.read_state, "terminal": self.terminal, "toon": self.toon, "picker": self.picker,
        }

    # ---- running a line ----------------------------------------------------------------------------------------------

    def execute(self, line):
        handler = self.native.get(line.verb)
        try:
            if handler is not None:
                return Result(True, {"result": handler(line)})
            return self.handlers[line.verb](line)
        except (ds.ExperimentFailed, OSError, ValueError, RuntimeError, TimeoutError, KeyError, IndexError) as error:
            self.client.deadline = None
            self.recover()
            return Result(False, error=str(error) or type(error).__name__)
        finally:
            # A native verb bounds its own call with an absolute deadline (drive_cli.bounded). Left on the client it would cap every
            # later line of the script and fail them as submit-ready-timeout or 实验超时 half a minute after that call.
            self.client.deadline = None

    def recover(self):
        """A failed command may have left an input held; give every driven input back so the player is not left firing."""
        try:
            self.client.command(ds.DRIVE, [call(INPUT, "LetAll")], timeout=30)
        except (ds.ExperimentFailed, OSError, ValueError, RuntimeError, TimeoutError):
            pass

    def run(self, steps, timeout=180):
        """One experiment command; the values its call and read steps answered, in order."""
        _, rows = self.client.command(ds.DRIVE, steps, timeout=timeout)
        values = []
        for row in rows:
            body = row.get("body", {})
            if row.get("kind") != "step" or body.get("step") not in ("call", "read"):
                continue
            detail = body.get("detail", "")
            values.append(detail.split(" -> ", 1)[1] if body["step"] == "call" and " -> " in detail
                          else detail.split(" :: ", 1)[1] if body["step"] == "read" and " :: " in detail else detail)
        return values

    @staticmethod
    def find(values, prefix):
        return next((value for value in reversed(values) if value.startswith(prefix)), "")

    def position(self):
        return pairs(self.client.read("localPlayer", ["Position", "DimensionIndex", "Alive"]))

    # ---- session and level -------------------------------------------------------------------------------------------

    def level(self, line):
        timeout = duration(line.options.get("timeout"), 600)
        if "seed" in line.options:
            if len(line.args) != 1:
                raise ValueError("level seed=build,host,session 需要一个关卡标号，例如 level 37-B1 seed=1,2,3")
            self.client.echo = True
            self.client.call(NATIVE + "SeededDrop", "Start", [line.args[0], line.options["seed"], int(line.options.get("bots", 3))])
            ds.wait_until(self.client, lambda: self.client.status()["gameState"] == "InLevel" and self.client.call(ds.MENU, "LandingReady") == "true",
                          timeout, "带种子落地", lambda: self.client.call(NATIVE + "SeededDrop", "State"))
            return Result(True, {"world": self.client.status()["worldEpoch"], "level": line.args[0]})
        if len(line.args) == 3:
            self.level_choice = {"rundown": int(line.args[0]), "tier": int(line.args[1]), "ordinal": int(line.args[2]),
                              "botSlots": line.options.get("bots", "[]")}
        if not {"rundown", "tier", "ordinal"} <= set(self.level_choice):
            raise ValueError("level 需要 <rundown> <tier> <ordinal>（会话里没有可沿用的关卡）")
        self.client.echo = True
        ds.enter_level(self.client, self.level_choice["rundown"], self.level_choice["tier"], self.level_choice["ordinal"], timeout,
                       self.level_choice.get("botSlots", "[]"))
        return Result(True, {"world": self.client.status()["worldEpoch"], **{key: self.level_choice[key] for key in ("rundown", "tier", "ordinal")}})

    def leave(self, line):
        ds.return_to_lobby(self.client)
        return Result(True, {"state": self.client.status()["gameState"]})

    def reenter(self, line):
        return self.level(ts.Line(line.number, line.text, "command", "level", options=dict(line.options)))

    # ---- placing and aiming ------------------------------------------------------------------------------------------

    def teleport(self, line):
        opt, args = line.options, line.args
        spec = {"schemaVersion": "1.0.0"}
        if "zone" in opt:
            spec.update(zone=opt["zone"], area=int(opt.get("area", 0)))
        elif "exit" in opt:
            spec.update(exit=opt["exit"], offset=[number(part, 0) for part in opt.get("offset", "0,0,0").split(",")])
        elif "room" in opt:
            if len(args) != 3:
                raise ValueError("teleport room=<房间 id> 需要房间内局部坐标 x y z")
            at = self.run([call("RoomLookup", "WorldPoint", opt["room"], *[number(a, 0) for a in args])])[-1]
            dimension, point = at.split(";", 1)
            spec.update(dimension=int(dimension), position=[float(part) for part in point.split(",")])
        elif len(args) == 3:
            spec.update(dimension=int(opt.get("dim", 0)), position=[number(a, 0) for a in args])
        else:
            raise ValueError("teleport 需要 x y z、zone= area=、exit= offset= 或 room= x y z")
        steps = [drive("drive.player.teleport", json.dumps(spec, separators=(",", ":"))), pause(0.5)]
        if "face" in opt:
            steps.append(call(INPUT, "Look", *[number(part, 0) for part in opt["face"].split(",")]))
        landing = summary(self.run(steps)[0])
        return Result(True, {**self.position(), **{key: ",".join(f"{part:.3f}" for part in value) for key, value in landing.items()}})

    def look(self, line):
        args = line.args
        if len(args) == 3:
            steps = [call(INPUT, "Look", *[number(a, 0) for a in args])]
        elif len(args) == 1:
            steps = [call(ACTIONS, "Aim", args[0])]
        else:
            raise ValueError(line.verb + " 需要 x y z 或一个目标名")
        if "--ads" in line.flags:
            steps += [drive("drive.ads.begin"), pause(0.6), drive("drive.ads.sample", "aimed"), drive("drive.ads.end")]
        values = self.run(steps)
        return Result(True, {"look": values[0], **({"ads": "aimed"} if "--ads" in line.flags else {})})

    # ---- the player's fight ------------------------------------------------------------------------------------------

    @staticmethod
    def aim(line):
        return [call(ACTIONS, "Aim", line.options["at"])] if "at" in line.options else []

    def target_health(self, line):
        return [call(ACTIONS, "Enemy", line.options["at"])] if "at" in line.options else []

    def combat_values(self, values, line):
        report = self.find(values, "wielded ")
        match = re.search(r"clip before (-?\d+) after (-?\d+); BulletWeapon\.Fire (\d+) Shotgun\.Fire (\d+) BulletHit (\d+) melee hits (\d+)", report)
        out = {"wielded": report.split(";")[0].replace("wielded ", "", 1)}
        if match:
            out.update(zip(("clipBefore", "clipAfter", "fires", "shotgunFires", "hits", "meleeHits"), (int(part) for part in match.groups())))
        if "at" in line.options:
            health = [pairs(value) for value in values if value.startswith("id=")]
            if health:
                out["targetHealthBefore"], out["targetHealthAfter"] = health[0].get("health"), health[-1].get("health")
                out["targetAlive"] = health[-1].get("alive")
        return out

    def fire(self, line):
        spec = line.args[0] if line.args else "1"
        direct = "--direct" in line.flags
        steps = self.target_health(line) + [call(FIRE, "Arm")] + self.aim(line)
        if direct:
            steps += [call(FIRE, "FireDirect", str(int(number(spec.rstrip("s"), 1)))), frames(5)]
        elif spec.endswith("s"):
            steps += [call(INPUT, "Hold", "Fire"), pause(number(spec[:-1], 1)), call(INPUT, "Let", "Fire"), pause(0.3), call(INPUT, "LetAll")]
        else:
            steps += [{"repeat": {"times": int(number(spec, 1)), "steps": [call(INPUT, "Hold", "Fire"), frames(FRAMES_PRESS),
                                                                       call(INPUT, "Let", "Fire"), frames(FRAMES_GAP)]}},
                      pause(0.3), call(INPUT, "LetAll")]
        values = self.run(steps + [call(FIRE, "Report")] + self.target_health(line))
        return Result(True, self.combat_values(values, line))

    def melee(self, line):
        steps = [call(FIRE, "Wield", SLOTS["melee"]), pause(1.2)] + self.target_health(line) + [call(FIRE, "Arm")] + self.aim(line)
        charge = duration(line.options.get("charge"), 0.15)
        if "--direct" in line.flags:
            steps += [call(ACTIONS, "Swing"), pause(0.9)]
        else:
            steps += [call(INPUT, "Hold", "Fire"), pause(charge), call(INPUT, "Let", "Fire"), pause(0.9), call(INPUT, "LetAll")]
        return Result(True, self.combat_values(self.run(steps + [call(FIRE, "Report")] + self.target_health(line)), line))

    def wield(self, line):
        name = line.args[0]
        slot = SLOTS.get(name)
        if slot is None:
            values = self.run([call(FIRE, "Equip", name), pause(1.0), call(FIRE, "Slots")])
            return Result(True, {"wielded": re.sub(r";.*", "", self.find(values, "wielded ")).replace("wielded ", "", 1)})
        # A slot request the game does not take (an exosuit keeps its own gun in the hands) fails with the slot it holds instead.
        taken = pairs(self.run([call(FIRE, "Wield", slot), pause(1.0), call(FIRE, "WieldTaken", slot)])[-1])
        if taken.get("taken") != "true":
            return Result(False, taken, error="wield.not-taken: 要求 " + slot + "，手里仍是 " + taken.get("wieldedSlot", "?") + " " + taken.get("wielded", ""))
        return Result(True, taken)

    def reload(self, line):
        before = [call(STATE, "AmmoRead")]
        press = [call(ACTIONS, "Reload")] if "--direct" in line.flags else [call(INPUT, "Hold", "Reload"), pause(0.3), call(INPUT, "Let", "Reload"), call(INPUT, "LetAll")]
        values = self.run(before + press + [pause(0.5), call(STATE, "AmmoRead")])
        first, last = pairs(self.find(values[:1], "clip=")), pairs(self.find(values, "clip="))
        return Result(True, {"clipBefore": first.get("clip"), "clipAfter": last.get("clip"), "maxClip": last.get("maxClip")})

    def throw(self, line):
        charge = duration(line.options.get("charge"), 0.4)
        steps = [call(FIRE, "Wield", SLOTS["consumable"]), pause(1.2)] + self.aim(line) + [call(FIRE, "Slots")]
        if "--direct" in line.flags:
            steps += [call(ACTIONS, "Throw", False), pause(charge), call(ACTIONS, "Throw", True), pause(1.0)]
        else:
            steps += [call(INPUT, "Hold", "Fire"), pause(charge), call(INPUT, "Let", "Fire"), pause(1.0), call(INPUT, "LetAll")]
        values = self.run(steps + [call(FIRE, "Slots")])
        slots = [value for value in values if value.startswith("wielded ")]
        return Result(True, {"before": slots[0], "after": slots[-1]})

    def posture(self, line):
        verb = line.verb
        if "--direct" in line.flags:
            steps = [call(ACTIONS, "Posture", verb.capitalize())]
        elif verb == "jump":
            steps = [call(INPUT, "Hold", "Jump"), frames(FRAMES_PRESS), call(INPUT, "Let", "Jump"), call(INPUT, "LetAll")]
        else:
            # Crouch is one press that toggles: stand presses it only while crouched.
            now = self.client.read("localPlayer", ["Locomotion.m_currentStateEnum"])
            crouched = "Crouch" in now
            if (verb == "crouch") == crouched:
                return Result(True, {"locomotion": now.split("=", 1)[1]})
            steps = [call(INPUT, "Hold", "Crouch"), frames(FRAMES_PRESS), call(INPUT, "Let", "Crouch"), call(INPUT, "LetAll")]
        self.run(steps + [pause(0.4)])
        return Result(True, {"locomotion": self.client.read("localPlayer", ["Locomotion.m_currentStateEnum"]).split("=", 1)[1]})

    def move(self, line):
        args = line.args
        if args[0] == "to":
            return self.move_to([number(a, 0) for a in args[1:4]])
        axes = {"forward": ("MoveVertical", 1), "back": ("MoveVertical", -1), "left": ("MoveHorizontal", -1), "right": ("MoveHorizontal", 1)}
        if args[0] not in axes or len(args) < 2:
            raise ValueError("move 需要 forward|back|left|right <秒> [run] 或 to x y z")
        action, sign = axes[args[0]]
        before = self.position()
        steps = [call(INPUT, "Axis", action, sign)] + ([call(INPUT, "Hold", "Run")] if "run" in args[2:] else []) \
            + [pause(number(args[1].rstrip("s"), 1)), call(INPUT, "LetAll")]
        started = time.monotonic()
        self.run(steps)
        after = self.position()
        return Result(True, {"from": before.get("Position"), "to": after.get("Position"), "seconds": round(time.monotonic() - started, 2)})

    def move_to(self, goal, within=REACHED, flat=False):
        """Walks at a point by facing it and pressing forward in short bursts until within `within` metres (along the ground when
        `flat`) or 30 seconds."""
        before = self.position()
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            here = [float(part) for part in self.position()["Position"].split(",")]
            left = sum((a - b) ** 2 for index, (a, b) in enumerate(zip(here, goal)) if not (flat and index == 1)) ** .5
            if left <= within:
                return Result(True, {"from": before["Position"], "to": self.position()["Position"], "reached": "true"})
            # Near the goal a burst only covers what is left, so a narrow window (a point well above or below the eye) is not stepped over.
            burst = min(WALK_SECONDS, max(.1, (left - within) / WALK_SPEED))
            self.run([call(INPUT, "Look", goal[0], here[1] + EYE_HEIGHT, goal[2]), call(INPUT, "Axis", "MoveVertical", 1), pause(burst), call(INPUT, "LetAll")])
        return Result(False, {"from": before["Position"], "to": self.position()["Position"], "reached": "false"}, error="30 秒内没有走到 " + ",".join(map(str, goal)))

    def interact(self, line):
        """Presses Use where the player looks. With a point `x y z` or `name=` it first finds the target (PlayerActions.Interactable:
        `name=text` prefers an interaction whose owner type contains the text over one whose own names do, `name=text@Owner` asks
        for that owner type), takes the posture it needs (crouched only for a point too low for a standing eye, a terminal standing),
        presses at once when the game already selects it from where the player stands, else walks onto the place a player stands to
        use it (a terminal: in front of its screen), faces it and presses only once the game selects it: the game's own selection
        and Use input take it, so a door, terminal or pickup all work the same way. A point with no game interaction near it (a mod's
        claim prompt) is walked at directly and only reported pressed. A found target, or with no target anything at all, that the game
        does not hold selected is refused instead of pressed."""
        opt, hold, values = line.options, line.options.get("hold"), {}
        if not line.args and "name" not in opt:
            selected, settled = self.selection(self.aim(line), None)
            if not settled:
                return Result(False, {"selected": selected}, error="interact.nothing-selected: 等了 " + str(SELECT_SECONDS)
                              + " 秒，游戏没有稳定选中任何交互物（最后读到 " + selected + "），不按")
            self.run(self.use(duration(hold, 0.3)))
            return Result(True, {"selected": selected, "interacted": "true"})
        if len(line.args) not in (0, 3):
            raise ValueError("interact 需要 x y z 或 name=<名字>（可同时写，name 在该点附近找）")
        point = [number(a, 0) for a in line.args]
        find = [call(ACTIONS, "Interactable", opt.get("name", ""), ",".join(map(str, point)), number(opt.get("radius"), 30 if "name" in opt else POINT_RADIUS))]
        try:
            # The game's interaction at the point (any within POINT_RADIUS) or by name, with the place a player stands to use it.
            values = pairs(self.run(find)[0])
        except ds.ExperimentFailed:
            if "name" in opt:
                raise
            values = {}  # nothing of the game's own there (a mod's claim prompt): walk at the point itself
        if hold is None and number(values.get("hold"), 0) > 0:
            hold = str(number(values["hold"], 0) + 0.5)  # a timed interaction is held through its whole timer
        # The posture comes with the find (PlayerActions.PostureFor): crouched only for a point too low for a standing eye, standing
        # for everything else and always for a terminal screen. The stand was searched with that posture's eye.
        crouched = values.get("posture") == "crouch" and not self.crouched()
        if crouched or (values.get("posture") == "stand" and self.crouched()):
            self.posture(ts.Line(line.number, line.text, "command", values["posture"]))
        aimed = [call(ACTIONS, "Aimed", *find[0]["args"])] if values else []
        try:
            if values and self.selected_from_here(values):
                # The game already selects it from where the player stands: a player presses Use here and walks nowhere. Only when
                # it does not is the found stand walked to (for a terminal, in front of its screen: PlayerActions.StandFor).
                point = [float(part) for part in values["point"].split(",")]
                values.update(pairs(self.run(aimed)[0]))
                self.run([call(INPUT, "Look", *point)] + self.use(duration(hold, 0.3)))
                return Result(True, {**values, "selected": values["name"], "walked": "false", "interacted": "true"})
            return self.interact_found(values, point, hold, aimed)
        finally:
            if crouched:
                self.posture(ts.Line(line.number, line.text, "command", "stand"))

    def selected_from_here(self, values):
        """Looks at the found interaction from where the player stands and answers whether the game settles on selecting it
        (`selection`, up to SELECT_SECONDS)."""
        return self.selection([call(INPUT, "Look", *[float(part) for part in values["point"].split(",")])], values["name"])[1]

    def crouched(self):
        return "Crouch" in self.client.read("localPlayer", ["Locomotion.m_currentStateEnum"])

    def interact_found(self, values, point, hold, aimed):
        if values:
            point = [float(part) for part in values["point"].split(",")]
            walked = self.move_to([float(part) for part in values["stand"].split(",")], within=STAND_REACHED)
        else:
            # move_to measures from the feet, so the goal is the point lowered by the eye height: the eye ends within REACH of the point.
            walked = self.move_to([point[0], point[1] - EYE_HEIGHT, point[2]], within=REACH)
        if not walked.ok:
            return Result(False, {**values, **walked.values}, error="interact.out-of-reach: " + walked.error)
        if not values:
            # A mod's claim prompt is not in the game's selection, so nothing says it was taken: the press is reported, a later read shows it.
            self.run([call(INPUT, "Look", *point)] + self.use(duration(hold, 0.3)))
            return Result(True, {"pressed": "true"})
        values["selected"], settled = self.selection([call(INPUT, "Look", *point)], values["name"])
        # How the look ended against the target (aimAngle, turn and pitch together) and, unselected, why (PlayerActions.Aimed).
        values.update(pairs(self.run(aimed)[0]))
        if not settled:
            return Result(False, values, error="interact.not-selected: " + values.get("reason", "none") + "：等了 " + str(SELECT_SECONDS)
                          + " 秒，游戏选中的是 " + values["selected"] + "，不是 " + values["name"] + "（视线偏 " + values.get("aimAngle", "?") + " 度）")
        self.run([call(INPUT, "Look", *point)] + self.use(duration(hold, 0.3)))
        return Result(True, {**values, "interacted": "true"})

    def selection(self, look, wanted):
        """Runs the `look` steps and reads the game's own selection every few frames until the same answer comes twice in a row and is
        `wanted` (any interaction when None), or SELECT_SECONDS pass. Answers the last selection and whether it settled."""
        deadline, last = time.monotonic() + SELECT_SECONDS, None
        while True:
            now = pairs(self.run(look + [frames(3), call(ACTIONS, "Selected")])[-1]).get("selected", "none")
            if now == last and (now == wanted if wanted else now != "none"):
                return now, True
            if time.monotonic() >= deadline:
                return now, False
            last = now

    @staticmethod
    def use(seconds):
        return [call(INPUT, "Hold", "Use"), pause(seconds), call(INPUT, "Let", "Use"), pause(0.3), call(INPUT, "LetAll")]

    def press(self, line):
        action = line.args[0]
        length = line.args[1] if len(line.args) > 1 else ("1s" if line.verb == "hold" else "3f")
        wait = frames(int(number(length[:-1], 3))) if length.endswith("f") else pause(number(length.rstrip("s"), 1))
        values = self.run([call(INPUT, "Hold", action), wait, call(INPUT, "Let", action), call(INPUT, "LetAll")])
        return Result(True, {"held": values[0]})

    def release(self, line):
        # `release <action>` lets that one input go; plain `release` lets everything go.
        return Result(True, {"released": self.run([call(INPUT, "Let", line.args[0]) if line.args else call(INPUT, "LetAll")])[0]})

    def key(self, line):
        self.run([call(INPUT, "HoldKey", line.args[0]), pause(number(line.args[1].rstrip("s"), 0.3) if len(line.args) > 1 else 0.3),
                  call(INPUT, "LetKey", line.args[0])])
        return Result(True, {"key": line.args[0]})

    def god(self, line):
        if line.args[0] not in ("on", "off"):
            raise ValueError("god 需要 on 或 off")
        return Result(True, {"god": line.args[0], "result": self.run([drive("drive.player." + ("protect" if line.args[0] == "on" else "unprotect"))])[0]})

    def give(self, line):
        return Result(True, pairs(self.run([call(ACTIONS, "Give", line.args[0], int(number(line.args[1], 1)) if len(line.args) > 1 else 1)])[0]))

    def ammo(self, line):
        if line.args[0] != "fill":
            raise ValueError("ammo 只支持 fill")
        values = self.run([call(FIRE, "Fill"), call(STATE, "AmmoRead")])
        return Result(True, pairs(values[-1]))

    def plain(self, line):
        return Result(True, {"result": self.run([call(ACTIONS, line.verb.capitalize())])[0]})

    def toon(self, line):
        """`toon forward|deferred|off [object]`: ForgeVisual's toon shader on the wielded item (or the named object), to compare its two lighting entries."""
        if line.args[0] not in ("forward", "deferred", "off"):
            raise ValueError("toon 需要 forward、deferred 或 off")
        return Result(True, {"result": self.client.call(NATIVE + "ToonCompare", "Apply", [line.args[0], line.args[1] if len(line.args) > 1 else ""])})

    def picker(self, line):
        """`picker hover|leave <card>`: the open ForgeCore picker as if the cursor entered or left list card <card> (its own hover entry,
        no mouse), then reads back the card its details follow (-1: what the slot holds)."""
        if line.args[0] not in ("hover", "leave"):
            raise ValueError("picker 需要 hover 或 leave")
        picker = {"target": "static:ForgeUI.Picker", "call": "get_Current"}
        values = self.run([picker, {"target": "result", "call": "Hover", "args": [int(line.args[1]), line.args[0] == "hover"]},
                           picker, {"target": "result", "call": "get_Focus", "args": []}])
        return Result(True, {"focus": values[-1]})

    # ---- the world ---------------------------------------------------------------------------------------------------

    def spawn(self, line):
        count = int(number(line.args[1], 1)) if len(line.args) > 1 else 1
        values = self.run([call(ACTIONS, "Spawn", line.args[0], count, line.options.get("at", "front:8"), line.options.get("mode", "aggressive"), line.name)])
        return Result(True, pairs(values[-1]))

    def enemies(self, line):
        return Result(True, {"list": self.run([call(ACTIONS, "Enemies", number(line.args[0], 30) if line.args else 30)])[0]})

    def clear(self, line):
        if line.args[0] not in ("enemies", "groups"):
            raise ValueError("clear 需要 enemies 或 groups")
        step = call(ACTIONS, "Clear") if line.args[0] == "enemies" else drive("drive.enemies.clear_groups")
        return Result(True, {"result": self.run([step])[0]})

    def enemy_probe(self, line):
        return Result(True, {"result": self.run([drive("drive.enemy." + line.verb, " ".join(line.args))])[0]})

    def door(self, line):
        if line.args[0] not in ("read", "open"):
            raise ValueError("door 需要 read 或 open")
        spec = line.args[1]
        if spec.startswith("near:"):  # the doors around the player, for a script that cannot know world coordinates
            center = [float(part) for part in self.position()["Position"].split(",")]
            spec = json.dumps({"center": center, "radius": number(spec[5:], 8)}, separators=(",", ":"))
        return Result(True, {"result": self.run([drive("drive.doors." + line.args[0], spec)])[0]})

    def noise(self, line):
        radius = number(line.args[0], 20)
        position = [float(part) for part in self.position()["Position"].split(",")]
        spec = {"position": position, "type": "detectable", "radiusMin": radius / 2, "radiusMax": radius, "neighbours": True, "raycast": False}
        return Result(True, {"result": self.run([drive("drive.noise.make", json.dumps(spec, separators=(",", ":")))])[0]})

    def event(self, line):
        self.run([{"worldEvent": json.loads(line.args[0])}])
        return Result(True, {"event": "executed"})

    def checkpoint(self, line):
        if line.args[0] not in ("save", "restore", "confirm", "wipe"):
            raise ValueError("checkpoint 需要 save|restore|confirm|wipe")
        return Result(True, {"result": self.run([drive("drive.checkpoint." + line.args[0])])[0]})

    def scan(self, line):
        if line.args[0] != "read":
            raise ValueError("scan 需要 read <exit> [半径]")
        spec = {"schemaVersion": "1.0.0", "exit": line.args[1], "radius": number(line.args[2], 5) if len(line.args) > 2 else 5}
        return Result(True, {"result": self.run([drive("drive.scan.read", json.dumps(spec, separators=(",", ":")))])[0]})

    def extract(self, line):
        spec = {"schemaVersion": "1.0.0", "exit": line.args[0], "timeoutSeconds": int(number(line.args[1], 60)) if len(line.args) > 1 else 60}
        return Result(True, {"result": self.run([drive("drive.extraction.complete", json.dumps(spec, separators=(",", ":")))],
                                                 timeout=spec["timeoutSeconds"] + 60)[0]})

    def nav(self, line):
        if line.args[0] not in ("near", "path", "sample", "voxel"):
            raise ValueError("nav 需要 near|path|sample|voxel <JSON>")
        return Result(True, {"result": self.run([drive("drive.nav." + line.args[0], line.args[1])])[0]})

    def terminal(self, line):
        """`terminal type "<line>"` types into the terminal the player uses and presses Enter (the game's own OnReturn), then reads the
        screen's last lines once the output had TERMINAL_SETTLE seconds to print; `terminal exit` leaves it (ExitFPSView); `terminal read
        [n]` reads the last n lines (default 8)."""
        what = line.args[0]
        if what == "type" and len(line.args) == 2:
            steps = [call(TERMINAL, "Type", line.args[1]), pause(TERMINAL_SETTLE), call(TERMINAL, "Read", 8)]
        elif what == "exit" and len(line.args) == 1:
            steps = [call(TERMINAL, "Exit"), pause(0.8), call(TERMINAL, "Read", 1)]
        elif what == "read" and len(line.args) <= 2:
            steps = [call(TERMINAL, "Read", int(number(line.args[1], 8)) if len(line.args) == 2 else 8)]
        else:
            raise ValueError('terminal 需要 type "<一行>"、exit 或 read [行数]')
        values = self.run(steps)
        return Result(True, {**pairs(values[0]), **pairs(values[-1])})

    def model_shot(self, line):
        return Result(True, {"result": self.run([drive("drive.model.shoot", line.args[0])])[0]})

    def alarms(self, line):
        """The scans and alarms running now and the survival waves the mastermind holds (`alarms all` also lists idle and solved scans)."""
        if line.args and line.args[0] != "all":
            raise ValueError("alarms 只接受 all")
        return Result(True, pairs(self.run([call(ACTIONS, "Alarms", bool(line.args))])[0]))

    def recorder(self, line):
        """What the session recorder lost: drops, queue refusals, the byte budget and the latest cause."""
        return Result(True, pairs(self.run([call("RecSession", "Losses")])[0]))

    # ---- reads -------------------------------------------------------------------------------------------------------

    def player(self, line):
        return Result(True, pairs(self.client.read("localPlayer", line.args or PLAYER_PATHS)))

    def read(self, line):
        return Result(True, pairs(self.client.read(line.args[0], line.args[1:])))

    def read_state(self, line):
        kind = line.args[0]
        if kind == "variables":
            spec = {"schemaVersion": "1.0.0", "prefix": line.args[1] if len(line.args) > 1 else ""}
            receipt = self.run([drive("drive.variables.read", json.dumps(spec, separators=(",", ":")))])[0]
            read = summary(receipt)
            return Result(True, {"result": receipt, "matched": read.get("matched"), "shown": read.get("shown"), **(read.get("values") or {})})
        step = {"ammo": call(STATE, "AmmoRead"), "stamina": call(STATE, "StaminaRead"), "fog": call(STATE, "FogRead"),
                "intel": call(STATE, "IntelRead"), "hud": call(STATE, "HudRead"), "lifecycle": drive("drive.lifecycle.read")}.get(kind)
        if step is None:
            raise ValueError("state 需要 " + "|".join(ts.STATES))
        return Result(True, pairs(self.run([step])[0]))

    # ---- values for expect and wait until ----------------------------------------------------------------------------

    def value_of(self, reader, variables, log_lines):
        """(actual, Result) of a condition's reader, or (None, failed Result) when it cannot be read."""
        head = reader[0]
        if head.startswith("$"):
            name, _, path = head[1:].partition(".")
            return ts.lookup(variables.get(name, {}), path), None
        if head == "log":
            return (None if log_lines is None else "\n".join(log_lines)), None
        field_path = reader[-1]
        if head == "player":
            result = self.player(ts.Line(0, "", "command", "player", [field_path.rpartition(".")[0] if field_path.rpartition(".")[2] in ("x", "y", "z") else field_path]))
        elif head == "read":
            result = self.read(ts.Line(0, "", "command", "read", [reader[1], field_path.rpartition(".")[0] if field_path.rpartition(".")[2] in ("x", "y", "z") else field_path]))
        elif head == "state":
            result = self.read_state(ts.Line(0, "", "command", "state", reader[1:2] + reader[2:-1]))
        elif head == "enemy":
            result = Result(True, pairs(self.run([call(ACTIONS, "Enemy", reader[1])])[0]))
        elif head == "enemies":
            result = Result(True, {"list": self.run([call(ACTIONS, "Enemies", 30)])[0]})
        elif head == "alarms":
            result = self.alarms(ts.Line(0, "", "command", "alarms"))
        else:
            raise ValueError("读取 " + head + " 不能用在断言里")
        return ts.lookup(result.values, field_path), result
