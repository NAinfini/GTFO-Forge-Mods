using System;
using System.Collections.Generic;
using System.Threading;
using ForgeEngine.Network;

namespace ForgeEngine.GameBindings;

/// <summary>One client-local observation, from a player of this world that runs the same engine and content.</summary>
internal readonly record struct DomainCommandRequest(
    ulong SenderSession,
    long WorldEpoch,
    long LifeEpoch,
    string ResourceId,
    byte[] Payload);

/// <summary>The host's verdict for one domain request, in the command-result vocabulary.</summary>
internal readonly record struct DomainCommandResult(
    string Status,
    string Commit,
    string Code,
    string Detail,
    int FactCount);

/// <summary>A host result correlated with the endpoint and resource of the request this process sent.</summary>
internal readonly record struct DomainCommandResponse(
    ulong RequestId,
    long WorldEpoch,
    string ResourceId,
    DomainCommandResult Result);

/// <summary>
/// The domain packages' way to hand the host an observation. An endpoint has one host executor; clients submit
/// observations to that executor and never run it locally: the host runs each exactly once and answers it (ForgeCore's
/// request). <see cref="Send"/> reports only that the request was submitted to the local host executor or sent to the
/// host, not that the host accepted or committed it.
/// </summary>
internal static class DomainCommands
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Func<DomainCommandRequest, DomainCommandResult>> Handlers = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Action<DomainCommandResponse>> ResultObservers = new(StringComparer.Ordinal);
    private static long _requests;

    /// <summary>Registers the host executor for one exact endpoint.</summary>
    public static IDisposable Register(string endpoint, Func<DomainCommandRequest, DomainCommandResult> execute)
    {
        DomainCommandRouting.RequireEndpoint(endpoint, nameof(endpoint));
        if (execute == null) throw new ArgumentNullException(nameof(execute));
        lock (Gate)
        {
            if (!Handlers.TryAdd(endpoint, execute))
                throw new InvalidOperationException("Domain command endpoint '" + endpoint + "' is already registered.");
        }
        return new Registration(endpoint, execute);
    }

    /// <summary>Registers one observer for results of requests this process sent to the named endpoint.</summary>
    public static IDisposable RegisterResults(string endpoint, Action<DomainCommandResponse> observe)
    {
        DomainCommandRouting.RequireEndpoint(endpoint, nameof(endpoint));
        if (observe == null) throw new ArgumentNullException(nameof(observe));
        lock (Gate)
        {
            if (!ResultObservers.TryAdd(endpoint, observe))
                throw new InvalidOperationException("Domain command result endpoint '" + endpoint + "' is already registered.");
        }
        return new ResultRegistration(endpoint, observe);
    }

    /// <summary>
    /// Submits one observation. A client sends it to the host; the host submits it directly to the same executor.
    /// False means no request was submitted: there is no session, or its host does not run the same engine and content.
    /// </summary>
    public static bool Send(string endpoint, string resourceId, long lifeEpoch, byte[] payload)
    {
        DomainCommandRouting.RequireEndpoint(endpoint, nameof(endpoint));
        DomainCommandRouting.RequireResource(resourceId, nameof(resourceId));
        if (lifeEpoch < 0) throw new ArgumentOutOfRangeException(nameof(lifeEpoch));
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        DomainCommandRouting.RequirePayload(payload.Length, nameof(payload));
        return EngineNet.Current?.SendCommand(endpoint, resourceId, lifeEpoch, payload) ?? false;
    }

    /// <summary>True when the session's host will never take a request: it refused this engine's messages, or answered none
    /// of its greetings. A <see cref="Send"/> that fails while this is false fell into the greeting with a new host (or
    /// between sessions) and is dropped without a report; one that fails while it is true is worth one report.</summary>
    internal static bool HostUnreachable => EngineNet.Current?.HostUnreachable ?? false;

    internal static bool TryGet(string endpoint, out Func<DomainCommandRequest, DomainCommandResult>? execute)
    {
        lock (Gate) return Handlers.TryGetValue(endpoint, out execute);
    }

    /// <summary>The number a request this process sends is answered under.</summary>
    internal static ulong NextRequest() => (ulong)Interlocked.Increment(ref _requests);

    /// <summary>Hands the host's answer to the observer of the endpoint the request was sent to, when there is one.</summary>
    internal static void Observe(string endpoint, DomainCommandResponse response)
    {
        Action<DomainCommandResponse>? observe;
        lock (Gate)
            if (!ResultObservers.TryGetValue(endpoint, out observe)) return;
        observe(response);
    }

    private sealed class Registration : IDisposable
    {
        private readonly string _endpoint;
        private Func<DomainCommandRequest, DomainCommandResult>? _execute;

        internal Registration(string endpoint, Func<DomainCommandRequest, DomainCommandResult> execute)
        {
            _endpoint = endpoint;
            _execute = execute;
        }

        public void Dispose()
        {
            var execute = _execute;
            if (execute == null) return;
            lock (Gate)
            {
                if (Handlers.TryGetValue(_endpoint, out var registered) && ReferenceEquals(registered, execute))
                    Handlers.Remove(_endpoint);
            }
            _execute = null;
        }
    }

    private sealed class ResultRegistration : IDisposable
    {
        private readonly string _endpoint;
        private Action<DomainCommandResponse>? _observe;

        internal ResultRegistration(string endpoint, Action<DomainCommandResponse> observe)
        {
            _endpoint = endpoint;
            _observe = observe;
        }

        public void Dispose()
        {
            var observe = _observe;
            if (observe == null) return;
            lock (Gate)
            {
                if (ResultObservers.TryGetValue(_endpoint, out var registered) && ReferenceEquals(registered, observe))
                    ResultObservers.Remove(_endpoint);
            }
            _observe = null;
        }
    }
}
