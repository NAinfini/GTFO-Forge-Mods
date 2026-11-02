using System;

namespace ForgeCore;

/// <summary>Per-frame work for mods, so no mod injects a driver component of its own. The callbacks run from ForgeCore's one driver,
/// in registration order: <see cref="OnUpdate"/> in the game's <c>Update</c>, <see cref="OnLateUpdate"/> in <c>LateUpdate</c> (after
/// the game's animation), <see cref="OnQuit"/> when the driver is destroyed at shutdown. A callback that throws is reported once
/// under the mod's ID and never stops the others or the next frame. Nothing here allocates on an ordinary frame; keep the callback a
/// static method or a delegate stored once.</summary>
public static class Frame
{
    internal static readonly FrameBook Updates = new("update"), LateUpdates = new("late update"), Quits = new("quit");

    public static void OnUpdate(string modId, Action callback) => Updates.Add(modId, callback);

    public static void OnLateUpdate(string modId, Action callback) => LateUpdates.Add(modId, callback);

    public static void OnQuit(string modId, Action callback) => Quits.Add(modId, callback);
}