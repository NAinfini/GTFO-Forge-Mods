using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
// The game's gear types are named through fully qualified aliases rather than imported: `Gear` is the namespace
// they live in and this file's own namespace is `ForgeWeapon.Native`, so an unqualified `Gear.X` would be looked
// for inside the package first. `ItemEquippable` is the root of the gear hierarchy in this build and lives in the
// global namespace, not in `Gear`.
using ArchetypeDataBlock = global::GameData.ArchetypeDataBlock;
using BulletWeaponArchetype = global::Gear.BulletWeaponArchetype;
using GearBase = global::ItemEquippable;
using MinMaxRange = global::GameData.MinMaxValue;
using RecoilDataBlock = global::GameData.RecoilDataBlock;
using WeaponBase = global::Gear.BulletWeapon;

namespace ForgeWeapon.Native;

/// <summary>
/// The one place in this package that changes a weapon's numbers: ruling 84's instance-level block replacement,
/// written the only way the shipped code allows.
///
/// The evidence (evidence/weapon-override-hooks.json) settles three facts this file is built on. First, rate of
/// fire, spread, burst length and damage are read out of `BulletWeaponArchetype.m_archetypeData` on every shot
/// and every frame, so changing what an instance points at changes its behaviour immediately. Second, exactly
/// two values are *not* read per shot: `m_recoilData`, resolved once in the archetype constructor from the
/// block's `RecoilDataID`, and `m_burstMax`, copied out of the block by the burst archetypes' own `Setup`. Both
/// are refreshed here, or a swapped block would give a weapon the new rate of fire and the old recoil. Third,
/// native rebuilds the archetype in exactly one chain — `OnGearSpawnComplete` — so an override survives until
/// the next gear spawn and is replayed there.
///
/// What this file will not do: it never writes the shared block (the clone is the instance's own), never builds a
/// Forge-only override that the player's own trigger would ignore, and never invents a value it could not read.
/// A field whose native destination the instance does not have is refused *before* the first write, so a refused
/// request leaves the weapon exactly as it was found.
/// </summary>
internal sealed partial class WeaponOverrideApplier : IWeaponOverrideSink
{
    // Only the exact member writes below belong to this applier. There is no whole-object restore snapshot.
    private sealed class Applied
    {
        internal EntityReference Equipment = null!;
        internal WeaponBase Weapon = null!;
        internal BulletWeaponArchetype Archetype = null!;
        internal NativeValues Confirmed = null!;
        internal List<MemberWrite> Writes = new();
        internal NativeValues? Pending;
        internal List<MemberWrite>? PendingWrites;
    }

    private readonly Dictionary<string, Applied> _applied = new(StringComparer.Ordinal);
    private readonly Func<EntityReference, WeaponBase?> _weaponOf;
    private readonly Func<ItemEquippable, EntityReference?> _equipmentOf;
    private readonly Func<EntityReference, global::Gear.MeleeWeaponFirstPerson?> _meleeOf;
    private readonly Action<string> _report;
    private readonly WeaponLog _log;
    private Action<string>? _profileCleanupReport;
    private EntityReference? _profileCleanupEquipment;

    // Scoped to the exact synchronous profile writer cleanup. Other writers keep their normal diagnostics.
    internal bool RevertProfileWriter(EntityReference equipment, WeaponOverrideSource source,
        Action<string> report, out string code)
    {
        var previous = _profileCleanupReport;
        var previousEquipment = _profileCleanupEquipment;
        _profileCleanupReport = report;
        _profileCleanupEquipment = equipment;
        try { return Ledger.RevertWriter(equipment, source, out code); }
        finally { _profileCleanupReport = previous; _profileCleanupEquipment = previousEquipment; }
    }

