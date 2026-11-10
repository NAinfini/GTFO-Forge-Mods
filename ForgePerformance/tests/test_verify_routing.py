"""Exercise the real verifier's selection/input routing; child programs are doubles.

This does not build or test C# and cannot establish native or game correctness.
"""
import contextlib
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT/"Mods"))
from verify_inputs import Inputs


class VerifyRoutingTests(unittest.TestCase):
    def verify(self, selectors, native=False, code_only=False, missing_resources=(), preparation_exit=0):
        parent = Path(os.environ.get("FORGE_VERIFY_ROUTING_WORKSPACE",ROOT / "artifacts/work/p4-perf/runner-tests"))
        parent.mkdir(parents=True, exist_ok=True)
        with contextlib.nullcontext(tempfile.mkdtemp(dir=parent)) as temporary:
            work = Path(temporary)
            calls = []
            # Only child routing is doubled here. Supply explicit empty read-only reference
            # files so the real preflight does not confuse this test with missing QA inputs.
            bep=work/"bepinex";bep.mkdir()
            inputs=Inputs(ROOT)
            for project in ROOT.glob("Mods/*/tests/**/*.csproj"):
                for hint in inputs.project(project)["hints"]:
                    if hint.startswith("$(GTFOBepInExPath)"):
                        target=bep/hint.split(")",1)[1].replace("\\","/").lstrip("/")
                        if "$" in str(target):continue
                        target.parent.mkdir(parents=True,exist_ok=True);target.touch()
            game=work/"game";game.mkdir();(game/"GameAssembly.dll").touch()

            def child(argv, **kwargs):
                calls.append((list(map(str, argv)), kwargs["env"]))
                if argv[:2] == ["dotnet", "test"]:
                    log = next(a for a in argv if str(a).startswith("trx;LogFileName="))
                    report = Path(argv[argv.index("--results-directory") + 1]) / log.split("=", 1)[1]
                    report.write_text('<TestRun><Results><UnitTestResult outcome="Passed" /></Results>'
                                      '<ResultSummary><Counters total="1" executed="1" passed="1" failed="0" />'
                                      '</ResultSummary></TestRun>', encoding="utf-8")
                if argv[:1] == ["node"] and "--prepare" in argv:
                    return subprocess.CompletedProcess(argv, preparation_exit)
                return subprocess.CompletedProcess(argv, 0)

            argv = [str(ROOT / "Mods/verify.py"), *selectors, "--work-dir", str(work), "--bepinex", str(bep),
                    "--collector-baseline", str(work)]  # given explicitly: verify.py does not rebuild it from Git
            if code_only:
                argv += ["--code-only"]
            if native:
                # The routing test needs existing paths, not game bytes. Real native
                # checks are separate and run against the QA inputs through verify.py.
                argv += ["--game", str(game), "--dump", __file__]
            with open(os.devnull, "w", encoding="utf-8") as output, patch.object(sys, "argv", argv), patch.dict(os.environ, {
                "FORGE_GTFO_GAME": "", "FORGE_GTFO_DUMP": "", "GTFO_BEPINEX_PATH": ""
            }), patch("subprocess.run", side_effect=child), patch.object(Inputs,"missing",side_effect=lambda resources:sorted(set(resources)&set(missing_resources))), contextlib.redirect_stdout(output):
                try:
                    runpy.run_path(str(ROOT / "Mods/verify.py"), run_name="__main__")
                    code = 0
                except SystemExit as error:
                    code = error.code
            summary = json.loads((work / "verify.json").read_text(encoding="utf-8"))
            return code, calls, summary

    def test_missing_native_inputs_do_not_hide_managed_tests_or_pass_the_full_gate(self):
        code, calls, summary = self.verify(["ForgePerformance"])
        self.assertEqual(code, 2)  # A partial run reports 2: it is not a complete verification pass.
        states = {row["step"]: row["exit"] for row in summary}
        self.assertEqual(states["inspect_native.py"], "NOT_RUN")
        self.assertFalse(any("<no-game-input>" in argument for argv, _ in calls for argument in argv))

    def test_code_only_keeps_resource_checks_visible_without_preparing_or_building(self):
        code, calls, summary = self.verify(["ForgeEngine:Framework"], code_only=True)
        self.assertEqual(code, 2)
        self.assertEqual(calls, [])
        self.assertEqual([(row["step"], row["exit"]) for row in summary], [("Framework", "NOT_RUN")])
        self.assertIn("需要本机资源，未运行", summary[0]["tail"][0])

    def test_missing_resource_root_reports_plan_consumers_not_run_and_names_them(self):
        # Detection: without the published-data root the compiled-plan consumers must be NOT_RUN with the missing input listed,
        # and the failing Prepare must not run at all (a failed Prepare used to hide every later step).
        code, calls, summary = self.verify(["ForgeEngine:Framework"], missing_resources=("Web/public/data/maps/game/catalog.json",))
        self.assertEqual(code, 2)
        self.assertFalse(any(argv[:1] == ["node"] and "--prepare" in argv for argv, _ in calls))
        rows = {row["step"]: row for row in summary}
        self.assertEqual(rows["Framework"]["exit"], "NOT_RUN")
        self.assertIn("Web/public/data/maps/game/catalog.json", rows["Framework"]["tail"][0])
    def test_failed_preparation_prevents_consumer_execution_and_fails_verification(self):
        code, calls, summary = self.verify(["ForgeEngine:Framework"], preparation_exit=17)
        self.assertEqual(code, 1)
        self.assertTrue(any(argv[:1] == ["node"] and "--prepare" in argv for argv, _ in calls))
        self.assertFalse(any(any(arg.endswith("ForgeEngine.EngineCore.Tests.dll") for arg in argv) for argv, _ in calls))
        self.assertTrue(any(row["exit"] == 17 for row in summary))



if __name__ == "__main__":
    unittest.main()
