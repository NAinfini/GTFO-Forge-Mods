using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>One guarded read that failed: the path read and the exception type, one line of a document's <c>errors</c>.</summary>
internal sealed record CaptureInventoryError(string Path, string Error);

/// <summary>
/// A once-per-level inventory document (the enemy appearance and enemy limb inventories). The rows hold only managed
/// values; serialization and the identity comparison run on the recorder's writer thread. The document is rewritten only
/// when its content without the volatile header (reason, tick, elapsed time) differs from the last one this session wrote.
/// </summary>
internal sealed class CaptureInventoryDocument
{
    private readonly string _fileName, _kind;
    private readonly int _schemaVersion;
    private byte[]? _identity;
    private string _identitySession = "";

    internal CaptureInventoryDocument(string fileName, string kind, int schemaVersion)
    {
        _fileName = fileName; _kind = kind; _schemaVersion = schemaVersion;
    }

    /// <param name="body">Writes the document's own fields between the common header and the <c>errors</c> array.</param>
    internal void Write(object inventory, string reason, long tick, long startedTimestamp, Action<Utf8JsonWriter> body,
        IReadOnlyList<CaptureInventoryError> errors)
    {
        var elapsedMs = (Stopwatch.GetTimestamp() - startedTimestamp) * 1000.0 / Stopwatch.Frequency;
        var session = RecSession.SessionId;
        var gameVersion = RecUnity.GameVersion;
        byte[]? nextIdentity = null;
        if (!RecSession.WriteFrozenDocument(_fileName, () =>
        {
            var identity = Serialize("", 0, 0);
            if (_identitySession == session && _identity != null && _identity.AsSpan().SequenceEqual(identity)) return null;
            nextIdentity = identity;
            return Serialize(reason, tick, elapsedMs);
        }, RecFrozenBytes.Measure(inventory) + RecFrozenBytes.Measure(new[] { reason, session, gameVersion }), success =>
        {
            if (!success) { _identity = null; return; }
            if (nextIdentity != null) { _identity = nextIdentity; _identitySession = session; }
        })) throw new InvalidOperationException(Path.GetFileNameWithoutExtension(_fileName) + "-recorder-backpressure");

        byte[] Serialize(string documentReason, long documentTick, double documentElapsedMs)
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.WriteStartObject();
                json.WriteNumber("schemaVersion", _schemaVersion);
                json.WriteString("kind", _kind);
                json.WriteString("gameVersion", gameVersion);
                json.WriteBoolean("gameExecuted", true);
                json.WriteString("reason", documentReason);
                json.WriteNumber("tick", documentTick);
                json.WriteNumber("elapsedMs", Math.Round(documentElapsedMs, 3));
                body(json);
                json.WritePropertyName("errors");
                json.WriteStartArray();
                foreach (var error in errors)
                {
                    json.WriteStartObject();
                    json.WriteString("path", error.Path);
                    json.WriteString("error", error.Error);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            return stream.ToArray();
        }
    }
}
