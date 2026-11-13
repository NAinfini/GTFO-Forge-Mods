using ForgeAudio;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

/// <summary>Arsenal's sounds: the <c>Arsenal/audio</c> folder next to the DLL, the part's sound set (<see cref="PartSounds.Arsenal"/>,
/// which the plugin loads). The cue settings (gain, range, priority, limits, the return sound's half-second gap) live in
/// <c>audio/cues.forge</c>. AudioKit logs every cue that cannot load or play by name.</summary>
internal static class ArsenalAudio
{
    internal static class Cue
    {
        internal const string Incoming = "strike.incoming_shell";
        internal const string SmallBoom = "strike.small_boom";
        internal const string BigBoom = "strike.big_boom";
        internal const string Debris = "strike.debris";
        internal const string ClusterSplit = "strike.cluster_split";
        internal const string FireIgnite = "strike.fire_ignite";
        internal const string FireLoop = "strike.fire_loop";
        internal const string GasRelease = "strike.gas_release";
        internal const string GasLoop = "strike.gas_loop";
        internal const string StasisPulse = "strike.stasis_pulse";
        internal const string StasisLoop = "strike.stasis_loop";
        internal const string BeamStart = "strike.beam_start";
        internal const string BeamLoop = "strike.beam_loop";
        internal const string BeamCutting = "strike.beam_cutting";
        internal const string BeamEnd = "strike.beam_end";
        internal const string Handover = "weapon.handover";
        internal const string GrenadeFire = "weapon.grenade_fire";
        internal const string GrenadeReload = "weapon.grenade_reload";
        internal const string GrenadeBurst = "weapon.grenade_burst";
        internal const string WeaponReturn = "weapon.return";
        internal const string TurretDeploy = "turret.deploy";
        internal const string TurretIdle = "turret.idle";
        internal const string TurretExpire = "turret.expire";
        internal const string TossSpin = "device.toss_spin";
        internal const string TossOne = "device.toss_one";
        internal const string RadarDeploy = "radar.deploy";
        internal const string RadarSweep = "radar.sweep";
        internal const string FloodFan = "floodlight.fan";
        internal const string FloodOn = "floodlight.on";
        internal const string MineLand = "mine.land";
        internal const string MineTrigger = "mine.trigger";
        internal const string MineExplode = "mine.explode";
        internal const string GlueLand = "glue.land";
        internal const string GlueTrigger = "glue.trigger";
        internal const string RecoillessFire = "weapon.recoilless_fire";
        internal const string RecoillessBackblast = "weapon.recoilless_backblast";
        internal const string RecoillessBurst = "weapon.recoilless_burst";
        internal const string HomingFire = "weapon.homing_fire";
        internal const string StrafeLine = "strike.strafe_line";
        internal const string StrafeImpact = "strike.strafe_impact";
        internal const string HunterLock = "strike.hunter_lock";
    }

    /// <summary>The game's own glue gun shot (<c>AK.EVENTS.GLUEGUNFIRE1SHOT</c>), played through ForgeAudio's vanilla entry.</summary>
    internal const string GlueGunShotEvent = "GLUEGUNFIRE1SHOT";

    /// <summary>A loop on <paramref name="owner"/> that plays while <paramref name="keepWhile"/> is true and ends with the owner.</summary>
    internal static void Loop(string cue, GameObject owner, System.Func<bool> keepWhile) => PartSounds.Arsenal?.Sustain(cue, owner, null, keepWhile);

    /// <summary>The local shooter's own shot (ForgeAudio only reads the options, so the one instance serves every play).</summary>
    private static readonly PlayOptions SelfOptions = new() { IsSelf = true };

    /// <summary>A sound at a point, from <paramref name="start"/> seconds into it (a whistle cut so that its peak meets the impact).</summary>
    internal static void PlayAt(string cue, Vector3 position, float start = 0f, float gain = 1f)
    {
        PartSounds.Arsenal?.Play(cue, position, start > 0f || gain != 1f ? new PlayOptions { StartSeconds = start, Gain = gain } : null);
    }

    /// <summary>The library's silent start of every one-shot cue (see <see cref="AudioLibrary.Prewarm"/>), so the first real play of a cue
    /// does not pay its first-start cost (a hitch of tens of milliseconds, measured on the first falling shell and the first landing) in
    /// the middle of a strike. One cue per call; true once every cue was started.</summary>
    internal static bool Prewarm() => PartSounds.Arsenal?.Prewarm() ?? true;

    /// <summary>A shot of the local shooter (2D) or of another player (at the muzzle).</summary>
    internal static void PlayShot(string cue, Vector3 position, bool isSelf)
    {
        PartSounds.Arsenal?.Play(cue, position, isSelf ? SelfOptions : null);
    }

    internal static void PlayAttached(string cue, GameObject owner, int dimension, bool isSelf = false)
    {
        PartSounds.Arsenal?.Play(cue, owner, new PlayOptions { IsSelf = isSelf, Dimension = dimension });
    }

    /// <summary>A sound that follows <paramref name="owner"/> (3D); a looping cue plays until the owner is destroyed.</summary>
    internal static void PlayOn(string cue, GameObject owner, float start = 0f)
    {
        PartSounds.Arsenal?.Play(cue, owner, start > 0f ? new PlayOptions { StartSeconds = start } : null);
    }
}
