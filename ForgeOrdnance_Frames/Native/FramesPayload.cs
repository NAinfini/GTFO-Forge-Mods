using System.Text.Json;
using ForgeOrdnance;

namespace ForgeOrdnanceFrames;

/// <summary>Payload type <c>Exosuit</c>, registered with ForgeOrdnance's public API. The call arrives as the suit itself: it drops
/// through the ceiling (no collision), lands and stands empty for anyone to board; a direct arrival skips the fall. The host owns the suit's record and sends
/// every change as kept per-call state (<see cref="OrdnanceApi.SendState"/>, replayed to late joiners); requests and gunfire travel on this plugin's own events.</summary>
internal sealed class FramesPayload : IOrdnancePayload
{
    public object Parse(JsonElement payload)
    {
        var spec = FrameSpecParser.Parse(payload);
        FrameAssets.RequireArt(spec);
        FrameWorld.Known(spec);
        return spec;
    }

    /// <summary>The suit plays its own drop and impact shake.</summary>
    public bool HasOwnArrival => true;

    /// <summary>A direct arrival stands the suit empty at the landing point at once, ready to board, with no fall.</summary>
    public bool SupportsDirect => true;

    public void Execute(OrdnanceExecution execution) => FrameWorld.HostArrive(execution);

    /// <summary>The drop is counting down: the pilot's first-person shell of this model is built now if the elevator ride did not.</summary>
    public void OnBeaconLanded(OrdnancePresentation presentation)
    {
        if (presentation.Parameters is FrameSpec spec) LocalPilot.Called(spec);
    }

    public void OnArrived(OrdnancePresentation presentation) => FrameWorld.Arrived(presentation);

    public void OnData(OrdnancePayloadData data) => FrameWorld.OnData(data);
}
