using System;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The structural-parameter readers the shot rows (hitscan shot, shot rule, bullet projectile) share. Each reads one
/// optional value from either a compiled frame's slot or a hand-built JSON bag and refuses a value outside the card's vocabulary
/// with <see cref="ResultCode.ParameterInvalid"/>.</summary>
internal static class ShotParameters
{
    /// <summary>The member list one enum parameter of a block-table row declares; a parameter without one has none, so the reader
    /// refuses it rather than the type initializer crashing.</summary>
    public static string[] Members(JsonElement row, string parameter)
    {
        foreach (var candidate in row.GetProperty("graph").GetProperty("parameters").EnumerateArray())
        {
            if (!string.Equals(candidate.GetProperty("id").GetString(), parameter, StringComparison.Ordinal)) continue;
            if (!candidate.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return values.EnumerateArray().Select(value => value.GetString()!).ToArray();
        }
        return Array.Empty<string>();
    }

    /// <summary>One optional enum member in either wire spelling: a compiled plan writes the member's index, a hand-built frame may
    /// write its name, and an absent key is the parameter's own first member, the same default the compiler materializes.</summary>
    public static bool TryEnum(JsonElement bag, string id, string[] members, out string member, out string code)
    {
        member = members.Length > 0 ? members[0] : "";
        code = "";
        if (members.Length == 0) { code = ResultCode.ParameterInvalid; return false; }
        if (bag.ValueKind != JsonValueKind.Object || !bag.TryGetProperty(id, out var declared)
            || declared.ValueKind == JsonValueKind.Null) return true;
        if (declared.ValueKind == JsonValueKind.String) member = declared.GetString() ?? "";
        else if (declared.ValueKind == JsonValueKind.Number && declared.TryGetInt32(out var index)
            && index >= 0 && index < members.Length) member = members[index];
        else { code = ResultCode.ParameterInvalid; return false; }
        if (Array.IndexOf(members, member) < 0) { code = ResultCode.ParameterInvalid; return false; }
        return true;
    }

    public static bool TryEnum(CommandContext context, HandlerSlot slot, string[] members, out string member, out string code)
    {
        member = members.Length > 0 ? members[0] : "";
        code = "";
        if (members.Length == 0) { code = ResultCode.ParameterInvalid; return false; }
        if (context.Kind(slot) is ValueKind.Missing or ValueKind.Null) return true;
        member = context.Text(slot) ?? "";
        if (Array.IndexOf(members, member) >= 0) return true;
        code = ResultCode.ParameterInvalid;
        return false;
    }

    /// <summary>One optional boolean, with the row's own default for an absent key.</summary>
    public static bool TryFlag(JsonElement bag, string id, bool fallback, out bool value, out string code)
    {
        value = fallback;
        code = "";
        if (bag.ValueKind != JsonValueKind.Object || !bag.TryGetProperty(id, out var declared)
            || declared.ValueKind == JsonValueKind.Null) return true;
        if (declared.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        { code = ResultCode.ParameterInvalid; return false; }
        value = declared.GetBoolean();
        return true;
    }

    public static bool TryFlag(CommandContext context, HandlerSlot slot, bool fallback, out bool value, out string code)
    {
        value = fallback;
        code = "";
        if (context.Kind(slot) is ValueKind.Missing or ValueKind.Null) return true;
        if (context.Boolean(slot) is { } declared) { value = declared; return true; }
        code = ResultCode.ParameterInvalid;
        return false;
    }
}
