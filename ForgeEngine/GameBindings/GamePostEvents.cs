using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using GameEvent;
using Player;
using Metadata = Il2CppSystem.Collections.Generic.Dictionary<string, string>;

namespace ForgeEngine.GameBindings;

public enum GamePostEventPhase { Posted, Returned, Completed }

/// <summary>The exact five native arguments, with read-only metadata access and the host epoch.</summary>
public readonly struct GamePostEvent
{
    public long WorldEpoch { get; }
    public eGameEvent Event { get; }
    public PlayerAgent? Player { get; }
    public float Value { get; }
    public string? Text { get; }
    public long InvocationId { get; }
    public Exception? Exception { get; }
    public bool OriginalRan { get; }
    private readonly Metadata? _metadata;
    internal GamePostEvent(long epoch, eGameEvent e, PlayerAgent? player, float value, string? text, Metadata? metadata,
        long invocationId, Exception? exception = null, bool originalRan = true)
    {
        WorldEpoch = epoch; Event = e; Player = player; Value = value; Text = text; _metadata = metadata;
        InvocationId = invocationId; Exception = exception; OriginalRan = originalRan;
    }
    public bool TryReadMetadata(string key, out string? value)
    {
        value = null;
        // Not TryGetValue: its interop wrapper builds the out value as `new TValue(pointer)`, which throws MissingMethodException for string.
        if (_metadata == null || !_metadata.ContainsKey(key)) return false;
        value = _metadata[key]; return true;
    }
    /// <summary>A defensive snapshot, copied only when a diagnostic reader explicitly requests it.</summary>
    public IReadOnlyDictionary<string, string>? ReadMetadata()
    {
        if (_metadata == null) return null;
        var copy = new Dictionary<string, string>();
        foreach (var pair in _metadata) copy.Add(pair.Key, pair.Value);
        return new ReadOnlyDictionary<string, string>(copy);
    }
    internal GamePostEvent Complete(Exception? exception, bool originalRan)
        => new(WorldEpoch, Event, Player, Value, Text, _metadata, InvocationId, exception, originalRan);
}

/// <summary>ForgeCore patches PostEvent once (<see cref="ForgeCore.GameHooks.GameEventPosted"/>); Runtime takes one subscription and
/// hands out world-bound, independently leased entry, normal-return and completion observations.</summary>
public static class GamePostEvents
{
    private static readonly MethodInfo Target = typeof(GameEventManager).GetMethod(nameof(GameEventManager.PostEvent),
        new[] { typeof(eGameEvent), typeof(PlayerAgent), typeof(float), typeof(string), typeof(Metadata) })
        ?? throw new MissingMethodException("GameEventManager.PostEvent(five arguments)");
    private static long _invocation;
    private static readonly SharedBracket Core = new("ForgeEngine.PostEvent", Target,
        static () => ForgeCore.GameHooks.GameEventPosted.Around<GamePostEvent>(Posted, Returned, Faulted));
    private static readonly NativeHookChannel<GamePostEvent> Before = new(Core, "posted"), After = new(Core, "returned"),
        Finally = new(Core, "completed");
    public static bool IsTarget(MethodBase target) => Target.Equals(target);
    public static NativeHookLease Subscribe(string consumer, long worldEpoch, Action<GamePostEvent> callback,
        GamePostEventPhase phase = GamePostEventPhase.Posted)
        => phase switch
        {
            GamePostEventPhase.Posted => Before.Subscribe(consumer, worldEpoch, callback),
            GamePostEventPhase.Returned => After.Subscribe(consumer, worldEpoch, callback),
            GamePostEventPhase.Completed => Finally.Subscribe(consumer, worldEpoch, callback),
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
    // The call's state is this value (InvocationId 0 when no host world was open); ForgeCore keeps it per call, so nested posts keep their own.
    private static GamePostEvent Posted(ForgeCore.GameEventPost post)
    {
        var epoch = NativeHookRegistration.WorldEpoch;
        if (epoch < 0) return default;
        var call = new GamePostEvent(epoch, post.Event, post.Player, post.Value, post.Text, post.Metadata, Interlocked.Increment(ref _invocation));
        Before.Publish(call, epoch);
        return call;
    }
    private static void Returned(ForgeCore.GameEventPost _, GamePostEvent call, bool ran)
    {
        if (call.InvocationId == 0) return;
        if (ran) After.Publish(call, call.WorldEpoch);
        Finally.Publish(call.Complete(null, ran), call.WorldEpoch);
    }
    private static void Faulted(ForgeCore.GameEventPost _, GamePostEvent call, Exception error)
    {
        if (call.InvocationId != 0) Finally.Publish(call.Complete(error, false), call.WorldEpoch);
    }
}
