using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeEnemy.Spawn;

/// <summary>Dependencies an enemy content definition actually needs at runtime.</summary>
/// <param name="RequiresEnemyPack">True only when a referenced plan binds a ForgeEnemy provider binding.</param>
/// <param name="EnemyBindings">Distinct ForgeEnemy binding ids referenced by plans, ordinal order.</param>
/// <param name="OtherProviders">Distinct non-Enemy provider ids referenced by plans; resolved by their own packages.</param>
/// <param name="Resources">Distinct resource revisions (models, materials, animation) in ordinal order.</param>
internal sealed record EnemyContentDependencySet(
    bool RequiresEnemyPack,
    IReadOnlyList<string> EnemyBindings,
    IReadOnlyList<string> OtherProviders,
    IReadOnlyList<string> Resources);

internal static class EnemyContentDependencies
{
    /// <summary>
    /// Computes dependencies from the references a definition really carries. Appearance resources
    /// never pull in Enemy behaviour; a plan's <c>domain</c> label is not a dependency either, only
    /// its explicit binding providers are.
    /// </summary>
    public static EnemyContentDependencySet Compute(IEnumerable<string> resourceRevisions, IEnumerable<string> planJsons)
    {
        if (resourceRevisions == null) throw new ArgumentNullException(nameof(resourceRevisions));
        if (planJsons == null) throw new ArgumentNullException(nameof(planJsons));
        var resources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var resource in resourceRevisions)
        {
            if (string.IsNullOrWhiteSpace(resource) || resource.Trim() != resource)
                throw new InvalidDataException("Resource revisions must be non-empty, trimmed identifiers.");
            resources.Add(resource);
        }
        var enemyBindings = new SortedSet<string>(StringComparer.Ordinal);
        var otherProviders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var plan in planJsons)
        {
            using var document = JsonDocument.Parse(plan ?? throw new InvalidDataException("Plan text is null."));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("A plan must declare its bindings array; dependencies are never inferred.");
            foreach (var binding in bindings.EnumerateArray())
            {
                string provider = Text(binding, "providerId"), bindingId = Text(binding, "bindingId");
                if (provider == ModuleDefinition.ProviderId) enemyBindings.Add(bindingId);
                else otherProviders.Add(provider);
            }
        }
        return new EnemyContentDependencySet(enemyBindings.Count > 0, enemyBindings.ToArray(), otherProviders.ToArray(), resources.ToArray());
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException("Plan binding is missing " + name + ".");
}
