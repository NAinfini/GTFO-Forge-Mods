using ForgeCore;
using ForgeSkin.Format;
using Player;
using UnityEngine;

namespace ForgeSkin.Plugin;

// A skin's finger turns (HandShapes.LeftFingers / RightFingers) on the hands that hold its gun. The game poses the fingers; after it
// has, each finger joint is turned on from that pose by the skin's turn for it. A joint the game wrote this frame is turned from what
// it wrote; one it left alone (a body whose animation does not move the fingers) is turned from the pose it had before the first
// turn, so nothing builds up either way.
//  - First person (the local player's arms): after the first-person holder has placed itself (GameHooks.FirstPersonHolderLateUpdate).
//    While the gun reloads the turns fade out (the game moves the hands onto the magazine then) and fade back in after it. A weapon
//    inspect's hand shape (InspectFingers) is posed right after, in the same hook, so it lies over these turns while it plays.
//  - Third person (what others see, and the local player's own body): the same turns on the body of whoever holds the gun, in
//    ForgeCore's late update, while the gun is in that body's hand. Every machine works it out from the skin; nothing is sent.
static class FingerPose
{
    const float FadePerSecond = 6f;
    // HumanBodyBones: 24..38 the left hand's finger joints in HandShapes order, 39..53 the right's.
    const int LeftFirst = 24, RightFirst = 39, Count = HandShapes.Joints * 2;
    static readonly Quaternion Unturned = new(0f, 0f, 0f, 1f); // the identity, without the interop getter each frame

    // One body's finger joints, with the pose each was turned from and what was written to it.
    sealed class Fingers
    {
        IntPtr _animator;
        readonly Transform?[] _joints = new Transform?[Count];
        readonly Quaternion[] _from = new Quaternion[Count], _written = new Quaternion[Count];
        readonly bool[] _turned = new bool[Count];
        int _turnedCount; // how many joints are turned: none, and a hand with no turns has nothing to read
        public Transform? RightHand;
        public bool Turned => _turnedCount > 0;

        public bool On(Animator? animator)
        {
            if (animator == null) return false;
            if (animator.Pointer == _animator) return true; // a body taken earlier was human
            if (!animator.isHuman) return false;
            _animator = animator.Pointer;
            RightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            for (var i = 0; i < HandShapes.Joints; i++)
            {
                _joints[i] = animator.GetBoneTransform((HumanBodyBones)(LeftFirst + i));
                _joints[HandShapes.Joints + i] = animator.GetBoneTransform((HumanBodyBones)(RightFirst + i));
            }
            Array.Clear(_turned);
            _turnedCount = 0;
            return true;
        }

        public void Turn(Quaternion[]? left, Quaternion[]? right, float weight)
        {
            for (var i = 0; i < Count; i++)
            {
                var turns = i < HandShapes.Joints ? left : right;
                // A joint with no turn now that was not turned before has nothing to put back or turn: its pose is not read.
                var none = turns == null || weight <= 0f;
                var turn = none ? Unturned : turns![i % HandShapes.Joints];
                if (!_turned[i] && (none || turn.x == 0f && turn.y == 0f && turn.z == 0f)) continue;
                var joint = _joints[i];
                if (joint == null || joint.WasCollected) continue;
                var now = joint.localRotation;
                var untouched = _turned[i] && now == _written[i];
                if (none)
                {
                    // The last turned frame is put back once, then the joint is not read again.
                    if (untouched) joint.localRotation = _from[i];
                    _turned[i] = false;
                    _turnedCount--;
                    continue;
                }
                if (!untouched) _from[i] = now;
                _written[i] = _from[i] * (weight >= 1f ? turn : Quaternion.Slerp(Unturned, turn, weight));
                joint.localRotation = _written[i];
                if (!_turned[i]) { _turned[i] = true; _turnedCount++; }
            }
        }
    }

