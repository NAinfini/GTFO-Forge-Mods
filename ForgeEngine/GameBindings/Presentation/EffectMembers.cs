using System;
using System.Collections.Generic;

namespace ForgeEngine.Presentation;

/// <summary>Where a member of `forge.action.presentation.effect_play` comes from, which is also how it is played.
/// Every original effect is taken through an entry the game itself uses; a Forge member is drawn on the viewer's own
/// machine from the effect bundle ForgeVisual ships.</summary>
internal enum EffectSource
{
    /// <summary>A prefab in the game's own preload bundle ("Preloaded Effects", referenced by `FX_Manager`), taken
    /// with `FX_Manager.GetPreloadedEffectPool(name)` and played from the pool.</summary>
    PreloadedPool,
    /// <summary>A pooled effect prefab in the game's Resources table, loaded with `Resources.Load(path)` and played
    /// from the pool `FX_Manager.GetEffectPool(prefab)` hands back.</summary>
    ResourcePrefab,
    /// <summary>A third-person muzzle flash from the startup asset shards: an `EX_SpriteMuzzleFlash` instance of the
    /// prefab at the `Assets/` path, played with its own `Play`.</summary>
    MuzzleFlash,
    /// <summary>An effect the game does not have, drawn by Forge on each viewer's machine from ForgeVisual's effect bundle; presentation only.</summary>
    Forge
}

/// <summary>One member of the effect card.
///
/// <paramref name="Native"/> is the original effect's own name for the entry its source takes: the preload bundle's
/// prefab name, the Resources path, or the `Assets/` path of the muzzle-flash prefab. <paramref name="AssetPath"/> is
/// the original `Assets/` path when the game data spells one, recorded so every original member names the resource
/// it plays. A line member (<paramref name="NeedsEnd"/>) is drawn from `position` to `end` and refuses a request that
/// names no end; every other member refuses an end. <paramref name="DefaultSize"/> is the size a Forge member draws at
/// when `size` is unset, and <see cref="float.NaN"/> for a member that has no size, which refuses one.
///
/// Every member is played by ForgeVisual's `Fx` under <see cref="VisualName"/>: the game's effect catalog (`Game:`) for an
/// original member, the bundle Forge ships (`Forge:`) for a Forge member. How a Forge member draws (line, size, hold) is
/// ForgeVisual's own table; the offline tests keep this table and that one in step.</summary>
internal sealed record EffectMember(string Id, EffectSource Source, string Native, string? AssetPath,
    bool NeedsEnd, bool NeedsDirection, float DefaultSize, float MaximumSize)
{
    public bool TakesSize => !float.IsNaN(DefaultSize);

    /// <summary>The name ForgeVisual knows this member by.</summary>
    public string VisualName => Source switch
    {
        EffectSource.Forge => "Forge:" + Id,
        EffectSource.MuzzleFlash => "Game:" + System.IO.Path.GetFileNameWithoutExtension(Native),
        _ => "Game:" + Native.Replace(' ', '_')
    };
}

/// <summary>The members of `forge.action.presentation.effect_play`, in the catalog's own order. The table is the
/// one place a member's native entry is spelled; the inventory behind it (every effect prefab the game data holds
/// and why the instance-only ones are not members) is recorded with the F4 evidence.</summary>
internal static class EffectMembers
{
    private const string FxEffects = "Assets/AssetPrefabs/FX_Effects/";
    private const string Muzzle = "Assets/AssetPrefabs/Effects/Weapon/MuzzleFlash/";

