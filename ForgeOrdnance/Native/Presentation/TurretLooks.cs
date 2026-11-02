using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnance;

/// <summary>
/// The turret looks packages register with <see cref="OrdnanceTurrets.RegisterLook"/>, on every sentry whose call-in names one (the
/// look key travels in the sentry's marker), on every peer. The game's sentry keeps working (detection,
/// aiming, firing, hits, ammo, muzzle flash, tracers, particles, audio and the scanner fan); only its body meshes are hidden.
/// <para>The game assembles a sentry's parts after <c>OnGearSpawnComplete</c>, so a sentry is only registered there and polled
/// until its rig is really present: the rotation Animator, <c>a_yaw</c> and <c>a_pitch</c> under it, the firing part's muzzle
/// and body meshes under <c>a_pitch</c>. The look stands on the ground on the yaw axis, facing where the sentry faces now: its
/// <c>base</c> stays still with the sentry, its <c>yaw</c> part is parented under the live <c>a_yaw</c> (the game's animation
/// turns it), and its <c>head</c> (barrels, model −Z, along the native muzzle's forward) hangs on a pitch holder under
/// <c>a_yaw</c> at the head's own pivot, which takes <c>a_pitch</c>'s turn every frame, so the head pitches about its own
/// hinge even where that is not the native pivot. If the native nodes are destroyed while the sentry lives, the look is
/// mounted again on the new ones.</para>
/// </summary>
internal static class TurretLooks
{
    private const float MaxMuzzleGap = 0.02f;

    /// <summary><c>a_yaw</c>'s rest local rotation (−90° about X, from <c>Turret_Base_1</c>): its parent's forward at rest is the
    /// sentry's facing.</summary>
    private static readonly Quaternion RestYaw = new(-0.70710678f, 0f, 0f, 0.70710678f);
    private static readonly Quaternion Flip = Quaternion.Euler(0f, 180f, 0f);
    private const float PollSeconds = 0.2f;
    private const float GiveUpSeconds = 15f;
    private const float LateMeshSeconds = 3f;
    private static readonly List<Pending> Waiting = new();
    private static readonly List<Look> Looks = new();

    private sealed class Pending
    {
        internal SentryGunInstance Sentry = null!;
        internal float Since, NextPoll;
        internal int Attempt;
    }

    private sealed class Look
    {
        internal SentryGunInstance Sentry = null!;
        internal string Prefab = "";
        internal Transform Yaw = null!, Pitch = null!, Turn = null!, Holder = null!, Head = null!;
        internal GameObject Root = null!;
        internal Quaternion YawAtMount, PitchAtMount, PitchToHolder;
        internal bool MovedLogged;
        internal Transform ModelMuzzle = null!, NativeMuzzle = null!;
        internal SentryGunInstance_Detection? Detection;
        internal bool DetectionResolved;
        internal Quaternion NativeMuzzleRest;
        internal float RescanUntil, NextRescan;
        internal int Mount;
    }

    /// <summary>Every peer, from <c>OnGearSpawnComplete</c>: the sentry's parts are still being assembled; start polling.</summary>
    internal static void Assembled(SentryGunInstance sentry)
    {
        foreach (var p in Waiting) if (p.Sentry.Pointer == sentry.Pointer) return;
        foreach (var l in Looks) if (l.Sentry.Pointer == sentry.Pointer) return;
        Waiting.Add(new Pending { Sentry = sentry, Since = Time.time, NextPoll = Time.time });
    }

    private static Transform? Find(Transform root, string name)
    {
        if (root.name == name) return root;
        for (var i = 0; i < root.childCount; i++)
            if (Find(root.GetChild(i), name) is { } found) return found;
        return null;
    }

    private sealed class Rig
    {
        internal Animator Animator = null!;
        internal Transform Yaw = null!, Pitch = null!, Muzzle = null!;
        internal SentryGunInstance_Firing_Bullets Firing = null!;
    }

