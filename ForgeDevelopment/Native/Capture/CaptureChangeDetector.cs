using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeDevelopment.Native;

/// <summary>What one change pass cost and what it found. The budget is enforced here rather than by the caller, so the
/// collection/comparison cost is explicit. The caller also measures deferred output in the complete frame report.</summary>
internal sealed class CaptureChangeResult
{
    internal int Rows { get; set; }
    internal int Fields { get; set; }
    internal int Added { get; set; }
    internal int Removed { get; set; }
    internal int Changed { get; set; }
    internal bool BudgetExhausted { get; set; }
    internal double Milliseconds { get; set; }
    /// <summary>Set when the sweep failed after some rows had already changed signature: those rows are in
    /// <see cref="ChangedRows"/> and must be written before the failure is reported.</summary>
    internal System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure { get; set; }
    /// <summary>Rows whose identity was not in the compared sections and is therefore new in this pass.</summary>
    internal List<CaptureRow> ChangedRows { get; } = new();
    internal void Clear()
    {
        Rows = Fields = Added = Removed = Changed = 0;
        BudgetExhausted = false; Milliseconds = 0; Failure = null; ChangedRows.Clear();
    }
}

/// <summary>
/// The per-frame change detector. It diffs the rows of the sections a change pass asked for, keyed by identity, and
/// answers only the rows whose signature moved. The budget is a row count and a stopwatch: the pass stops at whichever
/// comes first and says so, because a detector that silently drops frames is indistinguishable from a game stutter.
/// </summary>
internal sealed class CaptureChangeDetector
{
    private readonly Dictionary<string, string> _signatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _kinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _current = new(StringComparer.Ordinal);
    private readonly System.Text.StringBuilder _signature = new(512);
    private IEnumerator<CaptureRow?>? _cursor;
    private IEnumerator<KeyValuePair<string, string>>? _removals;
    private bool _seeding;
    private bool _hasBaseline;
    private bool _membershipStable;
    private long _membershipQueryFailures;
    internal void InvalidateMembership() => _membershipStable = false;
    internal bool IsScanning => _cursor != null || _removals != null;
    // Keeps a deferred write bounded independently of the number of unchanged objects inspected.
    internal const int MaximumChangedRows = 16;

    private readonly Func<string, bool?>? _present;

    internal CaptureChangeDetector(int maxRows, double maxMilliseconds, Func<string, bool?>? present = null)
    {
        _present = present;
        MaxRows = Math.Max(1, maxRows);
        MaxMilliseconds = Math.Max(0.001, maxMilliseconds);
    }

    /// <summary>Rows one pass may compare before it stops. A frame that produces more than this is a frame whose
    /// change record would cost more than the change it reports.</summary>
    internal int MaxRows { get; }

    internal double MaxMilliseconds { get; }

    /// <summary>Rows seen by the pass that seeded the detector; the first pass reports nothing because everything is
    /// new relative to nothing.</summary>
    internal int Seeded { get; private set; }

    internal CaptureChangeResult Compare(IEnumerable<CaptureSection> sections)
        => Poll(() => Rows(sections), new CaptureWorkBudget(MaxRows, MaxMilliseconds));

    private static IEnumerable<CaptureRow?> Rows(IEnumerable<CaptureSection> sections)
    {
        foreach (var section in sections) foreach (var row in section.Rows) yield return row;
    }

