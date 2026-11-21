using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// Value access for experiment steps. Reading and writing go through the same path grammar, so a path that
/// reads in one step can be written in the next. Failures return text instead of throwing: a bad path is a
/// step result, not a crashed run.
/// </summary>
internal static class ExperimentValue
{
    internal const int MaximumDepth = 12;

    internal readonly record struct ReadResult(bool Ok, object? Value, string Error);
    internal readonly record struct WriteResult(bool Ok, string Error);

    internal static ReadResult Read(object? root, string path)
    {
        if (root == null) return new ReadResult(false, null, "the target resolved to nothing");
        var current = root;
        var segments = path.Split('.');
        for (var index = 0; index < segments.Length; index++)
        {
            if (current == null) return new ReadResult(false, null, "segment " + index + " ('" + segments[index] + "') of '" + path + "' is null");
            if (TryIndex(segments[index], out var position))
            {
                if (!TryElement(current, position, out var element, out var indexError)) return new ReadResult(false, null, "'" + path + "': " + indexError);
                current = element;
                continue;
            }
            if (!TryMember(current, segments[index], out var member, out var error))
                return new ReadResult(false, null, "'" + path + "': " + error);
            try { current = ReadMemberValue(current!, member!); }
            catch (Exception e) { return new ReadResult(false, null, "'" + path + "' failed at '" + segments[index] + "': " + Unwrap(e).Message); }
        }
        return new ReadResult(true, current, "");
    }

