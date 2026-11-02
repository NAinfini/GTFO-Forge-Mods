using System;
using Il2CppInterop.Runtime;

namespace ForgeCore;

/// <summary>A mod's hold on some of the game's input actions (movement axes, fire, weapon keys ...); it can also press buttons and\n/// drive one axis itself (<see cref="Press"/>, <see cref="SetAxis"/>). While held, the blocked
/// actions read as not pressed (axes as 0, or as <see cref="Steer"/> values for the movement axes) everywhere the game reads
/// them; everything else passes through. Leases stack: the blocked set is every held lease's together. When the last lease is
/// released the game's input is put back exactly as it was. Leases are released automatically at level cleanup and when the local
/// player goes down (<see cref="GameEvents.LocalPlayerDown"/>). The game's own answer stays readable through <see cref="Down"/>,
/// <see cref="Held"/> and <see cref="Axis"/>. Main thread only.</summary>
public sealed class InputLease : IDisposable
{
    private static readonly InputLeaseBook Book = new();
    private static InputMapper.DelGetButton? _button, _down, _up, _ownButton, _ownDown, _ownUp;
    private static InputMapper.DelGetAxis? _axis, _ownAxis;
    private static bool _wrapped;
    private readonly InputLeaseBook.Entry _entry;

    private InputLease(InputLeaseBook.Entry entry) => _entry = entry;

    /// <summary>A new lease for <paramref name="modId"/> that blocks nothing yet.</summary>
    public static InputLease Take(string modId)
    {
        WatchDown();
        return new(Book.Take(modId));
    }

    public string Owner => _entry.Owner;

    /// <summary>Not released yet (by <see cref="Dispose"/> or automatically).</summary>
    public bool IsHeld => Book.Holds(_entry);

    /// <summary>Blocks exactly <paramref name="actions"/> for this lease (replacing what it blocked before).</summary>
    public void Block(params InputAction[] actions)
    {
        Span<int> values = stackalloc int[actions.Length];
        for (var i = 0; i < actions.Length; i++) values[i] = (int)actions[i];
        Book.Block(_entry, values);
        Apply();
    }

    /// <summary>While the movement axes are blocked, the game reads these values for them instead of 0 (sideways and forward,
    /// -1..1), so its own locomotion moves the player. The newest steering lease wins.</summary>
    public void Steer(float sideways, float forward)
    {
        Book.Steer(_entry, true, sideways, forward);
        Apply();
    }

    /// <summary>Holds <paramref name="action"/> as pressed everywhere the game reads it and arms a fresh press edge, also when the
    /// same button was held or released before. The press edge belongs to the first query frame of the game's input layer.</summary>
    public void Press(InputAction action)
    {
        Book.Press(_entry, (int)action, UnityEngine.Time.frameCount);
        Apply();
    }

    /// <summary>Lets go of a pressed button (its release edge is answered like the press edge). The hold is dropped even when the
    /// game never queried the press; with <paramref name="requireConsumed"/> that is then an error.</summary>
    public void Release(InputAction action, bool requireConsumed = true)
    {
        if (Book.ReleaseButton(_entry, (int)action, UnityEngine.Time.frameCount) && requireConsumed)
            throw new InvalidOperationException("input.press-was-not-consumed-by-game");
    }

    /// <summary>Answers <paramref name="action"/> (one axis per lease) with <paramref name="value"/> everywhere the game reads it.</summary>
    public void SetAxis(InputAction action, float value)
    {
        Book.SetAxis(_entry, (int)action, value);
        Apply();
    }

    /// <summary>Releases every pressed button and zeroes the axis; throws after doing so when a press was never queried.</summary>
    public void ReleasePressed(bool requireConsumed = true)
    {
        var unconsumed = false;
        if (_entry.AxisAction >= 0) Book.SetAxis(_entry, _entry.AxisAction, 0f);
        for (var action = 1; action < InputLeaseBook.Actions; action++)
            if (_entry.Pressed[action]) unconsumed |= Book.ReleaseButton(_entry, action, UnityEngine.Time.frameCount);
        if (unconsumed && requireConsumed) throw new InvalidOperationException("input.press-was-not-consumed-by-game");
    }

    /// <summary>The state of a button this lease pressed (all zero frames and flags when it never did).</summary>
    public InputButtonState Button(InputAction action) => _entry.Buttons[(int)action];

    /// <summary>The lease ends by itself when <paramref name="invalid"/> returns a reason: checked before every query this lease would
    /// answer, so a lease whose owner is gone stops answering at once. <paramref name="ended"/> then receives the reason and the query.</summary>
    public void Guard(Func<string?> invalid, Action<string> ended)
        => Book.Guarded(_entry, invalid ?? throw new ArgumentNullException(nameof(invalid)), ended ?? throw new ArgumentNullException(nameof(ended)));

    public void Dispose()
    {
        if (Book.Release(_entry)) Apply();
    }

    /// <summary>The game's own "pressed this frame", ignoring every lease.</summary>
    public static bool Down(InputAction action) => (_wrapped ? _down : InputMapper.GetButtonDown)?.Invoke(action, eFocusState.FPS) ?? false;

    /// <summary>The game's own "held", ignoring every lease.</summary>
    public static bool Held(InputAction action) => (_wrapped ? _button : InputMapper.GetButton)?.Invoke(action, eFocusState.FPS) ?? false;

    /// <summary>The game's own axis value, ignoring every lease.</summary>
    public static float Axis(InputAction action) => (_wrapped ? _axis : InputMapper.GetAxis)?.Invoke(action, eFocusState.FPS) ?? 0f;

