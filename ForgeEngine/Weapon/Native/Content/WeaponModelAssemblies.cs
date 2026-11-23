using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Gear;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>The accepted model-workshop assemblies keyed by the exact native weapon/item block they decorate.</summary>
internal sealed class WeaponModelAssemblies : IDisposable
{
    private readonly IReadOnlyDictionary<string, Entry> _entries;
    private readonly WeaponLog _log;
    private readonly Action<string> _report;
    private readonly OnceReporter _once;
    private readonly Dictionary<IntPtr, GearPartHolder> _pending = new();
    // Whole guns mounted by this session, by holder; shared by every snapshot (With) like the holders themselves.
    private Dictionary<IntPtr, ForgeVisual.GunMount> _mounts = new();
    // Models that replace a part and carry its muzzle, left hand and sight, by holder; shared like the mounts.
    private Dictionary<IntPtr, ForgeVisual.GunPartMount> _partMounts = new();
    // Native part renderers a model replaces, by holder (not drawn, no shadow); shared by every snapshot like the mounts.
    private Dictionary<IntPtr, List<Renderer>> _replaced = new();
    // The block of each workshop weapon by its gear category, for guns whose game has no catalogue record on them (see With).
    private Dictionary<uint, string> _blockByCategory = new();
    private bool _levelBuilt;

    /// <summary>The block a holder's gun is: the catalogue record the local game put on it, else (a gun rebuilt from another player's replicated
    /// components) the workshop weapon owning its category.</summary>
    internal string? BlockOf(GearPartHolder holder) => BlockOf(holder, out _, out _);

    /// <summary><paramref name="key"/> says which one matched: "record" (the local catalogue record), "category" (the category table) or "none";
    /// <paramref name="category"/> is the gun's `GearCategory` number, 0 when it has no gear range.</summary>
    private string? BlockOf(GearPartHolder holder, out string key, out uint category)
    {
        var gear = holder.GearIDRange;
        category = gear == null ? 0u : gear.GetCompID(eGearComponent.Category);
        var record = EquipmentNativeAdapter.GearBlockId(gear);
        if (record != null) { key = "record"; return record; }
        if (category != 0 && _blockByCategory.TryGetValue(category, out var block)) { key = "category"; return block; }
        key = "none";
        return null;
    }

    /// <summary>The gated readback of one holder's block match, written once per Apply: Info when a block matched and by which key (a gun another
    /// player's game rebuilt matches by category), Trace when none did (every vanilla gun, so only a Trace log has them).</summary>
    private void ReadBack(string state, string? blockId, string key, uint category)
    {
        try
        {
            var level = blockId != null ? RuntimeLogLevel.Info : RuntimeLogLevel.Trace;
            if (!_log.Logs(level)) return;
            var values = new List<RuntimeLogValue> { RuntimeLogValue.Of("state", state), RuntimeLogValue.Of("key", key), RuntimeLogValue.Of("category", (long)category),
                RuntimeLogValue.Of("categories", (long)_blockByCategory.Count), RuntimeLogValue.Of("levelBuilt", _levelBuilt ? "yes" : "no") };
            if (blockId != null) { values.Add(RuntimeLogValue.Of("block", blockId)); values.Add(RuntimeLogValue.Of("model", _entries.ContainsKey(blockId) ? "entry" : "no-entry")); }
            _log.Write(level, WeaponLogCodes.ModelApplied, new RuntimeLogFacts { Values = values });
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponModelAssemblies.ReadBack", failure); }
    }

    private WeaponModelAssemblies(IReadOnlyDictionary<string, Entry> entries, Action<string> report, WeaponLog log)
    { _entries = entries; _report = report; _once = new OnceReporter(report); _log = log; }

    internal static WeaponModelAssemblies Empty(Action<string> report, WeaponLog log)
        => new(new Dictionary<string, Entry>(StringComparer.Ordinal), report, log);
    // Accepted weapons enter the native equipment pool, including merge-mode Rundowns. Their models are
    // common equipment resources, not placements owned by the level that happens to demonstrate the weapon.
    // The imported models come from the package that ships the content entry (<paramref name="entryPath"/>, BepInEx-relative).
    internal static NativeModelRuntime.NativeModelRegistration Prepare(string owner, NativeModelContent content, string entryPath, Action<string> report)
        => NativeModelRuntime.Prepare(owner, content, static () => true, ImportedModelFiles.ForEntry(BepInEx.Paths.BepInExRootPath, entryPath, content,
            failure => report(failure.Code + ": " + failure.Relative + " " + failure.Reason)));
    internal int Count { get { int count = 0; foreach (var row in _entries.Values) count += row.Content.Assemblies.Count; return count; } }

    /// <param name="category">The gear's own `GearCategory` number (each workshop weapon has one), 0 for none. Another player's game rebuilds a gun from the
    /// replicated components, which carry the category and not the catalogue record <see cref="EquipmentNativeAdapter.GearBlockId"/> reads, so the category
    /// is how the gun everyone else holds finds its model.</param>
    internal WeaponModelAssemblies With(string blockId, string owner, NativeModelContent content,
        ForgeVisual.SurfaceAppearance? appearance = null, IReadOnlyDictionary<GearPartSlot, ForgeVisual.SurfaceAppearance>? partAppearances = null,
        ForgeVisual.GunGrips? grips = null, string? leftHandShape = null, string? rightHandShape = null, ForgeVisual.GunPartPoints? partPoints = null, IReadOnlySet<string>? followers = null,
        uint category = 0)
    {
        var copy = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal) { [blockId] = new(owner, content, appearance, partAppearances ?? new Dictionary<GearPartSlot, ForgeVisual.SurfaceAppearance>(), grips, leftHandShape, rightHandShape, partPoints, followers ?? new HashSet<string>(StringComparer.Ordinal)) };
        var byCategory = new Dictionary<uint, string>(_blockByCategory);
        if (category != 0) byCategory[category] = blockId;
        var next = new WeaponModelAssemblies(copy, _report, _log) { _levelBuilt = _levelBuilt, _mounts = _mounts, _partMounts = _partMounts, _replaced = _replaced, _blockByCategory = byCategory };
        foreach (var holder in _pending) next._pending.Add(holder.Key, holder.Value);
        return next;
    }

