"""Exercise the real static auditor against isolated, deliberately broken specifications."""
from __future__ import annotations
import argparse
import copy
import json
from pathlib import Path
import shutil
import subprocess


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("auditor", type=Path)
    parser.add_argument("bepinex", type=Path)
    parser.add_argument("game", type=Path)
    parser.add_argument("forge", type=Path, help="ForgeEngine.dll whose Enemy IL usage the spec freezes")
    parser.add_argument("spec", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--damage-window", type=Path, default=None,
                        help="damage window evidence file passed to the auditor as its optional sixth argument")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    original = json.loads(args.spec.read_text(encoding="utf-8"))
    window_source = args.damage_window or args.spec.parent / "e10-damage-window.json"
    window_original = json.loads(window_source.read_text(encoding="utf-8"))
    # Mutated specs live in the output directory; give them the same relative data evidence files.
    for data in original["dataEvidenceFiles"]:
        target = args.output / data["path"]
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(args.spec.parent / data["path"], target)
    results: list[dict] = []

    def run(name: str, candidate: dict, expected_id: str | None, window: dict | None = None) -> bool:
        spec_path = args.output / f"{name}.spec.json"
        report_path = args.output / f"{name}.report.json"
        window_path = args.output / f"{name}.damage-window.json"
        spec_path.write_text(json.dumps(candidate, indent=2), encoding="utf-8")
        window_path.write_text(json.dumps(window if window is not None else window_original, indent=2), encoding="utf-8")
        command = ["dotnet", str(args.auditor.resolve()), "NativeEvidence", str(args.bepinex.resolve()), str(args.game.resolve()),
                   str(args.forge.resolve()), str(spec_path.resolve()), str(report_path.resolve()), str(window_path.resolve())]
        completed = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
        (args.output / f"{name}.log").write_text(completed.stdout + completed.stderr, encoding="utf-8")
        report = json.loads(report_path.read_text(encoding="utf-8"))
        failed_ids = [check["Id"] for check in report["checks"] if not check["Passed"]]
        passed = (completed.returncode == 0 and not failed_ids) if expected_id is None else (
            completed.returncode == 1 and expected_id in failed_ids)
        results.append({"id": name, "passed": passed, "exitCode": completed.returncode,
                        "expectedFailure": expected_id, "observedFailures": failed_ids})
        print(f"{'PASS' if passed else 'FAIL'} {name}: exit={completed.returncode}, failures={failed_ids}")
        return passed

    def method(spec: dict, signature: str) -> dict:
        matches = [m for m in spec["methods"] if m["signature"] == signature]
        if len(matches) != 1:
            raise ValueError(f"Mutation anchor missing: {signature}")
        return matches[0]

    if not run("baseline", original, None):
        raise RuntimeError("Baseline auditor failed; mutation rejection cannot be credited.")
    send = "System.Void Dam_SyncedDamageBase::SendSetHealth(System.Single)"
    global_id = "System.UInt16 Agents.Agent::get_GlobalID()"
    spawn = "System.Void SurvivalWave::OnSpawn(pSurvivalWaveSpawnData)"  # a member the plugin still patches itself (EnemySync.OnSpawn moved to ForgeCore's subscription)
    alive = "System.Boolean Agents.Agent::get_Alive()"
    mutations = []
    item = copy.deepcopy(original); item["buildId"] = "0"
    mutations.append(("wrong-build", item, "steam.build"))
    item = copy.deepcopy(original); item["gameAssemblySha256"] = "0" * 64
    mutations.append(("wrong-native-hash", item, "native.hash"))
    item = copy.deepcopy(original); item["metadataSha256"] = "0" * 64
    mutations.append(("wrong-metadata-hash", item, "metadata.hash"))
    item = copy.deepcopy(original); item["assemblies"][0]["signatureSha256"] = "0" * 64
    mutations.append(("wrong-interop-signature", item, "interop." + item["assemblies"][0]["file"]))
    item = copy.deepcopy(original); item["methods"][0]["signature"] = "System.Void Missing::Method()"
    mutations.append(("wrong-signature", item, item["methods"][0]["id"]))
    item = copy.deepcopy(original); item["methods"][0]["isStatic"] = not item["methods"][0]["isStatic"]
    mutations.append(("wrong-static-flag", item, item["methods"][0]["id"]))
    item = copy.deepcopy(original); item["methods"][0]["type"] = "Missing.Type"
    mutations.append(("missing-type", item, item["methods"][0]["id"]))
    item = copy.deepcopy(original); item["absentDeclaredMethods"][0]["name"] = "ReceiveSetHealth"
    mutations.append(("false-absence-claim", item, item["absentDeclaredMethods"][0]["id"]))
    item = copy.deepcopy(original); item["methods"] = []
    mutations.append(("empty-spec", item, "audit.error"))
    item = copy.deepcopy(original); item["schemaVersion"] = "2.0.0"
    mutations.append(("retired-schema", item, "audit.error"))
    item = copy.deepcopy(original); item["methods"][0]["confidence"] = "guessed"
    mutations.append(("unknown-method-field", item, "audit.error"))
    item = copy.deepcopy(original); item["methods"][0]["evidenceLevel"] = "runtime-verified"
    mutations.append(("unknown-evidence-level", item, "evidence-level." + item["methods"][0]["id"]))
    item = copy.deepcopy(original); target = method(item, global_id)
    target["evidenceLevel"] = "static-native"; target["nativeRva"] = "0x161F790"
    mutations.append(("static-native-overclaim", item, "evidence-level." + target["id"]))
    item = copy.deepcopy(original); target = method(item, send)
    target["evidenceLevel"] = "metadata"; del target["nativeRva"]
    mutations.append(("static-native-underclaim", item, "evidence-level." + target["id"]))
    item = copy.deepcopy(original); item["methods"][0]["nativeCallPhase"] = "main-thread-postfix"
    mutations.append(("claimed-native-call-phase", item, "call-phase." + item["methods"][0]["id"]))
    item = copy.deepcopy(original); target = method(item, spawn); target["forgeHooks"] = []
    mutations.append(("forge-hook-dropped", item, "forge-use." + target["id"]))
    item = copy.deepcopy(original)
    target = next(m for m in item["methods"] if not m["forgeHooks"] and not m["forgeCallers"])
    target["forgeCallers"] = ["ForgeEnemy.Native.EnemyModule::Heal"]
    mutations.append(("forge-caller-fabricated", item, "forge-use." + target["id"]))
    item = copy.deepcopy(original); item["methods"].remove(method(item, alive))
    mutations.append(("forge-api-unfrozen", item, "forge-use.call-coverage"))
    item = copy.deepcopy(original); item["enumConstants"][0]["value"] += 1
    mutations.append(("wrong-enum-value", item, item["enumConstants"][0]["id"]))
    item = copy.deepcopy(original)
    target = next(m for m in item["methods"] if m["dataEvidence"])
    target["dataEvidence"][0]["pointer"] = target["dataEvidence"][0]["pointer"].rsplit(".", 1)[0] + ".guessedField"
    mutations.append(("data-pointer-missing", item, f"data-evidence.{target['id']}.0"))
    item = copy.deepcopy(original); item["dataEvidenceFiles"][0]["sha256"] = "0" * 64
    mutations.append(("data-file-hash", item, "data-file." + item["dataEvidenceFiles"][0]["path"]))
    for name, candidate, expected_id in mutations:
        run(name, candidate, expected_id)
    # The damage window file is a second frozen input; each mutation below must be rejected by the check that
    # owns the claim, so a passing audit cannot hide a wrong build, a moved body or a fabricated call edge.
    window_mutations = []
    item = copy.deepcopy(window_original); item["gameAssemblySha256"] = "0" * 64
    window_mutations.append(("window-wrong-native-hash", item, "damage-window.build"))
    item = copy.deepcopy(window_original); item["buildId"] = "0"
    window_mutations.append(("window-wrong-build", item, "damage-window.build"))
    item = copy.deepcopy(window_original); item["schemaVersion"] = "2.0.0"
    window_mutations.append(("window-retired-schema", item, "damage-window.schema"))
    item = copy.deepcopy(window_original)
    target = next(m for m in item["damageMethods"] if m["id"].endswith("ProcessReceivedDamage"))
    target["nativeRva"] = "0x137E571"
    window_mutations.append(("window-moved-rva", item, "damage-window.rva." + target["id"]))
    item = copy.deepcopy(window_original)
    target = next(m for m in item["damageMethods"] if m["id"].endswith("ProcessReceivedDamage"))
    target["parameters"][1]["name"] = "attacker"
    window_mutations.append(("window-fabricated-parameter", item, "damage-window.parameters." + target["id"]))
    item = copy.deepcopy(window_original)
    item["packetTypes"][0]["fields"][2]["declaration"] = "byte limbID"
    window_mutations.append(("window-packet-layout", item, "damage-window.packet." + item["packetTypes"][0]["type"]))
    item = copy.deepcopy(window_original)
    edge = item["callEdges"][0]
    edge["targetRva"] = "0x1380D50"
    edge["to"] = "Dam_SyncedDamageBase.SendSetHealth"
    item["callSiteWindows"] = [w for w in item["callSiteWindows"] if w["site"] != edge["site"]]
    window_mutations.append(("window-fabricated-edge", item, "damage-window.edge." + edge["site"]))
    item = copy.deepcopy(window_original)
    # Dropping a frozen instruction window leaves the call edge that cites it unverifiable, so the edge
    # check is the one that must reject it; the site is read from the file instead of hard-coded.
    edge_sites = {edge["site"] for edge in item["callEdges"]}
    removed = next(w["site"] for w in item["callSiteWindows"] if w["site"] in edge_sites)
    item["callSiteWindows"] = [w for w in item["callSiteWindows"] if w["site"] != removed]
    window_mutations.append(("window-missing-window", item, "damage-window.edge." + removed))
    item = copy.deepcopy(window_original)
    item["conclusions"].pop("q3-sentry-source")
    window_mutations.append(("window-missing-conclusion", item, "damage-window.conclusions"))
    for name, candidate, expected_id in window_mutations:
        run(name, original, expected_id, window=candidate)
    summary = {"schemaVersion": "1.0.0", "gameExecuted": False, "passed": sum(r["passed"] for r in results),
               "failed": sum(not r["passed"] for r in results), "checks": results}
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(f"{'PASS' if not summary['failed'] else 'FAIL'} {summary['passed']}/{len(results)} auditor negative-case checks; game NOT executed.")
    return 1 if summary["failed"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
