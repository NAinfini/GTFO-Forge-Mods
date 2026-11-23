using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeWeapon.Native;

/// <summary>Which native object a Forge block belongs to, in the game's own identity: an `ItemDataBlock`
/// `persistentID`, a `PlayerOfflineGearDataBlock` `persistentID` or an archetype `persistentID`. The document
/// entry names exactly one of them and the block may name more of them; every one of them selects the same
/// entry.</summary>
internal enum WeaponFieldKey
{
    Item,
    Offline,
    Archetype
}

/// <summary>The document family `plugins/&lt;package&gt;/forge/weapons/*.forge`, read once at plugin start.
///
/// Shape: `{ "schemaVersion": "1.0.0", "weapons": [ { "&lt;key&gt;": &lt;id&gt;, "blocks": { "&lt;block id&gt;": { "&lt;path&gt;": &lt;value&gt; } } } ] }`.
/// The block ids and paths are the field table's own (`catalog/authoring/object-fields.json`, `host=weapon`) and the
/// vocabulary the applier handles is <see cref="WeaponDefinitionFields"/>; a block id or path outside it is a
/// data error, never an unknown-and-ignored key.
///
/// One document uses one key spelling for every entry it carries (`itemId`, `offlineId` or `archetypeId`), and
/// one native object is described by one entry across the whole install: two entries claiming the same
/// `(kind, id)` withdraw that object whole rather than letting scan order decide which one wins. The snapshot is
/// immutable once loaded — no watch, no reload, no second source — and a broken file is refused on its own with
/// one diagnostic carrying the first reason found.</summary>
internal static class WeaponFieldSchema
{
    internal const int MaximumDocuments = 64;
    internal const int MaximumDocumentBytes = PublishedResources.MaximumFileBytes;
    internal const int MaximumEntriesPerDocument = 256;
    internal const int MaximumBlocksPerEntry = 32;
    internal const int MaximumListItems = 64;
}

/// <summary>One parsed value. A scalar carries its own number/flag/text; a vector is three finite numbers; a list
/// is an ordered array of bags whose paths are the list's entry shape.</summary>
internal sealed class WeaponFieldValue
{
    private WeaponFieldValue(WeaponFieldKind kind) { Kind = kind; }

    internal WeaponFieldKind Kind { get; }
    internal double Number { get; private init; }
    internal long Integer { get; private init; }
    internal bool Boolean { get; private init; }
    internal string Text { get; private init; } = "";
    internal float[] Vector { get; private init; } = Array.Empty<float>();
    internal WeaponFieldBag[] Items { get; private init; } = Array.Empty<WeaponFieldBag>();

    internal static WeaponFieldValue Of(WeaponFieldKind kind, double number) => new(kind) { Number = number };
    internal static WeaponFieldValue Of(WeaponFieldKind kind, long integer) => new(kind) { Integer = integer };
    internal static WeaponFieldValue Of(bool value) => new(WeaponFieldKind.Boolean) { Boolean = value };
    internal static WeaponFieldValue Of(string text) => new(WeaponFieldKind.Text) { Text = text };
    internal static WeaponFieldValue Of(float[] vector) => new(WeaponFieldKind.Vector) { Vector = vector };
    internal static WeaponFieldValue Of(WeaponFieldBag[] items) => new(WeaponFieldKind.List) { Items = items };
}

/// <summary>A block's or list entry's fields, by their field-table path.</summary>
internal sealed class WeaponFieldBag
{
    private readonly Dictionary<string, WeaponFieldValue> _values;

    internal WeaponFieldBag(Dictionary<string, WeaponFieldValue> values) { _values = values; }

    internal IEnumerable<string> Paths => _values.Keys;

    internal bool Has(string path) => _values.ContainsKey(path);

    internal WeaponFieldValue? Value(string path) => _values.TryGetValue(path, out var value) ? value : null;

    internal double? Number(string path) => _values.TryGetValue(path, out var value) && value.Kind == WeaponFieldKind.Number ? value.Number : null;

