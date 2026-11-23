using System;
using System.Collections.Generic;
using System.Linq;
namespace ForgeMap.Fields;

/// <summary>The value type one field path carries, exactly as `catalog/authoring/object-fields.json` types it. `Vector3` is
/// three finite numbers; the two list kinds are JSON arrays of whole numbers or finite numbers.</summary>
internal enum MapFieldKind { Boolean, Integer, Number, Text, Enum, Vector3, IntegerList, NumberList, NumericEnum }

/// <summary>The object one block's entry is addressed to. A block names exactly one kind, so an entry whose
/// target is another kind is refused instead of being reinterpreted. `ZoneObject` is an object the map editor
/// authors itself (a sensor, a level sentry, a zone's item spawn rule): the author's own object id, the zone it
/// belongs to and, for an object placed at a point, the room it stands in.</summary>
internal enum MapFieldTargetKind { Level, Zone, Door, Terminal, Generator, ZoneObject }

/// <summary>One field path of one block. <see cref="Members"/> is the enum set, written exactly as the catalog
/// spells it (`1 Melee` for NumericEnum, whose JSON value is the leading integer), and is empty for non-enum kinds. <see cref="Length"/> is the exact item count of a
/// list path (0 when any count reads). <see cref="Unapplied"/>
/// is the reason this one path is read but not written when the rest of its block is.</summary>
internal sealed record MapFieldPath(string Path, MapFieldKind Kind, IReadOnlyList<string> Members, int Length = 0, string? Unapplied = null)
{
    internal MapFieldPath(string path, MapFieldKind kind) : this(path, kind, Array.Empty<string>()) { }

    /// <summary>Why this path is not written: its block's reason, or its own.</summary>
    internal string? UnappliedIn(MapFieldBlock block) => block.Unapplied ?? Unapplied;
}

/// <summary>
/// One `forge.` block of the map editor: the object kind its entries are addressed to and its field paths.
/// <see cref="Unapplied"/> is null when every path of the block is written into the game, or the reason none of
/// them is; a block that is accepted but not written is reported once per document field as
/// `map.field-unapplied`, never silently dropped.
/// </summary>
internal sealed record MapFieldBlock(string Block, MapFieldTargetKind Target, IReadOnlyList<MapFieldPath> Paths,
    string? Unapplied)
{
    /// <summary>The field id the manifest and the catalog name one path of this block by.</summary>
    internal string Id(string path) => "map." + Block + "." + path;
}

/// <summary>One group of blocks, applied together by one native applier. A group is declared in its own
/// `MapFields.&lt;group&gt;.cs` and listed once in <see cref="MapFieldSchema.Groups"/>.</summary>
internal sealed record MapFieldGroup(string Name, IReadOnlyList<MapFieldBlock> Blocks);

/// <summary>
/// The map editor's `forge.` block fields this package reads from `forge/map-fields/*.forge`. Each group lives in
/// its own `MapFields.&lt;group&gt;.cs`; this file merges them. <see cref="MapDefinitionFields"/> — every path whose
/// block and own <see cref="MapFieldPath.Unapplied"/> are both null — is the one list the registration declares,
/// so a field is declared exactly when an applier writes it.
/// </summary>
internal static partial class MapFieldSchema
{
    /// <summary>Every group this build reads. A new group adds its file and one entry here. Each group is built by
    /// a method rather than read from a field of another file, because the order static fields of a partial class
    /// initialize in across files is not defined.</summary>
    internal static IReadOnlyList<MapFieldGroup> Groups { get; } = new[] { DoorBuild(), GlobalWave(), SystemObjects() };

    /// <summary>Every accepted block by its `forge.` id. A block id two groups both declare is a build defect,
    /// so the table refuses to build instead of letting one group shadow the other.</summary>
    internal static IReadOnlyDictionary<string, MapFieldBlock> Blocks { get; } = Merge();

    /// <summary>The field ids this package writes into the game, in ordinal order: the only declaration source.</summary>
    internal static IReadOnlyList<string> MapDefinitionFields { get; } = Blocks.Values
        .SelectMany(block => block.Paths.Where(path => path.UnappliedIn(block) == null).Select(path => block.Id(path.Path)))
        .OrderBy(id => id, StringComparer.Ordinal).ToArray();

    private static IReadOnlyDictionary<string, MapFieldBlock> Merge()
    {
        var blocks = new Dictionary<string, MapFieldBlock>(StringComparer.Ordinal);
        foreach (var group in Groups)
            foreach (var block in group.Blocks)
                if (!blocks.TryAdd(block.Block, block))
                    throw new InvalidOperationException("Map field block " + block.Block + " is declared by two groups.");
        return blocks;
    }
}
