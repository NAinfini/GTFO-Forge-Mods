using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>
/// The four choices one effect volume is placed with, in the vocabulary the volume rows declare: which of a
/// target's own values the volume writes, which direction it writes them in, the amount the volume's own arithmetic
/// is multiplied by, and the two distances it interpolates between. The first three contents are native effect
/// members; `fog` instead allocates a FogSphereAllocator without registering an effect volume. For fog, Scale is
/// density, MinRadius is zero and Modification is inflict. The two vocabularies live here because every
/// volume-placing row of every package shares them, and a second copy is exactly how one row's
/// accepted spelling drifts from another's.
///
/// The record carries no shape: every body is a native sphere.
/// </summary>
internal sealed record AreaVolumeChoices(string Contents, string Modification, double Scale, double MinRadius, double MaxRadius)
{
    /// <summary>Native effect contents, followed by a fog-only body placed through FogSphereAllocator.</summary>
    public static readonly string[] ContentsMembers = { "all", "health", "infection", "fog" };
    /// <summary>The `modification` vocabulary, which is the game's own `eEffectVolumeModification` in order.</summary>
    public static readonly string[] ModificationMembers = { "inflict", "shield" };
    /// <summary>The bound on the amount multiplier; far past any use, so a value the sphere's arithmetic cannot
    /// represent is refused rather than kept.</summary>
    public const double MaximumScale = 1000.0;
    /// <summary>The greatest finite positive radius the game's Single can store.</summary>
    public const double MaximumRadius = AreaFieldDefinition.MaximumSize;
}

/// <summary>One volume's native body as the package that placed it hands it over: the object the body is moved and
/// released through, and whether the game could also draw it. A body of null is a placement the native side
/// refused, and the ledger keeps nothing for it. `Drawn` is presentation alone: a volume whose fog sphere the
/// game's own budget refused is still a placed, committed volume, and the row that reports it says which of the
/// two happened in its own result code.</summary>
internal readonly record struct AreaVolumePlacement(object? Body, bool Drawn)
{
    /// <summary>The one refusal shape: nothing was placed.</summary>
    public static AreaVolumePlacement Refused { get; } = new(null, false);
}

/// <summary>
/// The native half of one volume placement: what a ledger cannot do without the game. It is implemented exactly
/// once, by the shared game-bound body in the host assembly, so a package that places volumes declares only its row
/// while the sphere, the fog and the manager calls live in one place.
///
/// <see cref="Place"/> either answers a body or refuses with null; an exception from the native side is treated by
/// the ledger as a refusal, and an implementation that throws must leave nothing registered, because the ledger
/// files an entry only for the body it is handed.
/// </summary>
internal interface IAreaVolumeBody
{
    AreaVolumePlacement Place(IReadOnlyList<double> position, AreaVolumeChoices choices);
    void Move(object body, IReadOnlyList<double> position);
    void Release(object body);
}

/// <summary>One volume the ledger placed and has not released: the ledger's own id, the native body, the anchor it
/// belongs to, the kernel effect handle it is filed under (null for a step that applied no effect), whether it
/// follows its anchor, and where it was last placed. <see cref="Position"/> is a read-only view of the ledger's own
/// three coordinates, so it always reads where the volume was last moved to.</summary>
internal readonly record struct AreaVolumeEntry(long Id, object Body, EntityReference Anchor, RuntimeHandle? HandleKey,
    bool Follow, IReadOnlyList<double> Position);

/// <summary>
/// The one ledger of placed effect volumes, shared by every package that places them: a row's handler files what it
/// placed here, the package's own frame pump moves the volumes that follow their anchor, and the kernel's restore
/// callback — or the world's end — releases them by handle.
///
/// The ledger owns what is left after a placement and nothing about the native calls: the body does the sphere, the
/// fog and the manager, the ledger decides which entry is still there, which one follows its anchor, and which ones
/// a handle releases. An entry is removed only after its native release returns. Failed entries remain for an
/// explicit retry, and a batch still attempts its other entries before reporting the failure.
/// </summary>
internal sealed class AreaVolumeLedger
{
    private readonly IAreaVolumeBody _body;
    /// <summary>Each entry beside the three coordinates its <see cref="AreaVolumeEntry.Position"/> views: a move
    /// writes those in place, so the per-frame pump replaces neither the entry nor its position.</summary>
    private readonly List<(AreaVolumeEntry Entry, double[] Position)> _entries = new();
    private long _nextId;

