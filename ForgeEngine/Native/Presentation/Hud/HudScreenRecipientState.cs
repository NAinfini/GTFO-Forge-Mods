using System;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEngine.Presentation.Native;

/// <summary>The presentation provider's contribution to Runtime's recipient state: the `screen` elements of `hud_value` each
/// player account was shown, one line per element name, so an account that comes back gets them again with their value carried
/// forward by its rate. A hide is a line too: it replaces the showing.</summary>
internal sealed class HudScreenRecipientState : RecipientLedger
{
    internal const string StateKey = "forge.presentation.recipient-hud-screen";
    private const string SurfacePrefix = "screen:";

    private readonly HudValueActions _actions;
    private readonly Action<string> _report;

    internal HudScreenRecipientState(IForgeEngine kernel, IPlayerDirectory players, HudValueActions actions,
        Action<string> report) : base(kernel, players, StateKey)
    {
        _actions = actions; _report = report;
    }

    protected override bool Handles(PresentationOutput request)
        => request.Provider == PresentationContract.ProviderId && request.BindingId == PresentationContract.HudValueBindingId;

    protected override bool Keeps(CommandContext context) => context.Text(PresentationContract.HudValuePlacement) == "screen";

    protected override (HandlerSlot Audience, string Surface) Locate(PresentationOutput request, CommandContext context)
        => (PresentationContract.HudValueViewers,
            SurfacePrefix + (context.Text(PresentationContract.HudValueElement) is { Length: > 0 } named ? named : context.NodeId));

    protected override (long StartTick, double Seconds) Lifetime(PresentationOutput request) => (request.SimulationTick, 0);

    protected override bool Valid(Saved saved) => saved.Surface.StartsWith(SurfacePrefix, StringComparison.Ordinal) && saved.StartTick >= 0;

    protected override (bool Applied, string Code) Apply(Saved saved)
    {
        double elapsed = Math.Max(0, Kernel.CurrentTick - saved.StartTick) / Kernel.TicksPerSecond;
        return ShowToLocal(saved, context => _actions.ShowRecovered(context, elapsed));
    }

    protected override void Pending(string surface, string code, Saved saved)
    {
        if (!Once.ShouldReport(surface + ":pending:" + code)) return;
        try { _report("presentation.hud-screen-recovery: surface=" + surface + "; reason=" + code); }
        catch (Exception reportError) { PresentationLog.Contained("hud-value.recovery-report", reportError); }
    }
}
