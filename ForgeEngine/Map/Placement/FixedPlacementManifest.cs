using ForgeCore;
using System;
using System.Globalization;
using System.Text.Json;

namespace ForgeMap;


/// <summary>
/// The package documents that carry a level's fixed placements:
/// `plugins/&lt;package&gt;/forge/fixed-items.forge`, `plugins/&lt;package&gt;/forge/fixed-enemies.forge`,
/// `plugins/&lt;package&gt;/forge/fixed-sacks.forge`, `plugins/&lt;package&gt;/forge/dimension-landings.forge`,
/// `plugins/&lt;package&gt;/forge/bulkhead-controllers.forge` and
/// `plugins/&lt;package&gt;/forge/bulkhead-keys.forge`, the
/// same one-level package directory rule plans and the other authored content files use. All six carry the same
/// common row shape — an id, a level, a room locator and a prefab-root-local point with a yaw — and differ in what
/// they place. A controller row names the controller it moves instead of what it builds, and a key row names the
/// layer the key it moves generated on, because the level's own generator builds both and this family only puts them
/// at the authored points.
///
/// The documents are read row by row rather than all-or-nothing: a row this build cannot read is refused with its
/// own code and left out, because the placements of one level are independent of each other and an author fixing
/// one row must not have to fix every other row of the file first. The document's own shape — its version, its
/// array and the fields at its root — is still all-or-nothing, because a document whose root does not read has no
/// rows to keep. Reading the files off disk is the native half's job; this half is their one grammar.
/// </summary>
internal static partial class FixedPlacementManifest
{
    private const int MaximumDoorAddressChars = 128;

    /// <summary>The five refusals of a document's root, one fixed registered code each per document kind.</summary>
    private sealed record RootCodes(string Json, string Field, string Schema, string Rows, string Limit);

    private static readonly RootCodes ItemRootCodes = new("fixed-item-json", "fixed-item-field", "fixed-item-schema", "fixed-item-rows", "fixed-item-limit");
    private static readonly RootCodes EnemyRootCodes = new("fixed-enemy-json", "fixed-enemy-field", "fixed-enemy-schema", "fixed-enemy-rows", "fixed-enemy-limit");
    private static readonly RootCodes SackRootCodes = new("fixed-sack-json", "fixed-sack-field", "fixed-sack-schema", "fixed-sack-rows", "fixed-sack-limit");
    private static readonly RootCodes LandingRootCodes = new("dimension-landing-json", "dimension-landing-field", "dimension-landing-schema", "dimension-landing-rows", "dimension-landing-limit");
    private static readonly RootCodes ControllerRootCodes = new("fixed-controller-json", "fixed-controller-field", "fixed-controller-schema", "fixed-controller-rows", "fixed-controller-limit");
    private static readonly RootCodes BulkheadKeyRootCodes = new("fixed-bulkhead-key-json", "fixed-bulkhead-key-field", "fixed-bulkhead-key-schema", "fixed-bulkhead-key-rows", "fixed-bulkhead-key-limit");

    /// <summary>Reads one document's root and its row array: the version, the one array field, and the cap every
    /// document of this family carries. Everything here is all-or-nothing, because a root that does not read has
    /// no rows whose own refusals could be told apart. The document is handed back to the caller, which reads the
    /// rows while it is alive and disposes it when it is done.</summary>
    private static JsonDocument? Read(string text, RootCodes codes, string rowsField, out string? code, out string? reason)
    {
        code = null;
        reason = null;
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(text); }
        catch (JsonException error) { code = codes.Json; reason = error.Message; return null; }
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        { code = codes.Json; reason = "The document's root is not an object."; parsed.Dispose(); return null; }
        if (!DocumentReading.Known(root, out var unknown, "schemaVersion", rowsField))
        { code = codes.Field; reason = "Unknown field `" + unknown + "`."; parsed.Dispose(); return null; }
        if (!FormatVersion.Matches(root))
        { code = codes.Schema; reason = "The document must declare schemaVersion " + FormatVersion.Current + "."; parsed.Dispose(); return null; }
        if (!root.TryGetProperty(rowsField, out var rows) || rows.ValueKind != JsonValueKind.Array)
        { code = codes.Rows; reason = "The document carries no `" + rowsField + "` array."; parsed.Dispose(); return null; }
        if (rows.GetArrayLength() > FixedPlacement.MaximumRows)
        {
            code = codes.Limit;
            reason = "The document carries " + rows.GetArrayLength() + " rows; the cap is " + FixedPlacement.MaximumRows + ".";
            parsed.Dispose();
            return null;
        }
        return parsed;
    }

    /// <summary>Reads the `locator` field every row of every document of this family carries, so the readers
    /// cannot drift into two answers to which room a row stands in.</summary>
    private static bool Locator(JsonElement row, out RoomLocator? locator, out string? reason)
    {
        locator = null;
        reason = null;
        if (!row.TryGetProperty("locator", out var element))
        { reason = "A row carries the room it stands in; a room-local pose without a room cannot be placed."; return false; }
        if (!RoomLocator.TryParse(element, out locator, out _, out var locatorReason))
        { reason = locatorReason!; return false; }
        return true;
    }

    /// <summary>Whether an id carries the document's own prefix and a non-empty body after it. The prefix is what
    /// tells an item row from an enemy row in a log line, and a row whose id is not its own document's kind is a
    /// row written into the wrong file.</summary>
    private static bool Prefixed(string id, string prefix)
        => id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length && id.Trim() == id;

    private static string Row(int index, string reason)
        => "row " + index.ToString(CultureInfo.InvariantCulture) + ": " + reason;
}
