using System.Numerics;
using ForgeVisual;

/// <summary>The body mechanisms' pure logic: Unity-style rotations, matching a model's skeleton to the first-person body (alignment,
/// directions, measures, local scale), the pose copy's links and corrections, the held item's placement and the mesh cut.</summary>
internal static class BodyChecks
{
    private static bool Near(float a, float b, float e = 1e-4f) => MathF.Abs(a - b) <= e;
    private static bool Near(Vector3 a, Vector3 b, float e = 1e-4f) => (a - b).Length() <= e;
    private static bool Parallel(Vector3 a, Vector3 b) => Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)) > 1f - 1e-4f;
    private static bool Same(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) > 1f - 1e-5f;
    private static Vector3 R(Quaternion q, Vector3 v) => Vector3.Transform(v, q);

    internal static void Run(Action<bool, string> check)
    {
        Rotations(check);
        Wear(check);
        Pose(check);
        Held(check);
        Cut(check);
    }

    private static void Rotations(Action<bool, string> check)
    {
        check(Same(BodyMath.LookRotation(Vector3.UnitZ, Vector3.UnitY), Quaternion.Identity), "body: looking forward is no rotation");
        var right = BodyMath.LookRotation(Vector3.UnitX, Vector3.UnitY);
        check(Near(R(right, Vector3.UnitZ), Vector3.UnitX) && Near(R(right, Vector3.UnitX), -Vector3.UnitZ) && Near(R(right, Vector3.UnitY), Vector3.UnitY),
            "body: looking right turns forward to right and right to back (Unity's left-handed frame)");
        var tilted = BodyMath.LookRotation(new Vector3(1f, 2f, 3f), new Vector3(0.2f, 1f, 0f));
        check(Parallel(R(tilted, Vector3.UnitZ), new Vector3(1f, 2f, 3f)) && Vector3.Dot(R(tilted, Vector3.UnitY), new Vector3(0.2f, 1f, 0f)) > 0f, "body: look rotation keeps forward and leans up toward up");
        check(Same(BodyMath.LookRotation(Vector3.Zero, Vector3.UnitY), Quaternion.Identity), "body: no direction is no rotation");
        check(Parallel(R(BodyMath.LookRotation(Vector3.UnitY, Vector3.UnitY), Vector3.UnitZ), Vector3.UnitY), "body: looking along up still looks there");

        var from = new Vector3(1f, 0.5f, -0.2f);
        var to = new Vector3(-0.3f, 0.1f, 2f);
        check(Parallel(R(BodyMath.FromTo(from, to), from), to), "body: from-to turns one direction onto the other");
        check(Parallel(R(BodyMath.FromTo(Vector3.UnitX, -Vector3.UnitX), Vector3.UnitX), -Vector3.UnitX), "body: from-to turns a direction onto its opposite");
        check(Same(BodyMath.FromTo(from, from * 3f), Quaternion.Identity), "body: from-to of one direction is no rotation");

        check(Same(BodyMath.SkeletonFrame(new Vector3(-0.2f, 1.4f, 0f), new Vector3(0.2f, 1.4f, 0f), new Vector3(0f, 1f, 0f)), Quaternion.Identity),
            "body: an upright skeleton facing +Z has no frame rotation");
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        check(Same(BodyMath.SkeletonFrame(R(turned, new Vector3(-0.2f, 1.4f, 0f)), R(turned, new Vector3(0.2f, 1.4f, 0f)), R(turned, new Vector3(0f, 1f, 0f))), turned),
            "body: a turned skeleton's frame is its turn");
        check(Near(R(BodyMath.BoneFrame(-Vector3.UnitY), Vector3.UnitY), Vector3.UnitZ), "body: a bone pointing down has the body's front as up");
        check(Near(R(BodyMath.BoneFrame(Vector3.UnitZ), Vector3.UnitY), Vector3.UnitY), "body: a bone pointing forward has the body's top as up");
        check(Same(BodyMath.BoneFrame(Vector3.Zero), Quaternion.Identity), "body: a bone with no direction takes the body's frame");
    }

    // A game first-person skeleton (metres, body space) and a model of other proportions: larger, turned, legs relatively longer.
    private static readonly (BodyBone Bone, Vector3 At)[] GameSkeleton =
    {
        (BodyBone.Hips, new(0f, 1.0f, 0f)), (BodyBone.Spine, new(0f, 1.15f, 0f)), (BodyBone.Chest, new(0f, 1.3f, 0f)),
        (BodyBone.LeftUpperArm, new(-0.2f, 1.45f, 0f)), (BodyBone.RightUpperArm, new(0.2f, 1.45f, 0f)),
        (BodyBone.LeftUpperLeg, new(-0.1f, 0.95f, 0f)), (BodyBone.LeftLowerLeg, new(-0.1f, 0.5f, 0.02f)), (BodyBone.LeftFoot, new(-0.1f, 0.08f, 0f)),
        (BodyBone.RightUpperLeg, new(0.1f, 0.95f, 0f)), (BodyBone.RightLowerLeg, new(0.1f, 0.5f, 0.02f)), (BodyBone.RightFoot, new(0.1f, 0.08f, 0f)),
    };

    private static Vector3 ModelAt(BodyBone bone, Vector3 at)
    {
        // The model: 1.5 times the size, legs 1.8 times, turned 30 degrees and moved; the arms out to the side further.
        var legs = bone is BodyBone.LeftLowerLeg or BodyBone.LeftFoot or BodyBone.RightLowerLeg or BodyBone.RightFoot;
        var hip = bone is BodyBone.LeftLowerLeg or BodyBone.LeftFoot ? new Vector3(-0.1f, 0.95f, 0f) : new Vector3(0.1f, 0.95f, 0f);
        var local = legs ? hip * 1.5f + (at - hip) * 1.8f : at * 1.5f;
        // Its left knee bent forward: that shin points elsewhere at rest than the game's (the binding swings it onto the game's).
        if (bone == BodyBone.LeftLowerLeg) local += new Vector3(0f, 0f, 0.25f);
        return R(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6f), local) + new Vector3(3f, 0f, -1f);
    }

    private static void Wear(Action<bool, string> check)
    {
        var bones = new List<WearBone>();
        var model = new List<RestPose>();
        var game = new List<RestPose?>();
        var parents = new List<int>();
        var restTurn = Quaternion.CreateFromYawPitchRoll(0.3f, -0.4f, 0.9f);
        foreach (var (bone, at) in GameSkeleton)
        {
            var arm = bone is BodyBone.LeftUpperArm or BodyBone.RightUpperArm;
            var measure = bone switch
            {
                BodyBone.LeftUpperLeg or BodyBone.LeftLowerLeg or BodyBone.LeftFoot => BodyMeasure.LeftLeg,
                BodyBone.RightUpperLeg or BodyBone.RightLowerLeg or BodyBone.RightFoot => BodyMeasure.RightLeg,
                _ => BodyMeasure.ShoulderSpan,
            };
            bones.Add(new WearBone(bone.ToString(), bone, worn: !arm, scale: measure));
            model.Add(new RestPose(ModelAt(bone, at), Quaternion.Normalize(restTurn * Quaternion.CreateFromAxisAngle(Vector3.UnitX, at.Y)), 0.01f));
            game.Add(new RestPose(at, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, at.X)));
        }
        int I(BodyBone b) => WearSolver.IndexOf(bones, b);
        foreach (var (bone, _) in GameSkeleton)
            parents.Add(bone switch
            {
                BodyBone.Spine => I(BodyBone.Hips), BodyBone.Chest => I(BodyBone.Spine),
                BodyBone.LeftUpperArm or BodyBone.RightUpperArm => I(BodyBone.Chest),
                BodyBone.LeftUpperLeg or BodyBone.RightUpperLeg => I(BodyBone.Hips),
                BodyBone.LeftLowerLeg => I(BodyBone.LeftUpperLeg), BodyBone.LeftFoot => I(BodyBone.LeftLowerLeg),
                BodyBone.RightLowerLeg => I(BodyBone.RightUpperLeg), BodyBone.RightFoot => I(BodyBone.RightLowerLeg),
                _ => -1,
            });
        var parent = parents.ToArray();
        var child = WearSolver.Children(parent);
        check(child[I(BodyBone.Hips)] == -1 && child[I(BodyBone.Chest)] == -1, "wear: a bone with several listed children takes no child direction");
        check(child[I(BodyBone.Spine)] == I(BodyBone.Chest) && child[I(BodyBone.LeftUpperLeg)] == I(BodyBone.LeftLowerLeg) && child[I(BodyBone.LeftFoot)] == -1, "wear: one listed child is the child");

        var modelRest = model.ToArray();
        var gameRest = game.ToArray();
        check(WearSolver.Solve(bones, parent, modelRest, gameRest, out var offset, out var scale) == null, "wear: a full skeleton matches");
        check(Near(scale[I(BodyBone.Spine)], 1f / 1.5f) && Near(scale[I(BodyBone.Hips)], 1f / 1.5f), "wear: the body takes the shoulder span's scale");
        check(Near(scale[I(BodyBone.RightUpperLeg)], 1f / 1.8f, 1e-3f) && Near(scale[I(BodyBone.RightFoot)], 1f / 1.8f, 1e-3f) && scale[I(BodyBone.LeftUpperLeg)] < 1f / 1.8f - 1e-3f, "wear: each leg takes its own leg length's scale");
        check(Near(scale[I(BodyBone.LeftUpperArm)], 1f) && Same(offset[I(BodyBone.LeftUpperArm)], Quaternion.Identity), "wear: a line-up bone is not bound");

        // Bound on its game bone at the game's rest, every worn bone points where its game bone points (toward its child, or from its parent).
        var aligned = true;
        for (var i = 0; i < bones.Count; i++)
        {
            var c = child[i];
            if (!bones[i].Worn || (parent[i] < 0 && c < 0)) continue;
            var (modelAlong, gameAlong) = c >= 0
                ? (modelRest[c].Position - modelRest[i].Position, gameRest[c]!.Value.Position - gameRest[i]!.Value.Position)
                : (modelRest[i].Position - modelRest[parent[i]].Position, gameRest[i]!.Value.Position - gameRest[parent[i]]!.Value.Position);
            var local = R(Quaternion.Inverse(modelRest[i].Rotation), modelAlong);
            var world = R(gameRest[i]!.Value.Rotation * offset[i], local);
            aligned &= Parallel(world, gameAlong);
        }
        check(aligned, "wear: every worn bone lies along its game bone, the bent shin included");
        // A bone without a direction keeps the skeletons' common frame: hips (several children, no parent) turn by the alignment only.
        var align = BodyMath.SkeletonFrame(gameRest[I(BodyBone.LeftUpperArm)]!.Value.Position, gameRest[I(BodyBone.RightUpperArm)]!.Value.Position, gameRest[I(BodyBone.Hips)]!.Value.Position)
            * Quaternion.Inverse(BodyMath.SkeletonFrame(modelRest[I(BodyBone.LeftUpperArm)].Position, modelRest[I(BodyBone.RightUpperArm)].Position, modelRest[I(BodyBone.Hips)].Position));
        check(Same(gameRest[I(BodyBone.Hips)]!.Value.Rotation * offset[I(BodyBone.Hips)], align * modelRest[I(BodyBone.Hips)].Rotation), "wear: a bone with no direction turns by the skeletons' alignment");

        // A missing foot: that leg falls back to the shoulder span; the foot itself is not bound.
        var noFoot = (RestPose?[])gameRest.Clone();
        noFoot[I(BodyBone.LeftFoot)] = null;
        check(WearSolver.Solve(bones, parent, modelRest, noFoot, out var footOffset, out var footScale) == null
            && Near(footScale[I(BodyBone.LeftUpperLeg)], 1f / 1.5f) && Near(footScale[I(BodyBone.LeftFoot)], 1f) && Same(footOffset[I(BodyBone.LeftFoot)], Quaternion.Identity)
            && Near(footScale[I(BodyBone.RightUpperLeg)], 1f / 1.8f, 1e-3f), "wear: a limb missing a bone falls back to the shoulder span");
        var noHips = (RestPose?[])gameRest.Clone();
        noHips[I(BodyBone.Hips)] = null;
        check(WearSolver.Solve(bones, parent, modelRest, noHips, out _, out _) != null, "wear: the hips must be on the game's body");
        var noArm = bones.Where(b => b.Body != BodyBone.RightUpperArm).ToList();
        check(WearSolver.Solve(noArm, new int[noArm.Count], new RestPose[noArm.Count], new RestPose?[noArm.Count], out _, out _) != null, "wear: both upper arms must be listed");
        check(Near(WearSolver.Measure(bones, modelRest, gameRest, BodyMeasure.LeftArm, 0.5f), 0.5f), "wear: an arm without its forearm and hand measures the shoulder span");
        check(Near(WearSolver.LocalScale(0.5f, 2f, 0.01f, 4f), 0.0025f) && Near(WearSolver.LocalScale(0.5f, 1f, 1f, 0f), 0.5f), "wear: local scale over the game bone's scale, a zero scale ignored");
    }

    private static void Pose(Action<bool, string> check)
    {
        var links = new PoseLink[]
        {
            new("pelvis", BodyBone.Hips, toward: 1),
            new("spine", BodyBone.Spine, toward: 2),
            new("chest", BodyBone.UpperChest, toward: 3, bodyAlt: BodyBone.Chest, towardBody: BodyBone.Neck),
            new("head", BodyBone.Head),
            new("arm", BodyBone.LeftUpperArm, toward: 5),
            new("hand", BodyBone.LeftHand, from: 4),
        };
        check(PoseSolver.Validate(links) == null, "pose: a well-formed table is accepted");
        check(PoseSolver.Validate(Array.Empty<PoseLink>()) != null, "pose: no links refused");
        check(PoseSolver.Validate(new[] { new PoseLink("a", BodyBone.Hips, toward: 0) }) != null, "pose: a link toward itself refused");
        check(PoseSolver.Validate(new[] { new PoseLink("a", BodyBone.Hips, toward: 3) }) != null, "pose: a link toward a missing link refused");
        check(PoseSolver.Validate(new[] { new PoseLink("a", BodyBone.Hips, toward: 1, from: 1), new PoseLink("b", BodyBone.Spine) }) != null, "pose: toward and from together refused");
        check(PoseSolver.Validate(new[] { new PoseLink("a", BodyBone.Hips), new PoseLink("a", BodyBone.Spine) }) != null, "pose: a model bone listed twice refused");

        // The body at rest with its arm out to the side; the model at rest with its arm down, larger, its bones turned arbitrarily.
        var body = new[] { new Vector3(0f, 1f, 0f), new Vector3(0f, 1.2f, 0f), new Vector3(0f, 1.4f, 0f), new Vector3(0f, 1.65f, 0f), new Vector3(-0.2f, 1.45f, 0f), new Vector3(-0.75f, 1.45f, 0f) };
        var model = new[] { new Vector3(0f, 1.5f, 0f), new Vector3(0f, 1.8f, 0f), new Vector3(0f, 2.1f, 0f), new Vector3(0f, 2.4f, 0f), new Vector3(-0.35f, 2.15f, 0f), new Vector3(-0.38f, 1.3f, 0.05f) };
        var bodyRot = new Quaternion[links.Length];
        var modelRot = new Quaternion[links.Length];
        for (var i = 0; i < links.Length; i++)
        {
            bodyRot[i] = Quaternion.CreateFromYawPitchRoll(0.1f * i, 0.2f, -0.3f * i);
            modelRot[i] = Quaternion.CreateFromYawPitchRoll(-0.4f * i, 0.5f, 0.15f * i);
        }
        var toward = new Vector3?[links.Length];
        var unframe = PoseSolver.Unframe(links, model, modelRot);
        var correction = PoseSolver.Correct(links, body, bodyRot, toward, unframe);
        // Copied from the body at rest, each model bone points along its body bone: the arm goes out to the side like the body's.
        var along = true;
        for (var i = 0; i < links.Length; i++)
        {
            var link = links[i];
            if (link.Toward < 0 && link.From < 0) continue;
            var modelDir = link.Toward >= 0 ? model[link.Toward] - model[i] : model[i] - model[link.From];
            var bodyDir = link.Toward >= 0 ? body[link.Toward] - body[i] : body[i] - body[link.From];
            var copied = bodyRot[i] * correction[i];
            along &= Parallel(R(copied, R(Quaternion.Inverse(modelRot[i]), modelDir)), bodyDir);
        }
        check(along, "pose: copied from the body's rest, every model bone points along its body bone (arms out, not down)");
        var bent = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f);
        var bentArm = bent * bodyRot[4] * correction[4];
        check(Parallel(R(bentArm, R(Quaternion.Inverse(modelRot[4]), model[5] - model[4])), R(bent, body[5] - body[4])), "pose: a body bone's turn turns the model bone the same");
        // The same skeleton on both sides copies with no correction.
        var same = PoseSolver.Correct(links, model, modelRot, toward, unframe);
        check(same.All(q => Same(q, Quaternion.Identity)), "pose: identical skeletons need no correction");
        // The body's own toward bone replaces the toward link's joint.
        var neck = (Vector3?[])toward.Clone();
        neck[2] = new Vector3(0.3f, 1.6f, 0f);
        var withNeck = PoseSolver.Correct(links, body, bodyRot, neck, unframe);
        check(Parallel(R(bodyRot[2] * withNeck[2], R(Quaternion.Inverse(modelRot[2]), model[3] - model[2])), neck[2]!.Value - body[2]), "pose: the body's toward bone gives the direction");

        check(PoseSolver.Ratio(1.5f, 1f, out var ratio) == null && Near(ratio, 1.5f), "pose: size is root height over root height");
        check(PoseSolver.Ratio(1.5f, 0.05f, out _) != null && PoseSolver.Ratio(0f, 1f, out _) != null, "pose: a root on the floor refused");
        check(Near(PoseSolver.Root(new Vector3(0f, 1.5f, 0f), new Vector3(0.1f, 0.9f, 0.2f), new Vector3(0f, 1f, 0f), 1.5f), new Vector3(0.15f, 1.35f, 0.3f)), "pose: the root's sway and height scale with the size");
    }

    private static void Held(Action<bool, string> check)
    {
        check(Near(HeldPlacement.Position(Quaternion.Identity, new Vector3(1.2f, 1f, 0.3f), new Vector3(1f, 1f, 0f), new Vector3(5f, 2f, 0f), 1f), new Vector3(5.2f, 2f, 0.3f)),
            "held: the copy keeps the item's offset from the hand");
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        check(Near(HeldPlacement.Position(turn, new Vector3(0f, 0f, 1f), Vector3.Zero, new Vector3(0f, 2f, 0f), 2f), new Vector3(2f, 2f, 0f)),
            "held: the offset turns with the model and scales with its size");

        // A 0.5 + 0.4 m arm from the shoulder at the origin reaching a grip 0.6 m ahead, the body's elbow bent down.
        var grip = new Vector3(0f, 0f, 0.6f);
        var elbow = PoseSolver.Elbow(Vector3.Zero, grip, 0.5f, 0.4f, new Vector3(0f, -1f, 0.3f), out var hand);
        check(Near(hand, grip) && Near(elbow.Length(), 0.5f) && Near((hand - elbow).Length(), 0.4f) && elbow.Y < 0f && Near(elbow.X, 0f),
            "reach: the hand lands on the grip, both bones keep their length, the elbow bends in the body's plane");
        PoseSolver.Elbow(Vector3.Zero, new Vector3(0f, 0f, 2f), 0.5f, 0.4f, Vector3.UnitY, out var stretched);
        check(stretched.Z < 0.9f && stretched.Z > 0.89f && Near(stretched.X, 0f) && Near(stretched.Y, 0f), "reach: out of reach the arm stretches toward the grip");
        var straight = PoseSolver.Elbow(Vector3.Zero, grip, 0.5f, 0.4f, grip, out var straightHand);
        check(Near(straightHand, grip) && Near(straight.Length(), 0.5f) && Near((straightHand - straight).Length(), 0.4f), "reach: a straight body arm still bends to land the hand");
        check(PoseSolver.ValidateReach(new[] { new PoseReach(0, 1, 2, 3) }, 4) == null
              && PoseSolver.ValidateReach(new[] { new PoseReach(0, 1, 4, 3) }, 4) != null
              && PoseSolver.ValidateReach(new[] { new PoseReach(0, 1, 1, 3) }, 4) != null, "reach: links in range and three different ones");
    }

    private static void Cut(Action<bool, string> check)
    {
        var kept = new[] { true, false, true };
        var weights = new[]
        {
            new Weights4 { I0 = 0, W0 = 1f },
            new Weights4 { I0 = 0, W0 = 0.6f, I1 = 1, W1 = 0.4f },
            new Weights4 { I0 = 1, W0 = 0.7f, I1 = 2, W1 = 0.3f },
            new Weights4 { I0 = 2, W0 = 0.25f, I1 = 0, W1 = 0.25f, I2 = 1, W2 = 0.5f },
            new Weights4 { I0 = 9, W0 = 1f },
        };
        var keep = MeshCut.KeepVertices(weights, kept);
        check(keep.SequenceEqual(new[] { true, true, false, true, false }), "cut: a vertex stays when at least half its weight is on kept bones");
        check(Near(weights[1].W0, 1f) && Near(weights[1].W1, 0f) && Near(weights[3].W0 + weights[3].W1, 1f) && Near(weights[3].W2, 0f), "cut: kept weights renormalised over kept bones");
        check(Near(weights[2].W0, 0.7f), "cut: a dropped vertex is left as it was");
        check(MeshCut.KeepTriangles(new[] { 0, 1, 3, 0, 1, 2, 3, 1, 0, 4, 0, 1 }, keep).SequenceEqual(new[] { 0, 1, 3, 3, 1, 0 }), "cut: only triangles with three kept corners stay");
    }
}
