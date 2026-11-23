using System;
using System.Collections.Generic;
using Enemies;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;
using GameData;
using HarmonyLib;
using UnityEngine;

namespace ForgeEnemy.Native;

// The limb hook family: the prefab postfix that resolves a type's limbs and the three per-hit postfixes
// plus the one destruction prefix that apply what a document wrote about them. Attachment crack stages are decided
// by <see cref="EnemyModelLimbCracks"/> from the existing damage postfix and install no hook of their own. The per-hit hooks are the reason
// the rules are a table keyed by enemy type rather than a lookup through the session: they run on every hit, and
// a table that is empty makes them return before anything else happens.

/// <summary>
/// <c>Enemies.EnemyPrefabManager.BuildEnemyPrefab(EnemyDataBlock data)</c>, the same postfix the appearance
/// assembler runs from: it is the one moment a type's limb array and model exist together, and it happens once per
/// type rather than once per life. It resolves the parts and rebuilds the hit-time rule table; it writes nothing
/// to the prefab, so a refusal keeps the vanilla limbs.
/// </summary>
[HarmonyPatch(typeof(EnemyPrefabManager), nameof(EnemyPrefabManager.BuildEnemyPrefab))]
internal static class EnemyPrefabLimbs
{
    [HarmonyPostfix]
    private static void Postfix(EnemyDataBlock data, GameObject __result)
        => EnemyHost.Offer(EnemyFeature.LimbProfile)?.Guard(module => module.AssembleLimbs(data, __result));
}

/// <summary>
/// One enemy type's hit-time limb rules, as the native limb ids the prefab resolution answered. It exists because
/// the per-hit hooks must not allocate, take a string or walk a document: the table is built once per type at
/// prefab build, and a hit is one dictionary lookup and a few array reads.
///
/// The arrays are indexed by `m_limbID` and `NaN` means "this document said nothing", which is why a missing value
/// is not a zero: a zero maximum or a zero destroyed multiplier is a value an author may write.
/// </summary>
internal sealed class EnemyLimbRule
{
    internal EnemyLimbRule(int count)
    {
        MaxDamage = new float[count];
        DestroyedMultiplier = new float[count];
        BlockDestruction = new bool[count];
        for (int index = 0; index < count; index++)
        {
            MaxDamage[index] = float.NaN;
            DestroyedMultiplier[index] = float.NaN;
        }
        BackDamageMultiplier = float.NaN;
    }

    internal float[] MaxDamage { get; }
    internal float[] DestroyedMultiplier { get; }
    internal bool[] BlockDestruction { get; }
    internal float BackDamageMultiplier { get; set; }
    internal int Count => MaxDamage.Length;
}

/// <summary>
/// The one rule table the per-hit hooks read, keyed by the enemy type the receiver's owner names. It is static
/// because the hooks are static and run outside any session callback; the module owns its contents and is the only
/// writer, and a type whose prefab carries no hit-time values drops its entry.
///
/// The three formulas are separated from the native reads so the EnemyProfile suite can state them without a game:
/// `LimitDamage` is the destroyed / capped choice, `ScaleBehindBonus` is the behind-bonus scaling, and the reads
/// around them are one lookup plus the fields the table is keyed by.
/// </summary>
internal static class EnemyLimbRules
{
    private static Dictionary<uint, EnemyLimbRule>? _table;

    internal static void Set(uint typeId, EnemyLimbRule rule)
    {
        _table ??= new Dictionary<uint, EnemyLimbRule>();
        _table[typeId] = rule;
    }

    internal static void Remove(uint typeId) => _table?.Remove(typeId);

    internal static void Clear() => _table?.Clear();

    /// <summary>
    /// Builds the hit-time rule for one type out of the document and the resolved limbs. The array covers every
    /// limb the resolution named, so a limb the document does not mention is `NaN` rather than out of range. A
    /// document with no hit-time value at all answers null: the type then has no entry and the hooks skip it.
    /// </summary>
    internal static EnemyLimbRule? Build(EnemyProfileResolution profile, EnemyLimbResolution resolution)
    {
        int count = resolution.LimbCount;
        var rule = new EnemyLimbRule(count);
        bool any = false;
        foreach (var entry in profile.Limbs)
        {
            if (entry.MaxDamage == null && entry.DestroyedMultiplier == null && entry.Breakable != false) continue;
            if (!resolution.TryLimbIds(entry.Part, out var ids)) continue;
            any = true;
            foreach (int id in ids)
            {
                if (id < 0 || id >= count) continue;
                if (entry.MaxDamage is float max) rule.MaxDamage[id] = max;
                if (entry.DestroyedMultiplier is float destroyed) rule.DestroyedMultiplier[id] = destroyed;
                if (entry.Breakable == false) rule.BlockDestruction[id] = true;
            }
        }
        if (profile.BackDamageMultiplier is float back)
        {
            rule.BackDamageMultiplier = back;
            any = true;
        }
        return any ? rule : null;
    }

