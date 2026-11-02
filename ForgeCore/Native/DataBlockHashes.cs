using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ForgeCore;

/// <summary>Content hashes of the game data blocks the game has loaded, one per block type, read from memory: so a data block another mod
/// changed shows up here, and two machines that loaded the same data give the same hashes. Main thread only; reading every type takes a
/// moment, so do it once per level, not per frame.</summary>
public static class DataBlockHashes
{
    /// <summary>The hash of every loaded data block type, keyed by the type's name and ordered by it. A block type the game has not loaded is
    /// left out; a type that cannot be read is reported through <paramref name="unreadable"/> by name and left out of the result, never
    /// hashed as if empty.</summary>
    public static SortedDictionary<string, string> ByType(out List<string> unreadable)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        unreadable = new List<string>();
        foreach (var type in BlockTypes())
        {
            try
            {
                var closed = typeof(GameData.GameDataBlockBase<>).MakeGenericType(type);
                var loaded = closed.GetMethod("IsLoaded", BindingFlags.Public | BindingFlags.Static);
                if (loaded != null && loaded.Invoke(null, null) is bool isLoaded && !isLoaded) continue;
                var all = closed.GetMethod("GetAllBlocks", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as System.Collections.IEnumerable;
                if (all == null) continue;
                var entries = new List<(uint Id, string Hash)>();
                var cut = false;
                foreach (var block in all)
                {
                    if (block == null) continue;
                    var id = block.GetType().GetProperty("persistentID")?.GetValue(block) is uint u ? u : 0u;
                    entries.Add((id, ContentHash.Of(block, out var truncated)));
                    cut |= truncated;
                }
                if (cut) { unreadable.Add(type.Name + " (too deep)"); continue; }
                entries.Sort((x, y) => x.Id != y.Id ? x.Id.CompareTo(y.Id) : string.CompareOrdinal(x.Hash, y.Hash));
                var fingerprint = new Fingerprint();
                foreach (var (id, hash) in entries) fingerprint.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture), hash);
                result[type.Name] = fingerprint.Add("count", entries.Count).Hex();
            }
            catch (Exception error) { unreadable.Add(type.Name + " (" + error.GetType().Name + ")"); }
        }
        return result;
    }

    private static IEnumerable<Type> BlockTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name ?? "";
            if (!name.EndsWith("-ASM", StringComparison.Ordinal)) continue;
            Type[] types;
            try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException partial) { types = partial.Types.Where(t => t != null).ToArray()!; }
            foreach (var type in types)
            {
                var baseType = type.BaseType;
                if (baseType != null && baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(GameData.GameDataBlockBase<>)
                    && baseType.GetGenericArguments()[0] == type && !type.IsAbstract) yield return type;
            }
        }
    }
}