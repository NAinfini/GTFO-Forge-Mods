using System;
using System.Collections.Generic;
using Gear;
using HarmonyLib;

namespace ForgeWeapon.Native;

/// <summary>
/// The reload row's `speed_multiplier` as the game's own reload duration.
///
/// A multiplier belongs to one weapon instance, and the instance's slot in this table is the whole of its
/// storage: <see cref="Arm"/> is called by the holder channel before the game's own reload entry point, the
/// game's own `ReloadTime` getter is divided by what <see cref="Of"/> answers for that instance, and the reload's
/// own end drops it again. Nothing here copies an archetype block, swaps a pointer or writes a shared data block,
/// so a reload's duration is scaled for every reader for the whole reload rather than only while one call is on
/// the stack.
///
/// The table is fixed and allocated once with the type, so the read on the game's reload path allocates nothing
/// and looks nothing up by name or key: it is at most <see cref="Capacity"/> pointer comparisons. The capacity is
/// four because one process holds one local player, and that player has one wielded weapon whose reload can be in
/// flight at a time; a second arming of the same instance takes that instance's own slot rather than a new one.
/// The table is emptied wherever the session that armed it ends or faults, so no multiplier outlives the holder
/// channel that wrote it.
/// </summary>
internal static class ReloadSpeedSlots
{
    /// <summary>Instances one process may hold a pending multiplier for at once.</summary>
    internal const int Capacity = 4;

    private static readonly IntPtr[] Weapons = new IntPtr[Capacity];
    private static readonly float[] Multipliers = new float[Capacity];
    private static bool _leased;

    /// <summary>
    /// Arms one live instance with the multiplier its reload runs under. The caller owns the row's refusal
    /// code; this boundary also rejects a missing instance, a zero native pointer, or a multiplier that does not
    /// remain finite and positive when represented as a float. False means one of those checks failed or no
    /// free slot exists. A refusal leaves every existing multiplier unchanged.
    /// </summary>
    internal static bool Arm(BulletWeapon weapon, double multiplier)
    {
        if (weapon == null || weapon.Pointer == IntPtr.Zero || !double.IsFinite(multiplier)
            || !float.IsFinite((float)multiplier) || (float)multiplier <= 0f) return false;
        if (!_leased)
        {
            _leased = true;
            ForgeCore.Gear.LeaseReload("ForgeWeapon", Factor);
        }
        var pointer = weapon.Pointer;
        var free = -1;
        for (var index = 0; index < Capacity; index++)
        {
            if (Weapons[index] == pointer)
            {
                Multipliers[index] = (float)multiplier;
                return true;
            }
            if (free < 0 && Weapons[index] == IntPtr.Zero) free = index;
        }
        if (free < 0) return false;
        Weapons[free] = pointer;
        Multipliers[free] = (float)multiplier;
        return true;
    }

    /// <summary>What one instance's reload duration is divided by, or zero when it carries no pending multiplier.
    /// A fixed-table scan with no allocation and no dictionary, which is what the game's own reload path may call.</summary>
    internal static float Of(IntPtr weapon)
    {
        if (weapon == IntPtr.Zero) return 0f;
        for (var index = 0; index < Capacity; index++)
            if (Weapons[index] == weapon) return Multipliers[index];
        return 0f;
    }

    /// <summary>Drops one instance's pending multiplier. The reload's own end, a weapon switch and a session end
    /// are the three callers, and each of them is a point where the reload this multiplier belonged to is over.</summary>
    internal static void Clear(IntPtr weapon)
    {
        for (var index = 0; index < Capacity; index++)
        {
            if (Weapons[index] != weapon) continue;
            Weapons[index] = IntPtr.Zero;
            Multipliers[index] = 0f;
        }
    }

    /// <summary>Drops every pending multiplier. It is the session boundary: a holder channel that is going away
    /// has no reload in flight that anything could still be reading a duration for.</summary>
    internal static void ClearAll()
    {
        ReloadSequenceBound.Restore();
        for (var index = 0; index < Capacity; index++)
        {
            Weapons[index] = IntPtr.Zero;
            Multipliers[index] = 0f;
        }
    }

