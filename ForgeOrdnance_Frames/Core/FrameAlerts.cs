namespace ForgeOrdnanceFrames;

/// <summary>A moment worth the big text in the middle of the screen. Only full-attention moments are: the suit about to be destroyed
/// here, the destruction and the forced eject from the leave path. Everything else (planting, overheat, a broken barrier, low armour,
/// refused requests) shows on the HUD element it belongs to.</summary>
internal enum FrameAlert : byte { None, Failing }

/// <summary>Turns the worn suit's state into banners: armour falling under <see cref="FailingArmor"/> of its maximum, once each time it
/// does (never on the first look at a suit, so boarding a wrecked suit announces nothing). Pure, allocation-free.</summary>
internal sealed class FrameAlerts
{
    public const float FailingArmor = 0.1f;

    private bool _seen, _failing;

    /// <summary>Forget the suit; the next <see cref="Next"/> only takes its state in.</summary>
    public void Reset() => _seen = false;

    public static FrameAlertTone Tone(FrameAlert alert) => FrameAlertTone.Alert;

    public static string Text(FrameAlert alert, FrameWords words) => alert == FrameAlert.Failing ? words.Failing : "";

    public static float Seconds(FrameAlert alert) => 2f;

    public FrameAlert Next(FrameRecord r)
    {
        var spec = r.Spec;
        var failing = spec.Armor > 0f && r.Armor / spec.Armor < FailingArmor;
        var was = _failing;
        _failing = failing;
        if (!_seen) { _seen = true; return FrameAlert.None; }
        return failing && !was ? FrameAlert.Failing : FrameAlert.None;
    }
}

/// <summary>How a banner is coloured; the native side maps it onto ForgeUI's notice tones.</summary>
internal enum FrameAlertTone : byte { Info, Good, Warn, Alert }
