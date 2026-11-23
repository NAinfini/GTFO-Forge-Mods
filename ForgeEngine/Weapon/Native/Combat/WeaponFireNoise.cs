using System;
using System.Collections.Generic;
using System.Globalization;
using Agents;
using ForgeCore;
using ForgeEngine.Framework;
using HarmonyLib;
using Player;

namespace ForgeWeapon.Native;

/// <summary>The noise one gun's shot makes, projected out of the Forge weapon documents (block `forge.fireNoise`,
/// paths `radiusMax` and `radiusMin`, metres).
///
/// The game has no per-weapon noise member (evidence/fire-noise.json). Every one of the four Slot 151 `Fire` bodies
/// sets its owner's `Agent.Noise` to `Shoot`, and a sleeping enemy measures the shooter against its own
/// `EnemyDetectionDataBlock.weaponDetectionDistanceMin/Max`; `ShootSilenced` is read exactly like `Shoot`, and
/// `MeleeArchetypeDataBlock.NoiseLevel` belongs to the melee swing alone. A radius of the gun's own is therefore the
/// game's one noise entry, `NoiseManager.MakeNoise`, in the shape the sentry's own shot and the melee hit already
/// submit. The scoped setter adapts the current shot to Silent, and the owning machine submits its authored radii.
///
/// Only an entry written under `offlineId` is accepted: a gun is one assembled gear, and the firing weapon names its
/// gear by the offline record its `GearIDRange` carries. `radiusMax` is required, `radiusMin` defaults to 0, and an
/// inner radius above the outer one is refused rather than reordered.</summary>
internal static class WeaponFireNoiseData
{
    internal const string BlockId = "forge.fireNoise";
    internal const string RadiusMaxPath = "radiusMax";
    internal const string RadiusMinPath = "radiusMin";

    internal static WeaponFireNoiseSnapshot Project(WeaponFieldSet set, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(rejected);
        var radii = new Dictionary<string, FireNoiseRadii>(StringComparer.Ordinal);
        foreach (var entry in set.Entries)
        {
            if (!entry.TryBlock(BlockId, out var block)) continue;
            if (entry.Key != WeaponFieldKey.Offline)
            {
                rejected("weapon.fire-noise file=" + entry.Source + ": the `" + BlockId + "` block is written under "
                    + (entry.Key == WeaponFieldKey.Item ? "`itemId`" : "`archetypeId`")
                    + "; a gun's shot noise belongs to one gear, so the entry must be written under `offlineId`.");
                continue;
            }
            var key = entry.Id.ToString(CultureInfo.InvariantCulture);
            // The document loader has already checked that each number converts to a finite native Single.
            // Negative radii remain authored values; zero is only an authoring warning threshold.
            var max = block.Values.Number(RadiusMaxPath);
            if (max == null)
            {
                rejected("weapon.fire-noise file=" + entry.Source + " block=" + key + ": the block has no `" + RadiusMaxPath
                    + "`; an inner radius alone describes no noise.");
                continue;
            }
            var min = block.Values.Number(RadiusMinPath) ?? 0d;
            // Native ProcessNode normalizes by 1 / (radiusMax - radiusMin): equal ends give an infinite factor and
            // a NaN noise value, so they are refused. An inner radius above the outer one is a finite (negative)
            // factor and is admitted, as the website does.
            if (min == max.Value)
            {
                rejected("weapon.fire-noise file=" + entry.Source + " block=" + key + ": `" + RadiusMinPath + "` and `"
                    + RadiusMaxPath + "` are both " + min.ToString(CultureInfo.InvariantCulture)
                    + "; equal ends make the native normalization divide by zero.");
                continue;
            }
            radii[key] = new FireNoiseRadii((float)min, (float)max.Value);
        }
        return new WeaponFireNoiseSnapshot(radii);
    }
}

internal readonly struct FireNoiseRadii
{
    internal FireNoiseRadii(float min, float max) { Min = min; Max = max; }
    internal float Min { get; }
    internal float Max { get; }
}