    /// <summary>The same table with no entries: a content generation starts again from it. The holders this session mounted and the
    /// holders waiting for a level stay shared, as across every snapshot.</summary>
    internal WeaponModelAssemblies Cleared()
    {
        var next = new WeaponModelAssemblies(new Dictionary<string, Entry>(StringComparer.Ordinal), _report, _log)
        { _levelBuilt = _levelBuilt, _mounts = _mounts, _partMounts = _partMounts, _replaced = _replaced };
        foreach (var holder in _pending) next._pending.Add(holder.Key, holder.Value);
        return next;
    }

    // The native pool also assembles menu previews and icons before any world exists. Keep only holders that
    // need model work, then resolve their current gear after Runtime's build boundary has prepared the closure.
    internal void PartsSpawned(GearPartHolder? holder)
    {
        if (holder == null) return;
        if (_levelBuilt) { Apply(holder); return; }
        var blockId = BlockOf(holder, out var key, out var category);
        ReadBack("waiting-for-level", blockId, key, category);
        if (blockId != null && _entries.TryGetValue(blockId, out var entry)
            && (entry.Content.Assemblies.Count != 0 || entry.Appearance != null || entry.PartAppearances.Count != 0 || entry.LeftHandShape != null || entry.RightHandShape != null))
            _pending[holder.Pointer] = holder;
        else _pending.Remove(holder.Pointer);
    }

    internal void AwaitLevel() => _levelBuilt = false;

    internal void LevelBuilt()
    {
        _levelBuilt = true;
        var pending = new List<GearPartHolder>(_pending.Values);
        _pending.Clear();
        foreach (var holder in pending) Apply(holder);
    }

    internal void LevelEnded()
    {
        _levelBuilt = false;
        _pending.Clear();
        foreach (var mount in _mounts.Values) Unmount(mount);
        _mounts.Clear();
        foreach (var mount in _partMounts.Values) Unmount(mount);
        _partMounts.Clear();
        foreach (var hidden in _replaced.Values) Draw(hidden);
        _replaced.Clear();
    }

