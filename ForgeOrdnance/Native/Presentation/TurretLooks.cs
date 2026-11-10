using System;
using System.Collections.Generic;
using Enemies;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnance;

/// <summary>
/// The turret looks packages register with <see cref="OrdnanceTurrets.RegisterLook"/>, on every sentry whose call-in names one (the
/// look key travels in the sentry's marker), on every peer. The game's sentry keeps working (detection, firing, hits, ammo, muzzle
/// flash, tracers, particles, audio and the scanner fan); only its body meshes are hidden.
/// <para>The game assembles a sentry's parts after <c>OnGearSpawnComplete</c>, so a sentry is only registered there and polled until its
/// rig is really present. The look stands on the ground on the yaw axis, facing where the sentry faces. Its <c>base</c> stays still. Its
/// <c>yaw</c> and <c>head</c> are drawn to the game's aim, not carried by the game's nodes: the heading slews to where <c>a_yaw</c> points
/// (the game turns at some 950° a second, too fast to see as a turn) and adds the idle scan, and the head pitches about its own hinge until
/// its barrels point at the target. The game's muzzle point follows the look's muzzle every frame and points along the barrels, or at the
/// target once the barrels are within <see cref="LockDegrees"/> of it, so shots, flash and tracers always leave the barrel that is
/// drawn. If the native nodes are destroyed while the sentry lives, the look is mounted again on the new ones.</para>
/// <para>All of it reads what the game already keeps on every peer: the host its own detection, a client the sentry's synced target
/// direction. The scan phase is each peer's own; it is shown only with no target, when nothing depends on it.</para>
/// </summary>
internal static class TurretLooks
{
    /// <summary>Within this of the target the shot goes exactly to it; further off it goes along the barrels, as the game's own sentry fires while it turns.</summary>
    private const float LockDegrees = 8f;
    /// <summary>Degrees a second the drawn gun turns and pitches at a target, and while it scans or settles.</summary>
    private const float TurnDegrees = 360f, ScanTurnDegrees = 90f, PitchDegrees = 240f, RestPitchDegrees = 60f;
    /// <summary>The pitch the gun can draw: up (negative) and down of level. The game's own aim stops at 15° up and 65° down.</summary>
    private const float MaxUp = -40f, MaxDown = 65f;
    /// <summary>Seconds the scan takes to fade out on a target and back in after.</summary>
    private const float ScanFadeOut = 0.2f, ScanFadeIn = 1.5f;

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
        internal Transform Yaw = null!, Turn = null!, Holder = null!, Head = null!;
        internal Transform? Barrel, ShellEject;
        internal GameObject Root = null!;
        internal TurretMotionSpec Motion = null!;
        internal SentryGunInstance_Firing_Bullets Firing = null!;
        internal Transform ModelMuzzle = null!, NativeMuzzle = null!;
        internal SentryGunInstance_Detection? Detection;
        internal SentryGunInstance_Sync? Sync;
        /// <summary>The sentry's up and the mount's facing, the same in <c>a_yaw</c>'s frame, and the moving parts' world pose at that facing.</summary>
        internal Vector3 Up, Facing, YawForward, HolderOffset, HeadBack, HeadRest, BarrelBack, BarrelAxis, BarrelRest;
        internal Quaternion TurnRest, HolderRest, BarrelRestRotation;
        /// <summary>What is drawn now: the heading with the scan in it, the pitch (positive is down), the scan's strength and clock.</summary>
        internal Vector3 Heading;
        internal float Elevation, ScanWeight, ScanClock;
        /// <summary>What a shot left: metres the head and the barrel are back, degrees the gun is tipped up, the barrel's spin in degrees and degrees a second.</summary>
        internal float Recoil, BarrelSlide, Tip, Spin, SpinSpeed;
        /// <summary>The body aim point of the current target, found once per target.</summary>
        internal IntPtr TargetPointer;
        internal Transform? Body;
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
        var yaw = ForgeVisual.Models.Find(animator.transform, "a_yaw");
        var pitch = yaw != null ? ForgeVisual.Models.Find(yaw, "a_pitch") : null;
        if (yaw == null || pitch == null) { missing = yaw == null ? "a_yaw" : "a_pitch"; return null; }
        var firing = sentry.m_firingComp != null ? sentry.m_firingComp.TryCast<SentryGunInstance_Firing_Bullets>() : null;
        var muzzle = firing != null ? firing.MuzzleAlign : null;
        if (firing == null || muzzle == null || muzzle.WasCollected) { missing = "firing muzzle"; return null; }
        if (CountBody(sentry, pitch) == 0) { missing = "gun meshes under a_pitch"; return null; }
        return new Rig { Animator = animator, Yaw = yaw, Pitch = pitch, Muzzle = muzzle, Firing = firing };
    }

    /// <summary>What of the sentry stays drawn besides its effects (<see cref="ForgeVisual.Models.KeepsSentryEffects"/>): everything of ours.</summary>
    private static bool Ours(Renderer renderer, SentryGunInstance sentry)
    {
        var top = sentry.transform.Pointer;
        for (var p = renderer.transform; p != null && p.Pointer != top; p = p.parent)
            if (p.name.StartsWith("ForgeOrdnance", StringComparison.Ordinal)) return true;
        return false;
    }

    private static int CountBody(SentryGunInstance sentry, Transform under)
    {
        var effects = ForgeVisual.Models.KeepsSentryEffects(sentry);
        var count = 0;
        foreach (var renderer in under.GetComponentsInChildren<Renderer>(true))
            if (ForgeVisual.Models.IsMesh(renderer) && !Ours(renderer, sentry) && !effects(renderer)) count++;
        return count;
    }

    /// <summary>Stops drawing the sentry's body meshes (base, gun, screen) that are still drawn; effects stay. The sentry is destroyed
    /// as a whole, so nothing is given back.</summary>
    private static int HideBody(SentryGunInstance sentry)
    {
        Hidden.Clear();
        return ForgeVisual.Models.HideSentryBody(sentry, Hidden, renderer => Ours(renderer, sentry));
    }
    private static readonly List<Renderer> Hidden = new();

    private static Look? Mount(SentryGunInstance sentry, Rig rig, string prefab, int mount)
    {
        if (!OrdnanceTurrets.TryGet(prefab, out var model, out var spec)) return null;
        var rigRoot = rig.Yaw.parent;
        var up = rigRoot != null ? rigRoot.up : sentry.transform.up;
        // Where the sentry faces now: its rest facing turned by a_yaw's change from rest.
        var restFacing = Vector3.ProjectOnPlane(sentry.transform.forward, up).normalized;
        var yawTurn = rigRoot != null ? rig.Yaw.rotation * Quaternion.Inverse(rigRoot.rotation * RestYaw) : Quaternion.identity;
        var facing = Vector3.ProjectOnPlane(yawTurn * restFacing, up).normalized;
        if (facing.sqrMagnitude < 1e-4f) facing = restFacing;
        var ground = ForgeVisual.Models.Find(sentry.transform, "Ground_PlacementAlign");
        var floorHeight = ground != null ? Vector3.Dot(ground.position, up) : Vector3.Dot(sentry.transform.position, up);
        var floor = rig.Yaw.position - up * (Vector3.Dot(rig.Yaw.position, up) - floorHeight);

        var root = model.Spawn(floor, Quaternion.LookRotation(facing, up) * Flip, sentry.transform);
        if (root == null)
        {
            Plugin.Logger.ErrorOnce("Ordnance sentry keeps the game's look: the turret look " + prefab + " could not be spawned.");
            return null;
        }
        root.name = "ForgeOrdnance.TurretLook";
        // The game's sentry object is scaled: build the look at its world scale, not at a local scale under that parent.
        var parentScale = sentry.transform.lossyScale;
        var parentUniform = (parentScale.x + parentScale.y + parentScale.z) / 3f;
        root.transform.localScale = Vector3.one * (spec.WorldScale / parentUniform);
        if (root.transform.Find(spec.Base) == null) throw new InvalidOperationException(prefab + " has no " + spec.Base);
        var turn = root.transform.Find(spec.Yaw) ?? throw new InvalidOperationException(prefab + " has no " + spec.Yaw + " part");
        var head = root.transform.Find(spec.Head) ?? throw new InvalidOperationException(prefab + " has no " + spec.Head);
        var modelMuzzle = root.transform.Find(spec.Muzzle) ?? throw new InvalidOperationException(prefab + " has no " + spec.Muzzle + " point");
        // The muzzle point belongs to the head: it takes the head's pitch and kick.
        modelMuzzle.SetParent(head, true);
        turn.name = "ForgeOrdnance.TurretYaw";
        var holder = new GameObject("ForgeOrdnance.TurretPitch").transform;
        holder.SetPositionAndRotation(head.position, head.rotation);
        holder.SetParent(root.transform, true);
        head.SetParent(holder, true);
        head.name = "ForgeOrdnance.TurretHead";
        var barrel = head.Find(spec.Barrel);
        rig.Muzzle.position = modelMuzzle.position;

        HideBody(sentry);
        var detection = sentry.m_detection?.TryCast<SentryGunInstance_Detection>();
        if (detection == null) Plugin.Logger.ErrorOnce("ordnance-sentry-no-detection", "sentry look " + prefab + ": the sentry has no detection component, so the host's gun cannot lay on its target.");
        var look = new Look
        {
            Sentry = sentry,
            Prefab = prefab,
            Yaw = rig.Yaw,
            Turn = turn,
            Holder = holder,
            Head = head,
            Root = root,
            Barrel = barrel,
            Motion = spec.Motion,
            Firing = rig.Firing,
            ShellEject = rig.Firing.ShellEjectAlign,
            RescanUntil = Time.time + LateMeshSeconds,
            NextRescan = Time.time + 0.25f,
            Mount = mount,
            ModelMuzzle = modelMuzzle,
            NativeMuzzle = rig.Muzzle,
            Detection = detection,
            Sync = sentry.m_sync?.TryCast<SentryGunInstance_Sync>(),
            Up = up,
            Facing = facing,
            Heading = facing,
            YawForward = Quaternion.Inverse(rig.Yaw.rotation) * facing,
            HolderOffset = holder.position - turn.position,
            TurnRest = turn.rotation,
            HolderRest = holder.rotation,
            HeadRest = head.localPosition,
            HeadBack = Quaternion.Inverse(holder.rotation) * -facing,
        };
        if (barrel != null)
        {
            look.BarrelRest = barrel.localPosition;
            look.BarrelRestRotation = barrel.localRotation;
            look.BarrelAxis = Quaternion.Inverse(head.rotation) * facing;
            look.BarrelBack = -look.BarrelAxis / spec.WorldScale;
        }
        return look;
    }

    /// <summary>Every frame after the animation: mounts sentries whose rig is now present, hides body meshes the game adds
    /// shortly after, remounts on replaced nodes and draws the looks.</summary>
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
                if (ours) Plugin.Logger.ErrorOnce("Ordnance sentry look not mounted after " + GiveUpSeconds + " s: the sentry rig still has no " + missing + "; it keeps the game's look.");
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
            if (Gone(l.Yaw) || Gone(l.Turn) || Gone(l.Holder) || Gone(l.Head) || Gone(l.ModelMuzzle) || Gone(l.NativeMuzzle))
            {
                Looks.RemoveAt(i);
                if (l.Root != null && !l.Root.WasCollected) Object.Destroy(l.Root);
                Waiting.Add(new Pending { Sentry = l.Sentry, Since = now, NextPoll = now, Attempt = l.Mount });
                continue;
            }
            Draw(l, Time.deltaTime);
            if (now < l.RescanUntil && now >= l.NextRescan)
            {
                l.NextRescan = now + 0.25f;
                HideBody(l.Sentry);
            }
        }
    }

    /// <summary>One frame of a standing look: heading, pitch, kick and barrels, then the game's muzzle point on what is drawn.</summary>
    private static void Draw(Look l, float dt)
    {
        var motion = l.Motion;
        var hasTarget = TryAim(l, out var wanted);

        l.ScanWeight = Mathf.MoveTowards(l.ScanWeight, !hasTarget && motion.ScanDegrees > 0f ? 1f : 0f, dt / (hasTarget ? ScanFadeOut : ScanFadeIn));
        l.ScanClock += dt * l.ScanWeight;
        var scan = Mathf.Sin(l.ScanClock * 2f * Mathf.PI / motion.ScanSeconds) * motion.ScanDegrees * l.ScanWeight;
        var aimed = Vector3.ProjectOnPlane(l.Yaw.rotation * l.YawForward, l.Up);
        var heading = Quaternion.AngleAxis(scan, l.Up) * (aimed.sqrMagnitude > 1e-4f ? aimed.normalized : l.Heading);
        l.Heading = Vector3.RotateTowards(l.Heading, heading, (hasTarget ? TurnDegrees : ScanTurnDegrees) * Mathf.Deg2Rad * dt, 0f);
        var turned = Quaternion.AngleAxis(Vector3.SignedAngle(l.Facing, l.Heading, l.Up), l.Up);
        l.Turn.rotation = turned * l.TurnRest;
        l.Holder.position = l.Turn.position + turned * l.HolderOffset;
        var right = Vector3.Cross(l.Up, l.Heading).normalized;

        var pitch = hasTarget ? Mathf.Clamp(Vector3.SignedAngle(l.Heading, Vector3.ProjectOnPlane(wanted, right), right), MaxUp, MaxDown) : 0f;
        l.Elevation = Mathf.MoveTowards(l.Elevation, pitch, (hasTarget ? PitchDegrees : RestPitchDegrees) * dt);
        var keep = Mathf.Exp(-motion.RecoilReturn * dt);
        l.Recoil *= keep;
        l.BarrelSlide *= keep;
        l.Tip *= keep;
        var lay = Quaternion.AngleAxis(l.Elevation, right);
        l.Holder.rotation = Quaternion.AngleAxis(-l.Tip, right) * lay * turned * l.HolderRest;
        l.Head.localPosition = l.HeadRest + l.HeadBack * l.Recoil;
        if (!Gone(l.Barrel))
        {
            l.SpinSpeed = Mathf.MoveTowards(l.SpinSpeed, 0f, motion.BarrelSpin / motion.SpinDownSeconds * dt);
            l.Spin = (l.Spin + l.SpinSpeed * dt) % 360f;
            l.Barrel!.localPosition = l.BarrelRest + l.BarrelBack * l.BarrelSlide;
            l.Barrel.localRotation = Quaternion.AngleAxis(l.Spin, l.BarrelAxis) * l.BarrelRestRotation;
        }

        var muzzle = l.NativeMuzzle;
        muzzle.position = l.ModelMuzzle.position;
        var along = lay * l.Heading;
        var shot = hasTarget && Vector3.Angle(along, wanted) <= LockDegrees ? wanted : along;
        muzzle.rotation = Quaternion.LookRotation(shot, l.Up);
        // Spent cases leave the side of the gun, not the hidden native one.
        if (!Gone(l.ShellEject)) l.ShellEject!.position = l.Holder.position + right * (0.08f * l.Root.transform.lossyScale.x);
    }

    /// <summary>The way from the look's muzzle to the sentry's target, or false with no target. The host has the target itself and lays on the
    /// middle of its body (the game's aim point sits high, and shots laid on it fly over small or crouching bodies); a client only runs the
    /// game's synced state, which holds the direction the host's sentry aims.</summary>
    private static bool TryAim(Look l, out Vector3 wanted)
    {
        wanted = default;
        if (ForgeCore.Session.IsHost)
        {
            if (AimPoint(l) is not { } point) return false;
            wanted = point - l.ModelMuzzle.position;
            return wanted.sqrMagnitude > 1e-4f;
        }
        if (l.Sync == null) return false;
        var sync = l.Sync.LastSyncData;
        if (!sync.hasTarget || sync.targetDir.sqrMagnitude < 1e-4f) return false;
        wanted = sync.targetDir;
        return true;
    }

    private static Vector3? AimPoint(Look l)
    {
        if (l.Detection == null || !l.Detection.HasTarget) return null;
        var target = l.Detection.Target;
        if (target != null && !target.WasCollected && target.Pointer != l.TargetPointer)
        {
            l.TargetPointer = target.Pointer;
            l.Body = target.GetComponentInParent<EnemyAgent>()?.AimTargetBody;
        }
        if (l.Body != null && !l.Body.WasCollected) return l.Body.position;
        var aim = l.Detection.TargetAimTrans;
        return aim != null && !aim.WasCollected ? aim.position : null;
    }

    /// <summary>Every peer, on one shot of a sentry: its look kicks, its barrel slides and spins.</summary>
    internal static void Fired(SentryGunInstance_Firing_Bullets firing)
    {
        foreach (var l in Looks)
        {
            if (l.Firing.Pointer != firing.Pointer) continue;
            var motion = l.Motion;
            l.Recoil = Mathf.Min(l.Recoil + motion.Recoil, motion.MaxRecoil);
            l.Tip = Mathf.Min(l.Tip + motion.Kick, 4f * motion.Kick);
            l.BarrelSlide = Mathf.Min(l.BarrelSlide + motion.BarrelRecoil, 2f * motion.BarrelRecoil);
            l.SpinSpeed = motion.BarrelSpin;
            return;
        }
    }

    private static bool Gone(Transform? t) => t == null || t.WasCollected;

    /// <summary>The look of the sentry the call (<paramref name="thrower"/>, <paramref name="throwId"/>) stood: the sentry's identity is the call's own
    /// (<see cref="SentryIdentity.For"/>), so two turrets landing side by side keep their own looks.</summary>
    private static Look? LookOf(ulong thrower, uint throwId)
    {
        var identity = SentryIdentity.For(thrower, throwId);
        foreach (var l in Looks)
        {
            if (l.Sentry == null || l.Sentry.WasCollected) continue;
            if (GearPayload.TryIdentity(l.Sentry, out var found) && found == identity) return l;
        }
        return null;
    }

    /// <summary>Whether the sentry the call stood already wears its look.</summary>
    internal static bool HasLookOf(ulong thrower, uint throwId) => LookOf(thrower, throwId) != null;

    /// <summary>The call's look goes (its turret is being stowed, drawn by the shell from here): true, with the way it faces and its pitch, when there was one.</summary>
    internal static bool Retire(ulong thrower, uint throwId, out Vector3 facing, out float pitch)
    {
        facing = Vector3.forward;
        pitch = 0f;
        var l = LookOf(thrower, throwId);
        if (l == null) return false;
        facing = l.Heading;
        pitch = l.Elevation;
        Looks.Remove(l);
        if (l.Root != null && !l.Root.WasCollected) Object.Destroy(l.Root);
        return true;
    }

    internal static void Clear()
    {
        Waiting.Clear();
        Looks.Clear();
    }
}

/// <summary>Every peer: a sentry's gear is spawned; its parts are assembled over the next frames.</summary>
internal static class TurretLookHook
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.SentryGearSpawned.After((sentry, _) => Spawned(sentry));
        ForgeCore.GameHooks.SentryFired.After((firing, _) =>
        {
            try { TurretLooks.Fired(firing); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("ordnance-turret-recoil-failed", "a turret look's recoil failed: " + error); }
        });
    }

    private static void Spawned(SentryGunInstance sentry)
    {
        try { TurretLooks.Assembled(sentry); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Registering the Ordnance sentry look failed; the sentry keeps the game's look: " + error); }
    }
}
