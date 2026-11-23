using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using Il2CppInterop.Runtime;
using LocalMeleeWeapon = global::Gear.MeleeWeaponFirstPerson;
using ChargeState = global::Gear.MWS_ChargeUp;
using AnimationBlock = global::GameData.MeleeAnimationSetDataBlock;

namespace ForgeWeapon.Native;

internal sealed partial class WeaponOverrideApplier
{
    private sealed class MeleeApplied
    {
        internal EntityReference Equipment = null!;
        internal LocalMeleeWeapon Weapon = null!;
        internal AnimationBlock Block = null!;
        internal float Before;
        internal float Written;
        internal ChargeState? Charge;
        internal float ChargeBefore;
        internal float ChargeWritten;
        internal bool DoneWritten;
        internal MeleeApplied? Pending;
    }
    private readonly Dictionary<string, MeleeApplied> _meleeApplied = new(StringComparer.Ordinal);

    private WeaponOverrideWrite ApplyMelee(EntityReference equipment, LocalMeleeWeapon weapon,
        IReadOnlyList<WeaponOverrideField> fields, out string code)
    {
        code = ResultCode.OverrideFieldUnknown;
        if (fields.Count != 1 || fields[0].Name != "melee_charge_time" || fields[0].Unit != WeaponOverrideUnit.Native)
            return WeaponOverrideWrite.Refused;
        _meleeApplied.TryGetValue(equipment.Id, out var existing);
        var displaced = existing != null && (existing.Equipment != equipment || existing.Weapon.Pointer != weapon.Pointer) ? existing : null;
        if (displaced != null) existing = null;
        var staleEntries = new List<MeleeApplied>();
        foreach (var owned in _meleeApplied.Values)
            if (owned == displaced || owned.Equipment.Id != equipment.Id && owned.Weapon.Pointer == weapon.Pointer)
                staleEntries.Add(owned);
        MeleeApplied? attempt = null;
        bool mutated = false;
        try
        {
            if (weapon.Pointer == IntPtr.Zero || weapon.MeleeAnimationData is not { } current || current.Pointer == IntPtr.Zero)
            { code = ResultCode.OverrideBlockMissing; return WeaponOverrideWrite.Refused; }
            var baseline = current.MaxDamageChargeTime;
            if (existing?.Pending is { } pending && ForgeCore.NativeObjects.Same(current, pending.Block) && baseline.Equals(pending.Written))
                baseline = pending.Before;
            if (existing != null && (ForgeCore.NativeObjects.Same(current, existing.Block) || ForgeCore.NativeObjects.Same(current, existing.Pending?.Block))
                && baseline.Equals(existing.Written)) baseline = existing.Before;
            var field = fields[0];
            double value = field.Operation switch
            {
                WeaponOverrideOperation.Set => field.Value,
                WeaponOverrideOperation.Add => baseline + field.Value,
                WeaponOverrideOperation.Subtract => baseline - field.Value,
                _ => double.NaN
            };
            var charge = weapon.CurrentState?.TryCast<ChargeState>();
            var speed = charge == null ? 1f : weapon.MeleeSpeedModifier;
            if (!float.IsFinite((float)value) || charge != null && !float.IsFinite((float)value / speed))
            { code = ResultCode.InputUnsupported; return WeaponOverrideWrite.Refused; }
            if (staleEntries.Count != 0)
            {
                foreach (var stale in staleEntries)
                    if (!RestoreMelee(stale, out code)) return WeaponOverrideWrite.Failed;
                return ApplyMelee(equipment, weapon, fields, out code);
            }
            if (existing?.Pending is { } recovery)
            {
                if (!WithdrawMelee(recovery)) { code = ResultCode.OverrideRestoreUnconfirmed; return WeaponOverrideWrite.Failed; }
                if (ForgeCore.NativeObjects.Same(weapon.MeleeAnimationData, recovery.Block)) existing.Block = recovery.Block;
                existing.Pending = null;
                return ApplyMelee(equipment, weapon, fields, out code);
            }
            var candidate = CloneMelee(current);
            candidate.MaxDamageChargeTime = (float)value;
            attempt = new MeleeApplied
            {
                Equipment = equipment,
                Weapon = weapon,
                Block = candidate,
                Before = current.MaxDamageChargeTime,
                Written = (float)value,
                Charge = charge,
                ChargeBefore = charge?.m_maxDamageTime ?? 0f,
                ChargeWritten = (float)value / speed,
                DoneWritten = charge != null && charge.m_elapsed >= (float)value / speed
            };
            mutated = true;
            weapon.MeleeAnimationData = candidate;
            if (charge != null)
            {
                charge.m_maxDamageTime = attempt.ChargeWritten;
                charge.m_chargeUpDone = attempt.DoneWritten;
            }
            if (!ForgeCore.NativeObjects.Same(weapon.MeleeAnimationData, candidate) || candidate.MaxDamageChargeTime != (float)value
                || charge != null && (charge.m_maxDamageTime != attempt.ChargeWritten || charge.m_chargeUpDone != attempt.DoneWritten))
                throw new InvalidOperationException("Melee override readback failed.");
            // The next restore withdraws the whole merged value, not merely the preceding writer's value.
            attempt.Before = baseline;
            if (existing != null && charge != null && ForgeCore.NativeObjects.Same(charge, existing.Charge))
            {
                if (attempt.ChargeBefore.Equals(existing.ChargeWritten)) attempt.ChargeBefore = existing.ChargeBefore;
            }
            _meleeApplied[equipment.Id] = attempt;
            code = "";
            return WeaponOverrideWrite.Applied;
        }
        catch (Exception error)
        {
            SafeReport("weapon.melee-override-write-failed equipment=" + equipment.Id + ": " + error.GetType().Name);
            if (!mutated) { code = ResultCode.OverrideWriteFailed; return WeaponOverrideWrite.Failed; }
            if (WithdrawMelee(attempt!))
            {
                if (existing != null && ForgeCore.NativeObjects.Same(weapon.MeleeAnimationData, attempt!.Block)) existing.Block = attempt.Block;
                code = ResultCode.OverrideWriteRolledBack;
                return WeaponOverrideWrite.Failed;
            }
            if (existing != null) { existing.Pending = attempt; _meleeApplied[equipment.Id] = existing; }
            else _meleeApplied[equipment.Id] = attempt!;
            code = ResultCode.OverrideWriteUnconfirmed;
            return WeaponOverrideWrite.Unconfirmed;
        }
    }

