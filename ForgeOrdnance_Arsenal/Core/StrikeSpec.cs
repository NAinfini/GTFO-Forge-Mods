using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeCore;

namespace ForgeOrdnanceArsenal;

/// <summary>The offensive call-ins of payload type <see cref="StrikeSpec.TypeName"/>, chosen by the payload's <c>Kind</c>.</summary>
public enum StrikeKind : byte
{
    /// <summary>One heavy shell on the beacon.</summary>
    Precision = 1,
    /// <summary>Shells at random points around the beacon over a few seconds.</summary>
    Area = 2,
    /// <summary>Rows of shells walking forward along the throw direction.</summary>
    Creeping = 3,
    /// <summary>Small bomblets along a line through the beacon, across the throw direction.</summary>
    Cluster = 4,
    /// <summary>One shell, then a burning area that damages everyone inside.</summary>
    Incendiary = 5,
    /// <summary>A canister, then a gas cloud that damages everyone inside.</summary>
    Gas = 6,
    /// <summary>An emitter that slows the enemies around it (movement and attacks) while they stay inside.</summary>
    Stasis = 7,
    /// <summary>A beam from above that keeps hitting the nearest enemy.</summary>
    Beam = 8,
    /// <summary>One shell on the biggest enemy near the beacon.</summary>
    Hunter = 9,
}


/// <summary>Validated parameters of one offensive call-in. Every field is plain data read once at load; fields a kind does
/// not use keep their default and are rejected in the file.</summary>
public sealed class StrikeSpec
{
    public const string TypeName = "arsenal.Strike";

    public StrikeKind Kind { get; internal set; }
    /// <summary>Peak damage of one explosion (at its centre; the game's explosion falls off to its edge).</summary>
    public float Damage { get; internal set; }
    /// <summary>Radius of one explosion, metres.</summary>
    public float BlastRadius { get; internal set; }
    /// <summary>Push of one explosion on bodies (the game's explosion force).</summary>
    public float Force { get; internal set; }
    /// <summary>Number of shells (Area, Cluster) or shells per row (Creeping).</summary>
    public int Shells { get; internal set; }
    /// <summary>Area: radius the shells fall in. Hunter: radius searched for enemies; Beam: radius around the beam's current point searched for the next enemy.</summary>
    public float Spread { get; internal set; }
    /// <summary>Area, Cluster: seconds over which the shells fall. Zones, Beam: seconds the effect lasts.</summary>
    public float Duration { get; internal set; }
    public int Rows { get; internal set; }
    /// <summary>Creeping: metres between rows. Cluster: length of the line.</summary>
    public float Length { get; internal set; }
    /// <summary>Creeping: width of one row, metres.</summary>
    public float Width { get; internal set; }
    /// <summary>Creeping: seconds between rows.</summary>
    public float RowInterval { get; internal set; }
    /// <summary>Zones and Beam: seconds between damage ticks.</summary>
    public float Tick { get; internal set; }
    /// <summary>Zones and Beam: damage of one tick to one target.</summary>
    public float TickDamage { get; internal set; }
    /// <summary>Zones: radius of the area, metres.</summary>
    public float ZoneRadius { get; internal set; }
    /// <summary>Beam: metres per second its floor point moves toward the nearest living enemy.</summary>
    public float Speed { get; internal set; }
    /// <summary>Stasis: the speed an enemy keeps inside the field, as a share of its own (0.35 = 35 %). Overlapping fields
    /// never compound: the slowest field an enemy stands in applies.</summary>
    public float Slow { get; internal set; }

    private readonly struct Field
    {
        internal Field(string name, double fallback, double min, double max, Action<StrikeSpec, double> set, bool integer = false)
        {
            Name = name; Fallback = fallback; Min = min; Max = max; Set = set; Integer = integer;
        }

        internal readonly string Name;
        internal readonly double Fallback, Min, Max;
        internal readonly Action<StrikeSpec, double> Set;
        internal readonly bool Integer;
    }

