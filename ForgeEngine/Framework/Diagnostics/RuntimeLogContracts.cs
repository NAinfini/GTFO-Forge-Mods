using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ForgeEngine.Framework;

/// <summary>I-DIAG level. Ordered so an enabled check is one integer comparison; host cfg can only choose Off, Error or Info,
/// Trace exists only after <see cref="RuntimeKernel.ElevateLogging"/>.</summary>
public enum RuntimeLogLevel { Off = 0, Error = 1, Info = 2, Trace = 3 }

/// <summary>The one player-tier `Logging.Level` vocabulary, shared by the Runtime cfg and every package cfg: `off`, `error`
/// or `info`, case and surrounding whitespace insensitive. The SDK reads no cfg of its own; a package plugin reads its own
/// entry and parses the raw text here, so every package accepts the same values and rejects a malformed one with the same
/// message. `trace` is deliberately absent: only elevation reaches it.</summary>
internal static class RuntimeLogConfiguration
{
    public static RuntimeLogLevel ParseLevel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "off" => RuntimeLogLevel.Off,
        "error" => RuntimeLogLevel.Error,
        "info" => RuntimeLogLevel.Info,
        _ => throw new InvalidOperationException("Unsupported Forge Logging.Level; use off, error or info. Native startup was not attempted.")
    };
}

/// <summary>Rate-limit tier the writer applies; Elevated is irreversible for the session.</summary>
public enum RuntimeLogTier { Player, Elevated }

/// <summary>The I-DIAG event codes the kernel writes. `adapter.attached` and
/// `adapter.failed` are absent until I-ADAPTER-SCHEMA rules them, and so are the per-package native diagnostic codes.</summary>
public static partial class RuntimeLogCodes
{
    // The constants are generated from catalog/authoring/diagnostic-codes.json (artifacts/generated/diagnostics/RuntimeLogCodes.g.cs).
}

/// <summary>Kernel reason codes that are not one of the surfaced contract codes (value validation, plan, registration,
/// graph, step result and event groups are all carried by the exception or result that produced them).</summary>
internal static partial class RuntimeLogReasonCodes
{
    // The constants are generated from catalog/authoring/diagnostic-codes.json (artifacts/generated/diagnostics/RuntimeLogReasonCodes.g.cs).
}

public readonly struct RuntimeLogPlan
{
    public string PlanId { get; init; }
    public string ResourceId { get; init; }
    public string ResourceRevision { get; init; }
}

/// <summary>Status and commit stay strings: their vocabulary is validated once the code-table constants land, not here.</summary>
public readonly struct RuntimeLogResult
{
    public string Status { get; init; }
    public string? Commit { get; init; }
    public string Reason { get; init; }
}