internal sealed class WeaponFireNoiseSnapshot
{
    internal static readonly WeaponFireNoiseSnapshot Empty = new(new Dictionary<string, FireNoiseRadii>(StringComparer.Ordinal));
    private readonly IReadOnlyDictionary<string, FireNoiseRadii> _radii;
    internal WeaponFireNoiseSnapshot(IReadOnlyDictionary<string, FireNoiseRadii> radii) { _radii = radii; }
    internal int BlockCount => _radii.Count;
    internal bool TryGet(string blockId, out FireNoiseRadii radii) => _radii.TryGetValue(blockId, out radii);
}

/// <summary>One weapon's current noise parameters, or the previous Forge window carried to the end of the Fire call.
/// No native owner state is saved. A default shot is left alone.</summary>
internal struct FireNoiseShot
{
    internal PlayerAgent? Owner;
    internal FireNoiseRadii Radii;
    internal string? Block;
    internal WeaponFireNoise? Service;
    internal WeaponFireNoise? ScopeService;
}

/// <summary>Replaces one authored gun's shot noise, around the game's own `Fire` body.
///
/// Only the current Shoot argument on the exact owner is changed to Silent. The original setter handles its own
/// priority, timestamp and drama; no old flag or timestamp is ever written back. Nested Fire windows mask the
/// outer weapon, and the end of the call restores only Forge's own window even when Fire or another hook throws.
///
/// The replacement noise is submitted once per shot, by the machine that owns the shooter — its local player, or
/// the host for a bot — through `NoiseManager.MakeNoise`, which the game itself forwards from a client to the
/// master. The noise is the sentry shot's own shape: no noise maker, the shooter's position and course node, the
/// gear's two radii, a vertical scale of 1, `InstaDetect`, spreading into neighbouring areas and without the first
/// node ray. A gun no document names, and every gun of an install without the block, runs the body untouched: the
/// only per-shot cost for those is one count check.</summary>
internal sealed class WeaponFireNoise
{
    private const float VerticalScale = 1f;
    private readonly WeaponFireNoiseSnapshot _snapshot;
    private readonly WeaponLog _log;
    private readonly OnceReporter _once;
    private FireNoiseShot _current;
    /// <summary>Each gun's answer, read from its gear record on its first shot: the gun itself (held, so its native object
    /// and pointer stay its own), its block and its radii, or no block for a gun no document names. A gun's gear record is
    /// set when the game builds it.</summary>
    private readonly Dictionary<IntPtr, (ItemEquippable Weapon, string? Block, FireNoiseRadii Radii)> _guns = new();

    internal WeaponFireNoise(WeaponFireNoiseSnapshot snapshot, Action<string> report, WeaponLog log)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal int BlockCount => _snapshot.BlockCount;

    /// <summary>Open this weapon's window and return the prior Forge window to the bracket's end.</summary>
    internal FireNoiseShot Begin(ItemEquippable? weapon)
    {
        var previous = _current;
        previous.ScopeService = this;
        _current = default; // Even an unconfigured nested Fire masks the outer gun's parameters.
        if (_snapshot.BlockCount == 0 || weapon == null) return previous;
        if (!_guns.TryGetValue(weapon.Pointer, out var gun))
        {
            var blockId = EquipmentNativeAdapter.GearBlockId(weapon.GearIDRange);
            _guns[weapon.Pointer] = gun = blockId != null && _snapshot.TryGet(blockId, out var found) ? (weapon, blockId, found) : (weapon, null, default);
        }
        if (gun.Block == null) return previous;
        var owner = weapon.Owner;
        if (owner != null) _current = new FireNoiseShot { Owner = owner, Radii = gun.Radii, Block = gun.Block, Service = this };
        return previous;
    }

    /// <summary>Release this invocation's parameters without touching any native noise history.</summary>
    internal void End(FireNoiseShot shot) => _current = shot;

