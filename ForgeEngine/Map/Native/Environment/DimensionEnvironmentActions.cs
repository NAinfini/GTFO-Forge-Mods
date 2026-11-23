using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.Presentation;
using ForgeEngine.Presentation.Native;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The native half of `forge.action.presentation.dimension_environment`: one dimension's own outside environment,
/// written on the machine that runs the step.
///
/// <b>Where the members live.</b> The level keeps one record per dimension — `LevelGeneration.Dimension` — and its
/// own `DimensionData` member is the record the game's sky and weather read from
/// (`LevelGeneration.Dimension.DimensionData`, a global-namespace `DimensionData`; catalog/native/native-schema.json
/// `classes.DimensionData` and catalog/native/native-runtime.json `types.DimensionData` both list every member this file
/// writes). The dimension is resolved through the game's own lookup
/// (`Dimension.GetDimension(eDimensionIndex, out Dimension)`), so a request naming a dimension the level does not
/// hold is refused by name instead of being read as another one.
///
/// <b>What is written.</b> Only the entries the request filled; a member the request did not name is left exactly
/// as the level had it. Each entry is written straight onto the record's own public property, which is why a
/// re-read of the dimension sees the value a later frame renders — the written value is not mirrored in a Forge-side
/// table (only the value it replaced, for the restore below) and nothing is replicated, because what these members
/// decide is one client's view.
///
/// <b>What is refused.</b> A viewer set that is not players (<see cref="PresentationAudience"/>), a missing or
/// non-integer `dimension`, an index outside the game's own `eDimensionIndex` bound, a dimension this level does
/// not hold, and an entry whose JSON type or native range cannot carry the value it names. A request is judged in
/// full before the first write, so a refusal never leaves half a dimension changed.
///
/// <b>Restore.</b> Nothing proves the record is rebuilt per level — it may be the data block's own object, which the
/// next level reads again — so the first write of a member in a world records the value it had, and
/// <see cref="Restore"/> puts every recorded member back when the world ends (the session calls it on a world change
/// that is not a migration and when the runtime stops). Each client restores its own view; nothing is sent.
/// </summary>
internal sealed class DimensionEnvironmentActions
{
    /// <summary>The JSON type one entry is declared with, which is also the native member's own type.</summary>
    internal enum EntryKind { Boolean, Integer, Number }

    /// <summary>One entry's value, already checked against its kind. A single carrier rather than three parallel
    /// tables: the kind says which member is meaningful, and the other two are left at their defaults.</summary>
    internal readonly record struct Value(bool Boolean, uint Integer, float Number);

    /// <summary>
    /// The kind one declared entry takes, or null for a name this build has no member for. The switch below is
    /// the one mapping from the card's name to the game's member; this answers the type half of it, so a request
    /// is judged in full — including whether every entry is mapped at all — before anything is written.
    /// </summary>
    internal static EntryKind? KindOf(string name) => name switch
    {
        "is_outside" or "sandstorm" => EntryKind.Boolean,
        "atmosphere" or "clouds" => EntryKind.Integer,
        "light_azimuth" or "light_elevation" or "light_intensity" or "ambient_intensity"
            or "reflections_intensity" or "godray_range" or "godray_exponent" or "atmosphere_density"
            or "exposure" or "aerial_scale" or "mie_scattering" or "mie_g" or "multiple_scattering"
            or "clouds_coverage" or "clouds_density" or "clouds_sharpness" or "clouds_shadow_opacity"
            or "clouds_timescale" or "clouds_crawling" or "clouds_fade" or "sandstorm_edge_a"
            or "sandstorm_edge_b" or "sandstorm_min_fog" => EntryKind.Number,
        _ => null
    };