    /// <summary>The live rig, or null with what is still missing.</summary>
    private static Rig? Resolve(SentryGunInstance sentry, out string missing)
    {
        missing = "";
        var animator = sentry.m_rotationAnimator;
        if (animator == null || animator.WasCollected) { missing = "rotation animator"; return null; }
        var yaw = Find(animator.transform, "a_yaw");
        var pitch = yaw != null ? Find(yaw, "a_pitch") : null;
        if (yaw == null || pitch == null) { missing = yaw == null ? "a_yaw" : "a_pitch"; return null; }
        var firing = sentry.m_firingComp != null ? sentry.m_firingComp.TryCast<SentryGunInstance_Firing_Bullets>() : null;
        var muzzle = firing != null ? firing.MuzzleAlign : null;
        if (firing == null || muzzle == null || muzzle.WasCollected) { missing = "firing muzzle"; return null; }
        if (CountBody(sentry, firing, pitch) == 0) { missing = "gun meshes under a_pitch"; return null; }
        return new Rig { Animator = animator, Yaw = yaw, Pitch = pitch, Muzzle = muzzle, Firing = firing };
    }

    /// <summary>Native effect objects that stay visible: the scanner fan, the muzzle flash, the muzzle and shell
    /// eject aligns (what the game attaches there), and everything of ours.</summary>
    private static bool Keep(Renderer renderer, SentryGunInstance sentry, SentryGunInstance_Firing_Bullets firing)
    {
        var t = renderer.transform;
        var top = sentry.transform.Pointer;
        for (var p = t; p != null && p.Pointer != top; p = p.parent)
            if (p.name.StartsWith("ForgeOrdnance", StringComparison.Ordinal)) return true;
        var scanner = sentry.m_visualsComp != null ? sentry.m_visualsComp.TryCast<SentryGunInstance_ScannerVisuals_Plane>() : null;
        if (scanner != null && scanner.m_scannerPlane != null && t.IsChildOf(scanner.m_scannerPlane.transform)) return true;
        if (firing.m_muzzleFlash != null && t.IsChildOf(firing.m_muzzleFlash.transform)) return true;
        if (firing.MuzzleAlign != null && t.IsChildOf(firing.MuzzleAlign)) return true;
        if (firing.ShellEjectAlign != null && t.IsChildOf(firing.ShellEjectAlign)) return true;
        return false;
    }

    private static bool IsBody(Renderer renderer) => renderer.TryCast<MeshRenderer>() != null || renderer.TryCast<SkinnedMeshRenderer>() != null;

    private static int CountBody(SentryGunInstance sentry, SentryGunInstance_Firing_Bullets firing, Transform under)
    {
        var count = 0;
        foreach (var renderer in under.GetComponentsInChildren<Renderer>(true))
            if (IsBody(renderer) && !Keep(renderer, sentry, firing)) count++;
        return count;
    }

