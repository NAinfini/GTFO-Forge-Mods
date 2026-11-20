using System;
using System.Numerics;

namespace ForgeVisual;

/// <summary>How far each finger of a hand is bent, said one of two ways. Whole fingers (thumb to pinky): 0 is the canonical rest
/// (straight, a little curled), 1 a fist, the thumb's 1 across the palm, each finger's three joints in a fist's proportions. Or by
/// joint (<see cref="ByJoints"/>): each of the 15 joints (thumb to pinky, knuckle first) from 0 (as at rest) to 1 (as far as that
/// joint goes, <see cref="ClipFingers"/>). Either way the thumb's root may also turn out in front of the palm (<see cref="Spread"/>)
/// and about its own length to face the fingers (<see cref="Oppose"/>). Values outside 0 to 1 are refused: no joint bends back.</summary>
public readonly struct FingerBend
{
    private readonly float[]? _joints;

    public FingerBend(float thumb, float index, float middle, float ring, float pinky, float spread = 0f, float oppose = 0f)
    {
        Thumb = Check(thumb); Index = Check(index); Middle = Check(middle); Ring = Check(ring); Pinky = Check(pinky);
        Spread = Check(spread); Oppose = Check(oppose);
        _joints = null;
    }

    /// <summary>A shape by joint: <paramref name="joints"/> the 15 joints, thumb to pinky, knuckle first, each 0..1 of its range.</summary>
    public static FingerBend ByJoints(ReadOnlySpan<float> joints, float spread = 0f, float oppose = 0f)
    {
        if (joints.Length != 15) throw new ArgumentException("The 15 joints of a hand, thumb to pinky, knuckle first.", nameof(joints));
        var copy = new float[15];
        for (var j = 0; j < 15; j++) copy[j] = Check(joints[j]);
        return new FingerBend(copy, spread, oppose);
    }

    private FingerBend(float[] joints, float spread, float oppose)
    {
        _joints = joints;
        Spread = Check(spread); Oppose = Check(oppose);
        Thumb = Mean(joints, 0); Index = Mean(joints, 1); Middle = Mean(joints, 2); Ring = Mean(joints, 3); Pinky = Mean(joints, 4);
    }

    /// <summary>The whole fingers' bends; of a shape by joint, the mean of each finger's joints.</summary>
    public float Thumb { get; }
    public float Index { get; }
    public float Middle { get; }
    public float Ring { get; }
    public float Pinky { get; }
    /// <summary>How far the thumb's root turns out of the palm's plane toward its front (palmar abduction): 0 beside the fingers, as the
    /// canonical rest has it (and every hand that does not say), to 1 as far as a thumb goes.</summary>
    public float Spread { get; }
    /// <summary>How far the thumb turns about its own length so its pad faces the fingers (opposition): 0 as at rest, 1 as far as it goes.</summary>
    public float Oppose { get; }
    /// <summary>Whether the shape is given by joint.</summary>
    public bool ByJoint => _joints != null;

    /// <summary>The finger's bend, thumb (0) to pinky (4).</summary>
    public float this[int finger] => finger switch { 0 => Thumb, 1 => Index, 2 => Middle, 3 => Ring, 4 => Pinky, _ => throw new ArgumentOutOfRangeException(nameof(finger)) };

    /// <summary>Joint <paramref name="joint"/> (0 the knuckle) of finger <paramref name="finger"/>: the share of that joint's range it is bent.</summary>
    public float Joint(int finger, int joint) => _joints != null ? _joints[finger * 3 + joint] : ClipFingers.Whole(finger, joint) * this[finger] / ClipFingers.Range(finger, joint);

    public static FingerBend Lerp(in FingerBend a, in FingerBend b, float s)
    {
        if (!a.ByJoint && !b.ByJoint)
            return new(a.Thumb + (b.Thumb - a.Thumb) * s, a.Index + (b.Index - a.Index) * s, a.Middle + (b.Middle - a.Middle) * s,
                a.Ring + (b.Ring - a.Ring) * s, a.Pinky + (b.Pinky - a.Pinky) * s, a.Spread + (b.Spread - a.Spread) * s, a.Oppose + (b.Oppose - a.Oppose) * s);
        var joints = new float[15];
        for (var f = 0; f < 5; f++)
            for (var k = 0; k < 3; k++) joints[f * 3 + k] = Math.Clamp(a.Joint(f, k) + (b.Joint(f, k) - a.Joint(f, k)) * s, 0f, 1f);
        return new FingerBend(joints, a.Spread + (b.Spread - a.Spread) * s, a.Oppose + (b.Oppose - a.Oppose) * s);
    }

    private static float Mean(float[] joints, int finger) => (joints[finger * 3] + joints[finger * 3 + 1] + joints[finger * 3 + 2]) / 3f;

    private static float Check(float bend) => bend is >= 0f and <= 1f ? bend : throw new ArgumentOutOfRangeException(nameof(bend), "A finger bend is from 0 to 1.");
}

