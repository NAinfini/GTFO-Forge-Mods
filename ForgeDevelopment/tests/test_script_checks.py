"""The console's test script, checked offline (no game): the parser refuses a broken script and names the line, and a wrong expect is
caught when a recorded session is judged again. These are the two things a script run depends on to mean anything."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

TOOLS = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(next(p for p in Path(__file__).resolve().parents if (p / "Tools/Game").is_dir()) / "Tools" / "Game"))
import console_run  # noqa: E402
import game_profile  # noqa: E402
import test_script as ts  # noqa: E402


def session(folder):
    """What a live run leaves: two commands and their results, as `fire` and `spawn` answer them in the game."""
    rows = [("spawn Striker 1 at=front:8 as=s1", "0001-spawn", {"id": "41", "health": "100", "alive": "true"}),
            ("fire 6 at=s1 as=shots", "0002-fire", {"clipBefore": 30, "clipAfter": 24, "fires": 6, "targetHealthAfter": "40"})]
    lines = []
    for number, (text, name, values) in enumerate(rows, 1):
        (folder / name).mkdir()
        (folder / name / "result.json").write_text(json.dumps({"seq": number, "text": text, "ok": True, "values": values}), "utf-8")
        lines.append({"line": number, "text": text, "kind": "command", "folder": name})
    lines.append({"line": 3, "text": "expect $shots.clipAfter == 24", "kind": "expect", "reading": "24"})
    (folder / "commands.jsonl").write_text("".join(json.dumps(row) + "\n" for row in lines), "utf-8")


class ProfileGuard(unittest.TestCase):
    @unittest.skipUnless(game_profile.IN_REPOSITORY, "the profile allowlist is this repository's rule; a copied tooling folder (the public mirror) takes any profile")
    def test_profile_allowlists_by_purpose(self):
        root = Path(tempfile.gettempdir()) / "profiles"
        allowed = {"drive": {"Forge-Multiplayer"}, "install": {"Forge-Multiplayer"}}
        for purpose, names in allowed.items():
            for name in ("Forge-Multiplayer", "Forge-TestForge-Look", "replaytest"):
                with self.subTest(purpose=purpose, name=name):
                    if name in names:
                        self.assertEqual(game_profile.require_profile(root / name, purpose).name, name)
                    else:
                        with self.assertRaises(RuntimeError):
                            game_profile.require_profile(root / name, purpose)

    def test_install_without_an_explicit_profile_is_refused(self):
        with self.assertRaises(SystemExit):
            game_profile.main(["install-zip", "--zip", "x.zip"])

SCRIPT = "spawn Striker 1 at=front:8 as=s1\nfire 6 at=s1 as=shots\n"


class ScriptChecks(unittest.TestCase):
    def test_syntax_errors_are_refused_with_their_line(self):
        cases = {3: "# fine\nspawn Striker as=s1\nfire 6 at=$gone\n",      # a variable nobody defined
                 2: "level 1 1 1\nfly 3\n",                                  # a command that does not exist
                 1: "expect player Position.y 0.4\n",                        # no comparator
                 4: "wait 1s\nstop-on-fail\n\nwait soon\n"}                  # a wait that is no duration
        for line, text in cases.items():
            with self.subTest(line=line), self.assertRaises(ts.ScriptError) as caught:
                ts.parse(text)
            self.assertEqual(caught.exception.line, line)
        self.assertEqual([row.kind for row in ts.parse(SCRIPT + "expect $shots.fires == 6  # all six\nwait until state ammo clip == 30 timeout=5s\n").lines],
                         ["command", "command", "expect", "wait"])

    def test_a_wrong_expect_fails_when_a_recorded_session_is_judged_again(self):
        with tempfile.TemporaryDirectory() as temp:
            recorded = Path(temp) / "recorded"
            recorded.mkdir()
            session(recorded)
            judged = {}
            for name, expect in {"right": "expect $shots.clipAfter == 24\nexpect $s1.health > 50\n", "wrong": "expect $shots.clipAfter == 25\n",
                                 "read": "expect enemy $s1.id health < 100\n"}.items():
                script = ts.parse(SCRIPT + expect, name)
                judged[name] = console_run.Run(script, console_run.RecordedConsole(recorded), Path(temp) / name).run()
            self.assertEqual(judged["right"]["summary"]["failed"], 0)
            self.assertEqual([row["ok"] for row in judged["right"]["rows"]], [True, True, True, True])
            self.assertEqual(judged["wrong"]["summary"]["failed"], 1)
            self.assertFalse(judged["wrong"]["rows"][-1]["ok"])
            # A reading the recording holds is judged against what it read then (24), not against the live game.
            self.assertEqual(judged["read"]["rows"][-1]["actual"], "24")
            self.assertEqual(json.loads((Path(temp) / "wrong" / "run.json").read_text("utf-8"))["format"], "forge.console-run")


class LiveConsole:
    """A console whose only content is a game log: enough to run the judgements that read it (log expects, the reset around a script)."""
    class Log:
        rows = [(0, "[Info   :BepInEx] started"),
                (30, "[Info   :Forge Development] experiment: failed ds-1: 0/1 steps, Spawn threw ArgumentException: no enemy x"),  # a command that already failed
                (130, "[Error  :     Unity] NullReferenceException: Object reference not set to an instance of an object.")]

        def mark(self):
            return 0

        def entries(self, start, end=None):
            return [row for row in self.rows if row[0] >= start]

        def lines(self, start, end=None):
            return [text for _, text in self.entries(start)]

        def before(self, start, count=20):
            return []

    log = Log()
    resets = 0

    def execute(self, line):
        return console_run.cc.Result(True, {})

    def value_of(self, reader, variables, log_lines):
        return console_run.cc.Console.value_of(None, reader, variables, log_lines)

    def reset(self):
        self.resets += 1
        return None

    def publish_session(self):
        return None


class RunJudgements(unittest.TestCase):
    def test_a_log_expect_sees_a_real_exception_and_not_a_failure_the_script_already_had(self):
        with tempfile.TemporaryDirectory() as temp:
            def judge(expect):
                console = LiveConsole()
                document = console_run.Run(ts.parse("god on\n" + expect), console, Path(temp) / str(abs(hash(expect)))).run()
                self.assertEqual(console.resets, 2)  # before the first line and after the last
                return document["rows"][-1]
            caught = judge('expect log --all !contains "Exception"')
            self.assertFalse(caught["ok"])
            self.assertIn("NullReferenceException", caught["detail"])  # names the line that matched, and where it stands in the log
            self.assertIn("130", caught["detail"])
            self.assertNotIn("ArgumentException", caught["detail"])  # the failed command's own log line is not read a second time
            self.assertTrue(judge('expect log --all !contains "Exception" ignore=NullReferenceException')["ok"])

    def test_a_call_that_answers_false_is_a_failed_command_unless_it_asks_a_question(self):
        asked = ts.parse("call static:X Snapshot trial\ncall static:X IsReady --allow-false\n").lines
        failed = console_run.cc.Console.native_result(asked[0], {"result": "False"})
        self.assertFalse(failed.ok)
        self.assertIn("call.returned-false", failed.error)
        self.assertTrue(console_run.cc.Console.native_result(asked[0], {"result": "True"}).ok)
        self.assertTrue(console_run.cc.Console.native_result(asked[1], {"result": "False"}).ok)


if __name__ == "__main__":
    unittest.main()


class ReplicationCompare(unittest.TestCase):
    """The detection ability of the join check: two tables that differ in one index must be reported, equal ones must pass."""
    def test_a_differing_index_is_reported(self):
        import compare_replication as compare
        host = {"replication": "0:10;1:11;2:90", "replicationDigest": "a", "replicationCount": 3, "vanillaCount": 2, "ownOrder": "ER"}
        self.assertEqual(compare.differences(host, dict(host)), [])
        client = dict(host, replication="0:10;1:90;2:11", replicationDigest="b")
        found = compare.differences(host, client)
        self.assertEqual(found[0], "position 1: host 1:11, client 1:90")
        self.assertTrue(any(row.startswith("replicationDigest") for row in found))