    internal FireNoiseShot Parameter(PlayerAgent owner, ref Agent.NoiseType noise)
    {
        var shot = _current;
        if (shot.Owner == null || shot.Owner.Pointer != owner.Pointer || noise != Agent.NoiseType.Shoot) return default;
        // Fire has no MakeNoise call. Adapt only its current setter argument; the original setter still owns
        // priority, expiry and any intervening noise. Never restore an old owner flag or timestamp.
        noise = Agent.NoiseType.Silent;
        return shot;
    }

    internal void Emit(FireNoiseShot shot)
    {
        var owner = shot.Owner;
        if (owner == null) return;
        if (!OwnedHere(owner)) return;
        var node = owner.CourseNode;
        if (node == null)
        {
            _once.Report(shot.Block!, "weapon.fire-noise-node-missing block=" + shot.Block
                + ": the shooter stands on no course node, so the gear's shot noise was not made.");
            return;
        }
        // ForgeCore submits the game's noise request (the native value is built there, field by field).
        if (!ForgeCore.Combat.Noise(owner.Position, shot.Radii.Min, shot.Radii.Max, VerticalScale, node, NM_NoiseType.InstaDetect, true, false)) return;
        if (_log.Logs(RuntimeLogLevel.Trace))
            _log.Write(RuntimeLogLevel.Trace, WeaponLogCodes.FireNoise, new RuntimeLogFacts
            {
                Values = new[] { RuntimeLogValue.Of("block", shot.Block!), EquipmentNativeAdapter.WeaponName(shot.Block!), RuntimeLogValue.Of("radiusMin", (double)shot.Radii.Min),
                    RuntimeLogValue.Of("radiusMax", (double)shot.Radii.Max) }
            });
    }

    // The shooter's own machine: its local player, or a bot, which only the master drives.
    private static bool OwnedHere(PlayerAgent owner)
    {
        var player = owner.Owner;
        return player != null && (player.IsLocal || (player.IsBot && Session.IsHost));
    }
}

// The four Slot 151 bodies, subscribed through ForgeCore's `GameHooks.Fire`. Cleanup uses the captured service
// directly, including after a session fault, and runs whether the body returned or threw.
// The four targets are the same four bodies the shot facts are counted from (evidence/w4-player-fire.json).
internal static class FireNoiseHooks
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.Fire.BulletWeapon.Around(Begin, End, Faulted);
        ForgeCore.GameHooks.Fire.Shotgun.Around(Begin, End, Faulted);
        ForgeCore.GameHooks.Fire.BulletWeaponSynced.Around(Begin, End, Faulted);
        ForgeCore.GameHooks.Fire.ShotgunSynced.Around(Begin, End, Faulted);
    }

    private static FireNoiseShot Begin(ItemEquippable weapon)
        => WeaponNativeSession.Offer(WeaponFeature.Fire)?.Guard(static (session, fired) => session.FireNoise.Begin(fired), weapon, default(FireNoiseShot)) ?? default;

    private static void End(ItemEquippable weapon, FireNoiseShot shot, bool ran) => shot.ScopeService?.End(shot);

    private static void Faulted(ItemEquippable weapon, FireNoiseShot shot, Exception error) => shot.ScopeService?.End(shot);
}

[HarmonyPatch(typeof(PlayerAgent), "set_Noise")]
internal static class ShotNoiseParameter
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(PlayerAgent __instance, ref Agent.NoiseType __0, out FireNoiseShot __state)
    {
        __state = default;
        var session = WeaponNativeSession.Offer(WeaponFeature.Fire);
        if (session is not { Faulted: false }) return;
        __state = session.FireNoise.Parameter(__instance, ref __0);
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(FireNoiseShot __state, bool __runOriginal)
    {
        if (__runOriginal && __state.Service != null)
            WeaponNativeSession.Offer(WeaponFeature.Fire)?.Guard(static (session, shot) => session.FireNoise.Emit(shot), __state);
    }
}
