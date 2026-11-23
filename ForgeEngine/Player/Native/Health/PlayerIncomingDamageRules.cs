using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using ForgeEngine.Framework;
using Player;

namespace ForgePlayer.Native;

internal sealed class PlayerIncomingDamageRules : IDisposable
{
    /// <summary>One application of a policy. Overlapping effects stack as layers, newest wins, and an effect's end
    /// removes only its own layers, the same shape as the enemy side's action layers. A permanent layer (no handle)
    /// can never expose an older one again, so it replaces them.</summary>
    private sealed record Layer(LayerKey Key, IncomingDamagePolicy Policy);

    /// <summary>What a layer is removed by: the kernel's effect handle for a plan's rule, or the name an outside
    /// holder of the published <see cref="IncomingDamageReceiver"/> passed (a drive session's protection lease).
    /// Neither one is a permanent layer.</summary>
    private readonly record struct LayerKey(RuntimeHandle Handle, string? Name)
    {
        internal bool Permanent => !Handle.IsCast && Name == null;
        internal static LayerKey Of(RuntimeHandle? handle) => new(handle ?? default, null);
        internal static LayerKey Named(string? name) => new(default, string.IsNullOrEmpty(name) ? null : name);
    }

    private static void Push(List<Layer> layers, LayerKey key, IncomingDamagePolicy policy)
    {
        if (key.Permanent) { layers.Clear(); layers.Add(new Layer(key, policy)); return; }
        var index = layers.FindIndex(layer => layer.Key == key);
        if (index >= 0) layers[index] = new Layer(key, policy); else layers.Add(new Layer(key, policy));
    }

    private sealed class Rule
    {
        internal readonly EntityReference Target;
        internal readonly List<Layer> Layers = new();
        internal double Blocked;
        internal Rule(EntityReference target) { Target = target; }
    }
    private readonly PlayerIdentityModule _players;
    private readonly IForgeEngine _kernel;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly Dictionary<IntPtr, Rule> _rules = new();
    private readonly List<IntPtr> _retired = new();
    private readonly List<Action<ReducedIncomingDamage>> _observers = new();
    private readonly List<Layer> _everyone = new();
    internal static PlayerIncomingDamageRules? Installed { get; private set; }
    internal bool Active => _rules.Count != 0 || _everyone.Count != 0;

    internal PlayerIncomingDamageRules(PlayerIdentityModule players, IForgeEngine kernel, RuntimeModuleHandle registration)
    {
        _players = players; _kernel = kernel;
        _lifecycle = registration.ObserveLifecycle(value =>
        {
            if (value.Kind == RuntimeLifecycleKind.WorldChanged) Clear();
        });
        // A rule belongs to one life; the identity announces each life that ended, so no tick checks them. A rule
        // whose receiver was replaced without that announcement is still dropped by Apply, which compares pointers.
        players.LifeEnded += OnLifeEnded;
        registration.PublishService(new IncomingDamageReceiver(
            (target, policy, name) => Install(target, policy, LayerKey.Named(name)),
            name => Restore(LayerKey.Named(name)),
            Clear,
            (policy, name) => InstallEveryPlayer(policy, LayerKey.Named(name)),
            ObserveReduced));
        Installed = this;
    }

    private void OnLifeEnded(EntityReference life)
    {
        if (_rules.Count == 0) return;
        _retired.Clear();
        foreach (var pair in _rules) if (pair.Value.Target == life) _retired.Add(pair.Key);
        foreach (var pointer in _retired) _rules.Remove(pointer);
    }

    internal IncomingDamageReceipt Install(EntityReference target, IncomingDamagePolicy policy, RuntimeHandle? handle)
        => Install(target, policy, LayerKey.Of(handle));

    private IncomingDamageReceipt Install(EntityReference target, IncomingDamagePolicy policy, LayerKey handle)
    {
        IncomingDamageReceipt Refused(string code) => new(Status.Rejected, Commit.None, code, 0);
        if (!_players.Authoritative) return Refused(ResultCode.AuthorityOrPhase);
        if (!PlayerHealthReceiver.TryRead(_players, target, out var snapshot, out var code)) return Refused(code);
        if (policy.Source is { } source && !_kernel.IsEntityCurrent(source)) return Refused(ResultCode.StaleSourceFilter);
        _rules.TryGetValue(snapshot.ReceiverPointer, out var previous);
        double blocked = previous?.Blocked ?? 0;
        if (policy.Policy == IncomingDamagePolicy.Clear) _rules.Remove(snapshot.ReceiverPointer);
        else
        {
            // A rule left by a life this receiver no longer carries is not stacked on.
            var rule = previous != null && previous.Target == target ? previous : new Rule(target);
            Push(rule.Layers, handle, policy);
            _rules[snapshot.ReceiverPointer] = rule;
        }
        return new(Status.Succeeded, Commit.Confirmed, ResultCode.Committed, blocked);
    }

