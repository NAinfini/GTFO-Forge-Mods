using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// JSON literal to CLR value conversion for <c>call</c> arguments, <c>set</c> values and
/// <c>worldEvent</c> fields. Conversions are driven by the destination member's type, so a value is
/// only ever converted when a real signature asked for it.
/// </summary>
internal static class ExperimentConvert
{
    internal static bool TryConvert(object? literal, Type target, out object? converted, out string error)
    {
        converted = null;
        error = "";

        if (target == typeof(object))
        {
            converted = literal;
            return true;
        }
        if (literal == null || literal is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            if (!target.IsValueType || Nullable.GetUnderlyingType(target) != null)
            {
                converted = null;
                return true;
            }
            error = "null is not valid for " + target.Name;
            return false;
        }

        var type = Nullable.GetUnderlyingType(target) ?? target;
        if (type.IsInstanceOfType(literal) && literal is not JsonElement)
        {
            converted = literal;
            return true;
        }

        // Interop turns Nullable<T> into Il2CppSystem.Nullable<T>, which has no Nullable.GetUnderlyingType.
        var interopNullable = InteropNullable(target);
        if (interopNullable != null)
        {
            if (literal is JsonElement { ValueKind: JsonValueKind.Null }) return true;
            if (!TryConvert(literal, interopNullable, out var inner, out error)) return false;
            converted = WrapNullable(target, inner, out error);
            return error.Length == 0;
        }

        if (literal is JsonElement element)
        {
            var convertedElement = FromJson(element, type, out error);
            if (error.Length != 0) return false;
            converted = convertedElement;
            return true;
        }

        if (type == typeof(string)) { converted = literal.ToString(); return true; }
        if (type == typeof(bool) && literal is bool flag) { converted = flag; return true; }
        if (type.IsEnum)
        {
            var text = literal.ToString() ?? "";
            if (TryEnum(type, text, out var enumeration, out error)) { converted = enumeration; return true; }
            return false;
        }
        if (type.IsPrimitive || type == typeof(decimal))
        {
            try
            {
                converted = System.Convert.ChangeType(literal, type, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                // The author-facing error names the literal and the target type; the conversion exception adds nothing beyond that.
                error = "'" + literal + "' cannot be read as " + type.Name;
                return false;
            }
        }
        if (type == typeof(Vector3) && literal is Vector2 vector2) { converted = new Vector3(vector2.x, vector2.y, 0f); return true; }
        if (type == typeof(Color) && literal is Vector3 vector) { converted = new Color(vector.x, vector.y, vector.z, 1f); return true; }
        error = "no conversion from " + ExperimentValue.DescribeType(literal) + " to " + type.FullName;
        return false;
    }

    /// <summary>The interop stand-in for <c>Nullable&lt;T&gt;</c>, or null when the type is not one.</summary>
    internal static Type? InteropNullable(Type type) =>
        type.IsGenericType && type.Name.StartsWith("Nullable`1", StringComparison.Ordinal) && type.Namespace != null &&
        type.Namespace.StartsWith("Il2Cpp", StringComparison.Ordinal)
            ? type.GetGenericArguments()[0]
            : null;

    /// <summary>
    /// Wraps a value in the interop nullable. The conversion operator is found by reflection because the
    /// managed compiler refuses to name an interop generic instantiated over an interop type.
    /// </summary>
    private static object? WrapNullable(Type nullable, object? value, out string error)
    {
        error = "";
        if (value == null) return null;
        try
        {
            var method = nullable.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate => candidate.Name == "op_Implicit" && candidate.GetParameters().Length == 1 &&
                    candidate.GetParameters()[0].ParameterType.IsInstanceOfType(value));
            if (method == null)
            {
                error = "no implicit conversion into " + nullable.Name + " is available";
                return null;
            }
            return method.Invoke(null, new[] { value });
        }
        catch (Exception e)
        {
            error = "wrapping " + ExperimentValue.DescribeType(value) + " into " + nullable.Name + " failed: " + ExperimentValue.Unwrap(e).Message;
            return null;
        }
    }

