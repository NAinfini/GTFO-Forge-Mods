using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.Network;

namespace ForgeEngine.GameBindings;

/// <summary>Domain receipts carried by the existing variable snapshot/delta and checkpoint paths.</summary>
internal static partial class DomainStateChannel
{
    /// <summary>One registered domain source. Its owner calls <see cref="Changed"/> after every change it makes, so a
    /// session delta carries what changed in this source and no other source is captured for it.</summary>
    public sealed class NativeStateSource : IDisposable
    {
        internal readonly IForgeEngine Runtime; internal readonly string Key;
        internal readonly Func<JsonElement> Capture; internal readonly Action<JsonElement> Restore;
        /// <summary>The version this source's state is at, and the version the session stream last carried.</summary>
        internal long Version, Carried;
        /// <summary>The state the session stream last carried from here (the host) or last brought here (everyone else),
        /// the version this source was at then, and that state's place in the stream. The host writes the next delta as
        /// the rows that differ from it, and a receiver applies those rows to its own copy, which is the same state while
        /// both count the same <see cref="Revision"/>.</summary>
        internal JsonElement? Held;
        internal long HeldVersion, Revision;
        /// <summary>The stream has carried exactly <see cref="Held"/>, so the next delta can be written against it. A
        /// state that was restored here was not sent from here, and goes out whole.</summary>
        internal bool HeldCarried => Held != null && HeldVersion == Carried;
        internal Action? Detached;
        internal NativeStateSource(IForgeEngine runtime, string key, Func<JsonElement> capture, Action<JsonElement> restore)
        { Runtime = runtime; Key = key; Capture = capture; Restore = restore; }
        /// <summary>This source's state changed: the next session delta carries it.</summary>
        public void Changed()
        {
            if (Runtime is not RuntimeKernel kernel) throw new InvalidOperationException("native-state-runtime");
            Version++; kernel.MarkNativeStateChanged();
        }
        public void Dispose() { if (NativeSources.Remove(this)) Detached?.Invoke(); }
    }
    private static readonly List<NativeStateSource> NativeSources = new();
    /// <summary>Makes this channel the kernel's native state: its snapshots, deltas, checkpoints and migrations carry
    /// the sources registered for it.</summary>
    internal static void BindNativeState(RuntimeKernel kernel)
    {
        kernel.NativeStateCapture = mode => CaptureNativeState(kernel, mode);
        kernel.NativeStateValidate = value => ResolveNativeState(kernel, value);
        kernel.NativeStateRestore = value => RestoreNativeState(kernel, ResolveNativeState(kernel, value));
        kernel.NativeStateWriteDelta = writer => WriteNativeDelta(kernel, writer);
        kernel.NativeStateReadDelta = (reader, count) =>
        {
            var resolved = ReadNativeDelta(kernel, reader, count);
            return () => RestoreNativeState(kernel, resolved);
        };
    }
    public static NativeStateSource RegisterNativeState(IForgeEngine runtime, string key, Func<JsonElement> capture, Action<JsonElement> restore)
    {
        if (runtime == null || string.IsNullOrWhiteSpace(key) || capture == null || restore == null) throw new ArgumentException("native-state-source");
        foreach (var source in NativeSources) if (ReferenceEquals(source.Runtime, runtime) && source.Key == key) throw new InvalidOperationException("native-state-duplicate");
        // A new source is owed to the stream once: a client that joined before it existed holds none of its state.
        var added = new NativeStateSource(runtime, key, capture, restore) { Version = 1 }; NativeSources.Add(added); return added;
    }
    internal const string BaseCode = "native-state-base";
    private static readonly ArrayBufferWriter<byte> CaptureBuffer = new(4096);

