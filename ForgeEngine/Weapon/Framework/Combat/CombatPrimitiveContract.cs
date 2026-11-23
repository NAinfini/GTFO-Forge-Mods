using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The one combat row of this provider that is not a weapon state: one shot's own resolution
/// (`forge.trigger.combat.shot_resolved`).
///
/// <b>The trigger.</b> A plan that wants to react to "this bullet hit nothing" cannot compose it: absence of a
/// hit is not an event, and <c>forge.trigger.combat.hit_candidate</c> only ever fires when a ray did reach
/// something. The row therefore publishes all three outcomes of one native shot — it reached a damageable
/// target, it reached world geometry (with the point and the surface normal it struck), or it resolved with no
/// impact at all — and <c>position</c> and <c>normal</c> are absent exactly when nothing was struck. The unit is
/// one `Fire`: a shotgun's several pellets are one shot with a <c>hit_count</c>, never several shots, because
/// that is what the native body produced. A miss is published from the firing body's own end, so it is one
/// event per shot and can never be doubled by a later hit. `outcome` is a member of the shared `shot_outcome` set.
/// </summary>
internal static class CombatPrimitiveContract
{
    /// <summary>One shot reached a damageable target, reached world geometry, or reached nothing.</summary>
    public const string ShotResolvedCapability = Blocks.TriggerCombatShotResolved.Id;
    /// <summary>One of the game's own projectiles, launched from a point and a direction.</summary>
    public const string ProjectileLaunchCapability = Blocks.ActionCombatProjectileLaunch.Id;

    public const string ShotResolvedBinding = ModuleDefinition.ProviderId + ".binding.shot_resolved";
    public const string ProjectileLaunchBinding = ModuleDefinition.ProviderId + ".binding.projectile_launch";

    /// <summary>The handler names. The trigger's is a fact body the native half publishes through; the action's is
    /// the body the native half executes.</summary>
    public const string ShotResolvedHandler = "gtfo.weapon.shot_resolved";
    public const string ProjectileLaunchHandler = "gtfo.weapon.projectile_launch";

    /// <summary>The permission all three rows are read under: the shot's own combat reading, the same one the
    /// shot facts already carry.</summary>
    public const string ReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The permission the launch row writes under: it creates a world object, which is the same kind of
    /// write a deployed device is.</summary>
    public const string LaunchPermission = ModuleDefinition.DeployableReadPermission;

    /// <summary>The three outcomes a shot resolves to: their positions in the `shot_outcome` set (hit, world, miss),
    /// which is the value the enum port carries.</summary>
    public const int Hits = 0;
    public const int World = 1;
    public const int Misses = 2;

    /// <summary>The launch row's reviewed profile members, read from the block table's row; the one declaration of
    /// the vocabulary. The native half only maps each member to its game enum member.</summary>
    public static readonly IReadOnlyList<string> ProjectileProfiles = Array.AsReadOnly(
        Blocks.Capability(Blocks.ActionCombatProjectileLaunch.Index)
        .GetProperty("graph").GetProperty("parameters").EnumerateArray()
        .Where(parameter => parameter.GetProperty("id").GetString() == "profile")
        .SelectMany(parameter => parameter.GetProperty("values").EnumerateArray())
        .Select(value => value.GetString()!).ToArray());

    /// <summary>The ports the launch handler really reads, resolved at registration against the row's own ports.
    /// The trigger publishes facts instead of running a body, so it declares no shape.</summary>
    public static readonly HandlerShape ProjectileLaunchShape = new HandlerShape()
        .Inputs("origin", "direction", "source", "homing_target").Outputs("result").Parameters("profile", "count", "spread");
    /// <summary>The optional agent the projectile homes on: the `target` field of the game's own fire packet.</summary>
    public static readonly HandlerSlot ProjectileLaunchTargetSlot = ProjectileLaunchShape.InputSlot("homing_target");
    public static readonly HandlerSlot ProjectileLaunchOriginSlot = ProjectileLaunchShape.InputSlot("origin");
    public static readonly HandlerSlot ProjectileLaunchDirectionSlot = ProjectileLaunchShape.InputSlot("direction");
    public static readonly HandlerSlot ProjectileLaunchSourceSlot = ProjectileLaunchShape.InputSlot("source");
    public static readonly HandlerSlot ProjectileLaunchProfileSlot = ProjectileLaunchShape.ParameterSlot("profile");
    public static readonly HandlerSlot ProjectileLaunchCountSlot = ProjectileLaunchShape.ParameterSlot("count");
    public static readonly HandlerSlot ProjectileLaunchSpreadSlot = ProjectileLaunchShape.ParameterSlot("spread");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings(CommandHandler? handler) => handler != null ? new ProviderBinding[]
    {
        new ProviderBinding(ShotResolvedBinding, ShotResolvedCapability, ShotResolvedHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding(ProjectileLaunchBinding, ProjectileLaunchCapability, ProjectileLaunchHandler, "execute", new[] { LaunchPermission }, ProjectileLaunchShape)
    } : new ProviderBinding[]
    {
        new ProviderBinding(ShotResolvedBinding, ShotResolvedCapability, ShotResolvedHandler, "observe", new[] { ReadPermission })
    };
}