    internal long? Integer(string path) => _values.TryGetValue(path, out var value) && value.Kind == WeaponFieldKind.Integer ? value.Integer : null;

    internal bool? Boolean(string path) => _values.TryGetValue(path, out var value) && value.Kind == WeaponFieldKind.Boolean ? value.Boolean : null;

    internal string? Text(string path) => _values.TryGetValue(path, out var value) && value.Kind == WeaponFieldKind.Text ? value.Text : null;

    internal float[]? Vector(string path) => _values.TryGetValue(path, out var value) && value.Kind == WeaponFieldKind.Vector ? value.Vector : null;

    internal WeaponFieldBag[]? Items(string path) => _values.TryGetValue(path, out var value) && value.Kind == WeaponFieldKind.List ? value.Items : null;

    /// <summary>A float field, narrowed from the document's double with the one spelling the field table
    /// declares; the game's own members are `float`.</summary>
    internal float? Single(string path)
    {
        var number = Number(path);
        return number == null ? null : (float)number.Value;
    }
}

/// <summary>One block of one entry: the block id and its fields.</summary>
internal sealed class WeaponFieldBlock
{
    internal WeaponFieldBlock(string blockId, WeaponFieldBag values) { BlockId = blockId; Values = values; }

    internal string BlockId { get; }
    internal WeaponFieldBag Values { get; }
}

/// <summary>One document entry: the object key it was written under plus every Forge block of that object.</summary>
internal sealed class WeaponFieldEntry
{
    internal WeaponFieldEntry(WeaponFieldKey key, uint id, Dictionary<string, WeaponFieldBlock> blocks, string source, string? fixedSlot = null)
    {
        Key = key; Id = id; Blocks = blocks; Source = source; FixedSlot = fixedSlot;
    }

    internal WeaponFieldKey Key { get; }
    internal uint Id { get; }
    internal string Source { get; }
    internal string? FixedSlot { get; }
    private Dictionary<string, WeaponFieldBlock> Blocks { get; }

    internal bool TryBlock(string blockId, out WeaponFieldBlock block) => Blocks.TryGetValue(blockId, out block!);
}

/// <summary>The immutable startup snapshot: every accepted entry by every selector it claims.</summary>
internal sealed class WeaponFieldSet
{
    internal static readonly WeaponFieldSet Empty = new(Array.Empty<WeaponFieldEntry>(), new Dictionary<(WeaponFieldKey, uint), WeaponFieldEntry>());

    private readonly WeaponFieldEntry[] _entries;
    private readonly Dictionary<(WeaponFieldKey, uint), WeaponFieldEntry> _bySelector;

    internal WeaponFieldSet(WeaponFieldEntry[] entries, Dictionary<(WeaponFieldKey, uint), WeaponFieldEntry> bySelector)
    {
        _entries = entries; _bySelector = bySelector;
        foreach (var entry in entries)
            if (entry.TryBlock(ScannerBlockId, out _)) { HasScannerBlock = true; break; }
    }

    internal const string ScannerBlockId = "forge.vanilla.tool.enemy-scanner-tool";

    /// <summary>Whether any accepted entry carries an enemy-scanner block, decided once when the snapshot is built.
    /// A snapshot without one gives every scanner call nothing to apply.</summary>
    internal bool HasScannerBlock { get; }

    internal int EntryCount => _entries.Length;

    internal IReadOnlyList<WeaponFieldEntry> Entries => _entries;

    internal bool TryGet(WeaponFieldKey kind, uint id, out WeaponFieldEntry entry) => _bySelector.TryGetValue((kind, id), out entry!);

    internal WeaponFieldSet With(WeaponFieldEntry[] entries)
    {
        var merged = new Dictionary<(WeaponFieldKey, uint), WeaponFieldEntry>(_bySelector);
        foreach (var entry in entries)
            foreach (var selector in WeaponFieldDocuments.Selectors(entry))
                if (!merged.TryAdd(selector, entry)) throw new InvalidOperationException("Weapon field selector already claimed: " + selector);
        var all = new WeaponFieldEntry[_entries.Length + entries.Length];
        Array.Copy(_entries, all, _entries.Length); Array.Copy(entries, 0, all, _entries.Length, entries.Length);
        return new(all, merged);
    }

