using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace ForgeWeaponEnergyLab.Api;

/// <summary>Reusable firing behaviours; Off represents an unequipped weapon.</summary>
public enum EnergyMode
{
    /// <summary>No energy behaviour.</summary>
    Off,
    /// <summary>A sustained hitscan beam.</summary>
    Beam,
    /// <summary>A hitscan discharge that chains between visible enemies.</summary>
    ArcChain,
    /// <summary>A charged orb that explodes on contact.</summary>
    PlasmaBlast,
    /// <summary>A charged orb that passes through actors and emits pulses.</summary>
    PlasmaArc,
    /// <summary>A hitscan projectile with an explosion at its impact.</summary>
    Blast,
    /// <summary>A bouncing disc with one contact per actor between bounces.</summary>
    Disc,
    /// <summary>A sustained cone of fire.</summary>
    Flame,
    /// <summary>A projectile that creates a damage and attraction field.</summary>
    BlackHole
}

/// <summary>The native inventory slot used by a registered firearm.</summary>
public enum WeaponSlot
{
    /// <summary>The main weapon slot.</summary>
    Main,
    /// <summary>The special weapon slot.</summary>
    Special
}

/// <summary>An immutable model reference. Bundles contain presentation-format (5), presentation-profile,
/// the named prefab, Receiver/Rail/Front/Stock/Magazine/Sight parts and RightHand/LeftHand/Muzzle/SightLook sockets.
/// A bundle loads, after the bundles Unity's manifest beside it says it depends on, when the first gun using it is assembled
/// and unloads once no gun object uses it.</summary>
/// <param name="Key">Prefab key in the bundle.</param>
/// <param name="BundlePath">Absolute path to the bundle file. A data file's <c>Model.Bundle</c> is relative to its folder and is made absolute when read.</param>
public sealed record WeaponModel(string Key, string BundlePath);

/// <summary>An immutable registration. The eight shipped guns, data files in <c>ForgeEnergyLab</c> folders and dependent plugins all register through <see cref="EnergyLab.Register"/>.</summary>
/// <param name="Id">Stable lowercase namespaced identity, for example author.mod.weapon; never change it for saved loadouts.</param>
/// <param name="Name">Player-visible weapon name.</param>
/// <param name="Slot">Native inventory slot.</param>
/// <param name="Mode">Reusable firing behaviour, matched by the tuning record type.</param>
/// <param name="Tuning">Complete gameplay values for this weapon.</param>
/// <param name="Model">The model bundle and prefab key.</param>
/// <param name="Description">Player-visible loadout description, or null for the default text.</param>
/// <param name="Look">Sounds and effects this weapon plays instead of its behaviour's own, or null for the defaults.</param>
public sealed record WeaponDefinition(string Id, string Name, WeaponSlot Slot, EnergyMode Mode,
    EnergyTuning Tuning, WeaponModel Model, string? Description = null, WeaponLook? Look = null);

/// <summary>Replacements for what a weapon plays and draws. Keys name what is replaced, values what replaces it; every key not listed keeps the behaviour's own.</summary>
/// <param name="Sounds">Cue name of the Energy Lab sounds (such as <c>beam-start</c>) to the sound that plays instead: a cue of the weapon's own <c>audio</c> folder, or <c>identity/cue</c> for a cue of any loaded ForgeAudio library.</param>
/// <param name="Fx">Effect slot name (such as <c>energy.beam.muzzle</c>) to the ForgeVisual effect drawn instead, as <c>Pack:item</c>.</param>
public sealed record WeaponLook(IReadOnlyDictionary<string, string> Sounds, IReadOnlyDictionary<string, string> Fx);