    /// <summary>Switches off the sentry's body meshes (base, gun, screen) that are still on; effects stay.</summary>
    private static int HideBody(SentryGunInstance sentry, SentryGunInstance_Firing_Bullets firing)
    {
        var count = 0;
        foreach (var renderer in sentry.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer.enabled || !IsBody(renderer) || Keep(renderer, sentry, firing)) continue;
            renderer.enabled = false;
            count++;
        }
        return count;
    }

    private static Look? Mount(SentryGunInstance sentry, Rig rig, string prefab, int mount)
    {
        var archetype = sentry.ArchetypeData;
        if (!OrdnanceTurrets.TryGet(prefab, out var model, out var spec))
        {
            return null;
        }
        var rigRoot = rig.Yaw.parent;
        var up = rigRoot != null ? rigRoot.up : sentry.transform.up;
        // Where the sentry faces now: its rest facing turned by a_yaw's change from rest.
        var restFacing = Vector3.ProjectOnPlane(sentry.transform.forward, up).normalized;
        var yawTurn = rigRoot != null ? rig.Yaw.rotation * Quaternion.Inverse(rigRoot.rotation * RestYaw) : Quaternion.identity;
        var facing = Vector3.ProjectOnPlane(yawTurn * restFacing, up).normalized;
        if (facing.sqrMagnitude < 1e-4f) facing = restFacing;
        var right = Vector3.Cross(up, facing).normalized;
        var ground = Find(sentry.transform, "Ground_PlacementAlign");
        var floorHeight = ground != null ? Vector3.Dot(ground.position, up) : Vector3.Dot(sentry.transform.position, up);
        var floor = rig.Yaw.position - up * (Vector3.Dot(rig.Yaw.position, up) - floorHeight);

        var root = model.Spawn(floor, Quaternion.LookRotation(facing, up) * Flip, sentry.transform);
        if (root == null)
        {
            Plugin.Error("Ordnance sentry keeps the game's look: the turret look " + prefab + " could not be spawned.");
            return null;
        }
        root.name = "ForgeOrdnance.TurretLook";
        // The game's sentry object is scaled: build the look at its world scale, not at a local scale under that parent.
        var parentScale = sentry.transform.lossyScale;
        var parentUniform = (parentScale.x + parentScale.y + parentScale.z) / 3f;
        var scale = spec.WorldScale;
        root.transform.localScale = Vector3.one * (scale / parentUniform);
        if (root.transform.Find(spec.Base) == null) throw new InvalidOperationException(prefab + " has no " + spec.Base);
        var turn = root.transform.Find(spec.Yaw) ?? throw new InvalidOperationException(prefab + " has no " + spec.Yaw + " part");
        var head = root.transform.Find(spec.Head) ?? throw new InvalidOperationException(prefab + " has no " + spec.Head);
        var modelMuzzle = root.transform.Find(spec.Muzzle) ?? throw new InvalidOperationException(prefab + " has no " + spec.Muzzle + " point");
        // The muzzle point belongs to the head: it takes the head's elevation and pitch.
        modelMuzzle.SetParent(head, true);
        // Barrels along the native muzzle: turn the head about its own pivot by the muzzle's elevation.
        var muzzleForward = rig.Muzzle.forward;
        var elevation = Vector3.SignedAngle(facing, Vector3.ProjectOnPlane(muzzleForward, right), right);
        head.rotation = Quaternion.AngleAxis(elevation, right) * head.rotation;
        turn.SetParent(rig.Yaw, true);
        turn.name = "ForgeOrdnance.TurretYaw";
        var holder = new GameObject("ForgeOrdnance.TurretPitch").transform;
        holder.SetPositionAndRotation(head.position, head.rotation);
        holder.SetParent(rig.Yaw, true);
        head.SetParent(holder, true);
        head.name = "ForgeOrdnance.TurretHead";
        // The look keeps its own proportions on the yaw axis; the game's muzzle point (where shots, flash and tracers start) is
        // moved onto the look's muzzle instead, now and every frame (LateTick), so they leave the enlarged barrel tip.
        var nativeRest = rig.Muzzle.localRotation;
        rig.Muzzle.position = modelMuzzle.position;

        HideBody(sentry, rig.Firing);
        var gap = Vector3.Distance(modelMuzzle.position, rig.Muzzle.position);
        if (gap > MaxMuzzleGap) Plugin.Error("Sentry look " + prefab + ": the model muzzle is " + Math.Round(gap, 3) + " m from the native muzzle after alignment (limit " + MaxMuzzleGap + " m).");
        var look = new Look
        {
            Sentry = sentry, Prefab = prefab, Yaw = rig.Yaw, Pitch = rig.Pitch, Turn = turn, Holder = holder, Head = head, Root = root,
            YawAtMount = rig.Yaw.localRotation, PitchAtMount = rig.Pitch.localRotation,
            PitchToHolder = Quaternion.Inverse(rig.Pitch.rotation) * holder.rotation,
            RescanUntil = Time.time + LateMeshSeconds, NextRescan = Time.time + 0.25f, Mount = mount,
            ModelMuzzle = modelMuzzle, NativeMuzzle = rig.Muzzle, NativeMuzzleRest = nativeRest
        };
        return look;
    }

    /// <summary>Every frame after the animation: mounts sentries whose rig is now present, hides body meshes the game adds
    /// shortly after, remounts on replaced nodes and confirms once that native aiming turns the look.</summary>
    internal static void LateTick()
    {
        var now = Time.time;
        for (var i = Waiting.Count - 1; i >= 0; i--)
        {
            var p = Waiting[i];
            if (p.Sentry == null || p.Sentry.WasCollected) { Waiting.RemoveAt(i); continue; }
            if (now < p.NextPoll) continue;
            p.NextPoll = now + PollSeconds;
            var ours = GearPayload.TryIdentity(p.Sentry, out _, out var lookKey);
            if (ours && lookKey == 0)
            {
                Waiting.RemoveAt(i);
                continue;
            }
            var missing = "Ordnance marker";
            var rig = ours ? Resolve(p.Sentry, out missing) : null;
            if (rig == null)
            {
                if (now - p.Since < GiveUpSeconds) continue;
                Waiting.RemoveAt(i);
                if (ours) Plugin.Error("Ordnance sentry look not mounted after " + GiveUpSeconds + " s: the sentry rig still has no " + missing + "; it keeps the game's look.");
                continue;
            }
            Waiting.RemoveAt(i);
            var look = Mount(p.Sentry, rig, OrdnanceTurrets.IdOf(lookKey), p.Attempt + 1);
            if (look != null) Looks.Add(look);
        }
        for (var i = Looks.Count - 1; i >= 0; i--)
        {
            var l = Looks[i];
            if (l.Sentry == null || l.Sentry.WasCollected) { Looks.RemoveAt(i); continue; }
            if (Gone(l.Yaw) || Gone(l.Pitch) || Gone(l.Turn) || Gone(l.Holder) || Gone(l.Head))
            {
                Looks.RemoveAt(i);
                if (!Gone(l.Turn)) Object.Destroy(l.Turn.gameObject);
                if (!Gone(l.Holder)) Object.Destroy(l.Holder.gameObject);
                if (l.Root != null && !l.Root.WasCollected) Object.Destroy(l.Root);
                Waiting.Add(new Pending { Sentry = l.Sentry, Since = now, NextPoll = now, Attempt = l.Mount });
                continue;
            }
            l.Holder.rotation = l.Pitch.rotation * l.PitchToHolder;
            // The head hinges about its own pivot, not the native one, so its muzzle moves differently from the native muzzle point:
            // put the game's muzzle point (shots, flash, tracers) on the look's muzzle every frame, aimed at the target from there.
            if (!Gone(l.ModelMuzzle) && !Gone(l.NativeMuzzle)) AimMuzzle(l);
            if (now < l.RescanUntil && now >= l.NextRescan)
            {
                l.NextRescan = now + 0.25f;
                var firing = l.Sentry.m_firingComp != null ? l.Sentry.m_firingComp.TryCast<SentryGunInstance_Firing_Bullets>() : null;
                if (firing != null)
                {
                    var late = HideBody(l.Sentry, firing);
                }
            }
            if (!l.MovedLogged)
            {
                var yawTurn = Quaternion.Angle(l.Yaw.localRotation, l.YawAtMount);
                var pitchTurn = Quaternion.Angle(l.Pitch.localRotation, l.PitchAtMount);
                if (yawTurn > 3f || pitchTurn > 3f)
                {
                    l.MovedLogged = true;
                }
            }
        }
    }

    /// <summary>The game's muzzle point sits on the look's muzzle and its forward (the shot direction, which the game aimed from its own
    /// muzzle position) points at the target's aim point from there, so a muzzle carried higher than the game's does not lift every shot
    /// by that height. With no target the muzzle has the rotation the game gave it.</summary>
    private static void AimMuzzle(Look l)
    {
        var muzzle = l.NativeMuzzle;
        muzzle.position = l.ModelMuzzle.position;
        if (!l.DetectionResolved)
        {
            // The same component the game targets with (m_detection, set in OnGearSpawnComplete), read once and kept.
            l.DetectionResolved = true;
            l.Detection = l.Sentry.m_detection?.TryCast<SentryGunInstance_Detection>();
            if (l.Detection == null) Plugin.Error("Ordnance sentry look " + l.Prefab + ": the sentry has no detection component, so the muzzle cannot aim at its target and shots leave at the game's muzzle direction.");
        }
        var aim = l.Detection != null && l.Detection.HasTarget ? l.Detection.TargetAimTrans : null;
        if (aim == null || aim.WasCollected)
        {
            muzzle.localRotation = l.NativeMuzzleRest;
            return;
        }
        var toward = aim.position - muzzle.position;
        if (toward.sqrMagnitude > 1e-4f) muzzle.rotation = Quaternion.LookRotation(toward, muzzle.up);
    }

    private static bool Gone(Transform? t) => t == null || t.WasCollected;

    internal static void Clear()
    {
        Waiting.Clear();
        Looks.Clear();
    }
}

/// <summary>Every peer: a sentry's gear is spawned; its parts are assembled over the next frames.</summary>
internal static class TurretLookHook
{
    internal static void Subscribe() => ForgeCore.GameHooks.SentryGearSpawned.After((sentry, _) => Spawned(sentry));

    private static void Spawned(SentryGunInstance sentry)
    {
        try { TurretLooks.Assembled(sentry); }
        catch (Exception error) { Plugin.Error("Registering the Ordnance sentry look failed; the sentry keeps the game's look: " + error); }
    }
}