    /// <summary>One entry written onto the game's own member. Every arm is one name of the card and one public
    /// property of the record, and the caller has already refused a name this build maps no member for, so the
    /// default arm is a mapping this half does not have rather than a value a request can carry.</summary>
    internal static void Write(DimensionData data, string name, Value value)
    {
        switch (name)
        {
            case "is_outside": data.IsOutside = value.Boolean; return;
            case "light_azimuth": data.LightAzimuth = value.Number; return;
            case "light_elevation": data.LightElevation = value.Number; return;
            case "light_intensity": data.LightIntensity = value.Number; return;
            case "ambient_intensity": data.AmbientIntensity = value.Number; return;
            case "reflections_intensity": data.ReflectionsIntensity = value.Number; return;
            case "godray_range": data.GodrayRange = value.Number; return;
            case "godray_exponent": data.GodrayExponent = value.Number; return;
            // The two data block ids are `uint` members: the block itself is loaded by the game, and an id no
            // block answers for is the game's own "no atmosphere/clouds" value rather than a refusal this row
            // could prove.
            case "atmosphere": data.AtmosphereData = value.Integer; return;
            case "atmosphere_density": data.AtmosphereDensity = value.Number; return;
            case "exposure": data.Exposure = value.Number; return;
            case "aerial_scale": data.AerialScale = value.Number; return;
            case "mie_scattering": data.MieScattering = value.Number; return;
            case "mie_g": data.MieG = value.Number; return;
            case "multiple_scattering": data.MultipleScattering = value.Number; return;
            case "clouds": data.CloudsData = value.Integer; return;
            case "clouds_coverage": data.CloudsCoverage = value.Number; return;
            case "clouds_density": data.CloudsDensity = value.Number; return;
            case "clouds_sharpness": data.CloudsSharpness = value.Number; return;
            case "clouds_shadow_opacity": data.CloudsShadowOpacity = value.Number; return;
            case "clouds_timescale": data.CloudsTimescale = value.Number; return;
            case "clouds_crawling": data.CloudsCrawling = value.Number; return;
            case "clouds_fade": data.CloudsFade = value.Number; return;
            case "sandstorm": data.Sandstorm = value.Boolean; return;
            case "sandstorm_edge_a": data.SandstormEdgeA = value.Number; return;
            case "sandstorm_edge_b": data.SandstormEdgeB = value.Number; return;
            case "sandstorm_min_fog": data.SandstormMinFog = value.Number; return;
            default: throw new InvalidOperationException("No DimensionData member is mapped for the entry " + name + ".");
        }
    }

    internal static Value Read(DimensionData data, string name) => name switch
    {
        "is_outside" => new(data.IsOutside, 0, 0),
        "sandstorm" => new(data.Sandstorm, 0, 0),
        "atmosphere" => new(false, data.AtmosphereData, 0),
        "clouds" => new(false, data.CloudsData, 0),
        _ => new(false, 0, name switch
        {
            "light_azimuth" => data.LightAzimuth,
            "light_elevation" => data.LightElevation,
            "light_intensity" => data.LightIntensity,
            "ambient_intensity" => data.AmbientIntensity,
            "reflections_intensity" => data.ReflectionsIntensity,
            "godray_range" => data.GodrayRange,
            "godray_exponent" => data.GodrayExponent,
            "atmosphere_density" => data.AtmosphereDensity,
            "exposure" => data.Exposure,
            "aerial_scale" => data.AerialScale,
            "mie_scattering" => data.MieScattering,
            "mie_g" => data.MieG,
            "multiple_scattering" => data.MultipleScattering,
            "clouds_coverage" => data.CloudsCoverage,
            "clouds_density" => data.CloudsDensity,
            "clouds_sharpness" => data.CloudsSharpness,
            "clouds_shadow_opacity" => data.CloudsShadowOpacity,
            "clouds_timescale" => data.CloudsTimescale,
            "clouds_crawling" => data.CloudsCrawling,
            "clouds_fade" => data.CloudsFade,
            "sandstorm_edge_a" => data.SandstormEdgeA,
            "sandstorm_edge_b" => data.SandstormEdgeB,
            "sandstorm_min_fog" => data.SandstormMinFog,
            _ => throw new InvalidOperationException("No DimensionData member is mapped for the entry " + name + ".")
        })
    };