/// <summary>Immutable gameplay values. Distances are metres, times are seconds and ammunition costs are whole rounds.</summary>
public abstract record EnergyTuning
{
    /// <summary>Whole rounds in the native magazine.</summary>
    public int Magazine { get; init; } = 40;
    /// <summary>Native reserve units consumed by one magazine round.</summary>
    public float AmmoPerRound { get; init; } = 1.676f;
    /// <summary>Minimum interval between discharges.</summary>
    public double Interval { get; init; } = 0.1;
    /// <summary>Direct, explosion or field-pulse damage; charged orbs use this as minimum-charge damage.</summary>
    public float Damage { get; init; } = 4.35f;
    /// <summary>Whole rounds per discharge. Native fire requires exactly one, at every charge power.</summary>
    public int AmmoCost { get; init; } = 1;
    /// <summary>Native stagger multiplier for direct and selected hits.</summary>
    public float Stagger { get; init; } = 0.15f;
    /// <summary>Native precision multiplier for direct and selected hits.</summary>
    public float Precision { get; init; } = 1f;
    /// <summary>Native magazine reload duration.</summary>
    public float ReloadTime { get; init; } = 2.3f;
    /// <summary>Native equip transition duration.</summary>
    public float EquipTime { get; init; } = 0.6f;
    /// <summary>Native aim transition duration.</summary>
    public float AimTime { get; init; } = 0.25f;
}

/// <summary>Beam gameplay defaults. The beam has no range limit.</summary>
public sealed record BeamTuning : EnergyTuning;

/// <summary>Chain discharge gameplay values.</summary>
public sealed record ArcChainTuning : EnergyTuning
{
    /// <summary>Creates the built-in chain discharge defaults.</summary>
    public ArcChainTuning() { Magazine = 7; AmmoPerRound = 10.13f; Interval = 0.7; Damage = 15.9f; Stagger = 1; }
    /// <summary>Maximum distance of the first hit; beyond it the arc ends in the air.</summary>
    public float Range { get; init; } = 16f;
    /// <summary>Total recipients, including the first hit; the packet budget permits one to four.</summary>
    public int Targets { get; init; } = 4;
    /// <summary>Maximum distance of each hop after the first hit.</summary>
    public float JumpRange { get; init; } = 6f;
    /// <summary>Damage retained by each successive hop.</summary>
    public float DamageRetention { get; init; } = 0.8f;
    /// <summary>Duration of one arc's hit feedback.</summary>
    public float ArcLifetime { get; init; } = 0.22f;
    /// <summary>Presentation delay between consecutive hops; damage resolves immediately.</summary>
    public float HopDelay { get; init; } = 0.025f;
}

/// <summary>Charged orb values shared by blast and arc behaviours. Size, speed, travel and damage interpolate with power.</summary>
public abstract record PlasmaTuning : EnergyTuning
{
    /// <summary>Minimum hold duration that allows release to fire.</summary>
    public double MinimumCharge { get; init; } = 0.3;
    /// <summary>Hold duration that reaches full power.</summary>
    public double FullCharge { get; init; } = 1.1;
    /// <summary>Power used by native bot shots, which do not send charge input.</summary>
    public float BotChargePower { get; init; } = 0.45f;
    /// <summary>Minimum-charge projectile speed in metres per second.</summary>
    public float Speed { get; init; } = 10f;
    /// <summary>Full-charge projectile speed in metres per second.</summary>
    public float FullSpeed { get; init; } = 14f;
    /// <summary>Minimum-charge collision radius.</summary>
    public float BodyRadius { get; init; } = 0.12f;
    /// <summary>Full-charge collision radius.</summary>
    public float FullBodyRadius { get; init; } = 0.3f;
    /// <summary>Minimum-charge flight distance.</summary>
    public float TravelDistance { get; init; } = 18f;
    /// <summary>Full-charge flight distance.</summary>
    public float FullTravelDistance { get; init; } = 42f;
    /// <summary>Full-charge damage.</summary>
    public float FullDamage { get; init; } = 62.7f;
    /// <summary>Simultaneous flights from this definition, within the shooter's four shared projectile slots.</summary>
    public int ActiveProjectiles { get; init; } = 4;
}

