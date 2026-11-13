using ForgeVisual;
using Player;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

/// <summary>The game side of the strike effects (<see cref="StrikeFx"/>, which only builds sprites for ForgeVisual): gives them the
/// sheets once the bundle is loaded, keeps the falling shells' trails, and maps a strike's kind to its look. Every call is local and
/// cosmetic; without the bundle nothing is drawn and the log says why once.</summary>
internal static class StrikeBursts
{
    private const int TrailSlots = 8;
    private static readonly Trail?[] Shells = new Trail?[TrailSlots];
    /// <summary>The library's falling-shell effect held for a trail slot (null: the slot holds a dropped canister's or emitter's sprite trail, or nothing).</summary>
    private static readonly FxHandle?[] LibraryTrails = new FxHandle?[TrailSlots];
    /// <summary>The hold key of the shell now falling in each trail slot, taken new when the slot begins a trail so a reused slot never moves the last shell's copy.</summary>
    private static readonly int[] TrailKeys = new int[TrailSlots];
    private static TrailStyle? _canisterTrail, _emitterTrail;
    /// <summary>The one "no target" text at a time, and when it goes.</summary>
    private static WorldText? _noTarget;
    private static float _noTargetEnds;
    private const float NoTargetSeconds = 1.6f;

    private static bool Ready()
    {
        if (StrikeFx.Ready) return true;
        var sheets = ArsenalArt.Sheets;
        if (sheets == null) { Plugin.Logger.ErrorOnce("arsenal-strike-art-missing", "Strike blasts and trails are not drawn: " + ArsenalArt.Failure); return false; }
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
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected) return;
        var view = agent.FPSCamera;
        if (view == null) return;
        var camera = view.transform;
        StrikeFx.Warm(camera.position + camera.forward * 2f);
        _warmed = true;
    }

    internal static void Tick(float now)
    {
        if (!_inLevel) return;
        if (!_warmed) Warm(now);
        if (_priming) PrimeSounds();
        if (_noTarget != null) TickNoTarget(now);
    }

    /// <summary>The laser found nothing: words over the beacon, in this player's language, for a moment.</summary>
    internal static void NoTarget(Vector3 floor, float now)
    {
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected) return;
        _noTarget ??= WorldText.Create(null, "ForgeOrdnance.Arsenal.NoTarget", new WorldTextStyle { FontSize = 2.2f, Color = StrikeEffects.LaserColour });
        _noTarget.SetContent(OrdnanceText.T("strike.noTarget"), 8f);
        _noTarget.SetLocalPosition(floor + Vec.Up * 1.6f);
        _noTarget.SetShown(true);
        _noTargetEnds = now + NoTargetSeconds;
    }

    private static void TickNoTarget(float now)
    {
        if (now >= _noTargetEnds || !_noTarget!.Alive) { EndNoTarget(); return; }
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected) return;
        var view = agent.FPSCamera;
        if (view == null) return;
        var camera = view.transform;
        _noTarget.FaceCamera(camera, camera.rotation);
    }

    private static void EndNoTarget()
    {
        _noTarget?.Destroy();
        _noTarget = null;
    }

    /// <summary>Between the game's level-entered event and the end of the level; warming and priming only run then, since both need the player.</summary>
    private static bool _inLevel, _priming;

    internal static void EnterLevel() { _inLevel = true; _priming = true; }
    internal static void LeaveLevel() { _inLevel = false; _priming = false; }

    /// <summary>Each level, once the player stands in it: one strike sound per frame is started silently (see <see cref="ArsenalAudio.Prewarm"/>).</summary>
    private static void PrimeSounds()
    {
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected || agent.FPSCamera == null) return;
        _priming = !ArsenalAudio.Prewarm();
    }

    /// <summary>Level exit, checkpoint reload, host change: the shell trails end (ForgeVisual clears its own sprites and decal marks with
    /// the level; the decal rings stay for the next level).</summary>
    internal static void Clear()
    {
        _priming = _inLevel;
        for (var i = 0; i < TrailSlots; i++) EndTrail(i);
        EndNoTarget();
    }

    /// <summary>The library's trail of a falling shell or bomblet (gas canisters and stasis emitters, which are dropped, not fired, leave only a tracer in their own colour),
    /// starting at <paramref name="from"/>; -1 when there is none. At most <see cref="TrailSlots"/> shells fall at once.</summary>
    internal static int BeginTrail(ShellLook look, StrikeKind kind, Vector3 from, float now)
    {
        if (!Ready()) return -1;
        if (look is ShellLook.Canister or ShellLook.Emitter)
        {
            var style = look == ShellLook.Canister
                ? _canisterTrail ??= StrikeFx.DropTrail(StrikeFx.WarnColour(StrikeKind.Gas))
                : _emitterTrail ??= StrikeFx.DropTrail(StrikeFx.WarnColour(StrikeKind.Stasis));
            for (var i = 0; i < TrailSlots; i++)
            {
                if (Shells[i] is { Alive: true } || LibraryTrails[i] != null) continue;
                Shells[i] = Trails.Rent(from, style);
                return Shells[i] == null ? -1 : i;
            }
            return -1;
        }
        var library = kind == StrikeKind.Hunter ? ArsenalFxSlots.HunterTrail : ArsenalFxSlots.ShellTrail;
        if (library == null) return -1;
        for (var i = 0; i < TrailSlots; i++)
        {
            if (Shells[i] is { Alive: true } || LibraryTrails[i] != null) continue;
            LibraryTrails[i] = library;
            TrailKeys[i] = Fx.NewKey();
            return i;
        }
        return -1;
    }

    /// <summary>Every frame while the shell falls: <paramref name="progress"/> 0 at the top, 1 at the floor (the flame lengthens
    /// toward the floor).</summary>
    internal static void MoveTrail(int slot, Vector3 position, Vector3 heading, float progress, float now)
    {
        if (slot < 0 || slot >= TrailSlots) return;
        if (LibraryTrails[slot] is { } library) { Fx.Hold(library, TrailKeys[slot], position, heading, float.NaN, Color.white); return; }
        Shells[slot]?.Move(position, progress);
    }

    internal static void EndTrail(int slot)
    {
        if (slot < 0 || slot >= TrailSlots) return;
        LibraryTrails[slot] = null;
        Shells[slot]?.End();
        Shells[slot] = null;
    }
    /// <summary>A shell's landing: the big shells (precision, hunter) the big blast; the incendiary shell a splash of fire out to its burn area; a cluster bomblet its small blast;
    /// every other shell the small blast.</summary>
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
                StrikeFx.FireSplash(at, radius);
                break;
            case StrikeKind.Cluster:
                StrikeFx.BombletBlast(at, radius * 0.7f);
                break;
            default:
                StrikeFx.SmallBlast(at, radius);
                break;
        }
    }

    /// <summary>A cluster strike's one scorch and one short light, at its centre; drawn once per call, by its first landing.</summary>
    internal static void ClusterCentre(Vector3 at, float blast, float now)
    {
        if (Ready()) StrikeFx.ClusterCentre(at, blast, now);
    }

    /// <summary>The bomblet splits at <paramref name="burst"/>.</summary>
    internal static void ClusterBurst(Vector3 burst, float now)
    {
        if (Ready()) StrikeFx.ClusterBurst(burst);
    }

    /// <summary>A shell's warning: where it will land and how far it reaches, for the <paramref name="seconds"/> before it drops from <paramref name="from"/>.</summary>
    internal static void Warn(StrikeKind kind, Vector3 at, Vector3 from, float radius, float seconds, float now)
    {
        if (Ready()) StrikeFx.Warn(kind, at, from, radius, seconds);
    }

    /// <summary>The big shells' blow: a column of light up the way they came.</summary>
    internal static void ImpactColumn(Vector3 at, Vector3 from, float radius, float now)
    {
        if (Ready()) StrikeFx.ImpactColumn(at, from, radius);
    }

    internal static void RunHit(Vector3 at, Vector3 direction, Color colour, float now)
    {
        if (Ready()) StrikeFx.RunHit(at, direction, colour);
    }

    internal static void StasisBeat(Vector3 centre, float radius, float now)
    {
        if (Ready()) StrikeFx.StasisBeat(centre, radius);
    }

    internal static void GasBurst(Vector3 at, float radius, float now)
    {
        if (Ready()) StrikeFx.GasBurst(at, radius);
    }

    internal static void StasisPulse(Vector3 centre, float radius, float now)
    {
        if (Ready()) StrikeFx.StasisPulse(centre, radius, now);
    }

    internal static void BeamCharge(Vector3 at, float now)
    {
        if (Ready()) StrikeFx.BeamCharge(at);
    }

    internal static void BeamMark(Vector3 at, Color colour, float now)
    {
        if (Ready()) StrikeFx.BeamMark(at, colour);
    }

    /// <summary>The returned heavy weapon's shimmer, in front of the local player's camera where the gun is held (low and to the
    /// side, small and faint: it never covers the view).</summary>
    internal static void ReturnGlow(PlayerAgent agent, float now)
    {
        var view = agent.FPSCamera;
        if (view == null || !Ready()) return;
        var camera = view.transform;
        StrikeFx.ReturnGlow(camera.position + camera.forward * 0.55f + camera.right * 0.14f - camera.up * 0.14f);
    }
}
