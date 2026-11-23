using System;
using System.Linq;
using System.Reflection;

namespace ForgeEngine.GameBindings;

/// <summary>Runtime's hook declarations, installed through ForgeCore's <see cref="ForgeCore.Hooks"/>: the writer of each target and the
/// refusal text every caller reports. A game method several packages write or read around (enemy spawn, prefab build, wave start,
/// the shared entries below) is not here: ForgeCore patches it once and packages subscribe to its <see cref="ForgeCore.GameHooks"/>
/// bracket, through Runtime's world-bound entry where the reader needs the host's epoch.</summary>
public static partial class NativeHookRegistration
{
    internal static string Refusal(string reason, MethodBase method)
        => reason + " target=" + TargetIdentity(method);
    internal static string TargetIdentity(MethodBase? target)
    {
        if (target == null) return "<null>";
        string identity;
        try { identity = (target.DeclaringType?.FullName ?? "<unknown>") + "." + target.Name; }
        catch { return "<metadata-unavailable>"; } // The marker is the recorded reason: reflection on the target's declaring type failed.
        try { return identity + "(" + string.Join(",", target.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)) + ")"; }
        catch { return identity + "(?)"; } // The marker is the recorded reason: the parameter list could not be read.
    }

    public static bool IsSharedPostEvent(MethodBase method)
    {
        if (method is not MethodInfo info || !method.IsStatic || info.ReturnType != typeof(void)
            || method.Name != "PostEvent" || method.DeclaringType?.FullName != "GameEvent.GameEventManager") return false;
        var args = method.GetParameters();
        return args.Length == 5 && args[0].ParameterType.FullName == "GameEvent.eGameEvent"
            && args[1].ParameterType.FullName == "Player.PlayerAgent" && args[2].ParameterType == typeof(float)
            && args[3].ParameterType == typeof(string)
            && args[4].ParameterType.IsGenericType
            && args[4].ParameterType.GetGenericTypeDefinition().FullName == "Il2CppSystem.Collections.Generic.Dictionary`2"
            && args[4].ParameterType.GetGenericArguments().SequenceEqual(new[] { typeof(string), typeof(string) });
    }
    /// <summary>The Runtime writer of a shared entry several packages read, or null. Such an entry has one
    /// ForgeCore patch that packages subscribe to through Runtime (GamePostEvents, GamePlayerMembership, GameEnemyDamage); a package's own
    /// declaration of it is refused, so no package can occupy it first. Packages with their own hook coordinators
    /// (Development's tracer) ask this before installing anything and subscribe through the Runtime channel instead.</summary>
    public static string? RuntimeSharedWriter(MethodBase method)
    {
        if (IsSharedPostEvent(method)) return "ForgeEngine.PostEvent";
        if (method is not MethodInfo info || method.IsStatic || info.ReturnType != typeof(void)) return null;
        var owner = method.DeclaringType?.FullName;
        var args = method.GetParameters();
        bool spawn = args.Length == 2 && args[0].ParameterType.FullName == "Player.pPlayerSpawnData";
        if (owner == "Player.PlayerReplicationManager" && (method.Name == "OnSpawn" && spawn || method.Name == "OnDeSpawn" && args.Length == 1)
            || owner == "Player.PlayerManager" && method.Name == "OnPlayerSpawned" && spawn)
            return "ForgeEngine.PlayerMembership";
        return owner == "Dam_EnemyDamageBase" && args.Length == 1
            && (method.Name == "ReceiveBulletDamage" && args[0].ParameterType.FullName == "pBulletDamageData"
                || method.Name == "ReceiveMeleeDamage" && args[0].ParameterType.FullName == "pFullDamageData")
            ? "ForgeEngine.EnemyDamage" : null;
    }
}
