using System;
using System.Collections.Generic;
using LevelGeneration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnance;

/// <summary>One timeline every peer plays from the arrival moment, and the host schedules its spawns against.</summary>
internal static class DeliveryTiming
{
    internal const float FallHeight = 40f;
    internal const float FallSeconds = PodTimeline.FallSeconds;
    /// <summary>The fall sound lasts 1.77 s: it starts this far in, so that it ends on the touchdown.</summary>
    internal const float IncomingStart = 1.77f - FallSeconds;
    /// <summary>The drill stops this far above its rest and then bores in.</summary>
    internal const float SinkDepth = 0.25f;
    internal const float SinkSeconds = PodTimeline.SinkSeconds;
    internal const float PodOpenAfterImpact = PodTimeline.PodOpenAfterImpact;
    /// <summary>The clamp-release sound starts this long before the doors begin to open.</summary>
    internal const float UnlatchLead = 1.2f;
    internal const float DoorSeconds = 1.4f;
    internal const float ItemsAfterOpen = 1.6f;
    internal const float PodLingerAfterEmpty = 15f;
    internal const float LeaveSeconds = 2.5f;

    /// <summary>When the doors stand open and the weapon racks show their contents (<see cref="OrdnanceDelivery.PodContentsDelay"/>).</summary>
    internal static float ContentsOpenDelay => FallSeconds + PodOpenAfterImpact + ItemsAfterOpen;
    /// <summary>Seconds from the arrival until the base has bored in, waited, and its lids go.</summary>
    internal static float LidDelay(BurrowSpec burrow) => FallSeconds + burrow.BurrowSeconds + burrow.HoldSeconds;
    /// <summary>Seconds from the arrival until the upper part starts to rise out of the opened base.</summary>
    internal static float HeadRiseDelay(BurrowSpec burrow) => LidDelay(burrow) + burrow.LidClearSeconds;

    /// <summary>The pod's door axis (its local X) points back toward the thrower: <paramref name="yaw"/> faces from the thrower
    /// to the landing point.</summary>
    internal static Quaternion PodRotation(float yaw) => Quaternion.Euler(0f, yaw + 90f, 0f);
}

/// <summary>The drop pods and turret shells in the level (every peer) plus the host's delayed spawns and its watch over the
/// pickups in each pod. Cleared on level exit and checkpoint recovery, like every call in flight; a host change keeps the pods that still hold something to claim.</summary>
internal static class Deliveries
{
    private const float ContentsCheckSeconds = 0.5f;
    /// <summary>How far from a bay's anchor a placed pickup still counts as lying in that bay.</summary>
    private const float ShutBayReach = 0.6f;
    private static readonly List<Delivery> Active = new();
    private static readonly List<(float Due, Action Work)> HostWork = new();
    private static readonly List<Contents> Watched = new();

    private sealed class Contents
    {
        internal ulong Thrower;
        internal uint ThrowId;
        internal ItemInLevel?[] Items = Array.Empty<ItemInLevel?>();
        internal float NextCheck;
    }

    /// <summary>The vehicle stage of an arrival (<see cref="ArrivalPlan"/>): none for a direct one.</summary>
    internal static OrdnancePod? StartPod(OrdnancePresentation p)
        => !ArrivalPlan.HasVehicle(p.Arrival) ? null
            : FindPod(p.SummonerLookup, p.ThrowId) ?? (Add(new DropPod(p.SummonerLookup, p.ThrowId, p.Position, p.Rotation.eulerAngles.y, 0f, false, p.Definition), out var d) ? ((DropPod)d).Handle : null);

    internal static OrdnanceShell? StartShell(OrdnancePresentation p)
        => !ArrivalPlan.HasVehicle(p.Arrival) ? null
            : FindShell(p.SummonerLookup, p.ThrowId) ?? (Add(new TurretShell(p.SummonerLookup, p.ThrowId, p.Position, p.Rotation.eulerAngles.y, 0f, false, p.Definition), out var d) ? ((TurretShell)d).Handle : null);

    /// <summary>The pods and shells this peer has built since it entered the level or reloaded a checkpoint: a row builds one only once.</summary>
    private static readonly HashSet<DeliveryKey> Built = new();

    private static DeliveryKey KeyOf(Delivery d) => new(d.Thrower, d.ThrowId, d is DropPod ? DeliveryKey.Pod : DeliveryKey.Shell);

    /// <summary>Host: the row of a pod or shell as it stands now.</summary>
    private static void WriteRow(Delivery d)
    {
        var now = Time.time;
        var pod = d as DropPod;
        OrdnanceNetwork.DeliveryRows.Set(KeyOf(d), new DeliveryRow
        {
            Landing = new Vec3(d.LandingPoint.x, d.LandingPoint.y, d.LandingPoint.z),
            Yaw = d.Yaw,
            Elapsed = now - d.StartTime,
            SinceEmptied = pod?.SinceEmptied(now) ?? ((TurretShell)d).SinceRecalled(now),
            DefinitionKey = pod?.DefinitionKey ?? ((TurretShell)d).DefinitionKey
        });
    }

