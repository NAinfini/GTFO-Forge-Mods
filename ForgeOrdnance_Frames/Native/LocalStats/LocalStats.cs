using System;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>The local player's walk speed, jump, stamina cost and hit flinch, changed through the game's own paths. This folder knows
/// nothing of suits: whoever wears one sets the sources below (the first time the suit goes on, see <see cref="PilotLazyHooks"/>) and
/// the hooks ask them. Only this mod patches these methods, so the mechanism stays here; when a second package needs it, the whole
/// folder moves into ForgeCore as <c>PlayerStats</c>. Each peer changes only its own local player and nothing is sent.
/// <para>Walk: the velocity every locomotion state asks for (<c>PLOC_Base.GetHorizontalVelocityFromInput</c>) goes through
/// <see cref="Velocity"/>, and <see cref="CarryUnslowed"/> lifts the game's carry slowdown (<c>PlayerAgent.moveSpeedMult</c>, which the
/// states set before asking). Jump: <c>PLOC_Jump.Enter</c> reads <c>PlayerDataBlock.jumpVelInitial</c> and every air step clamps the
/// rise to <c>jumpVerticalVelocityMax</c>; for the local player's own calls both are scaled by <see cref="JumpSpeed"/> for the
/// duration of the game's call and put back right after it (the clamp only while rising). Stamina: while <see cref="NoStamina"/>
/// holds, <c>PlayerStamina.UseStamina</c> is skipped. Flinch: while <see cref="NoHitreact"/> holds, <c>Dam_PlayerDamageLocal.Hitreact</c>
/// is skipped.</para></summary>
internal static class LocalStats
{
    internal static Func<PlayerAgent, Vector3, Vector3>? Velocity;
    internal static Func<bool>? CarryUnslowed;
    internal static Func<PlayerAgent?, float>? JumpSpeed;
    internal static Func<PlayerAgent?, bool>? NoStamina;
    internal static Func<bool>? NoHitreact;

    /// <summary>Puts on the two hooks on the game's busiest paths (every locomotion step, every airborne step). Under Il2CppInterop a
    /// removed patch leaves the forwarding detour and the generated method behind, so they stay on once on.</summary>
    internal static void Ensure(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PLOC_Base), "GetHorizontalVelocityFromInput"),
            prefix: new HarmonyMethod(typeof(LocalStats), nameof(WalkPrefix)), postfix: new HarmonyMethod(typeof(LocalStats), nameof(WalkPostfix)));
        harmony.Patch(AccessTools.Method(typeof(PLOC_Base), "UpdateVerticalVelocityInAir"),
            prefix: new HarmonyMethod(typeof(LocalStats), nameof(RisePrefix)), postfix: new HarmonyMethod(typeof(LocalStats), nameof(RisePostfix)));
    }

    private static void WalkPrefix(PLOC_Base __instance)
    {
        if (CarryUnslowed == null || !CarryUnslowed()) return;
        var owner = __instance.m_owner;
        if (owner != null && owner.IsLocallyOwned && owner.moveSpeedMult < 1f) owner.moveSpeedMult = 1f;
    }

    private static void WalkPostfix(PLOC_Base __instance, ref Vector3 __result)
    {
        if (Velocity == null) return;
        var owner = __instance.m_owner;
        if (owner == null || !owner.IsLocallyOwned) return;
        try { __result = Velocity(owner, __result); }
        catch (Exception error) { Plugin.Error("Local walk speed failed: " + error); }
    }

    private static void RisePrefix(PLOC_Base __instance, out float __state)
    {
        __state = 0f;
        var owner = __instance.m_owner;
        var speed = JumpSpeed?.Invoke(owner) ?? 1f;
        if (speed == 1f || owner!.Locomotion == null || owner.Locomotion.VerticalVelocity.y <= 0f) return;
        var data = owner.PlayerData;
        if (data == null) return;
        __state = data.jumpVerticalVelocityMax;
        data.jumpVerticalVelocityMax = __state * speed;
    }

    private static void RisePostfix(PLOC_Base __instance, float __state)
    {
        if (__state <= 0f) return;
        var data = __instance.m_owner?.PlayerData;
        if (data != null) data.jumpVerticalVelocityMax = __state;
    }
}

[HarmonyPatch(typeof(PLOC_Jump), nameof(PLOC_Jump.Enter))]
internal static class LocalJump
{
    [HarmonyPrefix]
    private static void Prefix(PLOC_Jump __instance, out float __state)
    {
        __state = 0f;
        var owner = __instance.m_owner;
        var speed = LocalStats.JumpSpeed?.Invoke(owner) ?? 1f;
        if (speed == 1f) return;
        var data = owner!.PlayerData;
        if (data == null) return;
        __state = data.jumpVelInitial;
        data.jumpVelInitial = __state * speed;
    }

    [HarmonyPostfix]
    private static void Postfix(PLOC_Jump __instance, float __state)
    {
        if (__state <= 0f) return;
        var data = __instance.m_owner?.PlayerData;
        if (data != null) data.jumpVelInitial = __state;
    }
}

[HarmonyPatch(typeof(PlayerStamina), nameof(PlayerStamina.UseStamina))]
internal static class LocalStamina
{
    [HarmonyPrefix]
    private static bool Prefix(PlayerStamina __instance) => !(LocalStats.NoStamina?.Invoke(__instance.m_owner) ?? false);
}

[HarmonyPatch(typeof(Dam_PlayerDamageLocal), "Hitreact")]
internal static class LocalHitreact
{
    [HarmonyPrefix]
    private static bool Prefix() => !(LocalStats.NoHitreact?.Invoke() ?? false);
}