/// <summary>Charged explosive orb gameplay values.</summary>
public sealed record PlasmaBlastTuning : PlasmaTuning
{
    /// <summary>Creates the built-in charged explosive orb defaults.</summary>
    public PlasmaBlastTuning() { Magazine = 4; AmmoPerRound = 16; Interval = 0.85; Damage = 12; Stagger = 1; ReloadTime = 3.3f; AimTime = 0.35f; }
    /// <summary>Minimum-charge explosion radius.</summary>
    public float BlastRadius { get; init; } = 1.5f;
    /// <summary>Full-charge explosion radius.</summary>
    public float FullBlastRadius { get; init; } = 2.5f;
    /// <summary>Time an explosion occupies its projectile slot.</summary>
    public float ExplosionLifetime { get; init; } = 0.8f;
    /// <summary>Maximum native explosion noise radius.</summary>
    public float NoiseRadius { get; init; } = 20f;
    /// <summary>Native explosion receiver force.</summary>
    public float ExplosionForce { get; init; } = 12f;
}

/// <summary>Charged actor-piercing orb gameplay values.</summary>
public sealed record PlasmaArcTuning : PlasmaTuning
{
    /// <summary>Creates the built-in charged arc orb defaults.</summary>
    public PlasmaArcTuning() { Magazine = 5; AmmoPerRound = 16; Interval = 0.85; Damage = 12; Stagger = 1; ReloadTime = 3.3f; AimTime = 0.35f; }
    /// <summary>Minimum-charge pulse radius.</summary>
    public float ArcRadius { get; init; } = 2f;
    /// <summary>Full-charge pulse radius.</summary>
    public float FullArcRadius { get; init; } = 3.5f;
    /// <summary>Minimum interval between flight pulses.</summary>
    public double PulseInterval { get; init; } = 0.4;
    /// <summary>Fraction of direct orb damage applied by each pulse.</summary>
    public float PulseDamageFraction { get; init; } = 0.08f;
    /// <summary>Native stagger multiplier for a flight pulse.</summary>
    public float PulseStagger { get; init; } = 0.5f;
    /// <summary>Recipients of each pulse; the effect budget permits one to three.</summary>
    public int PulseTargets { get; init; } = 3;
    /// <summary>Fade duration after flight ends, during which its projectile slot remains occupied.</summary>
    public float FadeLifetime { get; init; } = 0.18f;
}

/// <summary>Explosive shell gameplay values. The shell flies along the aim at <see cref="Speed"/> and explodes at the first thing it touches; it has no range limit.</summary>
public sealed record BlastTuning : EnergyTuning
{
    /// <summary>Creates the built-in explosive shell defaults.</summary>
    public BlastTuning() { Magazine = 6; AmmoPerRound = 6.93f; Interval = 0.45; Damage = 30.13f; Stagger = 1; ReloadTime = 3.3f; AimTime = 0.35f; }
    /// <summary>Shell speed in metres per second; the shell explodes where it first touches terrain, an enemy or a player.</summary>
    public float Speed { get; init; } = 40f;
    /// <summary>Explosion radius with native linear falloff.</summary>
    public float BlastRadius { get; init; } = 2.2f;
    /// <summary>Time an explosion occupies its projectile slot.</summary>
    public float ExplosionLifetime { get; init; } = 0.4f;
    /// <summary>Maximum native explosion noise radius.</summary>
    public float NoiseRadius { get; init; } = 20f;
    /// <summary>Native explosion receiver force.</summary>
    public float ExplosionForce { get; init; } = 12f;
}

/// <summary>Ricochet disc gameplay values.</summary>
public sealed record DiscTuning : EnergyTuning
{
    /// <summary>Creates the built-in ricochet disc defaults.</summary>
    public DiscTuning() { Magazine = 16; AmmoPerRound = 5.464f; Interval = 0.55; Damage = 23.95f; Stagger = 0.7f; ReloadTime = 3.3f; AimTime = 0.35f; }
    /// <summary>Flight speed in metres per second.</summary>
    public float Speed { get; init; } = 45f;
    /// <summary>Collision radius.</summary>
    public float BodyRadius { get; init; } = 0.3f;
    /// <summary>Wall bounces allowed before removal.</summary>
    public int Bounces { get; init; } = 4;
    /// <summary>Maximum flight duration.</summary>
    public float Lifetime { get; init; } = 1.6f;
    /// <summary>Simultaneous discs from this definition, within three shared disc slots.</summary>
    public int ActiveProjectiles { get; init; } = 3;
}