    /// <summary>Resumes the same sweep. A null item is collection work with no diagnostic row, such as a hierarchy
    /// node or zone-index entry. Deletion is considered after the complete source sweep, in bounded steps. Current lifecycle membership
    /// overrides inferred absence; an invalidated sweep alone cannot prove absence for unindexed families.</summary>
    internal CaptureChangeResult Poll(Func<IEnumerable<CaptureRow?>> source, CaptureWorkBudget budget, CaptureChangeResult? reuse = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(budget);
        var result = reuse ?? new CaptureChangeResult();
        result.Clear();
        try
        {
            if (!IsScanning)
            {
                _current.Clear(); _seeding = !_hasBaseline; _membershipStable = true;
                _cursor = source().GetEnumerator();
            }
            while (budget.TryStep())
            {
                if (_cursor != null)
                {
                    if (!_cursor.MoveNext())
                    {
                        _cursor.Dispose(); _cursor = null;
                        _removals = _signatures.GetEnumerator();
                        continue;
                    }
                    var row = _cursor.Current; if (row == null) continue;
                    result.Rows++; _current.Add(row.Identity);
                    // The signature is built into one reused buffer and compared in place; a new string exists only for
                    // a row that is new or really changed, so an unchanged sweep allocates nothing here.
                    _signature.Clear(); row.WriteSignature(_signature); result.Fields += row.Fields.Count + 1;
                    if (!_signatures.TryGetValue(row.Identity, out var previous))
                    {
                        _signatures[row.Identity] = _signature.ToString(); _kinds[row.Identity] = row.Kind;
                        if (!_seeding) { result.Added++; result.ChangedRows.Add(row); }
                    }
                    else if (!_signature.Equals(previous.AsSpan()))
                    {
                        _signatures[row.Identity] = _signature.ToString(); result.Changed++; result.ChangedRows.Add(row);
                    }
                }
                else if (_removals != null)
                {
                    if (!_removals.MoveNext())
                    {
                        _removals.Dispose(); _removals = null;
                        if (_seeding) Seeded = _signatures.Count;
                        _hasBaseline = true;
                        result.Milliseconds = budget.ElapsedMilliseconds;
                        return result;
                    }
                    var identity = _removals.Current.Key;
                    // The lookup is one charged step in this same budget, never an all-world rescan. A current
                    // lifecycle index can prove absence even during churn. Unknown families still require a stable
                    // complete sweep; a newly born/reused identity is protected by its current owner at deletion time.
                    bool? present;
                    try { present = _present?.Invoke(identity); }
                    catch (Exception)
                    {
                        // This identity stays tracked for a later sweep. Do not fall back to inferred absence:
                        // a failed authoritative lookup is not an unknown family. Earlier changes in this batch
                        // must still reach CaptureChangePass rather than vanish with a thrown result.
                        _membershipQueryFailures++;
                        continue;
                    }
                    if (present == false || (present == null && _membershipStable && !_current.Contains(identity)))
                    {
                        // Dictionary.Remove does not invalidate enumeration on supported .NET 6+ runtimes.
                        _signatures.Remove(identity); _kinds.TryGetValue(identity, out var kind); _kinds.Remove(identity);
                        result.Removed++; result.ChangedRows.Add(new CaptureRow(kind ?? "unknown", identity, identity).Field("present", "false"));
                    }
                }
                if (result.ChangedRows.Count >= MaximumChangedRows) break;
            }
            result.BudgetExhausted = IsScanning;
            result.Milliseconds = budget.ElapsedMilliseconds;
            return result;
        }
        catch (Exception error)
        {
            // A partial or failed enumeration is never a complete-set deletion assertion.
            _cursor?.Dispose(); _cursor = null; _removals?.Dispose(); _removals = null; _current.Clear();
            if (result.ChangedRows.Count == 0) throw;
            // Signatures of the rows already found have moved; dropping them would hide those changes for good.
            result.Failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
            result.Milliseconds = budget.ElapsedMilliseconds;
            return result;
        }
    }

    /// <summary>Initial baseline from an already collected low-frequency full snapshot. Never called each frame.</summary>
    internal void Seed(IEnumerable<CaptureSection> sections)
    {
        Reset();
        foreach (var section in sections) foreach (var row in section.Rows)
        { _signatures[row.Identity] = row.Signature; _kinds[row.Identity] = row.Kind; }
        Seeded = _signatures.Count; _hasBaseline = true;
    }

    /// <summary>Drops every remembered signature: a level change or a checkpoint restore makes the previous pass
    /// meaningless, and diffing across it would report the whole level as changed.</summary>
    internal void Reset()
    {
        _cursor?.Dispose(); _cursor = null; _removals?.Dispose(); _removals = null;
        _signatures.Clear();
        _kinds.Clear();
        _current.Clear();
        Seeded = 0; _hasBaseline = false; _membershipQueryFailures = 0;
    }

    internal string Describe()
        => "rows=" + MaxRows.ToString(CultureInfo.InvariantCulture) + " budgetMs=" + MaxMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)
            + " tracked=" + _signatures.Count.ToString(CultureInfo.InvariantCulture) + " seeded=" + Seeded.ToString(CultureInfo.InvariantCulture)
            + " membershipQueryFailures=" + _membershipQueryFailures.ToString(CultureInfo.InvariantCulture);
}
