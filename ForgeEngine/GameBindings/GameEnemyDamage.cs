using System;
using System.Reflection;
using System.Threading;

namespace ForgeEngine.GameBindings;

/// <summary>The enemy damage receivers more than one Forge package reads. ForgeCore patches each once
/// (<see cref="ForgeCore.GameHooks.EnemyBulletReceived"/>, <see cref="ForgeCore.GameHooks.EnemyMeleeReceived"/>); Runtime takes one
/// subscription to each and hands out world-bound windows, so every package is a read-only window observer: installing either package
/// alone, both, or in any load order changes nothing about the other.</summary>
public enum GameEnemyDamageEntry
{
    /// <summary>`Dam_EnemyDamageBase.ReceiveBulletDamage(pBulletDamageData)`.</summary>
    Bullet,
    /// <summary>`Dam_EnemyDamageBase.ReceiveMeleeDamage(pFullDamageData)`.</summary>
    Melee
}

/// <summary>One receiver call: its receiver, the packet the native body was handed and, when the window closes,
/// the exception the body or a later patch raised (null when the call returned).</summary>
public readonly struct GameEnemyDamageCall
{
    public long WorldEpoch { get; }
    public GameEnemyDamageEntry Entry { get; }
    public Dam_EnemyDamageBase Receiver { get; }
    /// <summary>The bullet packet; meaningful only for <see cref="GameEnemyDamageEntry.Bullet"/>.</summary>
    public pBulletDamageData Bullet { get; }
    /// <summary>The melee packet; meaningful only for <see cref="GameEnemyDamageEntry.Melee"/>.</summary>
    public pFullDamageData Melee { get; }
    public Exception? Exception { get; }
    internal GameEnemyDamageCall(long epoch, GameEnemyDamageEntry entry, Dam_EnemyDamageBase receiver,
        pBulletDamageData bullet, pFullDamageData melee, Exception? exception)
    { WorldEpoch = epoch; Entry = entry; Receiver = receiver; Bullet = bullet; Melee = melee; Exception = exception; }
}

/// <summary>Per-call windows around the receivers. An observer's <c>entered</c> runs before the native body and
/// returns the observer's own state for this call; its <c>left</c> receives that state after the body returned or
/// threw. Windows open by descending priority (Harmony's Priority values, First = 800 to Last = 0, ties in
/// subscription order) and close in reverse, so they nest: the first to open is the last to close. Every window
/// that opened is closed, even when its lease ended or the world changed during the call.</summary>
public static class GameEnemyDamage
{
    internal const string Writer = "ForgeEngine.EnemyDamage";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly MethodInfo BulletTarget = Method("ReceiveBulletDamage", typeof(pBulletDamageData));
    private static readonly MethodInfo MeleeTarget = Method("ReceiveMeleeDamage", typeof(pFullDamageData));

    internal sealed class Window
    {
        internal readonly Func<GameEnemyDamageCall, object?> Entered;
        internal readonly Action<GameEnemyDamageCall, object?> Left;
        internal long EnteredCalls, LeftCalls;
        internal Window(Func<GameEnemyDamageCall, object?> entered, Action<GameEnemyDamageCall, object?> left) { Entered = entered; Left = left; }
    }
    /// <summary>One call's opened windows, in opening order. Absent when nothing observed the call. Rented from
    /// <see cref="_frames"/> and cleared on return, so a hit reuses its window instead of allocating one.</summary>
    internal sealed class Frame
    {
        internal long Epoch;
        internal NativeHookObservers<Window>.Entry[] Opened = Array.Empty<NativeHookObservers<Window>.Entry>();
        internal object?[] States = Array.Empty<object?>();
        internal int Count;
    }

    // A frame is rented by synchronous nesting depth per thread: a receiver's body may enter a receiver again, so an
    // inner call must never reuse the outer call's window. Leave returns the frame in a finally even when the body or
    // an observer threw; clearing on return keeps a finished world's observers and state out of the next hit.
    [ThreadStatic] private static Frame?[] _frames = Array.Empty<Frame?>();
    [ThreadStatic] private static int _depth;

    private static readonly MethodInfo[] Targets = { BulletTarget, MeleeTarget };
    /// <summary>The one subscription to ForgeCore's bracket of each entry (the installation this reports).</summary>
    private static readonly SharedBracket[] Core =
    {
        new(Writer, BulletTarget, static () => ForgeCore.GameHooks.EnemyBulletReceived.Around(
            call => Enter(GameEnemyDamageEntry.Bullet, call.Receiver, call.Data, default),
            (call, frame, _) => Leave(GameEnemyDamageEntry.Bullet, call.Receiver, call.Data, default, null, frame),
            (call, frame, error) => Leave(GameEnemyDamageEntry.Bullet, call.Receiver, call.Data, default, error, frame))),
        new(Writer, MeleeTarget, static () => ForgeCore.GameHooks.EnemyMeleeReceived.Around(
            call => Enter(GameEnemyDamageEntry.Melee, call.Receiver, default, call.Data),
            (call, frame, _) => Leave(GameEnemyDamageEntry.Melee, call.Receiver, default, call.Data, null, frame),
            (call, frame, error) => Leave(GameEnemyDamageEntry.Melee, call.Receiver, default, call.Data, error, frame)))
    };
    private static readonly NativeHookObservers<Window>[] Observers = { Windows(), Windows() };
    private static NativeHookObservers<Window> Windows() => new(static () => NativeHookRegistration.WorldEpoch, NativeHookRegistration.ObserverFailed, 64);
    static GameEnemyDamage() => NativeHookRegistration.TrackShared(ReadDiagnostics);
    private static System.Collections.Generic.IEnumerable<NativeHookDiagnostic> ReadDiagnostics()
    {
        for (var index = 0; index < Targets.Length; index++)
        {
            var target = Targets[index];
            var installed = Core[index].Installations;
            foreach (var phase in new[] { "entered", "left" })
            {
                var entries = Observers[index].Items;
                if (entries.Length == 0)
                    yield return new(Writer, target, phase, "none", NativeHookRegistration.WorldEpoch,
                        "no-subscribers", installed, installed, 0);
                foreach (var entry in entries)
                    yield return new(Writer, target, phase, entry.Lease.Consumer, entry.Lease.WorldEpoch,
                        entry.Lease.Status, installed, installed,
                        Interlocked.Read(ref (phase == "entered" ? ref entry.Observer.EnteredCalls : ref entry.Observer.LeftCalls)));
            }
        }
    }