    /// <summary>Host: every row again with its times as they are now (a player joined).</summary>
    internal static void WriteRows()
    {
        foreach (var d in Active) WriteRow(d);
    }

    /// <summary>Every peer: the host's row of a pod or shell. A peer that has not built it (it was not in the level when it arrived)
    /// shows it at its point in the timeline, without its past sounds, shake and events; <see cref="FindPod"/> and
    /// <see cref="FindShell"/> find it from now on. A peer that has built it takes only the news that the pod is empty or the shell
    /// takes its turret back.</summary>
    internal static void Apply(DeliveryKey key, DeliveryRow row)
    {
        if (!row.Landing.IsFinite || !float.IsFinite(row.Yaw) || !float.IsFinite(row.Elapsed) || row.Elapsed < 0f) { Plugin.Logger.ErrorOnce("Malformed delivery row from the host for throw " + key.ThrowId + "."); return; }
        foreach (var d in Active)
        {
            if (KeyOf(d) != key) continue;
            if (row.SinceEmptied < 0f) return;
            if (d is DropPod standing) standing.Emptied(row.SinceEmptied);
            else ((TurretShell)d).Recall(row.SinceEmptied);
            return;
        }
        if (Built.Contains(key)) return;
        if (!OrdnanceContent.Set.TryGet(row.DefinitionKey, out var definition)) { Plugin.Logger.ErrorOnce("A delivery row has unknown definition " + row.DefinitionKey + "."); return; }
        var landing = new Vector3(row.Landing.X, row.Landing.Y, row.Landing.Z);
        if (key.Kind == DeliveryKey.Pod)
        {
            var pod = new DropPod(key.Thrower, key.ThrowId, landing, row.Yaw, row.Elapsed, true, definition);
            if (row.SinceEmptied >= 0f) pod.Emptied(row.SinceEmptied);
            Add(pod, out _);
        }
        else if (key.Kind == DeliveryKey.Shell)
        {
            var shell = new TurretShell(key.Thrower, key.ThrowId, landing, row.Yaw, row.Elapsed, true, definition);
            if (row.SinceEmptied >= 0f) shell.Recall(row.SinceEmptied);
            Add(shell, out _);
        }
    }

    internal static OrdnancePod? FindPod(ulong thrower, uint throwId)
    {
        foreach (var d in Active)
            if (d is DropPod pod && pod.Thrower == thrower && pod.ThrowId == throwId) return pod.Handle;
        return null;
    }

    internal static OrdnanceShell? FindShell(ulong thrower, uint throwId)
    {
        foreach (var d in Active)
            if (d is TurretShell shell && shell.Thrower == thrower && shell.ThrowId == throwId) return shell.Handle;
        return null;
    }

    internal static bool IsSupplyPosition(Vector3 position)
    {
        foreach (var delivery in Active)
            if (delivery is DropPod pod && pod.IsOpen)
                for (var i = 0; i < 4; i++)
                    if (pod.Anchor(i) is { } anchor && Vec.SqrLength(anchor.position - position) < .09f) return true;
        return false;
    }

    /// <summary>A pod whose doors are still shut with a pickup bay at <paramref name="position"/>.</summary>
    internal static bool TryShutPodAt(Vector3 position, out ulong thrower, out uint throwId)
    {
        thrower = 0; throwId = 0;
        foreach (var delivery in Active)
        {
            if (delivery is not DropPod { IsOpen: false } pod) continue;
            for (var i = 0; i < ItemList.MaxPickups; i++)
            {
                if (pod.Anchor(i) is not { } anchor || Vec.SqrLength(anchor.position - position) > ShutBayReach * ShutBayReach) continue;
                thrower = pod.Thrower; throwId = pod.ThrowId;
                return true;
            }
        }
        return false;
    }

    private static bool Add(Delivery delivery, out Delivery added)
    {
        added = delivery;
        if (delivery.Root == null)
        {
            return false;
        }
        if (delivery.Quiet && !delivery.CatchUp())
        {
            delivery.Destroy();
            return false;
        }
        Active.Add(delivery);
        Built.Add(KeyOf(delivery));
        if (ForgeCore.Session.IsHost) WriteRow(delivery);
        return true;
    }

    /// <summary>Host: runs <paramref name="work"/> after <paramref name="seconds"/> unless the calls in flight are voided first.</summary>
    internal static void HostAfter(float seconds, Action work) => HostWork.Add((Time.time + seconds, work));

    /// <summary>Host: watches the pickups placed in a pod and tells every peer when the last one has been taken.</summary>
    internal static void Watch(ulong thrower, uint throwId, ItemInLevel?[] items)
        => Watched.Add(new Contents { Thrower = thrower, ThrowId = throwId, Items = items, NextCheck = Time.time + ContentsCheckSeconds });

    /// <summary>Host: a pod is empty; it stays a while, then leaves. Every other peer learns it from the pod's row.</summary>
    internal static void ReportEmpty(ulong thrower, uint throwId)
    {
        foreach (var d in Active)
            if (d is DropPod pod && pod.Thrower == thrower && pod.ThrowId == throwId) { pod.Emptied(); WriteRow(pod); }
    }

