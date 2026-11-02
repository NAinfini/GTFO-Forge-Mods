using System;
using LevelGeneration;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>
/// The built-in arrivals other payload types can reuse. Every peer starts the same pod or shell from
/// <see cref="IOrdnancePayload.OnArrived"/> (same position and <see cref="OrdnancePresentation.Rotation"/>, so they match); the
/// host places networked contents from <see cref="IOrdnancePayload.Execute"/> after the matching delay. A payload that starts a
/// pod or shell should return true from <see cref="IOrdnancePayload.HasOwnArrival"/> (the pod and shell shake the camera themselves).
/// An arrival is two stages, the vehicle and its contents: a <see cref="OrdnanceArrivalMode.Direct"/> arrival has no vehicle, so
/// <see cref="StartPod"/> and <see cref="StartShell"/> return null for it and the contents delays are 0.
/// </summary>
public static class OrdnanceDelivery
{
    /// <summary>Seconds from arrival until the pod's doors stand open (when the host places pod contents); 0 for a direct arrival.</summary>
    public static float PodContentsDelay(OrdnanceArrivalMode arrival) => ArrivalPlan.ContentsAfter(arrival, DeliveryTiming.ContentsOpenDelay);

    /// <summary>Seconds from arrival until the shell's petals are open (when the host stands the shell's contents); 0 for a direct arrival.</summary>
    public static float ShellContentsDelay(OrdnanceArrivalMode arrival) => ArrivalPlan.ContentsAfter(arrival, DeliveryTiming.SentrySpawnDelay);

    /// <summary>Pickup anchors in a pod.</summary>
    public static int PodAnchorCount => ItemList.MaxPickups;

    /// <summary>The pod's world rotation for an arrival rotation: its doors face back toward the thrower.</summary>
    public static Quaternion PodRotation(Quaternion arrival) => DeliveryTiming.PodRotation(arrival.eulerAngles.y);

    /// <summary>Host or any peer: the world position of pickup anchor <paramref name="index"/> (0-based) of the pod for this
    /// landing, without a pod instance. False when the art bundle did not load (there is no pod then).</summary>
    public static bool TryPodAnchor(Vector3 landing, Quaternion arrival, int index, out Vector3 position)
    {
        position = landing;
        if (!OrdnanceAssets.TryBay(landing, PodRotation(arrival), index, false, out var bay)) return false;
        position = bay.Position;
        return true;
    }

    /// <summary>As <see cref="TryPodAnchor"/> for the first weapon bay's floor centre (a long object such as a heavy weapon).</summary>
    public static bool TryPodWeaponAnchor(Vector3 landing, Quaternion arrival, out Vector3 position)
    {
        position = landing;
        if (!OrdnanceAssets.TryBay(landing, PodRotation(arrival), 0, true, out var bay)) return false;
        position = bay.Position;
        return true;
    }

    /// <summary>Host or any peer: pickup bay <paramref name="index"/> (0-based; 0 and 1 are the floor tier of the pod's
    /// +X and -X bay, 2 and 3 the shelf above them) of the pod for this landing, without a pod instance: floor centre,
    /// orientation and usable size. False when the art bundle did not load.</summary>
    public static bool TryPodBay(Vector3 landing, Quaternion arrival, int index, out OrdnancePodBay bay)
        => OrdnanceAssets.TryBay(landing, PodRotation(arrival), index, false, out bay);

    /// <summary>As <see cref="TryPodBay"/> for the first weapon bay: one long object standing upright (its long axis
    /// along <see cref="OrdnancePodBay.Rotation"/>'s up).</summary>
    public static bool TryPodWeaponBay(Vector3 landing, Quaternion arrival, out OrdnancePodBay bay)
        => OrdnanceAssets.TryBay(landing, PodRotation(arrival), 0, true, out bay);

