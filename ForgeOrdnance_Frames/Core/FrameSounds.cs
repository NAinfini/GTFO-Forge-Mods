namespace ForgeOrdnanceFrames;

/// <summary>Every sound this package plays through ForgeAudio. The order is the index into <see cref="FrameSounds.Table"/>.
/// Each cue's settings, including its minimum gap between plays, are in <c>audio/cues.json</c>. The suit's arrival (fall, touchdown,
/// rubble) uses ForgeOrdnance's own drop sounds, and <see cref="Landing"/> lays the heavy machine's weight over the touchdown once.</summary>
public enum FrameSound
{
    Footstep, Boot, VoiceReady, Servo, Alarm, Destroyed, Boost, ShieldUp, ShieldHit, ShieldDown, Rocket,
    PlateSlide, HatchClose, HudSection, Unseal, PowerDown, Jump, ReloadStart, ReloadDone, DryFire,
    BoostEnd, SiegeOn, SiegeOff, BreachHit, ShoveHit, ArmorHit, HoldComplete, HoldCancel, Land, RocketBlast, SiegeBolt, Landing, SiegeDeploy,
}

/// <summary>The cue name of each <see cref="FrameSound"/>.</summary>
public static class FrameSounds
{
    public readonly record struct Entry(FrameSound Sound, string Cue);

    public static readonly Entry[] Table =
    {
        new(FrameSound.Footstep, "exo.footstep"), new(FrameSound.Boot, "exo.visor_boot"),
        new(FrameSound.VoiceReady, "exo.voice_ready"), new(FrameSound.Servo, "exo.servo_loop"), new(FrameSound.Alarm, "exo.damage_alarm"),
        new(FrameSound.Destroyed, "exo.destroyed"), new(FrameSound.Boost, "exo.boost"), new(FrameSound.ShieldUp, "exo.shield_up"),
        new(FrameSound.ShieldHit, "exo.shield_hit"), new(FrameSound.ShieldDown, "exo.shield_down"), new(FrameSound.Rocket, "exo.rocket"),
        new(FrameSound.PlateSlide, "exo.plate_slide"), new(FrameSound.HatchClose, "exo.hatch_close"),
        new(FrameSound.HudSection, "exo.hud_section"), new(FrameSound.Unseal, "exo.unseal"),
        new(FrameSound.PowerDown, "exo.power_down"), new(FrameSound.Jump, "exo.jump"), new(FrameSound.ReloadStart, "exo.reload_start"),
        new(FrameSound.ReloadDone, "exo.reload_done"), new(FrameSound.DryFire, "exo.dry_fire"),
        new(FrameSound.BoostEnd, "exo.boost_end"), new(FrameSound.SiegeOn, "exo.siege_on"), new(FrameSound.SiegeOff, "exo.siege_off"),
        new(FrameSound.BreachHit, "exo.breach_hit"), new(FrameSound.ShoveHit, "exo.shove_hit"), new(FrameSound.ArmorHit, "exo.armor_hit"),
        new(FrameSound.HoldComplete, "ui.hold_complete"), new(FrameSound.HoldCancel, "ui.hold_cancel"), new(FrameSound.Land, "exo.land"),
        new(FrameSound.RocketBlast, "exo.rocket_blast"), new(FrameSound.SiegeBolt, "exo.siege_bolt"), new(FrameSound.Landing, "exo.landing"), new(FrameSound.SiegeDeploy, "exo.siege_deploy"),
    };

    /// <summary>The siege sound for the state the suit just entered: deploying plays the deploy sound, leaving it the release.</summary>
    public static FrameSound Siege(bool sieged) => sieged ? FrameSound.SiegeOn : FrameSound.SiegeOff;
}