    private static object? FromJson(JsonElement element, Type type, out string error)
    {
        error = "";
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return FromString(element.GetString() ?? "", type, out error);
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (type == typeof(bool)) return element.GetBoolean();
                if (type == typeof(string)) return element.GetBoolean() ? "true" : "false";
                error = "a boolean is not valid for " + type.Name;
                return null;
            case JsonValueKind.Number:
                return FromNumber(element, type, out error);
            case JsonValueKind.Array:
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && type.GetGenericArguments().Length == 1)
                    return FromList(element, type, out error);
                return FromArray(element, type, out error);
            case JsonValueKind.Object:
                return FromObject(element, type, out error);
            default:
                error = "unsupported JSON value for " + type.Name;
                return null;
        }
    }

    private static object? FromString(string text, Type type, out string error)
    {
        error = "";
        if (type == typeof(string)) return text;
        if (type == typeof(bool))
        {
            if (text is "true" or "True") return true;
            if (text is "false" or "False") return false;
            error = "'" + text + "' is not a boolean";
            return null;
        }
        if (type.IsEnum)
        {
            if (TryEnum(type, text, out var enumeration, out error)) return enumeration;
            return null;
        }
        if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color))
        {
            if (TryNumbers(text, out var values, out error)) return FromNumbers(values, type, out error);
            return null;
        }
        if (type == typeof(Localization.LocalizedText))
            return new Localization.LocalizedText(text);
        if (type == typeof(Type))
        {
            var resolved = ExperimentTypes.Find(text);
            if (resolved == null)
            {
                error = "no loaded type matches '" + text + "'";
                return null;
            }
            return resolved;
        }
        if (type.IsPrimitive || type == typeof(decimal))
        {
            if (TryNumber(text, type, out var parsed)) return parsed;
            error = "'" + text + "' is not a " + type.Name;
            return null;
        }
        if (type == typeof(object)) return text;
        error = "no conversion from a string to " + type.FullName;
        return null;
    }

    /// <summary>Builds a <c>List&lt;T&gt;</c> from a JSON array, converting each element against T.</summary>
    private static object? FromList(JsonElement element, Type type, out string error)
    {
        error = "";
        var item = type.GetGenericArguments()[0];
        var list = (System.Collections.IList)Activator.CreateInstance(type)!;
        foreach (var entry in element.EnumerateArray())
        {
            if (!TryConvert(entry, item, out var converted, out var itemError))
            {
                error = "element " + list.Count.ToString(CultureInfo.InvariantCulture) + ": " + itemError;
                return null;
            }
            list.Add(converted);
        }
        return list;
    }

    /// <summary>
    /// Strict, invariant parsing. <c>Convert.ChangeType</c> is deliberately avoided: it accepts thousands
    /// separators and whitespace, so "1,5" would silently become 15 for an int parameter.
    /// </summary>
    private static bool TryNumber(string text, Type type, out object? value)
    {
        value = null;
        var styles = NumberStyles.Float | NumberStyles.AllowLeadingSign;
        if (type == typeof(float) && float.TryParse(text, styles, CultureInfo.InvariantCulture, out var single)) { value = single; return true; }
        if (type == typeof(double) && double.TryParse(text, styles, CultureInfo.InvariantCulture, out var number)) { value = number; return true; }
        if (type == typeof(decimal) && decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var money)) { value = money; return true; }
        if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) { value = integer; return true; }
        if (type == typeof(uint) && uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned)) { value = unsigned; return true; }
        if (type == typeof(long) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wide)) { value = wide; return true; }
        if (type == typeof(ulong) && ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wider)) { value = wider; return true; }
        if (type == typeof(short) && short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var small)) { value = small; return true; }
        if (type == typeof(ushort) && ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var smaller)) { value = smaller; return true; }
        if (type == typeof(byte) && byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tiny)) { value = tiny; return true; }
        if (type == typeof(sbyte) && sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed)) { value = signed; return true; }
        return false;
    }

    private static object? FromNumber(JsonElement element, Type type, out string error)
    {
        error = "";
        if (type.IsEnum)
        {
            if (element.TryGetInt64(out var enumValue)) return Enum.ToObject(type, enumValue);
            error = "an enum needs an integer or a member name";
            return null;
        }
        if (type == typeof(bool))
        {
            error = "a number is not valid for a boolean";
            return null;
        }
        if (type == typeof(string)) return element.GetRawText();
        if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color))
        {
            var values = new List<double>();
            if (element.TryGetDouble(out var single)) values.Add(single);
            else values.Add(0);
            return FromNumbers(values, type, out error);
        }
        if (type.IsPrimitive || type == typeof(decimal))
        {
            var number = element.GetDouble();
            try
            {
                return System.Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // The author-facing error names the value and the target type; the overflow exception adds nothing beyond that.
                error = element.GetRawText() + " is out of range for " + type.Name;
                return null;
            }
        }
        error = "no conversion from a number to " + type.FullName;
        return null;
    }

    private static object? FromArray(JsonElement element, Type type, out string error)
    {
        error = "";
        var values = new List<double>();
        var anyText = false;
        var text = "";
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number)) values.Add(number);
            else if (item.ValueKind == JsonValueKind.String) { anyText = true; text = item.GetString() ?? ""; }
            else
            {
                error = "vector and color literals may only contain numbers";
                return null;
            }
        }
        if (anyText && values.Count == 0 && type == typeof(Vector3) && text.Length > 0)
            return FromString(text, type, out error);
        return FromNumbers(values, type, out error);
    }

    private static object? FromObject(JsonElement element, Type type, out string error)
    {
        error = "";
        if (type == typeof(Localization.LocalizedText))
        {
            if (element.TryGetProperty("id", out var id))
            {
                if (!id.TryGetUInt32(out var value))
                {
                    error = "LocalizedText.id must be a non-negative integer";
                    return null;
                }
                return new Localization.LocalizedText(value);
            }
            if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return new Localization.LocalizedText(text.GetString() ?? "");
            error = "LocalizedText literals use {\"text\":\"...\"} or {\"id\":123}";
            return null;
        }
        if (element.TryGetProperty("x", out var x) && element.TryGetProperty("y", out var y))
        {
            var z = element.TryGetProperty("z", out var zNode) && zNode.TryGetDouble(out var zValue) ? zValue : 0;
            if (!x.TryGetDouble(out var xValue) || !y.TryGetDouble(out var yValue))
            {
                error = "vector components must be numbers";
                return null;
            }
            if (type == typeof(Vector2)) return new Vector2((float)xValue, (float)yValue);
            if (type == typeof(Vector3)) return new Vector3((float)xValue, (float)yValue, (float)z);
            if (type == typeof(Vector4)) return new Vector4((float)xValue, (float)yValue, (float)z, element.TryGetProperty("w", out var w) && w.TryGetDouble(out var wValue) ? (float)wValue : 0f);
            if (type == typeof(Quaternion)) return new Quaternion((float)xValue, (float)yValue, (float)z, element.TryGetProperty("w", out var qw) && qw.TryGetDouble(out var qwValue) ? (float)qwValue : 0f);
        }
        if (type == typeof(Color) && element.TryGetProperty("r", out var r) && element.TryGetProperty("g", out var g) && element.TryGetProperty("b", out var b))
        {
            if (!r.TryGetDouble(out var rv) || !g.TryGetDouble(out var gv) || !b.TryGetDouble(out var bv))
            {
                error = "color components must be numbers";
                return null;
            }
            return new Color((float)rv, (float)gv, (float)bv, element.TryGetProperty("a", out var a) && a.TryGetDouble(out var av) ? (float)av : 1f);
        }
        // A string parameter takes the object as its JSON text (reads that accept a JSON request, such as an Engine author read).
        if (type == typeof(string)) return element.GetRawText();
        error = "no conversion from a JSON object to " + type.Name;
        return null;
    }

    private static object? FromNumbers(IReadOnlyList<double> values, Type type, out string error)
    {
        error = "";
        float Value(int index) => index < values.Count ? (float)values[index] : (index == 3 ? 1f : 0f);
        if (type == typeof(Vector2))
        {
            if (values.Count < 2) { error = "Vector2 needs two numbers"; return null; }
            return new Vector2(Value(0), Value(1));
        }
        if (type == typeof(Vector3))
        {
            if (values.Count < 3) { error = "Vector3 needs three numbers"; return null; }
            return new Vector3(Value(0), Value(1), Value(2));
        }
        if (type == typeof(Vector4))
        {
            if (values.Count < 4) { error = "Vector4 needs four numbers"; return null; }
            return new Vector4(Value(0), Value(1), Value(2), Value(3));
        }
        if (type == typeof(Quaternion))
        {
            if (values.Count < 4) { error = "Quaternion needs four numbers"; return null; }
            return new Quaternion(Value(0), Value(1), Value(2), Value(3));
        }
        if (type == typeof(Color))
        {
            if (values.Count is < 3 or > 4) { error = "Color needs three or four numbers"; return null; }
            return new Color(Value(0), Value(1), Value(2), values.Count == 4 ? Value(3) : 1f);
        }
        error = "no conversion from a number array to " + type.Name;
        return null;
    }

    private static bool TryNumbers(string text, out List<double> values, out string error)
    {
        values = new List<double>();
        error = "";
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                error = "'" + part + "' is not a number";
                return false;
            }
            values.Add(value);
        }
        if (values.Count == 0)
        {
            error = "no numbers found";
            return false;
        }
        return true;
    }

    internal static bool TryEnum(Type type, string text, out object? value, out string error)
    {
        value = null;
        error = "";
        var name = text;
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        try
        {
            var parsed = Enum.Parse(type, name, true);
            value = parsed;
            return true;
        }
        catch (ArgumentException)
        {
            // Not a member name: the numeric form and the named error below handle the miss.
        }
        if (long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            value = Enum.ToObject(type, number);
            return true;
        }
        var members = string.Join(", ", Enum.GetNames(type).Take(12));
        if (Enum.GetNames(type).Length > 12) members += ", ...";
        error = "'" + text + "' is not a member of " + type.Name + " (" + members + ")";
        return false;
    }
}