    /// <summary>Every peer: plays a drop pod for this arrival. Null for a direct arrival, and (one log line) when the art bundle did not load.</summary>
    public static OrdnancePod? StartPod(OrdnancePresentation arrival) => Deliveries.StartPod(arrival);

    /// <summary>Every peer: plays a turret shell for this arrival. Null for a direct arrival, and (one log line) when the art bundle did not load.</summary>
    public static OrdnanceShell? StartShell(OrdnancePresentation arrival) => Deliveries.StartShell(arrival);

    /// <summary>The pod still in the level for a call, e.g. from <see cref="IOrdnancePayload.OnData"/>. Replayed pods already
    /// have their current pose and flags; past landing and opening events are not raised again.</summary>
    public static OrdnancePod? FindPod(ulong summonerLookup, uint throwId) => Deliveries.FindPod(summonerLookup, throwId);

    /// <summary>The shell still in the level for a call. Replayed shells already have their current pose and flags;
    /// past landing and opening events are not raised again.</summary>
    public static OrdnanceShell? FindShell(ulong summonerLookup, uint throwId) => Deliveries.FindShell(summonerLookup, throwId);

    /// <summary>Host: runs <paramref name="work"/> after <paramref name="seconds"/>, on the main thread. Dropped (not run) when
    /// the calls in flight are voided first: level exit, checkpoint reload, host change. An exception is logged.</summary>
    public static void HostAfter(float seconds, Action work)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        RequireHost(nameof(HostAfter));
        Deliveries.HostAfter(seconds, work);
    }

    /// <summary>Host: watches pickups placed in a pod; once none of them lies in the level any more, every peer's pod is told
    /// it is empty and leaves (<see cref="OrdnancePod.Left"/>) after its linger time.</summary>
    public static void HostWatchPickups(ulong summonerLookup, uint throwId, ItemInLevel[] items)
    {
        if (items == null || items.Length == 0) throw new ArgumentException("No pickups to watch.", nameof(items));
        RequireHost(nameof(HostWatchPickups));
        Deliveries.Watch(summonerLookup, throwId, Array.ConvertAll(items, i => (ItemInLevel?)i));
    }

    /// <summary>Host: declares a pod empty now (custom contents taken or used up); every peer's pod lingers, then leaves.</summary>
    public static void HostReportPodEmpty(ulong summonerLookup, uint throwId)
    {
        RequireHost(nameof(HostReportPodEmpty));
        Deliveries.ReportEmpty(summonerLookup, throwId);
    }

    /// <summary>Every peer: registers a claimable that lies where there is no drop pod (a direct arrival, or the art bundle did not load): it can be claimed at
    /// <paramref name="position"/> from <paramref name="openAfter"/> seconds on, and is removed once taken. See <see cref="OrdnancePod.AddClaimable"/>.</summary>
    public static OrdnanceClaimable AddClaimable(ulong summonerLookup, uint throwId, Vector3 position, float openAfter, ClaimableSpec spec)
        => OrdnanceClaims.Add(summonerLookup, throwId, spec, null, 0, position, openAfter);

    /// <summary>Host: from now on the claimable of this call can be requested (call it when the thing is really there to take, for
    /// example once its kept state is sent). Requests before are refused.</summary>
    public static void HostOpenClaim(ulong summonerLookup, uint throwId)
    {
        RequireHost(nameof(HostOpenClaim));
        OrdnanceClaims.HostOpen(summonerLookup, throwId);
    }

    private static void RequireHost(string what)
    {
        if (!SNet.IsMaster) throw new InvalidOperationException(what + " is host only.");
    }
}