    /// <summary>The members this world wrote, each with the value it had before its first write. Static because the
    /// record belongs to the game and outlives any one session that wrote it.</summary>
    private static readonly List<(DimensionData Data, string Name, Value Original)> WorldOriginals = new();

    private static void RecordOriginal(DimensionData data, string name, Value current)
    {
        foreach (var row in WorldOriginals)
            if (row.Data.Pointer == data.Pointer && row.Name == name) return;
        WorldOriginals.Add((data, name, current));
    }

    /// <summary>Puts every member this world wrote back to the value it had before, newest first, and forgets them.
    /// A record the game has released is skipped; a member that cannot be restored is reported and the others are
    /// still restored.</summary>
    internal static void Restore()
    {
        for (int index = WorldOriginals.Count - 1; index >= 0; index--)
        {
            var (data, name, original) = WorldOriginals[index];
            try
            {
                if (data.WasCollected || data.Pointer == IntPtr.Zero || Read(data, name) == original) continue;
                Write(data, name, original);
                if (Read(data, name) != original) throw new InvalidOperationException("DimensionData restore did not retain " + name + ".");
            }
            catch (Exception error) { MapLog.Contained("dimension-environment.restore", error); }
        }
        WorldOriginals.Clear();
    }

    private readonly Func<int, DimensionData?> _dimension;
    private readonly Action<string> _report;

