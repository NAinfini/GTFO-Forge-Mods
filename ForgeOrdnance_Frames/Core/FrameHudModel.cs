using System;

namespace ForgeOrdnanceFrames;

/// <summary>The suit gun as the pilot's game holds it this frame (the lent special-slot weapon); <see cref="ReloadProgress"/> is how far
/// the running reload is, 0..1, or -1 while none runs.</summary>
internal readonly record struct FrameGunReading(bool Lent, int Clip, int Reserve, bool Reloading, float ReloadProgress = -1f);

/// <summary>Which special the suit has, for its skill slot.</summary>
internal enum FrameSpecialKind : byte { None, Charge, Siege, Scan }

/// <summary>How the barrier layer over the armour bar reads: none (no barrier), standing, stowed (a planted-only barrier while the suit
/// walks: the layer and its number are not drawn) or broken.</summary>
internal enum BarrierState : byte { None, Up, Standby, Broken }

/// <summary>
/// What the cockpit shows, computed each frame from the suit's record, the pilot's sprint heat and clocks; ForgeUI's elements read
/// these values (bars, skill slots, arcs). The armour bar eases, rolls up, numbers and flashes itself (ForgeUI's segmented bar with
/// <see cref="Segments"/>, red under <see cref="LowArmor"/> with a flash per segment lost, flashing without pause under
/// <see cref="FrameAlerts.FailingArmor"/>); the model only gives the real share.
/// </summary>
internal sealed class FrameHudModel
{
    /// <summary>Segments of the armour bar.</summary>
    public const int Segments = 10;
    /// <summary>The armour bar turns red under this share.</summary>
    public const float LowArmor = 0.25f;
    /// <summary>The armour bar is amber under this share.</summary>
    public const float WarnArmor = 0.4f;
    /// <summary>How fast the armour bar follows a hit (per second, exponential).</summary>
    public const float ArmorEase = 7f;
    /// <summary>The scout's scan lights its slot this long each time it runs (it has no cooldown to show).</summary>
    public const float ScanLit = 0.4f;

    public FrameWords Words { get; set; }

    public FrameHudModel(FrameWords words) => Words = words;

    // ---- armour, barrier ---------------------------------------------------------------------------------------

    public float ArmorFraction { get; private set; }

    public BarrierState Barrier { get; private set; }
    /// <summary>The barrier's charge, 0..1; 0 while broken (a broken barrier stops nothing, even as it refills).</summary>
    public float BarrierFraction { get; private set; }
    /// <summary>The barrier layer and its number are drawn: a barrier that stands now or is broken while it would stand.</summary>
    public bool BarrierShown => Barrier is BarrierState.Up or BarrierState.Broken;
    /// <summary>The barrier's points as its number reads them (the charge rounded up).</summary>
    public int BarrierPoints { get; private set; }
    /// <summary>A broken barrier's way back: the wait without a hit fills the first half, the refill the second; -1 while it stands.</summary>
    public float BarrierRecovery { get; private set; } = -1f;

    // ---- heat, gun ---------------------------------------------------------------------------------------------

    public float Heat { get; private set; }
    public bool Overheated { get; private set; }
    public int Clip { get; private set; }
    public int Reserve { get; private set; }
    /// <summary>All the rounds the suit gun starts with and can never exceed (magazine and reserve of its spec), for the share of rounds left.</summary>
    public int StockCapacity { get; private set; }
    /// <summary>The suit gun's running reload, 0..1; -1 while none runs.</summary>
    public float ReloadProgress { get; private set; } = -1f;

    // ---- special, rockets --------------------------------------------------------------------------------------

    public FrameSpecialKind Special { get; private set; }
    /// <summary>The special's name (CHARGE, SIEGE, SCAN).</summary>
    public string SpecialName { get; private set; } = "";
    /// <summary>Seconds until the charge can be used again (0 ready); always 0 for siege and scan, which have no cooldown to show.</summary>
    public float SpecialRemaining { get; private set; }
    public float SpecialCooldown { get; private set; }
    /// <summary>The special is on: charging, planted, or the scan has just run.</summary>
    public bool SpecialActive { get; private set; }
    /// <summary>Planting: how far the deploy is, 0..1; -1 otherwise.</summary>
    public float SpecialProgress { get; private set; } = -1f;