/// <summary>One compartment of the drop pod, in world space. An object stands on <see cref="Position"/> with its base
/// centred there; up is <see cref="Rotation"/>'s up, the bay opens toward its forward (the doors) and right runs across it.
/// <see cref="Size"/> is the usable room in metres: x across, y height, z depth from the divider to the closed doors.</summary>
public readonly struct OrdnancePodBay
{
    internal OrdnancePodBay(Vector3 position, Quaternion rotation, Vector3 size, Transform? anchor = null) { Position = position; Rotation = rotation; Size = size; Anchor = anchor; }

    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public Vector3 Size { get; }
    /// <summary>The pod's anchor of this bay (what a live pod's contents are parented to), or null for a bay computed without a pod.</summary>
    public Transform? Anchor { get; }

    /// <summary>
    /// Stands <paramref name="look"/> (an instance of a long model, such as a heavy weapon) upright in this bay at real size: a holder
    /// is made at the bay's floor centre (under <see cref="Anchor"/> when there is one, so it falls, opens and leaves with the pod), the
    /// model goes into it with its muzzle up and its side to the doors, and its feet rest on the floor. <paramref name="frontToForward"/>
    /// turns the model's own front to +Z (exported models face -Z). A model longer than the bay is high leans across the bay as far as
    /// needed, and only if leaning is not enough is shrunk. Returns the holder (destroy it to remove the model) and the scale used
    /// (1 = real size) and lean in degrees. Needs the model's meshes to be present; a model with none stays at its own size.
    /// </summary>
    public GameObject Fit(GameObject look, Quaternion frontToForward, out float scale, out float lean)
    {
        if (look == null) throw new ArgumentNullException(nameof(look));
        var holder = new GameObject("ForgeOrdnance.BayHolder");
        var h = holder.transform;
        if (Anchor != null)
        {
            h.SetParent(Anchor, false);
            var lossy = Anchor.lossyScale;
            h.localScale = new Vector3(PodBayFit.SafeInverse(lossy.x), PodBayFit.SafeInverse(lossy.y), PodBayFit.SafeInverse(lossy.z));
        }
        h.position = Position;
        h.rotation = Rotation;
        (scale, lean) = PodBayFit.StandUpright(look, h, Size, frontToForward);
        return holder;
    }
}

/// <summary>The upright fit behind <see cref="OrdnancePodBay.Fit"/>.</summary>
internal static class PodBayFit
{
    internal static float SafeInverse(float v) => Math.Abs(v) < 1e-4f ? 1f : 1f / v;

    internal static (float Scale, float Lean) StandUpright(GameObject look, Transform bay, Vector3 room, Quaternion frontToForward)
    {
        var t = look.transform;
        t.SetParent(bay, false);
        var lossy = bay.lossyScale;
        var unit = new Vector3(SafeInverse(lossy.x), SafeInverse(lossy.y), SafeInverse(lossy.z));
        t.localPosition = Vector3.zero;
        // Model front (+Z after the turn) up, model top across the bay (+X), model side toward the doors (+Z).
        var upright = Quaternion.LookRotation(Vector3.up, Vector3.right) * frontToForward;
        var best = (Scale: 0f, Lean: 0f);
        for (var lean = 0f; lean <= 50f; lean += 2f)
        {
            t.localScale = unit;
            t.localRotation = Quaternion.AngleAxis(lean, Vector3.forward) * upright;
            if (!ForgeVisual.Models.NativeBounds(bay, null, out var b)) return (1f, 0f);
            var fit = Math.Min(1f, Math.Min(room.y / Math.Max(b.size.y, 1e-4f), Math.Min(room.x / Math.Max(b.size.x, 1e-4f), room.z / Math.Max(b.size.z, 1e-4f))));
            if (fit > best.Scale + 1e-3f) best = (fit, lean);
            if (fit >= 1f) break;
        }
        t.localScale = unit * best.Scale;
        t.localRotation = Quaternion.AngleAxis(best.Lean, Vector3.forward) * upright;
        if (ForgeVisual.Models.NativeBounds(bay, null, out var placed))
            t.localPosition = new Vector3(-placed.center.x, -placed.min.y + 0.01f, -placed.center.z);
        return best;
    }
}

