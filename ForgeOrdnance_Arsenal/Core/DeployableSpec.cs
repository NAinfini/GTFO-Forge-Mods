using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeCore;
using ForgeOrdnance;

namespace ForgeOrdnanceArsenal;

/// <summary>The standing devices of payload type <see cref="DeployableSpec.TypeName"/>, chosen by the payload's <c>Kind</c>. Each arrives in the
/// turret shell (the base) with the device (the upper part) inside it: the base bores in, throws its lids off and the device rises out of it, and works until its time runs out.</summary>
internal enum DeployableKind : byte
{
    /// <summary>Rises, opens its plates and spins, throwing munitions out of the holes in them; each lies where it lands and, when an enemy comes
    /// close, bursts into an explosion, a spray of C-foam in every direction, or both (what each munition does is data). Then it lowers, takes on
    /// the next round and does it again, <see cref="DeployableSpec.Rounds"/> times, and sinks away; the munitions keep lying.</summary>
    Dispenser = 1,
    /// <summary>Tags every enemy in range for the whole team, again whenever a tag runs out.</summary>
    Scanner = 3,
    /// <summary>Lights a wide area and pushes the fog back.</summary>
    Floodlight = 4,
}

/// <summary>Validated parameters of one standing device. Fields a kind does not use keep their default and are rejected in the file.</summary>
internal sealed class DeployableSpec
{
    public const string TypeName = "arsenal.Deployable";
    /// <summary>The most munitions one dispenser lays in all (what fits one field record, <see cref="MineWire"/>).</summary>
    public const int MaxMines = 36;
    /// <summary>Seconds a dispenser's column stands still after its spin, before the plates shut.</summary>
    public const float StopSeconds = 0.35f;

    public DeployableKind Kind { get; private set; }
    /// <summary>The ForgeVisual model of the device's head, <c>Pack:item</c>. Every device and every turret shares one delivery shell and base (the turret shell); only the head differs, so swapping this changes only the look.</summary>
    public string Head { get; private set; } = "";
    public float Scale { get; private set; }
    /// <summary>Seconds the device works after it has risen.</summary>
    public float Lifetime { get; private set; }
    /// <summary>How the shell bores in, opens and the device rises and sinks again (the <see cref="BurrowSpec.Fields"/> in the definition: <c>BurrowSeconds</c>,
    /// <c>HoldSeconds</c>, the <c>Lid...</c> numbers, <c>RiseHeight</c>, <c>RiseSeconds</c>, <c>LeaveSeconds</c>); a device has no gun, so it has no turning down.</summary>
    public BurrowSpec Burrow { get; private set; } = new();

    /// <summary>Dispenser: munitions thrown per round, rounds, and the metres from the device they land. The device stands for the rounds it
    /// works and sinks; <see cref="Lifetime"/> is then how long the laid munitions last after it has finished.</summary>
    public int Count { get; private set; }
    public int Rounds { get; private set; }
    public float MinRange { get; private set; }
    public float MaxRange { get; private set; }
    /// <summary>Dispenser: the munition's own model, <c>Pack:item</c> (empty: the strikes' bomblet), and the seconds one takes to fly to where it lands.</summary>
    public string Munition { get; private set; } = "";
    public float FlightSeconds { get; private set; }
    /// <summary>Dispenser: the plates of its model, and the size of a munition sitting in a hole against the size it lands at (it grows in flight).</summary>
    public int Plates { get; private set; }
    public float SeatScale { get; private set; }
    /// <summary>Dispenser: seconds the plates take to open (and to close), seconds the column spins throwing, whole turns it makes, and seconds
    /// it takes to lower, load and rise again between rounds.</summary>
    public float OpenSeconds { get; private set; }
    public float SpinSeconds { get; private set; }
    public int SpinTurns { get; private set; }
    public float ReloadSeconds { get; private set; }