/// <summary>Flame cone gameplay values.</summary>
public sealed record FlameTuning : EnergyTuning
{
    /// <summary>Creates the built-in flame cone defaults.</summary>
    public FlameTuning() { Magazine = 50; AmmoPerRound = 1.8f; Interval = 0.1; Damage = 2.4f; Stagger = 0.12f; }
    /// <summary>Flame length.</summary>
    public float Range { get; init; } = 8f;
    /// <summary>Cone radius at the muzzle.</summary>
    public float StartRadius { get; init; } = 0.15f;
    /// <summary>Additional cone radius per metre along the ray.</summary>
    public float RadiusPerMetre { get; init; } = 0.16f;
}

/// <summary>Gravity projectile, field damage and attraction values. A shooter has one shared gravity slot.</summary>
public sealed record BlackHoleTuning : EnergyTuning
{
    /// <summary>Creates the built-in gravity core defaults.</summary>
    public BlackHoleTuning() { Magazine = 2; AmmoPerRound = 28; Interval = 2; Damage = 9f; Stagger = 0.1f; ReloadTime = 3.3f; AimTime = 0.35f; }
    /// <summary>Launch speed in metres per second.</summary>
    public float Speed { get; init; } = 20f;
    /// <summary>Projectile collision radius.</summary>
    public float BodyRadius { get; init; } = 0.18f;
    /// <summary>Time before an airborne projectile becomes a field.</summary>
    public float FlightLifetime { get; init; } = 3f;
    /// <summary>Downward projectile acceleration in metres per second squared.</summary>
    public float Gravity { get; init; } = 3f;
    /// <summary>Outer damage and attraction radius.</summary>
    public float Radius { get; init; } = 5f;
    /// <summary>Inner radius with full damage and no further attraction.</summary>
    public float CoreRadius { get; init; } = 0.8f;
    /// <summary>Field duration including collapse.</summary>
    public float Lifetime { get; init; } = 3.5f;
    /// <summary>Final duration without damage or attraction.</summary>
    public float CollapseDuration { get; init; } = 0.35f;
    /// <summary>Time for the visible field to reach full size.</summary>
    public float GrowthDuration { get; init; } = 0.2f;
    /// <summary>Minimum interval between damage pulses.</summary>
    public double PulseInterval { get; init; } = 0.4;
    /// <summary>Damage fraction at the outer edge; falloff starts at CoreRadius.</summary>
    public float EdgeDamageFraction { get; init; } = 0.25f;
    /// <summary>Maximum attraction speed in metres per second.</summary>
    public float PullSpeed { get; init; } = 7f;
    /// <summary>Maximum interval between attraction target queries.</summary>
    public float QueryInterval { get; init; } = 0.1f;
}

/// <summary>Registration and immutable tuning access. Call from a dependent plugin's Load before game data installation.</summary>
public static class EnergyLab
{
    /// <summary>BepInEx dependency GUID.</summary>
    public const string PluginId = "NAinfini.ForgeWeaponEnergyLab";
    /// <summary>Plugin package version.</summary>
    public const string Version = "1.0.1"; // package-version: NAinfini-ForgeWeapon_EnergyLab
    private static readonly object Gate = new();
    private static readonly Dictionary<string, WeaponDefinition> Definitions = new(StringComparer.Ordinal);
    private static readonly List<string> Order = new();
    private static bool _initialized, _closed;
    private static string _configDirectory = "";