internal static class ExperimentTypes
{
    private static Assembly[]? _assemblies;

    /// <summary>Loaded assemblies, refreshed when the game adds one (interop assemblies load late).</summary>
    internal static Assembly[] Assemblies
    {
        get
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            var cache = _assemblies;
            if (cache == null || cache.Length != loaded.Length) _assemblies = cache = loaded;
            return cache;
        }
    }

    internal static Type? Find(string name)
    {
        if (name.Length == 0) return null;
        var qualified = Type.GetType(name, false);
        if (qualified != null) return qualified;
        foreach (var assembly in Assemblies)
        {
            var type = assembly.GetType(name, false);
            if (type != null) return type;
        }
        foreach (var assembly in Assemblies)
        {
            Type? match = null;
            var duplicates = 0;
            foreach (var candidate in SafeTypes(assembly))
                if (candidate.Name == name || candidate.FullName == name)
                {
                    if (match == null) match = candidate;
                    else duplicates++;
                }
            if (match != null && duplicates == 0) return match;
        }
        return null;
    }

    internal static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
        catch (Exception error)
        {
            Plugin.PluginLog.LogWarning("[Experiment] types of " + assembly.FullName + " are unreadable and skipped: " + error);
            return Array.Empty<Type>();
        }
    }

    /// <summary>Type name search for <c>allOfType:</c>: exact simple or full name, or a namespace-qualified wildcard.</summary>
    internal static IReadOnlyList<Type> Match(string pattern)
    {
        var results = new List<Type>();
        foreach (var assembly in Assemblies)
            foreach (var type in SafeTypes(assembly))
            {
                if (!Matches(type, pattern)) continue;
                results.Add(type);
                if (results.Count >= 32) return results;
            }
        return results;
    }

    private static bool Matches(Type type, string pattern)
    {
        if (type.Name == pattern || type.FullName == pattern) return true;
        if (!pattern.Contains('*')) return false;
        var full = type.FullName ?? type.Name;
        var parts = pattern.Split('*');
        var position = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            var found = full.IndexOf(part, position, StringComparison.Ordinal);
            if (found < 0) return false;
            position = found + part.Length;
        }
        return true;
    }
}