    /// <summary>The sources of one runtime for <paramref name="mode"/>, each as <c>{"rev","state"}</c>: its whole state and
    /// that state's place in the stream. An export settles what it carried, and only after every capture it needed
    /// succeeded, so a failed capture leaves every source still owed.</summary>
    internal static JsonElement CaptureNativeState(IForgeEngine runtime, NativeCaptureMode mode)
    {
        List<(NativeStateSource Source, long Version, long Revision, JsonElement State)>? settled = mode == NativeCaptureMode.Complete ? null : new();
        CaptureBuffer.Clear();
        using (var writer = new Utf8JsonWriter(CaptureBuffer))
        {
            writer.WriteStartObject();
            foreach (var source in NativeSources)
            {
                if (!ReferenceEquals(source.Runtime, runtime)) continue;
                var version = source.Version;
                var state = source.Capture();
                // A state the stream has not carried takes the next place in it; an image for a checkpoint or a
                // migration is not part of the stream and names the place this source stands at.
                var revision = mode == NativeCaptureMode.Complete || source.HeldCarried && version == source.Carried
                    ? source.Revision : source.Revision + 1;
                writer.WriteStartObject(source.Key);
                writer.WriteNumber("rev", revision);
                writer.WritePropertyName("state");
                state.WriteTo(writer);
                writer.WriteEndObject();
                settled?.Add((source, version, revision, state));
            }
            writer.WriteEndObject();
        }
        if (settled != null) Settle(settled);
        using var document = JsonDocument.Parse(CaptureBuffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void Settle(List<(NativeStateSource Source, long Version, long Revision, JsonElement State)> settled)
    {
        foreach (var (source, version, revision, state) in settled)
        {
            source.Carried = source.HeldVersion = version;
            source.Revision = revision;
            source.Held = state.Clone();
        }
    }

    private const byte WholeState = 0, PatchedState = 1;

    /// <summary>A session delta's native part: the count of the sources changed since the stream last carried them, then
    /// each as its key, a form byte and a 7-bit number. Form 0 is the whole state (<see cref="StatePatch.WriteValue"/>) and
    /// the number its place in the stream; form 1, written against what the stream last carried, is the rows that differ
    /// (<see cref="StatePatch"/>) and the number the place it starts at. It settles what it carried once every capture
    /// succeeded.</summary>
    internal static void WriteNativeDelta(IForgeEngine runtime, BinaryWriter writer)
    {
        var settled = new List<(NativeStateSource Source, long Version, long Revision, JsonElement State)>();
        foreach (var source in NativeSources)
            if (ReferenceEquals(source.Runtime, runtime) && source.Version != source.Carried)
                settled.Add((source, source.Version, source.Revision + 1, source.Capture()));
        writer.Write7BitEncodedInt(settled.Count);
        foreach (var (source, _, revision, state) in settled)
        {
            writer.Write(source.Key);
            if (source.HeldCarried)
            {
                writer.Write(PatchedState);
                writer.Write7BitEncodedInt64(source.Revision);
                StatePatch.Write(writer, source.Held!.Value, state);
            }
            else
            {
                writer.Write(WholeState);
                writer.Write7BitEncodedInt64(revision);
                StatePatch.WriteValue(writer, state);
            }
        }
        Settle(settled);
    }

    /// <summary>Reads a delta's native part of <paramref name="count"/> sources into the states they stand for. A patch is
    /// applied here to the state it was written against, changing nothing, so the restore that follows only applies.</summary>
    private static List<(NativeStateSource Source, JsonElement Value, long Revision)> ReadNativeDelta(IForgeEngine runtime, BinaryReader reader, int count)
    {
        if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new FormatException("source count out of range");
        var resolved = new List<(NativeStateSource Source, JsonElement Value, long Revision)>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadString();
            if (!seen.Add(key)) throw new RuntimeContractException("native-state-duplicate", key);
            var source = Find(runtime, key);
            var form = reader.ReadByte();
            var number = reader.Read7BitEncodedInt64();
            if (form == WholeState) resolved.Add((source, RuntimeJson.Parse(RuntimeVariableStore.Text.GetString(StatePatch.ReadValue(reader))), number));
            else if (form == PatchedState)
            {
                if (source.Held is not { } held || source.Revision != number)
                    throw new RuntimeContractException(BaseCode, source.Key + " holds revision " + source.Revision + ", the change starts at " + number + ".");
                resolved.Add((source, StatePatch.Apply(held, reader), number + 1));
            }
            else throw new RuntimeContractException("native-state-object", "Native state of " + key + " is neither a whole state nor a change.");
        }
        return resolved;
    }

    private static NativeStateSource Find(IForgeEngine runtime, string key)
    {
        foreach (var source in NativeSources) if (ReferenceEquals(source.Runtime, runtime) && source.Key == key) return source;
        throw new RuntimeContractException("native-state-source-missing", key);
    }

    /// <summary>Applies resolved sources atomically: a failure restores the sources already applied.</summary>
    private static void RestoreNativeState(IForgeEngine runtime, List<(NativeStateSource Source, JsonElement Value, long Revision)> resolved)
    {
        // What a source is put back to: the state it was last given, while nothing has changed it since, else what it captures now.
        var original = new JsonElement[resolved.Count];
        for (var index = 0; index < resolved.Count; index++)
        {
            var source = resolved[index].Source;
            original[index] = source.Held is { } held && source.HeldVersion == source.Version ? held : source.Capture().Clone();
        }
        var applied = -1;
        try
        {
            for (var index = 0; index < resolved.Count; index++)
            {
                applied = index;
                resolved[index].Source.Restore(resolved[index].Value);
            }
            // A restore changed every source it named, so a session this machine hosts is owed each of them.
            for (var index = 0; index < resolved.Count; index++)
            {
                var (source, value, revision) = resolved[index];
                source.Held = value; source.Revision = revision; source.HeldVersion = ++source.Version;
            }
            if (resolved.Count != 0 && runtime is RuntimeKernel kernel) kernel.MarkNativeStateChanged();
        }
        catch (Exception error)
        {
            var rollbackFailures = new List<string>();
            for (var index = applied; index >= 0; index--)
                try { resolved[index].Source.Restore(original[index]); }
                catch (Exception rollback) { rollbackFailures.Add(resolved[index].Source.Key + ":" + rollback.GetType().Name + ":" + rollback.Message); }
            if (rollbackFailures.Count != 0)
                throw new RuntimeContractException("native-state-rollback-failed",
                    "Native restore failed at " + resolved[applied].Source.Key + " (" + error.GetType().Name + ":" + error.Message
                    + ") and rollback was incomplete: " + string.Join(" | ", rollbackFailures));
            if (error is RuntimeContractException) throw;
            throw new RuntimeContractException("native-state-restore-failed",
                resolved[applied].Source.Key + ":" + error.GetType().Name + ":" + error.Message);
        }
    }
    /// <summary>A complete image's sources as the states they stand for and those states' places in the stream. A delta is
    /// binary (<see cref="ReadNativeDelta"/>): a patch is taken only by a machine that holds the very state it was written
    /// against; any other is told so (<see cref="BaseCode"/>) and asks the host for the whole document.</summary>
    private static List<(NativeStateSource Source, JsonElement Value, long Revision)> ResolveNativeState(IForgeEngine runtime, JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) throw new RuntimeContractException("native-state-object", "Native state must be an object.");
        // Enemy receivers and recipient-indexed sources publish complete state, including empty contributions.
        // Check every required key of a complete image before invoking any receiver, so an omitted domain cannot
        // leave a partially restored world or silently retain this machine's earlier private presentation.
        foreach (var source in NativeSources)
            if (ReferenceEquals(source.Runtime, runtime)
                && (source.Key.StartsWith("forge.enemy.", StringComparison.Ordinal) || RecipientStateRequired(runtime, source.Key))
                && !state.TryGetProperty(source.Key, out _)) throw new RuntimeContractException("native-state-key-missing", source.Key);
        var resolved = new List<(NativeStateSource Source, JsonElement Value, long Revision)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in state.EnumerateObject())
        {
            if (!seen.Add(entry.Name)) throw new RuntimeContractException("native-state-duplicate", entry.Name);
            var found = Find(runtime, entry.Name);
            var members = 0;
            if (entry.Value.ValueKind == JsonValueKind.Object) foreach (var _ in entry.Value.EnumerateObject()) members++;
            if (members != 2 || !entry.Value.TryGetProperty("rev", out var revision) || !entry.Value.TryGetProperty("state", out var whole)
                || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var place))
                throw new RuntimeContractException("native-state-object", "Native state of " + found.Key + " is not a whole state.");
            resolved.Add((found, whole.Clone(), place));
        }
        return resolved;
    }
}
