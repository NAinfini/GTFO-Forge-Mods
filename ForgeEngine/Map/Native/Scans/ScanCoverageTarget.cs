using System;
using System.Collections.Generic;
using ChainedPuzzles;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using LevelGeneration;
using UnityEngine;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>Derived geometry and speed inputs, never another scan progression or scheduler.</summary>
internal sealed class ScanCoverageTarget
{
    internal readonly CP_Bioscan_Core Core;
    internal readonly IntPtr CorePointer;
    internal readonly CP_PlayerScanner Scanner;
    internal readonly ScanCoverageRow Row;
    private readonly double _roomYaw;
    private readonly ScanMembership? _membership;
    private readonly List<ScanBeatFrame> _frames = new();
    private readonly List<ScanSpeedInput> _detachedInputs = new();
    private readonly PlayerAgent?[] _filteredPlayers = new PlayerAgent?[ScanCoverage.PlayerSlotLimit];
    private readonly bool[] _filteredRequired = new bool[ScanCoverage.RequiredItemLimit];
    private ScanSpeedInput? _inputOwner;
    private readonly float? _authorityRadius, _authorityRadiusSquared;
    private readonly PlayerRequirement? _authorityRequirement;
    private readonly bool _managesSpeeds;
    private float _beforeAuthorityRadius, _beforeAuthorityRadiusSquared;
    private PlayerRequirement _beforeAuthorityRequirement;
    private bool _ownsAuthorityRadius, _ownsAuthorityRadiusSquared, _ownsAuthorityRequirement;
    private int _depth;
    private long _generation;
    private bool _released;
    internal ScanCoverageTarget(ScanCoverageRow row, CP_Bioscan_Core core, CP_PlayerScanner scanner, double roomYaw,
        ScanMembership? membership = null, ScanSpeedInput? authoredSpeeds = null, float? authorityRadius = null,
        float? authorityRadiusSquared = null)
    {
        Row = row; Core = core; CorePointer = core.Pointer; Scanner = scanner; _roomYaw = roomYaw; _membership = membership;
        _inputOwner = authoredSpeeds; _authorityRadius = authorityRadius; _authorityRadiusSquared = authorityRadiusSquared;
        _authorityRequirement = row.Players is { } players ? (PlayerRequirement)players.NativeRequirement : null;
        _managesSpeeds = authoredSpeeds != null || membership != null || row.Shape != null || row.TeamSpeeds != null || row.Players != null || row.RequiredItems != null;
    }
    private Il2CppStructArray<float> EnsurePrivateInput()
    {
        var current = Scanner.m_scanSpeeds;
        if (current == null || current.Length != 4) throw new InvalidOperationException("the native speed table is unreadable");
        Span<float> source = stackalloc float[4];
        if (_inputOwner != null && current.Pointer == _inputOwner.Table.Pointer)
        {
            // An active frame owns the temporary uniform values. Outside a frame, any changed slot is a late
            // native input; unchanged slots retain their remembered pre-authoring value so factors are not doubled.
            if (_depth != 0 || _inputOwner.MatchesWritten()) return current;
            for (var i = 0; i < source.Length; i++) source[i] = _inputOwner.BaseAt(i);
        }
        else for (var i = 0; i < source.Length; i++) source[i] = current[i];
        // A peer promoted to host reaches this path on its first native scan beat. Derive the same authored table
        // the original host installed. A replaced array or a late write into the same array is a new native input.
        var values = new float[4];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Row.Players?.Allows(i + 1) == false ? 0 : source[i] * (float)(Row.TeamSpeeds?[i] ?? 1);
            if (!float.IsFinite(values[i])) throw new InvalidOperationException("an authored speed is not a finite Single");
        }
        var owned = new ScanSpeedInput(Scanner, values);
        _detachedInputs.Add(owned); owned.Apply(); _inputOwner = owned; return owned.Table;
    }
    // Required items make the native beat read speed slot 0 whatever the head count, and a player rule answers "not
    // allowed" with speed zero rather than "cannot advance", so both go through the beat frame.
    internal bool FiltersMembers => Row.Shape != null || _membership != null || Row.RequiredItems != null || (Row.Players != null && Row.ReduceSpeed > 0);
    internal void EnsureAuthorityInputs()
    {
        if (_released) throw new ObjectDisposedException(nameof(ScanCoverageTarget));
        if (_managesSpeeds) EnsurePrivateInput();
        if (_depth == 0 && _authorityRequirement is { } requirement)
        {
            if (!_ownsAuthorityRequirement || Scanner.m_playerRequirement != requirement)
                _beforeAuthorityRequirement = Scanner.m_playerRequirement;
            _ownsAuthorityRequirement = true; Scanner.m_playerRequirement = requirement;
        }
        if (_authorityRadius is { } radius)
        {
            if (!_ownsAuthorityRadius || !Scanner.m_scanRadius.Equals(radius)) _beforeAuthorityRadius = Scanner.m_scanRadius;
            _ownsAuthorityRadius = true; Scanner.m_scanRadius = radius;
        }
        if (_authorityRadiusSquared is { } radiusSquared)
        {
            if (!_ownsAuthorityRadiusSquared || !Scanner.m_scanRadiusSqr.Equals(radiusSquared))
                _beforeAuthorityRadiusSquared = Scanner.m_scanRadiusSqr;
            _ownsAuthorityRadiusSquared = true; Scanner.m_scanRadiusSqr = radiusSquared;
        }
    }
    internal bool Contains(Vector3 p)
    {
        var centre = Scanner.transform.position;
        return Row.Shape?.Contains(p.x - centre.x, p.y - centre.y, p.z - centre.z, _roomYaw)
            ?? (p - centre).sqrMagnitude < Scanner.m_scanRadiusSqr;
    }
    internal ScanNativeBeat BeforeNativeBeat(out string? refusalCode, out string? refusalDetail)
    {
        if (_released) throw new ObjectDisposedException(nameof(ScanCoverageTarget));
        EnsureAuthorityInputs();
        var readable = TryMemberState(out var count, out var teamCount, out var requiredAllowed, out refusalCode, out refusalDetail);
        var requirement = _depth > 0 && _frames[_depth - 1].OwnsCurrentRequirement ? _frames[_depth - 1].HudRequirement : Scanner.m_playerRequirement;
        var players = Row.Players;
        var allowed = readable && requiredAllowed && count > 0 && (players != null
            ? players.Allows(count) && (players.Rule != ScanPlayerRule.Team || count == teamCount)
            : requirement == PlayerRequirement.All ? count == teamCount : requirement != PlayerRequirement.Solo || count == 1);
        var table = _inputOwner!.Table;
        var speed = allowed ? SourceSpeed(table, count - 1) : 0f;
        if (_depth == _frames.Count) _frames.Add(new ScanBeatFrame(this, Scanner, _depth));
        var frame = _frames[_depth];
        var generation = ++_generation;
        frame.Prepare(generation, table, speed, requirement, refusalCode, refusalDetail, !allowed);
        _depth++;
        var token = new ScanNativeBeat(frame, generation);
        try { frame.Apply(); return token; } catch { token.Dispose(); throw; }
    }
    private bool TryMemberState(out int count, out int teamCount, out bool requiredAllowed,
        out string? refusalCode, out string? refusalDetail)
    {
        count = 0; teamCount = 0; requiredAllowed = true; refusalCode = null; refusalDetail = null;
        try
        {
            var readable = Players.Readable;
            if (!readable) return Refused(ScanCoverage.MemberUnreadableCode, "the native player table is unreadable",
                out refusalCode, out refusalDetail);
            for (var i = 0; i < Players.Count; i++)
            {
                var player = Players.At(i);
                if (player == null) return Refused(ScanCoverage.MemberUnreadableCode, "player " + i + " does not read",
                    out refusalCode, out refusalDetail);
                if (player.WasCollected) continue;
                teamCount++;
                if (_membership != null ? _membership.Owns(player.CourseNode) : Contains(player.Position)) count++;
            }
            if (count > ScanCoverage.PlayerSlotLimit)
                return Refused(ScanCoverage.MemberLimitCode, count + " players stand in this scan; the replicated state carries "
                    + ScanCoverage.PlayerSlotLimit + " slots", out refusalCode, out refusalDetail);
            if (!Core.m_reqItemsEnabled) return true;
            var items = Core.m_reqItems;
            if (items == null) return Refused(ScanCoverage.MemberUnreadableCode, "the required-item table does not read",
                out refusalCode, out refusalDetail);
            if (items.Count > ScanCoverage.RequiredItemLimit)
                return Refused(ScanCoverage.MemberLimitCode, "the scan requires " + items.Count + " items; the replicated state carries "
                    + ScanCoverage.RequiredItemLimit + " bits", out refusalCode, out refusalDetail);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.WasCollected) return Refused(ScanCoverage.MemberUnreadableCode, "required item " + i + " does not read",
                    out refusalCode, out refusalDetail);
                if (!(_membership != null ? _membership.OwnsItem(item) : Contains(ItemPosition(item)))) requiredAllowed = false;
            }
            return true;
        }
        catch (Exception error)
        {
            return Refused(ScanCoverage.MemberUnreadableCode, error.GetType().Name + ": " + error.Message,
                out refusalCode, out refusalDetail);
        }
    }
    internal bool TryFilterMembers(Il2CppSystem.Collections.Generic.List<PlayerAgent> source,
        Il2CppStructArray<bool>? nativeRequired, out int playerCount, out int requiredCount,
        out string? refusalCode, out string? refusalDetail)
    {
        playerCount = 0; requiredCount = 0;
        if (TryCurrentRefusal(out refusalCode, out refusalDetail)) return false;
        try
        {
            for (var i = 0; i < source.Count; i++)
            {
                var player = source[i];
                if (player == null || player.WasCollected) return Refused(ScanCoverage.MemberUnreadableCode, "player " + i + " does not read",
                    out refusalCode, out refusalDetail);
                if (Contains(player.Position))
                {
                    if (playerCount < _filteredPlayers.Length) _filteredPlayers[playerCount] = player;
                    playerCount++;
                }
            }
            if (playerCount > ScanCoverage.PlayerSlotLimit)
                return Refused(ScanCoverage.MemberLimitCode, playerCount + " players stand in this scan; the replicated state carries "
                    + ScanCoverage.PlayerSlotLimit + " slots", out refusalCode, out refusalDetail);
            if (!Core.m_reqItemsEnabled) { refusalCode = null; refusalDetail = null; return true; }
            var items = Core.m_reqItems;
            if (items == null) return Refused(ScanCoverage.MemberUnreadableCode, "the required-item table does not read",
                out refusalCode, out refusalDetail);
            if (items.Count > ScanCoverage.RequiredItemLimit)
                return Refused(ScanCoverage.MemberLimitCode, "the scan requires " + items.Count + " items; the replicated state carries "
                    + ScanCoverage.RequiredItemLimit + " bits", out refusalCode, out refusalDetail);
            if (nativeRequired == null || nativeRequired.Length != items.Count)
                return Refused("scan-coverage-required-item-mismatch", "the scan publishes "
                    + (nativeRequired == null ? "no" : nativeRequired.Length.ToString()) + " required-item flag(s) for " + items.Count + " required item(s)",
                    out refusalCode, out refusalDetail);
            requiredCount = items.Count;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.WasCollected) return Refused(ScanCoverage.MemberUnreadableCode, "required item " + i + " does not read",
                    out refusalCode, out refusalDetail);
                _filteredRequired[i] = nativeRequired[i] && Contains(ItemPosition(item));
            }
            refusalCode = null; refusalDetail = null; return true;
        }
        catch (Exception error)
        {
            return Refused(ScanCoverage.MemberUnreadableCode, error.GetType().Name + ": " + error.Message,
                out refusalCode, out refusalDetail);
        }
    }
    internal bool TryCurrentRefusal(out string? code, out string? detail)
    {
        if (_depth != 0 && _frames[_depth - 1].RefusalCode is { } current)
        { code = current; detail = _frames[_depth - 1].RefusalDetail; return true; }
        code = null; detail = null; return false;
    }
    private static bool Refused(string code, string detail, out string? refusalCode, out string? refusalDetail)
    { refusalCode = code; refusalDetail = detail; return false; }
    internal PlayerAgent FilteredPlayer(int index) => _filteredPlayers[index]!;
    internal bool FilteredRequired(int index) => _filteredRequired[index];
    private float SourceSpeed(Il2CppStructArray<float> table, int index)
    {
        var value = table[index];
        // Peel only this scanner's active temporary writes. A native edit to a slot is a new input; another
        // table is a new input too. No construction-time speed snapshot is used by later beats.
        for (var i = _depth - 1; i >= 0; i--)
            if (_frames[i].Table.Pointer == table.Pointer && value.Equals(_frames[i].Speed)) value = _frames[i].Before[index];
        return value;
    }
    internal void ReleaseFrame(ScanBeatFrame frame, long generation)
    {
        if (!frame.Active || frame.Generation != generation) return;
        frame.ReleaseRequested = true;
        Exception? failure = null;
        while (_depth > 0 && _frames[_depth - 1].ReleaseRequested)
        {
            var current = _frames[_depth - 1];
            try { current.Restore(); current.Active = false; _depth--; }
            catch (Exception error) { failure ??= error; break; }
        }
        if (failure != null) throw failure;
    }
    internal void ReleaseNativeInputs()
    {
        _released = true;
        Exception? failure = null;
        while (_depth != 0)
        {
            try { ReleaseFrame(_frames[_depth - 1], _frames[_depth - 1].Generation); }
            catch (Exception error) { failure = error; break; }
        }
        if (failure != null) throw failure;
        if (!Scanner.WasCollected && Scanner != null)
        {
            try
            {
                if (_ownsAuthorityRequirement && _authorityRequirement is { } requirement
                    && Scanner.m_playerRequirement == requirement) Scanner.m_playerRequirement = _beforeAuthorityRequirement;
                if (_ownsAuthorityRadius && _authorityRadius is { } radius && Scanner.m_scanRadius.Equals(radius))
                    Scanner.m_scanRadius = _beforeAuthorityRadius;
                if (_ownsAuthorityRadiusSquared && _authorityRadiusSquared is { } radiusSquared
                    && Scanner.m_scanRadiusSqr.Equals(radiusSquared)) Scanner.m_scanRadiusSqr = _beforeAuthorityRadiusSquared;
            }
            catch (Exception error) { failure ??= error; }
        }
        for (var i = _detachedInputs.Count - 1; i >= 0; i--)
            try { _detachedInputs[i].Dispose(); _detachedInputs.RemoveAt(i); } catch (Exception error) { failure ??= error; }
        if (failure != null) throw failure;
    }
    internal void RestoreRequirementForHud()
    {
        if (_depth != 0) _frames[_depth - 1].RestoreRequirementForHud();
    }
    internal static Vector3 ItemPosition(iWardenObjectiveItem item)
        => item.PickupItemStatus == ePickupItemStatus.PickedUp && item.PickedUpByPlayer is { } holder && !holder.WasCollected
            ? holder.Position : item.transform.position;

    internal static Mesh BuildMesh(ScanShape shape)
    {
        var count = shape.Outline.Count;
        var vertices = new Il2CppStructArray<Vector3>(count * 2);
        var uv = new Il2CppStructArray<Vector2>(count * 2);
        var triangles = new Il2CppStructArray<int>(count * 6);
        for (var i = 0; i < count; i++)
        {
            var point = shape.Outline[i]; var x = (float)(point.X / shape.Radius); var z = (float)(point.Z / shape.Radius);
            vertices[2 * i] = new Vector3(x, 0, z); vertices[2 * i + 1] = new Vector3(x * .96f, 0, z * .96f);
            uv[2 * i] = new Vector2(x * .5f + .5f, z * .5f + .5f); uv[2 * i + 1] = new Vector2(x * .48f + .5f, z * .48f + .5f);
            var a = 2 * i; var b = 2 * ((i + 1) % count); var at = 6 * i;
            triangles[at] = a; triangles[at + 1] = b + 1; triangles[at + 2] = b;
            triangles[at + 3] = a; triangles[at + 4] = a + 1; triangles[at + 5] = b + 1;
        }
        var mesh = new Mesh { name = "Forge scan " + shape.Kind, vertices = vertices, triangles = triangles, uv = uv };
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
    }
}