    /// <summary>One standing policy for every player receiver that has no rule of its own, present and future. Authority
    /// is the same as for a single receiver; installing Clear removes it.</summary>
    internal IncomingDamageReceipt InstallEveryPlayer(IncomingDamagePolicy policy, RuntimeHandle? handle)
        => InstallEveryPlayer(policy, LayerKey.Of(handle));

    private IncomingDamageReceipt InstallEveryPlayer(IncomingDamagePolicy policy, LayerKey handle)
    {
        if (!_players.Authoritative) return new(Status.Rejected, Commit.None, ResultCode.AuthorityOrPhase, 0);
        if (policy.Policy == IncomingDamagePolicy.Clear) _everyone.Clear();
        else Push(_everyone, handle, policy);
        return new(Status.Succeeded, Commit.Confirmed, ResultCode.Committed, 0);
    }

    internal IDisposable ObserveReduced(Action<ReducedIncomingDamage> observer)
    {
        _observers.Add(observer);
        return new Release(() => _observers.Remove(observer));
    }

    private sealed class Release : IDisposable
    {
        private Action? _release;
        internal Release(Action release) { _release = release; }
        public void Dispose() { _release?.Invoke(); _release = null; }
    }

    internal void Clear() { _rules.Clear(); _everyone.Clear(); }

    internal float Apply(Dam_PlayerDamageBase receiver, float incoming, Agent? sourceAgent, int? kind)
    {
        if (!Active || receiver == null || !_players.Authoritative || !float.IsFinite(incoming) || incoming <= 0) return incoming;
        IncomingDamagePolicy policy;
        if (_rules.TryGetValue(receiver.Pointer, out var rule))
        {
            var player = _players.CurrentAgent(rule.Target);
            if (player?.Damage?.Pointer != receiver.Pointer || receiver.Owner?.Pointer != player.Pointer)
            { _rules.Remove(receiver.Pointer); return incoming; }
            policy = rule.Layers[^1].Policy;
        }
        else if (_everyone.Count != 0) policy = _everyone[^1].Policy;
        else return incoming;
        EntityReference? source = null;
        if (policy.Source is { } filter && sourceAgent != null)
        {
            string? kindName = EntityKinds.Is(filter, EntityKinds.Player) ? EntityKinds.Player
                : EntityKinds.Is(filter, EntityKinds.Enemy) ? EntityKinds.Enemy : null;
            object? actor = kindName == EntityKinds.Player ? sourceAgent.TryCast<PlayerAgent>()
                : kindName == EntityKinds.Enemy ? sourceAgent.TryCast<EnemyAgent>() : null;
            if (actor != null) source = _kernel.ResolveEntityInstance(kindName!, actor);
        }
        if (!policy.Matches(source, kind)) return incoming;
        float adjusted = policy.Adjust(incoming, receiver.Health);
        if (rule != null) rule.Blocked += Math.Max(0, incoming - adjusted);
        if (adjusted < incoming && _observers.Count != 0) NotifyReduced(sourceAgent, kind, incoming, adjusted);
        return adjusted;
    }

    private void NotifyReduced(Agent? sourceAgent, int? kind, float incoming, float adjusted)
    {
        var name = "unresolved";
        try { if (sourceAgent != null) name = sourceAgent.ToString() ?? "unresolved"; }
        catch (Exception) { /* the name is only a label on the observer record, and "unresolved" is its declared value for an agent whose string form does not read */ }
        var reduced = new ReducedIncomingDamage(kind, PlayerDamageKindToken.Attack, name, incoming, adjusted);
        foreach (var observer in _observers.ToArray()) observer(reduced);
    }

    internal void Restore(RuntimeHandle handle) => Restore(LayerKey.Of(handle));

    private void Restore(LayerKey handle)
    {
        if (handle.Permanent) return;
        _everyone.RemoveAll(layer => layer.Key == handle);
        _retired.Clear();
        foreach (var pair in _rules)
            if (pair.Value.Layers.RemoveAll(layer => layer.Key == handle) != 0 && pair.Value.Layers.Count == 0) _retired.Add(pair.Key);
        foreach (var pointer in _retired) _rules.Remove(pointer);
    }

    public void Dispose()
    {
        _players.LifeEnded -= OnLifeEnded;
        Clear(); _observers.Clear(); _lifecycle.Dispose();
        if (ReferenceEquals(Installed, this)) Installed = null;
    }
}