    /// <summary>The shell that raised the sentry of this identity (<see cref="SentryIdentity.For"/>: the call's own, so two turrets landing side by side keep their own shells).</summary>
    private static TurretShell? ShellOf(ulong identity)
    {
        foreach (var d in Active)
            if (d is TurretShell shell && SentryIdentity.For(shell.Thrower, shell.ThrowId) == identity) return shell;
        return null;
    }

    /// <summary>Host: the shell of the sentry <paramref name="identity"/> takes its turret back now. Every other peer learns it from the shell's row.</summary>
    internal static void ReportRecall(ulong identity)
    {
        if (ShellOf(identity) is not { } shell) return;
        shell.Recall();
        WriteRow(shell);
    }

    /// <summary>Every peer whose own timer ran out for the sentry <paramref name="identity"/>; a shell already recalled stays as it is.</summary>
    internal static void RecallOf(ulong identity) => ShellOf(identity)?.Recall();

    /// <summary>Host: reports a failed supply delivery and retires its untrackable pod. Native pickups that did spawn are
    /// left alone and remain available.</summary>
    internal static void ReportFailure(ulong thrower, uint throwId)
    {
        Watched.RemoveAll(contents => contents.Thrower == thrower && contents.ThrowId == throwId);
        Plugin.Logger.ErrorOnce("Drop pod of throw " + throwId + " failed; telling every peer to retire it. Any pickups already spawned remain available.");
        OrdnanceNetwork.Failed.Broadcast(new CallKey(thrower, throwId));
        Plugin.Session.DeliveryFailed(thrower, throwId);
    }

    /// <summary>Every peer: a failed supply delivery retires its pod on the normal departure timeline without claiming the
    /// contents were taken.</summary>
    internal static void Failed(ulong thrower, uint throwId)
    {
        foreach (var d in Active)
            if (d is DropPod pod && pod.Thrower == thrower && pod.ThrowId == throwId) pod.Emptied();
    }

    /// <summary>Every frame, but an idle frame (no arrival, delayed step, watched pod, held pickup or spraying gravel) returns at
    /// once. <paramref name="now"/> is the frame's <c>Time.time</c>.</summary>
    internal static void Tick(float now)
    {
        if (Active.Count == 0 && HostWork.Count == 0 && Watched.Count == 0 && !SupplyHold.Any)
            return;
        for (var i = HostWork.Count - 1; i >= 0; i--)
        {
            if (now < HostWork[i].Due) continue;
            var work = HostWork[i].Work;
            HostWork.RemoveAt(i);
            try { work(); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A delayed delivery step failed: " + error); }
        }
        if (ForgeCore.Session.IsHost) CheckContents(now);
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            bool alive;
            try { alive = Active[i].Tick(now); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A delivery animation failed and was removed: " + error); alive = false; }
            if (alive) continue;
            Retire(Active[i]);
            Active.RemoveAt(i);
        }
        SupplyHold.Tick();
    }

    /// <summary>A pod or shell is gone from this peer; the host takes its row away with it.</summary>
    private static void Retire(Delivery d)
    {
        d.Finished();
        d.Destroy();
        if (ForgeCore.Session.IsHost) OrdnanceNetwork.DeliveryRows.Remove(KeyOf(d));
    }

    private static void CheckContents(float now)
    {
        for (var i = Watched.Count - 1; i >= 0; i--)
        {
            var c = Watched[i];
            if (now < c.NextCheck) continue;
            c.NextCheck = now + ContentsCheckSeconds;
            var left = false;
            foreach (var item in c.Items)
                if (item != null && !item.WasCollected && item.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>()?.GetCurrentState().status == ePickupItemStatus.PlacedInLevel)
                    left = true;
            if (left) continue;
            Watched.RemoveAt(i);
            ReportEmpty(c.Thrower, c.ThrowId);
        }
    }

    internal static void Clear()
    {
        foreach (var d in Active) Retire(d);
        Active.Clear();
        Built.Clear();
        EndHostSteps();
    }

    /// <summary>A host change: the host's own delayed steps and pickup watches are void (the new host starts its own), and so is every arrival
    /// but a pod that still holds something to claim (<see cref="OrdnanceClaims.HoldsPod"/>), which stays for its new host to carry on.</summary>
    internal static void HostChanged()
    {
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            var d = Active[i];
            if (d is DropPod && OrdnanceClaims.HoldsPod(d.Thrower, d.ThrowId)) continue;
            Active.RemoveAt(i);
            Retire(d);
        }
        EndHostSteps();
    }

    private static void EndHostSteps()
    {
        HostWork.Clear();
        Watched.Clear();
        SupplyHold.Clear();
        OrdnanceCues.Clear();
        OrdnanceArrival.Clear();
    }

}

/// <summary>One animated arrival, the same on every peer.</summary>
internal abstract class Delivery
{
    /// <summary>This arrival's own hold key for the tail of its fall.</summary>
    private readonly int _fallKey = ForgeVisual.Fx.NewKey();

