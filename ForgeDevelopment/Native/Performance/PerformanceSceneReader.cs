using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ForgeDevelopment.Native;

internal readonly record struct PerformanceScene(int State, int Area, int AreaSize,
    int NodeEnemies, int CloseEnemies, int AggressiveEnemies, float FogDensity, float FogAltitude, float PlayerY = float.NaN);

// Cache FieldInfo pointers and validate offsets against both native metadata and
// the dump before reading any object. Reading raw primitives avoids allocating
// IL2CPP object wrappers on every frame. No enumeration, raycast or scene walk.
internal sealed class PerformanceSceneReader
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StaticRead(IntPtr field, out IntPtr value);
    private StaticRead _readStatic = StaticGet;
    private IntPtr _stateCurrent, _playerCurrent, _enemySummary, _fogCurrent;
    private bool _state, _area, _enemies, _fog;
    private bool _nodeListFailed;
    private static bool _alarm;
    private readonly List<string> _fieldFailures = new();
    internal string Reason { get; private set; } = "scene_reader_not_initialized";
    internal bool AlarmFieldsVerified => _alarm;
    internal void Initialize()
    {
        _nodeListFailed = true;
        _alarm = false;
        try
        {
            if (NativeModuleFingerprint.Verified("GameAssembly.dll", NativeModuleFingerprint.GameSha256, out var reason) == IntPtr.Zero)
            { Reason = reason; return; }
            _stateCurrent = Field("Modules-ASM.dll", "", "GameStateManager", "Current", 0);
            _state = Check("Modules-ASM.dll", "", "GameStateManager", "m_currentStateName", 0x18);
            _playerCurrent = Field("Modules-ASM.dll", "Player", "PlayerManager", "Current", 0);
            _area = Check("Modules-ASM.dll", "Player", "PlayerManager", "m_localPlayerAgentInLevel", 0x28)
                && Check("Modules-ASM.dll", "Player", "PlayerAgent", "m_courseNode", 0x120)
                && Check("Modules-ASM.dll", "Agents", "Agent", "m_position", 0x48)
                && Check("Modules-ASM.dll", "AIGraph", "AIG_CourseNode", "m_area", 0x30)
                && Check("Modules-ASM.dll", "LevelGeneration", "LG_Area", "m_size", 0x4C)
                && Check("Modules-ASM.dll", "LevelGeneration", "LG_Area", "<UID>k__BackingField", 0x98);
            _nodeListFailed = !ClosedEnemyListCountVerified();
            _enemySummary = Field("Modules-ASM.dll", "Enemies", "EnemyUpdateManager", "Summary", 0x18);
            // Resolve nested metadata through its declaring class.
            var outer = IL2CPP.GetIl2CppClass("Modules-ASM.dll", "Enemies", "EnemyUpdateManager");
            var summary = IL2CPP.GetIl2CppNestedType(outer, "EnemySummaryData");
            _enemies = summary != IntPtr.Zero && Offset(summary, "m_closeEnemyCount") == 0x10 && Offset(summary, "m_agressiveEnemyCount") == 0x18;
            _fogCurrent = Field("Modules-ASM.dll", "", "LocalPlayerAgentSettings", "Current", 0);
            _fog = Check("Modules-ASM.dll", "", "LocalPlayerAgentSettings", "m_fogSettings", 0x20)
                && Check("Modules-ASM.dll", "", "FogSettings", "FogDensity", 0x10)
                && Check("Modules-ASM.dll", "", "FogSettings", "DensityHeightAltitude", 0x1C);
            _alarm = Check("Modules-ASM.dll", "ChainedPuzzles", "ChainedPuzzleInstance", "<Data>k__BackingField", 0x40)
                && Check("Modules-ASM.dll", "GameData", "ChainedPuzzleDataBlock", "<TriggerAlarmOnActivate>k__BackingField", 0x48);
            Reason = "state=" + _state + ";area=" + _area + ";node_count=" + !_nodeListFailed + ";enemy_summary=" + _enemies + ";fog=" + _fog + ";alarm=" + _alarm
                + ";field_failures=" + (_fieldFailures.Count == 0 ? "none" : string.Join("|", _fieldFailures));
        }
        catch (Exception error) { Reason = "scene_field_validation_failed:" + error.GetBaseException().Message; }
    }
    private IntPtr Field(string assembly, string ns, string type, string name, uint expected)
    {
        var klass = IL2CPP.GetIl2CppClass(assembly, ns, type);
        if (klass == IntPtr.Zero) { _fieldFailures.Add(assembly + ":" + ns + "." + type + ":class_missing"); return IntPtr.Zero; }
        var field = IL2CPP.GetIl2CppField(klass, name);
        var actual = field == IntPtr.Zero ? uint.MaxValue : IL2CPP.il2cpp_field_get_offset(field);
        if (actual == expected) return field;
        _fieldFailures.Add(type + "." + name + ":expected_0x" + expected.ToString("X") + ";actual_0x" + actual.ToString("X"));
        return IntPtr.Zero;
    }
    private bool Check(string assembly, string ns, string type, string name, uint offset) => Field(assembly, ns, type, name, offset) != IntPtr.Zero;
    private bool ClosedEnemyListCountVerified()
    {
        // An open List<T> has uninstantiated offsets (0 in the dump). Validate
        // the actual closed List<EnemyAgent> declared by m_enemiesInNode.
        var field = Field("Modules-ASM.dll", "AIGraph", "AIG_CourseNode", "m_enemiesInNode", 0x78);
        var list = field == IntPtr.Zero ? IntPtr.Zero : ClassFromType(FieldType(field));
        var offset = list == IntPtr.Zero ? uint.MaxValue : Offset(list, "_size");
        if (offset == 0x18) return true;
        _fieldFailures.Add("AIG_CourseNode.m_enemiesInNode:closed_List_EnemyAgent_size_offset_0x" + offset.ToString("X"));
        return false;
    }
    // The offset of a named field, uint.MaxValue when the field is missing (never il2cpp_field_get_offset on a null field).
    internal static uint Offset(IntPtr klass, string name)
    {
        var field = IL2CPP.GetIl2CppField(klass, name);
        return field == IntPtr.Zero ? uint.MaxValue : IL2CPP.il2cpp_field_get_offset(field);
    }
    private IntPtr Current(IntPtr field)
    {
        if (field == IntPtr.Zero) return IntPtr.Zero;
        _readStatic(field, out var value); return value;
    }
    internal PerformanceScene Read()
    {
        var stateObject = _state ? Current(_stateCurrent) : IntPtr.Zero;
        var state = stateObject != IntPtr.Zero ? Marshal.ReadByte(stateObject, 0x18) : -1;
        var areaId = -1; var areaSize = -1; var nodeEnemies = -1; var close = -1; var aggressive = -1;
        var density = float.NaN; var altitude = float.NaN; var playerY = float.NaN;
        if (state != 10) return new(state, areaId, areaSize, nodeEnemies, close, aggressive, density, altitude);
        if (_area)
        {
            var manager = Current(_playerCurrent);
            var player = manager != IntPtr.Zero ? Marshal.ReadIntPtr(manager, 0x28) : IntPtr.Zero;
            if (player != IntPtr.Zero) playerY = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(player, 0x4C));
            var node = player != IntPtr.Zero ? Marshal.ReadIntPtr(player, 0x120) : IntPtr.Zero;
            var area = node != IntPtr.Zero ? Marshal.ReadIntPtr(node, 0x30) : IntPtr.Zero;
            if (area != IntPtr.Zero) { areaId = Marshal.ReadInt32(area, 0x98); areaSize = Marshal.ReadInt32(area, 0x4C); }
            var enemies = !_nodeListFailed && node != IntPtr.Zero ? Marshal.ReadIntPtr(node, 0x78) : IntPtr.Zero;
            if (enemies != IntPtr.Zero && !_nodeListFailed) nodeEnemies = Marshal.ReadInt32(enemies, 0x18);
        }
        if (_enemies)
        {
            var summary = Current(_enemySummary);
            if (summary != IntPtr.Zero) { close = Marshal.ReadInt32(summary, 0x10); aggressive = Marshal.ReadInt32(summary, 0x18); }
        }
        if (_fog)
        {
            var local = Current(_fogCurrent);
            if (local != IntPtr.Zero)
            {
                density = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(local, 0x20));
                altitude = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(local, 0x2C));
            }
        }
        return new(state, areaId, areaSize, nodeEnemies, close, aggressive, density, altitude, playerY);
    }
    internal static bool IsAlarmPuzzle(IntPtr puzzle)
    {
        if (!_alarm || puzzle == IntPtr.Zero) return false;
        var data = Marshal.ReadIntPtr(puzzle, 0x40);
        return data != IntPtr.Zero && Marshal.ReadByte(data, 0x48) != 0;
    }
    [DllImport("GameAssembly", EntryPoint = "il2cpp_field_static_get_value", CallingConvention = CallingConvention.Cdecl)]
    private static extern void StaticGet(IntPtr field, out IntPtr value);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_field_get_type", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FieldType(IntPtr field);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_class_from_type", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ClassFromType(IntPtr type);
}
