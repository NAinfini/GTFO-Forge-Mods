"""Read-only evidence for the fingerprint-limited native performance observer.

Requires pefile and capstone only for this offline inspection, never in game.
No process, profile or game file is written. Output must be under artifacts.
"""
import argparse
import bisect
import hashlib
import json
from pathlib import Path
import re
import struct

import capstone
import pefile


PHASES = (
    "FixedUpdate.PhysicsFixedUpdate",
    "PreLateUpdate.DirectorUpdateAnimationBegin", "PreLateUpdate.DirectorUpdateAnimationEnd",
    "Update.ScriptRunBehaviourUpdate", "FixedUpdate.ScriptRunBehaviourFixedUpdate",
    "PreLateUpdate.ScriptRunBehaviourLateUpdate", "PostLateUpdate.UpdateAllRenderers",
    "PostLateUpdate.FinishFrameRendering", "PreUpdate.AIUpdate",
)
GAME = (
    ("Enemies.EnemyUpdateManager.GamestateUpdate", 0x1582240),
    ("Enemies.EnemyUpdateManager.GamestateUpdateFixed", 0x15821D0),
    ("Enemies.EnemyUpdateManager.MasterMindUpdate", 0x1582420),
    ("Enemies.EnemyUpdateManager.MasterMindUpdateFixed", 0x15823B0),
    ("SurvivalWave.StartWave", 0x13F5A10), ("SurvivalWave.SpawnGroup", 0x13F4FB0),
    ("SurvivalWave.StopEvent", 0x13F5BE0),
    ("SurvivalWave.RegisterGroup", 0x13F4C30),
    ("ChainedPuzzles.ChainedPuzzleInstance.MasterActivate", 0x17334D0),
    ("ChainedPuzzles.ChainedPuzzleInstance.MasterDeactivate", 0x1733A90),
    ("ChainedPuzzles.ChainedPuzzleInstance.OnStateChange", 0x1734BF0),
)


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