    public static bool IsTarget(MethodBase target) => BulletTarget.Equals(target) || MeleeTarget.Equals(target);

    /// <summary>Observe one receiver for one world. A refused install throws the native-hook-* refusal.</summary>
    public static NativeHookLease Observe(string consumer, long worldEpoch, GameEnemyDamageEntry entry, int priority,
        Func<GameEnemyDamageCall, object?> entered, Action<GameEnemyDamageCall, object?> left)
    {
        ArgumentNullException.ThrowIfNull(entered); ArgumentNullException.ThrowIfNull(left);
        if (!Enum.IsDefined(entry)) throw new ArgumentOutOfRangeException(nameof(entry));
        var index = (int)entry; var target = Targets[index];
        var lease = Observers[index].Subscribe(consumer, worldEpoch, priority, new Window(entered, left), reason => Refusal(reason, target));
        try { Core[index].Ensure(); }
        catch { lease.Dispose(); throw; }
        return lease;
    }

    // The refusal text the shared entries give: the reason, this writer and the target.
    private static string Refusal(string reason, MethodInfo target)
        => reason + " declaration=" + Writer + " target=" + NativeHookRegistration.TargetIdentity(target);

    private static MethodInfo Method(string name, Type packet)
    {
        var found = Array.FindAll(typeof(Dam_EnemyDamageBase).GetMethods(Instance), method => method.Name == name
            && method.ReturnType == typeof(void) && method.GetParameters() is { Length: 1 } parameters
            && parameters[0].ParameterType == packet);
        return found.Length == 1 ? found[0] : throw new MissingMethodException(typeof(Dam_EnemyDamageBase).FullName, name);
    }

    private static Frame? Enter(GameEnemyDamageEntry entry, Dam_EnemyDamageBase receiver, pBulletDamageData bullet, pFullDamageData melee)
    {
        // Snapshot only the observer array: subscribing or releasing during the call never duplicates a window.
        var list = Observers[(int)entry];
        var observers = list.Items;
        if (observers.Length == 0) return null;
        var epoch = NativeHookRegistration.WorldEpoch;
        if (epoch < 0) return null;
        var call = new GameEnemyDamageCall(epoch, entry, receiver, bullet, melee, null);
        Frame? frame = null;
        try
        {
            foreach (var observer in observers)
            {
                if (NativeHookRegistration.WorldEpoch != epoch) break;
                if (!observer.Lease.IsActive) continue;
                object? state;
                try { observer.Lease.CallbackEntered(); Interlocked.Increment(ref observer.Observer.EnteredCalls); state = observer.Observer.Entered(call); }
                catch (Exception error)
                {
                    list.Failed(observer.Lease, error);
                    continue;
                }
                frame ??= Rent(epoch, observers.Length);
                frame.Opened[frame.Count] = observer; frame.States[frame.Count++] = state;
            }
            return frame;
        }
        catch
        {
            // The prefix never returned a frame to Harmony, so this path owns the rented frame.
            if (frame != null) Return(frame);
            throw;
        }
    }

    private static Frame Rent(long epoch, int capacity)
    {
        var depth = _depth;
        // A thread-static field initializer only runs on the thread that initialized the type; other worker
        // threads start with null and get their own (empty) pool here.
        var frames = _frames ??= Array.Empty<Frame?>();
        if (depth == frames.Length)
        {
            Array.Resize(ref _frames, Math.Max(4, depth * 2));
            frames = _frames;
        }
        var frame = frames[depth] ??= new Frame();
        frame.Epoch = epoch;
        if (frame.Opened.Length < capacity)
        {
            frame.Opened = new NativeHookObservers<Window>.Entry[capacity];
            frame.States = new object?[capacity];
        }
        frame.Count = 0;
        _depth = depth + 1;
        return frame;
    }

    private static void Return(Frame frame)
    {
        Array.Clear(frame.Opened, 0, frame.Count);
        Array.Clear(frame.States, 0, frame.Count);
        frame.Count = 0;
        _depth--;
    }

    private static void Leave(GameEnemyDamageEntry entry, Dam_EnemyDamageBase receiver, pBulletDamageData bullet,
        pFullDamageData melee, Exception? exception, Frame? frame)
    {
        if (frame == null) return;
        try
        {
            var call = new GameEnemyDamageCall(frame.Epoch, entry, receiver, bullet, melee, exception);
            for (var index = frame.Count - 1; index >= 0; index--)
            {
                var observer = frame.Opened[index];
                try { observer.Lease.CallbackEntered(); Interlocked.Increment(ref observer.Observer.LeftCalls); observer.Observer.Left(call, frame.States[index]); }
                catch (Exception error) { Observers[(int)entry].Failed(observer.Lease, error); }
            }
        }
        finally { Return(frame); }
    }
}
