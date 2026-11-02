using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeCore;

namespace ForgeOrdnance;

internal static class Hashing
{
    /// <summary>The set of payload type names, independent of registration order.</summary>
    internal static ulong TypesHash(IEnumerable<string> types)
        => BitConverter.ToUInt64(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(types.OrderBy(t => t, StringComparer.Ordinal))), 0);

    /// <summary>Hash of the canonical content of every definition, ordered by Id. Source file names and order do not matter.</summary>
    public static ulong SetHash(IEnumerable<OrdnanceDefinition> definitions)
    {
        var records = definitions.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => new
        {
            d.Id,
            d.Name,
            d.Callable,
            d.Category,
            d.Icon,
            d.Description,
            Stats = d.Stats.Select(s => new { s.Name, s.Value, s.Max }).ToArray(),
            Code = d.Code.Select(c => (int)c).ToArray(),
            d.Cooldown,
            d.Uses,
            d.CallInDelay,
            d.BeamColour,
            d.PayloadType,
            Payload = StrictJson.Canonical(d.Payload)
        });
        return BitConverter.ToUInt64(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(records)), 0);
    }
}