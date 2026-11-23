using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>The authored gear-part poses, projected out of the one Forge document family
/// (`plugins/&lt;package&gt;/forge/weapons/*.forge`, block `forge.gearPartCustomization.Block`).
///
/// The document carries the field table's own paths: one `Parts` entry per part slot, named by `PartType` (the
/// game's `eGearComponent` member) and carrying the `forge.gearPartCustomization.Transform` pose paths
/// (`Enabled`, `LocalPosition`, `Rotation`, `Scale`) plus `Children`, whose entries name a descendant by
/// `ChildName` and carry the same paths. `AddedParts` entries describe the parts the author added on top of the
/// item: the same pose paths plus `Name`, `PartType`, `PartID`, `Variant` and `ParentType`. The document loader
/// has already refused an unknown block, an unknown path and a value of the wrong shape; what is left here is
/// the native vocabulary and hierarchy limits, and an object that breaks one of those is refused whole with
/// one diagnostic naming the object and the reason.
///
/// The data is presentation only: it names a native transform and the absolute local values to write onto it.
/// There is no watch, no reload and no second source, and the snapshot is immutable once projected.</summary>
internal static class GearPartTransformData
{
    /// <summary>Segments in one child path counted from the part down through every parent segment: nesting may
    /// not buy extra depth, so the limit is the finished path's length, not each node's own one.</summary>
    internal const int MaximumPathSegments = 8;
    /// <summary>Part entries in one object, the document's own list cap.</summary>
    internal const int MaximumParts = WeaponFieldSchema.MaximumListItems;
    /// <summary>Parts the author added to one object. The website refuses to export more, and a document that
    /// carries more is refused whole rather than partially applied.</summary>
    internal const int MaximumAddedParts = 16;
    /// <summary>Longest name one added part may carry; it names the copy of the model under its parent.</summary>
    internal const int MaximumAddedPartNameLength = 32;
    /// <summary>Longest tool-part variant name; the game's own names are a few words at most.</summary>
    private const int MaximumVariantLength = 64;

    private const string BlockId = "forge.gearPartCustomization.Block";

    /// <summary>Projects every accepted `forge.gearPartCustomization.Block` block onto the pose model, keyed by
    /// the object's own offline gear id or ItemDataBlock id. Item bodies occupy the native BaseItem component;
    /// their visual offset never changes gameplay transforms.</summary>
    internal static GearPartTransformSnapshot Project(WeaponFieldSet set, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(rejected);
        var blocks = new Dictionary<string, GearPartTransformBlock>(StringComparer.Ordinal);
        var items = new Dictionary<uint, GearPartTransform>();
        foreach (var entry in set.Entries)
        {
            if (!entry.TryBlock(BlockId, out var block)) continue;
            if (entry.Key is not (WeaponFieldKey.Offline or WeaponFieldKey.Item))
            {
                rejected("weapon.gear-part-pose file=" + entry.Source + ": the block is not written under an offline gear id, so no holder can be located.");
                continue;
            }
            var key = entry.Id.ToString(CultureInfo.InvariantCulture);
            if (!TryProject(block, key, out var projected, out var reason))
            {
                rejected("weapon.gear-part-pose file=" + entry.Source + " block=" + key + ": " + reason);
                continue;
            }
            if (entry.Key == WeaponFieldKey.Item)
            {
                var parts = projected!.Parts;
                if (parts.Length != 1 || parts[0].Slot != GearPartSlot.BaseItem || parts[0].Children.Length != 0 || projected.Added.Length != 0
                    || (parts[0].Scale is Vector3 scale && (scale.x != scale.y || scale.x != scale.z)))
                {
                    rejected("weapon.item-appearance file=" + entry.Source + ": items require one BaseItem visual pose, uniform scale, no child edits or added parts.");
                    continue;
                }
                var pose = parts[0];
                if (pose.Enabled is null && pose.Position is null && pose.EulerAngles is null && pose.Scale is null) continue;
                items[entry.Id] = pose;
            }
            else if (Array.Exists(projected!.Parts, part => part.Slot == GearPartSlot.BaseItem))
                rejected("weapon.gear-part-pose file=" + entry.Source + ": BaseItem visual poses require itemId.");
            else blocks[key] = projected!;
        }
        return new GearPartTransformSnapshot(blocks, items);
    }

