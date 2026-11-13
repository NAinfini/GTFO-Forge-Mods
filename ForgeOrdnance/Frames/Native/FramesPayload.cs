using System.Collections.Generic;
using System.Text.Json;

namespace ForgeOrdnance.Frames;

/// <summary>Payload type <c>Exosuit</c>, one of the carrier's built-in types. The call arrives as the suit itself: it drops
/// through the ceiling (no collision), lands and stands empty for anyone to board; a direct arrival skips the fall. The host owns the suit's record and sends
/// every change as kept per-call state (<see cref="OrdnanceApi.SendState(RegisteredPayload, ulong, uint, byte, byte[])"/>, replayed to late joiners); requests and gunfire travel on the suits' own events.</summary>
internal sealed class FramesPayload : IOrdnancePayload
{
    /// <summary>The registered type, which the suits' states are sent with; set by the plugin at load.</summary>
    internal static RegisteredPayload Type = null!;

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

    private static readonly OrdnanceOverrideField[] Fields =
    {
        new("Armor", 1, 100000), new("Magazine", 1, 10000, true), new("Reserve", 0, 100000, true)
    };

    /// <summary>A placement may set how the suit stands: <c>Armor</c> left, rounds <c>Magazine</c> loaded and <c>Reserve</c> rounds in the suit.</summary>
    public IReadOnlyList<OrdnanceOverrideField> OverrideFields => Fields;

    public string? CheckOverrides(object parameters, IReadOnlyDictionary<string, double> overrides)
    {
        var spec = (FrameSpec)parameters;
        if (overrides.TryGetValue("Armor", out var armor) && armor > spec.Armor) return "Armor: at most the model's own Armor (" + spec.Armor + ")";
        if (overrides.TryGetValue("Magazine", out var magazine) && magazine > spec.Weapon.Magazine) return "Magazine: at most the model's own Magazine (" + spec.Weapon.Magazine + ")";
        return null;
    }

    public void Execute(OrdnanceExecution execution) => FrameWorld.HostArrive(execution);

    /// <summary>The drop is counting down: the pilot's first-person shell of this model is built now if the elevator ride did not.</summary>
    public void OnBeaconLanded(OrdnancePresentation presentation)
    {
        if (presentation.Parameters is FrameSpec spec) LocalPilot.Called(spec);
    }

    public void OnArrived(OrdnancePresentation presentation) => FrameWorld.Arrived(presentation);

    public void OnData(OrdnancePayloadData data) => FrameWorld.OnData(data);

    public byte[] RefreshState(ulong summonerLookup, uint throwId, byte tag, byte[] data) => FrameWorld.RefreshState(summonerLookup, throwId, tag, data);
}