    internal static void ReleaseAll()
    {
        Book.ReleaseAll();
        Apply();
    }

    private static bool _watchingDown;

    /// <summary>A local player who goes down holds no input; one who leaves the level loses every lease at level cleanup.</summary>
    private static void WatchDown()
    {
        if (_watchingDown) return;
        GameEvents.LocalPlayerDown += static () => ReleaseAll();
        _watchingDown = true;
    }

    private static bool Blocks(InputAction action) => _wrapped && Book.Blocks((int)action);

    /// <summary>Ends every lease whose guard says its owner is gone, before the query is answered.</summary>
    private static void Admit(string member)
    {
        if (!Book.HasGuard()) return;
        foreach (var entry in Book.Snapshot())
        {
            if (entry.Guard == null) continue;
            string? reason;
            try { reason = entry.Guard(); }
            catch (Exception error) { reason = "lifecycle-guard-threw:" + error; }
            if (reason == null) continue;
            if (Book.Release(entry)) Apply();
            try { entry.Invalidated?.Invoke(reason + ";query=" + member); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("An input lease guard's end callback threw: " + error); }
        }
    }

    private static bool Pressed(InputAction action, string member, out bool value)
    {
        value = false;
        if (!_wrapped || !Book.Presses((int)action)) return false;
        Admit(member);
        if (!Book.Presses((int)action)) return false;
        value = member switch
        {
            "GetButton" => Book.Held((int)action),
            "GetButtonDown" => Book.Down((int)action, UnityEngine.Time.frameCount),
            _ => Book.Up((int)action, UnityEngine.Time.frameCount)
        };
        return true;
    }

    private static bool Drives(InputAction action, out float value)
    {
        value = 0f;
        if (!_wrapped || !Book.Drives((int)action)) return false;
        Admit("GetAxis");
        if (!Book.Drives((int)action)) return false;
        value = Book.Axis((int)action);
        return true;
    }

    private static float Steered(InputAction action)
        => action == InputAction.MoveHorizontal ? Book.Steered(true) : action == InputAction.MoveVertical ? Book.Steered(false) : 0f;

    private static void Apply()
    {
        if (Book.Engaged == _wrapped) return;
        if (Book.Engaged) Wrap();
        else Unwrap();
    }

    private static void Wrap()
    {
        _button = InputMapper.GetButton;
        _down = InputMapper.GetButtonDown;
        _up = InputMapper.GetButtonUp;
        _axis = InputMapper.GetAxis;
        if (_button == null || _down == null || _up == null || _axis == null) throw new InvalidOperationException("The game's input delegates are not available.");
        var button = _button; var down = _down; var up = _up; var axis = _axis;
        _ownButton = DelegateSupport.ConvertDelegate<InputMapper.DelGetButton>(new Func<InputAction, eFocusState, bool>((a, f) => Pressed(a, "GetButton", out var v) ? v : !Blocks(a) && button.Invoke(a, f)));
        _ownDown = DelegateSupport.ConvertDelegate<InputMapper.DelGetButton>(new Func<InputAction, eFocusState, bool>((a, f) => Pressed(a, "GetButtonDown", out var v) ? v : !Blocks(a) && down.Invoke(a, f)));
        _ownUp = DelegateSupport.ConvertDelegate<InputMapper.DelGetButton>(new Func<InputAction, eFocusState, bool>((a, f) => Pressed(a, "GetButtonUp", out var v) ? v : !Blocks(a) && up.Invoke(a, f)));
        _ownAxis = DelegateSupport.ConvertDelegate<InputMapper.DelGetAxis>(new Func<InputAction, eFocusState, float>((a, f) => Drives(a, out var v) ? v : Blocks(a) ? Steered(a) : axis.Invoke(a, f)));
        InputMapper.GetButton = _ownButton;
        InputMapper.GetButtonDown = _ownDown;
        InputMapper.GetButtonUp = _ownUp;
        InputMapper.GetAxis = _ownAxis;
        _wrapped = true;
    }

    /// <summary>The wrappers forward everything once unwrapped, so one left in place because another mod wrapped over it is harmless.</summary>
    private static void Unwrap()
    {
        _wrapped = false;
        Restore(InputMapper.GetButton, _ownButton, _button, "GetButton", v => InputMapper.GetButton = v);
        Restore(InputMapper.GetButtonDown, _ownDown, _down, "GetButtonDown", v => InputMapper.GetButtonDown = v);
        Restore(InputMapper.GetButtonUp, _ownUp, _up, "GetButtonUp", v => InputMapper.GetButtonUp = v);
        Restore(InputMapper.GetAxis, _ownAxis, _axis, "GetAxis", v => InputMapper.GetAxis = v);
        _button = _down = _up = _ownButton = _ownDown = _ownUp = null;
        _axis = _ownAxis = null;
    }

    private static void Restore<T>(T? current, T? owned, T? original, string name, Action<T?> set) where T : Il2CppSystem.Object
    {
        if (Same(current, owned)) set(original);
        else if (!Same(current, original))
            Plugin.Logger.ErrorOnce("Input delegate " + name + " was wrapped over by another mod while input was leased; left as that mod set it (ForgeCore's wrapper now forwards everything).");
    }

    private static bool Same(Il2CppSystem.Object? left, Il2CppSystem.Object? right)
        => ReferenceEquals(left, right) || left != null && right != null && left.Pointer == right.Pointer;
}