    private static bool TryProject(WeaponFieldBlock block, string key, out GearPartTransformBlock? projected, out string? reason)
    {
        projected = null; reason = null;
        // `PartType` belongs to a part entry: at the block's own level it names no node, so accepting it there
        // would silently drop a value the author wrote.
        if (block.Values.Has("PartType"))
        { reason = "`PartType` names a part slot inside a `Parts` entry, not a block of its own."; return false; }
        // The added-part identity paths belong to an added-part entry: beside the list they name no model, so a
        // value written there would never reach one.
        if (block.Values.Has("PartID") || block.Values.Has("Variant") || block.Values.Has("ParentType"))
        { reason = "`PartID`, `Variant` and `ParentType` name the model of an `AddedParts` entry, not a block of their own."; return false; }
        var parts = block.Values.Items("Parts") ?? Array.Empty<WeaponFieldBag>();
        if (parts.Length > MaximumParts)
        { reason = "The object carries " + Number(parts.Length) + " parts; the cap is " + Number(MaximumParts) + "."; return false; }
        var parsed = new List<GearPartTransform>(parts.Length);
        var seen = new HashSet<GearPartSlot>();
        int index = 0;
        foreach (var part in parts)
        {
            var slotValue = part.Integer("PartType");
            if (slotValue == null || slotValue.Value <= 0 || slotValue.Value > byte.MaxValue
                || !Enum.IsDefined(typeof(GearPartSlot), (byte)slotValue.Value))
            { reason = "Part " + Number(index) + " does not name a native gear part component."; return false; }
            var slot = (GearPartSlot)(byte)slotValue.Value;
            if (!seen.Add(slot))
            { reason = "`" + slot + "` is configured more than once."; return false; }
            if (!TryPart(part, slot, out var node, out reason)) return false;
            parsed.Add(node!);
            index++;
        }
        if (!TryAddedParts(block.Values, out var added, out reason)) return false;
        projected = new GearPartTransformBlock(key, parsed.ToArray(), added!);
        return true;
    }

    /// <summary>The parts the author added on top of the item. Each one names the model it copies the way the
    /// game's own gear record does — the part component, the part block id and, for the three tool components
    /// whose block keeps one model per type, that type's name — plus the fitted part it hangs under (none = the
    /// item itself) and its absolute local pose. The names are unique inside one object, because the name is what
    /// a later spawn looks up to replace the copy an earlier one left. Nothing here is looked up in the game:
    /// whether the block and its model exist is the spawner's question, asked when a holder spawns.</summary>
    private static bool TryAddedParts(WeaponFieldBag values, out GearPartAddedPart[]? added, out string? reason)
    {
        added = null; reason = null;
        if (!values.Has("AddedParts")) { added = Array.Empty<GearPartAddedPart>(); return true; }
        var items = values.Items("AddedParts")!;
        if (items.Length > MaximumAddedParts)
        { reason = "The object carries " + Number(items.Length) + " added parts; the cap is " + Number(MaximumAddedParts) + "."; return false; }
        var parsed = new List<GearPartAddedPart>(items.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var item in items)
        {
            var name = item.Text("Name");
            if (string.IsNullOrEmpty(name) || !IsAddedPartName(name) || !names.Add(name))
            { reason = "Added part " + Number(index) + " carries no unique `Name` of 1–" + Number(MaximumAddedPartNameLength) + " letters, digits, `-` or `_`."; return false; }
            var componentValue = item.Integer("PartType");
            if (componentValue == null || componentValue.Value <= 0 || componentValue.Value > byte.MaxValue
                || !Enum.IsDefined(typeof(GearPartSlot), (byte)componentValue.Value)
                || !HasPartBlock((GearPartSlot)(byte)componentValue.Value))
            { reason = "Added part `" + name + "` does not name a part component with its own part block."; return false; }
            var component = (GearPartSlot)(byte)componentValue.Value;
            var partValue = item.Integer("PartID");
            if (partValue == null || partValue.Value <= 0 || partValue.Value > uint.MaxValue)
            { reason = "Added part `" + name + "` carries no positive `PartID`."; return false; }
            var variant = item.Text("Variant");
            // The three tool components whose block keeps one model per type must name it, and a component whose
            // block has one model must not: a value that could never be read would otherwise be written silently.
            if (HasVariants(component) != (variant != null) || (variant != null && !IsVariant(variant)))
            {
                reason = HasVariants(component)
                    ? "Added part `" + name + "` is a " + component + " and has to name its `Variant` as one type name."
                    : "Added part `" + name + "` is a " + component + ", which keeps one model and carries no `Variant`.";
                return false;
            }
            GearPartSlot? parent = null;
            var parentValue = item.Integer("ParentType");
            if (parentValue != null)
            {
                if (parentValue.Value <= 0 || parentValue.Value > byte.MaxValue
                    || !Enum.IsDefined(typeof(GearPartSlot), (byte)parentValue.Value))
                { reason = "Added part `" + name + "` hangs under a `ParentType` that is not a gear part component."; return false; }
                parent = (GearPartSlot)(byte)parentValue.Value;
            }
            if (!TryNode(item, "", component, out var pose, out reason)) return false;
            // An added part is placed by position, rotation and scale only; the two node fields it shares a reader
            // with have nothing to act on there, so a value written for them is refused instead of dropped.
            if (pose!.Enabled != null || pose.Children.Length != 0)
            { reason = "Added part `" + name + "` takes a position, rotation and scale only, not `Enabled` or `Children`."; return false; }
            parsed.Add(new GearPartAddedPart(name, component, (uint)partValue.Value, variant, parent,
                pose!.Position ?? new Vector3(0f, 0f, 0f), pose.EulerAngles ?? new Vector3(0f, 0f, 0f),
                pose.Scale ?? new Vector3(1f, 1f, 1f)));
            index++;
        }
        added = parsed.ToArray();
        return true;
    }

