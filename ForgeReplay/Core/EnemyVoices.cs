using System;

namespace ForgeReplay;

/// <summary>The game's own Wwise voice events of an enemy family, picked by the enemy data block's name (the game has no sound field in the
/// block; its enemy scripts post these events by family). Names are the fields of the game's <c>AK.EVENTS</c>; effect names are the game effects ForgeVisual lists. A family without its own
/// event uses the generic enemy one.</summary>
internal readonly record struct EnemyVoice(string Death, string Hurt, string Scream, string TongueOut, string TongueBack, string DeathFx)
{
    private const string Generic = "";
    private static readonly EnemyVoice Default = new("ENEMYVOICEDEATH", "ENEMYVOICEHURT", "ENEMYDETECTIONSCREAM", "SNATCHERTENTACLESHOOT", Generic, "Game:FXC_MeshBloodBomb");

    /// <summary>The voices of the enemy named <paramref name="blockName"/> (for example <c>Striker_Big_Wave</c>); the longest matching family wins.</summary>
    internal static EnemyVoice Of(string? blockName)
    {
        var n = blockName ?? "";
        bool Is(string prefix) => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (Is("Striker_Big")) return Default with { Death = "STRIKER_BIG_DIE", Hurt = "STRIKER_BIG_HURT_SHORT", Scream = "STRIKERDETECTSCREAM", TongueOut = "STRIKER_BIG_CHARGE_TENTACLE", TongueBack = "STRIKER_BIG_TENTACLE_RETRACT" };
        if (Is("Striker_Child")) return Default with { Death = "STRIKER_CHILD_DIE", Hurt = "STRIKER_CHILD_HURT_SHORT", Scream = "STRIKERDETECTSCREAM" };
        if (Is("Striker")) return Default with { Death = "STRIKER_DIE", Hurt = "STRIKER_HURT_SHORT", Scream = "STRIKERDETECTSCREAM" };
        if (Is("Shooter")) return Default with { Death = "SHOOTERDEATHSCREAM", Hurt = "SHOOTERHURT", Scream = "SHOOTER_COMBAT_SCREAM" };
        if (Is("Birther_Boss") || Is("MegaMother")) return Default with { Death = "BIRTHER_BOSS_DEATHSCREAM", Hurt = "BIRTHER_BOSS_HURT_SMALL" };
        if (Is("Birther")) return Default with { Death = "BIRTHERDEATHSCREAM", Hurt = "BIRTHER_HURT_SMALL" };
        if (Is("Scout")) return Default with { Death = "SCOUT_DEATH_SCREAM", Scream = "SCOUT_DETECT_SCREAM" };
        if (Is("Shadow")) return Default with { Hurt = "SHADOW_VOICE_HURT_SMALL", Scream = "SHADOW_VOICE_SCREAM_SMALL" };
        if (Is("Tank")) return Default with { Death = "TANK_DIE", Hurt = "TANK_HURT_SHORT" };
        if (Is("Flyer_Big")) return Default with { Death = "FLYER_BIG_DIE", Hurt = "FLYER_BIG_HURT_SMALL", Scream = "FLYER_BIG_SCREAM", DeathFx = "Game:fxc_flyerbigdeath" };
        if (Is("Flyer")) return Default with { Death = "FLYER_DIE", Hurt = "FLYER_HURT_SMALL", Scream = "FLYER_SCREAM", DeathFx = "Game:fxc_flyerdeath" };
        if (Is("Pouncer")) return Default with { Death = "POUNCER_DIE", Hurt = "POUNCER_HURT_SML" };
        if (Is("Squid")) return Default with { Hurt = "IMMORTALHURT" };
        return Default;
    }
}