    internal bool TryGetItem(uint itemId, out WeaponFieldEntry entry) => TryGet(WeaponFieldKey.Item, itemId, out entry);
    internal bool TryGetOffline(uint offlineId, out WeaponFieldEntry entry) => TryGet(WeaponFieldKey.Offline, offlineId, out entry);

    internal bool TryGetEquipment(string? offlineId, bool hasGear, uint itemId, out WeaponFieldEntry entry)
    {
        entry = null!;
        if (!hasGear) return TryGetItem(itemId, out entry);
        return uint.TryParse(offlineId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id.ToString(CultureInfo.InvariantCulture) == offlineId && TryGetOffline(id, out entry);
    }
}

internal static class WeaponFieldDocuments
{
    private static readonly PublishedTextCodes Codes = new("field-path", "field-unreadable", "field-size", "field-json", "field-utf8");

    /// <summary>Every accepted document among the `forge/weapons/*.forge` files ForgeEngine's one content discovery hands this
    /// package (I-RESOLVE), ordinal sorted so one install always reads the same way.</summary>
    internal static WeaponFieldSet Load(string bepInExRoot, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        ArgumentNullException.ThrowIfNull(rejected);
        var parsed = new List<(string Relative, WeaponFieldEntry[] Entries)>();
        void Refused(PublishedResourceFailure failure) => rejected(Diagnostic(failure.Relative, failure.Code, failure.Reason));
        var discovered = ContentPackages.Read(bepInExRoot, ContentPackages.EngineReader, "forge/weapons", "*.forge", Codes,
            (Action<PublishedResourceFailure>)Refused);
        foreach (var document in discovered)
        {
            if (!PublishedResources.TryRead(document, WeaponFieldSchema.MaximumDocumentBytes, Codes,
                Refused, out var text, out _)) continue;
            if (!TryParse(text!, document.Relative, out var entries, out var reason, out var code))
            {
                rejected(Diagnostic(document.Relative, code!, reason!));
                continue;
            }
            parsed.Add((document.Relative, entries!));
        }
        // One native object has one entry for the whole install. When two or more entries claim the same
        // selector the object is withdrawn whole — the first is not kept as a winner — so which values an
        // install ends up with never depends on the order the files were found, and every claiming file is named
        // once. A block's own identity paths are selectors too: a mine written under its offline gear id is also
        // reachable by the item id its block names.
        var claims = new Dictionary<(WeaponFieldKey, uint), List<(string Relative, WeaponFieldEntry Entry)>>();
        foreach (var (relative, entries) in parsed)
            foreach (var entry in entries)
                foreach (var selector in Selectors(entry))
                {
                    if (!claims.TryGetValue(selector, out var list)) claims[selector] = list = new List<(string, WeaponFieldEntry)>();
                    if (!list.Exists(row => ReferenceEquals(row.Entry, entry))) list.Add((relative, entry));
                }
        var withdrawn = new HashSet<WeaponFieldEntry>();
        foreach (var (selector, claimants) in claims)
        {
            if (claimants.Count < 2) continue;
            // One diagnostic per claimed entry, even when several selectors are contested: the first selector
            // that withdraws an entry is the one reported, so the same file is named once.
            foreach (var (relative, entry) in claimants)
            {
                if (!withdrawn.Add(entry)) continue;
                rejected(Diagnostic(relative, "field-duplicate", "Object " + Name(selector.Item1) + " "
                    + Number(selector.Item2) + " is claimed by " + Number(claimants.Count) + " entries; the object is left unconfigured."));
            }
        }
        var accepted = new List<WeaponFieldEntry>();
        var bySelector = new Dictionary<(WeaponFieldKey, uint), WeaponFieldEntry>();
        foreach (var (_, entries) in parsed)
            foreach (var entry in entries)
            {
                if (withdrawn.Contains(entry)) continue;
                accepted.Add(entry);
                foreach (var selector in Selectors(entry)) bySelector[selector] = entry;
            }
        return new WeaponFieldSet(accepted.ToArray(), bySelector);
    }

