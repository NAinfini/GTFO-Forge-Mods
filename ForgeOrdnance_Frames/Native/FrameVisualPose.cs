using System;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>A worn suit is its pilot's own game body, scaled: every frame after the game animated the pilot's third-person body and put
/// its hands on the held item's grips, each suit bone takes the turn of the matching humanoid bone of that body and the suit's hips its
/// height and sway, scaled by hip height (ForgeVisual's <see cref="PoseCopy"/>, mapped by <see cref="FrameBodyRig.PoseLinks"/>); then
/// both arms bend so each wrist lands where the body's does, carried over from the chest (<see cref="FrameBodyRig.ArmReach"/>): the
/// suit's arms are not the body's scaled, so copied turns alone leave the hands off the grips. Whatever the game's body does (walking,
/// running, crouching, jumping, holding and aiming each weapon, reloading, swinging, carrying) the suit does, on every peer, from the
/// game's own replicated animation. The pilot's own item is held as a copy (ForgeVisual's <see cref="HeldMirror"/>) in the suit's right
/// hand, where the game's item sits against the body's hand, scaled the same; its muzzle is where the suit's shots leave from. The local
/// pilot's suit is drawn as its shadow only; it moves and holds the same.</summary>
internal sealed partial class FrameVisual
{
    private const float HeldCheck = 0.25f;
    /// <summary>The gear shader's light colour the suit's lights (<see cref="GlowColour"/>) are set on, on the first-person shell too.</summary>
    internal const string EmissiveColor = "_EmissiveColor";

    private readonly PoseTarget? _pose;
    private PoseCopy? _copy;
    private HeldMirror? _heldCopy;
    private bool _posed;
    private float _nextHeldCheck;
    private IntPtr _syncIK;
    private int _wornFrame = -1;

    private string PoseLabel => _unit.Record.Spec.Model;

    /// <summary>The pilot's body copied onto the suit and what the pilot holds mirrored into the suit's hand.</summary>
    private void BindPose(Player.PlayerAgent agent)
    {
        _copy = _pose != null && agent.AnimatorBody != null ? Models.CopyPose(agent.AnimatorBody, _pose, FrameBodyRig.ArmReach, PoseLabel) : null;
        _heldCopy = Models.MirrorHeld(agent, _root.layer, PoseLabel);
        _heldCopy.ShadowOnly(_ownHidden);
        _syncIK = agent.SyncIK != null ? agent.SyncIK.Pointer : IntPtr.Zero;
        PoseAfterIK.Hooks.Ensure();
    }

    /// <summary>The pilot's body just had its hands put on its grips, after this suit already followed it this frame: follow again.</summary>
    internal void HandsSolved(IntPtr syncIK)
    {
        if (syncIK != _syncIK || _wornFrame != Time.frameCount || _pilot == null) return;
        FollowPilot(Time.time);
    }

    /// <summary>After the game moved the players, while worn: the suit takes the pilot's body pose, then the held item its place.</summary>
    private void PoseFromPilot(float now)
    {
        if (_copy == null || _pose == null) return;
        if (!_copy.Apply()) { Unpose(); return; }
        _posed = true;
        if (_heldCopy == null) return;
        if (now >= _nextHeldCheck) { _nextHeldCheck = now + HeldCheck; _heldCopy.Refresh(); }
        _heldCopy.Place(_copy.Turn, _copy.Source(FrameBodyRig.RightHandLink), _pose.Bone(FrameBodyRig.RightHandLink), _copy.Scale);
    }

    /// <summary>The suit stops following its pilot (no pilot, boarding, leaving): its held item goes and its bones go back to rest.</summary>
    private void Unpose()
    {
        _wornFrame = -1;
        _heldCopy?.Hide();
        if (!_posed) return;
        _posed = false;
        _pose?.Rest();
    }

    /// <summary>The pilot is gone: the copies of what it held, and the body's animator handed back.</summary>
    private void UnbindPose()
    {
        _heldCopy?.Destroy();
        _heldCopy = null;
        _copy?.Release();
        _copy = null;
        _syncIK = IntPtr.Zero;
    }

    /// <summary>The held item's muzzle, while the suit shows one.</summary>
    private bool HeldMuzzle(out Vector3 muzzle)
    {
        muzzle = default;
        return _heldCopy != null && _heldCopy.TryMuzzle(out muzzle);
    }
}