    internal DimensionEnvironmentActions(Func<int, DimensionData?> dimension, Action<string> report)
    {
        _dimension = dimension ?? throw new ArgumentNullException(nameof(dimension));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The production wiring: the level's own dimension record, read through the game's own lookup at
    /// the moment the step runs, so a dimension that was torn down under the request is refused rather than
    /// written through a record of a level that is gone.</summary>
    internal static DimensionEnvironmentActions For(Action<string> report)
        => new(DimensionDataOf, report);

    /// <summary>The record one dimension index names, or null when the index is outside the enum or this level
    /// holds no such dimension. The record is the game's own; this reads it and writes its members, and keeps no
    /// copy of any of them.</summary>
    internal static DimensionData? DimensionDataOf(int dimension)
    {
        if (!EnvironmentZone.DimensionInRange(dimension)) return null;
        if (!Dimension.GetDimension((eDimensionIndex)dimension, out var resolved) || resolved == null) return null;
        return resolved.DimensionData;
    }

    internal CommandResult Handle(CommandContext context) => Apply(context);

    private CommandResult Apply(CommandContext context)
    {
        if (!PresentationAudience.TryCheck(context, DimensionEnvironmentContract.Viewers, out var refusal)) return refusal;
        if (context.Integer(DimensionEnvironmentContract.Dimension) is not { } dimensionValue
            || dimensionValue < int.MinValue || dimensionValue > int.MaxValue)
            return Outcome.Rejected(ResultCode.DimensionRequired);
        int dimension = (int)dimensionValue;
        if (!EnvironmentZone.DimensionInRange(dimension)) return Outcome.Rejected(ResultCode.DimensionIndexInvalid);
        // The whole request is judged before the first write, so a refusal leaves the dimension exactly as it
        // was: a value the native member cannot carry is never applied and then reported.
        if (!TryEntries(context, out var entries, out string code)) return Outcome.Rejected(code);
        DimensionData? data = null;
        var originals = new List<(string Name, Value Value)>(entries.Count);
        try
        {
            data = _dimension(dimension);
            if (data == null) return Outcome.Rejected(ResultCode.DimensionNotFound);
            foreach (var (name, _) in entries)
            {
                var current = Read(data, name);
                originals.Add((name, current));
                RecordOriginal(data, name, current);
            }
            foreach (var (name, value) in entries)
            {
                Write(data, name, value);
                if (Read(data, name) != value)
                    throw new InvalidOperationException("DimensionData did not retain " + name + ".");
            }
        }
        catch (Exception error)
        {
            // Restore every changed entry, including a setter that changed its member before throwing. A failure
            // on one member cannot prevent the other originals from being restored and read back.
            if (data != null)
                for (int index = originals.Count - 1; index >= 0; index--)
                {
                    var (name, value) = originals[index];
                    try
                    {
                        if (Read(data, name) == value) continue;
                        Write(data, name, value);
                        if (Read(data, name) != value) throw new InvalidOperationException("DimensionData restore did not retain " + name + ".");
                    }
                    catch (Exception restoreError)
                    { Report("map.dimension-environment-failed: restore " + name + ": " + restoreError.GetType().Name + ": " + restoreError.Message); }
                }
            Report("map.dimension-environment-failed: " + error.GetType().Name + ": " + error.Message);
            return Present(context, Status.Failed, ResultCode.NativeCommitException);
        }
        // The tier commits no world state: the values are this machine's own view of the dimension, so each
        // viewer's row is the presentation tier's `partial + none`.
        return Present(context, Status.Partial, ResultCode.DimensionEnvironmentApplied);
    }

    /// <summary>One row per viewer carrying the same answer and the viewer count.</summary>
    private static Outcome Present(CommandContext context, Status status, string code)
        => PresentationRows.Answer(context, status, code, Blocks.ActionPresentationDimensionEnvironment.RowTargetCount);

    /// <summary>Every declared entry the request filled, in the card's own order, or the code that names the
    /// first one that cannot be carried. An absent or explicitly null entry is not a request to change anything
    /// and is left out; an entry this build has no member for, one whose value is of another type, and one outside
    /// the range its native member takes are each refused whole before anything is written.</summary>
    private static bool TryEntries(CommandContext context, out List<(string Name, Value Value)> entries, out string code)
    {
        entries = new List<(string Name, Value Value)>();
        code = ResultCode.DimensionEntryType;
        for (int index = 0; index < DimensionEnvironmentContract.EnvironmentEntries.Length; index++)
        {
            var name = DimensionEnvironmentContract.EnvironmentEntries[index];
            var slot = DimensionEnvironmentContract.EntrySlots[index];
            if (!context.Present(slot)) continue;
            if (KindOf(name) is not { } kind)
            {
                // The card declares an entry this build maps to no member. It is refused by name rather than
                // dropped, because an entry nobody applies is not an entry that succeeded.
                code = ResultCode.DimensionEntryUnsupported;
                return false;
            }
            switch (kind)
            {
                case EntryKind.Boolean:
                    if (context.Boolean(slot) is not { } boolean) return false;
                    entries.Add((name, new Value(boolean, 0u, 0f)));
                    break;
                case EntryKind.Integer:
                    if (context.Integer(slot) is not { } integer)
                    {
                        code = context.Kind(slot) == ValueKind.Number ? ResultCode.DimensionEntryRange : ResultCode.DimensionEntryType;
                        return false;
                    }
                    if (integer < 0 || integer > uint.MaxValue) { code = ResultCode.DimensionEntryRange; return false; }
                    uint id = (uint)integer;
                    entries.Add((name, new Value(false, id, 0f)));
                    break;
                default:
                    if (context.Number(slot) is not { } number)
                    {
                        code = ResultCode.DimensionEntryType;
                        return false;
                    }
                    // The sun's two angles take any number of degrees and fold back onto one turn, so 450 is 90.
                    if (name is "light_azimuth" or "light_elevation") number %= 360d;
                    float native = (float)number;
                    if (!float.IsFinite(native))
                    {
                        // The member is a native float: a number that is not one — an infinity, a NaN, or a value
                        // past the float range — is outside what the field can carry.
                        code = ResultCode.DimensionEntryRange;
                        return false;
                    }
                    entries.Add((name, new Value(false, 0u, native)));
                    break;
            }
        }
        return true;
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { MapLog.Contained("dimension-environment.report", reportError); }
    }
}
