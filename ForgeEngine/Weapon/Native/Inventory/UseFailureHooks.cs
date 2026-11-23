using HarmonyLib;
using Gear;
using UnityEngine;

namespace ForgeWeapon.Native;

// These are request-local snapshots of branches in build 20403457, not inferred outcomes from a missing callback.
// Native bodies, offsets and input semantics: evidence/item-use-failures.json.
internal static class UseFailureReads
{
    internal const string Empty = "empty-consumable";
    internal const string Cooldown = "throw-cooldown";
    internal const string NotReady = "throw-not-ready";
    internal const string NoNeed = "resource-not-needed";

    /// <summary>A reason's position in the `use_failure_reason` set: the value the trigger's enum port carries and
    /// the byte a client reports it with.</summary>
    internal static byte Member(string reason) => reason switch
    {
        Empty => 0,
        Cooldown => 1,
        NotReady => 2,
        _ => 3
    };

    internal static bool Requested(ItemEquippable item, InputAction action)
    {
        // The plain field first; the owner is then read once (Unity equality: a destroyed agent reads as null).
        if (!item.m_isWielded) return false;
        var agent = item.Owner;
        if (agent == null || agent.Owner == null || !agent.Owner.IsLocal) return false;
        return InputMapper.GetButtonDown != null && InputMapper.GetButtonDown.Invoke(action, agent.InputFilter);
    }

    internal static string? ThrowRefusal(ThrowingWeapon item)
    {
        if (!Requested(item, InputAction.Fire)) return null;
        // Update takes this branch before reading FireButtonPressed (which busy-item filtering suppresses).
        if (item.m_waitingForReady) return Cooldown;
        // A press during charge can release a throw; it is not a refused request.
        if (item.m_inThrowCharge || item.m_inThrowRelease || !item.FireButtonPressed) return null;
        if (!item.m_ready) return NotReady;
        return item.CanWield ? null : Empty;
    }
}

/// <summary>One frame of a throwable's Update: the refusal the request met (null when it was not refused) and whether the throw was
/// already under way when the frame began.</summary>
internal readonly record struct ThrowFrame(string? Refusal, bool Underway, bool Thrown);

[HarmonyPatch(typeof(ThrowingWeapon), "Update")]
internal static class ThrowUseChecked
{
    [HarmonyPrefix]
    internal static void Prefix(ThrowingWeapon __instance, out ThrowFrame __state)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.WeaponUsePoll);
        __state = new(ReloadInventoryHooks.GuardUseObservation(WeaponFeature.Equipment, static item => UseFailureReads.ThrowRefusal(item), __instance, (string?)null),
            __instance.m_inThrowCharge || __instance.m_inThrowRelease, __instance.m_hasThrown);
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(ThrowingWeapon __instance, ThrowFrame __state)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.WeaponUsePoll);
        if (__state.Refusal != null)
            ReloadInventoryHooks.GuardInventory(WeaponFeature.Equipment, static (observer, call) => { if (ClientWeaponReport.Current?.UseFailed(call.Item, call.Reason, Time.frameCount) != true) observer.Refused(call.Item, call.Reason, Time.frameCount); },
                (Item: __instance, Reason: __state.Refusal));
        // A throwable is used from the moment its charge begins, which is before the last charge can be spent and the item
        // retired; the use sequence entry is not a reliable start for it. The use ends when the throw is over, so the next
        // throw of the same item is a use of its own.
        var underway = __instance.m_inThrowCharge || __instance.m_inThrowRelease;
        if (underway != __state.Underway)
            ReloadInventoryHooks.GuardInventory(WeaponFeature.Equipment,
                static (observer, call) =>
                {
                    if (ClientWeaponReport.Current is { } report && (call.Started ? report.UseStarted(call.Item) : report.UseEnded(call.Item))) return;
                    if (call.Started) observer.UseStarted(call.Item); else observer.UseEnded(call.Item);
                },
                (Item: __instance, Started: underway));
        // The throw leaves the hand on the frame the weapon's own Update sets m_hasThrown. Its velocity is the weapon's own
        // thrust along the owner's camera ray (ThrowingWeapon.Throw takes a strength, not a vector).
        if (!__state.Thrown && __instance.m_hasThrown)
            WeaponNativeSession.Offer(WeaponFeature.Equipment)?.Guard(static (session, thrower) =>
            {
                var owner = thrower.Owner;
                var camera = owner == null ? null : owner.FPSCamera;
                if (camera == null) return;
                var velocity = camera.CameraRayDir * thrower.CalcThrust();
                if (!session.ClientFacts.Throw(thrower, velocity)) EquipmentEventFacts.Current?.ThrowReleased(thrower, velocity);
            }, __instance);
    }
}

[HarmonyPatch(typeof(ResourcePackFirstPerson), "CheckIfTimerIsActivated")]
internal static class ResourceUseChecked
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(ResourcePackFirstPerson __instance, bool needResource, bool btnDown, bool __result)
    {
        // btnDown is the native caller's GetButtonDown (including separate-use mode). False alone means idle too.
        if (btnDown && !needResource && !__result)
            ReloadInventoryHooks.GuardInventory(WeaponFeature.Ammunition, static (observer, item) => { if (ClientWeaponReport.Current?.UseFailed(item, UseFailureReads.NoNeed, Time.frameCount) != true) observer.Refused(item, UseFailureReads.NoNeed, Time.frameCount); }, __instance);
    }
}