/// <summary>A hand's finger joints for a <see cref="JointClip"/> built in code (<see cref="JointClip.FromTurns(float, int, Quaternion[], Vector3[], string)"/>
/// with all <see cref="ClipRig.Count"/> joints), played with a <see cref="BodyPart.LeftFingers"/> or <see cref="BodyPart.RightFingers"/> mask so
/// only the fingers move. Each finger's three joints bend about their own side axis, the way a finger closes toward the palm (the thumb's across it).</summary>
public static class ClipFingers
{
    // Degrees each joint bends at a whole bend of 1, knuckle first: the four fingers into a fist, the thumb across the palm.
    private static readonly float[] Finger = { 80f, 95f, 60f }, Thumb = { 20f, 40f, 55f };
    // Each joint's range, knuckle first (a shape by joint's 1): a finger's knuckle 90 degrees, its middle joint 110, its last 80; the
    // thumb's root 45, its knuckle 60, its last joint 80. None bends back past straight.
    private static readonly float[] FingerRange = { 90f, 110f, 80f }, ThumbRange = { 45f, 60f, 80f };
    // The thumb's root: out of the palm's plane toward its front about its own -y (palmar abduction) as far as 60 degrees, then about its
    // own length (z, the way its bones run) toward the fingers as far as 40 (opposition), before it bends.
    private const float Spread = 60f * MathF.PI / 180f, Opposed = 40f * MathF.PI / 180f;

    internal static float Whole(int finger, int joint) => finger == 0 ? Thumb[joint] : Finger[joint];

    internal static float Range(int finger, int joint) => finger == 0 ? ThumbRange[joint] : FingerRange[joint];

    /// <summary>The 15 finger joints' turns (thumb to pinky, three each) of the <paramref name="right"/> or left hand into <paramref name="into"/>,
    /// in the shape <see cref="JointClip.Evaluate"/> gives them; <paramref name="hand"/> is that hand joint's turn in the same frame of the clip.</summary>
    public static void Turns(bool right, Quaternion hand, in FingerBend bend, Span<Quaternion> into)
    {
        if (into.Length < 15) throw new ArgumentException("Room for the 15 finger joints of a hand.", nameof(into));
        int wrist = right ? ClipRig.RightHand : ClipRig.LeftHand, first = right ? ClipRig.BodyCount + 15 : ClipRig.BodyCount;
        // In the canonical rig's own (mirrored) frame every finger joint closes about its local x, the thumb's toward the little finger.
        var held = Quaternion.Normalize(ClipRig.Mirror(hand) * ClipRig.RestWorld[wrist]);
        for (var finger = 0; finger < 5; finger++)
        {
            var world = held;
            for (var k = 0; k < 3; k++)
            {
                var j = first + finger * 3 + k;
                var degrees = bend.ByJoint ? Range(finger, k) * bend.Joint(finger, k) : Whole(finger, k) * bend[finger];
                world = world * ClipRig.Pose[j];
                if (finger == 0 && k == 0 && (bend.Spread > 0f || bend.Oppose > 0f))
                    world = world * Quaternion.CreateFromAxisAngle(-Vector3.UnitY, Spread * bend.Spread) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Opposed * bend.Oppose);
                world = Quaternion.Normalize(world * Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees * MathF.PI / 180f));
                into[finger * 3 + k] = ClipRig.Mirror(Quaternion.Normalize(world * ClipRig.RestWorldInverse[j]));
            }
        }
    }
}
