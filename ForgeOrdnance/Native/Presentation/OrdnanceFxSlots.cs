using ForgeVisual;

namespace ForgeOrdnance;

/// <summary>The moments whose effect comes from the effect library (the bundle <c>forge-ordnance-fx.bundle</c> and its slot table
/// <c>fx-slots.forge</c> next to the plugin, made by <c>Tools/Vfx/FxLibrary/fx_slots.py</c> from the owner's selection). Which prefab plays
/// for a slot is data in that table, not code here; a slot the table does not list, or an install without the files, logs its error
/// and draws nothing. Playing is ForgeVisual's <see cref="Fx"/>.</summary>
internal static class OrdnanceFxSlots
{
    internal static FxHandle? PodLeave, BeaconImpact, SentryOnline, SentryExpired, Pillar, Ribbon, PodFall, PodGravel, PodLand, PodGlow, PodVent, ServoSparks, ShellUnfold, TurretRise, HandPulse;

    /// <summary>The loaded set; <see cref="FxSlots.Empty"/> when the bundle is not shipped.</summary>
    internal static FxSlots Set = FxSlots.Empty;

    internal static void Load()
    {
        var set = Set = FxSlots.Load(Plugin.Id, "OrdnanceFx", "forge-ordnance-fx.bundle", "fx-slots.forge");
        Pillar = set.Find("beacon.pillar");
        Ribbon = set.Find("beacon.ribbon");
        PodLeave = set.Find("pod.leave");
        BeaconImpact = set.Find("beacon.impact");
        SentryOnline = set.Find("sentry.online");
        SentryExpired = set.Find("sentry.expired");
        PodFall = set.Find("pod.fall"); PodGravel = set.Find("pod.gravel"); PodLand = set.Find("pod.land"); PodGlow = set.Find("pod.glow");
        PodVent = set.Find("pod.vent"); ServoSparks = set.Find("mech.servo.sparks"); ShellUnfold = set.Find("shell.unfold");
        TurretRise = set.Find("turret.rise"); HandPulse = set.Find("hand.pulse");
    }
}