    internal static WriteResult Write(object? root, string path, object? value)
    {
        if (root == null) return new WriteResult(false, "the target resolved to nothing");
        var segments = path.Split('.');
        var current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var step = Read(current, segments[index]);
            if (!step.Ok) return new WriteResult(false, step.Error);
            if (step.Value == null) return new WriteResult(false, "'" + path + "' stops at null member '" + segments[index] + "'");
            current = step.Value;
        }
        var last = segments[^1];
        if (TryIndex(last, out var position))
        {
            if (current is not System.Collections.IList list) return new WriteResult(false, "'" + path + "' is not an indexable collection");
            if (position < 0 || position >= list.Count) return new WriteResult(false, "'" + path + "' index " + position + " is outside 0.." + (list.Count - 1));
            var element = list[position];
            var elementType = element?.GetType() ?? typeof(object);
            if (!ExperimentConvert.TryConvert(value, elementType, out var convertedElement, out var elementError))
                return new WriteResult(false, "'" + path + "': " + elementError);
            try
            {
                list[position] = convertedElement;
                return new WriteResult(true, "");
            }
            catch (Exception e)
            {
                return new WriteResult(false, "'" + path + "' write failed: " + Unwrap(e).Message);
            }
        }
        if (!TryMember(current!, last, out var member, out var error)) return new WriteResult(false, "'" + path + "': " + error);
        var target = MemberType(member!);
        if (!ExperimentConvert.TryConvert(value, target, out var converted, out var conversion))
            return new WriteResult(false, "'" + path + "': " + conversion);
        try
        {
            switch (member)
            {
                case PropertyInfo property:
                    if (!property.CanWrite) return new WriteResult(false, "'" + path + "' is a read-only property");
                    property.SetValue(current, converted);
                    return new WriteResult(true, "");
                case FieldInfo field:
                    field.SetValue(current, converted);
                    return new WriteResult(true, "");
                default:
                    return new WriteResult(false, "'" + path + "' is not writable");
            }
        }
        catch (Exception e)
        {
            return new WriteResult(false, "'" + path + "' write failed: " + Unwrap(e).Message);
        }
    }

    /// <summary>Reads a member on an IL2CPP or managed object, including private ones.</summary>
    internal static bool TryMember(object target, string name, out MemberInfo? member, out string error)
    {
        member = null;
        error = "";
        if (name.Length == 0)
        {
            error = "an empty segment is not a member";
            return false;
        }
        var type = target.GetType();
        while (type != null)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                member = property;
                return true;
            }
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                member = field;
                return true;
            }
            type = type.BaseType;
        }
        error = "no property or field named '" + name + "' on " + DescribeType(target);
        return false;
    }

    internal static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => typeof(object)
    };

    /// <summary>A numeric segment indexes an array, list or dictionary rather than naming a member.</summary>
    internal static bool TryIndex(string segment, out int position)
    {
        if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out position)) return true;
        position = 0;
        return false;
    }

    private static bool TryElement(object collection, int position, out object? element, out string error)
    {
        element = null;
        error = "";
        if (collection is System.Collections.IList list)
        {
            if (position < 0 || position >= list.Count)
            {
                error = "index " + position.ToString(CultureInfo.InvariantCulture) + " is outside 0.." + (list.Count - 1).ToString(CultureInfo.InvariantCulture);
                return false;
            }
            element = list[position];
            return true;
        }
        // Interop arrays project the native list through a generic indexer instead of IList.
        var indexer = collection.GetType().GetProperty("Item", new[] { typeof(int) });
        if (indexer != null)
        {
            try
            {
                element = indexer.GetValue(collection, new object[] { position });
                return true;
            }
            catch (Exception e)
            {
                error = "index " + position.ToString(CultureInfo.InvariantCulture) + " failed: " + Unwrap(e).Message;
                return false;
            }
        }
        error = DescribeType(collection) + " is not an indexable collection";
        return false;
    }

    internal static int Count(object collection)
    {
        if (collection is System.Collections.ICollection sized) return sized.Count;
        var count = collection.GetType().GetProperty("Count");
        return count == null ? -1 : System.Convert.ToInt32(count.GetValue(collection), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads one member value. Interop proxies expose a native field as both a property and a field;
    /// the property is preferred because its accessor is the one the game itself uses.</summary>
    internal static object? ReadMemberValue(object target, MemberInfo member)
    {
        var owner = member is PropertyInfo { GetMethod.IsStatic: true } || member is FieldInfo { IsStatic: true } ? null : target;
        return member switch
        {
            PropertyInfo property => property.GetValue(owner),
            FieldInfo field => field.GetValue(owner),
            _ => null
        };
    }

    internal static string DescribeType(object? value) => value switch
    {
        null => "null",
        Type type => type.Name,
        _ => value.GetType().Name
    };

    /// <summary>A short, stable identity for log records: type name plus the instance pointer when there is one.</summary>
    internal static string Identity(object? value)
    {
        if (value == null) return "null";
        if (value is UnityEngine.Object unity)
            return unity == null ? "destroyed " + value.GetType().Name : unity.GetType().Name + "#" + unity.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        var type = value.GetType();
        // An interop proxy has no useful ToString; its hash is a stable per-instance token within the run.
        if (type.Namespace != null && type.Namespace.StartsWith("Il2Cpp", StringComparison.Ordinal))
            return type.Name + "@" + value.GetHashCode().ToString("X", CultureInfo.InvariantCulture);
        return type.Name;
    }

    internal static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: { } inner } ? inner : error;

    internal static string Format(object? value, int depth = 0)
    {
        switch (value)
        {
            case null: return "null";
            case string text: return text;
            case bool flag: return flag ? "true" : "false";
            case float single: return single.ToString("R", CultureInfo.InvariantCulture);
            case double number: return number.ToString("R", CultureInfo.InvariantCulture);
            case Vector3 vector: return vector.x.ToString("R", CultureInfo.InvariantCulture) + "," + vector.y.ToString("R", CultureInfo.InvariantCulture) + "," + vector.z.ToString("R", CultureInfo.InvariantCulture);
            case Vector2 vector2: return vector2.x.ToString("R", CultureInfo.InvariantCulture) + "," + vector2.y.ToString("R", CultureInfo.InvariantCulture);
            case Quaternion rotation: return rotation.x.ToString("R", CultureInfo.InvariantCulture) + "," + rotation.y.ToString("R", CultureInfo.InvariantCulture) + "," + rotation.z.ToString("R", CultureInfo.InvariantCulture) + "," + rotation.w.ToString("R", CultureInfo.InvariantCulture);
            case Color color: return color.r.ToString("R", CultureInfo.InvariantCulture) + "," + color.g.ToString("R", CultureInfo.InvariantCulture) + "," + color.b.ToString("R", CultureInfo.InvariantCulture) + "," + color.a.ToString("R", CultureInfo.InvariantCulture);
            case Enum enumeration: return Convert.ToInt64(enumeration, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "/" + enumeration;
            case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
        }
        if (depth >= 2) return Identity(value);
        var enumerable = value as System.Collections.IEnumerable;
        if (enumerable == null || value is string) return Identity(value);
        var parts = new List<string>();
        foreach (var item in enumerable)
        {
            if (parts.Count >= 32) { parts.Add("..."); break; }
            parts.Add(item == null ? "null" : Format(item, depth + 1));
        }
        return "[" + string.Join(",", parts) + "]";
    }

    internal static string FormatJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => element.GetRawText()
    };
}
