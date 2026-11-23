using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeEngine.Framework;

/// <summary>One function of a package and the native callback classes it stands on, for a package's feature table: a class the installer
/// refuses withdraws exactly the features that name it, every other feature and the package keep working. A package derives its own row
/// with what its table adds (what the player loses, the bindings or content that go with it) and keeps its table and record text.</summary>
internal record HookFeature(string Name, Type[] Hooks)
{
    /// <summary>The features of <paramref name="table"/> a declaration pass took down: those with at least one refused class.</summary>
    public static IReadOnlyList<T> Withdrawn<T>(IEnumerable<T> table, IReadOnlyDictionary<Type, string> refused) where T : HookFeature
        => table.Where(feature => feature.Hooks.Any(refused.ContainsKey)).ToArray();

    /// <summary>This feature's refused classes with the installer's reasons, as `Class=reason,Class=reason`.</summary>
    public string Refusals(IReadOnlyDictionary<Type, string> refused)
        => string.Join(",", Hooks.Where(refused.ContainsKey).Select(hook => hook.Name + "=" + refused[hook]));
}
