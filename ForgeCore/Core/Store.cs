using System;
using System.IO;
using System.Text.Json;

namespace ForgeCore;

public enum StoreStatus { Missing, Read, Corrupt }

/// <summary>What <see cref="Store.Read{T}"/> found: nothing yet, the value, or a file it could not read (<see cref="Detail"/> says why).</summary>
public readonly record struct StoreRead<T>(StoreStatus Status, T? Value, string Detail);

/// <summary>Local progress, one JSON file per mod. Only read and write: no structure migration. A file that cannot be read is
/// reported and never overwritten.</summary>
public static class Store
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    /// <summary>The folder of the store files (set by ForgeCore's plugin to <c>BepInEx/ForgeCore/Store</c>).</summary>
    internal static string Root { get; set; } = Path.Combine(AppContext.BaseDirectory, "ForgeCoreStore");

    public static string PathOf(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A mod ID is a plugin GUID usable as a file name.", nameof(modId));
        return Path.Combine(Root, modId + ".json");
    }

    public static StoreRead<T> Read<T>(string modId)
    {
        var path = PathOf(modId);
        if (!File.Exists(path)) return new(StoreStatus.Missing, default, path);
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
            return value == null ? new(StoreStatus.Corrupt, default, path + ": empty") : new(StoreStatus.Read, value, path);
        }
        catch (Exception error) when (error is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Log.For(modId).Error("Store file " + path + " cannot be read; it is left as it is: " + error);
            return new(StoreStatus.Corrupt, default, path + ": " + error);
        }
    }

    /// <summary>Writes the whole value (to a temporary file first, then over the old one). Refuses, with an error in the mod's log,
    /// when the existing file cannot be read, so a damaged save is never replaced unseen.</summary>
    public static bool Write<T>(string modId, T value)
    {
        var path = PathOf(modId);
        try
        {
            if (File.Exists(path))
            {
                try { using var _ = JsonDocument.Parse(File.ReadAllText(path)); }
                catch (JsonException error)
                {
                    Log.For(modId).Error("Store file " + path + " is damaged; not overwritten: " + error);
                    return false;
                }
            }
            Directory.CreateDirectory(Root);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.For(modId).Error("Store file " + path + " could not be written: " + error);
            return false;
        }
    }
}