    /// <summary><paramref name="yaw"/> is the arrival's facing (thrower toward landing) in degrees; <paramref name="elapsed"/> the
    /// seconds already played on the host (a late joiner's replay), 0 for a fresh arrival. A replayed arrival skips the sounds
    /// shake and events of what already happened.</summary>
    protected Delivery(ulong thrower, uint throwId, Vector3 landing, float yaw, float elapsed, bool replayed)
    {
        AudioDimension = ForgeAudio.AudioKit.DimensionAt(landing);
        Thrower = thrower; ThrowId = throwId; Landing = landing; Yaw = yaw; Start = Time.time - elapsed; Quiet = replayed;
    }

    internal ulong Thrower { get; }
    internal uint ThrowId { get; }
    internal float Yaw { get; }
    internal Vector3 LandingPoint => Landing;
    internal float StartTime => Start;
    protected int AudioDimension { get; }
    protected Vector3 Landing { get; }
    protected float Start { get; }
    /// <summary>True while a replayed arrival restores its current pose and flags, before it is exposed.</summary>
    internal bool Quiet { get; private set; }

    internal bool CatchUp()
    {
        var alive = Tick(Time.time);
        Quiet = false;
        return alive;
    }
    internal GameObject? Root { get; set; }

    /// <summary>False once finished.</summary>
    internal abstract bool Tick(float now);

    /// <summary>Raised once when the delivery leaves the level (finished, or cleared by a level exit or reset).</summary>
    internal virtual void Finished() { }

    protected void Raise(Action? handler, string what)
    {
        if (Quiet || handler == null) return;
        try { handler(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce(GetType().Name + " of throw " + ThrowId + ": a " + what + " handler failed: " + error); }
    }

    /// <summary>The footprint of the model, which sizes the landing, door and departure effects.</summary>
    protected Vector3 FxSize { get; set; } = Vector3.one;

    internal virtual void Destroy()
    {
        if (Root != null && !Root.WasCollected) Object.Destroy(Root);
        Root = null;
    }

    /// <summary>The fall and the drill boring in; true from the frame of impact on.</summary>
    protected bool Fall(float t, ref bool impacted, OrdnanceImpact impact)
        => Fall(t, ref impacted, impact, Landing, DeliveryTiming.SinkDepth, DeliveryTiming.SinkSeconds);

    /// <summary>The fall to <paramref name="rest"/> (the root's resting position) plus <paramref name="sinkDepth"/>, then the drill
    /// boring in the last <paramref name="sinkDepth"/> metres over <paramref name="sinkSeconds"/>; the touchdown effects are at the landing point.</summary>
    protected bool Fall(float t, ref bool impacted, OrdnanceImpact impact, Vector3 rest, float sinkDepth, float sinkSeconds)
    {
        if (t < DeliveryTiming.FallSeconds)
        {
            var top = rest + Vec.Up * (sinkDepth + DeliveryTiming.FallHeight);
            var position = Vector3.Lerp(top, rest + Vec.Up * sinkDepth, t / DeliveryTiming.FallSeconds);
            Root!.transform.position = position;
            // Continuous, from the shared timeline: a late joiner mid-fall sees the tail from here on.
            if (OrdnanceFxSlots.PodFall is { } tail) ForgeVisual.Fx.Hold(tail, _fallKey, position, Vec.Down, float.NaN, Color.white);
            return false;
        }
        var sink = Math.Min(1f, (t - DeliveryTiming.FallSeconds) / sinkSeconds);
        Root!.transform.position = rest + Vec.Up * (sinkDepth * (1f - sink));
        if (!impacted)
        {
            impacted = true;
            if (!Quiet)
            {
                OrdnanceArrival.Impact(Root!.transform, Landing, impact);
            }
            Landed();
        }
        return true;
    }

    protected virtual void Landed() { }

    protected static float EaseOut(float x) { x = Math.Clamp(x, 0f, 1f); return 1f - (1f - x) * (1f - x); }
    protected static float EaseIn(float x) { x = Math.Clamp(x, 0f, 1f); return x * x; }

    /// <summary>A hinged part: its rest rotation, axis and opening direction.</summary>
    protected readonly struct Hinge
    {
        internal Hinge(Transform part, Vector3 axis, float angle)
        {
            Part = part; Rest = part.localRotation; Axis = axis; Angle = angle;
        }

        internal readonly Transform Part;
        internal readonly Quaternion Rest;
        internal readonly Vector3 Axis;
        internal readonly float Angle;

        internal void Set(float progress) => Part.localRotation = Rest * Quaternion.AngleAxis(Angle * progress, Axis);
    }

    protected static Transform Child(GameObject root, string name)
        => root.transform.Find(name) ?? throw new InvalidOperationException("The model has no part '" + name + "'.");
}

/// <summary>The one drop pod, for supplies and heavy weapons alike: falls, opens its four doors, holds the host's pickups on its
/// tiers or a weapon in a bay, and leaves a while after the host reports it empty. Pickups use the shelves; a weapon, which
/// stands a whole bay high, does not, so the shelves are only shown for the built-in <c>Item</c> payload.</summary>
internal sealed class DropPod : Delivery
{
    /// <summary>Deeper than the pod stands above the ground, so it is out of sight before it is removed.</summary>
    private const float LeaveMargin = 0.3f;
    private readonly Hinge[] _doors = Array.Empty<Hinge>();
    private readonly Transform[] _hinges = Array.Empty<Transform>();
    private readonly OrdnancePodAppearance? _appearance;
    private readonly Transform[] _weaponAnchors = Array.Empty<Transform>();
    private readonly Vector3 _size;
    /// <summary>The pod's touchdown: the thud and a heavy shake (its debris sound plays as the dust settles, in <see cref="Landed"/>).</summary>
    private static readonly OrdnanceImpact Impact = new() { Parts = OrdnanceImpactParts.Thud | OrdnanceImpactParts.Shake, Strength = 0.35f };
    private bool _impacted, _opened, _unlatched, _leaving;
    private float _emptiedAt = -1f;
    private readonly Transform?[] _anchors = new Transform?[ItemList.MaxPickups];