/// <summary>One I-DIAG line before the writer stamps schema, origin and seq. Passed by <c>in</c>, never boxed.
/// It has no message: the sink composes it off the simulation thread, so call sites never build strings.
/// It has no inputs: those belong to the ForgeDevelopment trace recorder and are not implemented.</summary>
public readonly struct RuntimeLogRecord
{
    public RuntimeLogLevel Level { get; init; }
    public string Code { get; init; }
    public string Provider { get; init; }
    /// <summary>The provider a record is about when it is not the owner, e.g. the rejected provider of registration.rejected.</summary>
    public string? SubjectProvider { get; init; }
    public long Tick { get; init; }
    public long WorldEpoch { get; init; }
    public long? Frame { get; init; }
    public string? CommandId { get; init; }
    public string? EventId { get; init; }
    public string? CauseId { get; init; }
    public string? RootEventId { get; init; }
    public RuntimeLogPlan? Plan { get; init; }
    /// <summary>The plan file's BepInEx-relative path, `/` separated. Carried by plan.loaded and plan.rejected.</summary>
    public string? Path { get; init; }
    /// <summary>Permissions the plan received, ordinal sorted. Carried by plan.loaded only.</summary>
    public IReadOnlyList<string>? Permissions { get; init; }
    public string? Entry { get; init; }
    public string? Step { get; init; }
    /// <summary>The resolved plan node kind for step lifecycle records: action, control, query or pure.</summary>
    public string? NodeKind { get; init; }
    public string? Binding { get; init; }
    /// <summary>Structured trigger-gate counters, present only on gate refusal records.</summary>
    public long? GateAccumulated { get; init; }
    public long? GateFired { get; init; }
    /// <summary>The actual generated level layout. Only ever set for <see cref="RuntimeLogCodes.MapLayoutGenerated"/>;
    /// the writer serializes it as the record's own `layout` object and the website validates that shape strictly.</summary>
    public RuntimeLogLayout? Layout { get; init; }
    /// <summary>The host's fixed timestep in seconds. Only ever set for <see cref="RuntimeLogCodes.RuntimeTickRate"/>,
    /// together with <see cref="TicksPerSecond"/>, and read from Unity by the only side that can: the game binding.</summary>
    public double? FixedDeltaTime { get; init; }
    /// <summary>The tick rate <see cref="FixedDeltaTime"/> converts to, as the host computed it. One tick is one host
    /// <c>FixedUpdate</c> (contract §3.2), which is what makes this the factor between simulation ticks and seconds.</summary>
    public double? TicksPerSecond { get; init; }
    /// <summary>The rate the host measured and the one source's rate it disagrees with. Only ever set for
    /// <see cref="RuntimeLogCodes.RuntimeTickRateMismatch"/>, together with <see cref="FixedDeltaTime"/>.</summary>
    public double? MeasuredTicksPerSecond { get; init; }
    public double? ExpectedTicksPerSecond { get; init; }
    public RuntimeLogResult? Result { get; init; }
    /// <summary>The entity reference id a domain record is about. Only ever set by a domain record point
    /// (<see cref="RuntimeModuleHandle.Log"/>); the writer serializes it as `entity`.</summary>
    public string? Entity { get; init; }
    /// <summary>The named scalar readings of a domain record, serialized as the `values` object. Only ever set by a domain
    /// record point, whose kernel path has already checked the names and values.</summary>
    public IReadOnlyList<RuntimeLogValue>? Values { get; init; }
    /// <summary>Free-text detail folded into the writer's composed message only; it is never its own JSON field.
    /// Carries the comma-joined, ordinal-sorted paths of every file in a plan-conflict group.</summary>
    public string? Detail { get; init; }
}

/// <summary>The type of one <see cref="RuntimeLogValue"/>: the four JSON scalars a domain record may carry.</summary>
public enum RuntimeLogValueKind : byte { Text, Integer, Number, Flag }

