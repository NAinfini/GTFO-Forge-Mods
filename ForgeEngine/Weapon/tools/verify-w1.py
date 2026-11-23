"""Build and verify W1 offline evidence. No installation, gameplay or network bindings.

Requires the repository's .NET SDK and an existing GTFO/BepInEx installation.
All outputs are isolated below artifacts/mods/ForgeEngine; every run uses a new folder.
"""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys

def fingerprint(path: Path) -> str:
    with path.open("rb") as stream:
        digest = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
        return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bepinex", required=True)
    parser.add_argument("--game", required=True)
    parser.add_argument("--architecture", action="store_true", help="Also build/test the existing six-module boundary checks")
    parser.add_argument("--metadata-only", action="store_true", help="Verify independent W1 tools without building a changing Runtime SDK")
    parser.add_argument("--output-root", help="A NEW directory below artifacts/mods/ForgeEngine")
    args = parser.parse_args()
    if args.metadata_only and args.architecture:
        parser.error("--metadata-only cannot be combined with --architecture")
    weapon = Path(__file__).resolve().parents[1]
    engine = weapon.parent
    repo = engine.parent
    bep = Path(args.bepinex).resolve()
    game = Path(args.game).resolve()
    if not (bep / "core/Mono.Cecil.dll").is_file() or not (game / "GameAssembly.dll").is_file():
        parser.error("Existing GTFO and BepInEx input files are required; nothing is downloaded or installed.")
    artifacts = repo.parent / "artifacts" / "mods" / "ForgeEngine"
    output = Path(args.output_root).resolve() if args.output_root else artifacts / (
        "w1-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ"))
    roots = (artifacts.resolve(), (repo.parent / "artifacts/work").resolve())
    if not any(output != root and output.is_relative_to(root) for root in roots):
        parser.error("Output must be a new subdirectory of artifacts/mods/ForgeEngine or artifacts/work")
    output.mkdir(parents=True, exist_ok=False)
    build = output / "build"
    source_roots = [weapon] if args.metadata_only else [weapon, engine / "Framework"]
    if args.architecture:
        source_roots += [engine / "tests/Architecture", engine / "tests/Contracts", repo / "ForgeDevelopment", engine / "Enemy", engine / "Map"]

    def snapshot() -> dict[str, str]:
        result: dict[str, str] = {}
        for root in source_roots:
            for path in root.rglob("*"):
                relative = path.relative_to(repo)
                if any(p in ("bin", "obj", ".artifacts", ".git") for p in relative.parts):
                    continue
                if path.is_file() and path.suffix in (".cs", ".csproj", ".props", ".targets", ".py", ".json"):
                    result[relative.as_posix()] = fingerprint(path)
        return dict(sorted(result.items()))

    receipt: dict = {"schemaVersion": "1.0.0", "startedUtc": datetime.now(timezone.utc).isoformat(),
                     "status": "running", "gameExecuted": False, "installed": False,
                     "verificationScope": "metadata-only" if args.metadata_only else "weapon-and-dependencies",
                     "stages": [], "sourceSnapshotBefore": snapshot()}

    def run(name: str, command: list[str]) -> None:
        log = output / (name + ".log")
        print("RUN", name, flush=True)
        with log.open("w", encoding="utf-8") as stream:
            result = subprocess.run(command, cwd=repo, stdout=stream, stderr=subprocess.STDOUT,
                                    timeout=900, check=False)
        receipt["stages"].append({"name": name, "argv": command, "exitCode": result.returncode,
                                   "log": log.name})
        if result.returncode != 0:
            raise RuntimeError(f"{name} failed with exit code {result.returncode}; see {log}")

    def build_project(name: str, path: Path) -> None:
        run(name, ["dotnet", "build", str(path), "-c", "Release", "--artifacts-path", str(build),
                   "--disable-build-servers", "-m:1", "-p:GTFOBepInExPath=" + str(bep), "-p:NuGetAudit=false", "-warnaserror"])

    try:
        build_project("native-audit-build", weapon / "tools/NativeAudit/NativeAudit.csproj")
        if not args.metadata_only:
            build_project("engine-build", engine / "ForgeEngine.csproj")
        auditor = build / "bin/NativeAudit/release/NativeAudit.dll"
        run("metadata-lock", ["dotnet", str(auditor), "--bepinex", str(bep), "--game", str(game),
                              "--contract", str(weapon / "evidence/w1-native-contract.json"),
                              "--output", str(output / "metadata-verification.json")])
        if args.architecture:
            build_project("architecture-build", engine / "tests/Contracts/EngineContracts.csproj")
            run("architecture-tests", ["dotnet", str(build / "bin/EngineContracts/release/ForgeEngine.EngineContracts.Tests.dll"), "Architecture"])
        receipt["sourceSnapshotAfter"] = snapshot()
        if receipt["sourceSnapshotBefore"] != receipt["sourceSnapshotAfter"]:
            raise RuntimeError("Source files changed during verification; rerun after reconciling concurrent changes.")
        evidence = json.loads((output / "metadata-verification.json").read_text(encoding="utf-8"))
        receipt.update(status="passed", metadataChecks=evidence["checks"],
                       gameplayAcceptanceCasesExecuted=0)
    except (OSError, ValueError, KeyError, StopIteration, RuntimeError, subprocess.TimeoutExpired) as error:
        receipt.update(status="failed", error=str(error), sourceSnapshotAfter=snapshot())
        print(str(error), file=sys.stderr)
    finally:
        receipt["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        (output / "validation-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
        print(output / "validation-receipt.json", flush=True)
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
