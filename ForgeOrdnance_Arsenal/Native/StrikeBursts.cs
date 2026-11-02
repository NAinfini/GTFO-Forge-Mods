using ForgeVisual;
using Player;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>The game side of the strike effects (<see cref="StrikeFx"/>, which only builds sprites for ForgeVisual): gives them the
/// sheets once the bundle is loaded, keeps the falling shells' trails, and maps a strike's kind to its look. Every call is local and
/// cosmetic; without the bundle nothing is drawn and the log says why once.</summary>
internal static class StrikeBursts
{
    private const int TrailSlots = 8;
    private static readonly Trail?[] Shells = new Trail?[TrailSlots];
    private static readonly TrailStyle?[] TrailStyles = new TrailStyle?[3];

    private static bool Ready()
    {
        if (StrikeFx.Ready) return true;
        var sheets = ArsenalArt.Sheets;
        if (sheets == null) { Plugin.Error("Strike blasts and trails are not drawn: " + ArsenalArt.Failure); return false; }
        StrikeFx.Init(sheets);
        return true;
    }

    /// <summary>Builds what a first strike would otherwise build in its landing frame (the decal rings, the shell trail looks, the
    /// ground mark layer of the beam's reticle) while the level is still loading; the GPU's first use of each material follows in
    /// <see cref="Tick"/> once the camera exists.</summary>
    internal static void Prepare()
    {
        var ready = Ready() && StrikeEffects.Prepare();
        if (ready) StrikeFx.Prepare();
    }

    private static bool _warmed;
    /// <summary>Once, when the player's camera exists: a moment of tiny invisible sprites of every sheet, so each material is used
    /// by the GPU before the first strike lands.</summary>
    private static void Warm(float now)
    {
        if (!StrikeFx.Ready) return;
        var agent = PlayerManager.GetLocalPlayerAgent();
        if (agent == null || agent.WasCollected || agent.FPSCamera == null) return;
        var camera = agent.FPSCamera.transform;
        StrikeFx.Warm(camera.position + camera.forward * 2f);
        _warmed = true;
    }

    internal static void Tick(float now)
    {
        if (!_inLevel) return;
        if (!_warmed) Warm(now);
        if (_priming) PrimeSounds();
    }

    /// <summary>Between the game's level-entered event and the end of the level; warming and priming only run then, since both need the player.</summary>
    private static bool _inLevel, _priming;

    internal static void EnterLevel() { _inLevel = true; _priming = true; }
    internal static void LeaveLevel() { _inLevel = false; _priming = false; }

    /// <summary>Each level, once the player stands in it: one strike sound per frame is started silently (see <see cref="ArsenalAudio.Prewarm"/>).</summary>
    private static void PrimeSounds()
    {
        var agent = PlayerManager.GetLocalPlayerAgent();
        if (agent == null || agent.WasCollected || agent.FPSCamera == null) return;
        _priming = !ArsenalAudio.Prewarm();
    }

    /// <summary>Level exit, checkpoint reload, host change: the shell trails end (ForgeVisual clears its own sprites with the level).</summary>
    internal static void Clear()
    {
        _priming = _inLevel;
        for (var i = 0; i < TrailSlots; i++) EndTrail(i);
        StrikeFx.Reset();
    }

    /// <summary>The smoke band and tail flame of a falling shell or bomblet (none for gas canisters and stasis emitters, which are
    /// dropped, not fired), starting at <paramref name="from"/>; -1 when there is none. At most <see cref="TrailSlots"/> shells fall at once.</summary>
    internal static int BeginTrail(ShellLook look, StrikeKind kind, Vector3 from, float now)
    {
        if (look is ShellLook.Canister or ShellLook.Emitter || !Ready()) return -1;
        var which = look == ShellLook.Bomblet ? 0 : kind is StrikeKind.Precision or StrikeKind.Hunter ? 2 : 1;
        var style = TrailStyles[which] ??= StrikeFx.ShellTrail(which == 0 ? 0.55f : which == 2 ? 1.3f : 1f);
        for (var i = 0; i < TrailSlots; i++)
        {
            if (Shells[i] is { Alive: true }) continue;
            Shells[i] = Trails.Rent(from, style);
            return Shells[i] == null ? -1 : i;
        }
        return -1;
    }