    public AreaVolumeLedger(IAreaVolumeBody body) => _body = body ?? throw new ArgumentNullException(nameof(body));

    /// <summary>How many volumes are still placed. Zero is the ordinary state of a world that placed none.</summary>
    public int Count => _entries.Count;

    /// <summary>One placed volume, by its position in the ledger. Index access rather than a copied list: the pump
    /// walks this every frame, and a snapshot per frame would be one allocation per frame for an answer the pump
    /// only reads once.</summary>
    public AreaVolumeEntry Entry(int index)
    {
        if (index < 0 || index >= _entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _entries[index].Entry;
    }

    /// <summary>
    /// One volume, placed. A position that is not three finite metres, or a body that refused or threw, answers
    /// false and files nothing; <paramref name="drawn"/> then says whether the game could also draw it. The id is
    /// the ledger's own and is what <see cref="Release(long)"/> addresses, so a caller that has to undo one
    /// placement of a batch — a receiver that changed while the volume was being registered — can name exactly it.
    /// </summary>
    public bool TryPlace(EntityReference anchor, IReadOnlyList<double> position, AreaVolumeChoices choices,
        bool follow, RuntimeHandle? handleKey, out long entryId, out bool drawn)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(choices);
        entryId = 0; drawn = false;
        if (!Finite(position)) return false;
        AreaVolumePlacement placement;
        try { placement = _body.Place(position, choices); }
        catch (Exception error) { NativeFault.Note("runtime.area-volume.place", error); return false; }
        if (placement.Body == null) return false;
        entryId = ++_nextId; drawn = placement.Drawn;
        var stored = new[] { position[0], position[1], position[2] };
        _entries.Add((new AreaVolumeEntry(entryId, placement.Body, anchor, handleKey, follow, Array.AsReadOnly(stored)), stored));
        return true;
    }

    /// <summary>One placement's end, by the id <see cref="TryPlace"/> answered with. An id the ledger no longer
    /// holds is the no-op it has to be.</summary>
    public void Release(long entryId)
    {
        ReleaseMatching(entry => entry.Id == entryId);
    }

    /// <summary>One volume, moved to where its anchor now is. A position that does not read leaves the volume where
    /// it last was rather than at an invented one; a body that refuses the move is not an error worth escaping into
    /// the caller's own per-frame pump. The ledger keeps its own copy of the position, written in place: the caller's
    /// list is read and never held, and a move allocates nothing.</summary>
    public void MoveAt(int index, IReadOnlyList<double> position)
    {
        if (index < 0 || index >= _entries.Count || !Finite(position)) return;
        var (entry, stored) = _entries[index];
        try { _body.Move(entry.Body, position); }
        catch (Exception error) { NativeFault.Note("runtime.area-volume.move", error); return; }
        stored[0] = position[0]; stored[1] = position[1]; stored[2] = position[2];
    }

    /// <summary>Every volume one kernel effect handle placed, released. Called by the kernel's own restore callback
    /// when the effect a dispatch applied ends; a null handle — a card with no `effect` block — names nothing and
    /// releases nothing, because those volumes are the module's own to keep.</summary>
    public void Release(RuntimeHandle? handleKey)
    {
        if (handleKey is not { } handle) return;
        ReleaseMatching(entry => entry.HandleKey == handle);
    }

    /// <summary>Every volume this ledger still holds, for the package that is going away. A package that unloads
    /// mid-level must not leave a volume registered with a manager that outlives it.</summary>
    public void ReleaseAll() => ReleaseMatching(_ => true);

    private void ReleaseMatching(Func<AreaVolumeEntry, bool> matches)
    {
        List<Exception>? failures = null;
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            var entry = _entries[index].Entry;
            if (!matches(entry)) continue;
            try
            {
                _body.Release(entry.Body);
                _entries.RemoveAt(index);
            }
            catch (Exception error)
            {
                NativeFault.Note("runtime.area-volume.release", error);
                (failures ??= new()).Add(error);
            }
        }
        if (failures != null) throw new AggregateException("Area volume restoration is incomplete.", failures);
    }

    private static bool Finite(IReadOnlyList<double> position)
        => position != null && position.Count == 3
            && double.IsFinite(position[0]) && double.IsFinite(position[1]) && double.IsFinite(position[2]);
}
