using System;
using System.IO;
using System.Text;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.Network;

/// <summary>
/// The bodies of the engine's own messages, written field by field: a presentation step and its answer, and an owner message.
/// The host and every other machine write and read them here and nowhere else. Every text is length-prefixed UTF-8, every
/// JSON value (a step's inputs, a result's outputs) travels as its own text behind a four-byte length, and a result's status
/// and commit state are one byte each. A body that is cut
/// short, carries a length past its end or an unknown kind, holds text that is not UTF-8 or JSON the runtime does not admit,
/// or has bytes left over, is not read at all.
/// </summary>
internal static class EngineMessages
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const byte Apply = 0, Result = 1, Ended = 2;

    internal static byte[] Encode(PresentationOrder order)
    {
        using var stream = new MemoryStream(256);
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            writer.Write(order.PlanId);
            writer.Write(order.StepIndex);
            writer.Write(order.CommandId);
            writer.Write(order.Sequence);
            writer.Write(order.Recipient);
            writer.Write(order.WorldEpoch);
            writer.Write(order.Tick);
            Write(writer, order.Inputs);
            writer.Write(order.Prediction);
        }
        return stream.ToArray();
    }

    internal static bool TryDecode(byte[] body, out PresentationOrder order)
    {
        PresentationOrder? read = null;
        var whole = TryRead(body, reader => read = new PresentationOrder(reader.ReadString(), reader.ReadInt32(), reader.ReadString(),
            reader.ReadInt64(), reader.ReadString(), reader.ReadInt64(), reader.ReadInt64(), ReadJson(reader), reader.ReadInt64()));
        order = read!;
        return whole;
    }

    internal static byte[] Encode(CommandResult result)
    {
        using var stream = new MemoryStream(256);
        using (var writer = new BinaryWriter(stream, Utf8, true)) Write(writer, result);
        return stream.ToArray();
    }

    internal static bool TryDecode(byte[] body, out CommandResult result)
    {
        CommandResult? read = null;
        var whole = TryRead(body, reader => read = ReadResult(reader));
        result = read!;
        return whole;
    }

    /// <summary>
    /// An "apply" carries the whole request the holder runs. An answer ("result" or "ended") names only the step it answers —
    /// its command id and world — because the host rebuilds every other field from the exact request it sent; an "ended" adds
    /// its reason and, when it reports a spontaneous ending, the ending's slot and handle. Then the result.
    /// </summary>
    internal static byte[] Encode(OwnerChannelMessage message)
    {
        using var stream = new MemoryStream(message.Kind == "apply" ? 512 : 64);
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            var request = message.Request;
            writer.Write(message.Kind switch
            {
                "apply" => Apply,
                "result" => Result,
                "ended" => Ended,
                _ => throw new ArgumentException("Unknown owner message kind '" + message.Kind + "'.", nameof(message))
            });
            if (message.Kind == "apply")
            {
                writer.Write(request.Provider);
                writer.Write(request.PlanId);
                writer.Write(request.NodeId);
                writer.Write(request.CommandId);
                writer.Write(request.BindingId);
                writer.Write(request.StepIndex);
                writer.Write(request.Holder);
                writer.Write(request.EventId);
                writer.Write(request.ScopeId);
                writer.Write(request.WorldEpoch);
                writer.Write(request.SimulationTick);
                Write(writer, request.Inputs);
                writer.Write(request.HolderIdentity);
                Write(writer, request.Identities);
                writer.Write(request.Operation);
                Write(writer, request.Data);
                return stream.ToArray();
            }
            writer.Write(request.CommandId);
            writer.Write(request.WorldEpoch);
            if (message.Kind == "ended")
            {
                writer.Write(message.Reason);
                var ending = OwnerCommandLedger.IsEnding(request);
                writer.Write(ending);
                if (ending)
                {
                    writer.Write(request.Data.GetProperty("endedSlot").GetString() ?? "");
                    Write(writer, request.Data.GetProperty("endedHandle"));
                }
            }
            Write(writer, message.Result!);
        }
        return stream.ToArray();
    }

    /// <summary>An answer reads as a request that holds only what the answer names: its command id, its world and, for a
    /// spontaneous ending, the ending (<see cref="OwnerCommandLedger.IsEnding"/>). The host takes every other field from the
    /// request it sent.</summary>
    internal static bool TryDecode(byte[] body, out OwnerChannelMessage message)
    {
        OwnerChannelMessage? read = null;
        var whole = TryRead(body, reader =>
        {
            var kind = reader.ReadByte();
            if (kind == Apply)
            {
                var request = new OwnerOutput(reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
                    reader.ReadString(), reader.ReadInt32(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
                    reader.ReadInt64(), reader.ReadInt64(), ReadJson(reader))
                {
                    HolderIdentity = reader.ReadUInt64(),
                    Identities = ReadJson(reader),
                    Operation = reader.ReadString(),
                    Data = ReadJson(reader)
                };
                read = new OwnerChannelMessage("apply", request, null, "");
                return;
            }
            if (kind != Result && kind != Ended) throw new FormatException("unknown owner message kind");
            var named = new OwnerOutput("", "", "", reader.ReadString(), "", 0, "", "", "", reader.ReadInt64(), 0, RuntimeJson.EmptyObject);
            var reason = "";
            if (kind == Ended)
            {
                reason = reader.ReadString();
                if (reader.ReadBoolean())
                    named = named with { Data = RuntimeJson.From(new { application = RuntimeJson.EmptyObject, endedSlot = reader.ReadString(), endedHandle = ReadJson(reader) }) };
            }
            read = new OwnerChannelMessage(kind == Result ? "result" : "ended", named, ReadResult(reader), reason);
        });
        message = read!;
        return whole;
    }

    /// <summary>Runs <paramref name="read"/> over the whole body; false when the body is not exactly one well-formed message.</summary>
    private static bool TryRead(byte[] body, Action<BinaryReader> read)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(body), Utf8);
            read(reader);
            return reader.BaseStream.Position == reader.BaseStream.Length;
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException or ArgumentException or FormatException
            or RuntimeContractException)
        { return false; }
    }

    /// <summary>A result's status and commit state travel as their places in the wire's <c>execution_outcome</c> and
    /// <c>commit_state</c> sets.</summary>
    private static readonly string[] Statuses = EnumSet("execution_outcome"), Commits = EnumSet("commit_state");

    private static string[] EnumSet(string name) => Array.Find(Wire.EnumSets, set => set.Name == name).Members
        ?? throw new InvalidOperationException("The wire has no '" + name + "' set.");

    private static byte Place(string[] members, string member)
    {
        var place = Array.IndexOf(members, member);
        return place >= 0 ? (byte)place : throw new ArgumentException("'" + member + "' is not a wire member.");
    }

    private static string Member(string[] members, byte place)
        => place < members.Length ? members[place] : throw new FormatException("member place out of range");

    private static void Write(BinaryWriter writer, CommandResult result)
    {
        writer.Write(Place(Statuses, result.Status));
        writer.Write(Place(Commits, result.CommitState));
        writer.Write(result.Code);
        writer.Write(result.Detail);
        Write(writer, result.Outputs);
        writer.Write(result.Facts.Count);
        foreach (var fact in result.Facts)
        {
            writer.Write(fact.BindingId);
            Write(writer, fact.Outputs);
        }
    }

    /// <summary>A result whose fields are all there but do not make a result the runtime admits reads as an unknown commit.</summary>
    private static CommandResult ReadResult(BinaryReader reader)
    {
        string status = Member(Statuses, reader.ReadByte()), commit = Member(Commits, reader.ReadByte()), code = reader.ReadString(), detail = reader.ReadString();
        var outputs = ReadJson(reader);
        var count = reader.ReadInt32();
        if (count < 0 || count > CommandResult.MaximumFacts) throw new FormatException("fact count out of range");
        var facts = new RuntimeFact[count];
        for (var index = 0; index < count; index++) facts[index] = new RuntimeFact(reader.ReadString(), ReadJson(reader));
        try { return CommandResult.Create(status, commit, code, detail, outputs, facts); }
        catch (Exception error) { return CommandResult.FailedUnknown("invalid-owner-result", RuntimeDiagnostics.Describe(error)); }
    }

    private static void Write(BinaryWriter writer, JsonElement value)
    {
        var text = RawJson.Bytes(value);
        writer.Write(text.Length);
        writer.Write(text);
    }

    private static JsonElement ReadJson(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new FormatException("JSON length out of range");
        return RuntimeJson.Parse(Utf8.GetString(reader.ReadBytes(length)));
    }
}