    /// <summary>The factor ForgeCore's one <c>BulletWeapon.get_ReloadTime</c> postfix multiplies the game's own reload duration by
    /// (a lease held from the first <see cref="Arm"/>): the reciprocal of the instance's pending multiplier, one when it has none or
    /// no session is live. The weapon's archetype block is never touched, so the scaled duration is what the reload's animation and
    /// transfer run under for the whole reload.</summary>
    internal static float Factor(BulletWeapon weapon, float duration)
    {
        if (weapon == null || WeaponNativeSession.Offer(WeaponFeature.ReloadSpeed) == null) return 1f;
        var multiplier = Of(weapon.Pointer);
        return multiplier > 0f ? 1f / multiplier : 1f;
    }
}

// The reload's end: the completion callback of the reload animation, which clears the item's IsReloading flag
// (ForgeCore's one patch of it, `GameHooks.ReloadDone`). The pending multiplier is dropped with it, so the next
// reload of this instance answers its own duration.
internal static class ReloadTimeEnded
{
    internal static void Subscribe() => ForgeCore.GameHooks.ReloadDone.After(Ended);

    private static void Ended(ItemEquippable __instance, bool ran)
    {
        if (__instance == null) return;
        ReloadSequenceBound.Ended(__instance.Pointer);
        if (WeaponNativeSession.Offer(WeaponFeature.ReloadSpeed) == null) return;
        ReloadSpeedSlots.Clear(__instance.Pointer);
    }
}

// The weapon being put away: `BulletWeapon.OnUnWield` (unique RVA 0x1461D20 in the game build) is the magazine family's
// own end of the item's use, which is the body a switch to another weapon reaches. A pending multiplier is dropped with
// it, so the next weapon to be wielded, and this one when it comes back, answers its own reload duration.
// ForgeCore patches the body once (`GameHooks.BulletWeaponUnwielded`); the multiplier is dropped after every other patch of it.
internal static class ReloadTimeUnwielded
{
    internal static void Subscribe() => ForgeCore.GameHooks.BulletWeaponUnwielded.After(Unwielded);

    private static void Unwielded(BulletWeapon weapon, bool ran)
    {
        if (weapon == null) return;
        ReloadSequenceBound.Ended(weapon.Pointer);
        if (WeaponNativeSession.Offer(WeaponFeature.ReloadSpeed) == null) return;
        ReloadSpeedSlots.Clear(weapon.Pointer);
    }
}

/// <summary>
/// The reload animation's own length, made equal to the reload time the author wrote (on that author's weapon only,
/// see <see cref="ReloadSequenceScope"/>). The game plays the reload
/// sequence scaled so that its `Empty` marker lands on `ReloadTime`, and the reload ends with the sequence's latest
/// item. A part whose sequence carries items after its marker (the revolver front: foley sounds at 2.3 s and 2.5 s
/// behind a 2.0 s marker) therefore runs longer than the author's value (2.3 s authored, 2.9 s played). Those trailing
/// items are only sounds, so they are held at the marker and the sequence ends where the reload time says it does.
///
/// The sequence items are the part's data block rows, shared by every weapon built on that front part, so the edit is
/// undone when the reload ends (the reload's completion, the weapon being put away, the session ending): a stock weapon
/// with the same front part plays its own tail again on its next reload. The held rows sit in a fixed table, so the
/// reload path allocates nothing; a sequence with more trailing rows than the table holds is left as the game wrote it.
/// </summary>
internal static class ReloadSequenceBound
{
    private const int Capacity = 16;
    private static readonly GameData.WeaponAnimSequenceItem?[] Held = new GameData.WeaponAnimSequenceItem?[Capacity];
    private static readonly float[] HeldTimes = new float[Capacity];
    private static int _held;
    private static IntPtr _entered, _owner;
    /// <summary>A sequence too long for the table: a Warning written once per weapon.</summary>
    private static readonly ForgeCore.OnceReporter Full = new(line => ForgeEngine.Plugin.PluginLog.LogWarning(line));

