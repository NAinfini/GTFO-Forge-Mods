using ForgeSkin.Format;
using ForgeVisual;
using Gear;
using UnityEngine;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace ForgeSkin.Plugin;

// The parts of the gun in hand a weapon inspect motion moves (PLAN §4.14, InspectMotion.Parts): nodes found by name under the gun (every
// node of the name), the magazine's mount ("@mag", the part holder's m_aMag) and a copy of the magazine's top round put in the chamber
// at the shell-eject point ("@round"). Which motions a gun can play is decided on the press (Missing: a gun without a part's node, or whose
// node draws nothing because a skin hid it or a mounted model replaced the gun, does not play that motion); the nodes and their poses at
// rest are measured then (Bind), in the gun's own space. Each frame Pose writes every node's local pose from the look's move of its part
// (InspectLook.PartDelta) and those rest measures, reading nothing of the game; Restore puts every node back and removes the copy.
sealed class InspectParts
{
    // One node a part moves: its pose at rest in the gun's space and its parent's (the frame its local pose is written in).
    sealed class Node
    {
        public Transform T = null!;
        public GameObject Object = null!;
        public int Part;
        public bool Round, Shown;
        public NVector3 GunAt, ParentAt, ParentScale;
        public NQuaternion GunTurn, ParentTurn;
        public Vector3 RestAt;
        public Quaternion RestTurn;
        // A carrier's children that are no part of the motion (InspectPart.Carrier): held where they are in the gun as it turns.
        public List<Kept>? Kept;
    }

    sealed class Kept
    {
        public Transform T = null!;
        public NVector3 GunAt, Scale;
        public NQuaternion GunTurn;
        public Vector3 RestAt;
        public Quaternion RestTurn;
    }

    const string Eject = "Front_SE"; // the shell-eject align, where the game spawns casings: the chamber's place
    const string RoundName = "Cartridge"; // a magazine's rounds (ammo_bullet nodes)

    readonly Node[] _nodes;
    readonly Renderer? _cartridge;
    GameObject? _copy;

    InspectParts(Node[] nodes, Renderer? cartridge) => (_nodes, _cartridge) = (nodes, cartridge);

