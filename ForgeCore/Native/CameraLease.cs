using System;
using Player;
using UnityEngine;

namespace ForgeCore;

/// <summary>A mod's hold on the local player's view: it can put the view anywhere (<see cref="Look"/>, with the flashlight riding
/// along), lift the eye above the player's own height (<see cref="Lift"/>) and pull the near clip plane in (<see cref="NearClip"/>).
/// Leases stack and only the newest owns the view; an older lease's lift and near clip come back when the newer one is released.
/// The lift rides on the game's own camera offset, so its crouch, stand, terminal and grab transitions keep running. A lease ends
/// with <see cref="Dispose"/>, at level cleanup, and when the local player goes down; the view then returns to the game's own
/// values. Call <see cref="Lift"/> every frame the lease is held (the game rewrites its offset target when the player crouches
/// or stands, and the lift is added to each new target). Main thread only.</summary>
public sealed class CameraLease : IDisposable
{
    private static readonly CameraLeaseBook Book = new();
    private static bool _watchingDown;
    private static Vector3 _writtenTarget;
    private static float _appliedLift;
    private static float _gameNear;
    private static bool _nearApplied;
    private readonly CameraLeaseBook.Entry _entry;

    private CameraLease(CameraLeaseBook.Entry entry) => _entry = entry;

    /// <summary>A new lease for <paramref name="modId"/> that changes nothing yet.</summary>
    public static CameraLease Take(string modId)
    {
        WatchDown();
        var local = PlayerManager.GetLocalPlayerAgent();
        var camera = local == null || local.WasCollected ? null : local.FPSCamera;
        if (Book.Count == 0 && camera != null && !camera.WasCollected) _writtenTarget = camera.m_posOffsetTarget;
        return new(Book.Take(modId));
    }

    public string Owner => _entry.Owner;

    /// <summary>Not released yet (by <see cref="Dispose"/> or automatically).</summary>
    public bool IsHeld => Book.Holds(_entry);

    /// <summary>This lease owns the view: no newer lease is held.</summary>
    public bool OwnsView => Book.Owns(_entry);

    /// <summary>The eye height (metres above the player's own eye) this lease asks for. Applies at once when the lease owns the
    /// view; call it every frame while held.</summary>
    public void Lift(float meters)
    {
        _entry.Lift = meters;
        Apply();
    }

    /// <summary>The near clip plane this lease asks for, or null for the game's own.</summary>
    public void NearClip(float? plane)
    {
        _entry.NearClip = plane ?? float.NaN;
        Apply();
    }

    /// <summary>Puts the view at <paramref name="position"/> turned <paramref name="rotation"/> (world space), and the flashlight with
    /// it: the game aims the flashlight from its own view, which stays where the player stands, so the lamp keeps the place and turn
    /// it has relative to the view this frame (a lamp not mounted near the view, over a metre away, is left alone). Does nothing when
    /// a newer lease owns the view. Call it late in the frame, after the game placed its camera.</summary>
    public bool Look(Vector3 position, Quaternion rotation)
    {
        if (!OwnsView) return false;
        var agent = PlayerManager.GetLocalPlayerAgent();
        var camera = agent == null || agent.WasCollected ? null : agent.FPSCamera;
        if (camera == null || camera.WasCollected) return false;
        var view = camera.transform;
        var light = agent!.Inventory != null ? agent.Inventory.m_flashlight : null;
        if (light == null || light.WasCollected) { view.SetPositionAndRotation(position, rotation); return true; }
        var lamp = light.transform;
        var inverse = Quaternion.Inverse(view.rotation);
        var offset = inverse * (lamp.position - view.position);
        var turn = inverse * lamp.rotation;
        view.SetPositionAndRotation(position, rotation);
        if (offset.sqrMagnitude <= 1f) lamp.SetPositionAndRotation(position + rotation * offset, rotation * turn);
        return true;
    }

    public void Dispose()
    {
        if (Book.Release(_entry)) Apply();
    }

    internal static void ReleaseAll()
    {
        Book.ReleaseAll();
        Apply();
    }

    /// <summary>A local player who goes down loses the view; one who leaves the level loses every lease at level cleanup.</summary>
    private static void WatchDown()
    {
        if (_watchingDown) return;
        GameEvents.LocalPlayerDown += static () => ReleaseAll();
        _watchingDown = true;
    }

    private static void Apply()
    {
        var agent = PlayerManager.GetLocalPlayerAgent();
        var camera = agent == null || agent.WasCollected ? null : agent.FPSCamera;
        if (camera == null || camera.WasCollected) { _appliedLift = 0f; _nearApplied = false; return; }
        ApplyLift(camera, Book.Lift);
        ApplyNear(camera, Book.NearClip);
    }

    /// <summary>The lift rides on top of the game's own camera offset. A target the game wrote gets the lift added, and a change of
    /// lift shifts the current, start and target offsets together, so the game's own transitions keep running.</summary>
    private static void ApplyLift(FPSCamera camera, float lift)
    {
        var target = camera.m_posOffsetTarget;
        if (target != _writtenTarget)
        {
            target += Vector3.up * _appliedLift;
            camera.m_posOffsetTarget = target;
            _writtenTarget = target;
        }
        if (Math.Abs(lift - _appliedLift) < 0.0001f) return;
        var d = Vector3.up * (lift - _appliedLift);
        _appliedLift = lift;
        camera.m_posOffset += d;
        camera.m_posOffsetStart += d;
        camera.m_posOffsetTarget += d;
        _writtenTarget = camera.m_posOffsetTarget;
    }

    private static void ApplyNear(FPSCamera camera, float plane)
    {
        var lens = camera.m_camera;
        if (lens == null || lens.WasCollected) return;
        if (float.IsNaN(plane))
        {
            if (!_nearApplied) return;
            _nearApplied = false;
            lens.nearClipPlane = _gameNear;
            return;
        }
        if (!_nearApplied) { _gameNear = lens.nearClipPlane; _nearApplied = true; }
        lens.nearClipPlane = plane;
    }
}