/// <summary>One drop pod in the level (every peer). Parent local objects (for example a heavy weapon's look) to
/// <see cref="Anchor"/> or <see cref="WeaponAnchor"/>: they fall, open and leave with the pod. Handlers run on the main thread;
/// an exception in one is logged.</summary>
public sealed class OrdnancePod
{
    private readonly DropPod _pod;

    internal OrdnancePod(DropPod pod) { _pod = pod; }

    public ulong SummonerLookup => _pod.Thrower;
    public uint ThrowId => _pod.ThrowId;
    /// <summary>The pod object; null once it has left.</summary>
    public GameObject? Root => _pod.Root;
    public bool HasLanded => _pod.HasLanded;
    public bool IsOpen => _pod.IsOpen;

    /// <summary>Pickup anchor <paramref name="index"/> (0 to <see cref="OrdnanceDelivery.PodAnchorCount"/> - 1); null once the pod has left.</summary>
    public Transform? Anchor(int index) => _pod.Anchor(index);

    /// <summary>The weapon bay's floor anchor for one long object; null once the pod has left.</summary>
    public Transform? WeaponAnchor => _pod.WeaponAnchor;

    /// <summary>Weapon bays supplied by this pod's appearance (two for the built-in pod).</summary>
    public int WeaponBayCount => _pod.WeaponBayCount;

    /// <summary>Indexed weapon anchor; null for an invalid index or after departure.</summary>
    public Transform? WeaponAnchorAt(int index) => _pod.WeaponAnchorAt(index);

    /// <summary>Indexed weapon bay at the pod's current pose; null for an invalid index or after departure.</summary>
    public OrdnancePodBay? WeaponBayAt(int index) => _pod.Bay(index, true);

    /// <summary>Pickup bay <paramref name="index"/> where the pod is now (it moves while falling and leaving); null once
    /// the pod has left or for an index out of range. See <see cref="OrdnanceDelivery.TryPodBay"/>.</summary>
    public OrdnancePodBay? Bay(int index)
        => _pod.Bay(index, false);

    /// <summary>The weapon bay where the pod is now; null once the pod has left.</summary>
    public OrdnancePodBay? WeaponBay
        => WeaponBayAt(0);

    /// <summary>Every peer: registers a claimable thing lying in this pod's weapon bay <paramref name="bay"/> (see <see cref="WeaponBayCount"/>):
    /// a player can aim at it, ask the host for it with the use key, install it and take it, all through <paramref name="spec"/>. It
    /// opens when the pod opens (at once when it is already open) and goes when the pod leaves.</summary>
    public OrdnanceClaimable AddClaimable(int bay, ClaimableSpec spec)
        => OrdnanceClaims.Add(SummonerLookup, ThrowId, spec, this, bay, default, 0f);

    /// <summary>The pod hit the ground (after <c>1 s</c> of fall).</summary>
    public event Action? Landed;
    /// <summary>The doors start to open (at <see cref="OrdnanceDelivery.PodContentsDelay"/> minus a short beat).</summary>
    public event Action? Opened;
    /// <summary>The pod is gone: it sank away after being emptied, or the level was left or reset. Remove anything you parented.</summary>
    public event Action? Left;

    internal Action? OnLanded => Landed;
    internal Action? OnOpened => Opened;
    internal Action? OnLeft => Left;
}

/// <summary>One turret shell in the level (every peer). Its petals open, then it sinks away a few seconds later.</summary>
public sealed class OrdnanceShell
{
    private readonly TurretShell _shell;

    internal OrdnanceShell(TurretShell shell) { _shell = shell; }

    public ulong SummonerLookup => _shell.Thrower;
    public uint ThrowId => _shell.ThrowId;
    public GameObject? Root => _shell.Root;
    public bool HasLanded => _shell.HasLanded;
    public bool IsOpen => _shell.IsOpen;

    public event Action? Landed;
    public event Action? Opened;
    public event Action? Left;

    internal Action? OnLanded => Landed;
    internal Action? OnOpened => Opened;
    internal Action? OnLeft => Left;
}
