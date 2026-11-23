using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeEngine.Framework;

/// <summary>One validated installed entry handed to its domain at startup. Identity, references and JSON are a
/// frozen startup snapshot; retaining this record is the consumer's choice. No JsonDocument lifetime escapes discovery.
/// Accepting this record means the domain consumed it, not that native game registration has completed.</summary>
public sealed class RuntimeContentEntry
{
    internal RuntimeContentEntry(string id, string domain, string name, IReadOnlyList<string> references,
        string contentJson, string path, uint persistentId, IReadOnlyDictionary<string, uint> blockIds)
    {
        Id = id; Domain = domain; Name = name; References = Array.AsReadOnly(references.ToArray());
        ContentJson = contentJson; Path = path; PersistentId = persistentId;
        BlockIds = new Dictionary<string, uint>(blockIds, StringComparer.Ordinal);
    }
    /// <summary>The issued number of every native block the entry declares, keyed by stable block id; the primary block is also <see cref="PersistentId"/>.</summary>
    public IReadOnlyDictionary<string, uint> BlockIds { get; }
    public string Id { get; }
    public string Domain { get; }
    public string Name { get; }
    public IReadOnlyList<string> References { get; }
    public string ContentJson { get; }
    public string Path { get; }
    public uint PersistentId { get; }
}

/// <summary>A domain's explicit answer to one startup entry. Native registration and its evidence remain owned by
/// the domain. A default answer is invalid and is rejected; it can never accidentally count as acceptance.</summary>
public readonly record struct ContentRegistrationResult(bool Accepted, string Code, string? Other = null)
{
    public static ContentRegistrationResult Accept() => new(true, "content-accepted");
    public static ContentRegistrationResult Reject(string code, string? other = null) => new(false, code, other);
}

/// <summary>Startup-only reference validation, shared by discovery and the defensive SDK dispatcher. Self
/// references name the entry's already-derived identity; a strongly connected group of distinct entries cannot
/// be consumed dependency-first. Both passes are iterative so the bounded file budget cannot exhaust the stack.</summary>
internal static class ContentReferenceGraph
{
    internal static IReadOnlyList<HashSet<string>> Cycles(IReadOnlyDictionary<string, IReadOnlyList<string>> references)
    {
        var edges = references.ToDictionary(row => row.Key, row => row.Value
            .Where(next => next != row.Key && references.ContainsKey(next)).Distinct(StringComparer.Ordinal)
            .OrderBy(next => next, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var inverse = references.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (id, next) in edges) foreach (var target in next) inverse[target].Add(id);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var finished = new List<string>();
        foreach (var root in references.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            var pending = new Stack<(string Id, bool Finish)>(); pending.Push((root, false));
            while (pending.TryPop(out var step))
            {
                if (step.Finish) { finished.Add(step.Id); continue; }
                if (!visited.Add(step.Id)) continue;
                pending.Push((step.Id, true));
                foreach (var target in edges[step.Id].AsEnumerable().Reverse()) if (!visited.Contains(target)) pending.Push((target, false));
            }
        }
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<HashSet<string>>();
        foreach (var root in finished.AsEnumerable().Reverse())
        {
            if (!assigned.Add(root)) continue;
            var component = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var id))
            {
                component.Add(id);
                foreach (var target in inverse[id]) if (assigned.Add(target)) pending.Push(target);
            }
            if (component.Count > 1) result.Add(component);
        }
        return result.OrderBy(component => component.OrderBy(id => id, StringComparer.Ordinal).First(), StringComparer.Ordinal).ToArray();
    }
}