    /// <summary>Dispenser, what a munition does when an enemy comes within <see cref="TriggerRadius"/> metres once it has lain
    /// <see cref="ArmSeconds"/>: an explosion (<see cref="Damage"/> over <see cref="BlastRadius"/> metres, pushing with <see cref="Force"/>; 0 damage
    /// is none) and/or <see cref="Spray"/> pellets of C-foam thrown in every direction at about <see cref="SpraySpeed"/> metres a second (0 is none).</summary>
    public float Damage { get; private set; }
    public float BlastRadius { get; private set; }
    public float Force { get; private set; }
    public float TriggerRadius { get; private set; }
    public float ArmSeconds { get; private set; }
    public int Spray { get; private set; }
    public float SpraySpeed { get; private set; }

    /// <summary>Scanner: metres around the device in which enemies are tagged, and the seconds between its looks.</summary>
    public float Range { get; private set; }
    public float Scan { get; private set; }
    /// <summary>Scanner: seconds its dish takes to unfold once it has risen (it looks only then) and to fold again before it sinks.</summary>
    public float UnfoldSeconds { get; private set; }

    /// <summary>Floodlight: reach of the light, metres of fog pushed back, and the light's intensity.</summary>
    public float LightRadius { get; private set; }
    public float FogRadius { get; private set; }
    public float Intensity { get; private set; }

    private static readonly string[] Common = new[] { "Head", "Scale", "Lifetime" }.Concat(BurrowSpec.Fields).ToArray();
    private static readonly string[] KindFields = { "Type", "Kind" };

    private static readonly Dictionary<DeployableKind, string[]> Own = new()
    {
        [DeployableKind.Dispenser] = new[]
        {
            "Count", "Rounds", "MinRange", "MaxRange", "Munition", "FlightSeconds", "Plates", "SeatScale", "OpenSeconds", "SpinSeconds", "SpinTurns", "ReloadSeconds",
            "Damage", "BlastRadius", "Force", "TriggerRadius", "ArmSeconds", "Spray", "SpraySpeed",
        },
        [DeployableKind.Scanner] = new[] { "Range", "Scan", "UnfoldSeconds" },
        [DeployableKind.Floodlight] = new[] { "LightRadius", "FogRadius", "Intensity" },
    };