    sealed class Worn
    {
        public Quaternion[]? Left, Right;
        public ItemEquippable? Third; // the third-person item, null for the first-person one
        public Transform? ThirdTransform; // its transform, read once when the turns are set
        public string Label = "";
        public readonly Fingers Body = new();
        public bool Said;
    }

    static readonly Dictionary<IntPtr, Worn> Skins = new();
    static readonly Fingers Arms = new();
    static bool _hooked;
    static IntPtr _item;
    static float _weight;

    /// <summary>The finger turns of the skin on <paramref name="item"/>; rows that are not 15 x 3 numbers are refused and said once.</summary>
    public static void Set(ItemEquippable item, HandShapes hands, string label)
    {
        var worn = new Worn { Left = Read(hands.LeftFingers, label, "left"), Right = Read(hands.RightFingers, label, "right"), Third = item.m_isFirstPerson ? null : item, Label = label };
        if (worn.Left == null && worn.Right == null) { Clear(item.Pointer); return; }
        if (worn.Third != null) worn.ThirdTransform = item.transform;
        Skins[item.Pointer] = worn;
        Hook();
    }

    // The first-person and third-person passes, once, when a skin first turns fingers or a look first shapes them.
    public static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        GameHooks.FirstPersonHolderLateUpdate.After(static (holder, _) => { FirstPerson(holder); InspectFingers.PoseArms(); });
        Frame.OnLateUpdate(ForgeSkinPlugin.Guid, ThirdPerson);
    }

    public static void Clear(IntPtr item)
    {
        if (Skins.Remove(item, out var worn) && worn.Third != null) worn.Body.Turn(null, null, 0f);
    }

    static Quaternion[]? Read(float[][]? rows, string label, string side)
    {
        if (rows == null) return null;
        if (rows.Length != HandShapes.Joints || rows.Any(r => r == null || r.Length != 3))
        {
            ForgeSkinPlugin.Fail("skin-hand-fingers-skipped", $"{label}: the {side} hand's finger turns are not {HandShapes.Joints} rows of three numbers; the hand keeps its shape");
            return null;
        }
        return rows.All(r => r[0] == 0f && r[1] == 0f && r[2] == 0f) ? null : rows.Select(r => Quaternion.Euler(r[0], r[1], r[2])).ToArray();
    }

    static void FirstPerson(FirstPersonItemHolder holder)
    {
        if (Skins.Count == 0) return;
        var arms = holder.FPSArms;
        if (!Arms.On(arms != null ? arms.m_anim : null)) return;
        var item = holder.WieldedItem;
        if (item == null || !Skins.TryGetValue(item.Pointer, out var worn)) { _item = IntPtr.Zero; Arms.Turn(null, null, 0f); return; }
        var want = item.IsReloading ? 0f : 1f;
        if (item.Pointer != _item) { _item = item.Pointer; _weight = want; }
        _weight = Mathf.MoveTowards(_weight, want, Frame.DeltaTime * FadePerSecond);
        Arms.Turn(worn.Left, worn.Right, _weight);
    }

    static void ThirdPerson()
    {
        foreach (var worn in Skins.Values)
        {
            var item = worn.Third;
            if (item == null || item.WasCollected) continue;
            var owner = item.Owner;
            if (owner == null || !worn.Body.On(owner.AnimatorBody)) continue;
            // In the hand: the game hangs the gun a body holds under that body's right hand.
            var held = worn.Body.RightHand != null && worn.ThirdTransform != null && worn.ThirdTransform.IsChildOf(worn.Body.RightHand);
            if (!held && !worn.Body.Turned) continue; // put away and its turns already put back: nothing to do
            worn.Body.Turn(worn.Left, worn.Right, held ? 1f : 0f);
            if (!held || worn.Said) continue;
            worn.Said = true;
            ForgeSkinPlugin.Info($"{worn.Label}: finger turns on the body that holds it");
        }
    }
}