    /// <summary>Every frame while the shell falls: <paramref name="progress"/> 0 at the top, 1 at the floor (the flame lengthens
    /// toward the floor).</summary>
    internal static void MoveTrail(int slot, Vector3 position, float progress, float now)
    {
        if (slot >= 0 && slot < TrailSlots) Shells[slot]?.Move(position, progress);
    }

    internal static void EndTrail(int slot)
    {
        if (slot < 0 || slot >= TrailSlots) return;
        Shells[slot]?.End();
        Shells[slot] = null;
    }
    /// <summary>A shell's landing: the big shells (precision, hunter) get the shockwave, smoke column and scorch; the incendiary
    /// shell a splash of fire out to its burn area; every other shell the small dust ring and sparks.</summary>
    internal static void Blast(StrikeKind kind, Vector3 at, float radius, float now)
    {
        if (!Ready()) return;
        switch (kind)
        {
            case StrikeKind.Precision:
            case StrikeKind.Hunter:
                StrikeFx.BigBlast(at, radius);
                break;
            case StrikeKind.Incendiary:
                StrikeFx.FireSplash(at, radius * 1.7f);
                break;
            case StrikeKind.Cluster:
                StrikeFx.SmallBlast(at, radius * 0.7f, true);
                break;
            default:
                StrikeFx.SmallBlast(at, radius);
                break;
        }
    }

    /// <summary>A cluster strike's one scorch and one short light, at its centre; drawn once per call, by its first landing.</summary>
    internal static void ClusterCentre(Vector3 at, float blast, float now)
    {
        if (Ready()) StrikeFx.ClusterCentre(at, blast);
    }

    private static readonly float[] Scatter = new float[ClusterScatter.Sparklets * 2];

    /// <summary>The bomblet splits at <paramref name="burst"/>; its sparklets land around <paramref name="impact"/> at points every
    /// peer derives from the call's seed and the impact point.</summary>
    internal static void ClusterBurst(ulong summoner, uint throwId, Vector3 burst, Vector3 impact, float blast, float flight, float now)
    {
        if (!Ready()) return;
        ClusterScatter.Offsets(ClusterScatter.Seed(summoner, throwId, impact.x, impact.z), ClusterScatter.Sparklets, blast * 0.9f, Scatter);
        StrikeFx.ClusterBurst(burst, impact, Scatter, ClusterScatter.Sparklets, flight, 1f);
    }

    internal static void GasBurst(Vector3 at, float now)
    {
        if (Ready()) StrikeFx.GasBurst(at);
    }

    internal static void StasisPulse(Vector3 centre, float radius, float now)
    {
        if (Ready()) StrikeFx.StasisPulse(centre, radius);
    }

    internal static void BeamCharge(Vector3 at, Color colour, float now)
    {
        if (Ready()) StrikeFx.BeamCharge(at, colour);
    }

    internal static void BeamMark(Vector3 at, Color colour, float now)
    {
        if (Ready()) StrikeFx.BeamMark(at, colour);
    }

    /// <summary>The returned heavy weapon's shimmer, in front of the local player's camera where the gun is held (low and to the
    /// side, small and faint: it never covers the view).</summary>
    internal static void ReturnGlow(PlayerAgent agent, float now)
    {
        if (agent.FPSCamera == null || !Ready()) return;
        var camera = agent.FPSCamera.transform;
        StrikeFx.ReturnGlow(camera.position + camera.forward * 0.55f + camera.right * 0.14f - camera.up * 0.14f);
    }
}
