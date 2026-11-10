using System;
using ForgeVisual;

namespace ForgeOrdnanceFrames;

/// <summary>The suit moments whose effect comes from the effect library (the bundle <c>forge-frames-fx.bundle</c> and its slot table
/// <c>fx-slots.forge</c> next to the plugin, made by <c>Tools/Vfx/FxLibrary/fx_slots.py</c> from the owner's selection). Which prefab plays
/// for a slot is data in that table, not code here; a slot the table does not list, or an install without the files, logs its error and draws
/// nothing. Playing is ForgeVisual's <see cref="Fx"/>. The moments in a suit's light colour (the landing and
/// kick rings, the barrier's pulse, hit and break, the siege ring, the ram) are resolved here once and played with that suit's <c>Light.Glow</c>: their layers take the hue of the colour passed (<c>@tint</c>).</summary>
internal static class FramesFxSlots
{
    internal static FxHandle? Explosion, Pulse, ShieldHit, ShieldBreak, Siege, Ram,
        Kick, Landing, ChargeDust, Backblast, Wreck, WreckSmoke, Eject, Vent, Heat, RocketMuzzle, GunMuzzle;

    /// <summary>The loaded set; <see cref="FxSlots.Empty"/> when the bundle is not shipped.</summary>
    internal static FxSlots Set = FxSlots.Empty;

    internal static void Load()
    {
        try
        {
            var set = Set = FxSlots.Load(Plugin.Id, "FramesLib", "forge-frames-fx.bundle", "fx-slots.forge");
            Explosion = set.Find("frame.explode");
            Pulse = set.Find("frame.pulse");
            ShieldHit = set.Find("frame.shield.hit");
            ShieldBreak = set.Find("frame.shield.break");
            Siege = set.Find("frame.siege");
            Ram = set.Find("sirius.ram");
            Kick = set.Find("frame.kick"); Landing = set.Find("frame.landing"); Backblast = set.Find("frame.backblast");
            Wreck = set.Find("frame.wreck"); WreckSmoke = set.Find("frame.wreck.smoke"); ChargeDust = set.Find("frame.charge.dust"); Eject = set.Find("frame.eject"); Vent = set.Find("frame.vent"); Heat = set.Find("frame.heat");
            RocketMuzzle = set.Find("frame.rocket.muzzle"); GunMuzzle = set.Find("frame.gun.muzzle");
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("frames-effects-prepare-failed", "library effects could not be prepared, they draw nothing: " + error); }
    }
}
