using ForgeAudio;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>Arsenal's sounds: the <c>audio</c> folder next to the DLL, loaded once through ForgeAudio. The cue settings
/// (gain, range, priority, limits, the return sound's half-second gap) live in <c>audio/cues.json</c>. AudioKit logs every
/// cue that cannot load or play by name.</summary>
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
    }

    private static AudioLibrary? _sounds;

    /// <summary>The sounds, for the calls that need the library itself (a sustained loop); null before <see cref="Register"/>.</summary>
    internal static AudioLibrary? Sounds => _sounds;

    internal static void Register() => _sounds ??= AudioKit.Load(Plugin.Id);

    internal static void Dispose()
    {
        _sounds?.Dispose();
        _sounds = null;
    }

    /// <summary>The returned heavy weapon, heard by its own player (2D). Peers do not play it: the loan is local state.</summary>
    internal static void PlayReturn(GameObject owner, int dimension)
    {
        _sounds?.Play(Cue.WeaponReturn, owner, new PlayOptions { IsSelf = true, Dimension = dimension });
    }

    internal static void PlayAt(string cue, Vector3 position)
    {
        _sounds?.Play(cue, position);
    }

    /// <summary>The library's silent start of every one-shot cue (see <see cref="AudioLibrary.Prewarm"/>), so the first real play of a cue
    /// does not pay its first-start cost (a hitch of tens of milliseconds, measured on the first falling shell and the first landing) in
    /// the middle of a strike. One cue per call; true once every cue was started.</summary>
    internal static bool Prewarm() => _sounds?.Prewarm() ?? true;

    internal static void PlayAttached(string cue, GameObject owner, int dimension, bool isSelf = false)
    {
        _sounds?.Play(cue, owner, new PlayOptions { IsSelf = isSelf, Dimension = dimension });
    }

    /// <summary>A sound that follows <paramref name="owner"/> (3D).</summary>
    internal static void PlayOn(string cue, GameObject owner)
    {
        _sounds?.Play(cue, owner);
    }

    /// <summary>A loop that follows <paramref name="owner"/> until it is destroyed.</summary>
    internal static void PlayLoop(string cue, GameObject owner)
    {
        _sounds?.Play(cue, owner);
    }
}
