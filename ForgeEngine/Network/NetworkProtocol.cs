using System;

namespace ForgeEngine.Network;

/// <summary>The numbers the engine's own message bodies share. Transport, membership, the world number and message layouts are
/// ForgeCore's (<c>NetChannel</c>); these bound what the engine writes inside a body.</summary>
internal static class NetworkProtocol
{
    /// <summary>The engine's ForgeCore channel.</summary>
    internal const string ChannelId = "NAinfini.Forge";

    /// <summary>The longest text a one-byte length prefix carries in a handshake detail or a command endpoint.</summary>
    internal const int TextSlotBytes = 128;

    /// <summary>A plan-set digest is SHA-256.</summary>
    internal const int PlanSetDigestBytes = 32;

    /// <summary>The kernel's world epochs inside one ForgeCore world number: the number times this, plus the steps the host
    /// takes without a new number (it suspended itself).</summary>
    internal const long WorldSpan = 256;

    /// <summary>The largest body of one owner message, presentation request or presentation answer.</summary>
    internal const int MaximumMessageBytes = 64 * 1024;

    /// <summary>The largest domain command payload.</summary>
    internal const int MaximumCommandBytes = 512;

    /// <summary>The largest state document (the whole variable state of a world) and the largest migration image of one buffer.</summary>
    internal const int MaximumStateBytes = 2 * 1024 * 1024, MaximumMigrationBytes = 4 * 1024 * 1024;
}

/// <summary>The stable codes the network layer reports: handshake verdicts, command refusals and world refusals.</summary>
internal static class NetworkCodes
{
    internal const string Accepted = "accepted";
    internal const string ProtocolMismatch = "protocol-mismatch";
    internal const string RuntimeMismatch = "runtime-mismatch";
    internal const string PlanSetMismatch = "plan-set-mismatch";
    internal const string FormatInvalid = "format-invalid";
    /// <summary>The player never answered the engine's handshake: it runs no Forge Engine, or has not been judged yet.</summary>
    internal const string HelloUnsent = "hello-unsent";
    /// <summary>The channel refused the player and the comparison names no difference.</summary>
    internal const string HostUnmatched = "handshake-unmatched";
    internal const string NoConsumer = "no-consumer";
    internal const string StaleWorld = "stale-world";
    /// <summary>The receiver ran the request for an earlier host; what it answered is not known.</summary>
    internal const string MigrationCommitUnknown = "migration-commit-unknown";
    /// <summary>A command to the host got no answer: the host left or stayed silent. Whether it ran is not known.</summary>
    internal const string Unanswered = "command-unanswered";
    internal const string SenderNotHost = "sender-not-host";
    internal const string PayloadTooLarge = "payload-too-large";
}

/// <summary>
/// The network layer's own contract failure. Decode faults are programming or peer errors that
/// must be reported with a stable code, never swallowed into a silent dropped message.
/// </summary>
internal sealed class NetworkContractException : Exception
{
    internal NetworkContractException(string code, string message) : base(message) => Code = code;
    internal string Code { get; }
}
