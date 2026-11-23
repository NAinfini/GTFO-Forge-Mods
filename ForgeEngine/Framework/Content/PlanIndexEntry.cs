using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// One plan file as startup indexes it: the digest of its bytes (the same one <see cref="PlanDocument"/> computes),
/// its identity and the levels it can run in. Startup reads only this; the plan itself is decoded, parsed and
/// admitted when a level that needs it is chosen. A file whose head cannot be read this way is indexed for every
/// level, so the kernel's own parse is still what refuses it, with its own code.
/// </summary>
internal sealed record PlanIndexEntry(string Path, string Digest, string? PlanId, string? ResourceId, string? ResourceRevision,
    string[]? Levels)
{
    private static ReadOnlySpan<byte> Bom => new byte[] { 0xEF, 0xBB, 0xBF };

    /// <summary>Whether this plan belongs to the plan set of <paramref name="level"/> (`rundown:tier:index`); a plan
    /// with no level restriction belongs to every level, and an unknown level takes only those.</summary>
    internal bool Loads(string? level) => Levels == null || level != null && Array.IndexOf(Levels, level) >= 0;

    internal static PlanIndexEntry Read(string path, ReadOnlySpan<byte> file)
    {
        var digest = Convert.ToHexString(SHA256.HashData(file));
        try { return Head(path, digest, file.StartsWith(Bom) ? file[Bom.Length..] : file); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { return new PlanIndexEntry(path, digest, null, null, null, null); }
    }

    // The level an attachment restricts its plan to, by the attachment kinds of RuntimeGraphContracts: a `level` mount
    // and a `player` mount name their level in the reference, a map-object mount may carry a `level` scope. Every other
    // mount can match in any level, and a plan matches where any one of its mounts does, so one unrestricted mount
    // makes the whole plan unrestricted.
    private static PlanIndexEntry Head(string path, string digest, ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        string? planId = null, resourceId = null, revision = null;
        List<string>? levels = null;
        var unrestricted = true;
        Expect(ref reader, JsonTokenType.StartObject);
        while (Next(ref reader) == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("planId")) { Next(ref reader); planId = reader.GetString(); }
            else if (reader.ValueTextEquals("resource"))
            {
                Expect(ref reader, JsonTokenType.StartObject);
                while (Next(ref reader) == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("id")) { Next(ref reader); resourceId = reader.GetString(); }
                    else if (reader.ValueTextEquals("revision")) { Next(ref reader); revision = reader.GetString(); }
                    else { Next(ref reader); reader.Skip(); }
                }
            }
            else if (reader.ValueTextEquals("attachments"))
            {
                Expect(ref reader, JsonTokenType.StartArray);
                var any = false;
                var open = false;
                while (Next(ref reader) == JsonTokenType.StartObject)
                {
                    any = true;
                    string? kind = null, reference = null, scope = null;
                    while (Next(ref reader) == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("kind")) { Next(ref reader); kind = reader.GetString(); }
                        else if (reader.ValueTextEquals("reference")) { Next(ref reader); reference = reader.GetString(); }
                        else if (reader.ValueTextEquals("level")) { Next(ref reader); scope = reader.GetString(); }
                        else { Next(ref reader); reader.Skip(); }
                    }
                    var level = kind is "level" or "player" ? reference : kind == "map-object" ? scope : null;
                    if (level == null) open = true;
                    else (levels ??= new List<string>()).Add(level);
                }
                if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Attachments must be objects.");
                unrestricted = !any || open;
            }
            else { Next(ref reader); reader.Skip(); }
        }
        return new PlanIndexEntry(path, digest, planId, resourceId, revision, unrestricted ? null : levels!.ToArray());
    }

    private static JsonTokenType Next(ref Utf8JsonReader reader)
    {
        if (!reader.Read()) throw new JsonException("Plan head ended early.");
        return reader.TokenType;
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType token)
    {
        if (Next(ref reader) != token) throw new JsonException("Unexpected plan head shape.");
    }
}