/// <summary>One named scalar of a domain record's `values` object. A value type, so a record point's facts cost nothing
/// until the array holding them is built — which a call site does only after <see cref="RuntimeModuleHandle.Logs"/>.</summary>
public readonly struct RuntimeLogValue
{
    private RuntimeLogValue(string name, RuntimeLogValueKind kind, string? text, long integer, double number)
    { Name = name; Kind = kind; Text = text; Integer = integer; Number = number; }
    public string Name { get; }
    public RuntimeLogValueKind Kind { get; }
    public string? Text { get; }
    public long Integer { get; }
    public double Number { get; }
    public static RuntimeLogValue Of(string name, string value) => new(name, RuntimeLogValueKind.Text, value, 0, 0);
    public static RuntimeLogValue Of(string name, long value) => new(name, RuntimeLogValueKind.Integer, null, value, 0);
    public static RuntimeLogValue Of(string name, double value) => new(name, RuntimeLogValueKind.Number, null, 0, value);
    public static RuntimeLogValue Of(string name, bool value) => new(name, RuntimeLogValueKind.Flag, null, value ? 1 : 0, 0);

    /// <summary>The longest authored text <see cref="Quote"/> keeps.</summary>
    public const int MaxQuoted = 64;

    /// <summary>A text value quoted from authored or native input the package does not control: a plan's reference, a
    /// file name, a game string. It is cut to <see cref="MaxQuoted"/> characters, and a text holding an
    /// account-shaped number is replaced by `account-shaped` whole, and a control character becomes a space, so a record
    /// point can never be refused, or leak an account id, because of what somebody else wrote. A package's own codes and
    /// reasons use <see cref="Of(string, string)"/>.</summary>
    public static RuntimeLogValue Quote(string name, string authored)
    {
        System.ArgumentNullException.ThrowIfNull(authored);
        if (AccountShaped(authored)) return Of(name, "account-shaped");
        var text = authored.Length > MaxQuoted ? authored.Substring(0, MaxQuoted) : authored;
        // Bounding can turn a longer digit run into an account-shaped one at the new end of the preview.
        if (AccountShaped(text)) return Of(name, "account-shaped");
        return Of(name, HasControl(text) ? string.Create(text.Length, text, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++) span[index] = IsControl(source[index]) ? ' ' : source[index];
        }) : text);
    }

    /// <summary>Whether a text holds a control character (below U+0020, or U+007F), which no log value carries.</summary>
    internal static bool HasControl(string text)
    {
        foreach (var character in text) if (IsControl(character)) return true;
        return false;
    }

    private static bool IsControl(char character) => character < ' ' || character == '\u007f';

    // Same bounded, case-insensitive SHA-256 token definition as the website's I-DIAG reader.
    private static readonly Regex ContentDigest = new("(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])");

    // A Steam64 id is seventeen digits and, for every account that exists, starts 76561 19 or 76561 20; a longer digit run is not one.
    // Any other seventeen-digit run (inside a generated hex id, a hash, a pointer) is not an account.
    private static readonly Regex Steam64 = new("76561(?:19|20)[0-9]{10}(?![0-9])");

    /// <summary>Whether text outside opaque content digests holds a Steam64-shaped account number.</summary>
    internal static bool AccountShaped(string? text)
        => text != null && Steam64.IsMatch(ContentDigest.Replace(text, " "));
}

/// <summary>What a domain record point says beyond its level and code. The provider, tick and world epoch are never part
/// of it: the handle stamps its own provider and the kernel its own clock, so a package cannot write a record in another
/// provider's name or at another time. <see cref="Detail"/> reaches the composed message only.</summary>
public readonly struct RuntimeLogFacts
{
    public string? Binding { get; init; }
    public string? EventId { get; init; }
    public string? Entity { get; init; }
    public IReadOnlyList<RuntimeLogValue>? Values { get; init; }
    public string? Detail { get; init; }
}

public readonly record struct RuntimeLogProviderLevel(string Provider, RuntimeLogLevel Level);

/// <summary>Immutable level-table snapshot. The kernel replaces the instance whenever levels change, which tells the
/// sink to write log.level again before the next record.</summary>
public sealed class RuntimeLogLevels
{
    internal RuntimeLogLevels(string runtimeProvider, IReadOnlyList<RuntimeLogProviderLevel> providers, RuntimeLogTier tier)
    { RuntimeProvider = runtimeProvider; Providers = providers; Tier = tier; }
    /// <summary>Owner of log.level and log.dropped.</summary>
    public string RuntimeProvider { get; }
    public IReadOnlyList<RuntimeLogProviderLevel> Providers { get; }
    public RuntimeLogTier Tier { get; }
}

/// <summary>Implemented by the host and passed to the RuntimeKernel constructor; there is no registration or replacement API.
/// The kernel calls it only on its simulation thread, after the level gate passed, so implementations must not block,
/// format or serialize inside the call.</summary>
public interface IRuntimeLogSink
{
    void Write(in RuntimeLogRecord record, RuntimeLogLevels levels);
}

/// <summary>Per-provider level field shared by every call site of that provider, so elevation reaches handles created earlier.</summary>
internal sealed class RuntimeLogGate
{
    internal RuntimeLogGate(RuntimeLogLevel level) { Level = level; }
    internal RuntimeLogLevel Level { get; set; }
    internal bool IsEnabled(RuntimeLogLevel level) => level <= Level;
    /// <summary>The record-point form: `Off` names no level a record can be written at, so it is never enabled.</summary>
    internal bool Writes(RuntimeLogLevel level) => level > RuntimeLogLevel.Off && level <= Level;
}
