using System;
using System.Globalization;

namespace ForgeDevelopment.Native;

internal enum ExperimentTargetKind
{
    LocalPlayer,
    LocalPlayerStatus,
    GuiManager,
    NavMarkerLayer,
    LookedAtEnemy,
    LookedAtDoor,
    LookedAtTerminal,
    LookedAtGenerator,
    NearestDoor,
    NearestGenerator,
    NearestTerminal,
    NearestEnemy,
    Zone,
    StaticType,
    AllOfType,

    /// <summary>The value the previous call returned; only usable as a step target.</summary>
    PreviousResult
}

/// <summary>
/// A parsed <c>target</c> selector. The grammar is decided here so a typo fails at load, while the
/// lookup itself (scene search, physics ray, zone table) belongs to the native resolver.
/// </summary>
internal sealed class ExperimentTargetSpec
{
    internal ExperimentTargetKind Kind { get; private init; }
    internal string Raw { get; private init; } = "";

    /// <summary><c>static:&lt;full type name&gt;</c> / <c>allOfType:&lt;name or wildcard&gt;</c>.</summary>
    internal string TypeName { get; private init; } = "";

    /// <summary><c>zone:&lt;dimension&gt;/&lt;layer&gt;/&lt;localIndex&gt;</c>.</summary>
    internal string Dimension { get; private init; } = "";
    internal string Layer { get; private init; } = "";
    internal string LocalIndex { get; private init; } = "";

    internal float MaximumDistance { get; private init; } = 0f;
    internal string LayerFilter { get; private init; } = "";

    /// <summary>Addresses the value the previous <c>call</c> returned, without inventing a selector for it.</summary>
    internal static readonly ExperimentTargetSpec ForResult = new() { Kind = ExperimentTargetKind.PreviousResult, Raw = "result" };


    internal static string DescribeSyntax() =>
        "localPlayer, localPlayerStatus, guiManager, navMarkerLayer, " +
        "lookedAtEnemy|lookedAtDoor|lookedAtTerminal|lookedAtGenerator[:maxDistance=<m>], " +
        "nearestDoor|nearestGenerator|nearestTerminal|nearestEnemy[:maxDistance=<m>,layer=<name>], " +
        "zone:<dimension>/<layer>/<localIndex>, static:<full type name>, allOfType:<name or namespace wildcard>; " +
        "a step may also use target \"result\" for the previous call's return value";