    /// <summary>The rule standing on this limb's owner, or false when the type has none. One table read and one
    /// block getter: this is the whole per-hit lookup cost.</summary>
    internal static bool TryFind(Dam_EnemyDamageLimb limb, out EnemyLimbRule rule)
    {
        rule = null!;
        var table = _table;
        if (table == null || table.Count == 0 || limb == null) return false;
        var damage = limb.m_base;
        if (damage == null) return false;
        var owner = damage.Owner;
        if (owner == null) return false;
        return table.TryGetValue(owner.EnemyDataID, out rule!);
    }

    /// <summary>The limb's own damage-modifier result, after the document's destroyed multiplier or its per-hit
    /// maximum. A limb id outside the table is a limb the type did not resolve, so the result stands, and a limb the
    /// document named no hit-time value for answers the native result without the destruction state being read.</summary>
    internal static float Modifiers(Dam_EnemyDamageLimb limb, float result)
    {
        if (!TryFind(limb, out var rule)) return result;
        int id = limb.m_limbID;
        if ((uint)id >= (uint)rule.Count) return result;
        float destroyedMultiplier = rule.DestroyedMultiplier[id], maxDamage = rule.MaxDamage[id];
        if (float.IsNaN(destroyedMultiplier) && float.IsNaN(maxDamage)) return result;
        return LimitDamage(result, limb.IsDestroyed, destroyedMultiplier, maxDamage);
    }

    /// <summary>The behind-bonus result, scaled by the enemy type's multiplier. The bonus needs no limb index: it
    /// is a type constant, so a limb id outside the table still scales.</summary>
    internal static float BehindBonus(Dam_EnemyDamageLimb limb, float dam, float result)
    {
        if (dam <= 0f) return result;
        if (!TryFind(limb, out var rule)) return result;
        return ScaleBehindBonus(dam, result, rule.BackDamageMultiplier);
    }

    /// <summary>Whether the document marked this limb unbreakable.</summary>
    internal static bool BlocksDestruction(Dam_EnemyDamageLimb limb)
    {
        if (!TryFind(limb, out var rule)) return false;
        int id = limb.m_limbID;
        return (uint)id < (uint)rule.Count && rule.BlockDestruction[id];
    }

    /// <summary>
    /// The destroyed / capped choice, in the order the plan states: a destroyed limb with a destroyed multiplier
    /// takes `result * multiplier` (the limb's own modifier result, so the weakspot and precision factors stay), and otherwise the result is capped at the per-hit maximum. Both members are
    /// `NaN` when the document said nothing, and `NaN` compares false, so an unset member never applies.
    /// </summary>
    internal static float LimitDamage(float result, bool destroyed, float destroyedMultiplier, float maxDamage)
    {
        if (destroyed && !float.IsNaN(destroyedMultiplier)) return result * destroyedMultiplier;
        if (!float.IsNaN(maxDamage) && result > maxDamage) return maxDamage;
        return result;
    }

    /// <summary>
    /// The behind bonus, scaled by the type's multiplier. The native bonus is a ratio over the un-bonused damage,
    /// so the ratio the receiver applied is recovered first and only the part above neutral (1) is scaled:
    /// `dam * (1 + (r - 1) * (m - 1))`, with `r` the ratio recovered and `m` the multiplier. A multiplier of one
    /// removes the bonus (the website writes `AllowDamgeBonusFromBehind = false` for it), two leaves the vanilla
    /// bonus as the game applied it, and any other value, zero and negative included, is written as it stands; only
    /// an unset multiplier leaves the result alone. With the vanilla ratio at x2 (believed, and the website presets
    /// the same value) the result is `dam * m`; the exact ratio the receiver applies is a game-time trace, not
    /// something this build has measured, and for any other ratio the author's number is not the factor applied.
    /// </summary>
    internal static float ScaleBehindBonus(float dam, float result, float multiplier)
    {
        if (dam <= 0f || float.IsNaN(multiplier)) return result;
        float ratio = result / dam;
        if (ratio <= 1f) return result;
        return dam * (1f + (ratio - 1f) * (multiplier - 1f));
    }
}

/// <summary>
/// <c>Dam_EnemyDamageLimb.ApplyWeakspotAndArmorModifiers(float dam, float precisionMulti)</c> (public virtual in
/// the interop of build 20403457; the native declaration is protected, which the interop does not keep). It is the
/// limb's own modifier step, so the document's cap and destroyed multiplier land here rather than on the hit's
/// total. The prefix/postfix body must not allocate: the table lookup is the first thing it does and an empty table
/// returns immediately.
/// </summary>
[HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.ApplyWeakspotAndArmorModifiers))]
internal static class EnemyLimbModifiers
{
    [HarmonyPostfix]
    private static void Postfix(Dam_EnemyDamageLimb __instance, ref float __result)
    {
        // A sac limb is answered by its own override's postfix below. Whether that override calls this base body is
        // not something the interop shows, and if it does, this postfix would run after the sac's and apply the
        // document's multiplier twice. Answering here only for the limbs whose own member is this one makes the
        // result the same either way.
        if (__instance is Dam_EnemyDamageLimb_Custom || !EnemyHost.Stands(EnemyFeature.LimbProfile)) return;
        // A native getter that throws inside the damage path would abort the game's own hit handling, and this
        // hook has no session entry that can fault the module instead (a `ref` result cannot be captured in the
        // guarded callback the other hooks use). The vanilla value therefore stands.
        try { __result = EnemyLimbRules.Modifiers(__instance, __result); }
        catch (Exception error) { NativeFault.Note("enemy.limb.damage-modifier", error); }
    }
}