    /// <summary>Registers one definition. Never throws: an invalid value, a duplicate identity, a native-ID collision (the message
    /// names both IDs) or a call after gear installation started is refused with one line saying which field and why.</summary>
    /// <param name="definition">Complete immutable weapon definition.</param>
    /// <param name="error">Why it was refused; null when registered.</param>
    /// <returns>True when the weapon is registered.</returns>
    public static bool Register(WeaponDefinition definition, [NotNullWhen(false)] out string? error)
    {
        lock (Gate)
        {
            try
            {
                RequireOpen();
                EnergyConfiguration.ValidateDefinition(definition);
                if (Definitions.ContainsKey(definition.Id)) throw new ArgumentException("Duplicate weapon id: " + definition.Id);
                var ids = EnergyGears.Ids(definition.Id);
                foreach (var other in Definitions.Values)
                {
                    var previous = EnergyGears.Ids(other.Id);
                    if (ids.Gear == previous.Gear || ids.Category == previous.Category || ids.Archetype == previous.Archetype)
                        throw new ArgumentException($"Native ID collision between {definition.Id} and {other.Id}.");
                }
                Definitions.Add(definition.Id, definition);
                Order.Add(definition.Id);
                error = null;
                return true;
            }
            catch (Exception problem) when (problem is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                error = problem.Message;
                return false;
            }
        }
    }

    /// <summary>Returns the immutable definition snapshot for a registered stable ID.</summary>
    /// <param name="id">Registered namespaced identity.</param>
    /// <returns>The current definition.</returns>
    public static WeaponDefinition GetDefinition(string id)
    {
        lock (Gate) { RequireInitialized(); return Definitions.TryGetValue(id, out var definition) ? definition : throw new KeyNotFoundException("Unknown weapon id: " + id); }
    }

    /// <summary>Returns a registered weapon's immutable tuning snapshot.</summary>
    /// <param name="id">Registered namespaced identity.</param>
    /// <returns>The behaviour-specific tuning record.</returns>
    public static EnergyTuning GetTuning(string id) => GetDefinition(id).Tuning;

    /// <summary>Replaces tuning atomically before installation. Existing snapshots remain unchanged.
    /// Throws InvalidOperationException once gear installation has started, KeyNotFoundException for an unregistered id,
    /// and InvalidDataException when the replacement tuning is invalid.</summary>
    /// <param name="id">Registered namespaced identity.</param>
    /// <param name="tuning">Replacement of the same behaviour-specific record type.</param>
    public static void SetTuning(string id, EnergyTuning tuning)
    {
        lock (Gate)
        {
            RequireOpen();
            var definition = GetDefinition(id) with { Tuning = tuning };
            EnergyConfiguration.ValidateDefinition(definition);
            Definitions[id] = definition;
        }
    }

    /// <summary>The rundown's <c>Settings.forge</c> rules, read before registration closes; <see cref="EnergySettings.None"/> without one.</summary>
    internal static EnergySettings Settings { get; private set; } = EnergySettings.None;

    /// <summary>Takes the rundown's rules; the Tuning in them is laid over every gun when registration closes.</summary>
    internal static void UseSettings(EnergySettings settings) { lock (Gate) { RequireOpen(); Settings = settings; } }

    internal static void Initialize(string configDirectory)
    {
        lock (Gate)
        {
            if (_initialized) return;
            _configDirectory = configDirectory;
            _initialized = true;
        }
    }

    /// <summary>Closes registration and applies the tuning files to every registered weapon, whoever registered it. The result is in registration order, which is the order the guns are listed in. Problems are added to <paramref name="errors"/>.</summary>
    internal static EnergyGear[] Freeze(List<string> errors)
    {
        lock (Gate)
        {
            RequireInitialized();
            _closed = true;
            // Settings.forge was read before any plugin registered; its Allowed Ids are checked now that every gun is known.
            foreach (var id in Settings.UnknownAllowed(Definitions.ContainsKey))
                errors.Add(EnergySettings.FileName + ": Allowed: '" + id + "' is not a loaded gun (is its package installed?).");
            var tuned = EnergyConfiguration.Load(_configDirectory, Order.Select(id => Definitions[id]).ToArray(), errors, Settings.Tuning);
            foreach (var definition in tuned) Definitions[definition.Id] = definition;
            return tuned.Select(d => new EnergyGear(d)).ToArray();
        }
    }

    private static void RequireInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("Energy Lab is unavailable: its plugin has not loaded successfully.");
    }
    private static void RequireOpen()
    {
        RequireInitialized();
        if (_closed) throw new InvalidOperationException("Energy Lab registration is closed: gear installation has started. Register and override tuning in your plugin's Load.");
    }
}