    internal static bool TryParse(string text, out ExperimentTargetSpec spec)
    {
        spec = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var raw = text.Trim();
        var colon = raw.IndexOf(':');
        var head = colon < 0 ? raw : raw[..colon];
        var tail = colon < 0 ? "" : raw[(colon + 1)..];
        switch (head)
        {
            case "localPlayer":
            case "localPlayerStatus":
            case "guiManager":
            case "navMarkerLayer":
                if (tail.Length != 0) return false;
                spec = new ExperimentTargetSpec
                {
                    Kind = head switch
                    {
                        "localPlayer" => ExperimentTargetKind.LocalPlayer,
                        "localPlayerStatus" => ExperimentTargetKind.LocalPlayerStatus,
                        "guiManager" => ExperimentTargetKind.GuiManager,
                        _ => ExperimentTargetKind.NavMarkerLayer
                    },
                    Raw = raw
                };
                return true;
            case "lookedAtEnemy":
            case "lookedAtDoor":
            case "lookedAtTerminal":
            case "lookedAtGenerator":
            case "nearestDoor":
            case "nearestGenerator":
            case "nearestTerminal":
            case "nearestEnemy":
                {
                    if (!TryOptions(tail, out var distance, out var layer, out var error)) return false;
                    var kind = head switch
                    {
                        "lookedAtEnemy" => ExperimentTargetKind.LookedAtEnemy,
                        "lookedAtDoor" => ExperimentTargetKind.LookedAtDoor,
                        "lookedAtTerminal" => ExperimentTargetKind.LookedAtTerminal,
                        "lookedAtGenerator" => ExperimentTargetKind.LookedAtGenerator,
                        "nearestDoor" => ExperimentTargetKind.NearestDoor,
                        "nearestGenerator" => ExperimentTargetKind.NearestGenerator,
                        "nearestTerminal" => ExperimentTargetKind.NearestTerminal,
                        _ => ExperimentTargetKind.NearestEnemy
                    };
                    spec = new ExperimentTargetSpec { Kind = kind, Raw = raw, MaximumDistance = distance, LayerFilter = layer };
                    return true;
                }
            case "zone":
                {
                    var parts = tail.Split('/');
                    if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) return false;
                    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return false;
                    if (!IsLayer(parts[1])) return false;
                    if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var localIndex) || localIndex < 0 || localIndex > 20) return false;
                    spec = new ExperimentTargetSpec { Kind = ExperimentTargetKind.Zone, Raw = raw, Dimension = parts[0], Layer = parts[1], LocalIndex = parts[2] };
                    return true;
                }
            case "static":
                if (tail.Length == 0 || !IsTypeName(tail)) return false;
                spec = new ExperimentTargetSpec { Kind = ExperimentTargetKind.StaticType, Raw = raw, TypeName = tail };
                return true;
            case "allOfType":
                if (tail.Length == 0 || !IsTypeName(tail)) return false;
                spec = new ExperimentTargetSpec { Kind = ExperimentTargetKind.AllOfType, Raw = raw, TypeName = tail };
                return true;
            default:
                return false;
        }
    }

    internal string Describe() => Kind switch
    {
        ExperimentTargetKind.StaticType => "static type " + TypeName,
        ExperimentTargetKind.AllOfType => "all loaded " + TypeName,
        ExperimentTargetKind.Zone => "zone " + Dimension + "/" + Layer + "/" + LocalIndex,
        ExperimentTargetKind.LookedAtEnemy or ExperimentTargetKind.LookedAtDoor or ExperimentTargetKind.LookedAtTerminal or ExperimentTargetKind.LookedAtGenerator
            => "crosshair " + Raw.Replace("lookedAt", "", StringComparison.Ordinal).ToLowerInvariant() + (MaximumDistance > 0 ? " within " + MaximumDistance.ToString("0.#", CultureInfo.InvariantCulture) + " m" : ""),
        ExperimentTargetKind.NearestDoor or ExperimentTargetKind.NearestGenerator or ExperimentTargetKind.NearestTerminal or ExperimentTargetKind.NearestEnemy
            => "nearest " + Raw.Replace("nearest", "", StringComparison.Ordinal).ToLowerInvariant() + (MaximumDistance > 0 ? " within " + MaximumDistance.ToString("0.#", CultureInfo.InvariantCulture) + " m" : ""),
        _ => Raw
    };

    private static bool TryOptions(string tail, out float distance, out string layer, out string error)
    {
        distance = 0f;
        layer = "";
        error = "";
        if (tail.Length == 0) return true;
        foreach (var part in tail.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                error = "options are key=value pairs";
                return false;
            }
            var key = pair[0].Trim();
            var value = pair[1].Trim();
            switch (key)
            {
                case "maxDistance":
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out distance) || distance <= 0)
                    {
                        error = "maxDistance must be a positive number";
                        return false;
                    }
                    break;
                case "layer":
                    if (!IsLayer(value))
                    {
                        error = "layer must be MainLayer, SecondaryLayer, ThirdLayer or 0..2";
                        return false;
                    }
                    layer = value;
                    break;
                default:
                    error = "unknown option '" + key + "'";
                    return false;
            }
        }
        return true;
    }

    private static bool IsLayer(string value) =>
        value is "MainLayer" or "SecondaryLayer" or "ThirdLayer" || (value is "0" or "1" or "2");

    private static bool IsTypeName(string value)
    {
        foreach (var c in value)
            if (!(char.IsLetterOrDigit(c) || c is '_' or '.' or '`' or '+' or '*')) return false;
        return value.Length > 0;
    }
}