    private static bool WithdrawMelee(MeleeApplied entry)
    {
        bool restored = true;
        try
        {
            if (entry.Weapon.Pointer != IntPtr.Zero && ForgeCore.NativeObjects.Same(entry.Weapon.MeleeAnimationData, entry.Block)
                && entry.Block.MaxDamageChargeTime.Equals(entry.Written))
            {
                entry.Block.MaxDamageChargeTime = entry.Before;
                if (!entry.Block.MaxDamageChargeTime.Equals(entry.Before)) restored = false;
            }
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponOverrideApplier.Melee.WithdrawMelee", failure); restored = false; }
        // Charge states are native objects of their own. A new Enter/current state owns its own caches.
        try
        {
            if (entry.Charge is { } charge && entry.Weapon.Pointer != IntPtr.Zero
                && ForgeCore.NativeObjects.Same(entry.Weapon.CurrentState, charge))
            {
                if (charge.m_maxDamageTime.Equals(entry.ChargeWritten))
                {
                    charge.m_maxDamageTime = entry.ChargeBefore;
                    if (!charge.m_maxDamageTime.Equals(entry.ChargeBefore)) restored = false;
                }
                if (charge.m_chargeUpDone == entry.DoneWritten)
                {
                    // Recompute from the native state's current elapsed time, never revive a stale done flag.
                    charge.m_chargeUpDone = charge.m_elapsed >= charge.m_maxDamageTime;
                }
            }
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponOverrideApplier.Melee.WithdrawMelee#2", failure); restored = false; }
        return restored;
    }

    private bool RestoreMelee(MeleeApplied entry, out string code)
    {
        code = "";
        if (entry.Pending is { } pending)
        {
            if (!WithdrawMelee(pending)) { code = ResultCode.OverrideRestoreUnconfirmed; return false; }
            if (ForgeCore.NativeObjects.Same(entry.Weapon.MeleeAnimationData, pending.Block)) entry.Block = pending.Block;
            entry.Pending = null;
        }
        if (!WithdrawMelee(entry)) { code = ResultCode.OverrideRestoreUnconfirmed; return false; }
        _meleeApplied.Remove(entry.Equipment.Id);
        return true;
    }

    private static AnimationBlock CloneMelee(AnimationBlock source)
    {
        var clone = Construct<AnimationBlock>(Il2CppClassPointerStore<AnimationBlock>.NativeClassPtr);
        clone.HoldToChargeTime = source.HoldToChargeTime;
        clone.MaxDamageChargeTime = source.MaxDamageChargeTime;
        clone.AutoAttackTime = source.AutoAttackTime;
        clone.AutoAttackWarningTime = source.AutoAttackWarningTime;
        clone.FPIdleAnim = source.FPIdleAnim;
        clone.FPSettleAnim = source.FPSettleAnim;
        clone.FPWalkAnim = source.FPWalkAnim;
        clone.FPRunAnim = source.FPRunAnim;
        clone.FPJumpAnim = source.FPJumpAnim;
        clone.FPLandAnim = source.FPLandAnim;
        clone.FPChargeCancelAnimRight = source.FPChargeCancelAnimRight;
        clone.FPChargeCancelAnimLeft = source.FPChargeCancelAnimLeft;
        clone.FPAttackMissRight = source.FPAttackMissRight;
        clone.FPAttackMissLeft = source.FPAttackMissLeft;
        clone.FPAttackHitRight = source.FPAttackHitRight;
        clone.FPAttackHitLeft = source.FPAttackHitLeft;
        clone.FPAttackPush = source.FPAttackPush;
        clone.FPAttackChargeUpRight = source.FPAttackChargeUpRight;
        clone.FPAttackChargeUpLeft = source.FPAttackChargeUpLeft;
        clone.FPAttackChargeUpReleaseRight = source.FPAttackChargeUpReleaseRight;
        clone.FPAttackChargeUpReleaseLeft = source.FPAttackChargeUpReleaseLeft;
        clone.FPAttackChargeUpHitRight = source.FPAttackChargeUpHitRight;
        clone.FPAttackChargeUpHitLeft = source.FPAttackChargeUpHitLeft;
        clone.TPAnimHashIdle = source.TPAnimHashIdle;
        clone.TPAnimHashIdleCrouch = source.TPAnimHashIdleCrouch;
        clone.TPAnimHashAttackLeft = source.TPAnimHashAttackLeft;
        clone.TPAnimHashAttackLeftCrouch = source.TPAnimHashAttackLeftCrouch;
        clone.TPAnimHashAttackLeftCharge = source.TPAnimHashAttackLeftCharge;
        clone.TPAnimHashAttackLeftChargeCrouch = source.TPAnimHashAttackLeftChargeCrouch;
        clone.TPAnimHashAttackLeftRelease = source.TPAnimHashAttackLeftRelease;
        clone.TPAnimHashAttackLeftReleaseCrouch = source.TPAnimHashAttackLeftReleaseCrouch;
        return clone;
    }
}
