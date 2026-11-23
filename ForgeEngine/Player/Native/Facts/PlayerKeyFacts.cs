using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using Player;
using UnityEngine;
using ForgeCore;

namespace ForgePlayer.Native;

/// <summary>Each player's own key edges, polled on that player's machine: `forge.trigger.input.key`. It is the one row of the player-event
/// family that is polled rather than handed to a hook — the game posts no event for "this key went down", so the
/// runtime reads `UnityEngine.Input.GetKeyDown` / `GetKeyUp` from the local player's own update.
///
/// Three rules shape this half, and each one is the reason the code is arranged the way it is:
/// <list type="bullet">
/// <item>The keys are a property of the loaded plans, not of the card: `key` is a structural enum parameter, and
/// the kernel rebuilds the per-binding member table wherever the subscription table is rebuilt. The poller reads
/// that table once per subscription generation and then keeps a dense `KeyCode[]` beside the parallel array of
/// each key's `key_code` index, so the poll path performs no string lookup and allocates nothing. A row no loaded
/// plan mounts has a closed gate and reads no key at all.</item>
/// <item>The player's own focus decides whether a key edge counts: while the terminal, chat or a menu owns the
/// input, `InputMapper.PlayerHasFocus` is false and no edge is published. The member is read from the game's own
/// mapper (`catalog/native/native-runtime.json`, type `InputMapper`, property `PlayerHasFocus`), never re-derived from
/// which screen is open.</item>
/// <item>The publication itself goes through the player-event half, so the host-only rule, the one-transition
/// ledger and the event identity are the same ones every other row of this family uses. On a client the edge is
/// reported to the host through <see cref="PlayerInputReport"/>, which publishes it for the sender's own player.</item>
/// </list>
///
/// The poll is a native callback, never a lifecycle observer: the kernel refuses a publication from inside a
/// lifecycle notification, so the edge is read and published from the update the game already runs, and the
/// kernel's tick is only read as the value an event carries.</summary>
internal sealed class PlayerKeyFacts : IDisposable
{
    private readonly RuntimeModuleHandle _registration;
    private readonly IForgeEngine _kernel;
    private readonly Action<string> _report;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly RuntimeSubscriptionGate _gate;
    private readonly RuntimeSubscriptionGate _fireGate;
    private readonly PlayerInputReport _input;
    internal PlayerInputReport Input => _input;
    private static readonly string KeyBinding = PlayerEventContract.Binding(PlayerEventContract.KeyCapability);
    private KeyCode[] _keys = Array.Empty<KeyCode>();
    /// <summary>The `key_code` member index of each subscribed key, parallel to <see cref="_keys"/>: the index the
    /// payload carries and the kernel filters an entry's own structural `key` member against. Resolved once per
    /// subscription rebuild, so the poll path reads it instead of looking a member name up.</summary>
    private int[] _keyIndices = Array.Empty<int>();
    /// <summary>Whether a pressed edge was published for each subscribed key, parallel to <see cref="_keys"/>: a key
    /// pressed while the game had the input is released as a fact even when the release comes after focus moved
    /// to the terminal, chat or a menu, so a plan waiting for the release never stays held.</summary>
    private bool[] _held = Array.Empty<bool>();
    private readonly Action<PlayerKeyFacts> _poll;
    private long _generation = -1;
    private long _epoch;
    private bool _disposed;

    internal static PlayerKeyFacts? Installed { get; private set; }

    internal PlayerKeyFacts(RuntimeModuleHandle registration, IForgeEngine kernel, PlayerEventFacts events, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        ArgumentNullException.ThrowIfNull(events);
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _gate = registration.SubscriptionGate(KeyBinding);
        _fireGate = registration.SubscriptionGate(PlayerEventContract.Binding(Blocks.TriggerInputFire.Id));
        _input = new PlayerInputReport(events, kernel, _report);
        _epoch = kernel.WorldEpoch;
        // One delegate built with the poller: the frame hook asks the session to run this instance, and building
        // the callback at the call site would allocate on every subscribed frame.
        _poll = keys => keys.Poll();
    }

    internal static PlayerKeyFacts Attach(RuntimeModuleHandle registration, IForgeEngine kernel, PlayerEventFacts events, Action<string> report)
    {
        var facts = new PlayerKeyFacts(registration, kernel, events, report);
        Installed = facts;
        return facts;
    }

    internal static void Detach(PlayerKeyFacts facts)
    {
        if (ReferenceEquals(Installed, facts)) Installed = null;
    }

    /// <summary>Whether any loaded plan is mounted on this row, read as the gate's own boolean. The frame hook
    /// asks this before it asks who the player is, so a row no loaded plan mounts costs the frame one field read
    /// and no cast.</summary>
    internal bool Subscribed => _gate.HasSubscribers || _fireGate.HasSubscribers;

