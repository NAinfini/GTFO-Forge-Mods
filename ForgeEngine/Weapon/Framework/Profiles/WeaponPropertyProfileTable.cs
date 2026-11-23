using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The named profile values a weapon project exported. No native block id or display-name lookup
/// is accepted as a substitute for this resource identity.</summary>
internal sealed class WeaponPropertyProfileTable
{
    private static readonly string[] Fields = { "Archetype.ShotDelay", "Archetype.HipFireSpread", "Archetype.AimSpread",
        "Archetype.ShotgunConeSize", "Recoil.horizontalScale.Min", "Recoil.verticalScale.Min", "Recoil.spring", "Recoil.power.Min" };
    private sealed record Entry(string Revision, string Name, WeaponProfileReading Reading);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public int Count => _entries.Count;
    public WeaponProfileReading? Reading(string id) => _entries.TryGetValue(id, out var entry) ? entry.Reading : null;
    public string? Revision(string id) => _entries.TryGetValue(id, out var entry) ? entry.Revision : null;
    public string? Name(string id) => _entries.TryGetValue(id, out var entry) ? entry.Name : null;
    public ResourceRef? Resolve(string id) => _entries.ContainsKey(id) ? new ResourceRef("profile", id) : null;
    public IReadOnlyList<ResourceRef> Enumerate() => _entries.Keys.OrderBy(id => id, StringComparer.Ordinal)
        .Select(id => new ResourceRef("profile", id)).ToArray();

    /// <summary>The `profile` kind's owner over this table: which ids the install ships and the revision each holds, so a
    /// compiled reference is checked against the file the game itself reads.</summary>
    public RuntimeResourceProvider Provider() => RuntimeResourceProvider.Of(Enumerate, Resolve, revision: Revision);

    /// <summary>The codes a refused `profile` runtime resource is reported with when ForgeEngine hands them out.</summary>
    public static readonly PublishedResourceSetCodes SetCodes = new("profile-path", "profile-unreadable", "profile-limit", "profile-duplicate-id");
    private static readonly PublishedTextCodes ReadCodes = new(SetCodes.PathCode, SetCodes.UnreadableCode, "profile-file-size", "profile-json", "profile-utf8");

    /// <summary>The profiles among <paramref name="files"/>: the install's `profile` runtime resources, handed out by ForgeEngine's
    /// one content discovery.</summary>
    public static WeaponPropertyProfileTable Load(IReadOnlyList<PublishedResource> files, Action<string> rejected)
    {
        var table = new WeaponPropertyProfileTable();
        foreach (var file in files)
        {
            if (!PublishedResources.TryRead(file, PublishedResources.MaximumResourceBytes, ReadCodes, rejected, out var text)) continue;
            if (!PublishedResources.TryResourceKind(text!, AreaFieldDocument.Format, "profile-format", "profile-kind", out var kind, out _, out _) || kind != "profile") continue;
            if (!TryRead(text!, out var id, out var entry))
            { rejected(PublishedResources.Diagnostic(file.Relative, "profile-unknown", "Invalid weapon property profile.")); continue; }
            table._entries.Add(id!, entry!);
        }
        return table;
    }

    private static bool TryRead(string text, out string? id, out Entry? entry)
    {
        id = null; entry = null;
        using var parsed = JsonDocument.Parse(text); var root = parsed.RootElement;
        if (root.EnumerateObject().Any(row => row.Name is not ("format" or "schemaVersion" or "kind" or "reference" or "payload"))
            || !ForgeCore.FormatVersion.Matches(root)
            || !root.TryGetProperty("reference", out var reference) || reference.ValueKind != JsonValueKind.Object
            || reference.EnumerateObject().Any(row => row.Name is not ("id" or "revision"))
            || !reference.TryGetProperty("id", out var identifier) || identifier.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id = identifier.GetString()) || id.Length > 512 || id.Any(char.IsWhiteSpace) || id.Contains('/') || id.Contains('\\')
            || !reference.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.String || !PublishedResources.IsSha256(revision.GetString())
            || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
            || payload.EnumerateObject().Any(row => row.Name is not ("name" or "values"))
            || !payload.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 128
            || !payload.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object || values.EnumerateObject().Count() != Fields.Length) return false;
        var numbers = new double[Fields.Length];
        for (int index = 0; index < Fields.Length; index++)
            if (!values.TryGetProperty(Fields[index], out var number) || number.ValueKind != JsonValueKind.Number
                || !number.TryGetDouble(out numbers[index]) || !double.IsFinite(numbers[index])
                || (index == 3 ? numbers[index] < int.MinValue || numbers[index] > int.MaxValue || numbers[index] != Math.Truncate(numbers[index])
                    : !number.TryGetSingle(out var native) || !float.IsFinite(native))) return false;
        var semantic = "{\"format\":\"gtfo-forge-engine-resource\",\"id\":" + JsonSerializer.Serialize(id)
            + ",\"kind\":\"profile\",\"payload\":" + RuntimeJson.Canonical(payload) + ",\"schemaVersion\":\"" + ForgeCore.FormatVersion.Current + "\"}";
        if (ForgeCore.Hash.Sha256Hex(semantic) != revision.GetString()) return false;
        entry = new Entry(revision.GetString()!, name.GetString()!, new WeaponProfileReading(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5], numbers[6], numbers[7])); return true;
    }

}