    /// <summary>Every selector one entry answers to: the key it was written under plus every identity path its
    /// own blocks name.</summary>
    internal static IEnumerable<(WeaponFieldKey, uint)> Selectors(WeaponFieldEntry entry)
    {
        var seen = new HashSet<(WeaponFieldKey, uint)> { (entry.Key, entry.Id) };
        yield return (entry.Key, entry.Id);
        foreach (var (blockId, block) in Blocks(entry))
        {
            foreach (var (path, kind) in IdentityPaths)
            {
                if (!block.Values.Has(path) || !TryId(block.Values, path, out var id)) continue;
                if (seen.Add((kind, id))) yield return (kind, id);
            }
        }
    }

    private static IEnumerable<(string BlockId, WeaponFieldBlock Block)> Blocks(WeaponFieldEntry entry)
    {
        foreach (var blockId in WeaponDefinitionFields.BlockIds)
            if (entry.TryBlock(blockId, out var block)) yield return (blockId, block);
    }

    private static readonly (string Path, WeaponFieldKey Kind)[] IdentityPaths =
    {
        ("ArchetypeID", WeaponFieldKey.Archetype)
    };

    /// <summary>Reads one document. Everything the applier cannot use is refused by name: a version it does not
    /// read, an unknown root or entry key, two key spellings in one document, two keys on one entry, no blocks,
    /// a block id or path outside the field table, a value whose shape does not match the field table's, and any
    /// count or size over the schema's cap.</summary>
    internal static bool TryParse(string text, string source, out WeaponFieldEntry[]? entries, out string? reason, out string? code)
    {
        entries = null; reason = null; code = null;
        JsonDocument document;
        try { document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException error) { code = "field-json"; reason = error.Message; return false; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { code = "field-schema"; reason = "The file's root is not an object."; return false; }
            if (!Known(root, "schemaVersion", "weapons"))
            { code = "field-schema"; reason = "The file carries a key outside `schemaVersion` and `weapons`."; return false; }
            if (!ForgeCore.FormatVersion.Matches(root))
            { code = "field-schema"; reason = "The document must declare `schemaVersion` " + ForgeCore.FormatVersion.Current + "."; return false; }
            if (!root.TryGetProperty("weapons", out var list) || list.ValueKind != JsonValueKind.Array)
            { code = "field-schema"; reason = "The document carries no `weapons` array."; return false; }
            if (list.GetArrayLength() > WeaponFieldSchema.MaximumEntriesPerDocument)
            { code = "field-limit"; reason = "The document carries " + Number(list.GetArrayLength()) + " entries; the cap is " + Number(WeaponFieldSchema.MaximumEntriesPerDocument) + "."; return false; }
            var parsed = new List<WeaponFieldEntry>(list.GetArrayLength());
            WeaponFieldKey? documentKey = null;
            foreach (var element in list.EnumerateArray())
            {
                if (!TryEntry(element, source, ref documentKey, out var entry, out reason, out code)) return false;
                parsed.Add(entry!);
            }
            entries = parsed.ToArray();
            return true;
        }
    }