    /// <summary>The one callback the frame hook hands the session: built with the poller, so a subscribed frame
    /// allocates nothing to ask for the poll.</summary>
    internal Action<PlayerKeyFacts> PollCallback => _poll;

    /// <summary>One poll of the local player's own key edges, run from the local player's update. The binding's
    /// gate is read first, so a row no loaded plan mounts reads no key at all; the subscription generation is
    /// compared second, so the dense key array is rebuilt only when the plans changed; and the focus check comes
    /// before any edge is published, so a key pressed while the terminal, chat or a menu owns the input is not a fact.</summary>
    internal void Poll()
    {
        CheckThread();
        if (_disposed || !_registration.IsRegistered) return;
        if (_kernel.WorldEpoch != _epoch) { Forget(); _epoch = _kernel.WorldEpoch; }
        if (_fireGate.HasSubscribers && InputLease.Down(InputAction.Fire) && Focused()) _input.FirePressed();
        if (!_gate.HasSubscribers)
        {
            if (_keys.Length != 0) Forget();
            return;
        }
        if (_registration.SubscriptionGeneration != _generation) Rebuild();
        if (_keys.Length == 0) return;
        // The two phase indices were resolved with the contract, never per poll, and the key index is the one the
        // subscription rebuild already resolved beside the `KeyCode` it belongs to. Focus and the local player are
        // read once, at the first edge: reading either wraps a native object, and most frames have no edge at all.
        // An edge seen without focus is still not a fact.
        PlayerAgent? local = null;
        var asked = false;
        var focused = false;
        for (var index = 0; index < _keys.Length; index++)
        {
            var down = UnityEngine.Input.GetKeyDown(_keys[index]);
            var up = UnityEngine.Input.GetKeyUp(_keys[index]);
            if (!down && !up) continue;
            if (!asked) { asked = true; focused = Focused(); local = Players.Local; }
            if (local == null) return;
            var key = _keyIndices[index];
            if (down && focused) { _input.KeyEdge(local, key, PlayerEventContract.PressedPhaseIndex); _held[index] = true; }
            if (up && (focused || _held[index])) { _input.KeyEdge(local, key, PlayerEventContract.ReleasedPhaseIndex); _held[index] = false; }
        }
    }

    /// <summary>The keys the loaded plans mounted on this binding actually name. A member the engine enum cannot
    /// parse is reported once and left out rather than polled as a neighbouring key; the engine name is rebuilt
    /// from the kebab-cased set member, which is the rule the set was built by. The member's own `key_code` index
    /// is resolved here as well, so the poll carries the value the kernel filters on.</summary>
    private void Rebuild()
    {
        _generation = _registration.SubscriptionGeneration;
        var members = _registration.SubscribedEnumMembers(KeyBinding, PlayerEventContract.KeyParameter);
        var keys = new List<KeyCode>(members.Count);
        var indices = new List<int>(members.Count);
        foreach (var member in members)
        {
            if (!Enum.TryParse<KeyCode>(UnityName(member), out var code))
            {
                _report("Player key observation: no engine key for key_code member " + member + ".");
                continue;
            }
            keys.Add(code);
            indices.Add(PlayerEventContract.KeyIndex(member));
        }
        _keys = keys.ToArray();
        _keyIndices = indices.ToArray();
        _held = new bool[_keys.Length];
    }

    /// <summary>Whether the game gives the local player's own input to gameplay right now. `PlayerHasFocus` is the
    /// mapper's own answer, so the terminal, chat and menu states are the game's judgement and not a list of
    /// screens this half keeps in step with.</summary>
    internal static bool Focused() => InputMapper.Current != null && InputMapper.PlayerHasFocus;

    /// <summary>The engine enum name one kebab-cased set member indexes: the set was built by kebab-casing the
    /// engine's own names, so the reverse is the same rule.</summary>
    internal static string UnityName(string member)
    {
        var parts = member.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++) parts[index] = char.ToUpperInvariant(parts[index][0]) + parts[index][1..];
        return string.Concat(parts);
    }

    /// <summary>Drops the cached key array and forces the next poll to read the subscription table again. It is
    /// used for a world boundary — the plans stay loaded across one, but the native input state does not — and for
    /// a closed gate, so a row that loses its last subscriber stops polling the keys it used to read.</summary>
    private void Forget()
    {
        _keys = Array.Empty<KeyCode>();
        _keyIndices = Array.Empty<int>();
        _held = Array.Empty<bool>();
        _generation = -1;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player key observation requires the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        Detach(this);
        _input.Dispose();
        _disposed = true;
        _keys = Array.Empty<KeyCode>();
        _keyIndices = Array.Empty<int>();
    }
}
