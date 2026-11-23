using System;
using SNetwork;

namespace ForgeEngine.GameBindings;

/// <summary>
/// The native half of <see cref="SessionFacts"/>: one subscription to the game's own player event,
/// <c>SNet_Events.OnPlayerEvent(SNet_Player, SNet_PlayerEvent, SNet_PlayerEventReason)</c> (SNet_ASM, a static IL2CPP
/// delegate — subscribed, never patched, so the host keeps no Harmony hook for it). The two members that end a
/// player's membership are the departure (`PlayerLeftSessionHub`) and the removal (`PlayerKickedFromSession`);
/// every other member — an agent despawn, a drop-in, a recall — is not a departure and is dropped before anything
/// is read. The reason code is not read: the port carries only which of the two members the game raised.
/// </summary>
internal static class SessionBinding
{
    private static SessionFacts? _facts;
    /// <summary>The converted IL2CPP delegate is kept so the same instance is removed on stop.</summary>
    private static Il2CppSystem.Action<SNet_Player, SNet_PlayerEvent, SNet_PlayerEventReason>? _handler;

    internal static void Start(SessionFacts facts)
    {
        if (_facts != null) throw new InvalidOperationException("The Forge session binding is already started.");
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _handler = (Action<SNet_Player, SNet_PlayerEvent, SNet_PlayerEventReason>)PlayerEvent;
        SNet_Events.OnPlayerEvent += _handler;
    }

    /// <summary>Idempotent, like the network binding's stop: startup cleanup runs it whether or not it started.</summary>
    internal static void Stop()
    {
        if (_handler != null) SNet_Events.OnPlayerEvent -= _handler;
        _handler = null;
        _facts?.Dispose();
        _facts = null;
    }

    private static void PlayerEvent(SNet_Player player, SNet_PlayerEvent playerEvent, SNet_PlayerEventReason reason)
    {
        var facts = _facts;
        // Gate first: a session no plan listens to pays this field read and nothing else.
        if (facts == null || !facts.LeftObserved || player == null) return;
        bool kicked = playerEvent == SNet_PlayerEvent.PlayerKickedFromSession;
        if (!kicked && playerEvent != SNet_PlayerEvent.PlayerLeftSessionHub) return;
        GameRuntimeBridge.Guard(() => facts.Left(player, kicked));
    }
}