    private void Report(EntityReference? equipment, string message)
    {
        var scoped = _profileCleanupReport;
        if (scoped == null || equipment != _profileCleanupEquipment) { SafeReport(message); return; }
        // A diagnostic observer can reenter another writer. That nested work keeps its own reporter;
        // it is not another failure of the profile whose cleanup produced this message.
        _profileCleanupReport = null;
        var previousEquipment = _profileCleanupEquipment;
        _profileCleanupEquipment = null;
        try { scoped(message); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponOverrideApplier.Report", failure); /* Diagnostics cannot interrupt ownership recovery. */ }
        finally { _profileCleanupReport = scoped; _profileCleanupEquipment = previousEquipment; }
    }

    private void SafeReport(string message)
    {
        try { _report(message); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponOverrideApplier.SafeReport", failure); /* Diagnostics cannot interrupt ownership recovery. */ }
    }

    /// <summary>`weaponOf` is the equipment domain's own current-instance lookup for `gtfo.equipment`, and
    /// `equipmentOf` is the inverse for a native weapon. Neither is a second table: a weapon this package was
    /// never told about is answered with null and the request is refused as stale, which is what an instance
    /// that no longer exists deserves.</summary>
    internal WeaponOverrideApplier(Func<EntityReference, WeaponBase?> weaponOf,
        Func<ItemEquippable, EntityReference?> equipmentOf, Action<string> report, WeaponLog log,
        Func<EntityReference, global::Gear.MeleeWeaponFirstPerson?> meleeOf)
    {
        _weaponOf = weaponOf ?? throw new ArgumentNullException(nameof(weaponOf));
        _equipmentOf = equipmentOf ?? throw new ArgumentNullException(nameof(equipmentOf));
        _meleeOf = meleeOf ?? throw new ArgumentNullException(nameof(meleeOf));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Ledger = new WeaponOverrideLedger(this);
    }

    /// <summary>The ledger this applier writes for. The two are one feature: the ledger decides what is true and
    /// this file is the only code that makes it true on a weapon, so a caller can never hold one without the
    /// other.</summary>
    internal WeaponOverrideLedger Ledger { get; }

    /// <summary>Whether this machine has written a reload time on this native weapon, which is what makes it an
    /// author's weapon for the reload sequence: a stock weapon is never touched.</summary>
    internal bool WritesReloadTime(IntPtr weapon)
    {
        foreach (var entry in _applied.Values)
            if (entry.Weapon.Pointer == weapon && entry.Writes.Exists(write => write.Member is NativeMember.ReloadTime or NativeMember.ItemReloadTime))
                return true;
        return false;
    }

    /// <summary>Instances currently carrying a clone this machine wrote.</summary>
    internal int AppliedCount => _applied.Count + _meleeApplied.Count;

    /// <summary>Withdraw native contributions before their equipment identities are released. A failed
    /// readback keeps the recovery entries and ledger alive so teardown can be retried explicitly.</summary>
    internal void EndSession()
    {
        RestoreAll();
        if (AppliedCount != 0)
            throw new RuntimeContractException(ResultCode.OverrideRestoreUnconfirmed,
                "Weapon session still owns " + AppliedCount + " unconfirmed native restores.");
        Ledger.EndWorld();
    }

    /// <summary>Build from the current native values, remove only this entry's still-owned members from the
    /// candidate, then apply the ledger's merged fields. Native methods keep their own execution path; there is
    /// no instance override API in this build, so the per-instance block references remain the narrow seam.</summary>
    public WeaponOverrideWrite Apply(EntityReference equipment, IReadOnlyList<WeaponOverrideField> fields, out string code)
    {
        if (_meleeOf(equipment) is { } melee) return ApplyMelee(equipment, melee, fields, out code);
        code = "";
        var key = equipment?.Id ?? "";
        Applied? entry = null;
        bool mutated = false;
        try
        {
            var weapon = _weaponOf(equipment!);
            if (weapon == null || weapon.Pointer == IntPtr.Zero)
            { code = ResultCode.StaleEntity; return WeaponOverrideWrite.Refused; }
            var archetype = weapon.m_archeType;
            if (archetype == null || archetype.Pointer == IntPtr.Zero || archetype.m_archetypeData == null)
            { code = ResultCode.OverrideBlockMissing; return WeaponOverrideWrite.Refused; }
            _applied.TryGetValue(key, out var existing);
            var displaced = existing != null && (existing.Equipment != equipment || existing.Archetype.Pointer != archetype.Pointer || existing.Weapon.Pointer != weapon.Pointer)
                ? existing : null;
            if (displaced != null) existing = null;
            var family = Family(weapon, archetype);
            bool wantsRecoil = false;
            foreach (var field in fields)
            {
                if (!WeaponActionRuntime.HasDestination(field.Name, family, out var recoil))
                { code = ResultCode.OverrideFieldUnknown; return WeaponOverrideWrite.Refused; }
                wantsRecoil |= recoil;
            }
            if (wantsRecoil && (archetype.m_recoilData == null || !HasRecoilRange(archetype.m_recoilData, fields))
                || fields.Any(field => field.Name == "reload_time") && weapon.ArchetypeData == null)
            { code = ResultCode.OverrideBlockMissing; return WeaponOverrideWrite.Refused; }

            var staleEntries = _applied.Values.Where(stale => stale == displaced || stale.Equipment.Id != key
                && stale.Archetype.Pointer == archetype.Pointer).ToArray();
            var current = Capture(weapon, archetype);
            var baseline = Copy(current);
            if (existing != null) RemoveFromCandidate(existing, current, baseline);
            foreach (var stale in staleEntries) RemoveFromCandidate(stale, current, baseline);
            var candidate = baseline;
            entry = existing ?? new Applied
            {
                Equipment = equipment!,
                Weapon = weapon,
                Archetype = archetype,
                Confirmed = current
            };
            var resolved = new double[fields.Count];
            for (int i = 0; i < fields.Count; i++)
                if (!TryResolve(baseline, fields[i], out resolved[i], out code)) return WeaponOverrideWrite.Refused;
            // Refused input must not perform even a recovery write. Once validation has accepted it,
            // settle old ownership and build again from the native values those setters actually left behind.
            if (existing?.Pending != null || staleEntries.Length != 0)
            {
                if (existing != null && !RollbackPending(existing))
                { code = ResultCode.OverrideRestoreUnconfirmed; return WeaponOverrideWrite.Failed; }
                foreach (var stale in staleEntries)
                    if (!RestoreEntry(stale, out code)) return WeaponOverrideWrite.Failed;
                return Apply(equipment!, fields, out code);
            }
            var writes = new List<MemberWrite>();
            for (int i = 0; i < fields.Count; i++) AddWrites(candidate, fields[i].Name, resolved[i], writes);

            // Rollback records the immediate before value only for the union of this transaction's members.
            // It never restores a saved block pointer or overwrites a native replacement reference.
            var touched = new HashSet<NativeMember>(writes.Select(write => write.Member));
            if (existing != null) foreach (var write in existing.Writes) touched.Add(write.Member);
            var pending = new List<MemberWrite>();
            foreach (var member in touched)
                pending.Add(new MemberWrite(member, Read(current, member), Read(candidate, member)));
            entry.Pending = candidate;
            entry.PendingWrites = pending;
            mutated = true;
            Install(candidate, touched);
            if (!Confirmed(candidate, pending)) return Settle(key, entry, out code);
            entry.Confirmed = candidate;
            entry.Writes = writes;
            entry.Pending = null;
            entry.PendingWrites = null;
            _applied[key] = entry;
            try
            {
                if (_log.Logs(RuntimeLogLevel.Info))
                    _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.OverrideApplied, new RuntimeLogFacts
                    { Entity = key.Length == 0 ? null : key, Values = new[] { RuntimeLogValue.Of("fields", (long)fields.Count) } });
            }
            catch (Exception failure) { NativeCallFailure.Note("WeaponOverrideApplier.Apply", failure); /* The native write is already confirmed. */ }
            return WeaponOverrideWrite.Applied;
        }
        catch (Exception error)
        {
            code = ResultCode.OverrideWriteFailed;
            Report(equipment, (mutated ? "weapon.override-write-threw" : "weapon.override-write-failed")
                + " equipment=" + key + " exception=" + error.GetType().Name);
            return mutated ? Settle(key, entry!, out code) : WeaponOverrideWrite.Failed;
        }
    }

    private WeaponOverrideWrite Settle(string key, Applied entry, out string code)
    {
        if (!RollbackPending(entry))
        {
            _applied[key] = entry;
            code = ResultCode.OverrideWriteUnconfirmed;
            Report(entry.Equipment, "weapon.override-rollback-unconfirmed equipment=" + key + " code=" + ResultCode.OverrideRestoreUnconfirmed);
            return WeaponOverrideWrite.Unconfirmed;
        }
        if (entry.Writes.Count != 0) _applied[key] = entry;
        else _applied.Remove(key);
        code = ResultCode.OverrideWriteRolledBack;
        Report(entry.Equipment, "weapon.override-rolled-back equipment=" + key + " code=" + code);
        return WeaponOverrideWrite.Failed;
    }

    private bool RollbackPending(Applied entry)
    {
        if (entry.Pending is not { } pending) return true;
        try
        {
            if (!Withdraw(pending, entry.PendingWrites!, entry.Equipment, "weapon.override-rollback-failed")) return false;
            // A partial install can leave different domains on the candidate and the previous clone. Retain
            // ownership only when the live reference is one of those two exact references.
            var old = entry.Confirmed;
            if (ForgeCore.NativeObjects.Same(entry.Archetype.m_archetypeData, pending.Block)) old.Block = pending.Block;
            if (ForgeCore.NativeObjects.Same(entry.Archetype.m_recoilData, pending.Recoil))
            { old.Recoil = pending.Recoil; old.Horizontal = pending.Horizontal; old.Vertical = pending.Vertical; old.Power = pending.Power; }
            if (ForgeCore.NativeObjects.Same(entry.Weapon.ArchetypeData, pending.ItemBlock)) old.ItemBlock = pending.ItemBlock;
            entry.Pending = null;
            entry.PendingWrites = null;
            return true;
        }
        catch (Exception error)
        {
            Report(entry.Equipment, "weapon.override-rollback-failed equipment=" + entry.Equipment.Id + " exception=" + error.GetType().Name);
            return false;
        }
    }

    public bool Restore(EntityReference equipment, out string code)
    {
        code = "";
        if (equipment == null) return true;
        if (_meleeApplied.TryGetValue(equipment.Id, out var melee))
            return melee.Equipment != equipment || RestoreMelee(melee, out code);
        if (!_applied.TryGetValue(equipment.Id ?? "", out var applied) || applied.Equipment != equipment) return true;
        return RestoreEntry(applied, out code);
    }

    private bool RestoreEntry(Applied applied, out string code)
    {
        code = "";
        if (!RollbackPending(applied) || !Withdraw(applied.Confirmed, applied.Writes, applied.Equipment, "weapon.override-restore-failed"))
        {
            code = ResultCode.OverrideRestoreUnconfirmed;
            Report(applied.Equipment, "weapon.override-restore-unconfirmed equipment=" + applied.Equipment.Id + " code=" + code);
            return false;
        }
        _applied.Remove(applied.Equipment.Id ?? "");
        try { _log.Text(RuntimeLogLevel.Info, WeaponLogCodes.OverrideRestored, null, applied.Equipment.Id); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponOverrideApplier.RestoreEntry", failure); /* The owned writes are already withdrawn. */ }
        return true;
    }

    private enum NativeMember
    {
        FireRate, ReloadTime, BurstCount, SpreadCone, MovementSpread, AimSpread,
        RecoilHorizontalMin, RecoilHorizontalMax, RecoilVerticalMin, RecoilVerticalMax,
        RecoilRecovery, RecoilPowerMin, RecoilPowerMax, ItemReloadTime, ArchetypeBurst, WeaponBurst
    }

    private readonly record struct MemberWrite(NativeMember Member, double Before, double Written);

    private sealed class NativeValues
    {
        internal WeaponBase Weapon = null!;
        internal BulletWeaponArchetype Archetype = null!;
        internal ArchetypeDataBlock Block = null!;
        internal RecoilDataBlock? Recoil;
        internal ArchetypeDataBlock? ItemBlock;
        internal IntPtr Horizontal;
        internal IntPtr Vertical;
        internal IntPtr Power;
        internal int ArchetypeBurst;
        internal int WeaponBurst;
    }

    private static NativeValues Capture(WeaponBase weapon, BulletWeaponArchetype archetype) => Ranges(new NativeValues()
    {
        Weapon = weapon,
        Archetype = archetype,
        Block = archetype.m_archetypeData,
        Recoil = archetype.m_recoilData,
        ItemBlock = weapon.ArchetypeData,
        ArchetypeBurst = ArchetypeBurst(archetype) ?? archetype.m_archetypeData.BurstShotCount,
        WeaponBurst = weapon.m_burstMax
    });

    private static NativeValues Copy(NativeValues source) => Ranges(new NativeValues()
    {
        Weapon = source.Weapon,
        Archetype = source.Archetype,
        Block = CloneBlock(source.Block),
        Recoil = source.Recoil == null ? null : CloneRecoil(source.Recoil),
        ItemBlock = source.ItemBlock == null ? null : CloneBlock(source.ItemBlock),
        ArchetypeBurst = source.ArchetypeBurst,
        WeaponBurst = source.WeaponBurst
    });

    private static NativeValues Ranges(NativeValues values)
    {
        values.Horizontal = values.Recoil?.horizontalScale?.Pointer ?? IntPtr.Zero;
        values.Vertical = values.Recoil?.verticalScale?.Pointer ?? IntPtr.Zero;
        values.Power = values.Recoil?.power?.Pointer ?? IntPtr.Zero;
        return values;
    }

    private static bool Owns(NativeValues values, NativeMember member)
    {
        if (values.Archetype.Pointer == IntPtr.Zero || values.Weapon.Pointer == IntPtr.Zero) return false;
        if (member <= NativeMember.AimSpread) return ForgeCore.NativeObjects.Same(values.Archetype.m_archetypeData, values.Block);
        if (member <= NativeMember.RecoilPowerMax)
        {
            if (!ForgeCore.NativeObjects.Same(values.Archetype.m_recoilData, values.Recoil)) return false;
            return member switch
            {
                NativeMember.RecoilHorizontalMin or NativeMember.RecoilHorizontalMax => values.Recoil?.horizontalScale?.Pointer == values.Horizontal,
                NativeMember.RecoilVerticalMin or NativeMember.RecoilVerticalMax => values.Recoil?.verticalScale?.Pointer == values.Vertical,
                NativeMember.RecoilPowerMin or NativeMember.RecoilPowerMax => values.Recoil?.power?.Pointer == values.Power,
                _ => true
            };
        }
        if (member == NativeMember.ItemReloadTime) return ForgeCore.NativeObjects.Same(values.Weapon.ArchetypeData, values.ItemBlock);
        return ForgeCore.NativeObjects.Same(values.Weapon.m_archeType, values.Archetype); // A rebuild owns its own caches.
    }

    private static double Read(NativeValues values, NativeMember member) => member switch
    {
        NativeMember.FireRate => values.Block.ShotDelay,
        NativeMember.ReloadTime => values.Block.DefaultReloadTime,
        NativeMember.BurstCount => values.Block.BurstShotCount,
        NativeMember.SpreadCone => values.Block.ShotgunConeSize,
        NativeMember.MovementSpread => values.Block.HipFireSpread,
        NativeMember.AimSpread => values.Block.AimSpread,
        NativeMember.RecoilHorizontalMin => values.Recoil!.horizontalScale!.Min,
        NativeMember.RecoilHorizontalMax => values.Recoil!.horizontalScale!.Max,
        NativeMember.RecoilVerticalMin => values.Recoil!.verticalScale!.Min,
        NativeMember.RecoilVerticalMax => values.Recoil!.verticalScale!.Max,
        NativeMember.RecoilRecovery => values.Recoil!.spring,
        NativeMember.RecoilPowerMin => values.Recoil!.power!.Min,
        NativeMember.RecoilPowerMax => values.Recoil!.power!.Max,
        NativeMember.ItemReloadTime => values.ItemBlock!.DefaultReloadTime,
        NativeMember.ArchetypeBurst => values.ArchetypeBurst,
        NativeMember.WeaponBurst => values.WeaponBurst,
        _ => throw new ArgumentOutOfRangeException(nameof(member))
    };

    private static double ReadLive(NativeValues values, NativeMember member) => member switch
    {
        NativeMember.ArchetypeBurst => ArchetypeBurst(values.Archetype) ?? values.ArchetypeBurst,
        NativeMember.WeaponBurst => values.Weapon.m_burstMax,
        _ => Read(values, member)
    };

    private static void WriteMember(NativeValues values, NativeMember member, double value, bool live = false)
    {
        switch (member)
        {
            case NativeMember.FireRate: values.Block.ShotDelay = (float)value; break;
            case NativeMember.ReloadTime: values.Block.DefaultReloadTime = (float)value; break;
            case NativeMember.BurstCount: values.Block.BurstShotCount = (int)value; break;
            case NativeMember.SpreadCone: values.Block.ShotgunConeSize = (int)value; break;
            case NativeMember.MovementSpread: values.Block.HipFireSpread = (float)value; break;
            case NativeMember.AimSpread: values.Block.AimSpread = (float)value; break;
            case NativeMember.RecoilHorizontalMin: values.Recoil!.horizontalScale!.Min = (float)value; break;
            case NativeMember.RecoilHorizontalMax: values.Recoil!.horizontalScale!.Max = (float)value; break;
            case NativeMember.RecoilVerticalMin: values.Recoil!.verticalScale!.Min = (float)value; break;
            case NativeMember.RecoilVerticalMax: values.Recoil!.verticalScale!.Max = (float)value; break;
            case NativeMember.RecoilRecovery: values.Recoil!.spring = (float)value; break;
            case NativeMember.RecoilPowerMin: values.Recoil!.power!.Min = (float)value; break;
            case NativeMember.RecoilPowerMax: values.Recoil!.power!.Max = (float)value; break;
            case NativeMember.ItemReloadTime: values.ItemBlock!.DefaultReloadTime = (float)value; break;
            case NativeMember.ArchetypeBurst:
                if (live) SetBurstCount(values.Archetype, (int)value);
                else values.ArchetypeBurst = (int)value;
                break;
            case NativeMember.WeaponBurst:
                if (live) values.Weapon.m_burstMax = (int)value;
                else values.WeaponBurst = (int)value;
                break;
        }
    }

    private static void AddWrites(NativeValues values, string name, double value, List<MemberWrite> writes)
    {
        void Add(NativeMember member)
        {
            var before = Read(values, member);
            WriteMember(values, member, value);
            writes.Add(new MemberWrite(member, before, Read(values, member)));
        }
        switch (name)
        {
            case "fire_rate": Add(NativeMember.FireRate); break;
            case "reload_time": Add(NativeMember.ReloadTime); Add(NativeMember.ItemReloadTime); break;
            case "burst_count":
                value = Math.Round(value, MidpointRounding.AwayFromZero);
                Add(NativeMember.BurstCount); Add(NativeMember.ArchetypeBurst); Add(NativeMember.WeaponBurst); break;
            case "spread_cone": value = Math.Round(value, MidpointRounding.AwayFromZero); Add(NativeMember.SpreadCone); break;
            case "spread_movement_scale": Add(NativeMember.MovementSpread); break;
            case "spread_aim_scale": Add(NativeMember.AimSpread); break;
            case "recoil_horizontal": Add(NativeMember.RecoilHorizontalMin); Add(NativeMember.RecoilHorizontalMax); break;
            case "recoil_vertical": Add(NativeMember.RecoilVerticalMin); Add(NativeMember.RecoilVerticalMax); break;
            case "recoil_recovery": Add(NativeMember.RecoilRecovery); break;
            case "recoil_camera_kick": Add(NativeMember.RecoilPowerMin); Add(NativeMember.RecoilPowerMax); break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private static Il2CppObjectBase? Target(NativeValues values, NativeMember member) => member switch
    {
        <= NativeMember.AimSpread => values.Block,
        NativeMember.RecoilHorizontalMin or NativeMember.RecoilHorizontalMax => values.Recoil?.horizontalScale,
        NativeMember.RecoilVerticalMin or NativeMember.RecoilVerticalMax => values.Recoil?.verticalScale,
        NativeMember.RecoilPowerMin or NativeMember.RecoilPowerMax => values.Recoil?.power,
        NativeMember.RecoilRecovery => values.Recoil,
        NativeMember.ItemReloadTime => values.ItemBlock,
        NativeMember.ArchetypeBurst => values.Archetype,
        NativeMember.WeaponBurst => values.Weapon,
        _ => null
    };

    private static bool OwnsCurrent(NativeValues owner, NativeValues current, NativeMember member)
        => Owns(owner, member) && ForgeCore.NativeObjects.Same(Target(owner, member), Target(current, member));

    private static void RemoveFromCandidate(Applied entry, NativeValues current, NativeValues baseline)
    {
        if (entry.Pending is { } pending)
            foreach (var write in entry.PendingWrites!)
                if (OwnsCurrent(pending, current, write.Member) && Read(baseline, write.Member).Equals(write.Written))
                    WriteMember(baseline, write.Member, write.Before);
        foreach (var write in entry.Writes)
            if ((OwnsCurrent(entry.Confirmed, current, write.Member) || entry.Pending is { } owner && OwnsCurrent(owner, current, write.Member))
                && Read(baseline, write.Member).Equals(write.Written))
                WriteMember(baseline, write.Member, write.Before);
    }

    private static void Install(NativeValues candidate, HashSet<NativeMember> touched)
    {
        if (touched.Any(member => member <= NativeMember.AimSpread)) candidate.Archetype.m_archetypeData = candidate.Block;
        if (touched.Any(member => member >= NativeMember.RecoilHorizontalMin && member <= NativeMember.RecoilPowerMax))
            candidate.Archetype.m_recoilData = candidate.Recoil;
        if (touched.Contains(NativeMember.ItemReloadTime)) candidate.Weapon.ArchetypeData = candidate.ItemBlock;
        if (touched.Contains(NativeMember.ArchetypeBurst)) SetBurstCount(candidate.Archetype, candidate.ArchetypeBurst);
        if (touched.Contains(NativeMember.WeaponBurst)) candidate.Weapon.m_burstMax = candidate.WeaponBurst;
    }

    private static bool Confirmed(NativeValues values, List<MemberWrite> writes)
    {
        foreach (var write in writes)
            if (!Owns(values, write.Member) || !ReadLive(values, write.Member).Equals(write.Written)) return false;
        return true;
    }

    // Continue past an individual native failure: one unavailable member must not strand the other writes.
    // A changed value or replacement reference is no longer ours, and is deliberately left alone.
    private bool Withdraw(NativeValues values, List<MemberWrite> writes, EntityReference equipment, string exceptionCode)
    {
        bool restored = true;
        foreach (var write in writes)
        {
            try
            {
                if (!Owns(values, write.Member) || !ReadLive(values, write.Member).Equals(write.Written)) continue;
                WriteMember(values, write.Member, write.Before, live: true);
                if (Owns(values, write.Member) && !ReadLive(values, write.Member).Equals(write.Before)) restored = false;
            }
            catch (Exception error)
            {
                restored = false;
                Report(equipment, exceptionCode + " equipment=" + equipment.Id + " exception=" + error.GetType().Name);
            }
        }
        return restored;
    }

    private static int? ArchetypeBurst(BulletWeaponArchetype archetype)
    {
        if (archetype.TryCast<global::Gear.BWA_Burst>() is { } burst) return burst.m_burstMax;
        if (archetype.TryCast<global::Gear.BWA_SemiBurst>() is { } semiBurst) return semiBurst.m_burstMax;
        return null;
    }

    /// <summary>Every instance this machine wrote, put back. Returns how many reverts were confirmed, so a world
    /// boundary can report whether anything was actually written back; an entry whose revert could not be
    /// confirmed stays owned and is reported rather than dropped.</summary>
    public int RestoreAll()
    {
        int meleeRestored = 0;
        foreach (var melee in new List<MeleeApplied>(_meleeApplied.Values))
            if (RestoreMelee(melee, out _)) meleeRestored++;
        if (_applied.Count == 0) return meleeRestored;
        var keys = new List<string>(_applied.Keys);
        var restored = meleeRestored;
        foreach (var key in keys)
        {
            if (!_applied.TryGetValue(key, out var applied)) continue;
            if (RestoreEntry(applied, out var code)) restored++;
            else SafeReport("weapon.override-restore-unconfirmed equipment=" + key + " code=" + code);
        }
        return restored;
    }

    /// <summary>
    /// One gear finished spawning, which is the only chain in the shipped code that rebuilds an archetype. An
    /// instance this package already overrode has just lost its clone, so the accepted fields are written again
    /// from the ledger's own answer. Replay is idempotent because it writes the merged absolute set and not a
    /// delta, and it is not a second application path: it calls the same <see cref="Apply"/>.
    /// </summary>
    internal void OnGearSpawnComplete(GearBase? gear)
    {
        if (gear?.TryCast<global::Gear.MeleeWeaponFirstPerson>() is { } melee)
        {
            var reference = _equipmentOf(melee);
            if (reference == null) return;
            var pending = Ledger.Pending(reference);
            if (pending.Count == 0) { Restore(reference, out _); return; }
            if (pending.Count != 0 && ApplyMelee(reference, melee, pending, out var failure) != WeaponOverrideWrite.Applied)
                SafeReport("weapon.override-replay-refused equipment=" + reference.Id + " code=" + failure);
            return;
        }
        var weapon = gear?.TryCast<WeaponBase>();
        if (weapon == null) return;
        var equipment = _equipmentOf(weapon);
        if (equipment == null) return;
        var fields = Ledger.Pending(equipment);
        if (fields.Count == 0) { Restore(equipment, out _); return; }
        // A spawn notification does not prove every reference changed. Apply checks each owned domain,
        // so duplicate notifications and a partial native rebuild cannot compound an add/multiply.
        var write = Apply(equipment, fields, out var code);
        if (write != WeaponOverrideWrite.Applied)
            SafeReport("weapon.override-replay-refused equipment=" + equipment.Id + " code=" + code);
    }

    /// <summary>
    /// Resolve once, before building or installing a candidate. Set never reads the base. Other operations
    /// read the instance's original value in the request's unit; only an explicit frequency becomes an
    /// interval. Native intervals, including negative zero, pass through without reciprocal arithmetic.
    /// </summary>
    private static bool TryResolve(NativeValues applied, WeaponOverrideField field, out double value, out string code)
    {
        value = field.Value;
        code = ResultCode.InputUnsupported;
        var frequency = field.Unit == WeaponOverrideUnit.ShotsPerSecond;
        if (field.Unit != WeaponOverrideUnit.Native && (!frequency || field.Name != "fire_rate")) return false;
        if (frequency)
        {
            code = ResultCode.WeaponRateUnrepresentable;
            if (!double.IsFinite(value)) return false;
        }
        if (field.Operation != WeaponOverrideOperation.Set)
        {
            var original = Original(applied, field.Name);
            if (frequency)
            {
                if (!double.IsFinite(original) || original == 0d)
                {
                    code = ResultCode.WeaponRateBaseUndefined;
                    return false;
                }
                original = 1d / original;
            }
            value = field.Operation switch
            {
                WeaponOverrideOperation.Add => original + value,
                WeaponOverrideOperation.Subtract => original - value,
                WeaponOverrideOperation.Multiply => original * value,
                WeaponOverrideOperation.Minimum => Math.Min(original, value),
                WeaponOverrideOperation.Maximum => Math.Max(original, value),
                _ => throw new ArgumentOutOfRangeException(nameof(field), "Unknown override operation.")
            };
        }
        if (frequency)
        {
            if (!double.IsFinite(value) || value == 0d) return false;
            var interval = (float)(1d / value);
            if (!float.IsFinite(interval) || interval == 0f) return false;
            value = interval;
        }
        else
        {
            // The destination is a float (an int for the burst length), so the value that is written is checked
            // as that type: an overflow to infinity would be read back as itself and reported as a success.
            if (!double.IsFinite(value) || !float.IsFinite((float)value)) return false;
            if (field.Name == "burst_count" && (value < int.MinValue || value > int.MaxValue)) return false;
        }
        code = "";
        return true;
    }

    /// <summary>What the instance's own block held for one name, before this package cloned it, expressed in the
    /// native member's unit. Recoil reads only arrive after the original recoil block and ranges were found.</summary>
    private static double Original(NativeValues applied, string name)
    {
        var block = applied.Block;
        return name switch
        {
            "fire_rate" => block.ShotDelay,
            "reload_time" => applied.ItemBlock!.DefaultReloadTime,
            "burst_count" => block.BurstShotCount,
            "spread_cone" => block.ShotgunConeSize,
            "spread_movement_scale" => block.HipFireSpread,
            "spread_aim_scale" => block.AimSpread,
            "recoil_horizontal" => applied.Recoil!.horizontalScale!.Min,
            "recoil_vertical" => applied.Recoil!.verticalScale!.Min,
            "recoil_recovery" => applied.Recoil!.spring,
            "recoil_camera_kick" => applied.Recoil!.power!.Min,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name,
                "an add or a subtract was resolved for a field with no original read")
        };
    }

    /// <summary>
    /// Whether one instance's own fire path samples the spread cone — the one destination a whole-profile write
    /// contributes only where it exists. The answer is the same family decision <see cref="Apply"/> makes, asked
    /// by the profile row before it builds its field set, so a cone is written exactly where the applier's own
    /// request check would accept one. An instance this machine cannot name answers false; the write that follows
    /// refuses it as stale, which is what a reference to a weapon that is gone deserves.
    /// </summary>
    internal bool ReadsCone(EntityReference equipment)
    {
        var weapon = _weaponOf(equipment);
        if (weapon == null || weapon.Pointer == IntPtr.Zero) return false;
        var archetype = weapon.m_archeType;
        if (archetype == null || archetype.Pointer == IntPtr.Zero) return false;
        return WeaponActionRuntime.HasDestination("spread_cone", Family(weapon, archetype), out _);
    }

    /// <summary>The live weapon one equipment reference currently names, re-checked through this applier's own
    /// lookup. It is the same instance-level entry point <see cref="Apply"/> writes to, offered to the row that
    /// has to reach the instance itself rather than its block: the profile row's end-of-state parameters act on
    /// the weapon the block belongs to. A reference this machine no longer answers for is null, which that row
    /// refuses by name.</summary>
    internal WeaponBase? CurrentWeapon(EntityReference equipment) => _weaponOf(equipment);
    internal bool HasCurrentEquipment(EntityReference equipment) => _weaponOf(equipment) != null || _meleeOf(equipment) != null;

    /// <summary>
    /// A copy of one archetype block, written field by field. There is no clone helper to call: the only
    /// parameterless constructor the interop type has is the game's own, reached through `il2cpp_object_new` plus
    /// an invoke of that constructor, and `MemberwiseClone` is not reachable at all. The constructor is still the
    /// right starting point — it writes the class defaults the game expects onto the new object and touches no
    /// static table, so the clone is never registered, never marked dirty and triggers no reload.
    ///
    /// The copy is explicit and preserves the source block's identity and every value the native weapon path can
    /// read. `PublicName`/`Description` are copied values; the two animation sequences keep the source references,
    /// which is deliberate because they are immutable in this package's hands. None of that registers the private
    /// clone in the game's data table.
    ///
    /// It is the one clone this package's instance-override write builds its candidate from, and it is private
    /// because that write is the only caller: nothing else changes a block this package owns a copy of.
    /// </summary>
    private static ArchetypeDataBlock CloneBlock(ArchetypeDataBlock source)
    {
        var clone = Construct<ArchetypeDataBlock>(Il2CppClassPointerStore<ArchetypeDataBlock>.NativeClassPtr);
        // The instance copy keeps the source block's identity and presentation metadata. Native authoring
        // readback identifies an equipped weapon through this persistent id, while the animation/name values
        // remain immutable shared objects; none of these assignments registers the clone in the static table.
        clone.persistentID = source.persistentID;
        clone.PublicName = source.PublicName;
        clone.Description = source.Description;
        clone.FireMode = source.FireMode;
        clone.RecoilDataID = source.RecoilDataID;
        clone.DamageBoosterEffect = source.DamageBoosterEffect;
        clone.Damage = source.Damage;
        clone.DamageFalloff = source.DamageFalloff;
        clone.StaggerDamageMulti = source.StaggerDamageMulti;
        clone.PrecisionDamageMulti = source.PrecisionDamageMulti;
        clone.DefaultClipSize = source.DefaultClipSize;
        clone.DefaultReloadTime = source.DefaultReloadTime;
        clone.CostOfBullet = source.CostOfBullet;
        clone.ShotDelay = source.ShotDelay;
        clone.ShellCasingSize = source.ShellCasingSize;
        clone.ShellCasingSpeedRange = source.ShellCasingSpeedRange;
        clone.PiercingBullets = source.PiercingBullets;
        clone.PiercingDamageCountLimit = source.PiercingDamageCountLimit;
        clone.HipFireSpread = source.HipFireSpread;
        clone.AimSpread = source.AimSpread;
        clone.EquipTransitionTime = source.EquipTransitionTime;
        clone.EquipSequence = source.EquipSequence;
        clone.AimTransitionTime = source.AimTransitionTime;
        clone.AimSequence = source.AimSequence;
        clone.BurstDelay = source.BurstDelay;
        clone.BurstShotCount = source.BurstShotCount;
        clone.ShotgunBulletCount = source.ShotgunBulletCount;
        clone.ShotgunConeSize = source.ShotgunConeSize;
        clone.ShotgunBulletSpread = source.ShotgunBulletSpread;
        clone.SpecialChargetupTime = source.SpecialChargetupTime;
        clone.SpecialCooldownTime = source.SpecialCooldownTime;
        clone.SpecialSemiBurstCountTimeout = source.SpecialSemiBurstCountTimeout;
        clone.Sentry_StartFireDelay = source.Sentry_StartFireDelay;
        clone.Sentry_RotationSpeed = source.Sentry_RotationSpeed;
        clone.Sentry_DetectionMaxRange = source.Sentry_DetectionMaxRange;
        clone.Sentry_DetectionMaxAngle = source.Sentry_DetectionMaxAngle;
        clone.Sentry_FireTowardsTargetInsteadOfForward = source.Sentry_FireTowardsTargetInsteadOfForward;
        clone.Sentry_ForceAimTowardsBody = source.Sentry_ForceAimTowardsBody;
        clone.Sentry_LongRangeThreshold = source.Sentry_LongRangeThreshold;
        clone.Sentry_ShortRangeThreshold = source.Sentry_ShortRangeThreshold;
        clone.Sentry_LegacyEnemyDetection = source.Sentry_LegacyEnemyDetection;
        clone.Sentry_FireTagOnly = source.Sentry_FireTagOnly;
        clone.Sentry_PrioTag = source.Sentry_PrioTag;
        clone.Sentry_StartFireDelayTagMulti = source.Sentry_StartFireDelayTagMulti;
        clone.Sentry_RotationSpeedTagMulti = source.Sentry_RotationSpeedTagMulti;
        clone.Sentry_DamageTagMulti = source.Sentry_DamageTagMulti;
        clone.Sentry_StaggerDamageTagMulti = source.Sentry_StaggerDamageTagMulti;
        clone.Sentry_CostOfBulletTagMulti = source.Sentry_CostOfBulletTagMulti;
        clone.Sentry_ShotDelayTagMulti = source.Sentry_ShotDelayTagMulti;
        return clone;
    }

    /// <summary>
    /// A copy of one recoil block. The three values the recoil path reads — `power`, `horizontalScale` and
    /// `verticalScale` — are `MinMaxValue` *objects*, not embedded numbers, so assigning the original's onto the
    /// clone would make both blocks share one range and every write here would reach back into the block the rest
    /// of the game is still using. They are cloned too, which is what makes an instance recoil change stay on the
    /// instance.
    /// </summary>
    private static RecoilDataBlock CloneRecoil(RecoilDataBlock source)
    {
        var clone = Construct<RecoilDataBlock>(Il2CppClassPointerStore<RecoilDataBlock>.NativeClassPtr);
        clone.persistentID = source.persistentID;
        clone.power = CloneRange(source.power);
        clone.spring = source.spring;
        clone.dampening = source.dampening;
        clone.hipFireCrosshairSizeDefault = source.hipFireCrosshairSizeDefault;
        clone.hipFireCrosshairRecoilPop = source.hipFireCrosshairRecoilPop;
        clone.hipFireCrosshairSizeMax = source.hipFireCrosshairSizeMax;
        clone.horizontalScale = CloneRange(source.horizontalScale);
        clone.verticalScale = CloneRange(source.verticalScale);
        clone.directionalSimilarity = source.directionalSimilarity;
        clone.worldToViewSpaceBlendHorizontal = source.worldToViewSpaceBlendHorizontal;
        clone.worldToViewSpaceBlendVertical = source.worldToViewSpaceBlendVertical;
        clone.recoilPosImpulse = source.recoilPosImpulse;
        clone.recoilPosShift = source.recoilPosShift;
        clone.recoilPosShiftWeight = source.recoilPosShiftWeight;
        clone.recoilPosStiffness = source.recoilPosStiffness;
        clone.recoilPosDamping = source.recoilPosDamping;
        clone.recoilPosImpulseWeight = source.recoilPosImpulseWeight;
        clone.recoilCameraPosWeight = source.recoilCameraPosWeight;
        clone.recoilAimingWeight = source.recoilAimingWeight;
        clone.recoilRotImpulse = source.recoilRotImpulse;
        clone.recoilRotStiffness = source.recoilRotStiffness;
        clone.recoilRotDamping = source.recoilRotDamping;
        clone.recoilRotImpulseWeight = source.recoilRotImpulseWeight;
        clone.recoilCameraRotWeight = source.recoilCameraRotWeight;
        clone.concussionIntensity = source.concussionIntensity;
        clone.concussionFrequency = source.concussionFrequency;
        clone.concussionDuration = source.concussionDuration;
        return clone;
    }

    private static MinMaxRange? CloneRange(MinMaxRange? source)
    {
        if (source == null || source.Pointer == IntPtr.Zero) return null;
        var clone = Construct<MinMaxRange>(Il2CppClassPointerStore<MinMaxRange>.NativeClassPtr);
        clone.Min = source.Min;
        clone.Max = source.Max;
        return clone;
    }

    /// <summary>
    /// The one way this package makes an object of a native class whose interop type has no public parameterless
    /// constructor. `il2cpp_object_new` allocates through the game's own allocator and the invoke of that class's
    /// own constructor gives the object the defaults the game wrote into it; the constructor is what the shipped
    /// code itself calls, so the result is an ordinary instance of the class and not a hand-built layout.
    ///
    /// The method is `unsafe` because the interop's invoke signature takes a `void**`, which no safe signature
    /// can spell; the array is null because none of these constructors takes a parameter, and the exception slot
    /// is how a throw inside the native constructor is reported back. The class pointer is the caller's, so each
    /// of the three call sites names its own type instead of this method guessing from `typeof(T)`.
    /// </summary>
    private static unsafe T Construct<T>(IntPtr klass) where T : Il2CppObjectBase
    {
        var constructor = IL2CPP.GetIl2CppMethod(klass, false, ".ctor", "System.Void", Array.Empty<string>());
        if (constructor == IntPtr.Zero) throw new InvalidOperationException("A native GameData block has no parameterless constructor.");
        var pointer = IL2CPP.il2cpp_object_new(klass);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("A native GameData block could not be allocated.");
        var exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(constructor, pointer, null, ref exception);
        if (exception != IntPtr.Zero) throw new InvalidOperationException("A native GameData block constructor threw.");
        return (T)Activator.CreateInstance(typeof(T), pointer)!;
    }

    /// <summary>Whether this archetype class carries the burst length field at all. `BWA_Burst` and
    /// `BWA_SemiBurst` declare `m_burstMax`; the other archetypes have no such field, and the request is refused
    /// for them rather than written to an offset that is a different member of a different class.</summary>
    private static WeaponActionRuntime.WeaponFamily Family(WeaponBase weapon, BulletWeaponArchetype archetype)
    {
        if (archetype.TryCast<global::Gear.BWA_Burst>() != null || archetype.TryCast<global::Gear.BWA_SemiBurst>() != null)
            return WeaponActionRuntime.WeaponFamily.Burst;
        return weapon.TryCast<global::Gear.Shotgun>() != null
            ? WeaponActionRuntime.WeaponFamily.Shotgun
            : WeaponActionRuntime.WeaponFamily.Bullet;
    }

    /// <summary>
    /// Whether the recoil block carries the objects the requested values are written through. `power`,
    /// `horizontalScale` and `verticalScale` are reference fields, so a block could in principle hold none of
    /// them; a request naming one of those values is refused rather than accepted with the value dropped.
    /// </summary>
    private static bool HasRecoilRange(RecoilDataBlock recoil, IReadOnlyList<WeaponOverrideField> fields)
    {
        foreach (var field in fields)
        {
            switch (field.Name)
            {
                case "recoil_horizontal":
                    if (recoil.horizontalScale == null || recoil.horizontalScale.Pointer == IntPtr.Zero) return false;
                    break;
                case "recoil_vertical":
                    if (recoil.verticalScale == null || recoil.verticalScale.Pointer == IntPtr.Zero) return false;
                    break;
                case "recoil_camera_kick":
                    if (recoil.power == null || recoil.power.Pointer == IntPtr.Zero) return false;
                    break;
            }
        }
        return true;
    }

    private static void SetBurstCount(BulletWeaponArchetype archetype, int count)
    {
        if (archetype.TryCast<global::Gear.BWA_Burst>() is { } burst) burst.m_burstMax = count;
        else if (archetype.TryCast<global::Gear.BWA_SemiBurst>() is { } semiBurst) semiBurst.m_burstMax = count;
    }
}
