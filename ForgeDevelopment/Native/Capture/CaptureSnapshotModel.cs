using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>One recorded object in a snapshot: what it is, what it is called and the field rows the snapshot asked
/// for. The type is deliberately not an entity-specific model: a door, a generator and a player all answer the same
/// shape, which is what lets one writer, one diff and one set of surface rules cover every domain.</summary>
internal sealed class CaptureRow
{
    internal CaptureRow(string kind, string identity, string name)
    {
        Kind = kind;
        Identity = identity;
        Name = name;
        Fields = new(FieldCounts.GetValueOrDefault(kind, 8));
    }

    // The largest field count a row of each kind has had, so the next row of that kind is sized once instead of
    // doubling its way up (a door row holds about a hundred fields, built again on every change sweep).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FieldCounts = new(StringComparer.Ordinal);

    internal string Kind { get; }
    /// <summary>The stable key a change record and a reader use: the object's own path or id, never its pointer.</summary>
    internal string Identity { get; }
    internal string Name { get; set; }
    internal string? Zone { get; set; }
    internal string? Group { get; set; }
    internal List<KeyValuePair<string, string?>> Fields { get; }
    internal List<KeyValuePair<string, RecProjectionValue>>? TypedFields { get; private set; }

    internal CaptureRow Value(string name, RecProjectionValue value)
    {
        (TypedFields ??= new()).Add(new(name, value));
        return this;
    }

    internal CaptureRow Field(string name, string? value)
    {
        Fields.Add(new KeyValuePair<string, string?>(name, value));
        return this;
    }

    /// <summary>The one text the diff compares and the change record carries. Reading a field twice must produce
    /// the same text, so this is built once and kept.</summary>
    internal string Signature
    {
        get
        {
            var text = new System.Text.StringBuilder(Kind.Length + Name.Length + 32);
            WriteSignature(text);
            return text.ToString();
        }
    }

    /// <summary>Appends the signature to a caller-owned buffer, so the change detector can compare an unchanged row
    /// against its remembered text without allocating a new string for every row of every sweep.</summary>
    internal void WriteSignature(System.Text.StringBuilder text)
    {
        if (FieldCounts.GetValueOrDefault(Kind) < Fields.Count) FieldCounts[Kind] = Fields.Count;
        text.Append(Kind).Append('\u001f').Append(Identity).Append('\u001f').Append(Name);
        foreach (var entry in Fields) text.Append('\u001f').Append(entry.Key).Append('=').Append(entry.Value ?? "\u0000");
    }
}

/// <summary>One named group of rows. Sections exist so the snapshot can record only what a reason asked for and so a
/// section that fails costs its own rows, not the snapshot.</summary>
internal sealed class CaptureSection
{
    internal CaptureSection(string name, string source)
    {
        Name = name;
        Source = source;
        RequiredForBudget = CaptureStateWriter.IsChangeSection(name);
    }

    internal string Name { get; }
    /// <summary>The interop source this section reads, so a report can say what produced an empty section.</summary>
    internal string Source { get; }
    /// <summary>True for the sections a per-frame change pass may not skip: the ones the change rules diff.</summary>
    internal bool RequiredForBudget { get; }
    internal List<CaptureRow> Rows { get; } = new();
}

/// <summary>One snapshot: why it was taken, what it covers, and the rows. Written to the `state` channel as one
/// record with one section per object kind.</summary>
internal sealed class CaptureSnapshot
{
    private static long _nextIdentity;
    internal long Identity { get; } = System.Threading.Interlocked.Increment(ref _nextIdentity);
    internal CaptureSnapshot(string reason, string scope, long tick)
    {
        Reason = reason;
        Scope = scope;
        Tick = tick;
    }

    internal string Reason { get; }
    /// <summary><c>world</c>, <c>session</c> or <c>datablocks</c>: which half of the capture a reason covers.</summary>
    internal string Scope { get; }
    internal long Tick { get; }
    internal long CompletedTick { get; set; }
    internal long StartedTick { get; set; }
    internal long WorldEpoch { get; set; }
    internal double MainThreadMilliseconds { get; set; }
    internal double PeakSliceMilliseconds { get; set; }
    internal long AllocatedBytes { get; set; }
    internal int CaptureFrames { get; set; }
    internal bool AcquisitionComplete { get; set; } = true;
    internal int DrainedRows { get; set; }
    internal List<CaptureSection> Sections { get; } = new();
    internal List<string> Notes { get; } = new();
    /// <summary>Sections a per-frame budget refused, so a thin snapshot says which rows it is missing.</summary>
    internal List<string> Skipped { get; } = new();
    internal ObservationTicket? Observation { get; set; }
    internal string? TerminalStatus { get; set; }
    internal string FieldCompleteness { get; set; } = "complete";
    internal string CoverageCompleteness { get; set; } = "complete";
    internal string TemporalConsistency { get; set; } = "interval";

