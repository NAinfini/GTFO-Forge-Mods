using System;
using ForgeCore;

namespace ForgeDevelopment.Native;

/// <summary>
/// The development mod's single hold on the game's input: a few buttons and one axis answer from a ForgeCore
/// <see cref="InputLease"/> while every other action goes to the game. It owns no delegate logic, clock or update loop;
/// its owner presses and releases from its own frame work and must arm a guard that ends the lease when the owner's
/// lifetime ends (the guard is asked before every query the lease would answer).
/// </summary>
internal static class HeldInputLease
{
    private static InputLease? _lease;
    private static Func<(bool Valid, string Reason)>? _guard;
    private static Action<string>? _invalidated;
    private static Action<Exception>? _invalidationFailed;
    private static InputAction _first = InputAction.None, _axisAction = InputAction.None;
    private static float _axisValue;

    internal static bool Installed => _lease is { IsHeld: true };

    internal static void ArmGuard(Func<(bool Valid, string Reason)> guard, Action<string> invalidated,
        Action<Exception> invalidationFailed)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _invalidated = invalidated ?? throw new ArgumentNullException(nameof(invalidated));
        _invalidationFailed = invalidationFailed ?? throw new ArgumentNullException(nameof(invalidationFailed));
    }

    internal static void DisarmGuard() { _guard = null; _invalidated = null; _invalidationFailed = null; }

    /// <summary>Holds the button and arms a fresh press edge, also when the same button was held or released before.</summary>
    internal static void Press(InputAction action)
    {
        if (action == InputAction.None) throw new ArgumentOutOfRangeException(nameof(action));
        var lease = Take();
        if (_first == InputAction.None) _first = action;
        lease.Press(action);
    }

    internal static void SetAxis(InputAction action, float value)
    {
        var lease = Take();
        lease.SetAxis(action, value);
        _axisAction = action;
        _axisValue = value;
    }

    /// <summary>Releases every held button and the axis. The hold is dropped even when the game never consumed a press.</summary>
    internal static object Release()
    {
        if (!Installed) return new { installed = false, released = true };
        _axisValue = 0f;
        _lease!.ReleasePressed();
        return Describe();
    }

    /// <summary>Releases one button. With <paramref name="requireConsumed"/> a press the game never queried is an error;
    /// a driver that only wants to let go of a button which may not be read (a weapon that is reloading) passes false.</summary>
    internal static void Release(InputAction action, bool requireConsumed = true)
    {
        if (Installed) _lease!.Release(action, requireConsumed);
    }

    internal static object Restore()
    {
        _lease?.Dispose();
        _lease = null;
        _first = _axisAction = InputAction.None;
        _axisValue = 0f;
        return new { installed = false, restored = true };
    }

    /// <summary>The first leased button's state, the shape the M5 evidence has always carried.</summary>
    internal static object Describe()
    {
        var first = _first == InputAction.None || _lease == null
            ? new InputButtonState { PressFrame = -1, ReleaseFrame = -1, DownQueryFrame = -1, UpQueryFrame = -1 }
            : _lease.Button(_first);
        return new
        {
            installed = Installed,
            buttonAction = _first.ToString(),
            buttonHeld = first.Held,
            axisAction = _axisAction.ToString(),
            axisValue = _axisValue,
            pressFrame = first.PressFrame,
            releaseFrame = first.ReleaseFrame,
            downQueryFrame = first.DownQueryFrame,
            upQueryFrame = first.UpQueryFrame,
            downPending = first.DownPending,
            upPending = first.UpPending
        };
    }

    private static InputLease Take()
    {
        if (Installed) return _lease!;
        if (_guard == null || _invalidated == null)
            throw new InvalidOperationException("input.lifecycle-guard-unavailable");
        var lease = InputLease.Take(Plugin.PluginGuid);
        lease.Guard(AskGuard, End);
        _lease = lease;
        _first = _axisAction = InputAction.None;
        _axisValue = 0f;
        return lease;
    }

    private static string? AskGuard()
    {
        var result = _guard?.Invoke() ?? (false, "lifecycle-guard-missing");
        return result.Valid ? null : result.Reason;
    }

    private static void End(string reason)
    {
        try { _invalidated?.Invoke(reason + ";cleanup=restored"); }
        catch (Exception error) { _invalidationFailed!(error); }
    }
}
