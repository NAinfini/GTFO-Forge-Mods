using System.Collections.Generic;

namespace ForgeOrdnance;

/// <summary>How a call arrives. <see cref="Drop"/> plays the call's vehicle first (the drop pod, the turret shell, an exosuit's own
/// fall) and hands over the contents when it opens; <see cref="Direct"/> has no vehicle: the contents are there at once. Player
/// call-ins always drop; a level's developer drops choose (<c>Arrival</c>).</summary>
public enum OrdnanceArrivalMode : byte
{
    Drop = 0,
    Direct = 1
}

/// <summary>The two stages of an arrival: the vehicle (fall, touchdown, opening) and the contents (pickups placed, sentry stood,
/// suit ready).</summary>
internal enum ArrivalStage
{
    Vehicle,
    Contents
}

/// <summary>Which stages an arrival plays, and when its contents stage starts. Every payload asks here, so a direct arrival is the
/// same arrival with the vehicle stage left out, not a second path.</summary>
internal static class ArrivalPlan
{
    private static readonly ArrivalStage[] Dropped = { ArrivalStage.Vehicle, ArrivalStage.Contents };
    private static readonly ArrivalStage[] Placed = { ArrivalStage.Contents };

    internal static IReadOnlyList<ArrivalStage> Stages(OrdnanceArrivalMode mode) => mode == OrdnanceArrivalMode.Direct ? Placed : Dropped;

    internal static bool HasVehicle(OrdnanceArrivalMode mode) => mode != OrdnanceArrivalMode.Direct;

    /// <summary>Seconds from the arrival until the contents stage: <paramref name="vehicleSeconds"/> (the vehicle's own time until it
    /// opens) for a drop, none for a direct arrival.</summary>
    internal static float ContentsAfter(OrdnanceArrivalMode mode, float vehicleSeconds) => HasVehicle(mode) ? vehicleSeconds : 0f;

    internal static bool TryParse(string? text, out OrdnanceArrivalMode mode)
    {
        switch (text)
        {
            case "Drop": mode = OrdnanceArrivalMode.Drop; return true;
            case "Direct": mode = OrdnanceArrivalMode.Direct; return true;
            default: mode = OrdnanceArrivalMode.Drop; return false;
        }
    }
}
