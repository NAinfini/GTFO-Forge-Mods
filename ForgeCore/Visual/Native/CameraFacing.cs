using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeVisual;

/// <summary>How a thing turns to a camera.</summary>
public enum Facing
{
    /// <summary>Takes the camera's own rotation (a sign or text that lies flat in the view).</summary>
    CameraTurn,
    /// <summary>Points its local +Z from the camera to itself, turning on every axis (a screen whose visible -Z face looks at the camera).</summary>
    AwayFromCamera,
    /// <summary>Turns only about the world's vertical axis so its front (local +Z after the offset) points at the camera; stays upright.</summary>
    YawToCamera,
}

/// <summary>
/// Things that keep facing a camera, for every mod: one pass a frame turns each followed transform about its own origin, run just before
/// the first camera of the frame culls (ForgeCore <c>Frame.OnPreCull</c>), so after every LateUpdate (the game's, the camera's and every package's
/// <c>ForgeCore.Frame.OnLateUpdate</c>) has moved the things and the camera; nothing turned is a frame behind what it follows. A thing it turns
/// is drawn by a renderer (a batched draw submits its matrix in LateUpdate, before the turn). The pre-cull subscription is taken with the first
/// follower and dropped when the last one leaves, so with none nothing runs. <see cref="Turn"/> is the one rule;
/// <see cref="Follow(Transform, Facing, Quaternion, Transform?)"/> registers a transform for the pass, with the camera it faces (null: the local
/// player's first-person camera, found again only when the one held is gone). Each machine turns its own copy; nothing is sent. The pass looks
/// nothing up by name and allocates nothing of its own (the interop wraps the culling camera for each call while the callback is installed); a
/// transform that is gone leaves the list by itself. Main thread only.
/// </summary>
public static class CameraFacing
{
    private static readonly List<Follower> Followers = new();
    private static readonly Vector3 Up = new(0f, 1f, 0f);
    private static Transform? _local;
    private static IDisposable? _preCull;
    // Set by ForgeVisual's late step, cleared by the first camera to cull after it: one pass a frame however many cameras render.
    private static bool _due;

    /// <summary>The rotation <paramref name="facing"/> gives a thing standing at <paramref name="at"/> for a camera at <paramref name="eye"/>
    /// turned by <paramref name="cameraRotation"/>; <paramref name="offset"/> is applied after it (the thing's own front for <see cref="Facing.YawToCamera"/>).
    /// Null when the camera stands on the thing (no direction), so the caller keeps the last rotation.</summary>
    public static Quaternion? Turn(Facing facing, Vector3 at, Vector3 eye, Quaternion cameraRotation, Quaternion offset)
    {
        if (facing == Facing.CameraTurn) return cameraRotation * offset;
        var toward = facing == Facing.AwayFromCamera ? at - eye : new Vector3(eye.x - at.x, 0f, eye.z - at.z);
        // Plain arithmetic, not the interop's sqrMagnitude and up getters: this runs for every follower every frame.
        if (toward.x * toward.x + toward.y * toward.y + toward.z * toward.z < 1e-10f) return null;
        return (facing == Facing.AwayFromCamera ? Quaternion.LookRotation(toward) : Quaternion.LookRotation(toward, Up)) * offset;
    }

    /// <summary>Keeps <paramref name="target"/> facing <paramref name="camera"/> (null: the local player's first-person camera) every frame before the cameras render
    /// until the returned handle is disposed or the target is gone. <paramref name="offset"/> as in <see cref="Turn"/>.</summary>
    public static IDisposable Follow(Transform target, Facing facing, Quaternion offset, Transform? camera = null)
    {
        var follower = new Follower(target, facing, offset, camera);
        Followers.Add(follower);
        if (_preCull == null)
        {
            _preCull = ForgeCore.Frame.OnPreCull(Plugin.Id, PreCull);
        }
        return follower;
    }

    /// <summary><see cref="Follow(Transform, Facing, Quaternion, Transform?)"/> with no offset.</summary>
    public static IDisposable Follow(Transform target, Facing facing, Transform? camera = null) => Follow(target, facing, Quaternion.identity, camera);

    /// <summary>ForgeVisual's late step: the next camera to cull runs the pass.</summary>
    internal static void NewFrame() => _due = true;

    private static void PreCull(Camera _)
    {
        if (!_due) return;
        _due = false;
        try { TickAll(); } catch (Exception error) { Scene.StepFailed("camera facing", error); }
    }

    /// <summary>The pass, once a frame from the first camera to cull.</summary>
    private static void TickAll()
    {
        // The camera last read and where it stands: followers of the same camera read it once a pass.
        IntPtr seen = IntPtr.Zero;
        Vector3 eye = default;
        Quaternion eyeTurn = default;
        for (int index = Followers.Count - 1; index >= 0; index--)
        {
            var follower = Followers[index];
            if (follower.Ended || ForgeCore.NativeObjects.Gone(follower.Target))
            {
                follower.Ended = true;
                Followers[index] = Followers[^1];
                Followers.RemoveAt(Followers.Count - 1);
                continue;
            }
            var camera = follower.Camera ?? LocalCamera();
            if (ForgeCore.NativeObjects.Gone(camera)) continue;
            if (camera.Pointer != seen) { seen = camera.Pointer; eye = camera.position; eyeTurn = camera.rotation; }
            var turn = Turn(follower.Facing, follower.Target.position, eye, eyeTurn, follower.Offset);
            if (turn is { } rotation) follower.Target.rotation = rotation;
        }
        // The last follower left: take the callback off, so no camera calls in until the next Follow.
        if (Followers.Count == 0 && _preCull != null)
        {
            _preCull.Dispose();
            _preCull = null;
        }
    }

    /// <summary>The local player's first-person camera, held until it is gone (reading it again every frame would make a wrapper each time).</summary>
    private static Transform? LocalCamera()
    {
        if (!ForgeCore.NativeObjects.Gone(_local)) return _local;
        var agent = ForgeCore.Players.Local;
        var fps = agent == null || agent.WasCollected ? null : agent.FPSCamera;
        return _local = fps == null || fps.WasCollected ? null : fps.transform;
    }

    private sealed class Follower : IDisposable
    {
        internal Follower(Transform target, Facing facing, Quaternion offset, Transform? camera)
        {
            Target = target; Facing = facing; Offset = offset; Camera = camera;
        }

        internal readonly Transform Target;
        internal readonly Facing Facing;
        internal readonly Quaternion Offset;
        internal readonly Transform? Camera;
        internal bool Ended;

        public void Dispose() => Ended = true;
    }
}
