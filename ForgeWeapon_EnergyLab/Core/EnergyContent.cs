using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeWeaponEnergyLab;

/// <summary>The weapon definition files: every <c>*.json</c> in a <c>ForgeEnergyLab</c> folder is an array of definitions. The eight
/// shipped guns are one such file; they register exactly as a content pack's guns do.</summary>
internal static class EnergyContent
{
    internal const string FolderName = "ForgeEnergyLab";
    private static readonly string[] Fields = { "Id", "Name", "Description", "Slot", "Mode", "Model", "Tuning" };
    private static readonly string[] ModelFields = { "Key", "Bundle" };

    /// <summary>Reads and registers every definition file of <paramref name="folders"/>. A broken entry is skipped with one line in
    /// <paramref name="errors"/> naming the file, the Id, the field and the reason; the rest of its file and the other files still load.</summary>
    /// <returns>Each registered definition with the folder its file is in.</returns>
    internal static List<(WeaponDefinition Definition, string Folder)> Load(IEnumerable<string> folders, List<string> errors)
    {
        var loaded = new List<(WeaponDefinition, string)>();
        foreach (var folder in folders)
        {
            string[] files;
            try { files = Directory.GetFiles(folder, "*.json"); }
            catch (Exception error) { errors.Add(folder + ": could not list the definition files: " + error); continue; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                string json;
                try { json = File.ReadAllText(file); }
                catch (Exception error) { errors.Add(file + ": could not be read: " + error); continue; }
                foreach (var definition in Parse(file, json, errors))
                {
                    if (EnergyLab.Register(definition, out var problem)) loaded.Add((definition, folder));
                    else errors.Add(file + ": " + problem);
                }
            }
        }
        return loaded;
    }

    /// <summary>The valid definitions of one file's text; <paramref name="file"/> gives the folder that <c>Model.Bundle</c> is relative to.</summary>
    internal static List<WeaponDefinition> Parse(string file, string json, List<string> errors)
    {
        var result = new List<WeaponDefinition>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException error) { errors.Add($"{file}: invalid JSON at line {error.LineNumber + 1}, byte {error.BytePositionInLine + 1}."); return result; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { errors.Add(file + ": expected an array of weapon definitions."); return result; }
            var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var label = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()! : "[" + index + "]";
                index++;
                try { result.Add(ParseOne(item, folder)); }
                catch (ForgeCore.StrictJsonException error) { errors.Add($"{file}: {label}{(error.Field.Length == 0 ? "" : "." + error.Field)}: {error.Message}"); }
            }
        }
        return result;
    }

    private static WeaponDefinition ParseOne(JsonElement item, string folder)
    {
        ForgeCore.StrictJson.UniqueMembers(item);
        ForgeCore.StrictJson.OnlyKnown(item, "", Fields);
        var id = Text(item, "", "Id", true)!;
        var name = Text(item, "", "Name", true)!;
        var description = Text(item, "", "Description", false);
        var slot = ForgeCore.StrictJson.Enum<WeaponSlot>(item, "", "Slot");
        var mode = ForgeCore.StrictJson.Enum<EnergyMode>(item, "", "Mode");
        var tuning = EnergyConfiguration.DefaultTuning(mode);
        var model = ForgeCore.StrictJson.Object(item, "", "Model");
        ForgeCore.StrictJson.OnlyKnown(model, "Model.", ModelFields);
        var key = Text(model, "Model.", "Key", true)!;
        var bundle = Text(model, "Model.", "Bundle", true)!;
        if (item.TryGetProperty("Tuning", out var tuningItem)) tuning = EnergyConfiguration.MergeTuning(tuning, tuningItem, "Tuning");
        return new WeaponDefinition(id, name, slot, mode, tuning, new WeaponModel(key, Bundle(folder, bundle)), description);
    }

    private static string? Text(JsonElement owner, string prefix, string field, bool required)
    {
        if (!owner.TryGetProperty(field, out var found))
            return required ? throw new ForgeCore.StrictJsonException(prefix + field, "is required.") : null;
        return found.ValueKind == JsonValueKind.String ? found.GetString() : throw new ForgeCore.StrictJsonException(prefix + field, "must be a string.");
    }

    // A bundle stays inside the folder of the file that names it: nothing absolute and no '..' step.
    private static string Bundle(string folder, string bundle)
    {
        const string Field = "Model.Bundle";
        if (string.IsNullOrWhiteSpace(bundle) || Path.IsPathRooted(bundle) || bundle.Split('/', '\\').Contains(".."))
            throw new ForgeCore.StrictJsonException(Field, "must be a file path inside the definition's folder (not absolute, no '..').");
        try { return Path.GetFullPath(Path.Combine(folder, bundle)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ForgeCore.StrictJsonException(Field, "is not a usable path: " + error.Message);
        }
    }
}