class Image:
    def __init__(self, path):
        self.path = path
        self.pe = pefile.PE(str(path), fast_load=True)
        self.pe.parse_data_directories(directories=[0, 3])
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.text = next(s for s in self.pe.sections if s.Name.startswith(b".text"))
        self.code = self.text.get_data()
        self.code_base = self.base + self.text.VirtualAddress
        self.executable = [(self.base+s.VirtualAddress, s.get_data()) for s in self.pe.sections if s.Characteristics & 0x20000000]
        self.functions = [(e.struct.BeginAddress, e.struct.EndAddress) for e in self.pe.DIRECTORY_ENTRY_EXCEPTION]
        self.starts = [a for a, _ in self.functions]

    def instructions(self, rva, size):
        return [f"0x{x.address:x} {x.mnemonic} {x.op_str}" for x in self.decoder.disasm(self.pe.get_data(rva, size), self.base+rva)]

    def icall(self, name):
        raw = name.encode()+b"\0"
        at = self.pe.__data__.find(raw)
        if at < 0:
            return {"status": "unavailable", "reason": "registration_string_absent"}
        target = self.base + self.pe.get_rva_from_offset(at)
        for match in re.finditer(rb"\x48\x8d\x0d", self.code):
            i = match.start()
            if self.code_base+i+7+struct.unpack_from("<i", self.code, i+3)[0] != target:
                continue
            if self.code[i-7:i-4] != b"\x48\x8d\x15":
                continue
            function = self.code_base+i+struct.unpack_from("<i", self.code, i-4)[0]
            return {"registration_rva": hex(self.code_base+i-self.base), "entry_rva": hex(function-self.base),
                    "instructions": self.instructions(function-self.base, 100)}
        return {"status": "unavailable", "reason": "registration_call_relationship_not_found"}

    def callers(self, target, limit=3):
        result = []
        address = self.base+target
        for code_base, code in self.executable:
            for match in re.finditer(rb"\xe8....", code, re.DOTALL):
                i = match.start()
                call = code_base+i
                if call+5+struct.unpack_from("<i", code, i+1)[0] != address:
                    continue
                rva = call-self.base
                index = bisect.bisect_right(self.starts, rva)-1
                if index < 0:
                    continue
                start, end = self.functions[index]
                if not start <= rva < end or end-start > 30000:
                    continue
                instructions = list(self.decoder.disasm(self.pe.get_data(start, end-start), self.base+start))
                at = next((j for j, x in enumerate(instructions) if x.address == call and x.mnemonic == "call"), None)
                if at is not None:
                    result.append({"caller_rva": hex(start), "call_rva": hex(rva), "instructions":
                                   [f"0x{x.address:x} {x.mnemonic} {x.op_str}" for x in instructions[max(0, at-5):at+3]]})
                if len(result) == limit:
                    return result
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", type=Path, required=True)
    parser.add_argument("--dump", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, default=Path("catalog/native/native-runtime.json"))
    parser.add_argument("--schema", type=Path, default=Path("catalog/native/native-schema.json"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[3]
    if not args.output.resolve().is_relative_to(repo / "artifacts"):
        parser.error("evidence output must be below repository artifacts")
    lines = args.dump.read_text(encoding="utf-8").splitlines()
    player, game = Image(args.game/"UnityPlayer.dll"), Image(args.game/"GameAssembly.dll")
    groups = {name: [] for name in ("Initialization", "EarlyUpdate", "PreUpdate", "PostLateUpdate")}
    for index in range(274598, 275225):
        match = re.match(r"public struct (\w+)\.(\w+) ", lines[index])
        if match and match[1] in groups:
            groups[match[1]].append({"type": match[1]+"."+match[2], "line": index+1, "declaration": lines[index]})
    assert {name: len(items) for name, items in groups.items()} == {
        "Initialization": 6, "EarlyUpdate": 31, "PreUpdate": 9, "PostLateUpdate": 42,
    }, "the supported dump's stage leaf types changed"
    quit_signatures = []
    for rva, name in ((0x1DD0930, "add_quitting"), (0x1DD0CB0, "remove_quitting"), (0x1DD0500, "Internal_ApplicationQuit")):
        matches = [{"line": index+2, "declaration": lines[index+1], "rva": hex(rva)}
                   for index in range(259518, 259651)
                   if f"RVA: 0x{rva:X} " in lines[index] and name+"(" in lines[index+1]]
        assert len(matches) == 1, f"quit member {name} not verified in dump"
        quit_signatures.extend(matches)
    evidence = {
        "inputs": {str(path): digest(path) for path in (args.game/"UnityPlayer.dll", args.game/"GameAssembly.dll", args.dump, args.runtime, args.schema)},
        "evidence_level": "offline_binary_dump_runtime_schema_inspection;no_loaded_game_or_hook_evidence",
        "player_loop_layout": [{"line": i+1, "text": line} for i, line in enumerate(lines) if 274553 <= i+1 <= 274588],
        "phase_types": {phase: [{"line": i+1, "text": line} for i, line in enumerate(lines) if f"struct {phase} " in line] for phase in PHASES},
        "aggregate_phase_types": groups,
        "aggregate_phase_exclusions": ["PreUpdate.AIUpdate", "PostLateUpdate.UpdateAllRenderers", "PostLateUpdate.FinishFrameRendering"],
        "aggregate_phase_boundary": "remaining leaf original-call sums; no parent/dispatch timing; 85 added leaf symbols plus 9 existing; runtime slot existence/ABI gates still required",
        "quit_members": quit_signatures,
        "quit_native_callback": {"rva": "0x1DD0500", "required_by_native_code": lines[259644:259647],
                                 "instructions": game.instructions(0x1DD0500, 0x50)},
        "loop_icalls": {name: player.icall("UnityEngine.LowLevel.PlayerLoop::"+name) for name in ("GetCurrentPlayerLoopInternal", "GetDefaultPlayerLoopInternal")},
        "loop_export_conversion": player.instructions(0x909DB0, 0x141),
        "loop_native_descriptor_copy": player.instructions(0x78E720, 0x67),
        "loop_executor": player.instructions(0x78D9C0, 0x115),
        "array_payload_accessor": player.instructions(0x8A9AE0, 0x80),
        "input_icall": player.icall("UnityEngine.Input::GetKeyDownInt"),
        "game_entries": [],
        "field_dump_evidence": [],
        "gc_exports": {}
    }
    for name, rva in GAME:
        signatures = []
        for i, line in enumerate(lines[:-1]):
            if re.search(rf"RVA: 0x{rva:X}\b", line):
                signatures.append({"line": i+2, "declaration": lines[i+1]})
        abi = "void(this_pointer,old_state_pointer,new_state_pointer,isRecall_bool,MethodInfo_pointer)" if name.endswith(".OnStateChange") else \
            "void(this_pointer,EnemyGroup_pointer,isRecall_bool,MethodInfo_pointer)" if name.endswith(".RegisterGroup") else "void(this_pointer,MethodInfo_pointer)"
        evidence["game_entries"].append({"name": name, "rva": hex(rva), "expected_abi": abi,
                                         "dump_signatures": signatures, "entry_bytes": game.pe.get_data(rva, 16).hex(),
                                         "instructions": game.instructions(rva, 100), "direct_callers": game.callers(rva)})
    ranges = ((558109,558121), (583463,583494), (656739,656750), (650535,650557), (680536,680559),
              (684324,684353), (698839,698865), (663575,663598), (663705,663728), (610099,610115), (620708,620717),
              (146161,146169), (610077,610083))
    for start, end in ranges:
        evidence["field_dump_evidence"].extend({"line": i+1, "text": lines[i]} for i in range(start-1,end) if " // 0x" in lines[i] or "class " in lines[i] or "struct " in lines[i])
    for export in game.pe.DIRECTORY_ENTRY_EXPORT.symbols:
        if export.name and export.name.decode() in ("il2cpp_gc_collect", "il2cpp_gc_get_used_size", "il2cpp_gc_get_heap_size", "il2cpp_field_static_get_value"):
            name = export.name.decode(); rva = export.address
            evidence["gc_exports"][name] = {"rva": hex(rva), "instructions": game.instructions(rva, 5)}
    evidence["explicit_gc_impl"] = game.instructions(0xECBE0, 5)+game.instructions(0xD2620, 160)
    evidence["fog_application"] = game.instructions(0x12E0230, 0x410)
    # The outer graphics dispatch still invokes a patched wait entry. No DXGI
    # or inner-wait bytes are required by the new collector installation.
    outer = player.instructions(0x4F1B70, 0xB0)
    assert any("call 0x180500850" in line for line in outer), "graphics frame-end no longer encloses the inspected wait"
    expected = bytes.fromhex("40534883ec2080b9cc1d000000")
    assert player.pe.get_data(0x4F1B70, len(expected)) == expected, "graphics frame-end prologue differs"
    evidence["graphics_frame_end"] = {
        "entry_rva": "0x4F1B70", "expected_abi": "void(native_graphics_device_pointer)",
        "entry_bytes": player.pe.get_data(0x4F1B70, 16).hex(), "instructions": outer,
        "vtable_reference_rva": "0x14AC828",
        "vtable_entry": hex(struct.unpack("<Q", player.pe.get_data(0x14AC828, 8))[0]),
        "inner_wait_entry_rva": "0x500850", "inner_wait_instructions": player.instructions(0x500850, 0x98),
        "scope": "inclusive graphics frame-end dispatch; existing inner wait/DXGI hooks execute unchanged; main-thread wall minus calibrated thread CPU; includes scheduling inside scope; not GPU execution"
    }
    assert struct.unpack("<Q", player.pe.get_data(0x14AC828, 8))[0] == player.base + 0x4F1B70
    begin = player.instructions(0x4EA210, 0xB0)
    assert any("call 0x180500850" in line for line in begin), "graphics frame-begin no longer encloses the inspected wait"
    assert struct.unpack("<Q", player.pe.get_data(0x14AC820, 8))[0] == player.base + 0x4EA210
    evidence["graphics_frame_begin"] = {
        "entry_rva": "0x4EA210", "expected_abi": "void(native_graphics_device_pointer)",
        "entry_bytes": player.pe.get_data(0x4EA210, 16).hex(), "instructions": begin,
        "vtable_reference_rva": "0x14AC820", "inner_wait_entry_rva": "0x500850",
        "scope": "covers the other verified caller of the present wait; begin/end nested scopes counted once"
    }
    runtime = json.loads(args.runtime.read_text(encoding="utf-8"))["types"]
    schema = json.loads(args.schema.read_text(encoding="utf-8"))
    assert "TriggerAlarmOnActivate" in runtime["ChainedPuzzleDataBlock"]["fields"]
    assert "OnStateChange" in runtime["ChainedPuzzleInstance"]["methods"]
    assert "m_enemiesInNode" in runtime["AIG_CourseNode"]["fields"]
    assert "RegisterGroup" in runtime["SurvivalWave"]["methods"]
    assert any(field["name"] == "TriggerAlarmOnActivate" for field in schema["types"]["ChainedPuzzle"]["fields"])
    evidence["scene_validation"] = {
        "alarm_data_type": "Modules-ASM.dll:GameData.ChainedPuzzleDataBlock; loader assembly confirmed from local interop metadata; not GameData-ASM.dll",
        "alarm_data_offset": "0x40", "alarm_flag_offset": "0x48",
        "alarm_state": "pChainedPuzzleState.isActive at payload offset 4; boxed field offset 0x14; value size 6; runtime validates before postfix installation",
        "area_failure": "open List<T> fields have offset 0 in dump; prior validation incorrectly required 0x18",
        "area_fix": "room size/player height validation is independent of node enemy count; il2cpp_field_get_type(AIG_CourseNode.m_enemiesInNode) -> il2cpp_class_from_type -> actual closed List<EnemyAgent>._size; offset 0x18 must be verified in loaded metadata for node count only",
        "runtime_namespace": runtime["ChainedPuzzleDataBlock"].get("ns"),
        "native_members_checked": ["ChainedPuzzleDataBlock.TriggerAlarmOnActivate", "ChainedPuzzleInstance.OnStateChange", "AIG_CourseNode.m_enemiesInNode"]
    }
    evidence["wave_client_boundary"] = {
        "StartWave_direct_callers": game.callers(0x13F5A10, 8),
        "replication_capture": game.instructions(0x13F5E20, 0x70),
        "client_group_registration": game.instructions(0x13F4C30, 0xB0),
        "group_registration_callers": game.callers(0x13F4C30, 5),
        "group_spawn_signature": [{"line": i+2, "declaration": lines[i+1]} for i, line in enumerate(lines[:-1]) if re.search(r"RVA: 0x1574F30\b", line)],
        "coverage": "StartWave is called by MastermindUpdate; local authority waves only. RegisterGroup is called by EnemyGroup.OnSpawn on host/client; native callers distinguish recall=true from a new group=false. Replication snapshots carry WaveState but cannot prove every client wave boundary is received. No client WaveStart is inferred from enemy counts or OnSpawn."
    }
    evidence["unavailable_physics_late"] = "No PreLateUpdate.PhysicsLateUpdate 3D phase type in dump; 2D phase is not a substitute."
    args.output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(f"Inspected {len(evidence['game_entries'])} game entries and {len(PHASES)} native loop types; evidence {args.output}; game not launched.")


if __name__ == "__main__":
    main()
