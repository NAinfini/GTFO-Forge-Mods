using System;
using System.IO;
using System.Text;
using ForgeEngine.Framework;
using ForgeEngine.Network;

namespace ForgeEngine.GameBindings;

/// <summary>
/// What a domain command is on its way to the host and back: the limits <see cref="DomainCommands.Send"/> checks, the body
/// a client's request and the host's answer travel as, and the one place the host runs the registered executor. The request
/// itself (who sent it, in which world, exactly once) is ForgeCore's.
/// </summary>
internal static class DomainCommandRouting
{
    internal const int MaximumPayloadBytes = NetworkProtocol.MaximumCommandBytes;
    private const int MaximumResourceCharacters = 128;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static DomainCommandResult Refused(string code, string detail = "")
        => new(CommandStatuses.Rejected, CommitStates.None, code, detail, 0);

    /// <summary>The host runs one admitted request through the executor registered for its endpoint. A handler that throws, or
    /// answers with a result the command vocabulary does not allow, leaves the commit unknown rather than claimed.</summary>
    internal static DomainCommandResult Execute(string endpoint, DomainCommandRequest request)
    {
        if (!DomainCommands.TryGet(endpoint, out var execute) || execute == null)
            return Refused(NetworkCodes.NoConsumer, "No executor registered for endpoint '" + endpoint + "'.");
        DomainCommandResult result;
        try { result = execute(request); }
        catch (Exception error)
        {
            return new DomainCommandResult(CommandStatuses.Failed, CommitStates.Unknown,
                "domain-handler-exception", error.GetType().Name + ": " + error.Message, 0);
        }
        if (!Valid(result, out var violation))
            return new DomainCommandResult(CommandStatuses.Failed, CommitStates.Unknown, "invalid-domain-result", violation, 0);
        return new DomainCommandResult(result.Status, result.Commit, result.Code ?? "", result.Detail ?? "", result.FactCount);
    }

    internal static void RequireEndpoint(string endpoint, string parameter)
    {
        if (string.IsNullOrEmpty(endpoint)) throw new ArgumentException("A domain command endpoint is required.", parameter);
        if (endpoint.Length > NetworkProtocol.TextSlotBytes)
            throw new ArgumentOutOfRangeException(parameter, "A domain command endpoint cannot exceed "
                + NetworkProtocol.TextSlotBytes + " characters.");
        foreach (var character in endpoint)
            if (character > 0xFF) throw new ArgumentException("A domain command endpoint must be Latin-1.", parameter);
    }

    internal static void RequireResource(string resourceId, string parameter)
    {
        if (!ValidResource(resourceId))
            throw new ArgumentException("A domain command resource id must follow the trigger-zone id contract.", parameter);
        try { Utf8.GetByteCount(resourceId); }
        catch (EncoderFallbackException error) { throw new ArgumentException("A domain command resource id must be valid Unicode.", parameter, error); }
    }

    internal static void RequirePayload(int payloadBytes, string parameter)
    {
        if (payloadBytes > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(parameter, "A domain command payload cannot exceed " + MaximumPayloadBytes + " bytes.");
    }

    private static bool ValidResource(string? value)
    {
        if (string.IsNullOrEmpty(value) || value!.Length > MaximumResourceCharacters || value.Trim() != value
            || value.IndexOf(':') >= 0 || value.IndexOf('/') >= 0) return false;
        foreach (var character in value)
            if (character < 0x20 || character == 0x7F) return false;
        return true;
    }

    /// <summary>A request's body: the endpoint, the resource, the life epoch, then the domain's own payload.</summary>
    internal static byte[] Encode(string endpoint, string resourceId, long lifeEpoch, byte[] payload)
    {
        using var stream = new MemoryStream(payload.Length + 64);
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            writer.Write(endpoint);
            writer.Write(resourceId);
            writer.Write(lifeEpoch);
            writer.Write(payload);
        }
        return stream.ToArray();
    }

    internal static bool TryDecode(byte[] body, out string endpoint, out string resourceId, out long lifeEpoch, out byte[] payload)
    {
        endpoint = resourceId = "";
        lifeEpoch = 0;
        payload = Array.Empty<byte>();
        try
        {
            using var reader = new BinaryReader(new MemoryStream(body), Utf8);
            endpoint = reader.ReadString();
            resourceId = reader.ReadString();
            lifeEpoch = reader.ReadInt64();
            payload = reader.ReadBytes(body.Length);
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException or ArgumentException) { return false; }
        return endpoint.Length != 0 && endpoint.Length <= NetworkProtocol.TextSlotBytes && ValidResource(resourceId)
            && lifeEpoch >= 0 && payload.Length <= MaximumPayloadBytes;
    }

    /// <summary>An answer's body: the host's verdict.</summary>
    internal static byte[] Encode(DomainCommandResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            writer.Write(result.Status);
            writer.Write(result.Commit);
            writer.Write(result.Code);
            writer.Write(result.Detail);
            writer.Write(result.FactCount);
        }
        return stream.ToArray();
    }

    /// <summary>The host's verdict as a client reads it. An answer that is not one leaves the commit unknown.</summary>
    internal static DomainCommandResult ReadResult(byte[] body)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(body), Utf8);
            var result = new DomainCommandResult(reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadInt32());
            if (Valid(result, out var violation)) return result;
            return new DomainCommandResult(CommandStatuses.Failed, CommitStates.Unknown, "invalid-domain-result", violation, 0);
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException or ArgumentException)
        { return new DomainCommandResult(CommandStatuses.Failed, CommitStates.Unknown, "invalid-domain-result", "unreadable-answer", 0); }
    }

    private static bool Valid(DomainCommandResult result, out string violation)
    {
        violation = "";
        if (result.FactCount < 0 || result.FactCount > CommandResult.MaximumFacts)
        { violation = "fact-count-out-of-range"; return false; }
        switch (result.Status)
        {
            case CommandStatuses.Succeeded:
                if (result.Commit == CommitStates.None && result.FactCount == 0) return true;
                if (result.Commit != CommitStates.Confirmed) { violation = "succeeded-commit-state"; return false; }
                return true;
            case CommandStatuses.Partial:
                if (result.Commit is not (CommitStates.Confirmed or CommitStates.Unknown or CommitStates.None))
                { violation = "partial-commit-state"; return false; }
                return true;
            case CommandStatuses.Rejected:
            case CommandStatuses.Cancelled:
            case CommandStatuses.Expired:
                if (result.Commit != CommitStates.None) { violation = "terminal-status-requires-none"; return false; }
                if (result.FactCount != 0) { violation = "terminal-status-cannot-carry-facts"; return false; }
                return true;
            case CommandStatuses.Failed:
                if (result.Commit is not (CommitStates.None or CommitStates.Unknown))
                { violation = "failed-commit-state"; return false; }
                if (result.Commit == CommitStates.None && result.FactCount != 0)
                { violation = "failed-none-cannot-carry-facts"; return false; }
                return true;
            default:
                violation = "unknown-status";
                return false;
        }
    }
}
