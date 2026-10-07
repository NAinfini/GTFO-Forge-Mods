using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Agents;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct BuildFingerprint : IEquatable<BuildFingerprint>
{
    internal ulong A, B, C, D;
    public bool Equals(BuildFingerprint other) => A == other.A && B == other.B && C == other.C && D == other.D;
    public override bool Equals(object? other) => other is BuildFingerprint value && Equals(value);
    public override int GetHashCode() => HashCode.Combine(A, B, C, D);
    public override string ToString() => $"{A:x16}{B:x16}{C:x16}{D:x16}";

    internal static BuildFingerprint Compute()
    {
        var text = new StringBuilder(EnergyProtocol.Version);
        foreach (var type in new[] { typeof(HelloPacket), typeof(FireCommand), typeof(FireVisual),
                     typeof(ChargePacket), typeof(ModePacket), typeof(BlastForcePacket), typeof(BuildFingerprint) })
        {
            text.Append('|').Append(type.Name).Append(':').Append(Marshal.SizeOf(type));
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .OrderBy(f => Marshal.OffsetOf(type, f.Name).ToInt32()))
                text.Append('|').Append(field.Name).Append(':').Append(field.FieldType.FullName)
                    .Append('@').Append(Marshal.OffsetOf(type, field.Name).ToInt32());
        }
        // Inline numeric literals are tuning too. Including our CLR bodies prevents an
        // un-named formula edit from evading the development-build handshake.
        foreach (var type in typeof(EnergyLab).Assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            text.Append('|').Append(type.FullName);
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (field.IsLiteral)
                    text.Append('|').Append(field.Name).Append('=').Append(Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture));
            }
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                         .OrderBy(m => m.ToString(), StringComparer.Ordinal))
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il != null) text.Append('|').Append(method).Append(':').Append(Convert.ToHexString(il));
            }
        }
        foreach (var gear in EnergyGears.All)
        {
            var definition = gear.Definition;
            var node = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = definition.Id, ["name"] = definition.Name, ["slot"] = (int)definition.Slot,
                ["mode"] = (int)definition.Mode, ["gearId"] = gear.GearId, ["categoryId"] = gear.CategoryId,
                ["archetypeId"] = gear.ArchetypeId, ["tuningType"] = definition.Tuning.GetType().FullName,
                ["tuning"] = EnergyConfiguration.TuningJson(definition.Tuning), ["modelKey"] = definition.Model.Key,
                ["modelSha256"] = EnergyGears.ModelHashes[gear.ModelPath]
            };
            text.Append('|').Append(node.ToJsonString());
        }
        foreach (var resource in typeof(EnergyLab).Assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = typeof(EnergyLab).Assembly.GetManifestResourceStream(resource)!;
            using var sha = SHA256.Create();
            text.Append('|').Append(resource).Append(':').Append(Convert.ToHexString(sha.ComputeHash(stream)));
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return new() { A = BitConverter.ToUInt64(hash, 0), B = BitConverter.ToUInt64(hash, 8),
            C = BitConverter.ToUInt64(hash, 16), D = BitConverter.ToUInt64(hash, 24) };
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HelloPacket
{
    internal ushort Major, Minor, Patch;
    internal ulong Epoch;
    internal BuildFingerprint Fingerprint;
    internal int Ready, Rejected;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FireCommand
{
    internal ulong Epoch;
    internal uint Sequence;
    internal uint CategoryId;
    internal int Mode, Dimension;
    internal float X, Y, Z, Dx, Dy, Dz, Power;
    internal Vector3 Start => new(X, Y, Z);
    internal Vector3 Direction => new(Dx, Dy, Dz);
    internal static FireCommand Create(uint sequence, EnergyGear gear, Vector3 start,
        Vector3 direction, int dimension, float power = 0f) => new()
    {
        Epoch = EnergyNetwork.Epoch, Sequence = sequence, Mode = (int)gear.Mode, CategoryId = gear.CategoryId, Dimension = dimension,
        X = start.x, Y = start.y, Z = start.z, Dx = direction.x, Dy = direction.y, Dz = direction.z, Power = power
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FireVisual
{
    internal ulong Epoch, Shooter;
    internal uint Sequence;
    internal uint CategoryId;
    internal int Mode, Segments, HitMask;
    internal float X, Y, Z, Dx, Dy, Dz, Power;
    internal float E0x, E0y, E0z, E1x, E1y, E1z, E2x, E2y, E2z, E3x, E3y, E3z;
    internal Vector3 Start => new(X, Y, Z);
    internal Vector3 Direction => new(Dx, Dy, Dz);
    internal bool Hit(int index) => (HitMask & (1 << index)) != 0;
    internal Vector3 End(int index) => index switch
    {
        0 => new(E0x, E0y, E0z), 1 => new(E1x, E1y, E1z),
        2 => new(E2x, E2y, E2z), 3 => new(E3x, E3y, E3z),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    internal void SetEnd(int index, Vector3 end)
    {
        switch (index)
        {
            case 0: E0x = end.x; E0y = end.y; E0z = end.z; break;
            case 1: E1x = end.x; E1y = end.y; E1z = end.z; break;
            case 2: E2x = end.x; E2y = end.y; E2z = end.z; break;
            case 3: E3x = end.x; E3y = end.y; E3z = end.z; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
    internal static FireVisual From(FireCommand command, ulong shooter) => new()
    {
        Epoch = command.Epoch, Shooter = shooter, Sequence = command.Sequence, Mode = command.Mode, CategoryId = command.CategoryId,
        X = command.X, Y = command.Y, Z = command.Z,
        Dx = command.Dx, Dy = command.Dy, Dz = command.Dz, Power = command.Power
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct BlastForcePacket
{
    internal ulong Epoch;
    internal uint Sequence;
    internal pEnemyAgent Target;
    internal float Magnitude;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ChargePacket
{
    internal ulong Epoch, Shooter;
    internal uint CategoryId;
    internal int Mode, Phase;
    internal float X, Y, Z;
    internal Vector3 Position => new(X, Y, Z);
    internal static ChargePacket Create(ulong shooter, EnergyGear gear, bool start, Vector3 position) => new()
    {
        Epoch = EnergyNetwork.Epoch, Shooter = shooter, Mode = (int)gear.Mode, CategoryId = gear.CategoryId, Phase = start ? 1 : 0,
        X = position.x, Y = position.y, Z = position.z
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ModePacket
{
    internal ulong Epoch, Shooter;
    internal uint Arm, CategoryId;
    internal int Mode;
    internal static ModePacket Create(EnergyGear? gear, uint arm) => new()
    { Epoch = EnergyNetwork.Epoch, Mode = (int)(gear?.Mode ?? EnergyMode.Off), CategoryId = gear?.CategoryId ?? 0, Arm = arm };
}
