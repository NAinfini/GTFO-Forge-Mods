using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ForgeCore;

/// <summary>A stable fingerprint over labelled values: the same values added in the same order give the same hex on every machine.
/// Each value is written as <c>label=value\n</c> in invariant culture before it is hashed (SHA-256, first 16 bytes as 32 hex
/// characters). Floats are rounded to the precision the caller states, so the same level read twice gives one fingerprint.</summary>
public sealed class Fingerprint
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly StringBuilder _line = new();

    public int Count { get; private set; }

    public Fingerprint Add(string label, string? value) => Write(label, value ?? "\u0001null");

    public Fingerprint Add(string label, long value) => Write(label, value.ToString(CultureInfo.InvariantCulture));

    public Fingerprint Add(string label, ulong value) => Write(label, value.ToString(CultureInfo.InvariantCulture));

    public Fingerprint Add(string label, bool value) => Write(label, value ? "1" : "0");

    /// <summary>A float rounded to <paramref name="decimals"/> places (0.01 m is 2). NaN and infinities are written by name.</summary>
    public Fingerprint Add(string label, double value, int decimals)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return Write(label, value.ToString(CultureInfo.InvariantCulture));
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0; // negative zero
        return Write(label, rounded.ToString("F" + decimals, CultureInfo.InvariantCulture));
    }

    /// <summary>Mixes another finished fingerprint in, so a section hash can be both kept on its own and counted in the whole.</summary>
    public Fingerprint Add(string label, Fingerprint inner) => Write(label, inner.Hex());

    private Fingerprint Write(string label, string value)
    {
        _line.Clear();
        _line.Append(label).Append('=').Append(value).Append('\n');
        _hash.AppendData(Encoding.UTF8.GetBytes(_line.ToString()));
        Count++;
        return this;
    }

    /// <summary>The fingerprint of everything added so far (32 lower-case hex characters). Adding more afterwards continues from the same
    /// state, so a fingerprint read in the middle does not end the run.</summary>
    public string Hex()
    {
        var bytes = _hash.GetCurrentHash();
        var hex = new StringBuilder(32);
        for (var i = 0; i < 16; i++) hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        return hex.ToString();
    }
}

/// <summary>Hashes the contents of any object graph by reading its public instance fields and properties (and the items of
/// anything enumerable) in name order, so two objects with equal contents give one hash whichever process built them. Used for
/// game data blocks, whose interop wrappers expose their values as properties. Cycles are cut by reference, and a graph deeper
/// than <see cref="MaxDepth"/> is reported (the hash then says so) instead of being followed without end.</summary>
public static class ContentHash
{
    public const int MaxDepth = 12;

    // Members that belong to the Il2Cpp wrapper, not to the data it wraps.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "Pointer", "WasCollected", "ObjectClass", "isWrapped", "Item", "SyncRoot", "IsSynchronized", "IsReadOnly", "IsFixedSize", "IsAlias",
    };

    // Wrapper and engine assemblies: their members are plumbing (type objects, pointers, game objects), not the data a block holds.
    // Value types (Vector3, Color) are read by their fields only, since their properties (normalized, magnitude) lead back to the same type; their reference types are named, never walked.
    private static bool IsPlumbing(Type type)
    {
        var name = type.Assembly.GetName().Name ?? "";
        return name.StartsWith("Il2Cpp", StringComparison.Ordinal) || name.StartsWith("UnityEngine", StringComparison.Ordinal)
            || name.StartsWith("System", StringComparison.Ordinal) || name == "mscorlib" || name == "netstandard";
    }

    private static readonly Dictionary<Type, MemberInfo[]> Members = new();

    /// <summary>The 32-character hex content hash of <paramref name="value"/>. <paramref name="truncated"/> is true when some branch
    /// ran into <see cref="MaxDepth"/>.</summary>
    public static string Of(object? value, out bool truncated)
    {
        var fingerprint = new Fingerprint();
        var cut = false;
        Walk(fingerprint, "", value, 0, new HashSet<object>(ReferenceComparer.Instance), ref cut);
        truncated = cut;
        return fingerprint.Hex();
    }

    private static void Walk(Fingerprint fp, string path, object? value, int depth, HashSet<object> seen, ref bool cut)
    {
        if (value == null) { fp.Add(path, (string?)null); return; }
        var type = value.GetType();
        if (value is string s) { fp.Add(path, s); return; }
        if (type.IsEnum) { fp.Add(path, Convert.ToInt64(value, CultureInfo.InvariantCulture)); return; }
        if (type.IsPrimitive || value is decimal)
        {
            switch (value)
            {
                case bool b: fp.Add(path, b); break;
                case float f: fp.Add(path, f, 4); break;
                case double d: fp.Add(path, d, 4); break;
                case ulong u: fp.Add(path, u); break;
                default: fp.Add(path, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            }
            return;
        }
        if (depth >= MaxDepth) { cut = true; fp.Add(path, "\u0001deep"); return; }
        if (!type.IsValueType && !seen.Add(value)) { fp.Add(path, "\u0001cycle"); return; }
        if (value is IEnumerable items && value is not string && IsCollection(type))
        {
            var index = 0;
            foreach (var item in items) Walk(fp, path + "[" + index++ + "]", item, depth + 1, seen, ref cut);
            fp.Add(path + ".count", index);
            if (!type.IsValueType) seen.Remove(value);
            return;
        }
        if (!type.IsValueType && IsPlumbing(type)) { fp.Add(path, "\u0001ref:" + type.Name); if (!type.IsValueType) seen.Remove(value); return; }
        foreach (var member in MembersOf(type))
        {
            object? child;
            try
            {
                child = member is PropertyInfo p ? p.GetValue(value) : ((FieldInfo)member).GetValue(value);
            }
            catch (Exception) { fp.Add(path + "." + member.Name, "\u0001unreadable"); continue; }
            Walk(fp, path + "." + member.Name, child, depth + 1, seen, ref cut);
        }
        if (!type.IsValueType) seen.Remove(value);
    }

    // Lists, arrays and dictionaries are walked by their items whatever assembly defines them.
    private static bool IsCollection(Type type) => type.IsArray || typeof(IEnumerable).IsAssignableFrom(type);

    private static MemberInfo[] MembersOf(Type type)
    {
        lock (Members)
        {
            if (Members.TryGetValue(type, out var known)) return known;
            var list = new List<MemberInfo>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            foreach (var p in type.GetProperties(flags))
                if (!type.IsValueType && !IsPlumbing(p.DeclaringType!)) if (p.CanRead && p.GetIndexParameters().Length == 0 && !Skipped.Contains(p.Name) && !p.Name.Contains("__BackingField", StringComparison.Ordinal)) list.Add(p);
            foreach (var f in type.GetFields(flags))
                if ((!IsPlumbing(f.DeclaringType!) || type.IsValueType) && !Skipped.Contains(f.Name) && !f.Name.Contains("__BackingField", StringComparison.Ordinal) && !f.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal)) list.Add(f);
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return Members[type] = list.ToArray();
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }
}