    internal OrdnancePod Handle { get; }
    internal uint DefinitionKey { get; }
    internal int WeaponBayCount => _appearance?.WeaponBayCount ?? 0;
    internal bool IsOpen => _opened;
    internal bool HasLanded => _impacted;
    internal Transform? Anchor(int index) => index >= 0 && index < _anchors.Length && Root != null ? _anchors[index] : null;
    internal Transform? WeaponAnchor => WeaponAnchorAt(0);

    internal Transform? WeaponAnchorAt(int index) => Root == null || index < 0 || index >= _weaponAnchors.Length ? null : _weaponAnchors[index];

    internal OrdnancePodBay? Bay(int index, bool weapon)
    {
        if (Root == null || index < 0) return null;
        var sockets = weapon ? _appearance!.WeaponBays : _appearance!.PickupBays;
        if (index >= sockets.Length) return null;
        var socket = sockets[index];
        var anchor = weapon ? _weaponAnchors[index] : _anchors[index]!;
        return new OrdnancePodBay(anchor.position, anchor.rotation * socket.Rotation, socket.Size, anchor);
    }

    internal DropPod(ulong thrower, uint throwId, Vector3 landing, float yaw, float elapsed, bool replayed, OrdnanceDefinition definition) : base(thrower, throwId, landing, yaw, elapsed, replayed)
    {
        Handle = new OrdnancePod(this);
        DefinitionKey = definition.Key;
        var hasPayload = OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload);
        _appearance = (hasPayload ? payload.GetPodAppearance(definition) : null) ?? OrdnanceAssets.PodAppearance;
        if (_appearance == null) return;
        _doors = new Hinge[_appearance.Doors.Length];
        _hinges = new Transform[_doors.Length];
        _weaponAnchors = new Transform[_appearance.WeaponBays.Length];
        var model = _appearance.Model;
        var podRotation = DeliveryTiming.PodRotation(yaw);
        Root = model.Spawn(landing + Vec.Up * (DeliveryTiming.SinkDepth + DeliveryTiming.FallHeight), podRotation)
            ?? throw new InvalidOperationException("The drop pod model could not be spawned (ForgeVisual has logged why).");
        Root.name = "ForgeOrdnance.DropPod";
        _size = ForgeVisual.Models.WorldBounds(Root).size;
        FxSize = _size;
        for (var i = 0; i < _doors.Length; i++)
        {
            var door = _appearance.Doors[i];
            _doors[i] = new Hinge(Child(Root, door.Path), door.Axis, door.Degrees);
            _hinges[i] = _doors[i].Part;
        }
        for (var i = 0; i < _appearance.PickupBays.Length; i++) _anchors[i] = Child(Root, _appearance.PickupBays[i].Path);
        for (var i = 0; i < _weaponAnchors.Length; i++) _weaponAnchors[i] = Child(Root, _appearance.WeaponBays[i].Path);
        if (definition.PayloadType != ItemPayload.TypeName)
            foreach (var shelf in OrdnanceAssets.ShelfPrefabs) Root.transform.Find(shelf)?.gameObject.SetActive(false);
        if (!Quiet) OrdnanceCues.Attach("drop.incoming", Root, AudioDimension, DeliveryTiming.IncomingStart);
    }

    internal void Emptied(float secondsAgo = 0f) { if (_emptiedAt < 0f) _emptiedAt = Time.time - secondsAgo; }

    /// <summary>Seconds since the host reported the pod empty, or -1.</summary>
    internal float SinceEmptied(float now) => _emptiedAt < 0f ? -1f : now - _emptiedAt;