    private static Field Damage_(double d, double max = 20000) => new("Damage", d, 0, max, (s, v) => s.Damage = (float)v);
    private static Field Blast(double d) => new("BlastRadius", d, 0.5, 30, (s, v) => s.BlastRadius = (float)v);
    private static Field Force_(double d) => new("Force", d, 0, 5000, (s, v) => s.Force = (float)v);
    private static Field Shells_(double d) => new("Shells", d, 1, 32, (s, v) => s.Shells = (int)v, true);
    private static Field Spread_(double d) => new("Spread", d, 0, 60, (s, v) => s.Spread = (float)v);
    private static Field Duration_(double d, double min = 0) => new("Duration", d, min, 120, (s, v) => s.Duration = (float)v);
    private static Field Tick_(double d) => new("Tick", d, 0.1, 5, (s, v) => s.Tick = (float)v);
    private static Field TickDamage_(double d) => new("TickDamage", d, 0, 2000, (s, v) => s.TickDamage = (float)v);
    private static Field Speed_(double d) => new("Speed", d, 0.5, 20, (s, v) => s.Speed = (float)v);
    private static Field Zone(double d) => new("ZoneRadius", d, 0.5, 30, (s, v) => s.ZoneRadius = (float)v);

    /// <summary>The fields each kind reads, with their defaults (the plan's §5 balance).</summary>
    private static readonly Dictionary<StrikeKind, Field[]> Fields = new()
    {
        [StrikeKind.Precision] = new[] { Damage_(600), Blast(5), Force_(800) },
        [StrikeKind.Area] = new[] { Damage_(180), Blast(4.5), Force_(500), Shells_(7), Spread_(15), Duration_(5) },
        [StrikeKind.Creeping] = new[]
        {
            Damage_(160), Blast(4.5), Force_(500), Shells_(3),
            new("Rows", 5, 1, 12, (s, v) => s.Rows = (int)v, true),
            new("Length", 5, 1, 20, (s, v) => s.Length = (float)v),
            new("Width", 10, 0, 30, (s, v) => s.Width = (float)v),
            new("RowInterval", 1.2, 0.2, 10, (s, v) => s.RowInterval = (float)v),
        },
        [StrikeKind.Cluster] = new[]
        {
            Damage_(90), Blast(3.5), Force_(300), Shells_(10), Duration_(1.5),
            new("Length", 20, 1, 60, (s, v) => s.Length = (float)v),
        },
        [StrikeKind.Incendiary] = new[] { Damage_(80), Blast(3.5), Force_(300), Duration_(10, 1), Tick_(0.5), TickDamage_(3), Zone(6) },
        [StrikeKind.Gas] = new[] { Duration_(12, 1), Tick_(0.5), TickDamage_(2.5), Zone(7) },
        [StrikeKind.Stasis] = new[]
        {
            Duration_(20, 1), Zone(8),
            new("Slow", 0.35, 0.05, 0.95, (s, v) => s.Slow = (float)v),
        },
        [StrikeKind.Beam] = new[] { Duration_(10, 1), Tick_(0.2), TickDamage_(6), Spread_(18), Speed_(4) },
        [StrikeKind.Hunter] = new[] { Damage_(2000, 50000), Blast(3), Force_(800), Spread_(25), TickDamage_(60) },
    };

    private static readonly string[] KindFields = { "Type", "Kind" };

    /// <summary>Parses the payload object (it also holds <c>Type</c>). Throws <see cref="StrictJsonException"/>.</summary>
    public static StrikeSpec Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new StrictJsonException("Payload", "must be an object.");
        var kind = StrictJson.Enum<StrikeKind>(payload, "", "Kind");
        var fields = Fields[kind];
        StrictJson.OnlyKnown(payload, "", Array.ConvertAll(fields, f => f.Name), KindFields, kind.ToString());
        var spec = new StrikeSpec { Kind = kind };
        foreach (var field in fields)
            field.Set(spec, field.Integer ? StrictJson.Integer(payload, "", field.Name, field.Fallback, field.Min, field.Max)
                : StrictJson.Number(payload, "", field.Name, field.Fallback, field.Min, field.Max));
        return spec;
    }
}