    /// <summary>The components whose part has its own part data block to read a model from: the sixteen a gear
    /// record names a part for. The five attachment slots are filled by the game from their parent part and have no
    /// block of their own an author could name.</summary>
    internal static bool HasPartBlock(GearPartSlot slot) => slot is GearPartSlot.FrontPart or GearPartSlot.ReceiverPart
        or GearPartSlot.StockPart or GearPartSlot.SightPart or GearPartSlot.MagPart or GearPartSlot.FlashlightPart
        or GearPartSlot.ToolMainPart or GearPartSlot.ToolGripPart or GearPartSlot.ToolDeliveryPart or GearPartSlot.ToolPayloadPart
        or GearPartSlot.ToolTargetingPart or GearPartSlot.ToolScreenPart or GearPartSlot.MeleeHeadPart or GearPartSlot.MeleeNeckPart
        or GearPartSlot.MeleeHandlePart or GearPartSlot.MeleePommelPart;

    /// <summary>The three tool components whose block keeps one model per delivery, payload or targeting type.</summary>
    internal static bool HasVariants(GearPartSlot slot)
        => slot is GearPartSlot.ToolDeliveryPart or GearPartSlot.ToolPayloadPart or GearPartSlot.ToolTargetingPart;

    private static bool IsAddedPartName(string text)
    {
        if (text.Length is 0 or > MaximumAddedPartNameLength) return false;
        foreach (var character in text)
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')) return false;
        return true;
    }

