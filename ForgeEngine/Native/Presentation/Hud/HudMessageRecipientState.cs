using System;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEngine.Presentation.Native;

/// <summary>The presentation provider's contribution to Runtime's recipient state: the `hud_message` lines each
/// player account was shown. A line is shown again only for the part of its lifetime that is still left.</summary>
internal sealed class HudMessageRecipientState : RecipientLedger
{
    internal const string StateKey = "forge.presentation.recipient-hud-message";
    private const string Surface = "hud";

    private readonly HudMessageActions _actions;
    private readonly Action<string> _report;

    internal HudMessageRecipientState(IForgeEngine kernel, IPlayerDirectory players, HudMessageActions actions,
        Action<string> report) : base(kernel, players, StateKey)
    {
        _actions = actions; _report = report;
    }

    protected override bool Handles(PresentationOutput request)
        => request.Provider == PresentationContract.ProviderId && request.BindingId == PresentationContract.HudMessageBindingId;

    protected override (HandlerSlot Audience, string Surface) Locate(PresentationOutput request, CommandContext context)
        => (PresentationContract.HudMessageViewers, Surface);

    protected override (long StartTick, double Seconds) Lifetime(PresentationOutput request)
        => (request.SimulationTick, ForgeUI.ScreenMessage.DefaultSeconds);

    /// <summary>Seconds of a line still to show: its declared lifetime minus the host ticks since it was issued.
    /// It never exceeds the declared lifetime, so a recovery cannot lengthen a line.</summary>
    private double RemainingSeconds(Saved saved)
        => saved.Seconds - Math.Max(0, Kernel.CurrentTick - saved.StartTick) / Kernel.TicksPerSecond;

    // A line past its lifetime shows nothing, so it only occupies room.
    protected override bool Expired(Saved saved) => saved.Seconds > 0 && RemainingSeconds(saved) <= 0;

    protected override bool Valid(Saved saved)
        => saved.Surface == Surface && saved.Seconds == ForgeUI.ScreenMessage.DefaultSeconds && saved.StartTick >= 0;

    protected override (bool Applied, string Code) Apply(Saved saved)
    {
        float remaining = (float)Math.Min(RemainingSeconds(saved), ForgeUI.ScreenMessage.DefaultSeconds);
        if (remaining <= 0f) return (true, "");
        return ShowToLocal(saved, context => _actions.ShowRemaining(context, remaining));
    }

    /// <summary>One recovery step that stays pending names the inputs it was restoring (clipped), so a text that
    /// cannot be rendered says which text block it was. Reported once per reason until the local account changes.</summary>
    protected override void Pending(string surface, string code, Saved saved)
    {
        if (!Once.ShouldReport(surface + ":pending:" + code)) return;
        var inputs = saved.Inputs.GetRawText();
        try
        {
            _report("presentation.hud-message-recovery: surface=" + surface + "; reason=" + code
                + "; inputs=" + (inputs.Length <= 200 ? inputs : inputs[..200]));
        }
        catch (Exception reportError) { PresentationLog.Contained("hud-message.recovery-report", reportError); }
    }
}