    internal CaptureSection Section(string name, string source)
    {
        foreach (var section in Sections)
            if (string.Equals(section.Name, name, StringComparison.Ordinal)) return section;
        var added = new CaptureSection(name, source);
        Sections.Add(added);
        return added;
    }

    internal int RowCount
    {
        get
        {
            var count = DrainedRows;
            foreach (var section in Sections) count += section.Rows.Count;
            return count;
        }
    }
}

// A binding names the owner's life, never a pointer, an instance ID or a name-based search.
internal sealed record TargetBinding(string Owner, string Token, long Generation, long WorldEpoch)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Token)
        && Generation > 0 && WorldEpoch >= 0;
}

// Every field is immutable managed data. Native wrappers and deferred argument arrays cannot enter a ticket.
internal sealed record ObservationTicket(string RequestId, string Session, string ObservationId, string SpecHash,
    long SourceWorldEpoch, long CaptureGeneration, string EntryId, string InstallationId, string Signature,
    long InvocationId, long TriggerFrame, long TriggerTick, long DeadlineFrame, double DeadlineMilliseconds,
    TargetBinding Target, string Run = "", string Step = "", int PayloadBytes = 0)
{
    internal int ManagedBytes => (int)Math.Min(int.MaxValue, 512L + 2L * PayloadBytes + 2L * (RequestId.Length + Session.Length + ObservationId.Length
        + SpecHash.Length + EntryId.Length + InstallationId.Length + Signature.Length + Target.Owner.Length
        + Target.Token.Length + Run.Length + Step.Length));
}

internal readonly record struct RecProjectionValue(string Type, string Status, JsonElement Data, string Reason)
{
    private static RecProjectionValue Scalar<T>(string type, T value)
        => new(type, "value", JsonSerializer.SerializeToElement(value), "");
    internal static RecProjectionValue Boolean(bool value) => Scalar("boolean", value);
    internal static RecProjectionValue Number(double value) => double.IsFinite(value)
        ? Scalar("number", value) : Error("number", "non-finite");
    internal static RecProjectionValue Text(string value) => Scalar("string", value);
    internal static RecProjectionValue Reference(string value) => Scalar("reference", value);
    internal static RecProjectionValue Count(int value) => value >= 0
        ? Scalar("collection", value) : Error("collection", "negative-count");
    internal static RecProjectionValue Color(float r, float g, float b, float a)
        => float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b) && float.IsFinite(a)
            ? Scalar("color", new[] { r, g, b, a }) : Error("color", "non-finite");
    internal static RecProjectionValue Null(string type) => State(type, "null", "");
    internal static RecProjectionValue Error(string type, string reason) => State(type, "error", reason);
    internal static RecProjectionValue Unsupported(string type, string reason) => State(type, "unsupported", reason);
    internal static RecProjectionValue Truncated(string type, string reason) => State(type, "truncated", reason);
    private static RecProjectionValue State(string type, string status, string reason)
        => new(type, status, JsonSerializer.SerializeToElement<object?>(null), reason);
}

/// <summary>The snapshot reasons. A reason is what asked for the snapshot, and it decides the scope: the periodic and
/// hotkey reasons take everything, while a checkpoint or a level change only needs the world.</summary>
internal static class CaptureReason
{
    internal const string Manual = "manual";
    internal const string Periodic = "periodic";
    internal const string LevelEntered = "level-entered";
    internal const string LevelGenerated = "level-generated";
    internal const string CheckpointStored = "checkpoint-stored";
    internal const string CheckpointRestored = "checkpoint-restored";
    internal const string DimensionChanged = "dimension-changed";
    internal const string HostMigrated = "host-migrated";
    internal const string PlayerJoined = "player-joined";
    internal const string PlayerLeft = "player-left";
    internal const string LevelEnded = "level-ended";
    internal const string DataBlocks = "datablocks";
    internal const string Change = "change";

    internal static readonly string[] All =
    {
        Manual, Periodic, LevelEntered, LevelGenerated, CheckpointStored, CheckpointRestored, DimensionChanged,
        HostMigrated, PlayerJoined, PlayerLeft, LevelEnded, DataBlocks, Change
    };

    /// <summary>A reason the world capture answers. <c>datablocks</c> is excluded because it is exported once per
    /// level from the loaded block table, not from the scene.</summary>
    internal static bool CapturesWorld(string reason)
        => reason is not (DataBlocks or Change);

    /// <summary>A reason that also exports the loaded data blocks.</summary>
    internal static bool CapturesDataBlocks(string reason)
        => reason is Manual or Periodic or LevelEntered or LevelGenerated or LevelEnded;

    /// <summary>A reason whose snapshot is a differential record: rows it holds are the rows that changed.</summary>
    internal static bool IsChange(string reason) => reason == Change;
}