    // Every transform under the gun by name (read once per press).
    public static Dictionary<string, List<Transform>> Names(Transform gun)
    {
        var names = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);
        foreach (var t in gun.GetComponentsInChildren<Transform>(true))
        {
            if (!names.TryGetValue(t.name, out var list)) names[t.name] = list = new List<Transform>();
            list.Add(t);
        }
        return names;
    }

    // What of `motion` this gun lacks (null: it can play it): a part's node that is not there or draws nothing.
    public static string? Missing(InspectMotion motion, GearPartHolder? holder, Dictionary<string, List<Transform>> names)
    {
        foreach (var part in motion.Parts)
        {
            if (part.Optional) continue;
            if (part.Node == InspectPart.Round)
            {
                if (Cartridge(holder) == null) return "no shown round in its magazine to put in the chamber";
                if (!names.ContainsKey(Eject)) return $"no shell-eject point ({Eject})";
                continue;
            }
            var found = Find(part, holder, names);
            if (found.Count == 0) return $"no node {string.Join(" or ", part.Nodes)}";
            if (!found.Any(Drawn)) return $"its {string.Join(" or ", part.Nodes)} draws nothing";
        }
        return null;
    }

    // A part's nodes on this gun: the magazine's mount, or every node of the first of its names the gun has that draws something (else of
    // the first it has at all).
    static List<Transform> Find(InspectPart part, GearPartHolder? holder, Dictionary<string, List<Transform>> names)
    {
        if (part.Node == InspectPart.Magazine) return holder != null && holder.m_aMag != null ? new List<Transform> { holder.m_aMag } : new List<Transform>();
        List<Transform>? any = null;
        foreach (var name in part.Nodes)
            if (names.TryGetValue(name, out var list))
            {
                if (list.Any(Drawn)) return list;
                any ??= list;
            }
        return any ?? new List<Transform>();
    }

    static bool Drawn(Transform t)
    {
        foreach (var r in t.GetComponentsInChildren<Renderer>())
            if (Models.IsMesh(r) && Models.Drawn(r)) return true;
        return false;
    }

    // The magazine's first shown round, which the chamber's round copies.
    static Renderer? Cartridge(GearPartHolder? holder)
    {
        if (holder == null || holder.m_aMag == null) return null;
        foreach (var r in holder.m_aMag.GetComponentsInChildren<Renderer>())
            if (r.name.StartsWith(RoundName, StringComparison.Ordinal) && Models.IsMesh(r) && Models.Drawn(r)) return r;
        return null;
    }

    // Measures the parts of `motion` on this gun (it has them: Missing returned null) and what the solve needs of them: each part's
    // pivot, down axis and the box of the drawn meshes under its nodes (`meshes`, the gun's drawn meshes and their boxes in its space; a
    // magazine with none of its own, the boxes of the gun's shown rounds; the chambered round, a round's size at the shell-eject point),
    // and every mesh's box with the part that moves it (the part whose node is its nearest ancestor).
    public static InspectParts Bind(InspectMotion motion, Transform gun, GearPartHolder? holder, Dictionary<string, List<Transform>> names, List<(Renderer Mesh, NVector3 Lo, NVector3 Hi)> meshes, out InspectPartRig[] rig, out InspectBox[] boxes)
    {
        var parts = Bind(motion, gun, holder, names, out rig, out var found);
        var kept = new HashSet<IntPtr>(parts._nodes.Where(n => n.Kept != null).SelectMany(n => n.Kept!).Select(k => k.T.Pointer));
        boxes = new InspectBox[meshes.Count];
        for (var m = 0; m < meshes.Count; m++)
        {
            boxes[m] = new InspectBox { Lo = meshes[m].Lo, Hi = meshes[m].Hi, Part = -1 };
            // (A mesh under a carrier's kept child stays with the gun.)
            for (var up = meshes[m].Mesh.transform; up != null && up.Pointer != gun.Pointer && boxes[m].Part < 0 && !kept.Contains(up.Pointer); up = up.parent)
                for (var i = 0; i < found.Length; i++)
                    if (found[i].Any(t => t.Pointer == up.Pointer)) { boxes[m].Part = i; break; }
        }
        for (var i = 0; i < rig.Length; i++)
        {
            NVector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            void Take(NVector3 l, NVector3 h) => (lo, hi) = (NVector3.Min(lo, l), NVector3.Max(hi, h));
            for (var m = 0; m < meshes.Count; m++) if (boxes[m].Part == i) Take(meshes[m].Lo, meshes[m].Hi);
            if (lo.X > hi.X && motion.Parts[i].Node == InspectPart.Magazine)
                foreach (var (mesh, l, h) in meshes) if (mesh.name.StartsWith(RoundName, StringComparison.Ordinal)) Take(l, h);
            if (lo.X > hi.X && motion.Parts[i].Node == InspectPart.Round) Take(rig[i].Pivot - new NVector3(RoundSize), rig[i].Pivot + new NVector3(RoundSize));
            (rig[i].Lo, rig[i].Hi) = lo.X <= hi.X ? (lo, hi) : (rig[i].Pivot, rig[i].Pivot);
        }
        return parts;
    }

    const float RoundSize = 0.006f; // half a round's size, for the chambered round's box

    static InspectParts Bind(InspectMotion motion, Transform gun, GearPartHolder? holder, Dictionary<string, List<Transform>> names, out InspectPartRig[] rig, out List<Transform>[] found)
    {
        var toGun = NQuaternion.Inverse(Q(gun.rotation));
        var gunScale = N(gun.lossyScale);
        var nodes = new List<Node>();
        found = new List<Transform>[motion.Parts.Length];
        rig = new InspectPartRig[motion.Parts.Length];
        Renderer? cartridge = null;
        for (var i = 0; i < motion.Parts.Length; i++)
        {
            rig[i].Under = -1;
            if (motion.Parts[i].Node == InspectPart.Round)
            {
                found[i] = new List<Transform>();
                cartridge = Cartridge(holder);
                if (cartridge == null || !names.TryGetValue(Eject, out var eject)) continue; // optional and missing: nothing to show
                // The copy hangs under the gun: its local pose is in the gun's space. It lies as the magazine's round does, at the
                // shell-eject point.
                var at = N(gun.InverseTransformPoint(eject[0].position));
                rig[i].Pivot = at;
                rig[i].Down = -NVector3.UnitY;
                nodes.Add(new Node { Part = i, Round = true, GunAt = at, GunTurn = toGun * Q(cartridge.transform.rotation), ParentScale = NVector3.One, ParentTurn = NQuaternion.Identity });
                continue;
            }
            found[i] = Find(motion.Parts[i], holder, names);
            if (found[i].Count == 0) continue; // an optional part this gun lacks
            rig[i].Pivot = N(gun.InverseTransformPoint(found[i][0].position));
            rig[i].Down = NVector3.Normalize(-N(gun.InverseTransformDirection(found[i][0].up)));
            foreach (var t in found[i])
            {
                var parent = t.parent;
                nodes.Add(new Node
                {
                    T = t,
                    Object = t.gameObject,
                    Part = i,
                    GunAt = N(gun.InverseTransformPoint(t.position)),
                    GunTurn = toGun * Q(t.rotation),
                    ParentAt = N(gun.InverseTransformPoint(parent.position)),
                    ParentTurn = toGun * Q(parent.rotation),
                    ParentScale = N(parent.lossyScale) / gunScale,
                    RestAt = t.localPosition,
                    RestTurn = t.localRotation,
                });
            }
        }
        // A part whose node hangs under another part's node goes with that part.
        for (var i = 0; i < found.Length; i++)
        {
            if (found[i].Count == 0) continue;
            for (var up = found[i][0].parent; up != null && up.Pointer != gun.Pointer && rig[i].Under < 0; up = up.parent)
                for (var j = 0; j < found.Length; j++)
                    if (j != i && found[j].Any(t => t.Pointer == up.Pointer)) { rig[i].Under = j; break; }
        }
        // A carrier's children that neither are nor hold another part of the motion stay with the gun (in the game's revolver model the
        // crane's node holds the barrel and frame too: only the cylinder swings out).
        var partNodes = found.Where(f => f != null).SelectMany(f => f).ToList();
        foreach (var node in nodes)
        {
            if (node.Round || !motion.Parts[node.Part].Carrier) continue;
            for (var k = 0; k < node.T.childCount; k++)
            {
                var child = node.T.GetChild(k);
                if (partNodes.Any(p => Holds(child, p))) continue;
                (node.Kept ??= new List<Kept>()).Add(new Kept
                {
                    T = child,
                    GunAt = N(gun.InverseTransformPoint(child.position)),
                    GunTurn = toGun * Q(child.rotation),
                    Scale = N(node.T.lossyScale) / gunScale,
                    RestAt = child.localPosition,
                    RestTurn = child.localRotation,
                });
            }
        }
        return new InspectParts(nodes.ToArray(), cartridge);

        // Whether `t` is `p` or one of its ancestors.
        static bool Holds(Transform t, Transform p)
        {
            for (var up = p; up != null; up = up.parent) if (up.Pointer == t.Pointer) return true;
            return false;
        }
    }

    // The look starts: the chamber's round is copied from the magazine's (hidden until a key shows it).
    public void Start(Transform gun)
    {
        if (_cartridge == null || _cartridge.WasCollected) return;
        foreach (var node in _nodes)
        {
            if (!node.Round) continue;
            _copy ??= UnityEngine.Object.Instantiate(_cartridge.gameObject, gun);
            _copy.name = "ForgeSkin inspect round";
            _copy.transform.localScale = U(N(_cartridge.transform.lossyScale) / N(gun.lossyScale));
            _copy.SetActive(false);
            node.T = _copy.transform;
            node.Object = _copy;
            node.Shown = false;
        }
    }

    // This frame's pose of every node (setters only: nothing of the game is read here).
    public void Pose(InspectLook look, float time, float weight)
    {
        foreach (var node in _nodes)
        {
            if (node.T is null) continue; // the chamber's round before the look started
            look.PartDelta(node.Part, time, weight, out var r, out var t, out var show);
            if (node.Round && show != node.Shown) node.Object.SetActive(node.Shown = show);
            var at = NVector3.Transform(node.GunAt, r) + t;
            var turn = r * node.GunTurn;
            var toParent = NQuaternion.Inverse(node.ParentTurn);
            node.T.localPosition = U(NVector3.Transform(at - node.ParentAt, toParent) / node.ParentScale);
            node.T.localRotation = U(NQuaternion.Normalize(toParent * turn));
            if (node.Kept == null) continue;
            var toNode = NQuaternion.Inverse(turn);
            foreach (var kept in node.Kept)
            {
                if (kept.T.WasCollected) continue;
                kept.T.localPosition = U(NVector3.Transform(kept.GunAt - at, toNode) / kept.Scale);
                kept.T.localRotation = U(NQuaternion.Normalize(toNode * kept.GunTurn));
            }
        }
    }

    // Every node back to its pose at rest; the chamber's round is removed.
    public void Restore()
    {
        foreach (var node in _nodes)
        {
            if (node.Round || node.T is null || node.T.WasCollected) continue;
            node.T.localPosition = node.RestAt;
            node.T.localRotation = node.RestTurn;
            foreach (var kept in node.Kept ?? [])
                if (!kept.T.WasCollected) (kept.T.localPosition, kept.T.localRotation) = (kept.RestAt, kept.RestTurn);
        }
        if (_copy != null && !_copy.WasCollected) UnityEngine.Object.Destroy(_copy);
        _copy = null;
    }

    static NVector3 N(Vector3 v) => new(v.x, v.y, v.z);

    static NQuaternion Q(Quaternion q) => new(q.x, q.y, q.z, q.w);

    static Vector3 U(NVector3 v) => new(v.X, v.Y, v.Z);

    static Quaternion U(NQuaternion q) => new(q.X, q.Y, q.Z, q.W);
}