    /// <summary>Parses the payload object (it also holds <c>Type</c>). Throws <see cref="StrictJsonException"/>.</summary>
    public static DeployableSpec Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new StrictJsonException("Payload", "must be an object.");
        var kind = StrictJson.Enum<DeployableKind>(payload, "", "Kind");
        var known = new List<string>(Common);
        known.AddRange(Own[kind]);
        StrictJson.OnlyKnown(payload, "", known.ToArray(), KindFields, kind.ToString());
        if (!payload.TryGetProperty("Head", out var model) || model.ValueKind != JsonValueKind.String || !HeavyModels.IsId(model.GetString()))
            throw new StrictJsonException("Head", "is required: a ForgeVisual model id written Pack:item.");
        var spec = new DeployableSpec
        {
            Kind = kind,
            Head = model.GetString()!,
            Scale = (float)StrictJson.Number(payload, "", "Scale", 1, 0.3, 4),
            Lifetime = (float)StrictJson.Number(payload, "", "Lifetime", 120, 10, 900),
            // A device has no gun to turn down; it rises a little slower than a turret.
            Burrow = BurrowSpec.Parse(payload, new BurrowSpec { RiseSeconds = 1.5f }),
        };
        switch (kind)
        {
            case DeployableKind.Dispenser:
                spec.Count = (int)StrictJson.Integer(payload, "", "Count", 6, 1, MaxMines);
                spec.Rounds = (int)StrictJson.Integer(payload, "", "Rounds", 2, 1, MaxMines);
                if (spec.Count * spec.Rounds > MaxMines) throw new StrictJsonException("Rounds", "times Count must not exceed " + MaxMines + ".");
                spec.MinRange = (float)StrictJson.Number(payload, "", "MinRange", 3, 0, 30);
                spec.MaxRange = (float)StrictJson.Number(payload, "", "MaxRange", 11, 1, 40);
                if (payload.TryGetProperty("Munition", out var munition))
                {
                    if (munition.ValueKind != JsonValueKind.String || !HeavyModels.IsId(munition.GetString()))
                        throw new StrictJsonException("Munition", "must be a ForgeVisual model id written Pack:item.");
                    spec.Munition = munition.GetString()!;
                }
                spec.FlightSeconds = (float)StrictJson.Number(payload, "", "FlightSeconds", 0.8, 0.2, 3);
                spec.Plates = (int)StrictJson.Integer(payload, "", "Plates", 6, 1, 12);
                spec.SeatScale = (float)StrictJson.Number(payload, "", "SeatScale", 1, 0.05, 1);
                spec.OpenSeconds = (float)StrictJson.Number(payload, "", "OpenSeconds", 0.9, 0.2, 3);
                spec.SpinSeconds = (float)StrictJson.Number(payload, "", "SpinSeconds", 2.6, 0.5, 10);
                spec.SpinTurns = (int)StrictJson.Integer(payload, "", "SpinTurns", 3, 1, 8);
                spec.ReloadSeconds = (float)StrictJson.Number(payload, "", "ReloadSeconds", 3, 0.4, 10);
                spec.Damage = (float)StrictJson.Number(payload, "", "Damage", 0, 0, 20000);
                spec.BlastRadius = (float)StrictJson.Number(payload, "", "BlastRadius", 7, 0.5, 30);
                spec.Force = (float)StrictJson.Number(payload, "", "Force", 400, 0, 5000);
                spec.TriggerRadius = (float)StrictJson.Number(payload, "", "TriggerRadius", 0.9, 0.5, 10);
                spec.ArmSeconds = (float)StrictJson.Number(payload, "", "ArmSeconds", 1.5, 0, 30);
                spec.Spray = (int)StrictJson.Integer(payload, "", "Spray", 0, 0, 60);
                spec.SpraySpeed = (float)StrictJson.Number(payload, "", "SpraySpeed", 7, 2, 20);
                if (spec.Damage <= 0f && spec.Spray == 0) throw new StrictJsonException("Damage", "or Spray must be set: a munition that does nothing is refused.");
                break;
            case DeployableKind.Scanner:
                spec.Range = (float)StrictJson.Number(payload, "", "Range", 35, 1, 100);
                spec.Scan = (float)StrictJson.Number(payload, "", "Scan", 2, 0.5, 30);
                spec.UnfoldSeconds = (float)StrictJson.Number(payload, "", "UnfoldSeconds", 1.2, 0, 5);
                break;
            case DeployableKind.Floodlight:
                spec.LightRadius = (float)StrictJson.Number(payload, "", "LightRadius", 30, 5, 80);
                spec.FogRadius = (float)StrictJson.Number(payload, "", "FogRadius", 22, 3, 80);
                spec.Intensity = (float)StrictJson.Number(payload, "", "Intensity", 1.6, 0.2, 8);
                break;
        }
        if (spec.MaxRange < spec.MinRange) throw new StrictJsonException("MaxRange", "must not be below MinRange.");
        return spec;
    }

    /// <summary>Seconds one round takes from its plates starting to open to them shut again.</summary>
    public float RoundSeconds => 2f * OpenSeconds + SpinSeconds + StopSeconds;

    /// <summary>Seconds from one round starting to the next: the round, then the column lowering and rising again.</summary>
    public float CycleSeconds => RoundSeconds + ReloadSeconds;

    /// <summary>Seconds the device stands and works after it has risen: a dispenser its rounds (the last lowers no column), the others <see cref="Lifetime"/>.</summary>
    public float WorkSeconds => Kind == DeployableKind.Dispenser ? (Rounds - 1) * CycleSeconds + RoundSeconds : Lifetime;

    /// <summary>Seconds after it has risen that the device starts its work: a scanner unfolds first.</summary>
    public float WorkDelay => Kind == DeployableKind.Scanner ? UnfoldSeconds : 0f;

    /// <summary>Seconds after the work ends that the device's laid munitions last (a dispenser), else 0.</summary>
    public float FieldSeconds => Kind == DeployableKind.Dispenser ? Lifetime : 0f;
}
