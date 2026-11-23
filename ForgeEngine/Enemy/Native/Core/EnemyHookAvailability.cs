using System;
using System.Collections.Generic;

namespace ForgeEnemy.Native;

/// <summary>The functions a player can notice, each standing on its own native callback classes. A class Runtime
/// refuses withdraws exactly the function that owns it; every other function keeps working.</summary>
internal enum EnemyFeature
{
    /// <summary>Which enemy lives are in the level. Every other function reads a life through this one.</summary>
    Tracking,
    SpawnFact,
    Damage,
    /// <summary>The kind of damage a hit carries (bullet, melee, explosion, push, fire).</summary>
    DamageKind,
    Death,
    LimbBreak,
    LimbProfile,
    ModelLimbs,
    Behavior,
    ScoutDetection,
    ScoutAbilities,
    ScannerAbilities,
    Tag,
    Glue,
    AbilityUsed,
    MeleeAttack,
    Scream,
    SleeperDetection,
    Projectile,
    Fog,
    Birth,
    PlayerRelease,
    Attack,
    Noise,
    Alarm,
    Grab,
    NativeState,
    CapCost,
    Wake,
    WakeHearing,
    Appearance,
    Animation,
    Look,
    Waves,
    ChildSpawning,
    /// <summary>The native state-change and target entries that the behaviour, native-state and wake functions share: one callback class per target.</summary>
    StateEntries,
    /// <summary>The projectile-hit-world and projectile-expired triggers and the shooter attribution both stand on.</summary>
    ProjectileWorld
}

/// <summary>The outcome of one declaration pass: which functions stand, which are withdrawn and why. It is plain data
/// so every module and fact publisher can ask it without knowing the native callback classes; the table that builds it
/// is <see cref="EnemyHookFeatures"/>.</summary>
internal sealed class EnemyHookAvailability
{
    internal static readonly EnemyHookAvailability Complete = new(new Dictionary<EnemyFeature, string>(), Array.Empty<string>());

    private readonly bool[] _withdrawn = new bool[Enum.GetValues<EnemyFeature>().Length];
    private readonly HashSet<string> _bindings;

    internal EnemyHookAvailability(IReadOnlyDictionary<EnemyFeature, string> reasons, IReadOnlyCollection<string> withdrawnBindings)
    {
        Withdrawn = reasons;
        foreach (var feature in reasons.Keys) _withdrawn[(int)feature] = true;
        _bindings = new HashSet<string>(withdrawnBindings, StringComparer.Ordinal);
    }

    /// <summary>Whether the function stands; a hook body of a withdrawn function returns before it touches the game.</summary>
    internal bool Offers(EnemyFeature feature) => !_withdrawn[(int)feature];
    internal bool IsComplete => Withdrawn.Count == 0;
    /// <summary>Every withdrawn function with the reason.</summary>
    internal IReadOnlyDictionary<EnemyFeature, string> Withdrawn { get; }
    internal bool WithdrawsBinding(string bindingId) => _bindings.Contains(bindingId);
    internal IReadOnlyCollection<string> WithdrawnBindings => _bindings;
}
