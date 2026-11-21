using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Development-only: answers the game's own input queries for a driver, so a session can use, fire, switch weapons, board and
// fire rockets without a synthesised key or mouse event. Two sources are covered. The game's InputMapper actions (Use, Fire,
// SelectSpecial, ...) go through HeldInputLease. ForgeUI key actions (a mod's own keys such as the exosuit's rockets) go through
// ForgeUI's public KeyAction.HoldDriven / ReleaseDriven, so the mod reads the press exactly as if the key were down. Both end with
// Let, with LetAll, or when the world changes (a held mod key is released by the lease guard or the next command boundary).
// Nothing here writes game state; what the press causes is read from the mods' own logs and the generic experiment reads.
internal static class GameInputDrive
{
    private static readonly Dictionary<string, object> Keys = new();
    private static MethodInfo? _id, _hold, _release;
    private static long _world = -1, _keyWorld = -1;

    private static InputAction Parse(string action)
    {
        if (!Enum.TryParse<InputAction>(action, false, out var parsed) || parsed == InputAction.None)
            throw new ArgumentException("input.unknown-action:" + action);
        return parsed;
    }

    private static void Arm()
    {
        _world = RecTime.WorldEpoch;
        HeldInputLease.ArmGuard(() => GameStateManager.CurrentStateName == eGameStateName.InLevel && RecTime.WorldEpoch == _world
            ? (true, "") : (false, "input-drive-world-ended"), reason => ReleaseKeys(), error => { });
    }

    /// <summary>Holds a game input action (a fresh press edge, then held) until <see cref="Let"/> or <see cref="LetAll"/>.</summary>
    internal static string Hold(string action)
    {
        var parsed = Parse(action);
        if (!HeldInputLease.Installed) Arm();
        HeldInputLease.Press(parsed);
        return "held " + parsed + " at frame " + Time.frameCount + "; lease " + HeldInputLease.Describe();
    }

    /// <summary>Drives one movement axis (<c>MoveVertical</c> forward is +1, <c>MoveHorizontal</c> right is +1) until <see cref="LetAll"/>.</summary>
    internal static string Axis(string action, float value)
    {
        var parsed = Parse(action);
        if (!HeldInputLease.Installed) Arm();
        HeldInputLease.SetAxis(parsed, value);
        return "axis " + parsed + " = " + value + " at frame " + Time.frameCount;
    }

    internal static string Let(string action)
    {
        var parsed = Parse(action);
        var before = HeldInputLease.Describe();
        HeldInputLease.Release(parsed, false);
        return "released " + parsed + " at frame " + Time.frameCount + "; lease before release " + before;
    }

    /// <summary>Gives every game input action and every forced mod key back.</summary>
    internal static string LetAll()
    {
        var released = ReleaseKeys();
        var state = HeldInputLease.Installed ? HeldInputLease.Restore().ToString() : "no-lease";
        HeldInputLease.DisarmGuard();
        return "restored; keys released: " + released + "; " + state;
    }

    private static int ReleaseKeys()
    {
        var count = Keys.Count;
        foreach (var action in Keys.Values) Call(ref _release, action, "ReleaseDriven");
        Keys.Clear();
        return count;
    }

    /// <summary>Called at every command boundary: a key held in a world that has ended is given back.</summary>
    internal static void ReleaseKeysOfEndedWorld()
    {
        if (Keys.Count > 0 && _keyWorld != RecTime.WorldEpoch) ReleaseKeys();
    }

    /// <summary>Holds a ForgeUI key action by its full id (<c>modId/name</c>) until <see cref="LetKey"/>.</summary>
    internal static string HoldKey(string id)
    {
        var action = FindAction(id) ?? throw new ArgumentException("input.unknown-key-action:" + id);
        Call(ref _hold, action, "HoldDriven");
        if (Keys.Count == 0) _keyWorld = RecTime.WorldEpoch;
        Keys[id] = action;
        return "holding key action " + id + " from frame " + (Time.frameCount + 1);
    }

    internal static string LetKey(string id)
    {
        if (!Keys.Remove(id, out var action)) return "key action " + id + " was not held";
        Call(ref _release, action, "ReleaseDriven");
        return "releasing key action " + id;
    }

    /// <summary>Points the local player's camera at a world point.</summary>
    internal static string Look(float x, float y, float z)
    {
        var agent = ForgeCore.Players.Local ?? throw new InvalidOperationException("input.local-player-unavailable");
        var camera = agent.FPSCamera ?? throw new InvalidOperationException("input.camera-unavailable");
        var direction = (new Vector3(x, y, z) - camera.Position).normalized;
        camera.SetLookDirection(direction);
        return "looking " + direction.x.ToString("0.###") + "," + direction.y.ToString("0.###") + "," + direction.z.ToString("0.###");
    }

    private static Type KeyBindsType => Type.GetType("ForgeUI.KeyBinds, ForgeCore") ?? throw new InvalidOperationException("input.forgeui-not-loaded");

    private static object? FindAction(string id)
    {
        var actions = (System.Collections.IEnumerable)KeyBindsType.GetField("Actions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        foreach (var action in actions)
        {
            _id ??= action.GetType().GetProperty("Id")!.GetMethod!;
            if ((string)_id.Invoke(action, null)! == id) return action;
        }
        return null;
    }

    private static void Call(ref MethodInfo? method, object action, string name) => (method ??= action.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!).Invoke(action, null);
}