    internal static void Enter(IntPtr weapon) => _entered = weapon;
    internal static void Leave() => _entered = IntPtr.Zero;

    internal static void Apply(Il2CppSystem.Collections.Generic.List<GameData.WeaponAnimSequenceItem>? sequence)
    {
        if (_entered == IntPtr.Zero || sequence == null) return;
        float marker = 0f, latest = 0f;
        int trailing = 0;
        for (var index = 0; index < sequence.Count; index++)
        {
            var item = sequence[index];
            if (item.Type == GameData.eWeaponAnimSequenceItemType.Empty && item.TriggerTime > marker) marker = item.TriggerTime;
            if (item.TriggerTime > latest) latest = item.TriggerTime;
        }
        if (!(marker > 0f) || !(latest > marker)) return;
        for (var index = 0; index < sequence.Count; index++) if (sequence[index].TriggerTime > marker) trailing++;
        Restore();
        if (trailing > Capacity)
        {
            if (ForgeCore.Log.Developer)
                Full.Report("weapon " + _entered, ForgeEngine.Plugin.PluginName + ": the reload sequence of weapon " + _entered + " has " + trailing
                    + " rows after its marker, more than the " + Capacity + " that can be put back; it plays at the game's own length.");
            return;
        }
        _owner = _entered;
        for (var index = 0; index < sequence.Count; index++)
        {
            var item = sequence[index];
            if (!(item.TriggerTime > marker)) continue;
            Held[_held] = item;
            HeldTimes[_held++] = item.TriggerTime;
            item.TriggerTime = marker;
        }
    }

    /// <summary>The reload of <paramref name="weapon"/> is over: its sequence rows get their own times back.</summary>
    internal static void Ended(IntPtr weapon)
    {
        if (weapon != IntPtr.Zero && weapon == _owner) Restore();
    }

    /// <summary>Puts every held row back. Also the session boundary.</summary>
    internal static void Restore()
    {
        for (var index = 0; index < _held; index++)
        {
            try { Held[index]!.TriggerTime = HeldTimes[index]; }
            catch (Exception failure) { NativeCallFailure.Note("ReloadSequenceBound.Restore", failure); }
            Held[index] = null;
        }
        _held = 0;
        _owner = IntPtr.Zero;
    }
}

// The reload entry is ForgeCore's one patch of `TryTriggerReloadAnimationSequence` (`GameHooks.ReloadTriggered`): the
// scope opens in its before and closes whether the body returned or threw.
internal static class ReloadSequenceScope
{
    internal static void Subscribe() => ForgeCore.GameHooks.ReloadTriggered.Around<bool>(Enter, Leave, Faulted);

    private static bool Enter(ItemEquippable item)
    {
        // Only a weapon whose reload time an author wrote has its sequence held to that time; a stock weapon keeps
        // the game's own sequence, tail sounds and all.
        if (WeaponNativeSession.Offer(WeaponFeature.ReloadSpeed) is { } session && session.Overrides.WritesReloadTime(item.Pointer))
            ReloadSequenceBound.Enter(item.Pointer);
        return true;
    }

    private static void Leave(ItemEquippable item, bool state, bool ran) => ReloadSequenceBound.Leave();

    private static void Faulted(ItemEquippable item, bool state, Exception error) => ReloadSequenceBound.Leave();
}

[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.DoItemAnimationSequence))]
internal static class ReloadSequenceBounded
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Il2CppSystem.Collections.Generic.List<GameData.WeaponAnimSequenceItem> sequence)
        => ReloadSequenceBound.Apply(sequence);
}

/// <summary>The hook classes this family installs. They are declared beside the table they read, and
/// <see cref="WeaponNativeHooks"/> carries them into the package's one installation loop, so a hook class that is
/// not listed here is never patched.</summary>
internal static class ReloadSpeedHooks
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(ReloadSequenceBounded)
    });

    internal static void Subscribe()
    {
        ReloadTimeUnwielded.Subscribe();
        ReloadTimeEnded.Subscribe();
        ReloadSequenceScope.Subscribe();
    }
}
