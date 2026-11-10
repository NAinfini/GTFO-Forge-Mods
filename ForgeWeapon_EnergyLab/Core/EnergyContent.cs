using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeWeaponEnergyLab;

/// <summary>The weapon definition files: every <c>*.forge</c> in a <c>ForgeEnergyLab</c> folder is an array of definitions. The eight
/// shipped guns are one such file; they register exactly as a content pack's guns do.</summary>
internal static class EnergyContent
{
    internal const string FolderName = "ForgeEnergyLab";
    private static readonly string[] Fields = { "Id", "Name", "Description", "Slot", "Mode", "Model", "Tuning", "Look" };
    private static readonly string[] ModelFields = { "Key", "Bundle", "From" };

    /// <summary>Reads and registers every definition file of <paramref name="folders"/>. A broken entry is skipped with one line in
    /// <paramref name="errors"/> naming the file, the Id, the field and the reason; the rest of its file and the other files still load.
    /// A <c>Settings.forge</c> is the rundown's rules, not a definition file: it is skipped here (and reported when it is not in <paramref name="rundownFolder"/>).</summary>
    /// <returns>Each registered definition with the folder its file is in.</returns>
    internal static List<(WeaponDefinition Definition, string Folder)> Load(IEnumerable<string> folders, List<string> errors, string? rundownFolder = null)
    {
        var items = new List<(WeaponDefinition Definition, string Folder, string File)>();
        var from = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            string[] files;
            try { files = Directory.GetFiles(folder, "*.forge"); }
            catch (Exception error) { errors.Add(folder + ": could not list the definition files: " + error); continue; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileName(file), EnergySettings.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(folder, rundownFolder, StringComparison.OrdinalIgnoreCase))
                        errors.Add(file + ": ignored; the rundown's rules are read only from the rundown's custom folder (CustomPath/" + FolderName + ").");
                    continue;
                }
                string json;
                try { json = File.ReadAllText(file); }
                catch (Exception error) { errors.Add(file + ": could not be read: " + error); continue; }
                foreach (var definition in Parse(file, json, errors, from)) items.Add((definition, folder, file));
            }
        }
        // A gun that names another gun's model (Model.From) takes it once that gun's own model is known, in whichever file it is. Plugins register after this pass, so a plugin's gun cannot be named.
        var models = new Dictionary<string, WeaponModel>(StringComparer.Ordinal);
        foreach (var item in items) if (!from.ContainsKey(item.Definition.Id)) models.TryAdd(item.Definition.Id, item.Definition.Model);
        var unresolved = new HashSet<int>(Enumerable.Range(0, items.Count).Where(i => from.ContainsKey(items[i].Definition.Id)));
        for (var progress = true; progress && unresolved.Count != 0;)
        {
            progress = false;
            foreach (var index in unresolved.ToArray())
            {
                var definition = items[index].Definition;
                if (!models.TryGetValue(from[definition.Id], out var model)) continue;
                items[index] = (definition with { Model = model }, items[index].Folder, items[index].File);
                models.TryAdd(definition.Id, model);
                unresolved.Remove(index);
                progress = true;
            }
        }
        foreach (var index in unresolved.OrderBy(i => i))
            errors.Add($"{items[index].File}: {items[index].Definition.Id}.Model.From: '{from[items[index].Definition.Id]}' is not a gun with a model of its own (a gun in a ForgeEnergyLab file; a plugin registers its guns after these files are read); this gun is skipped.");
        var loaded = new List<(WeaponDefinition, string)>();
        for (var index = 0; index < items.Count; index++)
        {
            if (unresolved.Contains(index)) continue;
            if (EnergyLab.Register(items[index].Definition, out var problem)) loaded.Add((items[index].Definition, items[index].Folder));
            else errors.Add(items[index].File + ": " + problem);
        }
        return loaded;
    }

    /// <summary>The valid definitions of one file's text; <paramref name="file"/> gives the folder that <c>Model.Bundle</c> is relative to.
    /// A definition that writes <c>Model.From</c> comes back with an empty model and its Id in <paramref name="from"/> (the model it takes);
    /// a caller without <paramref name="from"/> gets that definition refused.</summary>
    internal static List<WeaponDefinition> Parse(string file, string json, List<string> errors, Dictionary<string, string>? from = null)
    {
        var result = new List<WeaponDefinition>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, EnergyConfiguration.FileOptions); }
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
                try
                {
                    var definition = ParseOne(item, folder, out var modelFrom);
                    if (modelFrom != null && from == null) throw new ForgeCore.StrictJsonException("Model.From", "needs the other definitions it names; read the file through the folder loader.");
                    if (modelFrom != null && !from!.TryAdd(definition.Id, modelFrom)) throw new ForgeCore.StrictJsonException("Id", "is repeated in these files.");
                    result.Add(definition);
                }
                catch (ForgeCore.StrictJsonException error) { errors.Add($"{file}: {label}{(error.Field.Length == 0 ? "" : "." + error.Field)}: {error.Message}"); }
            }
        }
        return result;
    }

    private static WeaponDefinition ParseOne(JsonElement item, string folder, out string? modelFrom)
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
        modelFrom = Text(model, "Model.", "From", false);
        WeaponModel weaponModel;
        if (modelFrom != null)
        {
            if (model.TryGetProperty("Key", out _) || model.TryGetProperty("Bundle", out _))
                throw new ForgeCore.StrictJsonException("Model.From", "takes another gun's whole model; write it alone, without Key and Bundle.");
            weaponModel = new WeaponModel("", "");
        }
        else
        {
            var key = Text(model, "Model.", "Key", true)!;
            weaponModel = new WeaponModel(key, Bundle(folder, Text(model, "Model.", "Bundle", true)!));
        }
        if (item.TryGetProperty("Tuning", out var tuningItem)) tuning = EnergyConfiguration.MergeTuning(tuning, tuningItem, "Tuning");
        var look = item.TryGetProperty("Look", out var lookItem) ? EnergyLookNames.Read(ForgeCore.StrictJson.Object(item, "", "Look"), mode) : null;
        return new WeaponDefinition(id, name, slot, mode, tuning, weaponModel, description, look);
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