    private static bool IsVariant(string text)
    {
        if (text.Length is 0 or > MaximumVariantLength) return false;
        foreach (var character in text)
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) return false;
        return true;
    }

    /// <summary>A part entry: it has no path of its own — the part slot is its identity — so its children are the
    /// first level below the part.</summary>
    private static bool TryPart(WeaponFieldBag bag, GearPartSlot slot, out GearPartTransform? transform, out string? reason)
        => TryNode(bag, "", slot, out transform, out reason);

    /// <summary>One child node: its own `ChildName` is one or more `/`-separated names resolved against the path
    /// of the node above it, so a nested node carries its whole path from the part without the author repeating it.
    /// The depth is counted on the finished path, this node's own segments plus every segment above it, so nesting
    /// cannot push the whole path past the authored limit one level at a time.</summary>
    private static bool TryChild(WeaponFieldBag bag, string parentPath, GearPartSlot slot, out GearPartChild? child, out string? reason)
    {
        child = null; reason = null;
        if (!TryPath(parentPath, bag.Text("ChildName"), out var full))
        {
            reason = "A child path is not a relative transform path inside the part, or is deeper than "
            + Number(MaximumPathSegments) + " segments counted from the part."; return false;
        }
        if (!TryNode(bag, full!, slot, out var node, out reason)) return false;
        child = new GearPartChild(full!, node!);
        return true;
    }

    /// <summary>One node: a part entry when <paramref name="path"/> is empty, a child node otherwise. `path` is
    /// this node's own finished path from the part.</summary>
    private static bool TryNode(WeaponFieldBag bag, string path, GearPartSlot slot, out GearPartTransform? transform, out string? reason)
    {
        transform = null; reason = null;
        if (!TryVector(bag, "LocalPosition", out var position, out reason)) return false;
        if (!TryVector(bag, "Scale", out var scale, out reason)) return false;
        if (!TryVector(bag, "Rotation", out var euler, out reason)) return false;
        if (euler != null) euler = new Vector3(Normalize(euler.Value.x), Normalize(euler.Value.y), Normalize(euler.Value.z));
        var children = Array.Empty<GearPartChild>();
        if (bag.Has("Children"))
        {
            var items = bag.Items("Children")!;
            var parsed = new List<GearPartChild>(items.Length);
            foreach (var item in items)
            {
                if (!TryChild(item, path, slot, out var child, out reason)) return false;
                parsed.Add(child!);
            }
            children = parsed.ToArray();
        }
        transform = new GearPartTransform(slot, bag.Boolean("Enabled"), position, euler, scale, children);
        return true;
    }

    /// <summary>Euler angles are normalised into [0, 360): the same pose has exactly one authored form, and the
    /// number written to the transform is the number the preview shows.</summary>
    private static float Normalize(float degrees)
    {
        var wrapped = degrees % 360f;
        if (wrapped < 0f) wrapped += 360f;
        return wrapped == 360f ? 0f : wrapped;
    }

    /// <summary>A pose vector as the game's own transform takes it. The document loader already refused a value
    /// that is not three finite numbers, so this only restates the shape.</summary>
    private static bool TryVector(WeaponFieldBag bag, string path, out Vector3? value, out string? reason)
    {
        value = null; reason = null;
        var axes = bag.Vector(path);
        if (axes == null) return true;
        value = new Vector3(axes[0], axes[1], axes[2]);
        return true;
    }

    /// <summary>Every child path is one or more `/`-separated names, each non-empty, without a leading, trailing
    /// or doubled separator, a backslash, `.` or `..` (<see cref="ForgeCore.PosixPath.IsRelative"/>): the loader hands the text to the game's own hierarchy lookup,
    /// so a path that could climb out of the part is refused here instead of being attempted there. The depth is
    /// counted on the finished path, this node's own segments plus every segment above it, so a nested node
    /// cannot push the whole path past the authored limit one level at a time.</summary>
    internal static bool TryPath(string parentPath, string? path, out string? full)
    {
        full = null;
        if (string.IsNullOrEmpty(path)) return false;
        var fullPath = parentPath.Length == 0 ? path : parentPath + "/" + path;
        if (fullPath.Split('/').Length > MaximumPathSegments || !ForgeCore.PosixPath.IsRelative(fullPath)) return false;
        full = fullPath;
        return true;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One package's pose for one equipment block, keyed by the offline gear block id text, plus the parts
/// the author added on top of that block.</summary>
internal sealed class GearPartTransformBlock
{
    internal GearPartTransformBlock(string blockId, GearPartTransform[] parts, GearPartAddedPart[] added)
    { BlockId = blockId; Parts = parts; Added = added; }
    internal string BlockId { get; }
    internal GearPartTransform[] Parts { get; }
    internal GearPartAddedPart[] Added { get; }
}

/// <summary>One part the author added to the item: the model of part block `PartId` of `Component` (and, for a
/// tool part, its `Variant` type), hung under `Parent`'s fitted part — or the item itself when there is none — at
/// an absolute local pose. `Name` is the author's own name for it and is unique inside one object; it is what a
/// later spawn looks up to replace the copy an earlier one left. The part is presentation only and never enters
/// the holder's own part slots.</summary>
internal sealed class GearPartAddedPart
{
    internal GearPartAddedPart(string name, GearPartSlot component, uint partId, string? variant, GearPartSlot? parent,
        Vector3 position, Vector3 eulerAngles, Vector3 scale)
    {
        Name = name; Component = component; PartId = partId; Variant = variant; Parent = parent;
        Position = position; EulerAngles = eulerAngles; Scale = scale;
    }
    internal string Name { get; }
    internal GearPartSlot Component { get; }
    internal uint PartId { get; }
    internal string? Variant { get; }
    internal GearPartSlot? Parent { get; }
    internal Vector3 Position { get; }
    internal Vector3 EulerAngles { get; }
    internal Vector3 Scale { get; }
}

/// <summary>One part slot's pose. Values are absolute in its part frame; BaseItem uses an identity visual
/// frame under the Item root, so its values offset the existing meshes. Omitted fields keep vanilla.</summary>
internal sealed class GearPartTransform
{
    internal GearPartTransform(GearPartSlot slot, bool? enabled, Vector3? position, Vector3? eulerAngles,
        Vector3? scale, GearPartChild[] children)
    {
        Slot = slot; Enabled = enabled; Position = position; EulerAngles = eulerAngles; Scale = scale; Children = children;
    }
    internal GearPartSlot Slot { get; }
    /// <summary>Null means the document said nothing about visibility, which is not the same as "make it visible".</summary>
    internal bool? Enabled { get; }
    internal Vector3? Position { get; }
    internal Vector3? EulerAngles { get; }
    internal Vector3? Scale { get; }
    internal GearPartChild[] Children { get; }
}

/// <summary>A node below a part, either a direct child or another node's child. The path is the one the loader
/// hands to the game's own hierarchy lookup, and it is resolved against the part's transform.</summary>
internal sealed class GearPartChild
{
    internal GearPartChild(string path, GearPartTransform node) { Path = path; Node = node; }
    internal string Path { get; }
    internal GearPartTransform Node { get; }
}

/// <summary>The part slots a pose may name: BaseItem for an Item's existing visuals, and the twenty-one
/// components `GearPartHolder` exposes a `GameObject` for. Values are native `eGearComponent` members;
/// the holder applier's slot-to-property table
/// is an explicit switch over this enum and never a reflected property name. The numeric values are the build's
/// own and the layout suite checks every one of them against `Modules-ASM`; a pose names the member, so a wrong
/// number here would silently pose another slot.</summary>
internal enum GearPartSlot : byte
{
    BaseItem = 3,
    FrontPart = 12,
    FrontPartAttachmentA = 13,
    FrontPartAttachmentB = 14,
    ReceiverPart = 16,
    ReceiverPartAttachment = 17,
    StockPart = 19,
    SightPart = 21,
    MagPart = 23,
    FlashlightPart = 25,
    ToolMainPart = 27,
    ToolMainPartAttachment = 29,
    ToolGripPart = 30,
    ToolDeliveryPart = 33,
    ToolDeliveryPartAttachment = 35,
    ToolPayloadPart = 37,
    ToolTargetingPart = 40,
    ToolScreenPart = 42,
    MeleeHeadPart = 44,
    MeleeNeckPart = 46,
    MeleeHandlePart = 48,
    MeleePommelPart = 50
}

/// <summary>The immutable startup snapshot: every accepted block by its canonical id text.</summary>
internal sealed class GearPartTransformSnapshot
{
    internal static readonly GearPartTransformSnapshot Empty = new(new Dictionary<string, GearPartTransformBlock>(StringComparer.Ordinal));
    private readonly IReadOnlyDictionary<string, GearPartTransformBlock> _blocks;
    private readonly IReadOnlyDictionary<uint, GearPartTransform> _items;
    internal GearPartTransformSnapshot(IReadOnlyDictionary<string, GearPartTransformBlock> blocks,
        IReadOnlyDictionary<uint, GearPartTransform>? items = null)
    { _blocks = blocks; _items = items ?? new Dictionary<uint, GearPartTransform>(); }
    internal int BlockCount => _blocks.Count;
    internal bool HasItems => _items.Count != 0;
    internal bool TryItem(uint itemId, out GearPartTransform pose) => _items.TryGetValue(itemId, out pose!);
    internal bool TryGet(string blockId, out GearPartTransformBlock block) => _blocks.TryGetValue(blockId, out block!);
}
