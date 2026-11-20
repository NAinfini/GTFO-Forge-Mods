using System.Numerics;
using ForgeVisual;

namespace ForgeSkin.Backend;

// The whole-gun model (PLAN §4.2): where an imported model that replaces the whole gun goes and where the gun's four hold points
// go, by ForgeCore's one placement of a gun model on a game gun (GunHold.Solve, Data/gun-hold.json). Everything is in the gear's
// frame (Unity space: +Z the bore, metres). The model is made the glTF way, its front (muzzle end) along glTF +Z, which imported
// into Unity space is -Z: it is turned half round Y (`rotation`, Unity Euler degrees) so its muzzle points down the bore.
//
// The model's points are its nodes named as ForgeCore GunNodes names a gun model's sockets (RightHand, LeftHand, muzzle,
// SightLook; any case). A hand without its node grips where the replaced gun's palm of that hand touches, at the same place in
// the model's bounds as in the gun's. The model is shown at
// `scale` (the attachment's; none yet: gun-hold.json shownScale). The right and left hand nodes go to the solved wrists; the muzzle
// and aim point go to the model's own when it has them, else stay the gun's.
public static class GunSwap
{
    // ForgeCore GunNodes socket names (Visual/Native/Models.cs), keyed by the editor's hold point names, and the node a flashlight
    // model names where its light shines from (the editor puts the flashlight's light point there).
    static readonly (string Point, string Socket)[] Sockets = [("rightHand", "RightHand"), ("leftHand", "LeftHand"), ("muzzle", "muzzle"), ("sight", "SightLook"), ("light", "light")];

    static GunHoldRules? _rules;
    public static GunHoldRules Rules => _rules ??= ReadRules();

    static GunHoldRules ReadRules()
    {
        using var stream = typeof(GunSwap).Assembly.GetManifestResourceStream("ForgeVisual.Data.gun-hold.json")
            ?? throw new InvalidDataException("the backend is built without ForgeCore's gun-hold.json");
        using var reader = new StreamReader(stream);
        return GunHoldRules.Parse(reader.ReadToEnd());
    }

    // The model's socket nodes found in `glb`, by hold point name, as [x, y, z] in the model's Unity space.
    public static Dictionary<string, float[]> SocketsOf(string glb)
    {
        var nodes = GlbReader.NodePositions(glb);
        var found = new Dictionary<string, float[]>();
        foreach (var (point, socket) in Sockets)
            if (nodes.FirstOrDefault(n => string.Equals(n.Key, socket, StringComparison.OrdinalIgnoreCase)) is { Key: not null } n)
                found[point] = [n.Value.X, n.Value.Y, n.Value.Z];
        return found;
    }

    public sealed record Hand(float[] At, float[] Rotation);
    public sealed record Box(float[] Min, float[] Max);
    public sealed record Native(Hand RightHand, Hand LeftHand, float[]? Muzzle, float[]? Sight, Box Bounds);
    public sealed record Model(Dictionary<string, float[]>? Sockets, Box Bounds);

    public static object Seat(Native native, Model model, float? scale)
    {
        var rules = Rules;
        var k = scale ?? rules.ShownScale;
        if (!(k > 0) || !float.IsFinite(k)) throw new BackendException("bad-request", "scale must be a positive number");
        // Solve shows the model at ShownScale of the size it is given: given at k / ShownScale of its own, it shows at k.
        var f = k / rules.ShownScale;
        var sockets = model.Sockets ?? new();
        // The model's front is glTF's (+Z there, -Z once imported into Unity space): turned half round Y, its muzzle is down the bore.
        Vector3 Turned(Vector3 p) => new Vector3(-p.X, p.Y, -p.Z) * f;
        Vector3? Socket(string point) => sockets.TryGetValue(point, out var p) ? Turned(V(p)) : null;

        var right = V(native.RightHand.At);
        var rightTurn = Q(native.RightHand.Rotation);
        var left = V(native.LeftHand.At);
        var leftTurn = Q(native.LeftHand.Rotation);
        var (gunMin, gunMax) = (V(native.Bounds.Min), V(native.Bounds.Max));
        var (cornerA, cornerB) = (Turned(V(model.Bounds.Min)), Turned(V(model.Bounds.Max)));
        var (modelMin, modelMax) = (Vector3.Min(cornerA, cornerB), Vector3.Max(cornerA, cornerB));
        var length = modelMax.Z - modelMin.Z;
        if (!(length > 0)) throw new BackendException("bad-request", "the model has no length along the bore");

        // A hand the model has no node for: where that palm touches the gun, at the same place in the model's bounds (so it stays on
        // the model whatever its length).
        Vector3 Like(Vector3 wrist, Vector3 palm, Quaternion turn) =>
            modelMin + Along(wrist - GunHold.WristFromTouch(palm, turn) - gunMin, gunMax - gunMin) * (modelMax - modelMin);
        var modelRight = Socket("rightHand") ?? Like(right, rules.RightPalm, rightTurn);
        var modelLeft = Socket("leftHand") ?? Like(left, rules.LeftPalm, leftTurn);
        var modelMuzzle = Socket("muzzle");
        var modelSight = Socket("sight");
        var placed = GunHold.Solve(new GunHoldInput
        {
            NativeRight = right,
            NativeRightRotation = rightTurn,
            NativeLeft = left,
            NativeLeftRotation = leftTurn,
            NativeMuzzle = native.Muzzle is { } m ? V(m) : null,
            NativeSight = native.Sight is { } st ? V(st) : null,
            ModelRight = new GunGrip(modelRight),
            ModelLeft = new GunGrip(modelLeft),
            ModelMuzzle = modelMuzzle,
            ModelSight = modelSight,
            ModelLength = length,
        }, rules);
        float[]? At(Vector3? p) => p is { } v ? A(placed.Origin + v * rules.ShownScale) : null;
        return new
        {
            scale = k,
            origin = A(placed.Origin),
            rotation = new[] { 0f, 180f, 0f },
            rightHand = A(placed.Right.Position),
            leftHand = A(placed.Left.Position),
            muzzle = At(modelMuzzle),
            sight = At(modelSight),
            shownLength = placed.ShownLength,
            lengthMin = rules.LengthMin,
            lengthMax = rules.LengthMax,
            sockets = sockets.Keys.ToArray(),
        };
    }

    // Each axis of `offset` as a share of `size` (the middle where the gun has no size along it).
    static Vector3 Along(Vector3 offset, Vector3 size) => new(Share(offset.X, size.X), Share(offset.Y, size.Y), Share(offset.Z, size.Z));
    static float Share(float d, float size) => size > 1e-6f ? d / size : 0.5f;

    static Vector3 V(float[] a) => a.Length == 3 && a.All(float.IsFinite) ? new(a[0], a[1], a[2]) : throw new BackendException("bad-request", "a point is three finite numbers");
    static Quaternion Q(float[] a) => a.Length == 4 && a.All(float.IsFinite) ? Quaternion.Normalize(new(a[0], a[1], a[2], a[3])) : throw new BackendException("bad-request", "a rotation is four finite numbers");
    static float[] A(Vector3 v) => [v.X, v.Y, v.Z];
}