    public static readonly IReadOnlyList<EffectMember> All = new[]
    {
        Pooled("mine_explosion", "FX_Tripmine", FxEffects + "FX_Tripmine.prefab"),
        Pooled("glue_mine_explosion", "FX_TripmineGlue", FxEffects + "FX_TripmineGlue.prefab"),
        // The tracer reads its far end from `FX_Manager.EffectTargetPosition`, so it is a line member.
        Pooled("bullet_tracer", "FX_Tracer", FxEffects + "FX_Tracer.prefab") with { NeedsEnd = true },
        Pooled("bullet_impact_concrete", "FX_BulletImpact_Concrete"),
        Pooled("bullet_impact_metal", "FX_BulletImpact_Metal"),
        Pooled("bullet_impact_sheet_metal", "FX_BulletImpact_SheetMetal"),
        Pooled("bullet_impact_glass", "FX_BulletImpact_Glass"),
        Pooled("bullet_impact_wood", "FX_BulletImpact_Wood"),
        Pooled("bullet_impact_plastic", "FX_BulletImpact_Plastic"),
        Pooled("bullet_impact_mud", "FX_BulletImpact_Mud"),
        Pooled("bullet_impact_gravel", "FX_BulletImpact_Gravel"),
        Pooled("blood_impact", "FX_3D Blood Impact"),
        Pooled("blood_burst", "FXC_MeshBloodBomb"),
        Pooled("torch_cutting", "FX_TorchCutting"),
        Pooled("elevator_sparks", "FX_ElevatorSparks"),
        Pooled("lock_melter", "FX_LockMelter"),
        Pooled("infection_spit", "FX_InfectionSpit"),
        Resource("flyer_death", "fxc_flyerdeath"),
        Resource("flyer_big_death", "fxc_flyerbigdeath"),
        Resource("flyer_teleport", "fx_flyer_teleport"),
        Resource("flyer_big_teleport", "fx_flyer_big_teleport"),
        Resource("flesh_bulb_pop", "fxc_fleshbulb_pop"),
        Resource("flesh_bulb_pop_boss", "fxc_fleshbulb_pop_boss"),
        Flash("muzzle_flash_rifle", "AssaultRifle_MuzzleFlash_3RD.prefab"),
        Flash("muzzle_flash_pistol", "Pistol_MuzzleFlash_3RD.prefab"),
        Flash("muzzle_flash_shotgun", "Shotgun_MuzzleFlash_3RD.prefab"),
        Flash("muzzle_flash_smg", "SubMachine_MuzzleFlash_3RD.prefab"),
        Flash("muzzle_flash_sentry", "Sentrygun_MuzzleFlash_3RD.prefab"),
        Own("forge_energy_beam", needsEnd: true),
        Own("forge_lightning_arc", needsEnd: true),
        Own("forge_blast_tracer", needsEnd: true),
        Own("forge_plasma_discharge", needsEnd: true),
        Own("forge_plasma_orb", size: 0.3f, maximum: 4f),
        Own("forge_plasma_explosion", size: 3f, maximum: 12f),
        Own("forge_explosion", size: 2.2f, maximum: 8f),
        Own("forge_flame", needsDirection: true, size: 8f, maximum: 30f),
        Own("forge_flame_impact"),
        Own("forge_sparks"),
        Own("forge_black_hole", size: 8f, maximum: 20f)
    };

    private static readonly Dictionary<string, EffectMember> ById = Index();

    public static bool TryGet(string? id, out EffectMember member)
    {
        member = null!;
        return id != null && ById.TryGetValue(id, out member!);
    }

    private static Dictionary<string, EffectMember> Index()
    {
        var table = new Dictionary<string, EffectMember>(StringComparer.Ordinal);
        foreach (var member in All)
            if (!table.TryAdd(member.Id, member)) throw new InvalidOperationException("Duplicate effect member " + member.Id);
        return table;
    }

    private static EffectMember Pooled(string id, string prefab, string? path = null)
        => new(id, EffectSource.PreloadedPool, prefab, path, false, false, float.NaN, float.NaN);

    private static EffectMember Resource(string id, string path)
        => new(id, EffectSource.ResourcePrefab, path, null, false, false, float.NaN, float.NaN);

    private static EffectMember Flash(string id, string file)
        => new(id, EffectSource.MuzzleFlash, Muzzle + file, Muzzle + file, false, false, float.NaN, float.NaN);

    private static EffectMember Own(string id, bool needsEnd = false, bool needsDirection = false,
        float size = float.NaN, float maximum = float.NaN)
        => new(id, EffectSource.Forge, id, null, needsEnd, needsDirection, size, maximum);
}
