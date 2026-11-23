using System.Diagnostics;
using BepInEx;
using ForgeCore;
using ForgeSkin.Format;
using ForgeUI;
using ForgeVisual;
using Gear;
using Player;
using UnityEngine;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace ForgeSkin.Plugin;

// Weapon inspect (PLAN §4.14). The inspect key plays one of the held gun's authored motions in the local player's first-person hands
// (Core/InspectMotion: the motion table, inspect-motions.json embedded here, entries replaced by BepInEx/config/ForgeSkin.inspect.json).
// A gun the table names by its offline gear id plays one of its own motions at random, never the one it played last; any other gun one
// picked from its measured length, said once in the log. A motion whose parts this gun lacks or does not show (InspectParts: a skin hid
// the node, a mounted model replaced the gun) is left out for this gun, said once. When the look starts, the gun's drawn bounds (in its own space), the item camera, both hands' IK targets and both
// arms' bones are measured and the motion is solved for them (InspectLook: the grip hand's place and elbow swivel per key, the wrist
// kept within its limits, the off hand's places on the gun taken from the bounds, every frame measured against the frame rules); a rule a
// look breaks in these hands is a finding for the developer log (warn, once per gun and motion), never a reason not to play it: which
// motions are good is the owner's call. Each frame, before the first-person holder's
// LateUpdate puts the arms' IK targets on the grips (GameHooks.FirstPersonHolderLateUpdate), the gun is put where the motion has it
// between its keys (the game's pose for a rest key), so the grip hand follows it; after the holder, the off hand's IK target is moved off
// its grip where the motion says. The holder's Update writes the gun's pose and the LateUpdate the targets again every frame, so nothing
// builds up. Every player's motion goes to the others as a per-player state on ForgeSkin's Net channel (the motion's number); each body
// plays that motion's named clip "ForgeSkin/inspect/<id>" on its right hand (third person shows the wrist turning; its off hand stays on
// the gun), which ForgeCore's joint player reports to Recording, so a replay shows it. No input is held back; what the player does (fire,
// aim, reload, melee, use, sprint, a hit, a menu) puts the gun back within FadeOut. Nothing here reads a skin; no gun is a branch here.
// A melee weapon at rest (its swing state idle) is inspected the same way: its length is its own Y where a gun's is its Z, so its rig
// is turned (InspectRig.LengthAsY) and the look is solved and posed with the length as Z. Anything else in the hands (a tool, a pack,
// another mod's item) and a gun or melee weapon that can play none of its own motions plays the table's default (InspectTable.Default:
// turned to one side and the other from the hold the game has it in), by what it is, with no list of items; a table without one
// leaves the key without a look, said in the log.
static class Inspect
{
    const string ClipPrefix = ForgeSkinPlugin.Guid + "/inspect/";
    const float HandYaw = 0.5f;                      // the share of the motion's turn the third-person wrist bends
    const float BodyFadeIn = 0.2f, BodyFadeOut = 0.3f;

    struct State { public uint Motion; }

    // A player's body playing a motion: its joints, the clip's track and (a teammate's) the motion's sounds on their gun, the next one due.
    sealed class Body { public IntPtr Agent; public JointPlayer? Joints; public JointTrack Track; public InspectSounds.Sound[] Sounds = []; public int Next; public float Start; public GameObject? Gun; }

    static KeyAction? _key;
    static PlayerStateSlot<State>? _slot;
    static readonly InspectTable Table = new();
    static readonly Dictionary<uint, InspectMotion> Motions = new();      // every motion by its number, the table's and the packs'
    static readonly Dictionary<(uint, float), JointClip> Clips = new();   // third-person clips by motion number and how slow they play
    static readonly Dictionary<uint, uint> Last = new();     // per gun: the number of the motion it played last
    static readonly System.Random Chance = new();
    static InspectParts? _parts;                              // the parts the playing look moves
    static readonly HashSet<uint> Told = new();               // gun ids whose fallback, and motion numbers missing here, were logged
    static readonly HashSet<ulong> Found = new();             // gun id << 32 | motion number: rule findings already logged
    static readonly HashSet<ulong> Cannot = new();            // gun id << 32 | motion number: solved unplayable here (InspectLook.Unplayable)
    static readonly JointMask Hand = new JointMask().Add(BodyPart.RightHand);
    static readonly Dictionary<ulong, Body> Bodies = new();

    // The look playing on this machine's first-person gun; null: none.
    static InspectLook? _look;
    static Transform? _item, _view, _offTarget, _gripTarget;
    static Camera? _camera;
    static IntPtr _gun, _damage;
    static float _began, _stopped = -1f, _restView;
    static InspectSounds.Sound[] _sounds = [];   // the playing look's sounds, and the next one due
    static int _nextSound;
    static GameObject? _gunObject;
    // This frame's: time into the motion, its weight, the view's rotation and the gun's pose in view space (set before the holder's
    // LateUpdate, read after it).
    static float _time, _weight;
    static NQuaternion _viewTurn, _gunTurn;
    static NVector3 _gunAt;
    static NQuaternion _itemTurn = NQuaternion.Identity;   // the look's space to the item's own (a melee weapon's length is its Y)
    // Each arm's IK while a look plays: bent toward a goal at the solve's elbow (swung out or tucked in), with the game's own bend
    // setting kept to put back.
    sealed class Bend
    {
        public RootMotion.FinalIK.IKSolverLimb? Solver;
        public RootMotion.FinalIK.IKSolverLimb.BendModifier Was;
        public Transform? GoalWas, Elbow;
        public float WeightWas;
        // The hand's bone and its size as the game had it (a key's scale draws the hand smaller, InspectLook.OffScale).
        public Transform? Hand;
        public Vector3 HandScale = Vector3.one;
    }
    static readonly Bend _gripBend = new(), _offBend = new();

