using System;
using System.Collections.Generic;

namespace ForgePlayer.Native;

/// <summary>The catalog's `agent_modifier` members and the native `AgentModifier` each one names, spelled out
/// member by member. The native enum's values are not contiguous — `PistolDamage` is 50, `GlueStrength` 100,
/// `HackingProficiency` 150, `MeleeDamage` 200, `MovementSpeed` 250 — so nothing here may be derived from an
/// index or from the order of the catalog's set. The names are the catalog's own spelling (every native member
/// lower-cased, an underscore or a case boundary becoming a hyphen), which is what a plan's `attribute` port
/// carries after the kernel resolves the enum index.
///
/// The table carries the whole native enum, `None` included: a member the shared set drops (the ruled shape drops
/// `none`, a no-op rather than an applicable attribute) must still be recognized here so the handler can refuse
/// it by name instead of reporting an unknown attribute, and a member the shared set adds later fails the
/// coverage test until it is mapped here.</summary>
internal static class AgentModifierValues
{
    private static readonly (string Name, AgentModifier Modifier)[] Table =
    {
        ("none", AgentModifier.None),
        ("regeneration-cap", AgentModifier.RegenerationCap),
        ("regeneration-speed", AgentModifier.RegenerationSpeed),
        ("heal-support", AgentModifier.HealSupport),
        ("revive-speed-support", AgentModifier.ReviveSpeedSupport),
        ("revive-start-health-support", AgentModifier.ReviveStartHealthSupport),
        ("melee-resistance", AgentModifier.MeleeResistance),
        ("projectile-resistance", AgentModifier.ProjectileResistance),
        ("infection-resistance", AgentModifier.InfectionResistance),
        ("damage-over-time", AgentModifier.DamageOverTime),
        ("nanoswarm-shield", AgentModifier.Nanoswarm_Shield),
        ("nanoswarm-weakness", AgentModifier.Nanoswarm_Weakness),
        ("explosion-resistance", AgentModifier.ExplosionResistance),
        ("pistol-damage", AgentModifier.PistolDamage),
        ("smg-damage", AgentModifier.SMGDamage),
        ("dmr-damage", AgentModifier.DMRDamage),
        ("assault-rifle-damage", AgentModifier.AssaultRifleDamage),
        ("carbine-damage", AgentModifier.CarbineDamage),
        ("auto-pistol-damage", AgentModifier.AutoPistolDamage),
        ("hel-damage", AgentModifier.HELDamage),
        ("shotgun-damage", AgentModifier.ShotgunDamage),
        ("revolver-damage", AgentModifier.RevolverDamage),
        ("sniper-damage", AgentModifier.SniperDamage),
        ("burst-cannon-damage", AgentModifier.BurstCannonDamage),
        ("machine-gun-damage", AgentModifier.MachineGunDamage),
        ("machine-pistol-damage", AgentModifier.MachinePistolDamage),
        ("rifle-damage", AgentModifier.RifleDamage),
        ("burst-rifle-damage", AgentModifier.BurstRifleDamage),
        ("double-tap-rifle", AgentModifier.DoubleTapRifle),
        ("bullpup-rifle-damage", AgentModifier.BullpupRifleDamage),
        ("combat-shotgun-damage", AgentModifier.CombatShotgunDamage),
        ("choke-mod-shotgun-damage", AgentModifier.ChokeModShotgunDamage),
        ("standard-weapon-damage", AgentModifier.StandardWeaponDamage),
        ("special-weapon-damage", AgentModifier.SpecialWeaponDamage),
        ("glue-strength", AgentModifier.GlueStrength),
        ("glue-efficiency", AgentModifier.GlueEfficiency),
        ("sentry-gun-speed", AgentModifier.SentryGunSpeed),
        ("sentry-gun-damage", AgentModifier.SentryGunDamage),
        ("sentry-gun-long-range-damage", AgentModifier.SentryGunLongRangeDamage),
        ("sentry-gun-short-range-damage", AgentModifier.SentryGunShortRangeDamage),
        ("trip-mine-damage", AgentModifier.TripMineDamage),
        ("scanner-recharge-speed", AgentModifier.ScannerRechargeSpeed),
        ("ammo-support", AgentModifier.AmmoSupport),
        ("hacking-proficiency", AgentModifier.HackingProficiency),
        ("computer-processing-speed", AgentModifier.ComputerProcessingSpeed),
        ("initial-ammo-standard", AgentModifier.InitialAmmoStandard),
        ("initial-ammo-special", AgentModifier.InitialAmmoSpecial),
        ("initial-ammo-tool", AgentModifier.InitialAmmoTool),
        ("fog-repeller-effect", AgentModifier.FogRepellerEffect),
        ("glowstick-effect", AgentModifier.GlowstickEffect),
        ("bioscan-speed", AgentModifier.BioscanSpeed),
        ("melee-damage", AgentModifier.MeleeDamage),
        ("movement-speed", AgentModifier.MovementSpeed),
        ("movement-acceleration", AgentModifier.MovementAcceleration)
    };

    private static readonly Dictionary<string, AgentModifier> ByName = Build();

    /// <summary>Every member of the native enum, in the enum's own value order, as the catalog spells each one.</summary>
    internal static IReadOnlyList<(string Name, AgentModifier Modifier)> Members { get; } = Array.AsReadOnly(Table);

    /// <summary>The native member a catalog name stands for. A name the table does not carry — an attribute from
    /// another domain, or one the native build has and this table has not caught up with — answers false, and the
    /// handler refuses it by name rather than guessing a member.</summary>
    internal static bool TryParse(string? name, out AgentModifier modifier)
    {
        modifier = AgentModifier.None;
        return name != null && ByName.TryGetValue(name, out modifier);
    }

    private static Dictionary<string, AgentModifier> Build()
    {
        var table = new Dictionary<string, AgentModifier>(StringComparer.Ordinal);
        foreach (var (name, modifier) in Table) table.Add(name, modifier);
        return table;
    }
}
