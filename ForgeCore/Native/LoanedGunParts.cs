using System;
using System.Collections.Generic;
using HarmonyLib;
using Player;
using GearPartSpawner = Gear.GearPartSpawner;

namespace ForgeCore;

public static partial class LoanedGun
{
    // ---- the new gun's parts, at once -----------------------------------------------------------------------------------

    // The swap in flight (from the ask to the new gun's parts being finished), the slot's instance when it was asked, and the game's
    // part spawner coroutines started meanwhile with the part holder each belongs to.
    private static bool _swapping, _matched, _finishing;
    private static IntPtr _swappedFrom;
    private static readonly List<(IntPtr Holder, Il2CppSystem.Collections.IEnumerator Parts)> Assembling = new();

    // Armed before the ask: the game's part spawner of the new gun may start inside it.
    private static void BeginSwap()
    {
        _swappedFrom = TryLocalSpecial(out _, out var leaving) && leaving!.Instance != null ? leaving.Instance.Pointer : IntPtr.Zero;
        Assembling.Clear();
        _matched = false;
        _swapping = true;
    }

    /// <summary>The swap asked by <see cref="Equip"/> is over (or abandoned): no spawner coroutine is kept any more.</summary>
    public static void EndSwap()
    {
        _swapping = false;
        Assembling.Clear();
    }

    /// <summary>Call every frame of a swap, once the slot may hold the new gun. The game spawns a gun's parts one per frame
    /// (<see cref="GearAssembly"/>), which leaves the hand empty for a dozen frames after the old gun goes. Once the new gun's instance
    /// stands in the slot, its own part holder's spawner runs its remaining steps now, exactly as the coroutine would over the next frames,
    /// ending with the game's all-parts-spawned callback. Spawners of other holders are left to the game. A forced release that cannot
    /// wait is the caller's; an orderly one that can, does not call this.</summary>
    public static void FinishParts(BackpackItem item)
    {
        if (!_swapping) return;
        var instance = item.Instance;
        if (instance == null || instance.WasCollected || instance.Pointer == _swappedFrom) return;
        var holder = instance.TryCast<ItemEquippable>()?.GearPartHolder;
        if (holder == null) return;
        for (var i = Assembling.Count - 1; i >= 0; i--)
        {
            var (owner, parts) = Assembling[i];
            if (owner != holder.Pointer) continue;
            Assembling.RemoveAt(i);
            _matched = true;
            int steps;
            _finishing = true;
            try { steps = GearAssembly.Finish(parts.MoveNext); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Finishing the new gun's parts at once failed; the game assembles the rest: " + error); continue; }
            finally { _finishing = false; }
            if (steps < 0) Plugin.Logger.ErrorOnce("The new gun's part spawner still ran after " + GearAssembly.MaxSteps + " steps; the game assembles the rest.");
        }
        if (!item.IsLoaded) return;
        // Loaded without one captured spawner of this gun: the capture missed it (the hook did not run, or the game built the gun before
        // the swap was armed), so the parts came at the game's pace and the hand stayed empty meanwhile.
        if (!_matched) Plugin.Logger.ErrorOnce("The new gun's part spawner was not captured; its parts were assembled at the game's own pace (one per frame).");
        EndSwap();
    }

    /// <summary>The spawner coroutine <paramref name="parts"/> of <paramref name="spawner"/> took a step (<paramref name="more"/>: it is not
    /// over): kept, with its part holder, while a swap is in flight.</summary>
    internal static void PartsStepped(GearPartSpawner spawner, Il2CppSystem.Collections.IEnumerator parts, bool more)
    {
        if (!_swapping || _finishing) return;
        for (var i = Assembling.Count - 1; i >= 0; i--)
        {
            if (Assembling[i].Parts.Pointer != parts.Pointer) continue;
            if (!more) Assembling.RemoveAt(i);
            return;
        }
        var holder = spawner.m_holder;
        if (more && holder != null) Assembling.Add((holder.Pointer, parts));
    }
}

/// <summary>Each step of the game's part spawner coroutine (the compiler-made iterator of <c>GearPartSpawner.SpawnAllParts</c>), whose
/// first step runs when the game starts it: the coroutine is handed to <see cref="LoanedGun"/> while a swap is in flight. The
/// iterator's creating method is not hooked: it is a one-line stub the game's code does not call through the patched entry. The
/// coroutine itself is the game's; nothing is replaced or sent.</summary>
[HarmonyPatch(typeof(GearPartSpawner._SpawnAllParts_d__15), nameof(GearPartSpawner._SpawnAllParts_d__15.MoveNext))]
internal static class GearPartsStartedHook
{
    [HarmonyPostfix]
    private static void Postfix(GearPartSpawner._SpawnAllParts_d__15 __instance, bool __result)
    {
        try
        {
            var spawner = __instance.__4__this;
            if (spawner != null) LoanedGun.PartsStepped(spawner, __instance.Cast<Il2CppSystem.Collections.IEnumerator>(), __result);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Reading the game's part spawner failed: " + error); }
    }
}