/// <summary>
/// The sac limb's own override of the same member. Harmony patches the declared override and the base separately,
/// so a `Dam_EnemyDamageLimb_Custom` limb is answered here and every other limb by the base class above — which
/// steps aside for this type so exactly one of the two applies the document's values.
/// </summary>
[HarmonyPatch(typeof(Dam_EnemyDamageLimb_Custom), nameof(Dam_EnemyDamageLimb_Custom.ApplyWeakspotAndArmorModifiers))]
internal static class EnemyLimbCustomModifiers
{
    [HarmonyPostfix]
    private static void Postfix(Dam_EnemyDamageLimb_Custom __instance, ref float __result)
    {
        if (!EnemyHost.Stands(EnemyFeature.LimbProfile)) return;
        try { __result = EnemyLimbRules.Modifiers(__instance, __result); }
        catch (Exception error) { NativeFault.Note("enemy.limb.explosion-modifier", error); }
    }
}

/// <summary>
/// <c>Dam_EnemyDamageLimb.ApplyDamageFromBehindBonus(float dam, Vector3 pos, Vector3 dir, float backstabberMulti)</c>,
/// the behind-bonus step of the limb's modifier chain. The sac limb does not override it, so one patch covers every
/// limb. The scaling recovers the ratio the receiver applied from its own answer; the method is only reached for a
/// hit the game considers a behind hit, so a hit from the front never passes through here.
/// </summary>
[HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.ApplyDamageFromBehindBonus))]
internal static class EnemyLimbBackBonus
{
    [HarmonyPostfix]
    private static void Postfix(Dam_EnemyDamageLimb __instance, float dam, ref float __result)
    {
        if (!EnemyHost.Stands(EnemyFeature.LimbProfile)) return;
        try { __result = EnemyLimbRules.BehindBonus(__instance, dam, __result); }
        catch (Exception error) { NativeFault.Note("enemy.limb.back-damage", error); }
    }
}

/// <summary>
/// <c>Dam_EnemyDamageBase.CheckDestruction(Dam_EnemyDamageLimb limb, ref Vector3 localPos, ref Vector3 direction,
/// int limbID, ref CD_DestructionSeverity severity, ref bool tryForceHitreact, ref ES_HitreactType hitreact)</c>
/// (private in the native build, public in the interop of build 20403457). It is the host's single-limb
/// destruction decision: the frozen damage-window record shows its body calling `SendDestroyLimb` (site
/// 0x137D64D), so returning false for one limb skips that limb's break while the hit itself and every other limb
/// are untouched. A limb the game cannot break at all keeps the vanilla answer of the whole method.
/// </summary>
[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.CheckDestruction))]
internal static class EnemyLimbDestructionBlocked
{
    [HarmonyPrefix]
    private static bool Prefix(Dam_EnemyDamageLimb limb)
    {
        bool profile = EnemyHost.Stands(EnemyFeature.LimbProfile), model = EnemyHost.Stands(EnemyFeature.ModelLimbs);
        if (!profile && !model) return true;
        try
        {
            return !(profile && EnemyLimbRules.BlocksDestruction(limb))
                && !(model && EnemyModelHitParts.BlocksDestruction(limb));
        }
        catch (Exception error) { NativeFault.Note("enemy.limb.break-check", error); return true; }
    }
}

/// <summary>
/// The crack stage of an attachment part, decided by the host from the hit that just landed. The enemy damage
/// postfix (`EnemyDamage` on ProcessReceivedDamage) hands over the limb index the receiver named; only the host's
/// limb health is authoritative, because a client applies just its own hits locally and no packet carries limb
/// health (ReceiveBulletDamage 0x137EE6A writes m_health on the machine that runs it and sends nothing), so the
/// stage reaches the other machines through the replicated stage table, never their own reading.
/// </summary>
internal static class EnemyModelLimbCracks
{
    /// <summary>Called from the damage postfix with the gate already read: no live cracking part returns before
    /// the module is reached, so an enemy set without crack stages pays one static read per hit.</summary>
    internal static void AfterDamage(EnemyModule module, Dam_EnemyDamageBase receiver, int limbID)
    {
        if (EnemyModelHitParts.CrackCount == 0 || limbID <= 0) return;
        module.AfterModelLimbDamage(receiver, limbID);
    }
}
