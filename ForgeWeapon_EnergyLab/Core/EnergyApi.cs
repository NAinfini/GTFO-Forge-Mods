using System;
using System.Collections.Generic;
using System.Linq;
using ForgeWeaponEnergyLabExperimental;

namespace ForgeEnergyLab.Api;

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

/// <summary>An immutable model reference. Bundles contain presentation-format (4), presentation-profile,
/// the named prefab, Receiver/Rail/Front/Stock/Magazine/Sight parts and RightHand/LeftHand/Muzzle/SightLook sockets.
/// A bundle loads when the first gun using it is assembled and unloads once no gun object uses it.</summary>
/// <param name="Key">Prefab key in the bundle; built-in keys are beam, arc, flame, plasma-blast, plasma-arc, blast, disc and hole.</param>
/// <param name="BundlePath">Absolute path to the provider's bundle, or null for the built-in model with this key.</param>
/// <param name="IconPath">Absolute path to a PNG inventory icon (at most 1 MiB). Required with BundlePath;
/// null with a built-in model uses that model's icon.</param>
public sealed record WeaponModel(string Key, string? BundlePath = null, string? IconPath = null);

/// <summary>An immutable registration shared by built-in content and dependent plugins.</summary>
/// <param name="Id">Stable lowercase namespaced identity, for example author.mod.weapon; never change it for saved loadouts.</param>
/// <param name="Name">Player-visible weapon name.</param>
/// <param name="Slot">Native inventory slot.</param>
/// <param name="Mode">Reusable firing behaviour, matched by the tuning record type.</param>
/// <param name="Tuning">Complete gameplay values for this weapon.</param>
/// <param name="Model">A built-in or provider-owned model reference.</param>
/// <param name="Description">Player-visible loadout description, or null for the default text.</param>
public sealed record WeaponDefinition(string Id, string Name, WeaponSlot Slot, EnergyMode Mode,
    EnergyTuning Tuning, WeaponModel Model, string? Description = null);

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

/// <summary>Instant explosion gameplay values. The shot has no range limit.</summary>
public sealed record BlastTuning : EnergyTuning
{
    /// <summary>Creates the built-in instant explosion defaults.</summary>
    public BlastTuning() { Magazine = 6; AmmoPerRound = 6.93f; Interval = 0.45; Damage = 30.13f; Stagger = 1; ReloadTime = 3.3f; AimTime = 0.35f; }
    /// <summary>Explosion radius with native linear falloff.</summary>
    public float BlastRadius { get; init; } = 2.2f;
    /// <summary>Time an explosion occupies its projectile slot.</summary>
    public float ExplosionLifetime { get; init; } = 0.4f;
    /// <summary>Simultaneous explosions from this definition, within four shared projectile slots.</summary>
    public int ActiveProjectiles { get; init; } = 4;
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
    public FlameTuning() { Magazine = 50; AmmoPerRound = 1.614f; Interval = 0.1; Damage = 2.695f; Stagger = 0.12f; }
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
    public BlackHoleTuning() { Magazine = 2; AmmoPerRound = 28; Interval = 2; Damage = 6.75f; Stagger = 0.1f; ReloadTime = 3.3f; AimTime = 0.35f; }
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
    public const string PluginId = "NAinfini.ForgeWeaponEnergyLabExperimental";
    /// <summary>Plugin and protocol version.</summary>
    public const string Version = "0.0.4";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, WeaponDefinition> Definitions = new(StringComparer.Ordinal);
    private static bool _initialized, _closed;

    /// <summary>Registers one definition. Invalid values and duplicate identities or native IDs are rejected.
    /// Throws InvalidOperationException once gear installation has started or if Energy Lab is disabled.</summary>
    /// <param name="definition">Complete immutable weapon definition.</param>
    public static void Register(WeaponDefinition definition)
    {
        lock (Gate)
        {
            RequireOpen();
            EnergyConfiguration.ValidateDefinition(definition);
            if (Definitions.ContainsKey(definition.Id)) throw new ArgumentException("Duplicate weapon id: " + definition.Id, nameof(definition));
            var ids = EnergyGears.Ids(definition.Id);
            foreach (var other in Definitions.Values)
            {
                var previous = EnergyGears.Ids(other.Id);
                if (ids.Gear == previous.Gear || ids.Category == previous.Category || ids.Archetype == previous.Archetype)
                    throw new ArgumentException($"Native ID collision between {definition.Id} and {other.Id}.", nameof(definition));
            }
            Definitions.Add(definition.Id, definition);
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
    /// Throws InvalidOperationException once gear installation has started.</summary>
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

    /// <summary>Returns a reusable built-in model reference by key; this does not load or instantiate Unity assets.</summary>
    /// <param name="key">beam, arc, flame, plasma-blast, plasma-arc, blast, disc or hole.</param>
    /// <returns>An immutable reference that can be assigned to any behaviour.</returns>
    public static WeaponModel GetModel(string key) => EnergyGears.BuiltInModel(key);

    internal static void Initialize(string configDirectory)
    {
        lock (Gate)
        {
            if (_initialized) return;
            var definitions = EnergyConfiguration.Load(configDirectory, EnergyGears.Defaults());
            _initialized = true;
            try { foreach (var definition in definitions) Register(definition); }
            catch { Definitions.Clear(); _initialized = false; throw; }
        }
    }

    internal static EnergyGear[] Freeze()
    {
        lock (Gate)
        {
            RequireInitialized();
            _closed = true;
            return Definitions.Values.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => new EnergyGear(d)).ToArray();
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