    internal override bool Tick(float now)
    {
        var t = now - Start;
        if (!Fall(t, ref _impacted, Impact)) return true;
        var open = t - DeliveryTiming.FallSeconds - DeliveryTiming.PodOpenAfterImpact;
        if (!_unlatched && open >= -DeliveryTiming.UnlatchLead)
        {
            _unlatched = true;
            if (!Quiet) OrdnanceCues.World(OrdnanceSound.DropUnlatch, Root!, AudioDimension);
        }
        if (open >= 0f)
        {
            if (!_opened)
            {
                _opened = true;
                if (!Quiet)
                {
                    OrdnanceCues.Attach("drop.open", Root!, AudioDimension);
                    OrdnanceFx.PodOpened(Landing + Vec.Up * (_size.y * 0.4f), _hinges, _size);
                }
                Raise(Handle.OnOpened, "pod opened");
            }
            var progress = EaseOut(open / DeliveryTiming.DoorSeconds);
            foreach (var door in _doors) door.Set(progress);
        }
        if (_emptiedAt < 0f) return true;
        var leave = now - _emptiedAt - DeliveryTiming.PodLingerAfterEmpty;
        if (leave < 0f) return true;
        if (!_leaving)
        {
            _leaving = true;
            if (!Quiet)
            {
                OrdnanceCues.World(OrdnanceSound.DropLeave, Root!, AudioDimension);
                OrdnanceFx.PodLeaving(Landing, _size);
            }
        }
        if (leave >= DeliveryTiming.LeaveSeconds) return false;
        Root!.transform.position = Landing - Vec.Up * ((_size.y + LeaveMargin) * EaseIn(leave / DeliveryTiming.LeaveSeconds));
        return true;
    }

    protected override void Landed()
    {
        if (!Quiet)
        {
            OrdnanceFx.PodLanded(Landing, _size);
            OrdnanceCues.World(OrdnanceSound.DropDebris, Root!, AudioDimension);
        }
        Raise(Handle.OnLanded, "pod landed");
    }

    internal override void Finished() => Raise(Handle.OnLeft, "pod left");

}

/// <summary>The turret's drop base (the shell) with the upper part (<see cref="OrdnanceShellAppearance.Head"/>, the turret or device) hidden
/// inside it: the base falls drill first and bores down until its sealed top is level with the ground; its two lids (<c>lid_a</c>,
/// <c>lid_b</c>) are then thrown off to either side in a tumbling arc, with a puff of dust, and the upper part rises out of the opening with
/// its barrel straight up; a gun turns its barrel level and is then left to the game's sentry, which the host stands at that moment. At the
/// end the barrel turns up, the upper part sinks back and the base sinks out of sight. Every moment is read from the arrival's shared
/// timeline, so every peer, a late joiner included, draws the same pose. A base model without lids skips that step.</summary>
internal sealed class TurretShell : Delivery
{
    /// <summary>A gun's upper part is drawn until the sentry's own look stands; it is given up this long after the barrel has turned down.</summary>
    private const float HandoverGrace = 2.5f;
    private const float GunSettle = 0.6f;
    /// <summary>How long a shell without an appearance of its own stays.</summary>
    private const float PlainSeconds = 8f;
    /// <summary>Metres the bored-in rim stands above the floor, and how far the base sinks at the end to go below it.</summary>
    private const float RimAboveGround = 0.01f, LeaveDepth = 0.1f;
    private static readonly string[] LidNames = { "lid_a", "lid_b" };
    private static readonly Vector3 LidDust = new(0.7f, 0.5f, 0.7f);
    private static readonly OrdnanceShellAppearance Plain = new(new BurrowSpec { RiseHeight = 0.1f, RiseSeconds = 0f }, PlainSeconds);
    private static readonly OrdnanceImpact Impact = new() { Parts = OrdnanceImpactParts.Thud | OrdnanceImpactParts.Shake, Strength = 0.3f };
    private readonly OrdnanceShellAppearance _look;
    private readonly BurrowSpec _burrow;
    /// <summary>The base's root place once bored in (its top level with the ground), how far it sank from touchdown to there, and the height of its top over its root.</summary>
    private readonly Vector3 _rest;
    private readonly float _depth, _toTop;
    /// <summary>Seconds from the arrival: the lids go, the upper part starts to rise, and (never when infinite) starts to stow.</summary>
    private readonly float _lidAt, _riseAt;
    private float _stowAt;
    /// <summary>When the host called the turret back (out of ammunition or out of time), or negative.</summary>
    private float _recalledAt = -1f;
    private readonly List<Transform> _lids = new();
    private ForgeVisual.Thrown[] _thrown = Array.Empty<ForgeVisual.Thrown>();
    private GameObject? _head;
    private Transform? _gun;
    private Quaternion _gunLevel;
    private Vector3 _right;
    private float _gunUp;
    private bool _impacted, _opened, _online, _stowing, _stowShown, _lidsThrown, _lidsGone;

    internal OrdnanceShell Handle { get; }
    internal uint DefinitionKey { get; }
    internal bool IsOpen => _opened;
    internal bool HasLanded => _impacted;
    internal GameObject? HeadObject => _head;
    /// <summary>How far the upper part has risen, 0 (inside the base) to 1 (standing).</summary>
    internal float Rise { get; private set; }