    internal void Apply(GearPartHolder? holder)
    {
        if (holder == null) return;
        string instance = holder.Pointer.ToInt64().ToString("x", CultureInfo.InvariantCulture) + ":";
        // The whole gun goes before its placement: the mount gives the game gun back, then the placement is removed.
        if (_mounts.Remove(holder.Pointer, out var mounted)) Unmount(mounted);
        if (_partMounts.Remove(holder.Pointer, out var partMounted)) Unmount(partMounted);
        if (_replaced.Remove(holder.Pointer, out var hidden)) Draw(hidden);
        foreach (var value in _entries.Values)
            if (value.Content.Assemblies.Count != 0 || value.Appearance != null || value.PartAppearances.Count != 0)
                NativeModelRuntime.RemoveCopies(value.Owner, instance);
        var blockId = BlockOf(holder, out var key, out var category);
        ReadBack("apply", blockId, key, category);
        if (blockId == null || !_entries.TryGetValue(blockId, out var entry)) return;
        try { ForgeVisual.GunMount.SetHandShapes(holder, entry.LeftHandShape, entry.RightHandShape); }
        catch (Exception failure) { Refused(entry.Owner, blockId, "hands", "model-hand-shape-refused:" + failure.Message); }
        ApplySurfaces(holder, blockId, instance, entry);
        HideReplaced(holder, blockId, entry);
        NativeModelAssembly? wholeGun = null;
        foreach (var assembly in entry.Content.Assemblies) if (assembly.Replaces == "BaseItem") wholeGun = assembly;
        // The whole gun goes first: the models that follow a game node stand on its mount.
        foreach (var assembly in entry.Content.Assemblies.OrderBy(assembly => assembly != wholeGun))
        {
            // The whole gun stands in for every part and every other model (GunMount hides what has a renderer), except a model that follows a game node.
            if (wholeGun != null && assembly != wholeGun && !entry.Followers.Contains(assembly.Id)) continue;
            var host = Host(holder, assembly.Host, out var refusal);
            if (host == null) { Refused(entry.Owner, blockId, assembly.Id, refusal!); continue; }
            ForgeVisual.SurfaceAppearance? partAppearance = null;
            if (assembly.Host.WeaponPart != null && Enum.TryParse<GearPartSlot>(assembly.Host.WeaponPart, out var slot))
                entry.PartAppearances.TryGetValue(slot, out partAppearance);
            GameObject? placed = null;
            var whole = assembly == wholeGun;
            var follower = wholeGun != null && !whole;
            ForgeVisual.GunPartPoint? points = null;
            if (!whole) entry.PartPoints?.TryGet(assembly.Id, out points);
            refusal = NativeModelRuntime.Place(entry.Owner, assembly, host, instance,
                prepare: whole || follower || points != null ? (root, _) => { placed = root; return null; }
            : null,
                appearance: ForgeVisual.SurfaceAppearance.Over(partAppearance, entry.Appearance));
            if (refusal != null) { Refused(entry.Owner, blockId, assembly.Id, refusal); continue; }
            if (whole)
            {
                // The model is the whole gun: ForgeVisual seats it by the shared hold rule, hides the game gun and puts the hands on its grip points.
                // Imported models draw without a renderer: their size comes from the placement's own draws.
                Bounds? drawn = NativeModelRuntime.TryImportedBounds(entry.Owner, instance, assembly.Id, out var box, placed!.transform) ? box : null;
                try
                {
                    _mounts[holder.Pointer] = ForgeVisual.GunMount.Create(holder, placed!, new ForgeVisual.GunMountOptions
                    {
                        Name = assembly.Id,
                        Grips = entry.Grips,
                        Adopt = true,
                        DrawnBounds = drawn,
                        Viewed = view => Note(blockId, assembly.Id + ":view", view)
                    });
                }
                catch (Exception failure) { Refused(entry.Owner, blockId, assembly.Id, "model-whole-gun-refused:" + failure.Message); continue; }
            }
            else if (follower)
            {
                // The whole gun's own refusal is already reported; the model then stays on its node like any other.
                if (!_mounts.TryGetValue(holder.Pointer, out var mount)) continue;
                try { mount.FollowNode(placed!.transform, host); }
                catch (Exception failure) { Refused(entry.Owner, blockId, assembly.Id, "model-follower-refused:" + failure.Message); continue; }
            }
            else if (points != null)
            {
                try { _partMounts[holder.Pointer] = ForgeVisual.GunPartMount.Create(holder, placed!.transform, points, assembly.Id); }
                catch (Exception failure) { Refused(entry.Owner, blockId, assembly.Id, "model-part-points-refused:" + failure.Message); continue; }
            }
            if (_log.Logs(RuntimeLogLevel.Info)) _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ModelApplied,
                new RuntimeLogFacts
                {
                    Values = new[] { RuntimeLogValue.Of("block", blockId), EquipmentNativeAdapter.WeaponName(blockId),
                    RuntimeLogValue.Of("assembly", assembly.Id), RuntimeLogValue.Of("parts", (long)assembly.Parts.Count) }
                });
        }
    }

    private void ApplySurfaces(GearPartHolder holder, string blockId, string instance, Entry entry)
    {
        if (entry.Appearance == null && entry.PartAppearances.Count == 0) return;
        // Old Forge copies have been removed; capture native surfaces before placing this holder's attachments.
        var surfaces = new Dictionary<Renderer, ForgeVisual.SurfaceAppearance>(ForgeCore.NativeObjects.Identity);
        if (entry.Appearance != null)
            foreach (var renderer in holder.transform.GetComponentsInChildren<Renderer>(true))
                surfaces[renderer] = entry.Appearance;
        foreach (var part in entry.PartAppearances)
        {
            var renderers = PartRenderers(holder, part.Key);
            if (renderers == null)
            { Refused(entry.Owner, blockId, "surface:" + part.Key, "model-weapon-host-missing:" + part.Key); continue; }
            var appearance = ForgeVisual.SurfaceAppearance.Over(part.Value, entry.Appearance)!;
            foreach (var renderer in renderers) surfaces[renderer] = appearance;
        }
        if (surfaces.Count == 0)
        { Refused(entry.Owner, blockId, "surface", "model-surface-renderers-missing"); return; }
        var requests = new List<(Renderer Renderer, ForgeVisual.SurfaceAppearance Appearance)>(surfaces.Count);
        foreach (var surface in surfaces) requests.Add((surface.Key, surface.Value));
        var refusal = NativeModelRuntime.PaintSurfaces(entry.Owner, instance, requests);
        if (refusal != null) Refused(entry.Owner, blockId, "surface", refusal);
        else if (_log.Logs(RuntimeLogLevel.Info)) _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ModelApplied,
            new RuntimeLogFacts
            {
                Values = new[] { RuntimeLogValue.Of("block", blockId), EquipmentNativeAdapter.WeaponName(blockId),
                RuntimeLogValue.Of("assembly", "surface"), RuntimeLogValue.Of("parts", (long)surfaces.Count) }
            });
    }

    /// <summary>A part a model replaces is not drawn and casts no shadow (SurfaceLook.SetDrawn, like a removed part; opacity 0 keeps the shadow);
    /// the model then hangs on it. The whole gun is GunMount's to hide.</summary>
    private void HideReplaced(GearPartHolder holder, string blockId, Entry entry)
    {
        foreach (var assembly in entry.Content.Assemblies)
        {
            if (assembly.Replaces == null || !Enum.TryParse<GearPartSlot>(assembly.Replaces, out var part) || part == GearPartSlot.BaseItem) continue;
            var renderers = PartRenderers(holder, part);
            if (renderers == null) { Refused(entry.Owner, blockId, assembly.Id, "model-weapon-host-missing:" + part); continue; }
            if (!_replaced.TryGetValue(holder.Pointer, out var hidden)) _replaced[holder.Pointer] = hidden = new List<Renderer>();
            foreach (var renderer in renderers) { ForgeVisual.SurfaceLook.SetDrawn(renderer, false); hidden.Add(renderer); }
            Note(blockId, assembly.Id + ":hidden", part + " renderers=" + renderers.Count);
        }
    }

    /// <summary>One Info line for a model's placement state (the mount's view, the native renderers a model hides), for the in-game check.</summary>
    private void Note(string blockId, string assembly, string state)
    {
        try
        {
            if (_log.Logs(RuntimeLogLevel.Info)) _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ModelApplied,
                new RuntimeLogFacts
                {
                    Values = new[] { RuntimeLogValue.Of("block", blockId), EquipmentNativeAdapter.WeaponName(blockId),
                    RuntimeLogValue.Of("assembly", assembly), RuntimeLogValue.Of("state", state) }
                });
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponModelAssemblies.Note", failure); }
    }

    private static void Draw(List<Renderer> hidden)
    {
        foreach (var renderer in hidden)
            try { if (renderer != null) ForgeVisual.SurfaceLook.SetDrawn(renderer, true); }
            catch (Exception failure) { NativeCallFailure.Note("WeaponModelAssemblies.Draw", failure); }
    }

    /// <summary>The renderers of one native part, or null when the gun has no such part. Alignment may parent another native slot below this
    /// one; its meshes still belong to that slot.</summary>
    private static HashSet<Renderer>? PartRenderers(GearPartHolder holder, GearPartSlot part)
    {
        var root = GearPartTransformApplier.Part(holder, part);
        if (root == null) return null;
        var renderers = new HashSet<Renderer>(root.transform.GetComponentsInChildren<Renderer>(true), ForgeCore.NativeObjects.Identity);
        foreach (var slot in Enum.GetValues<GearPartSlot>())
        {
            if (slot == GearPartSlot.BaseItem || slot == part) continue;
            var nested = GearPartTransformApplier.Part(holder, slot);
            if (nested == null || !nested.transform.IsChildOf(root.transform)) continue;
            foreach (var renderer in nested.transform.GetComponentsInChildren<Renderer>(true)) renderers.Remove(renderer);
        }
        return renderers;
    }

    private static Transform? Host(GearPartHolder holder, NativeModelHost host, out string? refusal)
    {
        refusal = null;
        if (host.Kind != "weapon" || host.WeaponPart == null
            || !Enum.TryParse<GearPartSlot>(host.WeaponPart, ignoreCase: false, out var slot)
            || !Enum.IsDefined(slot)) { refusal = "model-weapon-host-invalid"; return null; }
        var root = slot == GearPartSlot.BaseItem ? holder.transform : GearPartTransformApplier.Part(holder, slot)?.transform;
        if (root == null) { refusal = "model-weapon-host-missing:" + host.WeaponPart; return null; }
        return NativeModelRuntime.ResolveChild(root, host.ChildPath, host.ChildIndices, out refusal);
    }

    private void Refused(string weapon, string block, string assembly, string reason)
    {
        string level = !ForgeCore.Level.TryActiveExpedition(out var rundown, out var tier, out var index) ? "unresolved" : rundown.ToString(CultureInfo.InvariantCulture)
            + "/" + tier.ToString(CultureInfo.InvariantCulture)
            + "/" + index.ToString(CultureInfo.InvariantCulture);
        var key = level + "\0" + block + "\0" + assembly + "\0" + reason;
        var first = _once.Count(key) == 0;
        try
        {
            if (first && _log.Logs(RuntimeLogLevel.Error)) _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.ModelRefused,
                new RuntimeLogFacts
                {
                    Values = new[] { RuntimeLogValue.Of("owner", weapon), RuntimeLogValue.Of("level", level), RuntimeLogValue.Of("block", block),
                    EquipmentNativeAdapter.WeaponName(block),
                    RuntimeLogValue.Of("assembly", assembly), RuntimeLogValue.Of("reason", reason) }
                });
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponModelAssemblies.Refused", failure); }
        try { _once.Report(key, WeaponLogCodes.ModelRefused + ": weapon=" + weapon + " level=" + level + " block=" + block + " assembly=" + assembly + " reason=" + reason); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponModelAssemblies.Refused#2", failure); }
    }

    private static void Unmount(ForgeVisual.GunMount mount)
    {
        try { mount.Release(); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponModelAssemblies.Unmount", failure); }
    }

    private static void Unmount(ForgeVisual.GunPartMount mount)
    {
        try { mount.Release(); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponModelAssemblies.Unmount#2", failure); }
    }

    public void Dispose()
    {
        LevelEnded();
        foreach (var entry in _entries.Values) NativeModelRuntime.Remove(entry.Owner);
    }

    private sealed record Entry(string Owner, NativeModelContent Content, ForgeVisual.SurfaceAppearance? Appearance,
        IReadOnlyDictionary<GearPartSlot, ForgeVisual.SurfaceAppearance> PartAppearances, ForgeVisual.GunGrips? Grips,
        string? LeftHandShape, string? RightHandShape, ForgeVisual.GunPartPoints? PartPoints, IReadOnlySet<string> Followers);
}
