using System;
using ForgeEngine.Framework;
using ForgeEngine.Logging;

namespace ForgeEngine.Network;

/// <summary>The network layer's record points: the session, each handshake verdict, each world the kernel moved to, each request
/// answered and each state document. It owns no count (traffic is ForgeCore's <c>Net.Traffic</c>); the kernel owns the level gate
/// and the sink.</summary>
internal sealed class NetworkLogPoints
{
    private readonly RuntimeKernel _kernel;

    internal NetworkLogPoints(RuntimeKernel kernel)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
    }

    /// <summary>This machine entered or left a session, as host or client.</summary>
    internal void Session(string phase, SessionRole role)
    {
        if (!_kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        _kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.Session, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("role", Name(role)) }
        });
    }

    /// <summary>The verdict this machine reached about one player.</summary>
    internal void Handshake(SessionRole role, bool peerIsHost, bool matched, string code, string detail)
    {
        if (!_kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        _kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.Handshake, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("role", Name(role)), RuntimeLogValue.Of("peer", peerIsHost ? "host" : "client"),
                RuntimeLogValue.Of("matched", matched), RuntimeLogValue.Of("code", code), RuntimeLogValue.Quote("detail", detail) }
        });
    }

    /// <summary>One packed resource of a level whose audio differs from a player's: each side lists its own files, so the
    /// missing one can be named by comparing two logs.</summary>
    internal void HandshakeResource(SessionRole role, string code, string kind, string name, int ordinal, int total)
    {
        if (!_kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        _kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.Handshake, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("role", Name(role)), RuntimeLogValue.Of("code", code), RuntimeLogValue.Of("kind", kind),
                RuntimeLogValue.Quote("resource", name), RuntimeLogValue.Of("ordinal", (long)ordinal), RuntimeLogValue.Of("of", (long)total) }
        });
    }

    /// <summary>The kernel moved to a world epoch, and why.</summary>
    internal void Epoch(string phase, long epoch, string reason)
    {
        if (!_kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        _kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.WorldEpoch, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("epoch", epoch), RuntimeLogValue.Of("reason", reason) }
        });
    }

    /// <summary>One request this machine answered or gathered the answers of; Trace only.</summary>
    internal void Request(SessionRole role, string tier, string status, string code)
    {
        if (!_kernel.RuntimeLogs(RuntimeLogLevel.Trace)) return;
        _kernel.LogRuntime(RuntimeLogLevel.Trace, RuntimeHostLogCodes.Request, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("role", Name(role)), RuntimeLogValue.Of("tier", tier),
                RuntimeLogValue.Of("status", status ?? ""), RuntimeLogValue.Quote("code", code ?? "") }
        });
    }

    /// <summary>One state body sent or applied; Trace only.</summary>
    internal void Variables(string phase, string body, long epoch, int bytes)
    {
        if (!_kernel.RuntimeLogs(RuntimeLogLevel.Trace)) return;
        _kernel.LogRuntime(RuntimeLogLevel.Trace, RuntimeHostLogCodes.Variables, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("body", body),
                RuntimeLogValue.Of("epoch", epoch), RuntimeLogValue.Of("bytes", bytes) }
        });
    }

    internal static string Name(SessionRole role) => role switch
    {
        SessionRole.Host => "host",
        SessionRole.Client => "client",
        _ => "unknown"
    };
}
