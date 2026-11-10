using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ForgeCore;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

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
        var text = new StringBuilder();
        foreach (var type in new[] { typeof(FireCommand), typeof(FireVisual),
                     typeof(ChargePacket), typeof(ModePacket), typeof(FlightEnd), typeof(BuildFingerprint) })
        {
            text.Append('|').Append(type.Name).Append(':').Append(Marshal.SizeOf(type));
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .OrderBy(f => Marshal.OffsetOf(type, f.Name).ToInt32()))
                text.Append('|').Append(field.Name).Append(':').Append(field.FieldType.FullName)
                    .Append('@').Append(Marshal.OffsetOf(type, field.Name).ToInt32());
        }
        // The module's version ID changes with every change of the compiled code, inline numeric literals and embedded resources
        // included (deterministic builds derive it from the whole image), so an un-named formula or bundle edit cannot evade the
        // development-build handshake.
        text.Append('|').Append(typeof(EnergyLab).Assembly.ManifestModule.ModuleVersionId);
        foreach (var gear in EnergyGears.All.OrderBy(g => g.Definition.Id, StringComparer.Ordinal))
        {
            var definition = gear.Definition;
            var node = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = definition.Id,
                ["name"] = definition.Name,
                ["slot"] = (int)definition.Slot,
                ["mode"] = (int)definition.Mode,
                ["gearId"] = gear.GearId,
                ["categoryId"] = gear.CategoryId,
                ["archetypeId"] = gear.ArchetypeId,
                ["tuningType"] = definition.Tuning.GetType().FullName,
                ["tuning"] = EnergyConfiguration.TuningJson(definition.Tuning),
                ["modelKey"] = definition.Model.Key,
                ["modelSha256"] = gear.ModelDigest,
                ["look"] = EnergyLookNames.Canonical(definition.Look)
            };
            text.Append('|').Append(node.ToJsonString());
        }
        // Which guns players may choose, rundown-wide and per level: a host and a client with other lists are refused like other builds.
        text.Append('|').Append(EnergyLab.Settings.Canonical());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return new()
        {
            A = BitConverter.ToUInt64(hash, 0),
            B = BitConverter.ToUInt64(hash, 8),
            C = BitConverter.ToUInt64(hash, 16),
            D = BitConverter.ToUInt64(hash, 24)
        };
    }
}


[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FireCommand : INetEpoch
{
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    internal ulong Epoch;
    internal uint Sequence;
    internal uint CategoryId;
    internal int Mode, Dimension;
    internal float X, Y, Z, Dx, Dy, Dz, Power;
    /// <summary>The shooter's damage for this shot with its boosters (the Blast Gun's explosion); other modes ignore it.</summary>
    internal float Damage;
    internal Vector3 Start => new(X, Y, Z);
    internal Vector3 Direction => new(Dx, Dy, Dz);
    internal static FireCommand Create(uint sequence, EnergyGear gear, Vector3 start,
        Vector3 direction, int dimension, float power = 0f, float damage = 0f) => new()
        {
            Epoch = EnergyNetwork.Epoch,
            Sequence = sequence,
            Mode = (int)gear.Mode,
            CategoryId = gear.CategoryId,
            Dimension = dimension,
            X = start.x,
            Y = start.y,
            Z = start.z,
            Dx = direction.x,
            Dy = direction.y,
            Dz = direction.z,
            Power = power,
            Damage = damage
        };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FireVisual : INetEpoch
{
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    internal ulong Epoch, Shooter;
    internal uint Sequence;
    internal uint CategoryId;
    internal int Mode, Segments, HitMask;
    internal float X, Y, Z, Dx, Dy, Dz, Power;
    internal float Damage;
    internal float E0x, E0y, E0z, E1x, E1y, E1z, E2x, E2y, E2z, E3x, E3y, E3z;
    internal Vector3 Start => new(X, Y, Z);
    internal Vector3 Direction => new(Dx, Dy, Dz);
    internal bool Hit(int index) => (HitMask & (1 << index)) != 0;
    internal Vector3 End(int index) => index switch
    {
        0 => new(E0x, E0y, E0z),
        1 => new(E1x, E1y, E1z),
        2 => new(E2x, E2y, E2z),
        3 => new(E3x, E3y, E3z),
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
        Epoch = command.Epoch,
        Shooter = shooter,
        Sequence = command.Sequence,
        Mode = command.Mode,
        CategoryId = command.CategoryId,
        X = command.X,
        Y = command.Y,
        Z = command.Z,
        Dx = command.Dx,
        Dy = command.Dy,
        Dz = command.Dz,
        Power = command.Power,
        Damage = command.Damage
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ChargePacket : INetEpoch
{
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    internal ulong Epoch, Shooter;
    internal uint CategoryId;
    internal int Mode, Phase;
    internal float X, Y, Z;
    /// <summary>Seconds the shooter has been charging when this was sent: a machine that missed the start, or let the charge run out in a stall, draws it at its real power.</summary>
    internal float Held;
    internal Vector3 Position => new(X, Y, Z);
    internal static ChargePacket Create(ulong shooter, EnergyGear gear, bool start, Vector3 position, float held) => new()
    {
        Epoch = EnergyNetwork.Epoch,
        Shooter = shooter,
        Mode = (int)gear.Mode,
        CategoryId = gear.CategoryId,
        Phase = start ? 1 : 0,
        X = position.x,
        Y = position.y,
        Z = position.z,
        Held = held
    };
}

/// <summary>What a flying projectile is (<see cref="FlightEnd.Kind"/>).</summary>
internal enum FlightKind { Shell = 1, Plasma, Disc, Hole }

/// <summary>The host's word on where a projectile of <see cref="Shooter"/>'s shot number <see cref="Sequence"/> ended (a shell burst, a plasma
/// body's stop, a disc's last stop, the gravity projectile's well): the other machines move their picture there, so the picture they
/// draw is where the host dealt the damage.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FlightEnd : INetEpoch
{
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    internal ulong Epoch, Shooter;
    internal uint Sequence;
    internal int Kind;
    internal float X, Y, Z;
    internal Vector3 Position => new(X, Y, Z);
    internal static FlightEnd Create(ulong shooter, FlightKind kind, uint sequence, Vector3 at) => new()
    { Epoch = EnergyNetwork.Epoch, Shooter = shooter, Sequence = sequence, Kind = (int)kind, X = at.x, Y = at.y, Z = at.z };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ModePacket : INetEpoch
{
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    internal ulong Epoch, Shooter;
    internal uint Arm, CategoryId;
    internal int Mode;
    internal static ModePacket Create(EnergyGear? gear, uint arm) => new()
    { Epoch = EnergyNetwork.Epoch, Mode = (int)(gear?.Mode ?? EnergyMode.Off), CategoryId = gear?.CategoryId ?? 0, Arm = arm };
}
