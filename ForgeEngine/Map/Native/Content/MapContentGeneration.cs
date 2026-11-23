using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>One content generation's Map content (contract I-RELOAD tiers B and C): the packages it was admitted from, the prepared
/// packages whose model registrations it committed, its Rundown menu and native rows, the publication that gives its sidecars
/// authority, and the accepted sidecar rows the Map halves hold while it is the committed generation.</summary>
internal sealed class MapContentGeneration
{
    internal MapContentGeneration(MapPackageAdmission packages, MapPreparedPackages prepared, RundownMenu menu, MapRundownRows rows)
    { Packages = packages; Prepared = prepared; Menu = menu; Rows = rows; }

    internal MapPackageAdmission Packages { get; }
    /// <summary>The prepared packages whose model and carry registrations this generation holds (a redo keeps the previous one's).</summary>
    internal MapPreparedPackages Prepared { get; set; }
    internal RundownMenu Menu { get; }
    internal MapRundownRows Rows { get; }
    internal RundownPlacementAdmission Published { get; set; } = null!;
    internal MapPreparedContent Accepted { get; set; } = null!;
    /// <summary>A redo kept the previous generation's sidecar halves as they were (nothing of them was lost).</summary>
    internal bool KeptSidecars { get; set; }

    /// <summary>Takes this generation's model and carry registrations out again (a commit that did not stand); the registrations they
    /// replaced become active again.</summary>
    internal void RollbackModels()
    {
        var failures = new List<Exception>();
        var content = Prepared.Content;
        for (int index = content.CarryAssemblies.Count - 1; index >= 0; index--)
            try { content.CarryAssemblies[index].Rollback(); } catch (Exception error) { failures.Add(error); }
        for (int index = content.ModelAssemblies.Count - 1; index >= 0; index--)
            try { content.ModelAssemblies[index].Rollback(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Map model registrations could not be taken back.", failures);
    }
}

/// <summary>The native rows of one generation's published Rundowns (contract I-RELOAD tier C), written by one table transaction per
/// package under the Map owner: a row the table still holds with the same definition stays as it is, a changed row gets its new object
/// in the same slot, a new row (or one the game lost when it rebuilt its tables) is added, the package's own rows of the previous
/// generation that it no longer publishes leave in the same transaction (a row whose number changed keeps its name), and a row of the
/// previous generation that no published package has any more is removed. The transactions hold the replaced and removed objects until
/// <see cref="Release"/> (the generation stands) or <see cref="Revert"/> (it does not: every row is back as the previous generation
/// had it).</summary>
internal sealed class MapRundownRows
{
    internal const string Owner = MapHost.ModId;
    private static readonly IReadOnlyDictionary<(string Type, uint Id), (string Package, string Json)> None
        = new Dictionary<(string, uint), (string, string)>();
    private readonly IReadOnlyDictionary<(string Type, uint Id), (string Package, string Json)> _previous;
    private readonly Dictionary<(string Type, uint Id), (string Package, string Json)> _rows = new();
    private readonly List<NativeDataBlockTables.Transaction> _committed = new();

    internal MapRundownRows(MapRundownRows? previous) => _previous = previous?._rows ?? None;

    /// <summary>Every row this generation's published packages hold, with its package and the definition it was written from.</summary>
    internal IReadOnlyDictionary<(string Type, uint Id), (string Package, string Json)> Rows => _rows;
    /// <summary>Rows written by this generation (added, replaced or removed), for the commit record.</summary>
    internal int Written { get; private set; }

    /// <summary>Writes one package's rows in one transaction, together with the removal of the rows the same package published in the
    /// previous generation and no longer has. A conflict or failed readback leaves the tables as they were and throws, which refuses
    /// that package alone.</summary>
    internal void Publish(string package, IReadOnlyList<NativeDataBlockRegistration> rows)
    {
        var transaction = new NativeDataBlockTables.Transaction(Owner);
        int steps = 0;
        var publishing = rows.Select(row => (row.Type, row.PersistentId)).ToHashSet();
        foreach (var (key, was) in _previous)
        {
            if (!string.Equals(was.Package, package, StringComparison.OrdinalIgnoreCase) || publishing.Contains(key)
                || !NativeDataBlockTables.Holds(Owner, key.Type, key.Id)) continue;
            transaction.Remove(key.Type, key.Id);
            steps++;
        }
        foreach (var row in rows)
        {
            bool held = NativeDataBlockTables.Holds(Owner, row.Type, row.PersistentId);
            if (held && _previous.TryGetValue((row.Type, row.PersistentId), out var was) && was.Json == row.Json) continue;
            if (held) transaction.Replace(row); else transaction.Add(row);
            steps++;
        }
        if (steps != 0)
        {
            transaction.Commit();
            _committed.Add(transaction);
            Written += steps;
        }
        foreach (var row in rows) _rows[(row.Type, row.PersistentId)] = (package, row.Json);
    }

    /// <summary>Removes the rows the previous generation published that no package of this generation published.</summary>
    internal void RemoveStale()
    {
        var transaction = new NativeDataBlockTables.Transaction(Owner);
        int steps = 0;
        foreach (var row in NativeDataBlockTables.OwnedRows())
        {
            if (row.Owner != Owner || _rows.ContainsKey((row.Type, row.Id))) continue;
            if (!NativeDataBlockTables.Holds(Owner, row.Type, row.Id))
            {
                // The game rebuilt its tables without this row and no package publishes it any more: there is nothing to take out.
                MapLog.Contained("map-content.stale-row-lost", new InvalidOperationException(row.Type + "/" + row.Id));
                continue;
            }
            transaction.Remove(row.Type, row.Id);
            steps++;
        }
        if (steps == 0) return;
        transaction.Commit();
        _committed.Add(transaction);
        Written += steps;
    }

    /// <summary>The generation stands: the objects its transactions replaced or removed are let go.</summary>
    internal void Release()
    {
        foreach (var transaction in _committed) transaction.Release();
        _committed.Clear();
    }

    /// <summary>The generation does not stand: every transaction is undone, newest first.</summary>
    internal void Revert()
    {
        var failures = new List<Exception>();
        for (int index = _committed.Count - 1; index >= 0; index--)
            try { _committed[index].Revert(); } catch (Exception error) { failures.Add(error); }
        _committed.Clear();
        if (failures.Count != 0) throw new AggregateException("Map Rundown rows could not be reverted.", failures);
    }

    /// <summary>Takes every row this generation published out of the tables (the generation's table lease).</summary>
    internal void RevokeAll()
    {
        var transaction = new NativeDataBlockTables.Transaction(Owner);
        int steps = 0;
        foreach (var key in _rows.Keys)
            if (NativeDataBlockTables.Holds(Owner, key.Type, key.Id)) { transaction.Remove(key.Type, key.Id); steps++; }
        if (steps == 0) return;
        transaction.Commit();
        transaction.Release();
    }
}