/// <summary>A concrete value-type Harmony state; generation prevents a late finalizer releasing a reused frame.</summary>
internal readonly struct ScanNativeBeat : IDisposable
{
    private readonly ScanBeatFrame _frame;
    private readonly long _generation;
    internal ScanNativeBeat(ScanBeatFrame frame, long generation) { _frame = frame; _generation = generation; }
    public void Dispose() => _frame?.Owner.ReleaseFrame(_frame, _generation);
}

internal readonly struct ScanSpeedValues
{
    private readonly float _a, _b, _c, _d;
    internal ScanSpeedValues(Il2CppStructArray<float> table) { _a = table[0]; _b = table[1]; _c = table[2]; _d = table[3]; }
    internal float this[int index] => index switch { 0 => _a, 1 => _b, 2 => _c, 3 => _d, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
}

/// <summary>Allocated only when nesting first reaches this depth. A completed frame carries no outstanding token.</summary>
internal sealed class ScanBeatFrame
{
    internal readonly ScanCoverageTarget Owner;
    private readonly CP_PlayerScanner _scanner;
    internal Il2CppStructArray<float> Table = null!;
    internal ScanSpeedValues Before;
    internal float Speed;
    internal long Generation;
    internal bool Active, ReleaseRequested;
    internal PlayerRequirement HudRequirement;
    internal string? RefusalCode, RefusalDetail;
    private PlayerRequirement _beforeRequirement, _writtenRequirement;
    private int _beforeCount;
    private bool _ownsRequirement;
    private bool _losing, _ownsReduce;
    internal ScanBeatFrame(ScanCoverageTarget owner, CP_PlayerScanner scanner, int depth) { Owner = owner; _scanner = scanner; }
    internal bool OwnsCurrentRequirement => _ownsRequirement && _scanner.m_playerRequirement == _writtenRequirement;
    internal void Prepare(long generation, Il2CppStructArray<float> table, float speed, PlayerRequirement hudRequirement,
        string? refusalCode, string? refusalDetail, bool losing)
    {
        _losing = losing; _ownsReduce = false;
        Generation = generation; Table = table; Before = new ScanSpeedValues(table); Speed = speed;
        _beforeRequirement = _scanner.m_playerRequirement; HudRequirement = hudRequirement; _beforeCount = _scanner.m_lastPlayerCount;
        RefusalCode = refusalCode; RefusalDetail = refusalDetail;
        _ownsRequirement = false; Active = true; ReleaseRequested = false;
    }
    internal void Apply()
    {
        for (var i = 0; i < 4; i++) Table[i] = Speed;
        _writtenRequirement = PlayerRequirement.None; _ownsRequirement = true; _scanner.m_playerRequirement = _writtenRequirement;
        _scanner.m_lastPlayerCount = -1;
        // The native loss only runs when its own gate finds nobody. Rules decided here apply it once themselves and
        // hold the native branch off for this beat; the native body then publishes the changed fraction.
        if (!_losing || !_scanner.m_reduceWhenNoPlayer) return;
        _ownsReduce = true; _scanner.m_reduceWhenNoPlayer = false;
        _scanner.m_scanProgression = Math.Max(0f, _scanner.m_scanProgression - _scanner.m_reduceSpeed * Clock.Delta);
    }
    internal void RestoreRequirementForHud()
    {
        if (!Active || _scanner.WasCollected) return;
        if (_ownsReduce) { _ownsReduce = false; _scanner.m_reduceWhenNoPlayer = true; }
        if (!_ownsRequirement) return;
        if (!OwnsCurrentRequirement) { _ownsRequirement = false; return; }
        _writtenRequirement = HudRequirement; _scanner.m_playerRequirement = HudRequirement;
    }
    internal void Restore()
    {
        try
        {
            // The captured private table may still have native aliases after the scanner field is replaced.
            // Clear our temporary slots on that table, never on the replacement and never by swapping it back.
            for (var i = 0; i < 4; i++) if (Table[i].Equals(Speed)) Table[i] = Before[i];
        }
        finally
        {
            if (!_scanner.WasCollected)
            {
                if (_ownsRequirement && _scanner.m_playerRequirement == _writtenRequirement) _scanner.m_playerRequirement = _beforeRequirement;
                if (_scanner.m_lastPlayerCount == -1) _scanner.m_lastPlayerCount = _beforeCount;
                if (_ownsReduce) { _ownsReduce = false; _scanner.m_reduceWhenNoPlayer = true; }
            }
        }
    }
}

/// <summary>Withdraw only writes that still have this owner's value. This is not a native-object snapshot.</summary>
internal sealed class ScanOwnedInputs : IDisposable
{
    private readonly List<Action> _undo = new();
    private bool _disposed;
    internal ScanSpeedInput? SpeedInput { get; private set; }
    internal void Track(Action undo) => _undo.Add(undo);
    /// <summary>`gone` answers whether the native object the write landed on has been destroyed since: Unity
    /// destroys a level's objects at its teardown, and a destroyed object has nothing left to withdraw from and
    /// raises a null reference to any read of it, so its receipt is dropped instead of replayed.</summary>
    internal void Write<T>(Func<T> read, Action<T> write, T value, Func<bool> gone, Func<T, T, bool>? same = null)
    {
        same ??= EqualityComparer<T>.Default.Equals;
        var before = read();
        if (same(before, value)) return;
        Track(() => { if (!gone() && same(read(), value)) write(before); });
        write(value);
    }
    internal ScanSpeedInput Speeds(CP_PlayerScanner scanner, float[] values)
    {
        var lease = new ScanSpeedInput(scanner, values);
        Track(lease.Dispose);
        lease.Apply();
        return SpeedInput = lease;
    }
    public void Dispose()
    {
        if (_disposed) return;
        Exception? failure = null;
        for (var i = _undo.Count - 1; i >= 0; i--)
            try { _undo[i](); _undo.RemoveAt(i); } catch (Exception error) { failure ??= error; }
        _disposed = _undo.Count == 0;
        if (failure != null) throw failure;
    }
}

/// <summary>A long-lived detached native array with slot-wise withdrawal; beats never swap or recycle it.</summary>
internal sealed class ScanSpeedInput : IDisposable
{
    private readonly CP_PlayerScanner _scanner;
    private readonly Il2CppStructArray<float> _original;
    private readonly Il2CppStructArray<float> _copy;
    internal Il2CppStructArray<float> Table => _copy;
    private readonly float[] _before, _values;
    private bool _disposed;
    internal ScanSpeedInput(CP_PlayerScanner scanner, float[] values)
    {
        _scanner = scanner; _original = scanner.m_scanSpeeds ?? throw new InvalidOperationException("the native speed table is unreadable");
        if (_original.Length != values.Length) throw new InvalidOperationException("the native speed table length changed");
        _values = (float[])values.Clone(); _before = new float[values.Length]; _copy = new Il2CppStructArray<float>(values.Length);
        for (var i = 0; i < values.Length; i++) { _before[i] = _original[i]; _copy[i] = values[i]; }
    }
    internal void Apply() => _scanner.m_scanSpeeds = _copy;
    internal bool MatchesWritten()
    {
        for (var i = 0; i < _values.Length; i++) if (!_copy[i].Equals(_values[i])) return false;
        return true;
    }
    internal float BaseAt(int index) => _copy[index].Equals(_values[index]) ? _before[index] : _copy[index];
    public void Dispose()
    {
        if (_disposed) return;
        // A failed slot/field setter retains this receipt for the next cleanup boundary.
        // Withdraw from the captured owned array even if native code kept an alias and replaced the field.
        for (var i = 0; i < _values.Length; i++) if (_copy[i].Equals(_values[i])) _copy[i] = _before[i];
        if (_scanner.WasCollected || _scanner == null || _scanner.m_scanSpeeds is not { } current || current.Pointer != _copy.Pointer) { _disposed = true; return; }
        var unchanged = _original.Length == current.Length;
        for (var i = 0; unchanged && i < current.Length; i++) unchanged = current[i].Equals(_original[i]);
        if (unchanged) _scanner.m_scanSpeeds = _original;
        _disposed = true;
    }
}

/// <summary>Per-graphics colour containers. Neither the preset array entries nor its mode dictionary is edited.</summary>
internal sealed class ScanColorInput : IDisposable
{
    private readonly CP_Bioscan_Graphics _graphics;
    private readonly Il2CppReferenceArray<ColorModeColor> _originalColors, _colors;
    private readonly Il2CppSystem.Collections.Generic.Dictionary<eChainedPuzzleGraphicsColorMode, Color> _originalModes, _modes;
    private readonly ColorModeColor[] _entries;
    private readonly eChainedPuzzleGraphicsColorMode[] _entryModes;
    private readonly Color[] _before, _written;
    private readonly Dictionary<eChainedPuzzleGraphicsColorMode, Color> _authored = new();
    private readonly Dictionary<eChainedPuzzleGraphicsColorMode, Color> _priorModes = new();
    private bool _disposed;
    internal ScanColorInput(CP_Bioscan_Graphics graphics, IReadOnlyList<ScanColor> colors)
    {
        _graphics = graphics; _originalColors = graphics.m_colors; _originalModes = graphics.m_colorsByMode;
        foreach (var color in colors) _authored.Add((eChainedPuzzleGraphicsColorMode)color.Mode, new Color(color.R, color.G, color.B, color.A));
        _modes = new Il2CppSystem.Collections.Generic.Dictionary<eChainedPuzzleGraphicsColorMode, Color>();
        foreach (var key in _originalModes.Keys) _modes[key] = _originalModes[key];
        foreach (var pair in _authored)
        {
            if (_originalModes.ContainsKey(pair.Key)) _priorModes.Add(pair.Key, _originalModes[pair.Key]);
            _modes[pair.Key] = pair.Value;
        }
        var absent = new HashSet<eChainedPuzzleGraphicsColorMode>(_authored.Keys);
        for (var i = 0; i < _originalColors.Length; i++) absent.Remove(_originalColors[i].mode);
        _colors = new Il2CppReferenceArray<ColorModeColor>(_originalColors.Length + absent.Count);
        _entries = new ColorModeColor[_colors.Length]; _entryModes = new eChainedPuzzleGraphicsColorMode[_colors.Length];
        _before = new Color[_colors.Length]; _written = new Color[_colors.Length];
        for (var i = 0; i < _originalColors.Length; i++)
        {
            var original = _originalColors[i];
            _before[i] = original.col;
            _entries[i] = new ColorModeColor { mode = original.mode, col = _authored.TryGetValue(original.mode, out var color) ? color : original.col };
        }
        var next = _originalColors.Length;
        foreach (var mode in absent) _entries[next++] = new ColorModeColor { mode = mode, col = _authored[mode] };
        for (var i = 0; i < _colors.Length; i++)
        {
            _colors[i] = _entries[i]; _entryModes[i] = _entries[i].mode; _written[i] = _entries[i].col;
        }
    }
    internal void Apply()
    {
        _graphics.m_colors = _colors; _graphics.m_colorsByMode = _modes;
        Refresh();
        // This runs after the graphics component's own observed Setup boundary. Do not assume SetColorMode/Setup leaves
        // assigned containers intact: verify this write before reporting the row applied.
        if (_graphics.m_colors.Pointer != _colors.Pointer || _graphics.m_colorsByMode.Pointer != _modes.Pointer)
            throw new InvalidOperationException("the native graphics replaced the instance colour containers during application");
        foreach (var pair in _authored)
            if (!_modes.ContainsKey(pair.Key) || !_modes[pair.Key].Equals(pair.Value))
                throw new InvalidOperationException("the native graphics did not retain an authored colour mode");
        for (var i = 0; i < _colors.Length; i++)
            if (_colors[i].Pointer != _entries[i].Pointer || _colors[i].mode != _entryModes[i] || !_colors[i].col.Equals(_written[i]))
                throw new InvalidOperationException("the native graphics did not retain an authored colour entry");
    }
    private eChainedPuzzleGraphicsColorMode? _pendingMode;
    private void Refresh()
    {
        var observed = _graphics.m_lastColorMode;
        var mode = observed == eChainedPuzzleGraphicsColorMode.NotSet ? (_pendingMode ?? observed) : observed;
        if (mode == eChainedPuzzleGraphicsColorMode.NotSet) return;
        _pendingMode = mode;
        Exception? failure = null;
        try
        {
            _graphics.m_lastColorMode = eChainedPuzzleGraphicsColorMode.NotSet;
            _graphics.SetColorMode(mode);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try
            {
                if (!_graphics.WasCollected && _graphics.m_lastColorMode == eChainedPuzzleGraphicsColorMode.NotSet)
                    _graphics.m_lastColorMode = mode;
                _pendingMode = null;
            }
            catch (Exception error) { failure ??= error; }
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (_graphics.WasCollected || _graphics == null) { _disposed = true; return; }
        Exception? failure = null;
        try { RestoreModes(); } catch (Exception error) { failure = error; }
        try { RestoreColors(); } catch (Exception error) { failure ??= error; }
        try { Refresh(); } catch (Exception error) { failure ??= error; }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        _disposed = true;
    }
    private void RestoreModes()
    {
        var currentModes = _modes;
        {
            foreach (var pair in _authored)
                if (currentModes.ContainsKey(pair.Key) && currentModes[pair.Key].Equals(pair.Value))
                {
                    if (_priorModes.TryGetValue(pair.Key, out var before)) currentModes[pair.Key] = before;
                    else currentModes.Remove(pair.Key);
                }
            var unchanged = currentModes.Count == _originalModes.Count;
            foreach (var key in currentModes.Keys)
                if (!_originalModes.ContainsKey(key) || !currentModes[key].Equals(_originalModes[key])) { unchanged = false; break; }
            if (unchanged && _graphics.m_colorsByMode?.Pointer == _modes.Pointer) _graphics.m_colorsByMode = _originalModes;
        }
    }
    private void RestoreColors()
    {
        var currentColors = _colors;
        {
            var retained = new List<ColorModeColor>(currentColors.Length);
            for (var i = 0; i < currentColors.Length; i++)
            {
                var entry = currentColors[i];
                var own = entry != null && entry.Pointer == _entries[i].Pointer && entry.mode == _entryModes[i] && entry.col.Equals(_written[i]);
                if (i < _originalColors.Length)
                {
                    if (own)
                    {
                        // Reuse the original entry only when its value is still the input observed by this lease.
                        // Otherwise restore our previous slot value on the private entry without changing the preset.
                        entry!.col = _before[i];
                        if (_originalColors[i].mode == _entryModes[i] && _originalColors[i].col.Equals(_before[i])) entry = _originalColors[i];
                    }
                    Il2CppArrays.Store(currentColors, i, entry);
                    retained.Add(entry!);
                }
                else if (!own) retained.Add(entry!);
            }
            var unchanged = retained.Count == _originalColors.Length;
            for (var i = 0; unchanged && i < retained.Count; i++) unchanged = retained[i]?.Pointer == _originalColors[i]?.Pointer;
            // After this lease the graphics' array is the only holder of the private entries it retains: stored with the
            // collector's write barrier, or the incremental collector frees them while the array still points at them.
            if (_graphics.m_colors?.Pointer == _colors.Pointer)
                _graphics.m_colors = unchanged ? _originalColors : Il2CppArrays.ToGameReferences(retained.ToArray());
            // A captured fixed-length array cannot be shortened for an external alias. Added slots are removed
            // from our live field above; no undocumented null/sentinel mode is written into a retained alias.
        }
    }

}

/// <summary>The authored colours and ring label of one graphics component. The colour lease is suspended around the
/// component's own native Setup and rebuilt after it. The label goes through the graphics' own `SetText`, which only
/// writes once Setup has assigned the label's text renderer; the preset's own label comes back when the row is
/// released. A single-ring preset's HUD writes its localized name into that same renderer whenever the game's texts
/// are refreshed, so the session replays the label after each such write (evidence/scan-ring-bodies-20403457.json
/// item 5).</summary>
internal sealed class ScanGraphicsState : IDisposable
{
    private readonly CP_Bioscan_Graphics _graphics;
    private readonly IReadOnlyList<ScanColor>? _colors;
    private readonly string? _text;
    private ScanColorInput? _lease;
    private string? _originalText;
    private int _setupDepth;
    private bool _disposed;
    internal ScanGraphicsState(CP_Bioscan_Graphics graphics, IReadOnlyList<ScanColor>? colors, string? text)
    { _graphics = graphics; _colors = colors; _text = text; }
    /// <summary>The renderer the label is drawn by, for matching a HUD's own text write against this ring.</summary>
    internal IntPtr LabelRenderer => _text == null || _graphics.WasCollected || _graphics.m_textMeshRenderer == null
        ? IntPtr.Zero : _graphics.m_textMeshRenderer.Pointer;
    internal void Apply()
    {
        if (_disposed || _setupDepth != 0) return;
        if (_colors != null)
        {
            var lease = new ScanColorInput(_graphics, _colors);
            _lease = lease; lease.Apply();
        }
        WriteText();
    }
    /// <summary>Writes the label again after something else rewrote the renderer it is drawn by.</summary>
    internal void WriteText()
    {
        if (_disposed || _setupDepth != 0 || _text == null || _graphics.WasCollected || _graphics.m_textMeshRenderer is not { } renderer) return;
        _originalText ??= renderer.text;
        _graphics.SetText(_text);
    }
    private void RestoreText()
    {
        if (_originalText == null || _graphics.WasCollected || _graphics.m_textMeshRenderer is not { } renderer) return;
        if (renderer.text == _text) _graphics.SetText(_originalText);
        _originalText = null;
    }
    internal void BeforeSetup()
    {
        if (_disposed) return;
        if (_setupDepth++ != 0) return;
        var lease = _lease; lease?.Dispose(); _lease = null;
    }
    internal void AfterSetup()
    {
        if (_disposed) return;
        if (_setupDepth > 0) _setupDepth--;
        if (_setupDepth == 0) Apply();
    }
    internal void FailedSetup() { if (_setupDepth > 0) _setupDepth--; }
    public void Dispose()
    {
        _disposed = true;
        var lease = _lease; lease?.Dispose(); _lease = null;
        RestoreText();
    }
}