    public bool HasRockets { get; private set; }
    public float RocketRemaining { get; private set; }
    public float RocketCooldown { get; private set; }

    public void Update(FrameRecord r, FrameGunReading gun, float heat, bool overheated, float now, float lastScanAt, float rocketsReadyAt)
    {
        var spec = r.Spec;
        var words = Words;

        // Armour.
        ArmorFraction = spec.Armor > 0f ? Math.Clamp(r.Armor / spec.Armor, 0f, 1f) : 0f;

        // Barrier.
        var shieldSpec = spec.Shield;
        var look = shieldSpec == null ? BarrierState.None
            : shieldSpec.WhileSieged && !r.Sieged ? BarrierState.Standby
            : !r.ShieldUp ? BarrierState.Broken
            : BarrierState.Up;
        Barrier = look;
        BarrierFraction = shieldSpec != null && r.ShieldUp ? Math.Clamp(r.Shield / Math.Max(1e-3f, shieldSpec.Capacity), 0f, 1f) : 0f;
        BarrierPoints = (int)MathF.Ceiling(BarrierFraction * (shieldSpec?.Capacity ?? 0f) - 1e-3f);
        BarrierRecovery = -1f;
        if (look == BarrierState.Broken)
        {
            var waited = now - r.ShieldLastHit;
            BarrierRecovery = waited < shieldSpec!.RegenDelay
                ? 0.5f * (shieldSpec.RegenDelay > 0f ? Math.Clamp(waited / shieldSpec.RegenDelay, 0f, 1f) : 1f)
                : 0.5f + 0.5f * Math.Clamp(r.Shield / Math.Max(1e-3f, shieldSpec.Capacity * shieldSpec.RaiseFraction), 0f, 1f);
        }

        // Heat and gun.
        Heat = Math.Clamp(heat, 0f, 1f);
        Overheated = overheated;
        Clip = gun.Lent ? gun.Clip : r.Magazine;
        Reserve = gun.Lent ? gun.Reserve : r.Reserve;
        StockCapacity = spec.Weapon.Magazine + spec.Weapon.Reserve;
        ReloadProgress = gun.Lent && gun.Reloading ? gun.ReloadProgress : -1f;

        // Special.
        SpecialProgress = -1f;
        SpecialRemaining = 0f;
        SpecialCooldown = 0f;
        if (spec.Boost != null)
        {
            Special = FrameSpecialKind.Charge;
            SpecialName = words.Charge;
            SpecialActive = r.Boosting(now);
            SpecialCooldown = spec.Boost.Cooldown;
            SpecialRemaining = SpecialActive ? 0f : Math.Max(0f, r.BoostReadyAt - now);
        }
        else if (spec.Siege != null)
        {
            Special = FrameSpecialKind.Siege;
            SpecialName = words.Siege;
            SpecialActive = r.Sieged;
            if (r.SiegeDeployEndsAt > 0f)
                SpecialProgress = spec.Siege.DeploySeconds > 0f ? Math.Clamp(1f - (r.SiegeDeployEndsAt - now) / spec.Siege.DeploySeconds, 0f, 1f) : 1f;
        }
        else if (spec.Scan != null)
        {
            Special = FrameSpecialKind.Scan;
            SpecialName = words.Scan;
            var since = now - lastScanAt;
            SpecialActive = since >= 0f && since < ScanLit;
        }
        else
        {
            Special = FrameSpecialKind.None;
            SpecialName = "";
            SpecialActive = false;
        }

        // Rockets.
        HasRockets = spec.Rockets != null;
        RocketCooldown = spec.Rockets?.Cooldown ?? 0f;
        RocketRemaining = HasRockets ? Math.Max(0f, rocketsReadyAt - now) : 0f;
    }
}