    public static void Register(NetChannel? channel)
    {
        try
        {
            using (var s = typeof(Inspect).Assembly.GetManifestResourceStream("ForgeSkin.inspect-motions.json")!)
            using (var r = new StreamReader(s)) Table.Add(r.ReadToEnd());
            var own = Path.Combine(Paths.ConfigPath, "ForgeSkin.inspect.json");
            if (File.Exists(own))
                try { Table.Add(File.ReadAllText(own)); ForgeSkinPlugin.Info($"inspect motions from {own}"); }
                catch (Exception e) when (e is FormatException or IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
                { ForgeSkinPlugin.Fail("skin-inspect-failed", $"{own} is not used: {e.Message}"); }
            foreach (var motion in Table.Motions) Know(motion);
            _key = UIKit.Mod(ForgeSkinPlugin.Guid).AddKeyAction("Inspect", "keys.inspect", "keys.group", "I", KeyMode.Press, "on-foot");
            GameHooks.FirstPersonHolderLateUpdate.Before(Pose);
            GameHooks.FirstPersonHolderLateUpdate.After(static (_, _) => PoseOffHand());
            GameHooks.PlayerReceived.After(static (call, _) => { if (_item != null && call.Receiver.Pointer == _damage) Stop(); });
            GameHooks.ItemUnwielded.After(static (item, _) => Unwielded(item.Pointer));
            GameHooks.BulletWeaponUnwielded.After(static (item, _) => Unwielded(item.Pointer));
            GameEvents.LevelEnded += Reset;
            GameEvents.CheckpointReloaded += Reset;
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-inspect-failed", $"weapon inspect is off: {e.Message}", e); _key = null; return; }
        if (channel == null) return; // no channel (logged as skin-look-failed): the motion is seen in first person only
        _slot = channel.PlayerState<State>("inspect");
        _slot.Changed += Seen;
    }

    // An item left the hands (the game's unwield, and UnwieldHook for the kinds with their own): its look is cut.
    internal static void Unwielded(IntPtr item) { if (item == _gun) Stop(cut: true); }

    static bool Melee(ItemEquippable item) => item.TryCast<MeleeWeaponFirstPerson>() != null || item.TryCast<MeleeWeaponThirdPerson>() != null;

    // Neither a gun nor a melee weapon: whatever else a player holds plays the table's default look.
    static bool Plain(ItemEquippable item) => !Melee(item) && item.TryCast<BulletWeapon>() == null;

    // A pack read (Skins.Apply, main thread): its own motions get their third-person clips, so a teammate or a replay that names one
    // finds it; a kind it adds to that the table does not have is said once, since no gun would ever play it.
    public static void Learn(Skin skin)
    {
        if (skin.Inspect is not { } pack) return;
        foreach (var motion in pack.Motions) Know(motion);
        if (pack.Kinds.Except(Table.Kinds).ToList() is { Count: > 0 } unknown)
            ForgeSkinPlugin.Note($"inspect: {Path.GetFileName(skin.File)} adds motions to kinds the table does not have ({string.Join(", ", unknown)}); no gun plays them");
    }

    // A motion is known by its number, and its third-person clip is made at every pace the table's kinds play at, so a teammate or a
    // replay that names one finds it.
    static void Know(InspectMotion motion)
    {
        Motions[motion.Number] = motion;
        foreach (var slow in Table.Slows.Append(1f).Distinct()) Clip(motion, slow);
    }

    // A motion's third-person clip at a pace (`slow` times slower, as the kind of gun it plays on has it; named "<id>@<slow>" when not 1):
    // the right wrist rolls about the forearm with the motion's roll (within the wrist's twist) and bends with a share of its turn. Only the
    // hand is masked, so the rest of the arm keeps the game's pose and the off hand stays on the gun. Third person shows the authored
    // intent only: the follow, the hand on the gun's parts, a toss and a twirl are solved for first-person arms and are not sent.
    static JointClip Clip(InspectMotion motion, float slow)
    {
        if (Clips.TryGetValue((motion.Number, slow), out var known)) return known;
        const float rate = 1f / 30f;
        const int joints = ClipRig.BodyCount;
        int frames = (int)MathF.Ceiling(motion.Seconds * slow / rate) + 1, hand = JointMask.Joints(BodyPart.RightHand)[0];
        var turns = new NQuaternion[frames * joints];
        Array.Fill(turns, NQuaternion.Identity);
        var forearm = ClipRig.RestDirection(JointMask.Joints(BodyPart.RightForearm)[0], hand);
        for (var f = 0; f < frames; f++)
        {
            motion.Intent(f * rate / slow, out var turn, out var roll);
            roll = -Math.Clamp(roll, -InspectArm.Twist, InspectArm.Twist); // the same turn the first-person solve gives a roll
            turns[f * joints + hand] = NQuaternion.CreateFromAxisAngle(forearm, roll * MathF.PI / 180f) * NQuaternion.CreateFromAxisAngle(NVector3.UnitY, turn * HandYaw * MathF.PI / 180f);
        }
        var name = ClipPrefix + motion.Id + (slow == 1f ? "" : "@" + slow.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        return Clips[(motion.Number, slow)] = JointClip.FromTurns(rate, joints, turns, new NVector3[frames], name);
    }

    // Main-thread tick (ForgeSkinPlugin.Update): the key, and what puts a motion back.
    public static void Tick()
    {
        if (_key == null) return;
        // Teammates' sounds, each once as its time passes (a body that joined late starts past the ones already gone).
        foreach (var body in Bodies.Values)
            for (var elapsed = Frame.GameTime - body.Start; body.Next < body.Sounds.Length && body.Sounds[body.Next].Time <= elapsed; body.Next++)
                InspectSounds.Play(body.Sounds[body.Next], body.Gun);
        if (_press != null) { if (_press.Solve.IsCompleted) Begin(); return; }
        if (_item == null)
        {
            // A press the gun was not ready for (the game still busy with it, a move not yet over) waits PressWait seconds for it; one
            // that never found it ready is logged with what stood in the way, so the key is never swallowed unseen.
            if (_key.Pressed) { _wanted = Frame.GameTime; _why = null; }
            if (_wanted < 0f) { Prepare(); return; }
            Begin();
            if (_press != null) _wanted = -1f;
            else if (Frame.GameTime - _wanted > PressWait) { ForgeSkinPlugin.Note($"inspect: the key was pressed and no look started: {_why ?? "no reason found (the look failed to measure)"}; {Hands()}"); _wanted = -1f; }
            return;
        }
        // Fading back: the holder's step clears it; a holder that stopped stepping (the view left the first person) leaves it here.
        if (_stopped >= 0f) { if (Frame.GameTime - _stopped > InspectMotion.FadeOut) Clear(); return; }
        var local = Players.Local;
        if (local == null || local.WasCollected || (_hold < 0f && Frame.GameTime - _began >= _look!.Seconds)) { Stop(cut: true); return; }
        // Any item plays a look, and only some kinds' unwield is heard (Unwielded): a look whose item is no longer the one the holder's
        // last step had in the hands is cut (ForgeCore GunMount.HeldItem, which the mounts read every frame: no second read here).
        if (GunMount.HeldItem != _gun) { Stop(cut: true); return; }
        if (Players.Motion(local) is not (PlayerMotion.Stand or PlayerMotion.Crouch or PlayerMotion.StandStill) || UIKit.GameScreenOpen
            || InputLease.Down(InputAction.Fire) || InputLease.Down(InputAction.Aim) || InputLease.Down(InputAction.Reload)
            || InputLease.Down(InputAction.Melee) || InputLease.Down(InputAction.Use)) Stop();
    }

    // A press of the key: a gun in the hands at rest (not aiming, sprinting, reloading or in the game's equip sequence) plays its next
    // motion, solved for this gun, camera and pair of arms. A press during a motion is ignored, so a motion never jumps.
    static void Begin()
    {
        if (_press != null) { Start(); return; } // the press's solve is done
        var pressed = Stopwatch.GetTimestamp();
        if (Setup() is not { } setup) return;
        var (item, gear, pool, rig) = (setup.Item, setup.Gear, setup.Pool, setup.Rig);
        Last.TryGetValue(gear, out var last);
        // A motion whose hand must clasp a part (a charging handle worked between the thumb and the fingers) plays only its look solved
        // ahead on this gun: the plain look would work the part with the hand off it. Until that is solved it is left out of the pick.
        static bool Clasps(InspectMotion m) => m.Hand.Any(h => h.Clasp);
        var unclasped = pool.Where(m => !Clasps(m)).ToList();
        var ready = pool.Where(m => !Clasps(m) || Ahead.TryGetValue((item.Pointer, m.Number), out var r) && r.Solve.IsCompleted).ToList();
        if (ready.Count < pool.Count && Told.Add(Fnv.Hash32("unsolved/" + item.Pointer)))
            ForgeSkinPlugin.Note($"inspect {item.name} (gear {gear}): {string.Join(", ", pool.Except(ready).Select(m => m.Id))} wait for their clasped looks to be solved at rest; another motion plays");
        var motion = InspectTable.Pick(ready.Count > 0 ? ready : pool, last, Chance);
        if (_holdMotion != null)
        {
            motion = Motions.Values.First(m => m.Id == _holdMotion);
            if (InspectParts.Missing(motion, setup.Parts, setup.Names) is { } lacks) { _why = $"{motion.Id} cannot play on {item.name}: {lacks}"; _holdMotion = null; return; }
            _holdMotion = null;
        }
        Last[gear] = motion.Number;
        var (moved, solvedRig, itemTurn) = Bind(setup, motion);
        var measured = (Stopwatch.GetTimestamp() - pressed) * 1000.0 / Stopwatch.Frequency;
        // The look solved ahead for this gun at rest (Prepare), where the gun, the camera, the arms and the hold are as they were then
        // (Unlike); else the plain look (no clasp: InspectLook.Plain, as quick as the looks before clasps) is solved off the main thread on
        // what was just measured and starts on the tick it is done, and the clasped look is solved again at rest for the next press.
        var unlike = Ahead.TryGetValue((item.Pointer, motion.Number), out var solved) ? Unlike(solved.Rig, solvedRig, solved.Meshes, setup.Meshes) : null;
        var ahead = solved != null && unlike == null;
        if (unlike != null) Forget(item.Pointer, $"{unlike} changed since its looks were solved");
        var done = ahead && solved!.Solve.IsCompleted;
        // (A clasp motion with no look solved ahead that holds, as Hold may ask for: another motion plays, or none where it has no other.)
        if (!done && Clasps(motion))
        {
            moved.Restore();
            if (unclasped.Count == 0)
            {
                _why = $"{motion.Id} on {item.name} waits for its clasped look to be solved at rest";
                ForgeSkinPlugin.Note($"inspect {item.name} (gear {gear}): {_why}; no look is played this time");
                return;
            }
            ForgeSkinPlugin.Note($"inspect {item.name} (gear {gear}), {motion.Id}: its clasped look is to be solved again; another motion plays");
            motion = InspectTable.Pick(unclasped, last, Chance);
            Last[gear] = motion.Number;
            (moved, solvedRig, itemTurn) = Bind(setup, motion);
            unlike = Ahead.TryGetValue((item.Pointer, motion.Number), out solved) ? Unlike(solved.Rig, solvedRig, solved.Meshes, setup.Meshes) : null;
            ahead = solved != null && unlike == null;
            done = ahead && solved!.Solve.IsCompleted;
        }
        if (!done) ForgeSkinPlugin.Note($"inspect {item.name} (gear {gear}), {motion.Id}: its clasped look is {(ahead ? "still being solved" : "not solved yet")}; the plain look plays this time");
        _press = new Press(done ? solved!.Solve : Solving(motion, solvedRig, plain: true), item, setup.Camera, setup.Arms, rig.TanHalf, gear, setup.Fallback, setup.Extents, measured,
            moved, setup.Kind, itemTurn, pressed, done);
    }

    // What a look of the item in the hands needs, measured now (null: it cannot play now, `_why` says why): the gun, the camera, the
    // arms, its kind and feel and the motions it can play.
    sealed record Setting(ItemEquippable Item, Camera Camera, PlayerFPSBody Arms, Transform GunNode, InspectRig Rig, uint Gear, bool Fallback, NVector3 Extents,
        string Kind, List<InspectMotion> Pool, GearPartHolder? Parts, Dictionary<string, List<Transform>> Names, List<(Renderer Mesh, NVector3 Lo, NVector3 Hi)> Meshes, bool Melee);

    static Setting? Setup()
    {
        // The first read hooks the holder's step in ForgeCore if no gun model has: the look starts on a later tick, when the holder has
        // stepped with this item, and Tick asks every frame.
        _ = GunMount.HeldItem;
        var local = Players.Local;
        var holder = local != null && !local.WasCollected ? local.FPItemHolder : null;
        var item = holder != null ? holder.WieldedItem : null;
        if (local == null || local.WasCollected || holder == null || (_why = NotReady(local, holder, item)) != null) return null;
        var camera = holder!.m_ItemLayerCamera;
        var arms = holder.FPSArms;
        if (camera == null || arms == null) { _why = "the item camera or the first-person arms are missing"; return null; }
        var gunNode = item!.transform;
        var view = camera.transform;
        if (!Measure(gunNode, out var centre, out var extents, out var meshes))
        {
            ForgeSkinPlugin.Fail("skin-inspect-failed", $"weapon inspect found no shown mesh under {item.name}; no look is played");
            return null;
        }
        var right = Arm(arms.m_rightHandIK, arms.m_rightHandIKTarget, view);
        var left = Arm(arms.m_leftHandIK, arms.m_leftHandIKTarget, view);
        if (right == null || left == null)
        {
            ForgeSkinPlugin.Fail("skin-inspect-failed", "weapon inspect found no first-person arm bones or hand targets; no look is played");
            return null;
        }
        var viewTurn = Q(view.rotation);
        var toView = NQuaternion.Inverse(viewTurn);
        var parts = item.GearPartHolder;
        var rig = new InspectRig
        {
            TanHalf = MathF.Tan(camera.fieldOfView * 0.5f * MathF.PI / 180f),
            Aspect = camera.aspect,
            Near = camera.nearClipPlane,
            Centre = centre,
            Extents = extents,
            GunAt = N(view.InverseTransformPoint(gunNode.position)),
            Gun = toView * Q(gunNode.rotation),
            GripAt = right.Value.At,
            Grip = right.Value.Turn,
            GripArm = right.Value.Arm,
            OffAt = left.Value.At,
            Off = left.Value.Turn,
            OffArm = left.Value.Arm,
            Up = N(view.InverseTransformDirection(Vec.Up)),
        };
        var gear = parts != null && parts.GearIDRange is { } range ? SkinnedGear.GearIdOf(range) : 0u;
        var melee = Melee(item);
        var plain = Plain(item);
        var length = (melee ? extents.Y : extents.Z) * 2f;
        rig.Feel = plain ? Table.DefaultFeel : Table.Feel(gear, length, melee);
        var kind = plain ? Table.DefaultKind : Table.Kind(gear, length, melee);
        // The skins this gun shows add their packs' motions to its pool.
        var fallback = false;
        var motions = plain ? Table.Default : Table.For(gear, length, out fallback, SkinnedGear.ShownOn(item.Pointer).Select(s => s.Inspect).OfType<InspectTable>(), melee);
        if (plain && motions.Count > 0 && Told.Add(Fnv.Hash32("plain/" + item.name))) ForgeSkinPlugin.Note($"inspect: {item.name} (gear {gear}) is neither a gun nor a melee weapon; it plays the default look ({string.Join(", ", motions.Select(m => m.Id))})");
        if (motions.Count == 0) { _why = $"the table has no motion for {item.name} (gear {gear}{(melee ? ", a melee weapon" : plain ? ", neither a gun nor a melee weapon, and no default" : "")}, {length:0.00} m long)"; return null; }
        if (fallback && Told.Add(gear)) ForgeSkinPlugin.Note($"inspect: {item.name} (gear {gear}) has no motion of its own; it plays one of {string.Join(", ", motions.Select(m => m.Id))}, picked from its length {length:0.00} m");
        var names = InspectParts.Names(gunNode);
        var pool = Playable(motions, item.name, gear, parts, names);
        if (pool.Count == 0 && !plain)
        {
            pool = Playable(Table.Fallback(length, melee), item.name, gear, parts, names);
            if (Told.Add(Fnv.Hash32("all/" + gear))) ForgeSkinPlugin.Note($"inspect: {item.name} (gear {gear}) can play none of its own motions; it plays one picked from its length {length:0.00} m");
            if (pool.Count == 0)
            {
                // Nor one of its length's: the default look, which needs no part.
                pool = Playable(Table.Default, item.name, gear, parts, names);
                if (pool.Count > 0) (kind, rig.Feel) = (Table.DefaultKind, Table.DefaultFeel);
                if (pool.Count > 0 && Told.Add(Fnv.Hash32("default/" + gear))) ForgeSkinPlugin.Note($"inspect: {item.name} (gear {gear}) can play no motion of its length either; it plays the default look");
            }
        }
        if (pool.Count == 0) { _why = "the item can play no motion of the table"; return null; }
        return new Setting(item, camera, arms, gunNode, rig, gear, fallback, extents, kind, pool, parts, names, meshes, melee);
    }

    // A motion's parts bound on the gun and the rig it is solved on (its parts' and meshes' boxes; a melee weapon's turned).
    static (InspectParts Moved, InspectRig Rig, NQuaternion ItemTurn) Bind(Setting setup, InspectMotion motion)
    {
        var rig = setup.Rig;
        var moved = InspectParts.Bind(motion, setup.GunNode, setup.Parts, setup.Names, setup.Meshes, out var partRig, out var boxes);
        rig.Parts = partRig;
        rig.Meshes = boxes;
        var itemTurn = NQuaternion.Identity;
        if (setup.Melee) (itemTurn, rig) = (InspectRig.LengthAsY, rig.Turned(InspectRig.LengthAsY));
        return (moved, rig, itemTurn);
    }

    static Task<(InspectLook Look, double Ms)> Solving(InspectMotion motion, InspectRig rig, bool plain = false) => Task.Run(() =>
    {
        var from = Stopwatch.GetTimestamp();
        var look = plain ? InspectLook.Plain(motion, rig) : InspectLook.Solve(motion, rig);
        return (look, (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency);
    });

    // Looks solved ahead: once the item in the hands is at rest with no look on, every motion it can play is solved off the main thread
    // (Prepare) and kept by item and motion with the rig it was solved on, so a press plays at once. Kept for the last AheadItems items
    // held (the ones in the hands and the backpack's slots); an item's looks are dropped once the gun, the camera or the arms are not
    // where they were (a skin, an attachment or the field of view changed), said once, and solved again at rest.
    sealed record Ready(InspectRig Rig, Task<(InspectLook Look, double Ms)> Solve, string[] Meshes);
    static readonly Dictionary<(IntPtr Item, uint Motion), Ready> Ahead = new();
    static readonly List<IntPtr> AheadOrder = new();
    const int AheadItems = 4;
    static IntPtr _prepared;
    static long _preparedAt;
    static Task? _preparing;
    static string? _preparedName;
    static uint _preparedGear;
    static long _checkedAt;
    static IntPtr _tried;
    const float RecheckEvery = 0.5f;

    static void Prepare()
    {
        if (_preparing is { } busy)
        {
            if (!busy.IsCompleted) return;
            _preparing = null;
            ForgeSkinPlugin.Note($"inspect {_preparedName}: every look solved ahead in {(Stopwatch.GetTimestamp() - _preparedAt) * 1000.0 / Stopwatch.Frequency:0} ms from rest");
            // (A motion solved unplayable on this gun leaves its pool now, said once, not first on a press.)
            foreach (var (key, solved) in Ahead)
                if (key.Item == _prepared && solved.Solve.IsCompletedSuccessfully && solved.Solve.Result.Look.Unplayable is { } cannot && Cannot.Add(((ulong)_preparedGear << 32) | key.Motion))
                    ForgeSkinPlugin.Note($"inspect {_preparedName}, {solved.Solve.Result.Look.Motion.Id} is not played: {cannot}");
        }
        // (The item in the hands as the holder's last step had it, which ForgeCore reads once a frame: nothing of the game is read here
        // on a frame where the item is the one prepared and no recheck is due.)
        var held = GunMount.HeldItem;
        if (held == IntPtr.Zero) return;
        if (held == _prepared)
        {
            // The item at rest is measured again every RecheckEvery seconds, and its looks solved again where what they were solved on
            // changed (a skin's attachment shown a moment after a look ends, say), so the next press still finds them.
            if (Stopwatch.GetTimestamp() - _checkedAt < RecheckEvery * Stopwatch.Frequency) return;
            _checkedAt = Stopwatch.GetTimestamp();
            Recheck(held);
            return;
        }
        if (AheadOrder.Contains(held)) { (_prepared, _preparing) = (held, null); AheadOrder.Remove(held); AheadOrder.Add(held); return; }
        // (An item not ready to measure (the game's equip sequence, a move) is tried again RecheckEvery seconds later, not every frame.)
        if (held == _tried && Stopwatch.GetTimestamp() - _checkedAt < RecheckEvery * Stopwatch.Frequency) return;
        (_tried, _checkedAt) = (held, Stopwatch.GetTimestamp());
        SolveAhead(held);
    }

    // (Prepare, every RecheckEvery seconds at rest: the item measured again against its looks solved ahead.)
    static void Recheck(IntPtr held)
    {
        var was = _why;
        var now = Setup();
        _why = was;
        if (now == null || now.Item.Pointer != held || now.Pool.FirstOrDefault(m => Ahead.ContainsKey((held, m.Number))) is not { } motion) return;
        var (moved, rig, _) = Bind(now, motion);
        moved.Restore();
        var ready = Ahead[(held, motion.Number)];
        if (Unlike(ready.Rig, rig, ready.Meshes, now.Meshes) is { } changed) Forget(held, $"{changed} changed at rest");
    }

    // (Prepare, once an item comes into the hands: every look of its pool solved ahead off the main thread.)
    static void SolveAhead(IntPtr held)
    {
        var why = _why;
        var setup = Setup();
        _why = why;
        if (setup == null || setup.Item.Pointer != held) return;
        (_prepared, _preparedAt, _preparedName, _preparedGear) = (held, Stopwatch.GetTimestamp(), $"{setup.Item.name} (gear {setup.Gear})", setup.Gear);
        AheadOrder.Remove(held);
        AheadOrder.Add(held);
        while (AheadOrder.Count > AheadItems) Forget(AheadOrder[0], null);
        var solves = new List<Task>();
        foreach (var motion in setup.Pool)
        {
            var (moved, rig, _) = Bind(setup, motion);
            moved.Restore();
            var solve = Solving(motion, rig);
            Ahead[(held, motion.Number)] = new Ready(rig, solve, [.. setup.Meshes.Select(m => m.Mesh.name)]);
            solves.Add(solve);
        }
        _preparing = Task.WhenAll(solves);
    }

    static void Forget(IntPtr item, string? why)
    {
        foreach (var key in Ahead.Keys.Where(k => k.Item == item).ToList()) Ahead.Remove(key);
        AheadOrder.Remove(item);
        if (item == _prepared) _prepared = IntPtr.Zero;
        if (why != null && Told.Add(Fnv.Hash32("ahead/" + item + "/" + why.Split(" (")[0]))) ForgeSkinPlugin.Note($"inspect: the looks solved ahead for the item in the hands are solved again: {why}");
    }

    // Whether a rig measured now is the one a look was solved ahead on: the same camera, gun (its bounds and parts) and hold, within
    // SameAt metres and SameTurn degrees. The hold is the gun and the off hand in the grip hand's frame, not where the three are in the
    // view: at rest the game sways the arms and the gun together in the view (by more than SameAt within seconds), and a look plays on
    // the gun's live pose each frame (Tick), so that sway does not change the look; a hold changed by a skin, attachment or other arms does.
    const float SameAt = 0.005f, SameTurn = 1f;
    // (Unlike: what differs between two rigs past SameAt and SameTurn, null when they are the same, for the log.)
    static string? Unlike(InspectRig a, InspectRig b, string[] then, List<(Renderer Mesh, NVector3 Lo, NVector3 Hi)> now)
    {
        static bool Near(NVector3 x, NVector3 y) => NVector3.Distance(x, y) <= SameAt;
        static float Turn(NQuaternion x, NQuaternion y) => 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(NQuaternion.Dot(x, y)))) * 180f / MathF.PI;
        static bool Turned(NQuaternion x, NQuaternion y) => Turn(x, y) <= SameTurn;
        if (MathF.Abs(a.TanHalf - b.TanHalf) > 1e-4f || MathF.Abs(a.Aspect - b.Aspect) > 1e-3f) return "the camera's field of view or aspect";
        if (!Near(a.Centre, b.Centre) || !Near(a.Extents, b.Extents)) return $"the gun's bounds ({NVector3.Distance(a.Centre, b.Centre) * 1000f:0} mm at the centre, {NVector3.Distance(a.Extents, b.Extents) * 1000f:0} mm in size)";
        if ((a.Meshes?.Length ?? 0) != (b.Meshes?.Length ?? 0) || (a.Parts?.Length ?? 0) != (b.Parts?.Length ?? 0)) return $"the gun's meshes ({a.Meshes?.Length ?? 0} then, {b.Meshes?.Length ?? 0} now: {string.Join(", ", now.Select(m => m.Mesh.name).Concat(then).Distinct().Select(n => (n, d: now.Count(m => m.Mesh.name == n) - then.Count(x => x == n))).Where(x => x.d != 0).Select(x => $"{x.n} {x.d:+0;-0}"))}) or parts";
        for (var i = 0; i < (a.Parts?.Length ?? 0); i++)
            if (!Near(a.Parts![i].Lo, b.Parts![i].Lo) || !Near(a.Parts[i].Hi, b.Parts[i].Hi)) return "the gun's parts";
        if (MathF.Abs(a.GripArm.Upper - b.GripArm.Upper) > SameAt || MathF.Abs(a.GripArm.Fore - b.GripArm.Fore) > SameAt
            || MathF.Abs(a.OffArm.Upper - b.OffArm.Upper) > SameAt || MathF.Abs(a.OffArm.Fore - b.OffArm.Fore) > SameAt) return "the arms";
        var (ia, ib) = (NQuaternion.Inverse(a.Grip), NQuaternion.Inverse(b.Grip));
        var (gunA, gunB) = (NVector3.Transform(a.GunAt - a.GripAt, ia), NVector3.Transform(b.GunAt - b.GripAt, ib));
        if (!Near(gunA, gunB) || !Turned(ia * a.Gun, ib * b.Gun)) return $"the gun in the grip hand ({NVector3.Distance(gunA, gunB) * 1000f:0} mm, {Turn(ia * a.Gun, ib * b.Gun):0.0} degrees)";
        var (offA, offB) = (NVector3.Transform(a.OffAt - a.GripAt, ia), NVector3.Transform(b.OffAt - b.GripAt, ib));
        if (!Near(offA, offB) || !Turned(ia * a.Off, ib * b.Off)) return $"the off hand against the grip hand ({NVector3.Distance(offA, offB) * 1000f:0} mm, {Turn(ia * a.Off, ib * b.Off):0.0} degrees)";
        return null;
    }

    // The motions of `motions` this gun can play: one whose parts it lacks or does not show is left out, said once per gun and motion,
    // and so is one already solved unplayable on it (Start said why).
    static List<InspectMotion> Playable(IReadOnlyList<InspectMotion> motions, string gun, uint gear, GearPartHolder? parts, Dictionary<string, List<Transform>> names)
    {
        var pool = new List<InspectMotion>();
        foreach (var motion in motions)
        {
            if (Cannot.Contains(((ulong)gear << 32) | motion.Number)) continue;
            if (InspectParts.Missing(motion, parts, names) is not { } missing) { pool.Add(motion); continue; }
            if (Told.Add(Fnv.Hash32(gear + "/" + motion.Id))) ForgeSkinPlugin.Note($"inspect: {gun} (gear {gear}) does not play {motion.Id}: {missing}");
        }
        return pool;
    }

    sealed record Press(Task<(InspectLook Look, double Ms)> Solve, ItemEquippable Item, Camera Camera, PlayerFPSBody Arms, float RestView, uint Gear, bool Fallback, NVector3 Extents, double MeasuredMs, InspectParts Parts, string Kind, NQuaternion ItemTurn, long Pressed, bool Ahead);
    static Press? _press;
    const float PressWait = 0.5f;
    static float _wanted = -1f;   // when a press is waiting for the gun to be ready; negative: none
    static string? _why;      // what stood in the way at the last look

    // Every state NotReady reads, for the log line of a press that found no free hands (the first reason hides the others).
    static string Hands()
    {
        var local = Players.Local;
        var holder = local != null && !local.WasCollected ? local.FPItemHolder : null;
        if (local == null || local.WasCollected || holder == null) return "no local player or item holder";
        var item = holder.WieldedItem;
        return $"motion {Players.Motion(local)}, busy {holder.ItemIsBusy}, sequence {holder.IsPlayingAnimationSequence}, aimed {holder.ItemAimTrigger}, aim held {InputLease.Down(InputAction.Aim)}, reloading {(item != null && item.IsReloading)}, screen {UIKit.GameScreenOpen}";
    }

    // Why a look cannot start now (null: it can). Only the game's own state of the hands and the player's motion are reasons.
    static string? NotReady(PlayerAgent local, FirstPersonItemHolder holder, ItemEquippable? item)
    {
        var motion = Players.Motion(local);
        if (motion is not (PlayerMotion.Stand or PlayerMotion.Crouch or PlayerMotion.StandStill)) return $"the player is {motion}";
        if (UIKit.GameScreenOpen) return "a game screen is open";
        if (InputLease.Down(InputAction.Aim)) return "aim is held";
        var swung = item != null ? item.TryCast<MeleeWeaponFirstPerson>() : null;
        if (item == null) return "no item is in hand";
        if (swung != null && swung.CurrentStateName != eMeleeWeaponState.Idle) return $"the melee weapon is in its {swung.CurrentStateName} state";
        if (holder.ItemIsBusy) return $"the item holder is busy (player {motion})";
        if (holder.IsPlayingAnimationSequence) return $"the game is playing an animation sequence (player {motion})";
        if (holder.ItemAimTrigger) return "the gun is aimed";
        if (item.IsReloading) return "the gun is reloading";
        return null;
    }

    // The solve of the last press is done: the look starts, if the same gun is still in the hands at rest.
    static void Start()
    {
        var press = _press!;
        _press = null;
        var (look, solved) = press.Solve.Result;
        var local = Players.Local;
        if (local == null || local.WasCollected) return;
        var holder = local.FPItemHolder;
        var item = press.Item;
        if (holder == null || holder.WieldedItem == null || holder.WieldedItem.Pointer != item.Pointer || press.Camera.WasCollected || press.Arms.WasCollected)
        {
            ForgeSkinPlugin.Note("inspect: the gun left the hands while the look was solved; no look is played");
            return;
        }
        if (NotReady(local, holder, item) is { } why) { ForgeSkinPlugin.Note($"inspect: the player moved on while the look was solved ({why}); no look is played"); return; }
        var (camera, arms, gear, motion, extents) = (press.Camera, press.Arms, press.Gear, look.Motion, press.Extents);
        var gunNode = item!.transform;
        var view = camera.transform;
        var length = extents.Z * 2f;
        var fallback = press.Fallback;
        // A look this gun cannot play (a part its hand must take between the thumb and the fingers that no clasp takes) is said once and
        // left out of the gun's pool; another motion is solved in its place.
        if (look.Unplayable is { } cannot)
        {
            Cannot.Add(((ulong)gear << 32) | motion.Number);
            ForgeSkinPlugin.Note($"inspect {item.name} (gear {gear}), {motion.Id} is not played: {cannot}; another motion plays");
            Begin();
            return;
        }
        if (!float.IsNaN(look.Fault) && Found.Add(((ulong)gear << 32) | motion.Number))
            ForgeSkinPlugin.Warn($"inspect {item.name} (gear {gear}), {motion.Id}: at {look.Fault:0.00} s {look.FaultRule} in this pair of hands (a finding; it plays)");
        if (look.IndexFault is { } index && Told.Add(Fnv.Hash32("index/" + gear + "/" + motion.Id)))
            ForgeSkinPlugin.Warn($"inspect {item.name} (gear {gear}), {motion.Id}: {index} (a finding; it plays)");
        if (look.MagNote is { } mag && Told.Add(Fnv.Hash32("mag/" + gear + "/" + motion.Id)))
            ForgeSkinPlugin.Note($"inspect {item.name} (gear {gear}), {motion.Id}: {mag}");
        if (look.SupportNote is { } support && Told.Add(Fnv.Hash32("support/" + gear + "/" + motion.Id)))
            ForgeSkinPlugin.Note($"inspect {item.name} (gear {gear}), {motion.Id}: {support}");
        _look = look;
        _itemTurn = press.ItemTurn;
        _parts = press.Parts;
        _parts.Start(gunNode);
        InspectFingers.Start(look, arms.m_anim);
        _sounds = InspectSounds.For(Table, motion, look.Slow, item, gear, press.Kind, self: true);
        _nextSound = 0;
        _gunObject = item.gameObject;
        _camera = camera;
        _restView = press.RestView;
        _item = gunNode;
        _view = view;
        _offTarget = arms.m_leftHandIKTarget;
        _gripTarget = arms.m_rightHandIKTarget;
        BendToward(_gripBend, arms.m_rightHandIK, "grip");
        BendToward(_offBend, arms.m_leftHandIK, "off");
        _gun = item.Pointer;
        _damage = local.Damage != null ? local.Damage.Pointer : IntPtr.Zero;
        _began = Frame.GameTime;
        _stopped = -1f;
        _slot?.Publish(new State { Motion = motion.Number }, _began);
        Play(local, motion.Number, _began, look.Slow);
        var views = string.Join(" ", _look.View.Select(v => (MathF.Atan(v) * 360f / MathF.PI).ToString("0")));
        var swivels = string.Join(" ", _look.Swivel.Select(v => v.ToString("0")));
        ForgeSkinPlugin.Note($"inspect {item.name} (gear {gear}, {press.Kind}): {motion.Id}{(fallback ? " (fallback)" : "")}, {look.Seconds:0.00} s with {_sounds.Length} sounds, bounds {extents.X * 2f:0.00} x {extents.Y * 2f:0.00} x {length:0.00} m, fov {camera.fieldOfView:0.#} aspect {camera.aspect:0.##} near {camera.nearClipPlane:0.###}, item view per key {views}, elbow swivel per key {swivels}; measured in {press.MeasuredMs:0.0} ms on the press, solved in {solved:0} ms off the main thread{(press.Ahead ? " ahead" : " on the press (plain)")}, {(Stopwatch.GetTimestamp() - press.Pressed) * 1000.0 / Stopwatch.Frequency:0} ms from the key to the look");
    }

    // The gun's drawn bounds in its own space: every shown mesh under it (parts, attachments, a skin's models), each by its mesh's box
    // where it has one, else by its world box.
    // A mesh whose box alone makes the gun's box more than twice as wide or tall as every other mesh's together (a sling or rope whose
    // mesh box is a large placeholder) is left out and logged once; the gun's body cannot be, since the other parts span about as much.
    // Once left out a mesh stays out (LeftOut): a rope swings, and in some of its poses its box is small enough to count, which changed
    // the gun's bounds and meshes from one measure to the next (and so dropped the looks solved ahead).
    // `kept`: every mesh counted, with its box (the look's mesh boxes, InspectRig.Meshes).
    static readonly HashSet<IntPtr> LeftOut = new();

    // A mesh counted once stays counted while the game or a skin switches it on and off (a skin's effect that shows and hides an
    // attachment, its object shown and hidden): that changed the gun's meshes from one measure to the next. One never drawn while
    // measured (a part a skin hides) is not counted.
    static readonly HashSet<IntPtr> Blinks = new();

    static bool Measure(Transform gun, out NVector3 centre, out NVector3 extents, out List<(Renderer Mesh, NVector3 Lo, NVector3 Hi)> kept)
    {
        var boxes = new List<(string Name, NVector3 Min, NVector3 Max)>();
        var renderers = new List<Renderer>();
        kept = new List<(Renderer, NVector3, NVector3)>();
        foreach (var r in gun.GetComponentsInChildren<Renderer>(true))
        {
            if (!Models.IsMesh(r)) continue;
            if (OnCharm(r.transform, gun)) continue;
            if (Models.Drawn(r) && r.gameObject.activeInHierarchy) Blinks.Add(r.Pointer);
            else if (!Blinks.Contains(r.Pointer)) continue;
            Box(r, gun, out var lo, out var hi);
            boxes.Add((r.name, lo, hi));
            renderers.Add(r);
        }
        var min = new NVector3(float.MaxValue);
        var max = new NVector3(float.MinValue);
        for (var i = 0; i < boxes.Count; i++)
        {
            NVector3 rmin = new(float.MaxValue), rmax = new(float.MinValue);
            for (var k = 0; k < boxes.Count; k++)
                if (k != i) (rmin, rmax) = (NVector3.Min(rmin, boxes[k].Min), NVector3.Max(rmax, boxes[k].Max));
            NVector3 all = NVector3.Max(rmax, boxes[i].Max) - NVector3.Min(rmin, boxes[i].Min), rest = rmax - rmin;
            if (LeftOut.Contains(renderers[i].Pointer)) continue;
            if (boxes.Count > 1 && (all.X > 2f * rest.X || all.Y > 2f * rest.Y))
            {
                LeftOut.Add(renderers[i].Pointer);
                if (Told.Add(Fnv.Hash32(boxes[i].Name)))
                    ForgeSkinPlugin.Note($"inspect: {gun.name}'s mesh {boxes[i].Name} ({all.X:0.00} x {all.Y:0.00} m with it, {rest.X:0.00} x {rest.Y:0.00} without) is left out of its bounds");
                continue;
            }
            min = NVector3.Min(min, boxes[i].Min);
            max = NVector3.Max(max, boxes[i].Max);
            kept.Add((renderers[i], boxes[i].Min, boxes[i].Max));
        }
        centre = (min + max) / 2f;
        extents = (max - min) / 2f;
        return min.X <= max.X;
    }

    // Whether a mesh hangs on a skin's charm (CharmLook.Roots): the rope and its pendant swing on the gun, so they are no part of its
    // bounds or meshes (counted, they changed both from one measure to the next and dropped the looks solved ahead).
    static bool OnCharm(Transform mesh, Transform gun)
    {
        for (var up = mesh; up != null && up.Pointer != gun.Pointer; up = up.parent)
            if (CharmLook.Roots.Contains(up.Pointer)) return true;
        return false;
    }

    // One mesh's box in the gun's space: its mesh's box where it has one, a skinned mesh's own box about its root bone, else its world
    // box. (A world box is square to the world: taken into the gun's space it grows and shrinks as the gun turns in the world, which
    // the hold's sway and the player's look do every frame.)
    static void Box(Renderer r, Transform gun, out NVector3 min, out NVector3 max)
    {
        min = new NVector3(float.MaxValue);
        max = new NVector3(float.MinValue);
        var mesh = r.TryCast<MeshRenderer>() != null && r.GetComponent<MeshFilter>() is { } filter ? filter.sharedMesh : null;
        var skinned = mesh == null ? r.TryCast<SkinnedMeshRenderer>() : null;
        var box = mesh != null ? mesh.bounds : skinned != null ? skinned.localBounds : r.bounds;
        var space = mesh != null ? r.transform : skinned != null ? (skinned.rootBone != null ? skinned.rootBone : r.transform) : null;
        for (var i = 0; i < 8; i++)
        {
            var corner = box.center + Vector3.Scale(box.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
            var p = N(gun.InverseTransformPoint(space != null ? space.TransformPoint(corner) : corner));
            min = NVector3.Min(min, p);
            max = NVector3.Max(max, p);
        }
    }

    // One arm, in view space: its two-bone IK's bones and the hand's IK target the game set last frame.
    static (InspectArm Arm, NVector3 At, NQuaternion Turn)? Arm(RootMotion.FinalIK.LimbIK? ik, Transform? target, Transform view)
    {
        var solver = ik != null ? ik.solver : null;
        Transform? shoulder = solver?.bone1?.transform, elbow = solver?.bone2?.transform, wrist = solver?.bone3?.transform;
        if (shoulder == null || elbow == null || wrist == null || target == null) return null;
        var at = N(view.InverseTransformPoint(target.position));
        var turn = NQuaternion.Inverse(Q(view.rotation)) * Q(target.rotation);
        return (new InspectArm(N(view.InverseTransformPoint(shoulder.position)), N(view.InverseTransformPoint(elbow.position)),
            N(view.InverseTransformPoint(wrist.position)), at, turn), at, turn);
    }

    // The holder is about to put the arms on the grips: put the gun where the motion has it this frame.
    static void Pose(FirstPersonItemHolder holder)
    {
        if (_look == null || _item == null || _view == null) return;
        var now = Frame.GameTime;
        _weight = _stopped < 0f ? 1f : 1f - (now - _stopped) / InspectMotion.FadeOut;
        if (_weight <= 0f) { Clear(); return; }
        _time = _hold >= 0f ? Math.Min(_hold, _look.Seconds) : now - _began;
        _viewTurn = Turn(_view);
        var toView = NQuaternion.Inverse(_viewTurn);
        _look.GunPose(_time, _weight, N(_view.InverseTransformPoint(_item.TransformPoint(Vec.Zero))), toView * Turn(_item) * _itemTurn, _restView, out _gunAt, out _gunTurn, out var view);
        _item.SetPositionAndRotation(_view.TransformPoint(U(_gunAt)), U(_viewTurn * _gunTurn * NQuaternion.Inverse(_itemTurn)));
        _camera!.fieldOfView = MathF.Atan(view) * 360f / MathF.PI;
        _parts?.Pose(_look, _time, _weight);
        InspectFingers.Pose(_look, _time, _weight);
        // Each beat's sound once, on the frame its time is crossed; none after the look was stopped.
        for (; _stopped < 0f && _nextSound < _sounds.Length && _sounds[_nextSound].Time <= _time; _nextSound++) InspectSounds.Play(_sounds[_nextSound], _gunObject);
    }

    // The holder has put both hands' IK targets on the gun's grips: while the gun is free (spun, tossed) keep the grip hand where it held
    // the gun, and move the off hand where the motion has it. The fingers take the shapes the motion gives them (InspectFingers) and keep
    // the game's grip elsewhere.
    static void PoseOffHand()
    {
        if (_look == null || _view == null) return;
        if (_gripBend.Elbow != null) _gripBend.Elbow.position = _view.TransformPoint(U(_look.GripElbow()));
        if (_look.Free && _gripTarget != null)
        {
            _look.GripPose(_look.HeldAt, _look.HeldTurn, out var hand, out var handTurn);
            _gripTarget.SetPositionAndRotation(_view.TransformPoint(U(hand)), U(_viewTurn * handTurn));
        }
        if (_offTarget == null) return;
        var live = N(_view.InverseTransformPoint(_offTarget.TransformPoint(Vec.Zero)));
        if (!_look.HandOff(_time))
        {
            var held = live;
            if (_offBend.Hand != null && !_offBend.Hand.WasCollected) _offBend.Hand.localScale = _offBend.HandScale * _look.OffScale(_time, _weight, ref held, NQuaternion.Identity);
            if (_offBend.Elbow != null) _offBend.Elbow.position = _view.TransformPoint(U(_look.OffElbowAt(_time, _weight, live)));
            return;
        }
        var toView = NQuaternion.Inverse(_viewTurn);
        _look.HandPose(_time, _weight, _gunAt, _gunTurn, live, toView * Turn(_offTarget), out var at, out var turn);
        // (The hand drawn at the key's size about its palm's middle: its bone scaled, its wrist's target moved to keep the palm in place.)
        var scale = _look.OffScale(_time, _weight, ref at, turn);
        if (_offBend.Hand != null && !_offBend.Hand.WasCollected) _offBend.Hand.localScale = _offBend.HandScale * scale;
        _offTarget.SetPositionAndRotation(_view.TransformPoint(U(at)), U(_viewTurn * turn));
        if (_offBend.Elbow != null) _offBend.Elbow.position = _view.TransformPoint(U(_look.OffElbow(at)));
    }

    // An arm's two-bone IK bends toward a goal the look moves (its elbow swung), instead of its own bend setting, until Clear.
    static void BendToward(Bend bend, RootMotion.FinalIK.LimbIK? ik, string arm)
    {
        var solver = ik != null ? ik.solver : null;
        if (solver == null) { ForgeSkinPlugin.Fail("skin-inspect-failed", $"weapon inspect found no {arm} arm IK solver; the elbow keeps the game's bend"); return; }
        if (bend.Elbow == null || bend.Elbow.WasCollected)
        {
            var go = new GameObject($"ForgeSkin inspect {arm} elbow");
            UnityEngine.Object.DontDestroyOnLoad(go);
            bend.Elbow = go.transform;
        }
        (bend.Solver, bend.Was, bend.GoalWas, bend.WeightWas) = (solver, solver.bendModifier, solver.bendGoal, solver.bendModifierWeight);
        bend.Hand = solver.bone3?.transform;
        bend.HandScale = bend.Hand != null ? bend.Hand.localScale : Vector3.one;
        solver.bendGoal = bend.Elbow;
        solver.bendModifier = RootMotion.FinalIK.IKSolverLimb.BendModifier.Goal;
        solver.bendModifierWeight = 1f;
    }

    static void Unbend(Bend bend)
    {
        if (bend.Solver != null && !bend.Solver.WasCollected)
        {
            bend.Solver.bendModifier = bend.Was;
            bend.Solver.bendGoal = bend.GoalWas;
            bend.Solver.bendModifierWeight = bend.WeightWas;
        }
        if (bend.Hand != null && !bend.Hand.WasCollected) bend.Hand.localScale = bend.HandScale;
        (bend.Solver, bend.GoalWas, bend.Hand) = (null, null, null);
    }

    // A transform's world rotation from its axes (read through methods: no property of the game's is read each frame).
    static NQuaternion Turn(Transform t) => InspectLook.Look(N(t.TransformDirection(Vec.Forward)), N(t.TransformDirection(Vec.Up)));

    static NVector3 N(Vector3 v) => new(v.x, v.y, v.z);

    static NQuaternion Q(Quaternion q) => new(q.x, q.y, q.z, q.w);

    static Vector3 U(NVector3 v) => new(v.X, v.Y, v.Z);

    static Quaternion U(NQuaternion q) => new(q.X, q.Y, q.Z, q.W);

    // Developer entry for in-game screenshots (drive_cli.py call static:ForgeSkin.Plugin.Inspect Hold <motion> <seconds>): the next look on
    // the gun in the hands plays that motion, whatever its pool, and stands at `seconds` into it (look time, after the kind's pace) until
    // Hold gives another time or Release lets it run out; Keys reads the playing look's key times. Play never calls these.
    static string? _holdMotion;
    static float _hold = -1f;

    internal static string Hold(string motion, float seconds)
    {
        if (!Motions.Values.Any(m => m.Id == motion)) return "inspect hold: no motion " + motion;
        _hold = Math.Max(0f, seconds);
        if (_look != null && _look.Motion.Id == motion) return $"inspect hold: {motion} at {_hold:0.000} s";
        if (_look != null) Clear();
        _holdMotion = motion;
        _wanted = Frame.GameTime;
        _why = null;
        return $"inspect hold: starting {motion} at {_hold:0.000} s";
    }

    internal static string Release()
    {
        _hold = -1f;
        _holdMotion = null;
        Stop(cut: true);
        return "inspect hold: released";
    }

    internal static string Keys()
    {
        if (_look is not { } look) return _press != null ? "inspect keys: solving" : "inspect keys: no look (" + (_why ?? "none") + ")";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Times(IEnumerable<float> times) => string.Join(" ", times.Select(t => (t * look.Slow).ToString("0.000", inv)));
        return $"inspect keys: {look.Motion.Id} seconds {look.Seconds.ToString("0.000", inv)} gun {Times(look.Motion.Gun.Select(k => k.Time))} hand {Times(look.Motion.Hand.Select(k => k.Time))} finding {(float.IsNaN(look.Fault) ? "none" : look.Fault.ToString("0.00", inv) + " s " + look.FaultRule)}";
    }

    // Puts the gun back: over FadeOut, or at once (`cut`: the motion ran out, or the gun left the hands).
    static void Stop(bool cut = false)
    {
        if (_item == null) return;
        if (cut) Clear();
        else if (_stopped < 0f) _stopped = Frame.GameTime;
        _slot?.Clear();
        if (Players.Local is { WasCollected: false } local && Bodies.TryGetValue(local.Owner.Lookup, out var body)) body.Track.Stop(BodyFadeOut);
    }

    // The item camera gets back the field of view it had when the look started (the motion may have widened it).
    static void Clear()
    {
        if (_camera != null && !_camera.WasCollected) _camera.fieldOfView = MathF.Atan(_restView) * 360f / MathF.PI;
        Unbend(_gripBend);
        Unbend(_offBend);
        _parts?.Restore();
        _parts = null;
        InspectFingers.Stop();
        _camera = null;
        _item = _view = _offTarget = _gripTarget = null;
        _look = null;
        _sounds = [];
        _gunObject = null;
        _gun = IntPtr.Zero;
        _stopped = -1f;
    }

    static void Reset()
    {
        Clear();
        _press = null;
        _wanted = -1f;
        _slot?.Clear();
        Bodies.Clear(); // the bodies are gone or rebuilt; ForgeCore stopped their clips
        InspectFingers.Forget();
    }

    // A teammate started or stopped a motion.
    static void Seen(ulong player, bool active)
    {
        if (active && _slot!.TryGet(player, out var state, out var start) && ForgeCore.Level.PlayerByLookup(player) is { } agent) Play(agent, state.Motion, start);
        else if (Bodies.TryGetValue(player, out var body)) { body.Track.Stop(BodyFadeOut); body.Sounds = []; }
    }

    // A motion this machine's table lacks (the other player's table was changed) is not shown on that body. `slow`: the local player's
    // look's pace; a teammate's is read from the gun in their hands (its kind's feel), and their gun plays the motion's sounds.
    static void Play(PlayerAgent agent, uint number, float start, float? slow = null)
    {
        if (!Motions.TryGetValue(number, out var motion)) { if (Told.Add(number)) ForgeSkinPlugin.Note($"inspect: motion {number:x8} of player {agent.Owner.Lookup} is not in this table; that body shows none"); return; }
        var player = agent.Owner.Lookup;
        if (!Bodies.TryGetValue(player, out var body)) Bodies.Add(player, body = new Body());
        body.Sounds = [];
        if (slow == null)
        {
            var item = agent.Inventory != null ? agent.Inventory.WieldedItem : null;
            var gear = item != null && item.GearPartHolder != null && item.GearPartHolder.GearIDRange is { } range ? SkinnedGear.GearIdOf(range) : 0u;
            var melee = item != null && Melee(item);
            var length = item != null && Measure(item.transform, out _, out var extents, out _) ? (melee ? extents.Y : extents.Z) * 2f : 0f;
            // The default look is paced by its own kind, on whatever plays it (a gun that can play none of its own included).
            var plain = item != null && (Plain(item) || Table.Default.Any(m => m.Number == number));
            slow = (plain ? Table.DefaultFeel : Table.Feel(gear, length, melee)).Slow;
            if (item != null)
            {
                body.Sounds = InspectSounds.For(Table, motion, slow.Value, item, gear, plain ? Table.DefaultKind : Table.Kind(gear, length, melee), self: false);
                body.Gun = item.gameObject;
                body.Start = start;
                body.Next = 0;
                var elapsed = Frame.GameTime - start;
                while (body.Next < body.Sounds.Length && body.Sounds[body.Next].Time <= elapsed) body.Next++; // joined late: the past beats stay silent
            }
        }
        var clip = Clip(motion, slow.Value);
        if (body.Agent != agent.Pointer || body.Joints?.Root == null || body.Joints.Root.WasCollected)
        {
            body.Agent = agent.Pointer;
            body.Joints = Models.BindJoints(agent); // null logged once by ForgeCore: that body shows no inspect
        }
        if (body.Joints != null) body.Track = body.Joints.CrossFade(body.Track, clip, new JointPlayback(start, false, Hand, 1f, BodyFadeIn, BodyFadeOut), BodyFadeOut);
    }
}
