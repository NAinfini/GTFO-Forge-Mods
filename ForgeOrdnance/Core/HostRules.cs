using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ForgeOrdnance;

/// <summary>The host's effective rules for the expedition selected in its lobby.</summary>
internal sealed class HostRulesSnapshot
{
    internal HostRulesSnapshot(string levelKey, ulong definitionHash, int loadoutSize, uint[]? allowedKeys,
        double cooldownMultiplier, double usesMultiplier, double maxThrowDistance, bool callMenu, bool lobbyPicker = true)
    {
        LevelKey = levelKey;
        DefinitionHash = definitionHash;
        LoadoutSize = loadoutSize;
        AllowedKeys = allowedKeys;
        CooldownMultiplier = cooldownMultiplier;
        UsesMultiplier = usesMultiplier;
        MaxThrowDistance = maxThrowDistance;
        CallMenu = callMenu;
        LobbyPicker = lobbyPicker;
    }

    internal string LevelKey { get; }
    internal ulong DefinitionHash { get; }
    internal int LoadoutSize { get; }
    /// <summary>Null means every definition; an empty array means none.</summary>
    internal uint[]? AllowedKeys { get; }
    internal double CooldownMultiplier { get; }
    internal double UsesMultiplier { get; }
    internal double MaxThrowDistance { get; }
    /// <summary>False when the level's developer turned the players' call-ins off (<see cref="OrdnanceLevelRules.CallMenu"/>).</summary>
    internal bool CallMenu { get; }
    /// <summary>False when the level's developer turned the lobby choice off (<see cref="OrdnanceLevelRules.LobbyPicker"/>).</summary>
    internal bool LobbyPicker { get; }

    internal static HostRulesSnapshot From(string? levelKey, DefinitionSet set, OrdnanceLevelRules rules)
    {
        uint[]? allowed = null;
        if (rules.Allowed != null)
        {
            allowed = rules.Allowed
                .Select(id => set.TryGetById(id, out var definition) ? definition.Key : (uint?)null)
                .Where(key => key.HasValue).Select(key => key!.Value).Distinct().OrderBy(key => key).ToArray();
        }
        return new HostRulesSnapshot(levelKey ?? "", set.Hash, rules.LoadoutSize, allowed,
            rules.CooldownMultiplier, rules.UsesMultiplier, rules.MaxThrowDistance, rules.CallMenu, rules.LobbyPicker);
    }

    internal OrdnanceLevelRules ToSettings(DefinitionSet set)
    {
        IReadOnlyList<string>? allowed = null;
        if (AllowedKeys != null)
        {
            var ids = new List<string>(AllowedKeys.Length);
            foreach (var key in AllowedKeys)
                if (set.TryGet(key, out var definition)) ids.Add(definition.Id);
            allowed = ids;
        }
        return new OrdnanceLevelRules
        {
            LoadoutSize = LoadoutSize,
            Allowed = allowed,
            CooldownMultiplier = CooldownMultiplier,
            UsesMultiplier = UsesMultiplier,
            MaxThrowDistance = MaxThrowDistance,
            CallMenu = CallMenu,
            LobbyPicker = LobbyPicker
        };
    }
}


/// <summary>The bounded binary form of the host's rules, published as the host's lobby value (<see cref="Messages.LobbyRules"/>).</summary>
internal static class HostRulesWire
{
    private const uint Magic = 0x53524F46; // FORS
    internal const int MaxPayloadBytes = 8192;
    internal const int MaxLevelKeyBytes = 512;
    internal const int MaxAllowedKeys = 1900;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(HostRulesSnapshot rules)
    {
        var level = Utf8.GetBytes(rules.LevelKey);
        if (level.Length > MaxLevelKeyBytes) throw new InvalidOperationException("The expedition key is too long to synchronize.");
        if (rules.AllowedKeys is { Length: > MaxAllowedKeys }) throw new InvalidOperationException("The allowed call-in list is too long to synchronize.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            writer.Write(Magic);
            writer.Write(rules.DefinitionHash);
            writer.Write((ushort)level.Length);
            writer.Write(level);
            writer.Write((byte)rules.LoadoutSize);
            writer.Write(rules.CooldownMultiplier);
            writer.Write(rules.UsesMultiplier);
            writer.Write(rules.MaxThrowDistance);
            writer.Write(rules.CallMenu);
            writer.Write(rules.LobbyPicker);
            writer.Write((short)(rules.AllowedKeys?.Length ?? -1));
            if (rules.AllowedKeys != null) foreach (var key in rules.AllowedKeys) writer.Write(key);
        }
        var result = stream.ToArray();
        if (result.Length > MaxPayloadBytes) throw new InvalidOperationException("The host rules are too large to synchronize.");
        return result;
    }

    internal static bool TryDecode(byte[] bytes, out HostRulesSnapshot? rules)
    {
        rules = null;
        if (bytes == null || bytes.Length > MaxPayloadBytes || bytes.Length < 43) return false;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream, Utf8, true);
            if (reader.ReadUInt32() != Magic) return false;
            var hash = reader.ReadUInt64();
            var levelLength = reader.ReadUInt16();
            if (levelLength > MaxLevelKeyBytes || levelLength > stream.Length - stream.Position) return false;
            var levelBytes = reader.ReadBytes(levelLength);
            if (levelBytes.Length != levelLength) return false;
            var level = Utf8.GetString(levelBytes);
            var size = reader.ReadByte();
            var cooldown = reader.ReadDouble();
            var uses = reader.ReadDouble();
            var distance = reader.ReadDouble();
            var menu = reader.ReadByte();
            var picker = reader.ReadByte();
            var count = reader.ReadInt16();
            if (size is < 1 or > Messages.MaxLoadout || !Finite(cooldown, 0, 10) || !Finite(uses, 0, 10)
                || !Finite(distance, 5, 500) || menu > 1 || picker > 1 || count < -1 || count > MaxAllowedKeys) return false;
            uint[]? allowed = null;
            if (count >= 0)
            {
                if (stream.Length - stream.Position != count * sizeof(uint)) return false;
                allowed = new uint[count];
                var unique = new HashSet<uint>();
                for (var i = 0; i < count; i++)
                {
                    allowed[i] = reader.ReadUInt32();
                    if (!unique.Add(allowed[i])) return false;
                }
            }
            if (stream.Position != stream.Length) return false;
            rules = new HostRulesSnapshot(level, hash, size, allowed, cooldown, uses, distance, menu == 1, picker == 1);
            return true;
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException or IOException) { return false; }
    }

    private static bool Finite(double value, double min, double max)
        => !double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max;
}
