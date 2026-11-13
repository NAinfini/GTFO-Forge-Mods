using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeOrdnance;

/// <summary>One step of a call-in code.</summary>
public enum Direction : byte { Up = 1, Down = 2, Left = 3, Right = 4 }

/// <summary>One validated call-in definition. <see cref="Key"/> is the stable network identity of <see cref="Id"/>.</summary>
public sealed class OrdnanceDefinition
{
    internal OrdnanceDefinition(string id, string name, string category, uint categoryBeam, string icon, string description, IReadOnlyList<Direction> code,
        double cooldown, int uses, double callInDelay, string payloadType, JsonElement payload, object parameters, string sourceFile,
        uint? beamColour = null, IReadOnlyList<OrdnanceStat>? stats = null, bool callable = true)
    {
        Id = id; Name = name; Category = category; CategoryBeamRgb = categoryBeam; Icon = icon; Description = description; Code = code; Cooldown = cooldown; Uses = uses;
        CallInDelay = callInDelay; PayloadType = payloadType; Payload = payload; Parameters = parameters;
        SourceFile = sourceFile; Key = ForgeCore.Fnv.Hash32(id); BeamColour = beamColour; Stats = stats ?? Array.Empty<OrdnanceStat>(); Callable = callable;
    }

    /// <summary>Rating rows shown as bars in the lobby picker's details, from the file's optional <c>Stats</c>.
    /// Display only; empty when the file gives none.</summary>
    public IReadOnlyList<OrdnanceStat> Stats { get; }

    public string Id { get; }
    public string Name { get; }
    /// <summary>False for a definition written with <c>"Callable": false</c>: players never pick or call it (no category, code,
    /// cooldown or uses); only a level's <c>Drops</c> and <see cref="OrdnanceApi.Place"/> bring it.</summary>
    public bool Callable { get; }
    /// <summary>The id of the category tab this definition sits under (<see cref="OrdnanceApi.RegisterCategory"/>).</summary>
    public string Category { get; }
    /// <summary>The default beam colour of <see cref="Category"/>, 0xRRGGBB.</summary>
    public uint CategoryBeamRgb { get; }
    /// <summary>File name of a PNG next to the definition file, or empty for the built-in icon of <see cref="Category"/>.</summary>
    public string Icon { get; }
    /// <summary>Text shown in the lobby picker; display only. Empty when the file gives none.</summary>
    public string Description { get; }
    public IReadOnlyList<Direction> Code { get; }
    /// <summary>Seconds of cooldown after a call is accepted.</summary>
    public double Cooldown { get; }
    /// <summary>Calls available per level before the settings multiplier, or <see cref="UnlimitedUses"/> when the file leaves
    /// <c>Uses</c> out: then only the cooldown limits it.</summary>
    public int Uses { get; }
    public bool Unlimited => Uses == UnlimitedUses;
    public const int UnlimitedUses = -1;
    /// <summary>Seconds between the beacon landing and the arrival.</summary>
    public double CallInDelay { get; }
    public string PayloadType { get; }
    /// <summary>The whole <c>Payload</c> object as written in the file, cloned.</summary>
    public JsonElement Payload { get; }
    /// <summary>What the payload type's parser returned for <see cref="Payload"/>.</summary>
    public object Parameters { get; }
    public string SourceFile { get; }
    public uint Key { get; }
    /// <summary>The call-in beacon's beam colour as 0xRRGGBB, from the file's optional <c>BeamColour</c> (<c>"#RRGGBB"</c>), or
    /// null when the file leaves it out. Display only.</summary>
    public uint? BeamColour { get; }
    /// <summary>The colour the beacon's beam is drawn in: <see cref="BeamColour"/>, else the default of <see cref="Category"/>.</summary>
    public uint BeamRgb => BeamColour ?? CategoryBeamRgb;
}

/// <summary>One rating row of a definition: <see cref="Value"/> of <see cref="Max"/> boxes lit after <see cref="Name"/>.</summary>
public sealed class OrdnanceStat
{
    internal OrdnanceStat(string name, int value, int max) { Name = name; Value = value; Max = max; }

    public string Name { get; }
    public int Value { get; }
    public int Max { get; }
}

/// <summary>Parses and validates the parameters of one payload type. Implemented by every payload.</summary>
public interface IOrdnancePayloadParser
{
    /// <summary>Returns the payload's own parameter object for the <c>Payload</c> JSON object (which also holds <c>Type</c>),
    /// or throws <see cref="PayloadParameterException"/> naming the offending field.</summary>
    object Parse(JsonElement payload);
}

public sealed class PayloadParameterException : ForgeCore.StrictJsonException
{
    public PayloadParameterException(string field, string message) : base(field, message) { }
}

/// <summary>The payload types known to this install, by name.</summary>
internal sealed class PayloadRegistry
{
    private readonly Dictionary<string, IOrdnancePayloadParser> _parsers = new(StringComparer.Ordinal);

    public void Register(string type, IOrdnancePayloadParser parser)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Trim() != type) throw new ArgumentException("Payload type must be a non-empty name without surrounding spaces.", nameof(type));
        if (parser == null) throw new ArgumentNullException(nameof(parser));
        if (!_parsers.TryAdd(type, parser)) throw new InvalidOperationException("Payload type '" + type + "' is already registered.");
    }

    public bool TryGet(string type, out IOrdnancePayloadParser parser) => _parsers.TryGetValue(type, out parser!);
    public IEnumerable<string> Types => _parsers.Keys;
    /// <summary>Which payload types this install has, independent of registration order: a missing or extra add-on makes the handshake differ.</summary>
    public ulong TypesHash => Hashing.TypesHash(_parsers.Keys);
}