    internal TurretShell(ulong thrower, uint throwId, Vector3 landing, float yaw, float elapsed, bool replayed, OrdnanceDefinition definition) : base(thrower, throwId, landing, yaw, elapsed, replayed)
    {
        Handle = new OrdnanceShell(this);
        DefinitionKey = definition.Key;
        _look = (OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload) ? payload.GetShellAppearance(definition) : null) ?? Plain;
        _burrow = _look.Burrow;
        _lidAt = DeliveryTiming.LidDelay(_burrow);
        _riseAt = DeliveryTiming.HeadRiseDelay(_burrow);
        _stowAt = _look.Lifetime > 0f ? _riseAt + _burrow.HeadSeconds + _look.Lifetime : float.PositiveInfinity;
        var model = OrdnanceAssets.Shell;
        if (model == null) return;
        Root = model.Spawn(landing + Vec.Up * DeliveryTiming.FallHeight, Quaternion.Euler(0f, yaw, 0f))
            ?? throw new InvalidOperationException("The turret shell model could not be spawned (ForgeVisual has logged why).");
        Root.name = "ForgeOrdnance.TurretShell";
        Root.transform.localScale = Vector3.one * _burrow.BaseScale;
        var bounds = ForgeVisual.Models.WorldBounds(Root);
        FxSize = bounds.size;
        // The base's lowest point is the drill tip and its top the rim of its body (the lids stand a little above it), wherever its origin is.
        var origin = Root.transform.position.y;
        var toTip = origin - bounds.min.y;
        _toTop = (Root.transform.Find("body") is { } body && body.GetComponent<Renderer>() is { } renderer ? renderer.bounds.max.y : bounds.max.y) - origin;
        _depth = Math.Max(0.01f, toTip + _toTop);
        _rest = landing + Vec.Up * (RimAboveGround - _toTop);
        foreach (var name in LidNames)
            if (Root.transform.Find(name) is { } lid) _lids.Add(lid);
        if (_look.Head != null)
        {
            _head = _look.Head.Spawn(landing + Vec.Up * DeliveryTiming.FallHeight, Vec.Identity)
                ?? throw new InvalidOperationException("The upper part model " + _look.Head.Name + " could not be spawned (its bundle is not loaded).");
            _head.name = "ForgeOrdnance.ShellHead";
            _head.transform.localScale = Vector3.one * _look.HeadScale;
            _gun = _look.Gun == null ? null : _head.transform.Find(_look.Gun) ?? throw new InvalidOperationException("The upper part model has no gun part '" + _look.Gun + "'.");
            Face(Quaternion.Euler(0f, yaw, 0f) * Vec.Forward);
            _head.SetActive(false);
        }
        if (!Quiet) OrdnanceCues.Attach("drop.incoming", Root, AudioDimension, DeliveryTiming.IncomingStart);
    }

    protected override void Landed()
    {
        if (!Quiet) if (OrdnanceFxSlots.ShellUnfold is { } unfold) ForgeVisual.Fx.Play(unfold, Landing, Vec.Up, OrdnanceFx.ScaleOf(FxSize), Color.white);
        Raise(Handle.OnLanded, "shell landed");
    }

    internal override void Finished() => Raise(Handle.OnLeft, "shell left");

    internal float SinceRecalled(float now) => _recalledAt < 0f ? -1f : now - _recalledAt;

    /// <summary>The turret stows from now (<paramref name="secondsAgo"/> before now for a late joiner) and the shell then sinks away, the same timeline as
    /// the end of its lifetime. Taken at once, while the look still stands: the sentry may be gone by the next frame.</summary>
    internal void Recall(float secondsAgo = 0f)
    {
        if (_recalledAt >= 0f) return;
        _recalledAt = Time.time - secondsAgo;
        _stowAt = Math.Min(_stowAt, _recalledAt - Start);
        if (_opened) BeginStow();
    }

    internal override void Destroy()
    {
        foreach (var lid in _lids)
            if (lid != null && !lid.WasCollected) Object.Destroy(lid.gameObject);
        _lids.Clear();
        if (_head != null && !_head.WasCollected) Object.Destroy(_head);
        _head = null;
        base.Destroy();
    }

    internal override bool Tick(float now)
    {
        var t = now - Start;
        var landed = Fall(t, ref _impacted, Impact, _rest, _depth, _burrow.BurrowSeconds);
        var head = t - _riseAt;
        var stowAt = _stowAt - _riseAt;
        // Before the upper part rises it is inside the base, barrel straight up.
        var pose = landed ? BurrowTimeline.Pose(_burrow, head, stowAt) : default;
        Rise = pose.Lift;
        if (landed)
        {
            if (t >= _lidAt) FlyLids(t - _lidAt);
            if (head >= 0f && !_opened)
            {
                _opened = true;
                if (!Quiet) { if (OrdnanceFxSlots.TurretRise is { } rise) ForgeVisual.Fx.Play(rise, Landing, Vec.Up, OrdnanceFx.ScaleOf(FxSize), Color.white); OrdnanceTurrets.RaiseDeployed(_look.LookId, Root!.transform); }
                Raise(Handle.OnOpened, "shell opened");
            }
            // The turret is online as the host's sentry appears, when the barrel has turned down: the same timeline on every peer.
            if (!_online && head >= _burrow.HeadSeconds)
            {
                _online = true;
                if (!Quiet) OrdnanceFx.SentryOnline(Landing + Vec.Up * 0.3f);
            }
        }
        var leave = head - stowAt - _burrow.HeadSeconds;
        if (landed && leave >= 0f)
        {
            if (leave >= _burrow.LeaveSeconds) return false;
            Root!.transform.position = _rest - Vec.Up * (LeaveDepth * EaseIn(leave / _burrow.LeaveSeconds));
        }
        var dive = _look.Dive != null && landed && head >= _burrow.HeadSeconds && head < stowAt ? Mathf.Clamp01(_look.Dive(head - _burrow.HeadSeconds)) : 0f;
        if (_head != null) PoseHead(landed && t >= _lidAt && leave < 0f, head, stowAt, pose, dive);
        return true;
    }

    /// <summary>The lids are thrown off at <paramref name="seconds"/> = 0 and fly, lie and vanish by <see cref="ForgeVisual.Thrown"/>'s one path.</summary>
    private void FlyLids(float seconds)
    {
        if (_lids.Count == 0 || _lidsGone) return;
        if (!_lidsThrown)
        {
            _lidsThrown = true;
            _thrown = new ForgeVisual.Thrown[_lids.Count];
            var side = Quaternion.Euler(0f, Yaw, 0f) * Vector3.right;
            var centre = Root!.transform.position;
            for (var i = 0; i < _lids.Count; i++)
            {
                var lid = _lids[i];
                var at = lid.position;
                var outward = Vector3.ProjectOnPlane(at - centre, Vec.Up);
                if (Vec.SqrLength(outward) < 1e-4f) outward = i % 2 == 0 ? side : -side;
                outward.Normalize();
                _thrown[i] = new ForgeVisual.Thrown(at, lid.rotation, outward * _burrow.LidSpeed + Vec.Up * _burrow.LidLift,
                    Vector3.Cross(Vec.Up, outward), _burrow.LidSpin, at.y);
                lid.SetParent(null, true);
            }
            if (!Quiet)
            {
                OrdnanceCues.World(OrdnanceSound.LidBlast, Root!, AudioDimension);
                PodImpactEffects.Play(Landing, LidDust);
            }
        }
        var over = true;
        for (var i = 0; i < _lids.Count; i++)
        {
            if (seconds > _thrown[i].FlightSeconds + _burrow.LidRestSeconds) continue;
            over = false;
            _thrown[i].Pose(seconds, out var position, out var rotation);
            _lids[i].SetPositionAndRotation(position, rotation);
        }
        if (!over) return;
        _lidsGone = true;
        foreach (var lid in _lids) lid.gameObject.SetActive(false);
    }

    private void PoseHead(bool open, float head, float stowAt, BurrowPose pose, float dive)
    {
        if (open && head >= stowAt) BeginStow();
        // A gun's upper part is the game's sentry once it has turned down: drawn again only while that sentry's look has not stood yet, or when it stows.
        var show = open && (_gun == null || (head < _burrow.HeadSeconds ? true : head < stowAt ? head < _burrow.HeadSeconds + HandoverGrace && !TurretLooks.HasLookOf(Thrower, ThrowId) : _stowShown));
        if (_head!.activeSelf != show) _head.SetActive(show);
        if (!show) return;
        // Standing, its origin is at the base's top; before that it is down inside the base.
        _head.transform.position = Root!.transform.position + Vec.Up * (_toTop - _burrow.RiseHeight * Mathf.Clamp01((1f - pose.Lift) + dive));
        // Coming down the gun swings a little past level and settles, in the same time.
        var level = head < stowAt ? pose.Level + GunSettle * Mathf.Sin(Mathf.PI * pose.Level) * pose.Level * pose.Level : pose.Level;
        if (_gun != null) _gun.rotation = Quaternion.AngleAxis(_gunUp * (1f - level), _right) * _gunLevel;
    }

    /// <summary>Stowing starts from where the turret stands now; with no sentry left (picked up) there is nothing to stow, unless this peer joined late or the host recalled it.</summary>
    private void BeginStow()
    {
        if (_gun == null || _head == null || _stowing) return;
        _stowing = true;
        var found = TurretLooks.Retire(Thrower, ThrowId, out var facing, out var pitch);
        _stowShown = found || Quiet || _recalledAt >= 0f;
        if (!found) return;
        Face(facing);
        // From the pitch the gun stood at, not from level: no snap on the way up.
        _gunLevel = Quaternion.AngleAxis(pitch, _right) * _gunLevel;
        _gunUp -= pitch;
    }

    /// <summary>Turns the upper part to face <paramref name="facing"/> (level) and takes note of the gun's level pose and the way it tips up.</summary>
    private void Face(Vector3 facing)
    {
        _head!.transform.rotation = Quaternion.LookRotation(facing, Vec.Up) * _look.HeadFront;
        if (_gun == null) return;
        _right = Vector3.Cross(Vec.Up, facing).normalized;
        _gunUp = Vector3.SignedAngle(facing, Vec.Up, _right);
        _gunLevel = _gun.rotation;
    }
}
