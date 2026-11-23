using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ForgeEnemy;

/// <summary>The website and provider consume this same ability resource definition. The ordinal is zero-based
/// within one AgentAbility kind's enabled components, never an AllComps offset.</summary>
internal static class EnemyAbilityIdentity
{
    private static readonly (string Id, byte Ability)[] Kinds;
    private static readonly string Separator;
    private static readonly int MaximumIndex;

    static EnemyAbilityIdentity()
    {
        using var stream = typeof(EnemyAbilityIdentity).Assembly.GetManifestResourceStream("forge.enemy.ability-resources")
            ?? throw new InvalidOperationException("Enemy ability resource definition is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "forge-enemy-ability-resources"
            || root.GetProperty("version").GetString() != "1.0.0")
            throw new InvalidOperationException("Enemy ability resource definition is invalid.");
        Separator = root.GetProperty("separator").GetString()!;
        MaximumIndex = root.GetProperty("maxIndex").GetInt32();
        Kinds = root.GetProperty("kinds").EnumerateArray().Select(row =>
            (row.GetProperty("id").GetString()!, row.GetProperty("ability").GetByte())).ToArray();
    }

    internal static bool TryParse(string id, out byte ability, out int ordinal)
    {
        ability = 0; ordinal = -1;
        if (id == null) return false;
        int separator = id.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0) return false;
        var number = id.AsSpan(separator + Separator.Length);
        if (number.Length == 0 || number.Length > 1 && number[0] == '0'
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
            || ordinal > MaximumIndex) return false;
        foreach (var kind in Kinds)
            if (id.AsSpan(0, separator).SequenceEqual(kind.Id.AsSpan())) { ability = kind.Ability; return true; }
        return false;
    }

    internal static bool TryId(byte ability, int ordinal, out string id)
    {
        if (ordinal >= 0 && ordinal <= MaximumIndex)
            foreach (var kind in Kinds)
                if (kind.Ability == ability)
                { id = kind.Id + Separator + ordinal.ToString(CultureInfo.InvariantCulture); return true; }
        id = ""; return false;
    }
}