    private static bool TryEntry(JsonElement element, string source, ref WeaponFieldKey? documentKey, out WeaponFieldEntry? entry, out string? reason, out string? code)
    {
        entry = null; reason = null; code = null;
        if (element.ValueKind != JsonValueKind.Object) { code = "field-schema"; reason = "A `weapons` row is not an object."; return false; }
        WeaponFieldKey? key = null;
        uint id = 0;
        JsonElement? blocks = null;
        string? fixedSlot = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == "fixedSlot")
            {
                if (fixedSlot != null || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != "HackingTool")
                { code = "field-fixed-slot"; reason = "The fixed slot must name HackingTool exactly once."; return false; }
                fixedSlot = property.Value.GetString();
                continue;
            }
            if (string.Equals(property.Name, "blocks", StringComparison.Ordinal))
            {
                if (blocks != null) { code = "field-schema"; reason = "A `weapons` row carries `blocks` twice."; return false; }
                blocks = property.Value;
                continue;
            }
            // authorId / displayName name the work for people and logs; nothing here reads them.
            if (property.Name is "authorId" or "displayName")
            {
                if (property.Value.ValueKind != JsonValueKind.String) { code = "field-schema"; reason = "`" + property.Name + "` must be text."; return false; }
                continue;
            }
            if (!TryKey(property.Name, out var kind))
            { code = "field-key"; reason = "`" + property.Name + "` is not `itemId`, `offlineId`, `archetypeId`, `fixedSlot` or `blocks`."; return false; }
            if (key != null) { code = "field-key"; reason = "A `weapons` row carries more than one object key."; return false; }
            if (!TryId(property.Value, out var value))
            { code = "field-key"; reason = "`" + property.Name + "` is not a positive object id."; return false; }
            key = kind; id = value;
        }
        if (key == null) { code = "field-key"; reason = "A `weapons` row carries no object key."; return false; }
        if (blocks == null) { code = "field-blocks"; reason = "A `weapons` row carries no `blocks` object."; return false; }
        // One document, one key spelling: the same install then always resolves the same way, and a file that
        // mixed spellings could mean two different objects for one entry depending on the reader.
        if (documentKey == null) documentKey = key;
        else if (documentKey != key)
        {
            code = "field-key-mixed";
            reason = "This file writes " + Name(documentKey.Value) + " entries but this row writes " + Name(key.Value) + ".";
            return false;
        }
        var parsedBlocks = new Dictionary<string, WeaponFieldBlock>(StringComparer.Ordinal);
        foreach (var property in blocks.Value.EnumerateObject())
        {
            if (!WeaponDefinitionFields.TryBlock(property.Name, out var spec))
            { code = "field-block"; reason = "`" + property.Name + "` is not a Forge block this applier reads."; return false; }
            if (!TryBag(property.Value, spec.Fields, out var bag, out reason, out code)) return false;
            parsedBlocks.Add(property.Name, new WeaponFieldBlock(property.Name, bag!));
        }
        if (parsedBlocks.Count == 0 && fixedSlot == null) { code = "field-blocks"; reason = "The row's `blocks` object is empty."; return false; }
        if (parsedBlocks.Count > WeaponFieldSchema.MaximumBlocksPerEntry)
        { code = "field-limit"; reason = "The row carries " + Number(parsedBlocks.Count) + " blocks; the cap is " + Number(WeaponFieldSchema.MaximumBlocksPerEntry) + "."; return false; }
        if (fixedSlot != null && (key != WeaponFieldKey.Item || id == 53))
        { code = "field-fixed-slot"; reason = "HackingTool must select its authored itemId, not the default tool or an offline gear."; return false; }
        // Two spellings of the same native target in one entry: the mine's own `FoamData` list and the
        // `forge.etc.MineFoam` block describe the same detonation payload, and letting one silently win would
        // make the applied value depend on which key the writer happened to emit.
        if (!Conflict(parsedBlocks, "forge.etc.MineDeployer", "FoamData", "forge.etc.MineFoam", out reason)
            || !Conflict(parsedBlocks, "forge.etc.MineDeployer", "BeamData", "forge.etc.MineBeam", out reason)
            || !Conflict(parsedBlocks, "forge.etc.MineDeployer", "LightData", "forge.etc.MineLight", out reason))
        { code = "field-conflict"; return false; }
        // An identity path the entry already answers by has to agree with the key the entry was written under,
        // so one entry can never select two different objects by two spellings of the same number.
        foreach (var (blockId, block) in Blocks(parsedBlocks))
            foreach (var (path, _) in IdentityPaths)
            {
                if (!block.Values.Has(path)) continue;
                if (!TryId(block.Values, path, out _))
                { code = "field-type"; reason = "`" + blockId + "." + path + "` is not a positive object id."; return false; }
            }
        entry = new WeaponFieldEntry(key.Value, id, parsedBlocks, source, fixedSlot);
        return true;
    }

    private static IEnumerable<(string BlockId, WeaponFieldBlock Block)> Blocks(Dictionary<string, WeaponFieldBlock> blocks)
    {
        foreach (var pair in blocks) yield return (pair.Key, pair.Value);
    }

    private static bool Conflict(Dictionary<string, WeaponFieldBlock> blocks, string listBlock, string listPath, string scalarBlock, out string? reason)
    {
        reason = null;
        if (!blocks.TryGetValue(listBlock, out var container) || !container.Values.Has(listPath)) return true;
        if (!blocks.ContainsKey(scalarBlock)) return true;
        reason = "`" + listBlock + "." + listPath + "` and the `" + scalarBlock + "` block describe the same native payload; write one of them.";
        return false;
    }

    private static bool TryBag(JsonElement element, WeaponDefinitionFields.Spec[] fields, out WeaponFieldBag? bag, out string? reason, out string? code)
    {
        bag = null; reason = null; code = null;
        if (element.ValueKind != JsonValueKind.Object) { code = "field-type"; reason = "A block or list entry is not an object."; return false; }
        var values = new Dictionary<string, WeaponFieldValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!TryField(fields, property.Name, out var spec))
            { code = "field-path"; reason = "`" + property.Name + "` is not a path of this block in the field table."; return false; }
            if (!TryValue(property.Value, spec, out var value, out reason, out code)) return false;
            values.Add(property.Name, value!);
        }
        bag = new WeaponFieldBag(values);
        return true;
    }

    private static bool TryValue(JsonElement element, WeaponDefinitionFields.Spec spec, out WeaponFieldValue? value, out string? reason, out string? code)
    {
        value = null; reason = null; code = null;
        switch (spec.Kind)
        {
            case WeaponFieldKind.Number:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number) || !double.IsFinite(number))
                { code = "field-type"; reason = "`" + spec.Path + "` is not a finite number."; return false; }
                // Scalar numbers ultimately reach Single members, through Single() or a domain projection.
                // Keep the game's normal rounding and underflow, but never let a finite double become infinity.
                if (!float.IsFinite((float)number))
                { code = "field-range"; reason = "`" + spec.Path + "` cannot be stored as a finite Single number."; return false; }
                value = WeaponFieldValue.Of(WeaponFieldKind.Number, number);
                return true;
            case WeaponFieldKind.Integer:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var integer))
                { code = "field-type"; reason = "`" + spec.Path + "` is not an integer."; return false; }
                if (spec.Minimum is { } integerMinimum && integer < integerMinimum)
                { code = "field-range"; reason = "`" + spec.Path + "` is below the field table's minimum " + integerMinimum.ToString("R", CultureInfo.InvariantCulture) + "."; return false; }
                if (spec.Maximum is { } integerMaximum && integer > integerMaximum)
                { code = "field-range"; reason = "`" + spec.Path + "` is above the field table's maximum " + integerMaximum.ToString("R", CultureInfo.InvariantCulture) + "."; return false; }
                value = WeaponFieldValue.Of(WeaponFieldKind.Integer, integer);
                return true;
            case WeaponFieldKind.Boolean:
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { code = "field-type"; reason = "`" + spec.Path + "` is not a boolean."; return false; }
                value = WeaponFieldValue.Of(element.GetBoolean());
                return true;
            case WeaponFieldKind.Text:
                if (element.ValueKind != JsonValueKind.String)
                { code = "field-type"; reason = "`" + spec.Path + "` is not text."; return false; }
                value = WeaponFieldValue.Of(element.GetString() ?? "");
                return true;
            case WeaponFieldKind.Reference:
                if (!TryId(element, out var reference))
                { code = "field-type"; reason = "`" + spec.Path + "` is not a positive object id."; return false; }
                // A reference is kept as its canonical decimal text, which is the one spelling the catalog, the
                // plan files and the game's own `persistentID` all use.
                value = WeaponFieldValue.Of(reference.ToString(CultureInfo.InvariantCulture));
                return true;
            case WeaponFieldKind.Vector:
                if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
                { code = "field-type"; reason = "`" + spec.Path + "` is not three numbers."; return false; }
                var axes = new float[3];
                var axis = 0;
                foreach (var component in element.EnumerateArray())
                {
                    if (component.ValueKind != JsonValueKind.Number || !component.TryGetSingle(out var single) || !float.IsFinite(single))
                    { code = "field-type"; reason = "`" + spec.Path + "` carries a value that is not a finite number."; return false; }
                    axes[axis++] = single;
                }
                value = WeaponFieldValue.Of(axes);
                return true;
            case WeaponFieldKind.List:
                if (element.ValueKind != JsonValueKind.Array)
                { code = "field-type"; reason = "`" + spec.Path + "` is not an array."; return false; }
                if (element.GetArrayLength() > WeaponFieldSchema.MaximumListItems)
                { code = "field-limit"; reason = "`" + spec.Path + "` carries " + Number(element.GetArrayLength()) + " entries; the cap is " + Number(WeaponFieldSchema.MaximumListItems) + "."; return false; }
                var items = new List<WeaponFieldBag>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    if (!TryBag(item, spec.Items ?? Array.Empty<WeaponDefinitionFields.Spec>(), out var bag, out reason, out code)) return false;
                    items.Add(bag!);
                }
                value = WeaponFieldValue.Of(items.ToArray());
                return true;
            default:
                code = "field-type";
                reason = "`" + spec.Path + "` has no reader.";
                return false;
        }
    }

    private static bool TryField(WeaponDefinitionFields.Spec[] fields, string path, out WeaponDefinitionFields.Spec spec)
    {
        foreach (var candidate in fields)
        {
            if (!string.Equals(candidate.Path, path, StringComparison.Ordinal)) continue;
            spec = candidate;
            return true;
        }
        spec = null!;
        return false;
    }

    /// <summary>An object id as the game spells it: a JSON integer or its canonical decimal text, in
    /// `[1, UInt32.MaxValue]`. Another spelling (sign, leading zero, radix prefix, decimal point, surrounding
    /// space) is refused rather than parsed, so the document, the catalog and the native `persistentID` cannot
    /// disagree about which object is meant.</summary>
    private static bool TryId(JsonElement element, out uint id)
    {
        id = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetUInt32(out var number) || number == 0) return false;
            id = number;
            return true;
        }
        if (element.ValueKind != JsonValueKind.String) return false;
        return TryIdText(element.GetString(), out id);
    }

    private static bool TryId(WeaponFieldBag bag, string path, out uint id)
    {
        id = 0;
        var value = bag.Value(path);
        if (value == null) return false;
        if (value.Kind == WeaponFieldKind.Text) return TryIdText(value.Text, out id);
        if (value.Kind == WeaponFieldKind.Integer && value.Integer is > 0 and <= uint.MaxValue)
        {
            id = (uint)value.Integer;
            return true;
        }
        return false;
    }

    private static bool TryIdText(string? text, out uint id)
    {
        id = 0;
        if (text == null) return false;
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0) return false;
        if (text != value.ToString(CultureInfo.InvariantCulture)) return false;
        id = value;
        return true;
    }

    private static bool TryKey(string name, out WeaponFieldKey key)
    {
        switch (name)
        {
            case "itemId": key = WeaponFieldKey.Item; return true;
            case "offlineId": key = WeaponFieldKey.Offline; return true;
            case "archetypeId": key = WeaponFieldKey.Archetype; return true;
            default: key = default; return false;
        }
    }

    private static string Name(WeaponFieldKey key) => key switch
    {
        WeaponFieldKey.Item => "itemId",
        WeaponFieldKey.Offline => "offlineId",
        _ => "archetypeId"
    };

    private static bool Known(JsonElement element, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            var known = false;
            foreach (var name in allowed) if (string.Equals(property.Name, name, StringComparison.Ordinal)) { known = true; break; }
            if (!known) return false;
        }
        return true;
    }

    private static string Diagnostic(string relative, string code, string reason)
        => "weapon." + code + " file=" + relative + ": " + reason;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
}
