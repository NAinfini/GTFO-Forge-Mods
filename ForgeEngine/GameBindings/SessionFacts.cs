using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeCore;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>
/// The Runtime's own session trigger, `forge.trigger.session.left`: a player left the session or was removed from
/// it. The capability row — its ports, domains and execution tier — belongs to the runtime's trigger contract
/// (`forge.contract.trigger`); this declaration registers only the binding that publishes the fact, on the host's
/// own provider id, so the ForgeEngine package's release row already names the provider that carries it.
/// </summary>
internal static class SessionContract
{
    /// <summary>The host identity doubles as the provider of the one binding the host itself observes.</summary>
    internal const string ProviderId = "forge.runtime";
    internal const string LeftCapability = Blocks.TriggerSessionLeft.Id;
    internal const string LeftBinding = ProviderId + ".binding.session.left";
    internal const string LeftHandler = ProviderId + ".session.left";

    /// <summary>The registration: one provider, no capability of its own, one observed binding. The binding is
    /// implemented by this package's native half, which holds no permission a plan would have to be granted.</summary>
    internal static RuntimeModule Module() => new ProviderDeclaration(ProviderId, "1.0.0") // package-version: NAinfini-ForgeEngine
        .Bind(new ProviderBinding(LeftBinding, LeftCapability, LeftHandler, "observe", Array.Empty<string>())).Module(RuntimeApi.Fingerprint);
}

/// <summary>
/// The publisher behind <see cref="SessionContract.LeftBinding"/>. It knows no game type: the native half reads the
/// game's own player event, decides whether it is a departure, and hands this half the leaving player's native object
/// and whether the game reported it as a removal (`PlayerKickedFromSession`) rather than a departure
/// (`PlayerLeftSessionHub`).
///
/// **Gate first.** The subscription gate is resolved once, when the half is built, and kept as a field. A native
/// callback asks <see cref="LeftObserved"/> before it reads anything, and <see cref="Left"/> asks it again before it
/// resolves the player or builds the payload, so a session whose plans never listen to this row pays one field read
/// per native event and allocates nothing.
///
/// **Host only.** The row is host-executed and the host is the one peer every departure reaches; a peer that is not
/// the authority publishes nothing and says so once. Nothing is synchronised to clients: the host's plans consume the
/// fact, and whatever they decide travels through the ordinary presentation and owner paths.
///
/// **Who left.** The `player` port is required. The player is named through the Player provider's own identity
/// (`ResolveEntityInstance("gtfo.player", …)`); a departure whose player the identity no longer holds is not
/// published and is reported once. One life leaves once: should the game report one departure through both of its
/// members, the first report is the fact and the second publishes nothing.
/// </summary>
internal sealed class SessionFacts : IDisposable
{
    private readonly RuntimeKernel _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly RuntimeSubscriptionGate _leftGate;
    private readonly Func<bool> _authority;
    private readonly OnceReporter _once;
    /// <summary>The lives that already left in <see cref="_leftWorld"/>. A reference carries its world, so the set
    /// is emptied when the world moves rather than growing across a session.</summary>
    private readonly HashSet<EntityReference> _left = new();
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private long _leftWorld = -1;
    private long _onceWorld = -1;
    private bool _disposed;

    internal SessionFacts(RuntimeKernel kernel, RuntimeModuleHandle registration, Func<bool> authority, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        _leftGate = registration.SubscriptionGate(SessionContract.LeftBinding);
    }

    /// <summary>Whether a loaded plan consumes the departure row right now: one field read.</summary>
    internal bool LeftObserved => !_disposed && _leftGate.HasSubscribers;

    /// <summary>One departure the native side reported. The answer is the kernel's own, or null when this half did
    /// not publish: nobody listens, a client, a runtime that is not running, a player the identity no longer holds,
    /// or a life that already left in this world.</summary>
    internal DispatchResult? Left(object player, bool kicked)
    {
        CheckThread();
        if (!LeftObserved) return null;
        ArgumentNullException.ThrowIfNull(player);
        if (!_registration.IsRegistered || _kernel.StartupState != RuntimeStartupState.Ready) return null;
        if (!_authority())
        {
            ReportOnce("client", "session.left observed on a non-authoritative peer: the host publishes session facts, a client does not.");
            return null;
        }
        if (_kernel.ResolveEntityInstance(EntityKinds.Player, player) is not { } leaver)
        {
            ReportOnce("unresolved", "session.left: the leaving player is no longer held by the player identity; the fact is not published.");
            return null;
        }
        long world = _kernel.WorldEpoch;
        if (world != _leftWorld) { _left.Clear(); _leftWorld = world; }
        if (!_left.Add(leaver)) return null;
        var raised = _registration.BeginEvent(_leftGate);
        raised.Values.Entity(Blocks.TriggerSessionLeft.OutPlayer, leaver);
        raised.Values.Boolean(Blocks.TriggerSessionLeft.OutKicked, kicked);
        var result = raised.Raise(EventId(world, leaver), world, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(world));
        // The Runtime owns publication refusal diagnostics, including counted suspended/queue-budget repeats.
        return result;
    }

    /// <summary>The event id of one life's departure from one world: the row, the world and the life, so a repeated
    /// report of the same departure is the kernel's own `duplicate` and never a second dispatch.</summary>
    private static string EventId(long world, EntityReference leaver)
        => SessionContract.LeftCapability + ":" + world.ToString(CultureInfo.InvariantCulture) + ":" + leaver.Id
            + ":" + leaver.LifeEpoch.ToString(CultureInfo.InvariantCulture);

    /// <summary>Counts per world: a level entered or left starts every key at its first report again.</summary>
    private void ReportOnce(string key, string message)
    {
        long world = _kernel.WorldEpoch;
        if (world != _onceWorld) { _once.Reset(); _onceWorld = world; }
        _once.Report(key, message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "The session trigger requires the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        _disposed = true;
        _left.Clear();
        _once.Reset();
    }
}
