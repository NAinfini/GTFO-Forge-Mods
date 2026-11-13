using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>The channel content (<c>Channel.Content</c>) two peers must share: SHA-256 hex of this build and its definitions.</summary>
internal static class BuildFingerprint
{
    internal static string Compute()
    {
        var text = new StringBuilder();
        // The message layouts are ForgeCore's own fingerprint. The module's version ID changes with every change of the compiled code,
        // inline numeric literals and embedded resources included (deterministic builds derive it from the whole image), so an
        // un-named formula or bundle edit cannot evade the development-build handshake.
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
        return ForgeCore.Hash.Sha256Hex(text.ToString());
    }
}


[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FireCommand
{
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
internal struct FireVisual
{
    internal ulong Shooter;
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
internal struct ChargePacket
{
    internal ulong Shooter;
    internal uint CategoryId;
    internal int Mode, Phase;
    internal float X, Y, Z;
    /// <summary>Seconds the shooter has been charging when this was sent: a machine that missed the start, or let the charge run out in a stall, draws it at its real power.</summary>
    internal float Held;
    internal Vector3 Position => new(X, Y, Z);
    internal static ChargePacket Create(ulong shooter, EnergyGear gear, bool start, Vector3 position, float held) => new()
    {
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
internal struct FlightEnd
{
    internal ulong Shooter;
    internal uint Sequence;
    internal int Kind;
    internal float X, Y, Z;
    internal Vector3 Position => new(X, Y, Z);
    internal static FlightEnd Create(ulong shooter, FlightKind kind, uint sequence, Vector3 at) => new()
    { Shooter = shooter, Sequence = sequence, Kind = (int)kind, X = at.x, Y = at.y, Z = at.z };
}

/// <summary>The gun a player has armed (<see cref="EnergyNetwork.Armed"/>, each player's own row): the other machines mount its model and
/// make the voices and buffers its shots need when the shooter takes it up, not on its first shot. <see cref="Arm"/> goes up with every
/// arming, so a machine whose copy of this shooter's gun stopped after a fault builds it again.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ArmedGun
{
    internal uint Arm, CategoryId;
    internal int Mode;
}
