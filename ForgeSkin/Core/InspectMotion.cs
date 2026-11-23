using System.Numerics;
using System.Text.Json;

namespace ForgeSkin.Format;

// Weapon inspect (PLAN §4.14): the motions are data, a table of authored keys and which gun plays which; this file reads the table,
// picks a gun's motions and solves and evaluates a motion for one gun in one pair of hands. Pure: view space (x right, y up, z forward
// from the item camera's eye), degrees in the data, metres. The plugin's Native/Inspect measures the gun, the camera and the arms when
// a look starts and lays the result over the game's pose each frame; nothing here knows a skin, and no gun is a branch in code.

// Where the off hand is at a hand key. Grip: the game's own grip on the gun. Away: lowered out of the view. The rest are on the gun,
// placed from its measured bounds: Under (its own grip slid along the gun), Side (the gun's left side), Top, Muzzle (under the front
// end). Node and Mag: on one of the motion's parts (Mag: its magazine part), on a face of the part's measured mesh box (Face, U, V),
// going with the part (a carried part hangs in the hand).
// Open: under the gun's held pose, as Under, while the gun is free (tossed): the hand waits where it held the gun and takes it back.
public enum InspectHold : byte { Grip, Away, Under, Side, Top, Muzzle, Mag, Node, Open }

// A face of a part's mesh box in the gun's space: Left (-x), Right (+x), Top (+y), Bottom (-y).
public enum InspectFace : byte { Left, Right, Top, Bottom }

// How a part key is reached: Smooth (a smooth step), In (s = u^2.2, gathering speed), Out (losing it), Spring (gathering speed into a
// stop at the key, bouncing back by Bounce of the way and settling within SpringSettle after it).
public enum InspectEase : byte { Smooth, In, Out, Spring }

// A part key that holds the part off the gun in the view: its mesh box centre at At (-1..1 of the half view of the item camera as the
// look starts) Dist metres from the eye, its top (the node's up) toward the eye, its length (the gun's z at rest) turned Roll degrees
// from the view's right toward its up.
public readonly record struct InspectCarry(Vector2 At, float Dist, float Roll, float Tip = 0f);

// How a kind of gun moves (per kind in the table's "feel"): its weight on the wrist that carries it over the authored keys (a spring of
// F hertz and Zeta damping that the keys' own acceleration drives: the gun hangs back as the hand starts and goes past as it stops;
// Carry how much of that shows, Wind the degrees it draws back before a large turn), the push of a hit (HitDegrees of the wrist pressing
// the muzzle down, HitMetres down), the most the gun may turn in a second while held (Spin degrees) and how much slower its motions play.
public readonly record struct InspectFeel(float F, float Zeta, float Carry, float Wind, float HitDegrees, float HitMetres, float Spin, float Slow);

// A sound role (the table's "sounds.roles"): played from the gun's own reload and fire sequences (the first event name that matches one
// of Match: "*x" ends with x, "x*" starts with it, "*x*" holds it, else equal; lower case), else Default by the gun's kind ("*" any
// kind), or (Zoom +1 in, -1 out) the gun's own aim sounds.
public sealed record InspectSoundRole(string[] Match, IReadOnlyDictionary<string, string> Default, int Zoom);

// A role a cue plays, After seconds after it.
public readonly record struct InspectCueRole(string Role, float After);

// Where the grip hand holds the gun at a gun key: Grip, the game's own grip; Stock, slid back under the stock (on a gun without one,
// at its grip).
public enum InspectGrip : byte { Grip, Stock }

// A key of the gun. Rest: the game's own pose. Otherwise the intent: Turn (muzzle toward the view's right), Tilt (muzzle up), Roll (about
// the bore, top toward the right), in degrees from the gun held level and pointing ahead, and where the gun's bounds centre sits in the view (At, -1..1 of the
// half width and height) with the shown part (InspectLook.Corner) inside Fill of the half view. How far away the gun sits is solved, not authored.
// Spin and Flip free the gun from the grip hand. Spin alone (degrees about its side axis) between two keys is a twirl on the trigger
// finger: the gun turns about its trigger guard and the hand goes with it (a small circle, the wrist rocking once a turn), on the way
// between the keys as any other (a smooth step; into a Through key it only gathers speed and runs on). Toss (metres up
// the view, the height above where it leaves the hand) makes this key the catch of a toss under Gravity: the gun leaves the hand
// InspectMotion.TossFlight before it, turning the key's whole turns of Spin and Flip (degrees about its bore) about its bounds centre, and
// the hand winds up, throws, follows through, meets it and sinks with it (InspectLook). Rise alone lifts the gun with both hands on it
// (a heft). They are interpolated as numbers, so a key can spin a whole turn or more. Hold: where the grip hand holds the gun at this key
// (between two keys it slides along the gun). Windup and Settle (shares of the way, 0..0.3): the way into this key turns back first and
// goes past it and back at its end (InspectMotion.GunSpan). Hit: reaching this key is a knock the gun takes (the kind's push). Cue: a
// sound the moment the look passes the key. Palm (a motion from the hold): the key turns the item from the hold with the grip hand's
// palm turned up (the shortest way), not from the hold itself: where an item is thrown from and caught, whichever way the game holds it.
public readonly record struct InspectGunKey(float Time, bool Rest, float Turn, float Tilt, float Roll, Vector2 At, float Fill, float Spin = 0f, float Flip = 0f, float Rise = 0f, bool Through = false, InspectGrip Hold = InspectGrip.Grip, float Toss = 0f, float Windup = 0f, float Settle = 0f, float Gravity = InspectMotion.Gravity, bool Hit = false, string? Cue = null, bool Palm = false);

// A key of the off hand: what it holds and, on the gun, where along it (0 the back of the bounds, 1 the front); on a part (Node, Mag
// with the motion's magazine part) the part's index, the face of its mesh box the palm lies on, where on it (U along the gun 0 back to
// 1 front, NaN its default; V across it: on Left and Right 0 bottom to 1 top, on Top and Bottom 0 the gun's left to 1 its right) and
// Offset (metres: out from the face, across it, along the gun); Spin (degrees) turns the hand on the face about the way out of it, from
// the way the game's own hold lays it there (a hand laid over a slide across it, its fingers down one side and its heel on the other,
// where the hold would lay them along it); Fingers its finger shape (null: the hold's own); Cue a sound at the key.
// Regrip: the way into this key from the key before, on the same part, is the hand changing its hold on the part (from one face to
// another, say from under a magazine to its side), turning round the part and touching it all the way, within InspectMotion.RegripMost.
// Scale: the off hand drawn this many times its size, about its palm's middle (a hand that would fill the view working a part close to
// the eye drawn smaller so the part shows); between keys on the hand's smooth step, 1 at the first and last keys.
public readonly record struct InspectHandKey(float Time, InspectHold On, float Along, int Part = -1, Vector3 Offset = default, ForgeVisual.FingerBend? Fingers = null, InspectFace Face = InspectFace.Left, float U = float.NaN, float V = 0.5f, string? Cue = null, bool Regrip = false, float Spin = 0f, bool Tips = false, bool Clasp = false, float Scale = 1f);

// A key of one of a motion's parts: Move (metres in the gun's space: x right, y up, z toward the muzzle), Out (metres along the node's own
// down axis: a magazine pulled out of its well) and Clear (as many lengths of its mesh box along that axis), Turn (degrees about the
// part's axis, through its pivot) and Show (0..1: whether a part the look adds, the chambered round, is drawn; 1 on a carried key); Rest: a key with none
// (only t), whose turn is the nearest whole turn of the part's symmetry to the key before. Ease and Bounce: how the way into it goes.
// Carry: held off the gun in the view instead. Cue: a sound at the key.
public readonly record struct InspectPartKey(float Time, Vector3 Move, float Out, float Turn, float Show, bool Rest = false, InspectEase Ease = InspectEase.Smooth, float Bounce = 0f, InspectCarry? Carry = null, string? Cue = null, float Clear = 0f);

// A part of the gun a motion moves: a node by name (every node of that name under the gun: a rig some guns carry twice moves as one;
// Names lists the names it may have on different guns, the first found on the gun is used, Node is the first), "@mag" (the magazine's
// mount, so the magazine part) or "@round" (a copy of the magazine's top round the look puts in the chamber, at the shell-eject point).
// Axis is in the gun's space at rest; a part under another part's node goes with it, and Follow (the index of an earlier part) makes one
// that is not go with that part too (the rounds of a revolver's speed loader with its cylinder). A gun without a part's node, or whose
// node draws nothing (a skin hid it), does not play the motion, unless the part is Optional (it moves what is there). Symmetry (degrees):
// it looks the same turned by this much (a cylinder of six chambers 60), so a rest key turns it back only to the nearest such turn.
// Carrier: the node only carries the motion's parts under it (a revolver's crane, whose node also holds the barrel and frame in the
// game's model): what else hangs under it stays with the gun.
public sealed record InspectPart(string Node, int Follow, Vector3 Axis, InspectPartKey[] Keys, bool Optional = false, string[]? Names = null, float Symmetry = 360f, bool Carrier = false)
{
    public const string Magazine = "@mag", Round = "@round";

    public string[] Nodes => Names ?? new[] { Node };

    // Per key, its turn, a rest key's wound to the nearest whole symmetry of the key before.
    public float[] Turns { get; } = Resolve(Keys, Symmetry);

    static float[] Resolve(InspectPartKey[] keys, float symmetry)
    {
        var turns = new float[keys.Length];
        for (var i = 0; i < keys.Length; i++) turns[i] = keys[i].Rest && i > 0 ? MathF.Round(turns[i - 1] / symmetry) * symmetry : keys[i].Turn;
        return turns;
    }
}

public sealed class InspectMotion
{
    public string Id { get; }
    public InspectGunKey[] Gun { get; }
    public InspectHandKey[] Hand { get; }
    public InspectPart[] Parts { get; }
    // Whether the keys turn the item from the hold the game has it in ("from": "hold"; turn, tilt and roll about the view's up, right
    // and forward) instead of from level and ahead: for an item whose hold is not a gun's ready pose (a melee weapon).
    public bool FromHold { get; }
    readonly float[] _gunTimes, _handTimes;

    InspectMotion(string id, InspectGunKey[] gun, InspectHandKey[] hand, InspectPart[] parts, bool fromHold)
    {
        Id = id;
        FromHold = fromHold;
        Gun = gun;
        Hand = hand;
        Parts = parts;
        _gunTimes = gun.Select(k => k.Time).ToArray();
        _handTimes = hand.Select(k => k.Time).ToArray();
        Number = ForgeCore.Fnv.Hash32(id);
    }

    // How a motion is named between players and in a recording: FNV-1a 32 of its id.
    public uint Number { get; }

    public float Seconds => Gun[^1].Time;

    // An interruption puts the gun back this fast.
    public const float FadeOut = 0.15f;

    public const float DefaultFill = 0.95f;

    // The gun keys around `time` and the smooth step between them (a == b past the last key).
    // A key the gun goes through without stopping (Through): the span into it only eases in and the span out of it only eases out.
    // A key with Windup or Settle (shares of the way, not with Through): the way into it first draws back by Windup over its first
    // WindupShare (anticipation), then goes past the key by Settle and comes back over its last SettleShare (overshoot and settle),
    // each stretch on a smooth step.
    public void GunSpan(float time, out int a, out int b, out float s)
    {
        Span(_gunTimes, time, out a, out b, out s);
        if (a == b) return;
        var t = Math.Clamp((time - _gunTimes[a]) / (_gunTimes[b] - _gunTimes[a]), 0f, 1f);
        if (Gun[b].Windup > 0f || Gun[b].Settle > 0f)
        {
            float w = Gun[b].Windup, k = Gun[b].Settle, t0 = w > 0f ? WindupShare : 0f, t1 = k > 0f ? 1f - SettleShare : 1f;
            static float Smooth(float x) => x * x * (3f - 2f * x);
            s = t < t0 ? -w * Smooth(t / t0) : t < t1 || k <= 0f ? -w + (1f + k + w) * Smooth((t - t0) / (t1 - t0)) : 1f + k - k * Smooth((t - t1) / (1f - t1));
            return;
        }
        if (!(Gun[a].Through || Gun[b].Through)) return;
        s = Gun[a].Through && Gun[b].Through ? t : Gun[b].Through ? t * t * (2f - t) : 1f - (1f - t) * (1f - t);
    }

    public const float WindupShare = 0.2f, SettleShare = 0.3f;

    public void HandSpan(float time, out int a, out int b, out float s) => Span(_handTimes, time, out a, out b, out s);

    // The finger shape of a hand on the gun at a key that gives none, and of a hand that touches nothing.
    public static readonly ForgeVisual.FingerBend HoldShape = new(0.5f, 0.7f, 0.75f, 0.8f, 0.85f), Relaxed = new(0.2f, 0.2f, 0.2f, 0.2f, 0.2f);

    // The off hand's finger shape at `time` as its keys give it, between its keys on a smooth step (a key without one: HoldShape on a
    // part, Relaxed away from the gun), and how far the hand takes it (0: the game's own grip, at grip keys and under the gun, where
    // the hand is turned as the game holds it and the game's fingers are the ones that fit). InspectLook relaxes it further where the
    // hand touches nothing and stops each finger on what it closes on. `held`: per key, a shape the look's hold gives it instead (a
    // clasped magazine's, InspectLook.Clasp).
    public float FingersAt(float time, out ForgeVisual.FingerBend bend, ForgeVisual.FingerBend?[]? held = null)
    {
        HandSpan(time, out var a, out var b, out var s);
        ForgeVisual.FingerBend? Shape(int key) => held?[key] ?? (Hand[key] is var k && k.On == InspectHold.Grip ? null
            : k.Fingers ?? (k.On == InspectHold.Away ? Relaxed : k.On == InspectHold.Under ? null : HoldShape));
        ForgeVisual.FingerBend? from = Shape(a), to = Shape(b);
        bend = from is { } x && to is { } y ? ForgeVisual.FingerBend.Lerp(x, y, s) : from ?? to ?? default;
        if (a != b && ClaspRegrip(b, held) && from is { } open && to is { } close)
            bend = s < RegripOpen ? ForgeVisual.FingerBend.Lerp(open, Opened, Smooth(s / RegripOpen))
                : s < RegripClose ? Opened : ForgeVisual.FingerBend.Lerp(Opened, close, Smooth((s - RegripClose) / (1f - RegripClose)));
        float wa = from != null ? 1f : 0f, wb = to != null ? 1f : 0f;
        return wa + (wb - wa) * s;
    }

    // The off hand's size at `time` (InspectHandKey.Scale), between its keys on the smooth step.
    public float ScaleAt(float time)
    {
        HandSpan(time, out var a, out var b, out var s);
        return Hand[a].Scale + (Hand[b].Scale - Hand[a].Scale) * s;
    }

    // A regrip into or out of a clasped magazine (`held`, InspectLook.Clasp) goes as a hand changes its hold: over the first third of
    // the span the fingers open (Opened) while the hand stays, in the middle third the hand goes to its new hold, and over the last
    // the fingers close on it (InspectLook.HandPose takes the hand's share of the way, RegripMove).
    public const float RegripOpen = 1f / 3f, RegripClose = 2f / 3f;

    // (A hand going from one clasped hold to a clasp of another kind, the gun's body to a part or back, changes its hold so too.)
    public bool ClaspRegrip(int b, ForgeVisual.FingerBend?[]? held) => held != null && b > 0 && b < held.Length
        && ((Hand[b].Regrip && (held[b - 1] != null || held[b] != null)) || (held[b - 1] != null && held[b] != null && Hand[b - 1].On != Hand[b].On));

    // How far the hand has gone on a clasp regrip at `s` of its span: none while the fingers open, all once they close.
    public static float RegripMove(float s) => Smooth(Math.Clamp((s - RegripOpen) / (RegripClose - RegripOpen), 0f, 1f));

    // The hand opened to let go and take the new hold: every joint as at rest, the four fingers out straight and the thumb straight and
    // spread as far as it goes (beside the fingers it would lie across the magazine the palm is on).
    public static readonly ForgeVisual.FingerBend Opened = ForgeVisual.FingerBend.ByJoints(new float[15], spread: 1f);

    static float Smooth(float t) => t * t * (3f - 2f * t);

    // A spring key's bounce settles within this long after it (seconds of the motion).
    public const float SpringSettle = 0.08f;

    // The longest a hand takes to change its hold on a part it holds (seconds of the motion): turning a magazine from the palm to the
    // fingers is one movement of the fingers and wrist, about a quarter to a third of a second; a slower change reads as the hand sliding
    // over the part, which no hold does.
    public const float RegripMost = 0.4f;

    // How far a regrip moves the hand on the same face at least (a share of the face, in u or v): the hand's own give in a hold is half
    // a centimetre (frame rule R3), and a quarter of the game's magazine faces (4 to 30 cm) is 1 to 7 cm, the palm's middle on another
    // quarter of the face; a smaller move is the same hold nudged, which R3 keeps still.
    public const float RegripShift = 0.25f;

    // The keys of part `part` around `time` and the eased share of the way between them (a == b before the first key and after the last).
    public void PartSpan(int part, float time, out int a, out int b, out float s)
    {
        var keys = Parts[part].Keys;
        s = 0f;
        if (time <= keys[0].Time) { a = b = 0; return; }
        if (time >= keys[^1].Time) { a = b = keys.Length - 1; return; }
        b = 1;
        while (keys[b].Time < time) b++;
        a = b - 1;
        s = Ease(keys[b].Ease, (time - keys[a].Time) / (keys[b].Time - keys[a].Time));
    }

    public static float Ease(InspectEase ease, float u) => ease switch
    {
        InspectEase.In => u * u * u * (4f - 3f * u),
        InspectEase.Out => 1f - MathF.Pow(1f - u, 2.2f),
        InspectEase.Spring => u * u,
        _ => u * u * (3f - 2f * u),
    };

    // A bounce back off a stop: 0 at both ends, 1 at its height (a third of the way), leaving at Rebound a unit of time.
    internal static float Bump(float v) => 6.75f * v * (1f - v) * (1f - v);
    const float Rebound = 6.75f;

    // A settle after a stop that ends at no speed: 0 at both ends and at no speed there, 1 at its height (half way).
    internal static float Settle(float v) => 16f * v * v * (1f - v) * (1f - v);

    // A spring key's bounce leaves its stop at Restitution times its bounce of the speed it came in at (Ease.Spring arrives at 2 shares
    // of the way a unit of time): the bounce goes on from the arrival, not at a speed of its own.
    const float Restitution = 3f;

    // Part `part` at `time`: its keys eased (InspectEase), a rest key's turn wound (InspectPart.Turns), a spring key's bounce after it;
    // at rest (all zero) before its first key and after its last (and its bounce), except that after its last it keeps the turn it was
    // wound to (a whole turn of its symmetry: a cylinder spun round does not snap back; InspectLook takes whole turns off only where they
    // look the same). A carried key counts as at rest here (InspectLook places it). Clear is a share of the part's length, which
    // InspectLook measures.
    public InspectPartKey PartAt(int part, float time)
    {
        var p = Parts[part];
        var keys = p.Keys;
        PartSpan(part, time, out var a, out var b, out var s);
        InspectPartKey Value(int k) => keys[k].Carry != null ? new InspectPartKey(keys[k].Time, Vector3.Zero, 0f, p.Turns[k], 1f) : new InspectPartKey(keys[k].Time, keys[k].Move, keys[k].Out, p.Turns[k], keys[k].Show, Clear: keys[k].Clear);
        static InspectPartKey Mix(InspectPartKey x, InspectPartKey y, float s, float time) => new(time, Vector3.Lerp(x.Move, y.Move, s), x.Out + (y.Out - x.Out) * s, x.Turn + (y.Turn - x.Turn) * s, x.Show + (y.Show - x.Show) * s, Clear: x.Clear + (y.Clear - x.Clear) * s);
        var bounced = false;
        var value = time <= keys[0].Time || time >= keys[^1].Time ? default : Mix(Value(a), Value(b), s, time);
        // The latest spring key whose bounce is still on: back toward the key before it by its share of the way.
        for (var j = keys.Length - 1; j >= 1; j--)
        {
            if (keys[j].Time > time) continue;
            if (keys[j].Ease == InspectEase.Spring && time < keys[j].Time + SpringSettle)
            {
                if (time >= keys[^1].Time) value = Value(keys.Length - 1);
                var arrival = 2f / (keys[j].Time - keys[j - 1].Time);
                var height = keys[j].Bounce * Restitution * arrival * SpringSettle / Rebound;
                value = Mix(value, Value(j - 1), MathF.Min(height, 1f) * Bump((time - keys[j].Time) / SpringSettle), time);
                bounced = true;
            }
            break;
        }
        return bounced || (time > keys[0].Time && time < keys[^1].Time) ? value with { Time = time } : time >= keys[^1].Time ? default(InspectPartKey) with { Turn = p.Turns[^1] } : default;
    }

    static void Span(float[] times, float time, out int a, out int b, out float s)
    {
        b = 1;
        while (b < times.Length - 1 && times[b] < time) b++;
        a = b - 1;
        if (times.Length == 1 || time >= times[^1]) { a = b = times.Length - 1; s = 0f; return; }
        var t = Math.Clamp((time - times[a]) / (times[b] - times[a]), 0f, 1f);
        s = t * t * (3f - 2f * t);
    }

    // The authored turn and roll at `time` (rest keys 0): what a body that cannot solve the hands (third person) shows of the motion.
    public void Intent(float time, out float turn, out float roll)
    {
        GunSpan(time, out var a, out var b, out var s);
        turn = Gun[a].Turn + (Gun[b].Turn - Gun[a].Turn) * s;
        roll = Gun[a].Roll + (Gun[b].Roll - Gun[a].Roll) * s;
    }

    public const float Gravity = 9.8f;
    // A toss's hand winds up and throws over TossLead seconds before the gun leaves it.
    public const float TossLead = 0.3f;

    // How long (seconds) a gun tossed `height` metres above where it leaves the hand under `gravity` is in the air.
    public static float TossFlight(float height, float gravity) => 2f * MathF.Sqrt(2f * height / gravity);

    // Whether the way into gun key `key` is a twirl: a spin alone between two keys of the motion's own.
    public bool IsTwirl(int key) => key > 0 && !Gun[key - 1].Rest && !Gun[key].Rest && Gun[key].Toss <= 0f && Gun[key - 1].Spin != Gun[key].Spin;

    // The cues the cues of a toss and a twirl play, without a key naming them: the gun leaving the hand, landing back in it, a twirl stopping.
    public const string TossCue = "toss", CatchCue = "catch", TwirlCue = "twirl-catch";

    // Every cue of the motion played `slow` times slower, by when (seconds into the look): the keys' and the toss's and twirl's own.
    public (float Time, string Cue)[] Cues(float slow)
    {
        var list = new List<(float, string)>();
        for (var i = 0; i < Gun.Length; i++)
        {
            if (Gun[i].Cue is { } c) list.Add((Gun[i].Time * slow, c));
            if (Gun[i].Toss > 0f)
            {
                list.Add((Gun[i].Time * slow - TossFlight(Gun[i].Toss, Gun[i].Gravity), TossCue));
                list.Add((Gun[i].Time * slow, CatchCue));
            }
            if (IsTwirl(i)) list.Add((Gun[i].Time * slow, TwirlCue));
        }
        foreach (var k in Hand) if (k.Cue is { } c) list.Add((k.Time * slow, c));
        foreach (var p in Parts) foreach (var k in p.Keys) if (k.Cue is { } c) list.Add((k.Time * slow, c));
        return list.OrderBy(e => e.Item1).ToArray();
    }

    internal static InspectMotion Read(string id, JsonElement motion)
    {
        var gun = new List<InspectGunKey>();
        foreach (var key in motion.GetProperty("gun").EnumerateArray())
        {
            float Angle(string name, float most = 180f)
            {
                var v = key.TryGetProperty(name, out var e) ? e.GetSingle() : 0f;
                return float.IsFinite(v) && Math.Abs(v) <= most ? v : throw Bad(id, $"{name} is a finite angle of at most {most} degrees");
            }
            var rise = key.TryGetProperty("rise", out var r) ? r.GetSingle() : 0f;
            if (!(rise >= 0f && rise <= 1f)) throw Bad(id, "rise is 0..1 metres");
            var rest = !key.EnumerateObject().Any(p => p.Name is not ("t" or "cue"));
            if (rest && key.TryGetProperty("through", out _)) throw Bad(id, "through is for a key of the motion's own");
            var at = new Vector2(float.NaN); // none written: no point of the view asked for
            if (key.TryGetProperty("at", out var place))
            {
                var xy = place.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                at = xy.Length == 2 && xy.All(v => Math.Abs(v) <= 1f) ? new Vector2(xy[0], xy[1]) : throw Bad(id, "at is [x, y], each -1..1");
            }
            var fill = key.TryGetProperty("fill", out var f) ? f.GetSingle() : DefaultFill;
            if (!(fill > 0f && fill <= 2f) || Math.Abs(at.X) >= fill || Math.Abs(at.Y) >= fill) throw Bad(id, "fill is in (0, 2] and at lies inside it");
            var hold = InspectGrip.Grip;
            if (key.TryGetProperty("hold", out var h))
                hold = Enum.TryParse<InspectGrip>(h.GetString(), true, out var g) && h.GetString()!.All(char.IsLower) && !rest ? g : throw Bad(id, "hold is grip or stock, on a key of the motion's own");
            var toss = key.TryGetProperty("toss", out var to) ? to.GetSingle() : 0f;
            if (!(toss >= 0f && toss <= 0.5f)) throw Bad(id, "toss is 0..0.5 metres");
            float Share(string name) => !key.TryGetProperty(name, out var e) ? 0f
                : e.GetSingle() is var v && v >= 0f && v <= 0.3f && !rest && !key.TryGetProperty("through", out _) ? v : throw Bad(id, $"{name} is 0..0.3, on a key of the motion's own that the gun does not go through");
            var gravity = key.TryGetProperty("g", out var gv) ? gv.GetSingle() : Gravity;
            if (!(gravity >= 8.5f && gravity <= 10.5f) || (key.TryGetProperty("g", out _) && toss <= 0f)) throw Bad(id, "g is 8.5..10.5, on a toss key");
            var hit = key.TryGetProperty("hit", out var hi) && hi.GetBoolean();
            if (hit && rest) throw Bad(id, "hit is for a key of the motion's own");
            gun.Add(new InspectGunKey(Time(id, key, gun.Count == 0 ? -1f : gun[^1].Time), rest, Angle("turn"), Angle("tilt"), Angle("roll"), at, fill, Angle("spin", 1440f), Angle("flip", 1440f), rise, key.TryGetProperty("through", out var th) && th.GetBoolean(), hold, toss, Share("windup"), Share("settle"), gravity, hit, Cue(id, key), key.TryGetProperty("palm", out var pm) && pm.GetBoolean()));
        }
        if (gun.Count < 2 || gun[0].Time != 0f || !gun[0].Rest || !gun[^1].Rest) throw Bad(id, "the gun has at least two keys, the first at 0, the first and last the game's pose ({ \"t\": … } alone)");
        // A toss leaves and lands in the hand: from a key of the motion's own, whole turns on and off, no rise on either. Any other turning
        // of the gun between keys of the motion's own is a twirl: a spin alone, at no rise (the game's pose takes the nearest whole turn).
        static bool Whole(float angle) => angle == MathF.Round(angle / 360f) * 360f;
        for (var i = 1; i < gun.Count; i++)
        {
            InspectGunKey from = gun[i - 1], to = gun[i];
            if (to.Toss > 0f)
            {
                if (from.Rest || from.Rise != 0f || to.Rise != 0f || !Whole(from.Spin) || !Whole(from.Flip) || !Whole(to.Spin) || !Whole(to.Flip) || (from.Spin == to.Spin && from.Flip == to.Flip))
                    throw Bad(id, "a toss goes from a key of the motion's own to its key by whole turns of spin or flip, with no rise on either");
                if (to.Time - TossFlight(to.Toss, to.Gravity) - TossLead < from.Time - 1e-4f)
                    throw Bad(id, $"a toss of {to.Toss} m is {TossFlight(to.Toss, to.Gravity):0.00} s in the air and its hand winds up {TossLead} s before it: its key comes at least that long after the key before");
            }
            else if (!from.Rest && !to.Rest && (from.Spin != to.Spin || from.Flip != to.Flip) && (from.Flip != to.Flip || from.Rise != 0f || to.Rise != 0f))
                throw Bad(id, "only a toss flips the gun or turns it with a rise; a spin alone is a twirl");
        }
        // The grip hand leaves the game's grip only between keys of the motion's own, so it slides back to it before the game's pose.
        for (var i = 0; i < gun.Count; i++)
            if (gun[i].Hold != InspectGrip.Grip && (gun[i - 1].Rest || gun[i + 1].Rest)) throw Bad(id, "a key next to the game's pose holds the grip");
        var parts = new List<InspectPart>();
        if (motion.TryGetProperty("parts", out var ps))
            foreach (var part in ps.EnumerateArray())
            {
                var nodeProperty = part.GetProperty("node");
                var names = nodeProperty.ValueKind == JsonValueKind.Array ? nodeProperty.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : new[] { nodeProperty.GetString() ?? "" };
                var node = names.Length > 0 ? names[0] : "";
                if (node.Length == 0 || names.Any(n => n.Length == 0) || (node[0] == '@' && (node is not (InspectPart.Magazine or InspectPart.Round) || names.Length > 1))
                    || names.Skip(1).Any(n => n[0] == '@') || parts.Any(p => p.Node == node))
                    throw Bad(id, "a part's node is a node name, a list of the names it has on different guns, @mag or @round, each part once");
                var symmetry = part.TryGetProperty("symmetry", out var sy) ? sy.GetSingle() : 360f;
                if (!(symmetry > 0f && symmetry <= 360f)) throw Bad(id, $"part {node}: symmetry is (0, 360] degrees");
                var follow = -1;
                if (part.TryGetProperty("follow", out var fo) && (follow = parts.FindIndex(p => p.Node == fo.GetString())) < 0) throw Bad(id, $"part {node} follows a part named before it");
                var axis = !part.TryGetProperty("about", out var ab) ? Vector3.UnitZ : ab.GetString() switch
                {
                    "bore" => Vector3.UnitZ,
                    "side" => Vector3.UnitX,
                    "up" => Vector3.UnitY,
                    _ => throw Bad(id, $"part {node}: about is bore, side or up"),
                };
                var keys = new List<InspectPartKey>();
                foreach (var key in part.GetProperty("keys").EnumerateArray())
                {
                    float Value(string name, float most) => !key.TryGetProperty(name, out var e) ? 0f
                        : e.GetSingle() is var v && float.IsFinite(v) && Math.Abs(v) <= most ? v : throw Bad(id, $"part {node}: {name} is at most {most}");
                    var move = Vector3.Zero;
                    if (key.TryGetProperty("move", out var mv))
                    {
                        var xyz = mv.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                        move = xyz.Length == 3 && xyz.All(v => float.IsFinite(v) && Math.Abs(v) <= 0.5f) ? new Vector3(xyz[0], xyz[1], xyz[2]) : throw Bad(id, $"part {node}: move is [x, y, z], each at most 0.5 m");
                    }
                    var show = Value("show", 1f);
                    if (show < 0f) throw Bad(id, $"part {node}: show is 0..1");
                    var clear = Value("clear", 3f);
                    if (clear < 0f) throw Bad(id, $"part {node}: clear is 0..3 lengths");
                    var ease = InspectEase.Smooth;
                    if (key.TryGetProperty("ease", out var es))
                        ease = es.GetString() switch { "in" => InspectEase.In, "out" => InspectEase.Out, "spring" => InspectEase.Spring, _ => throw Bad(id, $"part {node}: ease is in, out or spring") };
                    var bounce = key.TryGetProperty("bounce", out var bo) ? bo.GetSingle() : ease == InspectEase.Spring ? 0.08f : 0f;
                    if (!(bounce >= 0f && bounce <= 0.3f) || (bounce > 0f && ease != InspectEase.Spring)) throw Bad(id, $"part {node}: bounce is 0..0.3, on a spring key");
                    InspectCarry? carry = null;
                    if (key.TryGetProperty("carry", out var ca))
                    {
                        var xy = ca.TryGetProperty("at", out var cat) ? cat.EnumerateArray().Select(v => v.GetSingle()).ToArray() : [0f, 0f];
                        var dist = ca.TryGetProperty("dist", out var cd) ? cd.GetSingle() : 0.35f;
                        var croll = ca.TryGetProperty("roll", out var cr) ? cr.GetSingle() : 0f;
                        var tip = ca.TryGetProperty("tip", out var ct) ? ct.GetSingle() : 0f;
                        if (xy.Length != 2 || xy.Any(v => !(Math.Abs(v) <= 1f)) || !(dist >= 0.2f && dist <= 0.6f) || !(Math.Abs(croll) <= 180f) || !(tip >= 0f && tip <= 80f))
                            throw Bad(id, $"part {node}: carry is {{ at: [x, y] each -1..1, dist: 0.2..0.6 m, roll: degrees, tip: 0..80 degrees }}");
                        if (new[] { "move", "out", "turn", "show", "clear" }.Any(n => key.TryGetProperty(n, out _))) throw Bad(id, $"part {node}: a carried key moves the part by its carry alone");
                        carry = new InspectCarry(new Vector2(xy[0], xy[1]), dist, croll, tip);
                    }
                    var restKey = !key.EnumerateObject().Any(p => p.Name is not ("t" or "cue" or "ease" or "bounce"));
                    keys.Add(new InspectPartKey(Time(id, key, keys.Count == 0 ? -1f : keys[^1].Time), move, Value("out", 0.5f), Value("turn", 1440f), show, restKey, ease, bounce, carry, Cue(id, key), clear));
                }
                if (keys.Count < 2 || !keys[0].Rest || !keys[^1].Rest || keys[^1].Time > gun[^1].Time)
                    throw Bad(id, $"part {node} has at least two keys, its first and last at rest (only t, cue, ease), within the motion");
                for (var k = 1; k + 1 < keys.Count; k++)
                    if (keys[k].Ease == InspectEase.Spring && keys[k + 1].Time - keys[k].Time < SpringSettle - 1e-4f) throw Bad(id, $"part {node}: a spring key's bounce takes {SpringSettle} s before the next key");
                if (keys.Any(k => k.Carry != null) && follow >= 0) throw Bad(id, $"part {node}: a carried part follows no other part");
                var carrier = part.TryGetProperty("carrier", out var cr2) && cr2.GetBoolean();
                if (carrier && node[0] == '@') throw Bad(id, $"part {node}: carrier is for a named node");
                parts.Add(new InspectPart(node, follow, axis, keys.ToArray(), part.TryGetProperty("optional", out var op) && op.GetBoolean(), names.Length > 1 ? names : null, symmetry, carrier));
            }
        var magazine = parts.FindIndex(p => p.Node == InspectPart.Magazine);
        var hand = new List<InspectHandKey>();
        if (motion.TryGetProperty("hand", out var hands))
            foreach (var key in hands.EnumerateArray())
            {
                var on = key.TryGetProperty("on", out var o) && Enum.TryParse<InspectHold>(o.GetString(), true, out var held) && o.GetString()!.All(char.IsLower)
                    ? held : throw Bad(id, "on is one of grip, away, under, side, top, muzzle, mag, node, open");
                // Without along, under is the off hand's own place on the gun (where the game puts it).
                var along = key.TryGetProperty("along", out var l) ? l.GetSingle() : on is InspectHold.Under or InspectHold.Open ? float.NaN : 0.5f;
                if (!(along >= 0f && along <= 1f) && !float.IsNaN(along)) throw Bad(id, "along is 0..1");
                var part = on == InspectHold.Mag ? magazine : -1;
                if (on == InspectHold.Mag && part < 0) throw Bad(id, "a mag hand needs the motion's @mag part");
                if (on == InspectHold.Node && (!key.TryGetProperty("node", out var n) || (part = parts.FindIndex(p => p.Node == n.GetString())) < 0))
                    throw Bad(id, "a node hand names one of the motion's parts (by the first of its names)");
                var onPart = on is InspectHold.Node or InspectHold.Mag;
                if (!onPart && new[] { "face", "at", "offset" }.Any(p => key.TryGetProperty(p, out _))) throw Bad(id, "face, at and offset are for a hand on a part (node, mag)");
                var face = InspectFace.Left;
                if (key.TryGetProperty("face", out var fa))
                    face = fa.GetString() switch { "left" => InspectFace.Left, "right" => InspectFace.Right, "top" => InspectFace.Top, "bottom" => InspectFace.Bottom, _ => throw Bad(id, "face is left, right, top or bottom") };
                float u = float.NaN, v = 0.5f;
                if (key.TryGetProperty("at", out var uv))
                {
                    var p = uv.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    (u, v) = p.Length == 2 && p.All(e => e >= 0f && e <= 1f) ? (p[0], p[1]) : throw Bad(id, "at is [u, v], each 0..1");
                }
                var offset = Vector3.Zero;
                if (key.TryGetProperty("offset", out var off))
                {
                    var xyz = off.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    offset = xyz.Length == 3 && xyz.All(e => float.IsFinite(e) && Math.Abs(e) <= 0.1f) ? new Vector3(xyz[0], xyz[1], xyz[2]) : throw Bad(id, "offset is [out, across, along] from the face (a magazine in the hand: to its left, up it, to its front), each at most 0.1 m");
                }
                var time = Time(id, key, hand.Count == 0 ? -1f : hand[^1].Time);
                var regrip = key.TryGetProperty("regrip", out var rg) && rg.GetBoolean();
                if (regrip && !(onPart && hand.Count > 0 && hand[^1].Part == part && time - hand[^1].Time <= RegripMost + 1e-4f))
                    throw Bad(id, $"regrip is the way into a key on the part the key before holds too, at most {RegripMost} s long");
                // (An at left out is compared as the face's middle.)
                if (regrip && hand[^1].Face == face && MathF.Abs((float.IsNaN(u) ? 0.5f : u) - (float.IsNaN(hand[^1].U) ? 0.5f : hand[^1].U)) < RegripShift && MathF.Abs(v - hand[^1].V) < RegripShift)
                    throw Bad(id, $"a regrip changes the hold: another face, or at moved by at least {RegripShift} of the face");
                var spin = 0f;
                if (key.TryGetProperty("spin", out var sp))
                    spin = onPart && sp.GetSingle() is var degrees && MathF.Abs(degrees) <= 180f ? degrees : throw Bad(id, "spin (degrees about the way out of the face, -180..180) is for a hand on a part (node, mag)");
                var tips = key.TryGetProperty("tips", out var tp) && tp.GetBoolean();
                if (tips && on != InspectHold.Node) throw Bad(id, "tips (the fingertips on the face, not the palm) is for a hand on a node");
                // `clasp`: the part held between the thumb and the fingers (InspectLook.Clasped), not laid against.
                var clasp = key.TryGetProperty("clasp", out var cp) && cp.GetBoolean();
                if (clasp && (on != InspectHold.Node || tips)) throw Bad(id, "clasp (the part held between the thumb and the fingers) is for a hand on a node, without tips");
                var scale = key.TryGetProperty("scale", out var sc) ? sc.GetSingle() : 1f;
                if (!(scale > 0f && float.IsFinite(scale))) throw Bad(id, "scale is a positive size of the hand");
                hand.Add(new InspectHandKey(time, on, along, part, offset, Fingers(id, key), face, u, v, Cue(id, key), regrip, spin, tips, clasp, scale));
            }
        else hand.Add(new InspectHandKey(0f, InspectHold.Grip, 0.5f));
        if (hand[0].Time != 0f || hand[0].On != InspectHold.Grip || hand[^1].On != InspectHold.Grip || hand[^1].Time > gun[^1].Time)
            throw Bad(id, "the hand's first key is at 0, its first and last keys are grip, and it ends with the gun");
        if (hand[0].Fingers != null || hand[^1].Fingers != null) throw Bad(id, "the hand's first and last keys keep the game's fingers");
        if (hand[0].Scale != 1f || hand[^1].Scale != 1f) throw Bad(id, "the hand's first and last keys keep the game's hand size");
        var fromHold = motion.TryGetProperty("from", out var fr) && (fr.GetString() == "hold" ? true : throw Bad(id, "from is hold (or left out: level and ahead)"));
        if (!fromHold && gun.Any(k => k.Palm)) throw Bad(id, "palm is for a motion from the hold");
        return new InspectMotion(id, gun.ToArray(), hand.ToArray(), parts.ToArray(), fromHold);
    }

    static string? Cue(string id, JsonElement key) => !key.TryGetProperty("cue", out var c) ? null
        : c.GetString() is { Length: > 0 } name ? name : throw Bad(id, "cue is a cue name of the table's sounds");

    // A key's finger shape: [thumb, index, middle, ring, pinky], each a whole finger's bend, 0 (open, as the hand rests) to 1 (closed),
    // or [its knuckle, middle and last joint] each 0..1 of that joint's range (ForgeCore FingerBend by joint), the thumb's three
    // followed by its root's spread and opposition, or not.
    static ForgeVisual.FingerBend? Fingers(string id, JsonElement key)
    {
        if (!key.TryGetProperty("fingers", out var f)) return null;
        const string Form = "fingers is [thumb, index, middle, ring, pinky], each a bend 0..1 or its joints [knuckle, middle, last] 0..1 (the thumb's then [spread, opposition] or not)";
        var fingers = f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().ToArray() : [];
        if (fingers.Length != 5) throw Bad(id, Form);
        float Share(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.GetSingle() is var x && x >= 0f && x <= 1f ? x : throw Bad(id, Form);
        if (fingers.All(e => e.ValueKind == JsonValueKind.Number))
            return new ForgeVisual.FingerBend(Share(fingers[0]), Share(fingers[1]), Share(fingers[2]), Share(fingers[3]), Share(fingers[4]));
        float Bend(int n) => fingers[n].ValueKind == JsonValueKind.Number ? Share(fingers[n]) : 0f;
        var whole = new ForgeVisual.FingerBend(Bend(0), Bend(1), Bend(2), Bend(3), Bend(4));
        var (joints, root) = (new float[15], new float[2]);
        for (var n = 0; n < 5; n++)
        {
            if (fingers[n].ValueKind == JsonValueKind.Number) { for (var k = 0; k < 3; k++) joints[n * 3 + k] = Math.Min(1f, whole.Joint(n, k)); continue; }
            var parts = fingers[n].ValueKind == JsonValueKind.Array ? fingers[n].EnumerateArray().Select(Share).ToArray() : [];
            if (parts.Length != 3 && !(n == 0 && parts.Length == 5)) throw Bad(id, Form);
            parts.AsSpan(0, 3).CopyTo(joints.AsSpan(n * 3));
            if (parts.Length == 5) (root[0], root[1]) = (parts[3], parts[4]);
        }
        return ForgeVisual.FingerBend.ByJoints(joints, root[0], root[1]);
    }

    static float Time(string id, JsonElement key, float after)
    {
        var t = key.GetProperty("t").GetSingle();
        return float.IsFinite(t) && t > after ? t : throw Bad(id, "key times rise from 0");
    }

    static FormatException Bad(string id, string what) => new($"inspect motion {id}: {what}");
}

// The motion table: every motion, kinds of gun (named lists of motions), the motions of each gun by its offline gear id (a pool: each
// press plays one of them at random, never the one it played last), and the fallback for a gun the table does not name, by the measured
// length of its bounds. A gun's list and the fallback name motions by id and kinds as "@kind". A later table (a config file) replaces
// motions, kinds and gun entries by id; its fallback list, when it has one, replaces the whole list.
// A skin pack's table (`owner` its skin id) adds to the pools of the guns that wear the skin instead (For): its kinds append to the
// kinds of those names, its gear lists to those guns. Its motions are named "<owner>/<id>", so no pack's motion is mistaken for
// another's or for one of the plugin's, between players too; it has no fallback, and its lists name only its own motions and kinds.
public sealed class InspectTable
{
    readonly Dictionary<string, InspectMotion> _motions = new(StringComparer.Ordinal);
    readonly Dictionary<string, string[]> _kinds = new(StringComparer.Ordinal);
    readonly Dictionary<uint, string[]> _gear = new();
    (float UpTo, string[] Motions, bool Melee)[] _fallback = Array.Empty<(float, string[], bool)>();
    string[] _default = [];
    readonly string? _owner;
    readonly Dictionary<string, InspectFeel> _feel = new(StringComparer.Ordinal);
    readonly Dictionary<string, InspectSoundRole> _roles = new(StringComparer.Ordinal);
    readonly Dictionary<string, InspectCueRole[]> _cues = new(StringComparer.Ordinal);

    // The sound roles and what each cue plays (the table's "sounds").
    public IReadOnlyDictionary<string, InspectSoundRole> Roles => _roles;
    public IReadOnlyDictionary<string, InspectCueRole[]> CueRoles => _cues;

    // The kind of gun `gear` (offline gear id) whose bounds are `length` metres long: the first kind its list names, else the kind of
    // the fallback for its length (which names it first). `melee`: a melee weapon, whose fallback is the entries marked melee.
    public string Kind(uint gear, float length, bool melee = false)
    {
        if (_gear.TryGetValue(gear, out var ids) && ids.FirstOrDefault(i => i.StartsWith('@')) is { } kind) return kind[1..];
        return ByLength(length, melee) is { Length: > 0 } motions ? motions[0].TrimStart('@') : "";
    }

    // The table's "default": the motions of anything held that has none of its own (an item that is neither a gun nor a melee
    // weapon: a tool, a pack, another mod's item; and a gun or melee weapon that can play none of its list), its kind (the first
    // "@kind" it names) and that kind's feel. Its motions turn the item from the hold the game has it in.
    public IReadOnlyList<InspectMotion> Default => _default.Length > 0 ? Expand(_default, _motions, _kinds, "default") : [];
    public string DefaultKind => _default.FirstOrDefault(i => i.StartsWith('@'))?[1..] ?? "";
    public InspectFeel DefaultFeel => _feel.TryGetValue(DefaultKind, out var feel) ? feel : default;

    // The fallback list for that length (empty: the table has none for it).
    string[] ByLength(float length, bool melee) => _fallback.FirstOrDefault(e => e.Melee == melee && length <= e.UpTo).Motions ?? [];

    // How that gun moves (its kind's feel); a kind without one (a table that names no feel) does not follow, slow or cap its turn.
    public InspectFeel Feel(uint gear, float length, bool melee = false) => _feel.TryGetValue(Kind(gear, length, melee), out var feel) ? feel : default;

    // Every pace (Slow) a kind of the table plays at.
    public IEnumerable<float> Slows => _feel.Values.Select(f => f.Slow).Distinct();

    public InspectTable(string? owner = null) => _owner = owner;

    public IEnumerable<InspectMotion> Motions => _motions.Values;

    // The kinds this table names.
    public IEnumerable<string> Kinds => _kinds.Keys;

    // Reads `json` over what the table holds; throws FormatException naming the first fault, and then the table is unchanged.
    public void Add(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Own(string id) => _owner == null || id.StartsWith('@') ? id : _owner + "/" + id;
        var motions = new Dictionary<string, InspectMotion>(_motions, StringComparer.Ordinal);
        if (root.TryGetProperty("motions", out var m))
            foreach (var p in m.EnumerateObject())
                motions[p.Name.StartsWith('@') ? throw new FormatException($"inspect motion {p.Name}: an id does not start with @") : Own(p.Name)] = InspectMotion.Read(Own(p.Name), p.Value);
        string[] Refs(JsonElement list) => list.EnumerateArray().Select(v => Own(v.GetString() ?? "")).ToArray();
        var kinds = new Dictionary<string, string[]>(_kinds, StringComparer.Ordinal);
        if (root.TryGetProperty("kinds", out var k))
            foreach (var p in k.EnumerateObject()) kinds[p.Name] = Refs(p.Value);
        if (_owner != null && (root.TryGetProperty("fallback", out _) || root.TryGetProperty("default", out _))) throw new FormatException("inspect fallback: a skin pack adds to the guns that wear it and has no fallback or default");
        var byDefault = root.TryGetProperty("default", out var df) ? Refs(df) : _default;
        var gear = new Dictionary<uint, string[]>(_gear);
        if (root.TryGetProperty("gear", out var g))
            foreach (var p in g.EnumerateObject())
                gear[uint.TryParse(p.Name, out var id) ? id : throw new FormatException($"inspect gear {p.Name}: not a gear id")] = Refs(p.Value);
        var fallback = _fallback;
        if (root.TryGetProperty("fallback", out var f))
        {
            fallback = f.EnumerateArray().Select(e => (e.TryGetProperty("upTo", out var up) ? up.GetSingle() : float.PositiveInfinity, Refs(e.GetProperty("motions")),
                e.TryGetProperty("melee", out var me) && me.GetBoolean())).ToArray();
            if (fallback.Where(e => !e.Item3).Select(e => e.Item1).DefaultIfEmpty(0f).Last() is var guns && !float.IsPositiveInfinity(guns)
                || fallback.Where(e => e.Item3).Select(e => e.Item1).DefaultIfEmpty(float.PositiveInfinity).Last() is var melees && !float.IsPositiveInfinity(melees))
                throw new FormatException("inspect fallback: the last entry (and the last melee entry) has no upTo");
        }
        foreach (var (name, ids) in kinds)
        {
            if (ids.Length == 0) throw new FormatException($"inspect kind {name}: no motion");
            if (ids.FirstOrDefault(id => !motions.ContainsKey(id)) is { } missing) throw new FormatException($"inspect kind {name}: no motion {missing}");
        }
        foreach (var (where, ids) in gear.Select(e => ("gear " + e.Key, e.Value)).Concat(fallback.Select((e, i) => ("fallback " + i, e.Motions))).Concat(byDefault.Length > 0 ? [("default", byDefault)] : Array.Empty<(string, string[])>()))
            if (Expand(ids, motions, kinds, where).FirstOrDefault(m => where == "default" && !m.FromHold) is { } level)
                throw new FormatException($"inspect default: {level.Id} is not from the hold (an item of any shape is turned from how the game holds it)");
        var feel = new Dictionary<string, InspectFeel>(_feel, StringComparer.Ordinal);
        var roles = new Dictionary<string, InspectSoundRole>(_roles, StringComparer.Ordinal);
        var cues = new Dictionary<string, InspectCueRole[]>(_cues, StringComparer.Ordinal);
        if (_owner != null && (root.TryGetProperty("feel", out _) || root.TryGetProperty("sounds", out _)))
            throw new FormatException("inspect: a skin pack plays the feel and the sounds of the plugin's table and has none of its own");
        if (root.TryGetProperty("feel", out var fe))
            foreach (var p in fe.EnumerateObject())
            {
                float F(string name, float lo, float hi, float fallback = float.NaN)
                {
                    var v = p.Value.TryGetProperty(name, out var e) ? e.GetSingle() : fallback;
                    return v >= lo && v <= hi ? v : throw new FormatException($"inspect feel {p.Name}: {name} is {lo}..{hi}");
                }
                var hit = p.Value.TryGetProperty("hit", out var h) ? h.EnumerateArray().Select(e => e.GetSingle()).ToArray() : [0f, 0f];
                if (hit.Length != 2 || !(hit[0] >= 0f && hit[0] <= 15f) || !(hit[1] >= 0f && hit[1] <= 0.05f)) throw new FormatException($"inspect feel {p.Name}: hit is [degrees 0..15, metres 0..0.05]");
                feel[p.Name] = new InspectFeel(F("f", 0.3f, 10f), F("zeta", 0.1f, 1.5f), F("carry", 0f, 2f, 1f), F("wind", 0f, 12f, 5f), hit[0], hit[1], F("spin", 30f, 3000f), F("slow", 0.5f, 3f, 1f));
            }
        if (root.TryGetProperty("sounds", out var so))
        {
            if (so.TryGetProperty("roles", out var rs))
                foreach (var p in rs.EnumerateObject())
                {
                    var match = p.Value.TryGetProperty("match", out var ma) ? ma.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : [];
                    if (match.Any(m => m.Trim('*').Length == 0 || m != m.ToLowerInvariant() || m.Trim('*').Contains('*'))) throw new FormatException($"inspect sound role {p.Name}: a match is a lower-case event name with * at its start or end");
                    var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (p.Value.TryGetProperty("default", out var de))
                    {
                        if (de.ValueKind == JsonValueKind.String) defaults["*"] = de.GetString()!;
                        else foreach (var d in de.EnumerateObject()) defaults[d.Name] = d.Value.GetString() ?? throw new FormatException($"inspect sound role {p.Name}: a default is an event name");
                    }
                    var zoom = !p.Value.TryGetProperty("zoom", out var zo) ? 0 : zo.GetString() switch { "in" => 1, "out" => -1, _ => throw new FormatException($"inspect sound role {p.Name}: zoom is in or out") };
                    roles[p.Name] = new InspectSoundRole(match, defaults, zoom);
                }
            if (so.TryGetProperty("cues", out var cs))
                foreach (var p in cs.EnumerateObject())
                    cues[p.Name] = p.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? new InspectCueRole(e.GetString()!, 0f)
                        : new InspectCueRole(e.GetProperty("role").GetString()!, e.TryGetProperty("after", out var af) ? af.GetSingle() : 0f)).ToArray();
        }
        if (_owner == null)
        {
            foreach (var (name, list) in cues)
            {
                if (list.Length == 0) throw new FormatException($"inspect cue {name}: plays no role");
                foreach (var r in list)
                    if (!roles.ContainsKey(r.Role) || !(r.After >= 0f && r.After <= 0.3f)) throw new FormatException($"inspect cue {name}: role {r.Role} is not in sounds.roles, or comes after more than 0.3 s");
            }
            foreach (var motion in motions.Values)
                if (motion.Cues(1f).FirstOrDefault(c => !cues.ContainsKey(c.Cue)) is { Cue: { } missing }) throw new FormatException($"inspect motion {motion.Id}: cue {missing} is not in sounds.cues");
            foreach (var name in kinds.Keys)
                if (!feel.ContainsKey(name)) throw new FormatException($"inspect kind {name}: no feel");
            if (fallback.Any(e => e.Motions.Length == 0 || !e.Motions[0].StartsWith('@'))) throw new FormatException("inspect fallback: each entry names its kind (@kind) first");
        }
        _feel.Clear();
        foreach (var (name, v) in feel) _feel[name] = v;
        _roles.Clear();
        foreach (var (name, v) in roles) _roles[name] = v;
        _cues.Clear();
        foreach (var (name, v) in cues) _cues[name] = v;
        _motions.Clear();
        foreach (var (id, motion) in motions) _motions[id] = motion;
        _kinds.Clear();
        foreach (var (name, ids) in kinds) _kinds[name] = ids;
        _gear.Clear();
        foreach (var (id, ids) in gear) _gear[id] = ids;
        _fallback = fallback;
        _default = byDefault;
    }

    // A list of motion ids and "@kind"s as its motions, each once.
    static InspectMotion[] Expand(string[] ids, Dictionary<string, InspectMotion> motions, Dictionary<string, string[]> kinds, string where)
    {
        var list = new List<InspectMotion>();
        foreach (var id in ids)
            foreach (var each in id.StartsWith('@') ? kinds.TryGetValue(id[1..], out var kind) ? kind : throw new FormatException($"inspect {where}: no kind {id[1..]}") : new[] { id })
            {
                var motion = motions.TryGetValue(each, out var found) ? found : throw new FormatException($"inspect {where}: no motion {each}");
                if (!list.Contains(motion)) list.Add(motion);
            }
        return list.Count > 0 ? list.ToArray() : throw new FormatException($"inspect {where}: no motion");
    }

    // The motions of gun `gear` (offline gear id, 0 when unknown) whose bounds are `length` metres along its barrel; `fallback` says the
    // table does not name it and the motions come from its length. `packs`: the tables of the skin packs it wears, whose motions for
    // its gear id and for the kinds its list names come after its own.
    public IReadOnlyList<InspectMotion> For(uint gear, float length, out bool fallback, IEnumerable<InspectTable>? packs = null, bool melee = false)
    {
        fallback = !_gear.TryGetValue(gear, out var ids);
        ids ??= ByLength(length, melee);
        if (ids.Length == 0) return [];
        var pool = Expand(ids, _motions, _kinds, fallback ? "fallback" : "gear " + gear);
        if (packs == null) return pool;
        var list = pool.ToList();
        foreach (var pack in packs)
        {
            var adds = ids.Where(id => id.StartsWith('@') && pack._kinds.ContainsKey(id[1..])).ToList();
            if (pack._gear.TryGetValue(gear, out var own)) adds.AddRange(own);
            if (adds.Count > 0)
                foreach (var motion in Expand(adds.ToArray(), pack._motions, pack._kinds, "pack " + pack._owner))
                    if (!list.Contains(motion)) list.Add(motion);
        }
        return list;
    }

    // The fallback motions for a gun (or melee weapon) `length` metres long; none when the table has no fallback for it.
    public IReadOnlyList<InspectMotion> Fallback(float length, bool melee = false) => ByLength(length, melee) is { Length: > 0 } ids ? Expand(ids, _motions, _kinds, "fallback") : [];

    // One motion of `pool` at random, never `last` (a motion's Number) while the pool has another.
    public static InspectMotion Pick(IReadOnlyList<InspectMotion> pool, uint last, Random random)
    {
        var others = pool.Count(m => m.Number != last);
        if (others == 0) return pool[0];
        var n = random.Next(others);
        foreach (var motion in pool)
            if (motion.Number != last && n-- == 0) return motion;
        return pool[0];
    }
}

// One first-person arm as the game's two-bone IK moves it: the shoulder stays, the elbow bends in a plane through the shoulder and the
// hand (its rest plane swung about the shoulder-to-hand line by a swivel: positive raises the elbow outward, as a person lifts the
// forearm across the face), and the hand bone takes the target's rotation. Comfort keeps a hand rotation within what the wrist does from
// its rest pose relative to the forearm: a twist about the forearm (turning the palm) of at most Twist degrees and a bend within
// Flexion, Extension, Radial and Ulnar (an ellipse through the four); a pose the game's IK would reach only by wringing the wrist is
// turned back to the nearest one within those limits, and Past says by how many degrees. The solve places the hand and swings the
// elbow so that the forearm carries the gun where the wrist would pass them (InspectLook.Solve, OffSwivelFor).
// Joint measures the wrist for rule W (InspectLook.WristBreaks, a finding, never a change of the pose), from the game's own hold, not from
// an anatomical zero: the first-person arm's fingers lie 59 to 103 degrees off the forearm's line in its own bones (pistol and assault
// rifle holds, both hands), and that hold is what the player sees as a relaxed wrist. Flexion, extension and the forearm's rotation are
// within the active range of a healthy wrist (Greene and Heckman, The Clinical Measurement of Joint Motion, AAOS 1994: 80, 70, 80) and a
// little past what daily tasks use (Ryu, Cooney, Askew, An and Chao, J Hand Surg 16A:409-419, 1991: 54 to 60); deviation is the most the
// two motions the owner holds good (`pistol`, `revolver`) reach, 64 radial and 53 ulnar, plus 5 (not the AAOS's 20 and 30).
public readonly struct InspectArm
{
    public const float Twist = 80f, Flexion = 65f, Extension = 65f, Radial = 70f, Ulnar = 58f;
    // The swivel's human range in degrees from the rest plane: the elbow lifts out up to SwivelOut and tucks in up to SwivelIn.
    public const float SwivelOut = 90f, SwivelIn = 20f;

    public readonly Vector3 Shoulder;
    public readonly float Upper, Fore;
    readonly Vector3 _bend;
    readonly float _out; // the sign of a turn about the shoulder-to-hand line that raises the elbow
    readonly Quaternion _rest;
    // In the forearm's frame (Frame), the axes the hand turns about from its rest pose to deviate (+ toward the thumb) and to flex
    // (+ toward the palm), both across the forearm.
    readonly Vector3 _deviate, _flex;

    // From the rest pose: the shoulder, elbow and wrist bones and the hand's IK target.
    public InspectArm(Vector3 shoulder, Vector3 elbow, Vector3 wrist, Vector3 target, Quaternion hand)
    {
        Shoulder = shoulder;
        Upper = Vector3.Distance(shoulder, elbow);
        Fore = Vector3.Distance(elbow, wrist);
        var bend = Vector3.Cross(elbow - shoulder, target - shoulder);
        _bend = bend.LengthSquared() > 1e-10f ? Vector3.Normalize(bend) : Vector3.UnitX;
        _out = 1f;
        _rest = Quaternion.Identity;
        _out = Elbow(target, 10f).Y >= Elbow(target).Y ? 1f : -1f;
        _rest = Quaternion.Inverse(Frame(target)) * hand;
        // The hand's own axes (gun-hold.json: -x toward the fingers, +z toward the thumb, the palm +y on the right hand and -y on the
        // left; the right arm's shoulder is right of the eye): turning about the palm's normal deviates, about the thumb's line flexes.
        var palm = shoulder.X >= 0f ? 1f : -1f;
        static Vector3 Across(Vector3 v) => new(v.X, v.Y, 0f);
        var deviate = Across(Vector3.Transform(Vector3.UnitY, _rest)); // +y turns the fingers (-x) toward the thumb (+z)
        _deviate = deviate.LengthSquared() > 1e-8f ? Vector3.Normalize(deviate) : Vector3.UnitX;
        _flex = Vector3.Cross(Vector3.UnitZ, _deviate);
        if (Vector3.Dot(_flex, Vector3.Transform(-palm * Vector3.UnitZ, _rest)) < 0f) _flex = -_flex; // about -z (right), +z (left): fingers toward the palm
    }

    // The wrist with the hand's IK target at `hand` turned `turn`, in degrees from the game's hold: Flexion (+ toward the palm, -
    // extension toward the back of the hand), Deviation (+ radial, toward the thumb; - ulnar) and Rotation (the forearm's twist,
    // pronation or supination). The hand's turn on the forearm from its rest pose is split into a twist about the forearm and a swing
    // across it (InspectArm.Wring), and the swing into its parts about the palm's normal and about the line across both.
    public (float Flexion, float Deviation, float Rotation) Joint(Vector3 hand, Quaternion turn, float swivel = 0f)
    {
        Split(hand, turn, swivel, out _, out var swing, out var twist);
        var s = Log(swing) * (180f / MathF.PI);
        return (Vector3.Dot(s, _flex), Vector3.Dot(s, _deviate), 2f * MathF.Atan2(twist.Z, twist.W) * 180f / MathF.PI);
    }


    static Vector3 Log(Quaternion q)
    {
        if (q.W < 0f) q = -q;
        var half = MathF.Acos(Math.Min(1f, q.W));
        var sin = MathF.Sin(half);
        return sin < 1e-6f ? Vector3.Zero : new Vector3(q.X, q.Y, q.Z) * (2f * half / sin);
    }

    public float Reach => (Upper + Fore) * 0.98f;

    // The point toward `hand` the arm reaches.
    public Vector3 Within(Vector3 hand)
    {
        var d = hand - Shoulder;
        var l = d.Length();
        return l <= Reach ? hand : Shoulder + d * (Reach / l);
    }

    // The bend plane's normal with the elbow swivelled `swivel` degrees.
    Vector3 Bend(Vector3 hand, float swivel)
    {
        if (swivel == 0f) return _bend;
        var d = hand - Shoulder;
        return d.LengthSquared() > 1e-10f ? Vector3.Transform(_bend, Quaternion.CreateFromAxisAngle(Vector3.Normalize(d), _out * swivel * MathF.PI / 180f)) : _bend;
    }

    public Vector3 Elbow(Vector3 hand, float swivel = 0f)
    {
        var d = hand - Shoulder;
        var l = Math.Clamp(d.Length(), Math.Abs(Upper - Fore) + 1e-4f, Upper + Fore - 1e-4f);
        var dn = Vector3.Normalize(d);
        var x = (Upper * Upper - Fore * Fore + l * l) / (2f * l);
        var h = MathF.Sqrt(MathF.Max(0f, Upper * Upper - x * x));
        var side = Vector3.Cross(dn, Bend(hand, swivel));
        return Shoulder + dn * x + (side.LengthSquared() > 1e-10f ? Vector3.Normalize(side) : Vector3.Zero) * h;
    }

    // The forearm's frame with the hand at `hand`: z along the forearm, x the bend plane's normal.
    public Quaternion Frame(Vector3 hand, float swivel = 0f)
    {
        var bend = Bend(hand, swivel);
        var z = Vector3.Normalize(hand - Elbow(hand, swivel));
        var x = bend - z * Vector3.Dot(bend, z);
        x = x.LengthSquared() > 1e-10f ? Vector3.Normalize(x) : Vector3.Normalize(Vector3.Cross(z, Vector3.UnitY));
        return InspectLook.Look(z, Vector3.Cross(z, x));
    }

    public Quaternion Comfort(Vector3 hand, Quaternion turn, float swivel = 0f)
    {
        Split(hand, turn, swivel, out var frame, out var swing, out var twist);
        return frame * Bent(swing) * Limit(twist, Twist) * _rest;
    }

    // How far the hand's line (the wrist to the knuckles) lies off the forearm's, in degrees: the fold of the wrist as it is seen,
    // whichever way it bends.
    public float Fold(Vector3 hand, Quaternion turn, float swivel = 0f) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(hand - Elbow(hand, swivel)), Vector3.Transform(-Vector3.UnitX, turn)), -1f, 1f)) * 180f / MathF.PI;

    // How many degrees past its limits a hand rotation asks the wrist (0 within them): how far Comfort turns it back.
    public float Past(Vector3 hand, Quaternion turn, float swivel = 0f) =>
        2f * MathF.Acos(Math.Min(1f, MathF.Abs(Quaternion.Dot(Comfort(hand, turn, swivel), Quaternion.Normalize(turn))))) * 180f / MathF.PI;

    // A bend of the wrist kept within its limits.
    Quaternion Bent(Quaternion swing)
    {
        var s = Log(swing) * (180f / MathF.PI);
        float f = Vector3.Dot(s, _flex) / Flexion, d = Vector3.Dot(s, _deviate);
        if (f < 0f) f *= Flexion / Extension;
        d /= d > 0f ? Radial : Ulnar;
        var past = MathF.Sqrt(f * f + d * d);
        return past <= 1f ? swing : Quaternion.CreateFromAxisAngle(Vector3.Normalize(s), s.Length() / past * (MathF.PI / 180f));
    }
    // How far a hand rotation turns the wrist from its rest pose: the twist about the forearm (signed, -180..180) and the bend, in degrees.
    public (float Twist, float Swing) Wring(Vector3 hand, Quaternion turn, float swivel = 0f)
    {
        Split(hand, turn, swivel, out _, out var swing, out var twist);
        return (2f * MathF.Atan2(twist.Z, twist.W) * 180f / MathF.PI, 2f * MathF.Acos(Math.Min(1f, MathF.Abs(swing.W))) * 180f / MathF.PI);
    }

    // A hand rotation as the wrist's turn from its rest pose in the forearm's frame, and back.
    public Quaternion Wrist(Vector3 hand, Quaternion turn, float swivel = 0f) => Wrist(Frame(hand, swivel), turn);

    Quaternion Wrist(Quaternion frame, Quaternion turn)
    {
        var wrist = Quaternion.Normalize(Quaternion.Inverse(frame) * turn * Quaternion.Inverse(_rest));
        return wrist.W < 0f ? -wrist : wrist;
    }

    public Quaternion Hand(Vector3 hand, Quaternion wrist, float swivel = 0f) => Frame(hand, swivel) * wrist * _rest;

    void Split(Vector3 hand, Quaternion turn, float swivel, out Quaternion frame, out Quaternion swing, out Quaternion twist)
    {
        frame = Frame(hand, swivel);
        var wrist = Wrist(frame, turn);
        twist = new Quaternion(0f, 0f, wrist.Z, wrist.W);
        twist = twist.LengthSquared() > 1e-10f ? Quaternion.Normalize(twist) : Quaternion.Identity;
        swing = wrist * Quaternion.Conjugate(twist);
    }

    static Quaternion Limit(Quaternion q, float degrees)
    {
        if (q.W < 0f) q = -q;
        var angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        var max = degrees * MathF.PI / 180f;
        if (angle <= max) return q;
        var axis = new Vector3(q.X, q.Y, q.Z);
        return axis.LengthSquared() > 1e-12f ? Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), max) : q;
    }

}

// The off hand's fingers as lines, for the look to stop them on what the hand holds (InspectLook.BakeFingers) and for the audit to find
// one inside the gun (rule F): per finger (thumb to pinky) its knuckle and the ends of its three bones in the hand's frame (the wrist
// node: -X toward the fingers, +Z toward the thumb, the left palm -Y), bent as ForgeCore bends them (ClipFingers.Turns on the
// canonical rig's left hand, from its rest). The bones' lengths and the knuckles' places are a nominal hand's (the canonical rig's):
// the game's own finger bones are not measured, so a finger stops within about a finger's width of where the player sees it stop.
public static class InspectHand
{
    public const int Steps = 20;
    // A finger's half thickness, and how far its middle line may lie inside a mesh's box before rule F reports it.
    public const float Radius = 0.008f, Inside = 0.004f;
    public static readonly string[] Names = ["thumb", "index finger", "middle finger", "ring finger", "little finger"];
    // Per finger: its knuckle from the wrist (along the fingers, toward the palm, toward the thumb) and its three bones' lengths.
    static readonly (float Along, float Palmward, float Across, float A, float B, float C)[] Size =
    [
        (0.042f, 0.013f, 0.027f, 0.056f, 0.041f, 0.03f), (0.11f, 0.01f, 0.047f, 0.06f, 0.036f, 0.024f), (0.124f, 0f, 0.017f, 0.056f, 0.038f, 0.025f),
        (0.12f, 0f, -0.01f, 0.055f, 0.032f, 0.023f), (0.11f, 0f, -0.033f, 0.044f, 0.025f, 0.02f),
    ];
    // The hand's frame in the canonical rig (along the fingers, away from the left palm, toward the thumb), and the fingers' lines per
    // whole bend step.
    static readonly (Vector3 Along, Vector3 Back, Vector3 Thumb) Frame = BuildFrame();
    static readonly Vector3[] Lines = Build();

    static (Vector3, Vector3, Vector3) BuildFrame()
    {
        int hand = Array.IndexOf(ForgeVisual.ClipRig.Names, "leftHand"), first = ForgeVisual.ClipRig.BodyCount;
        Vector3 f = ForgeVisual.ClipRig.RestDirection(hand, first + 6), t = ForgeVisual.ClipRig.RestDirection(first + 12, first + 3);
        t = Vector3.Normalize(t - f * Vector3.Dot(t, f));
        var n = Vector3.Cross(f, t);
        // The fingers close toward the palm, the left hand's -Y: the frame's third axis is the one that has them do so.
        Span<Quaternion> turns = stackalloc Quaternion[15];
        Span<Vector3> open = stackalloc Vector3[4], shut = stackalloc Vector3[4];
        ForgeVisual.ClipFingers.Turns(false, Quaternion.Identity, default, turns);
        Bones(1, turns, open, (f, n, t));
        ForgeVisual.ClipFingers.Turns(false, Quaternion.Identity, new ForgeVisual.FingerBend(1f, 1f, 1f, 1f, 1f), turns);
        Bones(1, turns, shut, (f, n, t));
        return shut[3].Y < open[3].Y ? (f, n, t) : (f, -n, t);
    }

    static Vector3[] Build()
    {
        var lines = new Vector3[5 * (Steps + 1) * 4];
        Span<Quaternion> turns = stackalloc Quaternion[15];
        for (var s = 0; s <= Steps; s++)
        {
            var b = s / (float)Steps;
            ForgeVisual.ClipFingers.Turns(false, Quaternion.Identity, new ForgeVisual.FingerBend(b, b, b, b, b), turns);
            for (var finger = 0; finger < 5; finger++) Bones(finger, turns, lines.AsSpan((finger * (Steps + 1) + s) * 4, 4), Frame);
        }
        return lines;
    }

    // Finger `finger`'s knuckle and its three bones' ends, its joints turned `turns` (ClipFingers.Turns of the left hand), in the frame.
    static void Bones(int finger, ReadOnlySpan<Quaternion> turns, Span<Vector3> into, (Vector3 Along, Vector3 Back, Vector3 Thumb) frame)
    {
        Vector3 InHand(Vector3 v) => new(-Vector3.Dot(v, frame.Along), Vector3.Dot(v, frame.Back), Vector3.Dot(v, frame.Thumb));
        var (j, size) = (ForgeVisual.ClipRig.BodyCount + finger * 3, Size[finger]);
        Vector3 d0 = ForgeVisual.ClipRig.RestDirection(j, j + 1), d1 = ForgeVisual.ClipRig.RestDirection(j + 1, j + 2);
        into[0] = new Vector3(-size.Along, -size.Palmward, size.Across);
        into[1] = into[0] + InHand(Vector3.Transform(d0, turns[finger * 3])) * size.A;
        into[2] = into[1] + InHand(Vector3.Transform(d1, turns[finger * 3 + 1])) * size.B;
        into[3] = into[2] + InHand(Vector3.Transform(d1, turns[finger * 3 + 2])) * size.C;
    }
    // A knuckle also bends back: a bend below 0, down to -Lift, turns the finger, straight, about its knuckle away from the palm, as
    // far for each part of it as the knuckle closes (ForgeCore's finger bends start at 0; Lifted gives the joints' turns). Per finger,
    // in the hand's frame: the axis its knuckle closes about and how far (radians) a whole bend turns it.
    public const float Lift = 0.3f;
    static readonly (Vector3 Axis, float Angle)[] Knuckles = [.. Enumerable.Range(0, 5).Select(Knuckle)];

    static (Vector3, float) Knuckle(int finger)
    {
        int open = finger * (Steps + 1) * 4, shut = open + Steps * 4;
        Vector3 a = Vector3.Normalize(Lines[open + 1] - Lines[open]), b = Vector3.Normalize(Lines[shut + 1] - Lines[shut]);
        return (Vector3.Normalize(Vector3.Cross(a, b)), MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)));
    }

    // Finger `finger` (thumb 0 to little finger 4) of a shape: by joint, or with the thumb's root turned (ForgeCore FingerBend), worked
    // out from the joints' turns; a whole bend from the table. Its knuckle and its three bones' ends into `into`, of the left hand or
    // (`right`) of the right.
    public static void Line(int finger, in ForgeVisual.FingerBend bend, Span<Vector3> into, bool right = false)
    {
        if (!bend.ByJoint && (finger != 0 || (bend.Spread == 0f && bend.Oppose == 0f)))
        {
            Line(finger, bend[finger], into, right);
            return;
        }
        Span<Quaternion> turns = stackalloc Quaternion[15];
        ForgeVisual.ClipFingers.Turns(false, Quaternion.Identity, bend, turns);
        Bones(finger, turns, into, Frame);
        if (right)
            for (var k = 0; k < 4; k++) into[k].Y = -into[k].Y;
    }

    // Finger `finger` (thumb 0 to little finger 4) bent `bend` (-Lift..1): its knuckle and its three bones' ends into `into`, of the
    // left hand or (`right`) of the right, its mirror (the palm +Y).
    public static void Line(int finger, float bend, Span<Vector3> into, bool right = false)
    {
        var x = Math.Clamp(bend, 0f, 1f) * Steps;
        var s = Math.Min((int)x, Steps - 1);
        var at = (finger * (Steps + 1) + s) * 4;
        for (var k = 0; k < 4; k++) into[k] = Vector3.Lerp(Lines[at + k], Lines[at + 4 + k], x - s);
        if (bend < 0f)
        {
            var back = Quaternion.CreateFromAxisAngle(Knuckles[finger].Axis, MathF.Max(bend, -Lift) * Knuckles[finger].Angle);
            for (var k = 1; k < 4; k++) into[k] = into[0] + Vector3.Transform(into[k] - into[0], back);
        }
        if (right)
            for (var k = 0; k < 4; k++) into[k].Y = -into[k].Y;
    }

    // The three joints' turns of finger `finger` of a hand with its knuckle bent back by `bend` (below 0), in the shape ClipFingers.Turns
    // gives them: all three the knuckle's turn at a whole bend, taken that share of the way the other way.
    public static void Lifted(bool right, int finger, float bend, Span<Quaternion> joints)
    {
        Span<Quaternion> turns = stackalloc Quaternion[15];
        ForgeVisual.ClipFingers.Turns(right, Quaternion.Identity, new ForgeVisual.FingerBend(finger == 0 ? 1f : 0f, finger == 1 ? 1f : 0f, finger == 2 ? 1f : 0f, finger == 3 ? 1f : 0f, finger == 4 ? 1f : 0f), turns);
        var whole = turns[finger * 3];
        if (whole.W < 0f) whole = Quaternion.Negate(whole);
        var axis = new Vector3(whole.X, whole.Y, whole.Z);
        var back = axis.LengthSquared() > 1e-12f ? Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), 2f * MathF.Acos(Math.Clamp(whole.W, -1f, 1f)) * MathF.Max(bend, -Lift)) : Quaternion.Identity;
        joints[0] = joints[1] = joints[2] = back;
    }
}

// What one look is solved from, measured when it starts, in view space: the item camera, the gun's drawn bounds (in the gun's own space),
// the game's pose of the gun and of both hands' IK targets, the two arms, the box of every drawn mesh of the gun (in its space; none: its
// bounds alone) and how its kind moves (InspectTable.Feel).
public struct InspectRig
{
    public float TanHalf, Aspect, Near;
    public Vector3 Centre, Extents;
    public Vector3 GunAt;
    public Quaternion Gun;
    public Vector3 GripAt, OffAt;
    public Quaternion Grip, Off;
    public InspectArm GripArm, OffArm;
    // Per part of the motion (InspectMotion.Parts), as found on this gun.
    public InspectPartRig[]? Parts;
    public InspectBox[]? Meshes;
    public InspectFeel Feel;
    // The world's up in view space as the look starts (zero: the view's up), which a tossed gun falls along.
    public Vector3 Up;

    // The length of a melee weapon is its own Y (head on +Y, as the game builds them), a gun's its Z. A look is solved with the length
    // as Z: this turn takes the look's space to such an item's own (Z to its Y, X kept).
    public static readonly Quaternion LengthAsY = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f);

    // This rig with the item's own space turned by `turn` (the look's space to the item's): its pose, bounds, mesh boxes and parts.
    public readonly InspectRig Turned(Quaternion turn)
    {
        var back = Quaternion.Inverse(turn);
        (Vector3 Lo, Vector3 Hi) Box(Vector3 lo, Vector3 hi)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (var i = 0; i < 8; i++)
            {
                var c = Vector3.Transform(new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z), back);
                (min, max) = (Vector3.Min(min, c), Vector3.Max(max, c));
            }
            return (min, max);
        }
        var rig = this;
        var (lo, hi) = Box(Centre - Extents, Centre + Extents);
        (rig.Centre, rig.Extents, rig.Gun) = ((lo + hi) / 2f, (hi - lo) / 2f, Quaternion.Normalize(Gun * turn));
        rig.Meshes = Meshes?.Select(m => { var b = Box(m.Lo, m.Hi); return m with { Lo = b.Lo, Hi = b.Hi }; }).ToArray();
        rig.Parts = Parts?.Select(q => { var b = Box(q.Lo, q.Hi); return q with { Pivot = Vector3.Transform(q.Pivot, back), Down = Vector3.Transform(q.Down, back), Lo = b.Lo, Hi = b.Hi }; }).ToArray();
        return rig;
    }
}

// One part of a motion as found on the gun, in the gun's space at rest: its pivot (the node's origin), its own down axis (where a
// magazine leaves its well), the box of its drawn meshes (Lo = Hi = Pivot when it draws none) and the earlier-measured part whose node
// it hangs under (-1: none), which it goes with.
public struct InspectPartRig
{
    public Vector3 Pivot, Down, Lo, Hi;
    public int Under;
}

// One drawn mesh's box in the gun's space at rest, and the part of the motion that moves it (-1: none).
public struct InspectBox
{
    public Vector3 Lo, Hi;
    public int Part;
}

// What one frame shows of the gun: Inside, the share of its length (back to front of its bounds) that lies in the view (a stock near the
// eye draws large, so a share of the drawn area would count the stock and not the gun); Cover, the share of the view inside the outline
// of its bounds (about twice what the gun itself covers); Depth, how far in front of the eye the nearest corner of its shown part is
// (metres); Centre and Shown, how far out its bounds centre and the farthest corner of its shown part (receiver to muzzle,
// InspectLook.Corner) project (1 the view's edge).
public readonly record struct InspectShot(float Inside, float Cover, float Depth, float Centre, float Shown)
{
    // What a key aims for (InspectLook.Short): most of the gun in the view, not filling it, its shown part not so near the eye it cannot
    // be read, its centre and its shown part in the view (the stock behind the hand may be cut, or pass the eye as in the game's hold).
    public const float MinInside = 0.65f, MaxCover = 0.75f, MinDepth = 0.15f;
    // What no frame may do: leave most of the gun out of the view, or bring its shown part nearer than this (or than the game's own hold
    // has it: a compact gun's receiver sits under 0.1 m from the eye there).
    public const float LeastInside = 0.5f, LeastDepth = 0.12f;

    // The rule this frame breaks, or null. A frame on the way to or from the game's pose (`fromGame`) is held to the poorer of the game's
    // pose and each rule (a rifle's stock reaches behind the eye there), give or take 0.1 of a share (the follow carries the gun a little
    // past the game's pose as it settles there, which on a gun the game shows only part of takes a few more hundredths) and 0.02 m.
    // A gun twirling on the trigger finger (`twirl`) may swing its far end out of the view as it goes round: it is watched from as near
    // as it is held, not from where every turn of it would fit.
    public string? Breaks(in InspectShot game, bool fromGame, bool twirl = false)
    {
        float share = fromGame ? 0.1f : 1e-3f, metres = fromGame ? 0.02f : 1e-3f;
        if (!twirl && Inside < (fromGame ? MathF.Min(LeastInside, game.Inside) : LeastInside) - share) return $"only {Inside:P0} of the gun is in the view";
        if (Depth < MathF.Min(LeastDepth, game.Depth) - metres) return $"the gun comes {Depth:0.00} m from the eye";
        return null;
    }
}

// One look solved for one gun in one pair of hands. Each gun key is turned into a pose of the gun in view space: the intent's turns are
// applied to the game's pose, and the grip hand (which stays on the gun) is placed, by a sweep of its arm's reach and its elbow's
// swivel, where its wrist holds that turn within its limits, the off hand reaches the places it touches about that key, the frame keeps
// the frame rules (InspectShot) with a margin, and the shown part of the gun (receiver to muzzle, Corner) needs the narrowest item
// camera view; that view is the key's (never narrower than the game's, at most WideDegrees). The first-person arms are short (about
// 0.6 m from the shoulder), so a rifle's front shows only in a somewhat wider item camera view: the gun and hands draw smaller, the
// world's camera is untouched. Every frame of the look is then sampled at 60 a second; a look whose frames break a rule has a Fault
// and is not played. The off hand's keys on the gun are points of the gun's bounds, so one motion fits every gun of a kind; between
// keys it moves on a smooth step and its wrist is kept within its limits the same way.
public sealed class InspectLook
{
    public const float WideDegrees = 75f;
    const float PalmSkin = 0.015f; // the palm's middle (Palm) lies this far off the surface the palm touches
    static readonly Vector3 AwayFromShoulder = new(0.02f, -0.42f, 0.22f);

    public InspectMotion Motion { get; }
    // Per gun key, the item camera's tangent of half its vertical field of view (the game's at a rest key).
    public float[] View { get; }
    readonly Vector3[] _gunAt;
    readonly Quaternion[] _gun;
    readonly Quaternion[] _wrist; // per gun key, the grip hand's wrist turn in its forearm's frame (InspectArm.Wrist)
    readonly Vector3[] _handAt;
    readonly Quaternion[] _hand;
    // Per gun key, the grip arm's elbow swivel in degrees (0 at a rest key).
    public float[] Swivel { get; }
    // Per gun key, how many degrees the grip wrist's limits held the gun back from the key's turn (rule W, Rules).
    public float[] KeyHeldBack { get; }
    readonly InspectArm _off, _arm;
    readonly Vector3 _gripAt; // the grip hand's target in the gun's space
    readonly Quaternion _grip;
    readonly Vector3[] _keyGripAt; // per gun key, where the grip hand holds the gun (InspectGrip)
    Vector3 _heldGripAt;           // where it holds it this frame (set by GunPose)
    readonly Vector3 _centre; // the gun's bounds centre, which a tossed gun turns about
    readonly Vector3 _trigger; // the trigger guard, which a twirled gun turns about (Trigger)
    // What a spin turns the item about, in its space: a gun's side axis; an item played from its hold (a melee weapon) the axis across
    // its length nearest the line of the grip hand's fingers, so it turns end over end in the plane across the fingers and the
    // forearm, whichever way the game holds it, and never sweeps through them. _spinUp: across both.
    readonly Vector3 _spin = Vector3.UnitX, _spinUp = Vector3.UnitY;
    readonly InspectRig _rig;

    // Real seconds per second of the motion (the kind's Slow), the look's length, and a time of the look as a time of the motion's keys.
    // Every public method of the look takes real seconds into the look.
    public float Slow { get; }
    public float Seconds => Motion.Seconds * Slow;
    public float MotionTime(float time) => time / Slow;
    readonly InspectFeel _feel;

    // The look's cues, by real seconds into it (InspectMotion.Cues).
    public (float Time, string Cue)[] Cues { get; }

    // The look of a motion in a rig: its magazine held by its side clasped (Clasp) where the clasp holds as solved, else pinched.
    // The first ClaspTries ranks of clasp (Clasped) are solved at once, and the best ranked whose solved look keeps its clasp
    // (ClaspBreaks) plays; the pinch is solved alongside, for a look none keeps. A look whose `clasp` key holds no clasp (a charging
    // handle the hand cannot take between its thumb and fingers) is Unplayable: the plugin plays another motion and says why.
    // A hand under, beside or on top of the gun (Support) wraps the gun's body there with the same clasp (SupportBox), each hold on its
    // own: one whose clasp breaks the solved look keeps its plain hold (said in SupportNote) and the look is solved again without it,
    // the other holds and the part's clasp untouched.
    // No clasp may make the off hand's fingers twitch more than the plain hold does (Plain, solved alongside; Twitch): a support hold
    // whose clasp twitches more than the plain hold over its keys keeps its plain hold, and a look whose clasps twitch more than the
    // plain look over the whole look plays the plain look; each said in SupportNote.
    public const int ClaspTries = 12;

    // The look with no clasp: every hand holds the gun as its keys say (a magazine or charging handle pinched, a hand under the gun laid
    // on it), as the looks before clasps were. Plays where a clasp twitches more, and on a press before the clasped look is solved.
    public static InspectLook Plain(InspectMotion motion, in InspectRig rig) => new(motion, rig, false, 0, SupportSlots(motion));

    static HashSet<int> SupportSlots(InspectMotion motion) => [.. Enumerable.Range(0, motion.Hand.Length).Where(h => Support(motion.Hand[h])).Select(h => motion.Parts.Length + h)];

    public static InspectLook Solve(InspectMotion motion, in InspectRig rig)
    {
        var on = rig;
        var dropped = new HashSet<int>();
        var notes = new List<string>();
        var ranks = motion.Hand.Any(h => h.Clasp || h.On == InspectHold.Mag) ? ClaspTries : 1;
        InspectLook? plain = null;
        while (true)
        {
            var drop = new HashSet<int>(dropped);
            var tries = new InspectLook?[ranks + 2];
            var why = new string?[ranks + 2];
            var holds = new Dictionary<int, string>?[ranks + 2];
            System.Threading.Tasks.Parallel.For(0, ranks + 2, i =>
            {
                if (i == ranks + 1) { if (plain == null) tries[i] = Plain(motion, on); return; }
                var look = new InspectLook(motion, on, i < ranks, i < ranks ? i : 0, drop);
                // (A rank past the last place that clasps a part holds nothing of its own: no look of it.)
                if (i > 0 && i < ranks && !look.PartClasped) return;
                var bare = -1;
                for (var h = 0; h < motion.Hand.Length && bare < 0 && i < ranks; h++) if (motion.Hand[h].Clasp && look._held[h] == null) bare = h;
                var broke = look.ClaspBreaks(holds[i] = []);
                (tries[i], why[i]) = (look, bare >= 0 ? look.MagNote ?? $"no clasp of the part at {motion.Hand[bare].Time:0.00} s" : broke is { } b ? $"the clasp {b}" : null);
            });
            plain ??= tries[ranks + 1]!;
            InspectLook? kept = null;
            for (var i = 0; i < ranks && kept == null; i++)
                if (tries[i] is { } look && why[i] == null && holds[i]!.Count == 0) kept = look;
            // (Where the best ranked place leaves the hand off what it holds, the place that leaves it closest.)
            if (kept != null && kept.WidestGap() is var keptGap && keptGap > SnugBy)
                for (var i = 0; i < ranks; i++)
                    if (tries[i] is { } look && look != kept && why[i] == null && holds[i]!.Count == 0 && look.WidestGap() is var gap && gap < keptGap - SnugBy)
                        (kept, keptGap) = (look, gap);
            if (kept != null)
            {
                if (kept.Calmer(plain, dropped, notes)) continue;
                return Noted(Snug(Calmest(kept, plain, notes), notes), notes);
            }
            var pinched = tries[ranks]!;
            // Every support hold whose clasp broke the best ranked look (or the pinch) keeps its plain hold, all at once.
            if ((tries[0] != null && holds[0]!.Count > 0 ? holds[0] : holds[ranks]) is { Count: > 0 } broken)
            {
                foreach (var (slot, broke) in broken)
                {
                    dropped.Add(slot);
                    var key = motion.Hand[slot - pinched.PartsLen];
                    notes.Add($"the hand {key.On.ToString().ToLowerInvariant()} the gun at {key.Time:0.00} s keeps its plain hold (the clasp {broke})");
                }
                continue;
            }
            var reason = why[0] ?? tries[0]?.MagNote;
            if (motion.Hand.Any(h => h.Clasp)) pinched.Unplayable = $"no place of the hand clasps the part its clasp keys hold and keeps the look's rules ({reason})";
            pinched.MagNote = ranks > 1 && reason != null && !reason.Contains("pinched") ? $"{reason}; the magazine is pinched instead" : reason ?? pinched.MagNote;
            if (pinched.Unplayable == null && pinched.Calmer(plain, dropped, notes)) continue;
            return Noted(pinched.Unplayable == null ? Snug(Calmest(pinched, plain, notes), notes) : pinched, notes);
        }
    }

    // Each support hold this look clasps whose keys' span twitches more than the plain look's (Twitches) goes into `dropped`, said in
    // `notes`; whether any did (the look is solved again without them).
    bool Calmer(InspectLook plain, HashSet<int> dropped, List<string> notes)
    {
        var any = false;
        for (var h = 0; h < Motion.Hand.Length; h++)
        {
            if (!Support(Motion.Hand[h]) || _held[h] == null) continue;
            float from = (h > 0 ? Motion.Hand[h - 1].Time : 0f) * Slow, to = (h + 1 < Motion.Hand.Length ? Motion.Hand[h + 1].Time : Motion.Seconds) * Slow;
            var (mine, theirs) = (Twitches(from, to), plain.Twitches(from, to));
            if (mine <= theirs) continue;
            dropped.Add(PartsLen + h);
            notes.Add($"the hand {Motion.Hand[h].On.ToString().ToLowerInvariant()} the gun at {Motion.Hand[h].Time:0.00} s keeps its plain hold (its clasp twitches the fingers {mine} times there, the plain hold {theirs})");
            any = true;
        }
        return any;
    }

    // The clasped look, unless it twitches the fingers more than the plain one does and the plain one lays them no deeper in the gun.
    static InspectLook Calmest(InspectLook look, InspectLook plain, List<string> notes)
    {
        var (mine, theirs) = (look.Twitches(0f, look.Seconds), plain.Twitches(0f, plain.Seconds));
        if (mine <= theirs) return look;
        var (deep, plainDeep) = (look.DeepestFinger(), plain.DeepestFinger());
        if (plainDeep > deep + InspectHand.Inside)
        {
            notes.Add($"the clasped look twitches the fingers {mine} times, the plain one {theirs}, which lays a finger {plainDeep * 100f:0.0} cm in the gun; the clasped look plays");
            return look;
        }
        notes.Add($"the clasped look twitches the fingers {mine} times, the plain one {theirs}; the plain look plays");
        return plain;
    }

    // How deep the off hand's fingers lie in the gun's meshes at most over the look (OffFingerSunk, at AuditTimes), in metres.
    float DeepestFinger()
    {
        var moved = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        var deepest = 0f;
        foreach (var time in AuditTimes())
        {
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            Moved(time, moved);
            OffPose(time, gunAt, gun, out var off, out var offTurn);
            deepest = MathF.Max(deepest, OffFingerSunk(time, off, offTurn, gunAt, gun, moved).Deep);
        }
        return deepest;
    }

    // How deep the grip hand's index finger lies in the gun at most while the hand holds it (IndexDeep, at AuditTimes), in metres.
    float IndexDeepest()
    {
        var moved = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        var deepest = 0f;
        foreach (var time in AuditTimes())
        {
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            if (Free) continue;
            GripPose(HeldAt, HeldTurn, out var grip, out var gripTurn);
            Moved(time, moved);
            if (IndexDeep(time, grip, gripTurn, gunAt, gun, moved) is var deep && deep > deepest) deepest = deep;
        }
        return deepest;
    }

    // How far the off hand lies at most from the part it holds while it holds one (ContactGap, at AuditTimes), in metres.
    float WidestGap()
    {
        var widest = 0f;
        foreach (var time in AuditTimes())
        {
            if (!HandOff(time) || ContactOf(time) < 0) continue;
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            OffPose(time, gunAt, gun, out var off, out var offTurn);
            if (ContactGap(time, off, offTurn, gunAt, gun) is var gap && gap > widest) widest = gap;
        }
        return widest;
    }

    // The look, or a twin whose fingers close back faster (SnugRates, the last holding nothing back) where the one with nothing held back
    // lays them shallower in the gun or the hand closer to what it holds: the slowest twin that lies as shallow and close as that one
    // plays. Holding a finger back keeps it from flicking, but must not leave it in the gun.
    static readonly float[] SnugRates = [2.5f, 4f, 6f, 9f, 14f, 20f, 26f, 32f, 40f, float.PositiveInfinity];

    static InspectLook Snug(InspectLook look, List<string> notes)
    {
        if (look.Unplayable != null) return look;
        var (deep, gap) = (look.DeepestFinger(), look.WidestGap());
        if (deep <= SnugBy && gap <= SnugBy) return look;
        InspectLook Twin(float rate) => new(look.Motion, look._rig, look._clasp, look._rank, look._dropped, rate);
        var snapped = Twin(float.PositiveInfinity);
        var (sDeep, sGap) = (snapped.DeepestFinger(), snapped.WidestGap());
        if (sDeep > deep + 1e-4f || sGap > gap + 1e-4f || (sDeep > deep - SnugBy && sGap > gap - SnugBy)) return look;
        // (The grip hand's index finger, the same in every twin, lies as deep anyway: a twin need lie no shallower than it.)
        var index = look.IndexDeepest();
        foreach (var rate in SnugRates)
        {
            var twin = float.IsPositiveInfinity(rate) ? snapped : Twin(rate);
            var (tDeep, tGap) = (twin.DeepestFinger(), twin.WidestGap());
            if (tDeep > MathF.Max(sDeep, index) + 1e-4f || tGap > sGap + 1e-4f) continue;
            notes.Add($"its fingers close back {(float.IsPositiveInfinity(rate) ? "at once" : $"{rate:0} shares a second")} (held back as fast as {FingerBack:0.0} they lie {deep * 100f:0.0} cm in the gun and {gap * 100f:0.0} cm off what the hand holds, so {tDeep * 100f:0.0} and {tGap * 100f:0.0})");
            return twin;
        }
        return look;
    }

    const float SnugBy = 0.001f;

    static InspectLook Noted(InspectLook look, List<string> notes)
    {
        notes.AddRange(look._unwrapped);
        look.SupportNote = notes.Count > 0 ? string.Join("; ", notes) : null;
        return look;
    }

    // Why a hand under, beside or on top of the gun keeps its plain hold (null: every one wraps the gun's body), logged once a gun and motion.
    public string? SupportNote { get; private set; }

    readonly List<string> _unwrapped = [];

    bool PartClasped => Enumerable.Range(0, _held.Length).Any(h => _held[h] != null && !Support(Motion.Hand[h]));

    // Why a look cannot play on this gun (null: it can): a part its `clasp` keys hold that no clasp of the hand takes (Solve).
    public string? Unplayable { get; private set; }

    readonly bool _clasp;

    // How fast the off hand's fingers close back, in shares of their bend a second (FingerBack; Snug's twins faster). Infinite (_snap):
    // they stop on the gun sample by sample with nothing held back, and nothing limits how fast the hand takes hold or hands its
    // fingers over.
    readonly float _backRate;
    bool _snap => float.IsPositiveInfinity(_backRate);

    InspectLook(InspectMotion motion, in InspectRig rig, bool clasp, int rank, IReadOnlySet<int> dropped, float backRate = FingerBack)
    {
        (_clasp, _rank, _dropped, _backRate) = (clasp, rank, dropped, backRate);
        Motion = motion;
        _feel = rig.Feel;
        Slow = rig.Feel.Slow > 0f ? rig.Feel.Slow : 1f;
        Cues = motion.Cues(Slow);
        var keys = motion.Gun;
        View = new float[keys.Length];
        Swivel = new float[keys.Length];
        KeyHeldBack = new float[keys.Length];
        _gunAt = new Vector3[keys.Length];
        _gun = new Quaternion[keys.Length];
        _wrist = new Quaternion[keys.Length];
        var toGun = Quaternion.Inverse(rig.Gun);
        var gripAt = Vector3.Transform(rig.GripAt - rig.GunAt, toGun);
        var grip = toGun * rig.Grip;
        (_gripAt, _grip, _centre, _rig) = (gripAt, grip, rig.Centre, rig);
        _heldGripAt = gripAt;
        _trigger = Trigger(rig, gripAt, grip);
        if (motion.FromHold && Vector3.Transform(Vector3.UnitX, grip) is var fingers && fingers.X * fingers.X + fingers.Y * fingers.Y > 0.04f)
            (_spin, _spinUp) = (Vector3.Normalize(new Vector3(fingers.X, fingers.Y, 0f)), Vector3.Normalize(new Vector3(-fingers.Y, fingers.X, 0f)));
        // Where the grip hand holds the gun at each key: the game's grip, or slid back under the stock (its wrist as low as at the grip).
        _keyGripAt = new Vector3[keys.Length];
        var back = rig.Centre.Z - rig.Extents.Z;
        var stock = back < gripAt.Z - StockBehind ? gripAt with { Y = gripAt.Y + StockRaise, Z = gripAt.Z + (back - gripAt.Z) * StockShare } : gripAt;
        for (var i = 0; i < keys.Length; i++) _keyGripAt[i] = keys[i].Hold == InspectGrip.Stock ? stock : gripAt;
        if (motion.Parts.Length > 0 && rig.Parts?.Length != motion.Parts.Length) throw new ArgumentException($"inspect {motion.Id}: the rig measured {rig.Parts?.Length ?? 0} parts of {motion.Parts.Length}");
        (_off, _arm) = (rig.OffArm, rig.GripArm);
        var hands = motion.Hand;
        _handAt = new Vector3[hands.Length];
        _hand = new Quaternion[hands.Length];
        var offAt = Vector3.Transform(rig.OffAt - rig.GunAt, toGun);
        var off = toGun * rig.Off;
        (_offAtGun, _offGun, _supportZ) = (offAt, off, new float[hands.Length]);
        for (var i = 0; i < hands.Length; i++) Anchor(hands[i], rig, offAt, off, out _handAt[i], out _hand[i]);
        _held = [.. hands.Select(h => ClaspOf(h)?.Shape)];
        for (var i = 0; i < hands.Length; i++)
            if (Support(hands[i]) && !_dropped.Contains(PartsLen + i) && _held[i] == null)
                _unwrapped.Add($"the hand {hands[i].On.ToString().ToLowerInvariant()} the gun at {hands[i].Time:0.00} s keeps its plain hold: no clasp lays the thumb on one side of the gun there and three fingers on the other, clear of its meshes");
        if (_held.Any(h => h != null)) FingerRate = ClaspFingerRate;
        if (_clasp && Array.FindIndex(hands, h => (MagSide(h) || h.Clasp) && Clasped(h.Part) == null) is var pinched and >= 0)
            MagNote = $"no clasp of the magazine held by its side at {hands[pinched].Time:0.00} s lays the thumb's pad on one face and three fingers' on the other; it is pinched instead";
        _palmFaces = Vector3.Transform(Vector3.Normalize(new Vector3(rig.Centre.X - offAt.X, rig.Centre.Y - offAt.Y, 0f)), Quaternion.Inverse(off));
        _handMoved = new (Quaternion, Vector3)[motion.Parts.Length];
        Array.Fill(_handMoved, (Quaternion.Identity, Vector3.Zero));
        _offForearmSunk = Sunk(_off.Elbow(rig.OffAt), rig.OffAt, rig.Off, rig.GunAt, rig.Gun, _handMoved, float.PositiveInfinity, palm: false);
        _offPalmSunk = MathF.Max(0f, PalmSkin - Gap(rig.OffAt + Vector3.Transform(Palm, rig.Off), rig.GunAt, rig.Gun, _handMoved));
        var touches = new List<Vector3>();
        for (var i = 0; i < keys.Length; i++)
        {
            View[i] = rig.TanHalf;
            if (keys[i].Rest) continue;
            // The off hand's places on the gun at its keys nearest in time to this one and either side of this key's time; a run of keys
            // holding one pose takes them all, so each of them is solved alike and the gun holds still while the hand moves over it.
            touches.Clear();
            var (from, to) = (i, i);
            while (from > 0 && Holds(keys[from - 1], keys[from])) from--;
            while (to + 1 < keys.Length && Holds(keys[to], keys[to + 1])) to++;
            for (var k = from; k <= to; k++)
            {
                motion.HandSpan(keys[k].Time, out var ha, out var hb, out _);
                for (var h = 0; h < hands.Length; h++)
                    if (OnGun(hands[h].On) && (Nearest(keys, hands[h].Time) == k || h == ha || h == hb) && !touches.Contains(_handAt[h])) touches.Add(_handAt[h]);
            }
            // A key next to one with another spin twirls the gun: every turn of it is weighed. The keys a toss leaves and lands on are
            // solved as held; its flight is held to the frame rules with the rest of the motion.
            var tossed = keys[i].Toss > 0f || (i < keys.Length - 1 && keys[i + 1].Toss > 0f);
            // (The game's pose turns to the whole turn nearest its neighbour, GunPose: a gun back from a whole flip is not turned again.)
            static bool Turns(in InspectGunKey a, in InspectGunKey b) => a.Rest || b.Rest
                ? MathF.Abs(MathF.IEEERemainder(a.Rest ? b.Spin : a.Spin, 360f)) > 1e-3f || MathF.Abs(MathF.IEEERemainder(a.Rest ? b.Flip : a.Flip, 360f)) > 1e-3f
                : a.Spin != b.Spin || a.Flip != b.Flip;
            var turning = !tossed && ((i > 0 && Turns(keys[i - 1], keys[i])) || (i < keys.Length - 1 && Turns(keys[i + 1], keys[i])));
            Solve(keys[i], motion.FromHold, turning, rig, _keyGripAt[i], grip, touches, keys.Any(k => k.Toss > 0f) ? TossMove : Move, out _gunAt[i], out _gun[i], out View[i], out Swivel[i], out KeyHeldBack[i]);
            _wrist[i] = _arm.Wrist(_gunAt[i] + Vector3.Transform(_keyGripAt[i], _gun[i]), _gun[i] * grip, Swivel[i]);
        }
        // Between two keys of its own the gun's centre may bow off the straight line (forward, down, out) when the straight way breaks a
        // frame rule (a long gun turning across the view swings its stock at the eye): the least bow whose frames keep the rules.
        _game = Shot(rig, rig.GunAt, rig.Gun, rig.TanHalf, gripAt);
        _bow = new Vector3[keys.Length];
        for (var i = 0; i + 1 < keys.Length; i++)
        {
            // (Not between two keys that hold one pose: the gun is shown there and holds still; nor into a toss: the gun flies there,
            // and a bowed hand would turn it with its forearm, about a leaning axis.)
            if ((keys[i].Rest && keys[i + 1].Rest) || Holds(keys[i], keys[i + 1]) || keys[i + 1].Toss > 0f || SpanBreaks(i, out _) < 0f) continue;
            var fewest = int.MaxValue;
            var chosen = Vector3.Zero;
            foreach (var bow in Bows)
            {
                _bow[i] = bow;
                var broken = SpanBreaks(i, out var count);
                if (broken < 0f) { chosen = bow; break; }
                if (count < fewest) (fewest, chosen) = (count, bow);
            }
            _bow[i] = chosen;
        }
        // The gun's pose follows the keys through a second-order system (the kind's feel), measured from here on.
        Follow();
        Presses(rig, offAt, off);
        Around();
        RideHolds();
        BakeSwivel();
        CarryFix();
        RideEnds();
        // Every frame of the look at 60 a second, against the bar of how far it has moved from the game's pose, and the rules of a look
        // that shows the gun in the hands (Rules).
        Fault = float.NaN;
        var last = default(Wrists);
        for (var f = 0; f * Step <= Seconds && float.IsNaN(Fault); f++)
            if ((Breaks(f * Step) ?? Wrings(f * Step, f == 0, ref last)) is { } broken) (Fault, FaultRule) = (f * Step, broken);
        if (float.IsNaN(Fault) && Rules(out var at) is { } rule) (Fault, FaultRule) = (at, rule);
        SolveIndex();
        BakeFingers();
    }

    // Both wrists as one frame leaves them: each hand's twist about its forearm (wound on from the first frame), its bend, and the grip
    // arm's elbow swivel, in degrees.
    struct Wrists { public float Grip, GripSwing, Swivel, Off, OffSwing, OffSwivel; }

    // Most a wrist's twist or bend or the grip elbow's swivel may move in a 60th of a second, and how far past its limit a forearm may wind.
    // (An off hand dropping out of view bends its wrist about 10 degrees a frame; a limit flipping across the half turn jumps 160.)
    public const float WringStep = 15f, WoundPast = 5f;

    // The wrist rule this frame breaks after the last, or null: no joint jumps from one frame to the next (a hand rotation crossing the
    // wrist's half turn flips the way it is limited), neither forearm winds further from its rest than the wrist's twist limit, and the
    // off hand is not turned off its hold past OffBack (rule W).
    string? Wrings(float time, bool first, ref Wrists last)
    {
        GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var at, out var turn, out var view);
        GripPose(HeldAt, HeldTurn, out var hand, out var handTurn);
        var grip = _arm.Wring(hand, handTurn, HeldSwivel);
        OffPose(time, at, turn, out var offAt, out var offTurn);
        if (HandOff(time) && OffHeldBack > OffBack && HandSeen(offAt, offTurn, view)) return OffTurnedOff();
        var off = _off.Wring(offAt, offTurn, OffSwivel);
        static float Turned(float from, float to) => to - from - MathF.Round((to - from) / 360f) * 360f;
        var now = first
            ? new Wrists { Grip = grip.Twist, Off = off.Twist }
            : new Wrists { Grip = last.Grip + Turned(last.Grip, grip.Twist), Off = last.Off + Turned(last.Off, off.Twist) };
        (now.GripSwing, now.Swivel, now.OffSwing, now.OffSwivel) = (grip.Swing, HeldSwivel, off.Swing, OffSwivel);
        var was = first ? now : last;
        last = now;
        var (step, joint) = (0f, "");
        foreach (var (moved, name) in new[] { (now.Grip - was.Grip, "grip wrist twists"), (now.GripSwing - was.GripSwing, "grip wrist bends"), (now.Swivel - was.Swivel, "grip elbow swings"),
            (now.Off - was.Off, "off wrist twists"), (now.OffSwing - was.OffSwing, "off wrist bends"), (now.OffSwivel - was.OffSwivel, "off elbow swings") })
            if (MathF.Abs(moved) > step) (step, joint) = (MathF.Abs(moved), name);
        if (step > WringStep) return $"W: a wrist or elbow turns {step:0} degrees in a 60th of a second (the {joint})";
        var wound = MathF.Max(MathF.Abs(now.Grip), MathF.Abs(now.Off));
        // (How far each wrist bends is rule W, Rules.)
        if (wound > InspectArm.Twist + WoundPast) return $"W: a forearm winds {wound:0} degrees from its rest";
        // (The off wrist bends past its limit only holding a carried part as its key turns it, HandPose.)
        return _off.Past(offAt, offTurn, OffSwivel) is var past && past > WoundPast ? $"W: the off wrist bends {past:0} degrees past its limits holding a carried part" : null;
    }

    readonly InspectShot _game;
    readonly Vector3[] _bow; // per gun key, how far the centre bows off the straight line to the next key at the middle of the way

    // The bows tried, least first: up to 0.25 m forward, 0.15 m down and 0.1 m to either side.
    static readonly Vector3[] Bows = (
        from x in new[] { 0f, -0.1f, 0.1f }
        from y in new[] { 0f, -0.05f, -0.1f, -0.15f }
        from z in new[] { 0f, 0.05f, 0.1f, 0.15f, 0.2f, 0.25f }
        select new Vector3(x, y, z)).OrderBy(v => v.Length()).ToArray();

    // The first sampled time in the way from gun key `key` to the next that breaks a rule (-1 when none) and how many do.
    float SpanBreaks(int key, out int count)
    {
        count = 0;
        var first = -1f;
        for (var time = Motion.Gun[key].Time * Slow; time <= Motion.Gun[key + 1].Time * Slow; time += Step)
            if (Breaks(time) != null) { count++; if (first < 0f) first = time; }
        return first;
    }

    // The frame rule the look breaks at `time`, or null.
    string? Breaks(float time)
    {
        var keys = Motion.Gun;
        GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var at, out var turn, out var view);
        Motion.GunSpan(time / Slow, out var a, out var b, out _);
        var broken = Shot(_rig, at, turn, view, _gripAt).Breaks(_game, keys[a].Rest || keys[b].Rest, Twirling);
        if (broken != null) return broken;
        // Both hands stay within their arms' reach of the gun: the grip hand where it holds it, an off hand on the gun where it touches
        // (on a part the motion's keys move, where they have moved it; a part the hand carries is taken where it sits in the gun).
        var hand = HeldAt + Vector3.Transform(_heldGripAt, HeldTurn);
        if (!keys[a].Rest && !keys[b].Rest && Vector3.Distance(hand, _rig.GripArm.Shoulder) > _rig.GripArm.Reach + 0.02f)
            return "the grip hand cannot reach the gun";
        Motion.HandSpan(time / Slow, out var ha, out var hb, out var hs);
        Vector3 Touched(int key)
        {
            var (place, held) = (_handAt[key], _hand[key]);
            if (Motion.Hand[key].Part is >= 0 and var part && Motion.Parts[part].Keys.All(k => k.Carry == null)) WithPart(key, time, 1f, ref place, ref held);
            return place;
        }
        if (OnGun(Motion.Hand[ha].On) && OnGun(Motion.Hand[hb].On)
            && Vector3.Distance(HeldAt + Vector3.Transform(Vector3.Lerp(Touched(ha), Touched(hb), hs), HeldTurn), _rig.OffArm.Shoulder) > _rig.OffArm.Reach + 0.02f)
            return "the off hand cannot reach the gun";
        return null;
    }
    const float Step = 1f / 60f;

    // The first time (sampled at 60 a second) a frame of the look breaks a frame rule (InspectShot), NaN when none does, and which rule.
    public float Fault { get; }
    public string? FaultRule { get; private set; }

    // What one frame shows of the gun with its bounds at `at`, `turn` in an item camera of tangent `view`.
    public static InspectShot Shot(in InspectRig rig, Vector3 at, Quaternion turn, float view, Vector3 gripAt)
    {
        float near = rig.Near, aspect = rig.Aspect;
        Vector2 Screen(Vector3 p) => new(p.X / (MathF.Max(p.Z, near) * view * aspect), p.Y / (MathF.Max(p.Z, near) * view));
        Span<Vector2> box = stackalloc Vector2[8];
        float depth = float.MaxValue, shown = 0f;
        Vector3 lo = rig.Centre - rig.Extents, hi = rig.Centre + rig.Extents;
        for (var i = 0; i < 8; i++)
        {
            var p = at + Vector3.Transform(new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z), turn);
            box[i] = Screen(p);
            var c = at + Vector3.Transform(Corner(rig, i, gripAt), turn);
            var q = Screen(c);
            depth = MathF.Min(depth, c.Z);
            shown = MathF.Max(shown, c.Z <= rig.Near ? float.MaxValue : MathF.Max(MathF.Abs(q.X), MathF.Abs(q.Y)));
        }
        // The share of the gun's length (its bounds' centre line, back to front) that is in the view.
        const int Along = 32;
        var inside = 0;
        for (var i = 0; i <= Along; i++)
        {
            var p = at + Vector3.Transform(rig.Centre + new Vector3(0f, 0f, rig.Extents.Z * (2f * i / Along - 1f)), turn);
            var q = Screen(p);
            if (p.Z > near && MathF.Abs(q.X) <= 1f && MathF.Abs(q.Y) <= 1f) inside++;
        }
        var centre = at + Vector3.Transform(rig.Centre, turn);
        var m = Screen(centre);
        return new InspectShot(inside / (Along + 1f), Area(Clip(Hull(box))) / 4f, depth, centre.Z <= near ? float.MaxValue : MathF.Max(MathF.Abs(m.X), MathF.Abs(m.Y)), shown);
    }

    // The convex hull of points, counter-clockwise (monotone chain).
    static List<Vector2> Hull(Span<Vector2> points)
    {
        var sorted = points.ToArray();
        Array.Sort(sorted, (p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Vector2>();
        for (var pass = 0; pass < 2; pass++)
        {
            var start = hull.Count;
            for (var i = 0; i < sorted.Length; i++)
            {
                var p = sorted[pass == 0 ? i : sorted.Length - 1 - i];
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0f) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    // A convex polygon cut to the view (-1..1 on both axes).
    static List<Vector2> Clip(List<Vector2> poly)
    {
        for (var edge = 0; edge < 4 && poly.Count > 0; edge++)
        {
            var axis = edge & 1;
            var sign = edge < 2 ? 1f : -1f;
            float Out(Vector2 p) => sign * (axis == 0 ? p.X : p.Y) - 1f; // > 0 past this edge
            var cut = new List<Vector2>();
            for (var i = 0; i < poly.Count; i++)
            {
                Vector2 p = poly[i], q = poly[(i + 1) % poly.Count];
                float dp = Out(p), dq = Out(q);
                if (dp <= 0f) cut.Add(p);
                if ((dp <= 0f) != (dq <= 0f)) cut.Add(p + (q - p) * (dp / (dp - dq)));
            }
            poly = cut;
        }
        return poly;
    }

    static float Area(List<Vector2> poly)
    {
        var area = 0f;
        for (var i = 0; i < poly.Count; i++) area += poly[i].X * poly[(i + 1) % poly.Count].Y - poly[(i + 1) % poly.Count].X * poly[i].Y;
        return MathF.Abs(area) * 0.5f;
    }

    static bool OnGun(InspectHold on) => on is not (InspectHold.Grip or InspectHold.Away);

    static int Nearest(InspectGunKey[] keys, float time)
    {
        var best = -1;
        for (var i = 0; i < keys.Length; i++)
            if (!keys[i].Rest && (best < 0 || MathF.Abs(keys[i].Time - time) < MathF.Abs(keys[best].Time - time))) best = i;
        return best;
    }

    // The rotation that turns z to `forward` and y toward `up` (Unity's LookRotation).
    public static Quaternion Look(Vector3 forward, Vector3 up)
    {
        var z = Vector3.Normalize(forward);
        var x = Vector3.Normalize(Vector3.Cross(up, z));
        var y = Vector3.Cross(z, x);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f)));
    }

    // The gun's pose at `time` (real seconds into the look; `weight` 1: the motion's, 0: the game's), given the game's pose this frame,
    // and the item camera's tangent of half its field of view (`rest` the game's). The keyed pose (Keyed) goes through the second-order
    // follow (Follow), then a toss's or a twirl's own motion is laid over it.
    public void GunPose(float time, float weight, Vector3 liveAt, Quaternion live, float rest, out Vector3 at, out Quaternion turn, out float view)
    {
        Posed(time, weight, liveAt, live, rest, out at, out turn, out view);
        (_poseAt, _poseTurn, _poseView) = (at, turn, view);
    }

    // The gun as the grip hand holds it at `time`: the keyed pose (Keyed) through the follow, before a toss or a twirl.
    void Held(float time, float weight, Vector3 liveAt, Quaternion live, float rest, out Vector3 pin, out Vector3 at, out Quaternion turn, out float view, out int a, out int b, out float s)
    {
        Keyed(time, weight, liveAt, live, rest, out pin, out at, out turn, out view, out a, out b, out s);
        if (_followAt == null) return;
        Followed(time, out var dp, out var dq);
        var grip = at + Vector3.Transform(_gripAt, turn);
        turn = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, dq, weight) * turn);
        at = grip + dp * weight - Vector3.Transform(_gripAt, turn);
    }

    // How many degrees the off wrist's limits turned the off hand from the turn its hold asks on the last off hand pose (rule W, Rules).
    public float OffHeldBack { get; private set; }
    // Where the last GunPose put the gun (a carried part is placed against it).
    Vector3 _poseAt;
    float _poseView;
    Quaternion _poseTurn = Quaternion.Identity;

    void Posed(float time, float weight, Vector3 liveAt, Quaternion live, float rest, out Vector3 at, out Quaternion turn, out float view)
    {
        Held(time, weight, liveAt, live, rest, out var pin, out at, out turn, out view, out var a, out var b, out var s);
        (HeldAt, HeldTurn) = (at, turn);
        Free = Twirling = Beating = false;
        var tossed = TossAt(time);
        if (tossed >= 0) { Toss(tossed, time, weight, pin, liveAt, live, rest, ref at, ref turn); return; }
        InspectGunKey ga = Motion.Gun[a], gb = Motion.Gun[b];
        // A game's-pose key spins to the whole turn nearest its neighbour, so a gun spun round is not wound back; an interruption fades the
        // spin from its nearest whole turn. A twirl turns on the way between its keys (GunSpan), as the rest of the pose does.
        static float Wind(bool rest, float value, float other) => rest ? MathF.Round(other / 360f) * 360f : value;
        static float Fade(float angle, float weight) => (angle - MathF.Round(angle / 360f) * 360f) * weight;
        var spin = Wind(ga.Rest, ga.Spin, gb.Spin) + (Wind(gb.Rest, gb.Spin, ga.Spin) - Wind(ga.Rest, ga.Spin, gb.Spin)) * s;
        var flip = Wind(ga.Rest, ga.Flip, gb.Flip) + (Wind(gb.Rest, gb.Flip, ga.Flip) - Wind(ga.Rest, ga.Flip, gb.Flip)) * s;
        if (weight < 1f) (spin, flip) = (Fade(spin, weight), Fade(flip, weight));
        // At a whole turn the gun is back in the hand's hold.
        static bool Turned(float angle) => angle != MathF.Round(angle / 360f) * 360f;
        Free = Turned(spin) || Turned(flip);
        if (!Free) return;
        const float Degrees = MathF.PI / 180f;
        // A spin outside a toss is a twirl on the trigger finger: the gun turns about its trigger guard, and the hand goes with it. The
        // finger carries the guard round a small circle in the plane of the spin and the wrist rocks once a turn, both back where they held
        // the gun at every whole turn, so the hand takes the grip again without a jump.
        Twirling = true;
        var pivot = at + Vector3.Transform(_trigger, turn);
        var phase = spin * Degrees;
        pivot += Vector3.Transform(_spinUp * (TwirlRadius * MathF.Sin(phase)) + Vector3.UnitZ * (TwirlRadius * (1f - MathF.Cos(phase))), turn);
        HeldTurn = Quaternion.Normalize(turn * Quaternion.CreateFromAxisAngle(_spin, TwirlRock * Degrees * MathF.Sin(phase)));
        HeldAt = pivot - Vector3.Transform(_trigger, HeldTurn);
        turn = Quaternion.Normalize(turn * Quaternion.CreateFromAxisAngle(_spin, phase) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, flip * Degrees));
        at = pivot - Vector3.Transform(_trigger, turn);
    }

    // The pose the keys give at `time` before the follow: the grip point it turns about (`pin`, the gun's space), the gun keys around it and
    // the share of the way between them.
    void Keyed(float time, float weight, Vector3 liveAt, Quaternion live, float rest, out Vector3 pin, out Vector3 at, out Quaternion turn, out float view, out int a, out int b, out float s)
    {
        Motion.GunSpan(time / Slow, out a, out b, out s);
        var ka = Motion.Gun[a].Rest;
        var kb = Motion.Gun[b].Rest;
        // The way into a toss's catch: the gun stays at the key before until it leaves the hand and goes evenly to the catch's over its flight.
        if (a != b && Motion.Gun[b].Toss > 0f)
        {
            var (release, flight) = Flight(b);
            s = Math.Clamp((time - release) / flight, 0f, 1f);
        }
        // The bounds centre moves on a straight line between keys (so it stays in the view between two keys that show it) while the gun
        // turns about it; to and from the game's pose the back of the gun does (so the stock leaves the eye and comes back without
        // swinging nearer).
        // A gun with nothing behind the grip hand to speak of (a pistol) has no stock to keep from the eye: its hand goes straight.
        var back = _centre - new Vector3(0f, 0f, _rig.Extents.Z);
        pin = (ka || kb) && back.Z < _gripAt.Z - StockBehind ? back : _gripAt;
        // Between two keys of its own whose grip hand holds the gun at different places, the hand slides along it: it goes straight from
        // its place at one key to its place at the other while the place on the gun moves between them.
        Vector3 pinA = ka || kb ? pin : _keyGripAt[a], pinB = ka || kb ? pin : _keyGripAt[b];
        pin = pinA == pinB ? pinA : Vector3.Lerp(pinA, pinB, s);
        var liveMid = liveAt + Vector3.Transform(pin, live);
        // A windup (s below 0) draws back the turn only: the hand stays where it starts, as drawing it back along the way could take
        // the gun out of the view. A settle (s past 1) carries the hand past the key and back, with no bow turned inside out.
        var bowed = Math.Clamp(s, 0f, 1f);
        var mid = Vector3.Lerp(ka ? liveMid : _gunAt[a] + Vector3.Transform(pinA, _gun[a]), kb ? liveMid : _gunAt[b] + Vector3.Transform(pinB, _gun[b]), MathF.Max(s, 0f)) + 4f * bowed * (1f - bowed) * _bow[a];
        _heldGripAt = ka || kb ? _gripAt : pin;
        turn = Quaternion.Slerp(ka ? live : _gun[a], kb ? live : _gun[b], s);
        // Between two keys of the motion's own the gun turns with the grip hand's wrist, which goes between the keys' wrist turns the
        // short way in its forearm's frame (the grip hand is at the centre line there): the short way in view space can wind the forearm
        // a whole turn round while the arm swings.
        var swivel = (ka ? 0f : Swivel[a]) + ((kb ? 0f : Swivel[b]) - (ka ? 0f : Swivel[a])) * s;
        if (!ka && !kb) turn = Quaternion.Normalize(_arm.Hand(mid, Quaternion.Slerp(_wrist[a], _wrist[b], s), swivel) * Quaternion.Inverse(_grip));
        view = (ka ? rest : View[a]) + ((kb ? rest : View[b]) - (ka ? rest : View[a])) * s;
        mid = Vector3.Lerp(liveMid, mid, weight);
        turn = Quaternion.Slerp(live, turn, weight);
        at = mid - Vector3.Transform(pin, turn);
        view = rest + (view - rest) * weight;
        HeldSwivel = swivel * weight;
        InspectGunKey ga = Motion.Gun[a], gb = Motion.Gun[b];
        // Rise lifts the gun in both hands (a heft).
        at.Y += (ga.Rise + (gb.Rise - ga.Rise) * s) * weight;
        // Between two keys of the motion's own the item camera widens as far as this frame needs to keep the shown part in the view (not
        // in a twirl or a toss: their keys' view holds through them; keys that let the gun past the view, Fill over 1, let it here too).
        if (!ka && !kb && !Motion.IsTwirl(b) && gb.Toss <= 0f && TossAt(time) < 0)
            view = MathF.Max(view, rest + (ViewFor(Whole with { Fill = MathF.Max(1f, MathF.Max(ga.Fill, gb.Fill)) }, _rig, turn, at, _gripAt) - rest) * weight);
    }

    // When gun key `key`'s toss leaves the hand (real seconds into the look) and how long it is in the air.
    (float Release, float Flight) Flight(int key)
    {
        var flight = InspectMotion.TossFlight(Motion.Gun[key].Toss, Motion.Gun[key].Gravity);
        return (Motion.Gun[key].Time * Slow - flight, flight);
    }

    // The gun key whose toss is playing at `time` (from its windup to the end of its sink), or -1.
    int TossAt(float time)
    {
        var keys = Motion.Gun;
        for (var k = 1; k < keys.Length; k++)
        {
            if (keys[k].Toss <= 0f) continue;
            var (release, _) = Flight(k);
            if (time >= release - InspectMotion.TossLead && time < keys[k].Time * Slow + TossSink) return k;
        }
        return -1;
    }

    // A toss, from the hand's view: it sinks TossDip and cocks the wrist back TossCock degrees, throws up to TossOver above its place (the
    // wrist whipping TossWhip forward) and lets the gun go there at the gun's own speed, follows through TossFollowUp higher and comes back
    // within TossFollow, lifts TossRise to meet the gun TossMeet before it lands and takes it at its speed, then sinks with it on a
    // second-order spring (TossSinkHz, TossSinkZeta) over TossSink, the wrist pressing the muzzle down TossPress degrees a metre of sink.
    // The gun turns in the fingers over TossRide before it leaves and after it lands (its rate rising from and falling to nothing), and
    // evenly in the air: its whole turns spread over the flight and the two rides.
    const float TossRide = 0.07f, TossThrow = 0.14f, TossDip = 0.04f, TossCock = 12f, TossWhip = 10f, TossOver = 0.02f, TossFollowUp = 0.02f,
        TossFollow = 0.15f, TossMeet = 0.09f, TossRise = 0.015f, TossSinkHz = 3f, TossSinkZeta = 0.5f, TossSink = 0.5f, TossPress = 150f,
        TossKnee = 0.01f, TossAside = 0.05f;

    // Set by GunPose: whether a toss's hand is at work this frame (wind-up to sink).
    public bool Beating { get; private set; }

    void Toss(int key, float time, float weight, Vector3 pin, Vector3 liveAt, Quaternion live, float rest, ref Vector3 at, ref Quaternion turn)
    {
        const float Degrees = MathF.PI / 180f;
        InspectGunKey from = Motion.Gun[key - 1], to = Motion.Gun[key];
        var (release, flight) = Flight(key);
        float v0 = MathF.Sqrt(2f * to.Gravity * to.Toss), tau = time - release, after = time - to.Time * Slow;
        float spins = to.Spin - from.Spin, flips = to.Flip - from.Flip, ws = spins / (flight + TossRide), wf = flips / (flight + TossRide);
        Beating = true;
        HandBeat(tau, after, flight, v0, out var dy, out var pitch);
        var up = _rig.Up.LengthSquared() > 1e-6f ? Vector3.Normalize(_rig.Up) : Vector3.UnitY;
        var grip = at + Vector3.Transform(pin, turn);
        var keyed = turn;
        Quaternion Wrist(float degrees) => Quaternion.Normalize(keyed * Quaternion.CreateFromAxisAngle(_spin, degrees * weight * Degrees));
        Quaternion Turned(Quaternion held, float spin, float flip) => Quaternion.Normalize(held * Quaternion.CreateFromAxisAngle(_spin, (from.Spin + spin) * Degrees) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (from.Flip + flip) * Degrees));
        // (The wrist cocks and whips only as far as it has left: all of it within its limits, less toward their end, WristKept.)
        if (pitch != 0f) pitch *= WristKept(at, keyed, up * (dy * weight), Quaternion.Normalize(Wrist(pitch) * Quaternion.Inverse(keyed)));
        HeldTurn = Wrist(pitch);
        HeldAt = grip + up * (dy * weight) - Vector3.Transform(pin, HeldTurn);
        // The hand going up and down with the throw swings its forearm under it: the wrist keeps within its limits there too (the hand
        // turned back about where it holds the gun).
        var handAt = HeldAt + Vector3.Transform(_heldGripAt, HeldTurn);
        HeldTurn = Quaternion.Normalize(_arm.Comfort(handAt, HeldTurn * _grip, HeldSwivel) * Quaternion.Inverse(_grip));
        HeldAt = handAt - Vector3.Transform(_heldGripAt, HeldTurn);
        Quaternion gun;
        Vector3 gunAt;
        if (tau >= 0f && after < 0f)
        {
            // In the air: the centre on one parabola under gravity along the world's up, from where it leaves the hand (the held pose at
            // the release) to where it lands (the held pose at the catch), both fixed for the flight, turning evenly about itself.
            var q = tau / flight;
            Vector3 Centre(float when, float whip, float spin, float flip)
            {
                Held(when, weight, liveAt, live, rest, out var p, out var held, out var held2, out _, out _, out _, out _);
                var hand = Quaternion.Normalize(held2 * Quaternion.CreateFromAxisAngle(_spin, whip * weight * Degrees));
                var g = Turned(hand, spin, flip);
                return held + Vector3.Transform(p, held2) + up * TossOver - Vector3.Transform(p, g) + Vector3.Transform(_centre, g);
            }
            var left = Centre(release, TossWhip, ws * TossRide / 2f, wf * TossRide / 2f);
            var lands = Centre(to.Time * Slow, 0f, spins - ws * TossRide / 2f, flips - wf * TossRide / 2f);
            gun = Turned(Wrist(TossWhip * (1f - q)), ws * (TossRide / 2f + tau), wf * (TossRide / 2f + tau));
            gunAt = Vector3.Lerp(left, lands, q) + up * (v0 * tau - 0.5f * to.Gravity * tau * tau) - Vector3.Transform(_centre, gun);
            Free = true;
        }
        else
        {
            // In the fingers: the gun turns about the grip as it leaves and as it lands.
            float x, rideS, rideF;
            if (tau < 0f) { x = MathF.Max(0f, tau + TossRide); (rideS, rideF) = (ws * x * x / (2f * TossRide), wf * x * x / (2f * TossRide)); }
            else
            {
                x = MathF.Min(after, TossRide);
                var turned = x - x * x / (2f * TossRide) - TossRide / 2f;
                (rideS, rideF) = (spins + ws * turned, flips + wf * turned);
            }
            gun = Turned(HeldTurn, rideS, rideF);
            gunAt = HeldAt + Vector3.Transform(pin, HeldTurn) - Vector3.Transform(pin, gun);
            Free = tau >= -TossRide && after < TossRide;
        }
        if (weight < 1f) (gun, gunAt) = (Quaternion.Slerp(HeldTurn, gun, weight), Vector3.Lerp(HeldAt, gunAt, weight));
        (at, turn) = (gunAt, gun);
        // A flip (a roll about the gun's length) comes round under the gun as it lands: the open hand waits beside where the gun will
        // be (TossAside off the side it holds it from, from TossFollow after the gun left) and closes on it while it stops turning
        // (TossRide). A hand waiting at the grip would have the gun's underside go through its wrist on the last of the turn.
        if (flips == 0f || tau < 0f) return;
        var aside = after < 0f ? Smooth(tau / TossFollow) : 1f - Smooth(after / TossRide);
        HeldAt += Vector3.Transform(new Vector3(pin.X < 0f ? -TossAside : TossAside, 0f, 0f), HeldTurn) * (aside * weight);
    }

    // The grip hand of a toss: how far up from its keyed place (metres) and how far its wrist presses the muzzle down (degrees), at `tau`
    // seconds after the gun leaves it and `after` seconds after it lands.
    static void HandBeat(float tau, float after, float flight, float v0, out float dy, out float pitch)
    {
        static float Smooth(float x) => x * x * (3f - 2f * x);
        static float Hermite(float p0, float m0, float p1, float m1, float d, float u)
        {
            float u2 = u * u, u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * d * m0 + (3f * u2 - 2f * u3) * p1 + (u3 - u2) * d * m1;
        }
        (dy, pitch) = (0f, 0f);
        if (after >= 0f)
        {
            if (after >= TossSink) return;
            float wn = 2f * MathF.PI * TossSinkHz, wd = wn * MathF.Sqrt(1f - TossSinkZeta * TossSinkZeta);
            dy = MathF.Exp(-TossSinkZeta * wn * after) * (TossOver * MathF.Cos(wd * after) + (-v0 + TossSinkZeta * wn * TossOver) / wd * MathF.Sin(wd * after));
            if (after > TossSink - 0.1f) dy *= Smooth((TossSink - after) / 0.1f);
            // From nothing and at no rate where the hand sinks below where it took the gun (x²/(x+knee)), not from a corner.
            var sunk = MathF.Max(0f, -dy);
            pitch = sunk * sunk / (sunk + TossKnee) * TossPress;
            return;
        }
        if (tau < -InspectMotion.TossLead) return;
        if (tau < -TossThrow)
        {
            var p = Smooth((tau + InspectMotion.TossLead) / (InspectMotion.TossLead - TossThrow));
            (dy, pitch) = (-TossDip * p, -TossCock * p);
            return;
        }
        if (tau < 0f)
        {
            var u = (tau + TossThrow) / TossThrow;
            (dy, pitch) = (Hermite(-TossDip, 0f, TossOver, v0, TossThrow, u), -TossCock + (TossCock + TossWhip) * Smooth(u));
            return;
        }
        pitch = TossWhip * (1f - Smooth(MathF.Min(1f, tau / TossFollow)));
        float rise = 2f * TossFollowUp / v0, meet = MathF.Max(TossFollow, flight - TossMeet), peak = MathF.Max(meet, flight - 0.03f);
        dy = tau < rise ? Hermite(TossOver, v0, TossOver + TossFollowUp, 0f, rise, tau / rise)
            : tau < TossFollow ? Hermite(TossOver + TossFollowUp, 0f, TossOver, 0f, TossFollow - rise, (tau - rise) / (TossFollow - rise))
            : tau < meet ? TossOver
            : tau < peak ? Hermite(TossOver, 0f, TossOver + TossRise, 0f, peak - meet, (tau - meet) / (peak - meet))
            : Hermite(TossOver + TossRise, 0f, TossOver, -v0, flight - peak, (tau - peak) / (flight - peak));
    }

    // A toss, from the grip hand's fingers (all alike): how far they are open, from the game's grip (0) to an open hand (1). They
    // open as the hand throws (from TossOpen before the gun leaves to as long after), stay open under the gun in the air and until it
    // has landed and stopped turning in the hand (TossRide), and only then close on it (over TossClose).
    const float TossOpen = 0.04f, TossClose = 0.16f;

    float TossFingers(float time)
    {
        var key = TossAt(time);
        if (key < 0) return 0f;
        var (release, _) = Flight(key);
        var after = time - Motion.Gun[key].Time * Slow;
        return after >= 0f ? 1f - Smooth((after - TossRide) / TossClose) : Smooth((time - release + TossOpen) / (2f * TossOpen));
    }

    // The grip hand's fingers, the same for every motion. The thumb and the three fingers round the grip keep the game's own grip
    // while the hand holds the gun (the one pose known to lie on this gun's grip: a grip sits inside its frame's box, so the look has
    // no surface there to stop a shape of its own on), and the look only opens them from it, toward an open hand (Opened), for a gun
    // that leaves the hand: on a twirl they open to let the gun round (within TwirlOpen) and close on it over the last TwirlClose of
    // the spin; on a toss all five open and close (TossFingers). The index finger comes off the trigger for the look (IndexBend: laid
    // along the gun's side, over IndexTime after the look starts and back before it ends), goes back into the guard IndexGuard before
    // a twirl, which turns the gun on it, until as long after, and opens with the others round a toss. `bend` is the shape, the
    // result how far the thumb and the three fingers are taken toward it, `index` how far the index finger is and `indexBend` its
    // own bend (InspectHand: below 0 its knuckle bent back).
    const float TwirlOpen = 0.12f, TwirlClose = 0.14f, IndexTime = 0.25f, IndexGuard = 0.2f;
    static readonly ForgeVisual.FingerBend Opened = new(0.15f, 0.35f, 0.2f, 0.25f, 0.3f);

    float GripFingers(float time, out ForgeVisual.FingerBend bend, out float index, out float indexBend)
    {
        float open = 0f, along = float.IsNaN(IndexBend) ? 0f : Smooth(time / IndexTime) * Smooth((Seconds - time) / IndexTime);
        var keys = Motion.Gun;
        for (var k = 1; k < keys.Length; k++)
        {
            if (!Motion.IsTwirl(k)) continue;
            float from = keys[k - 1].Time * Slow, to = keys[k].Time * Slow;
            if (time > from && time < to) open = MathF.Max(open, Smooth((time - from) / TwirlOpen) * Smooth((to - time) / TwirlClose));
            along *= 1f - Smooth((time - (from - IndexGuard - IndexTime)) / IndexTime) * Smooth((to + IndexGuard + IndexTime - time) / IndexTime);
        }
        var toss = TossFingers(time);
        index = MathF.Max(along, toss);
        float held = along * (1f - toss), share = held + toss;
        indexBend = share > 0f ? ((float.IsNaN(IndexBend) ? 0f : IndexBend) * held + Opened.Index * toss) / share : Opened.Index;
        bend = new ForgeVisual.FingerBend(Opened.Thumb, Math.Clamp(indexBend, 0f, 1f), Opened.Middle, Opened.Ring, Opened.Pinky);
        return MathF.Max(open, toss);
    }

    // The grip hand's index finger off the trigger: the bend (InspectHand, the right hand's; below 0 the knuckle bent back) that lays
    // it along the side of the gun, outside the trigger guard. NaN: the look leaves it on the game's grip (an item not held as a gun
    // is, a motion from the hold; or a hand that does not hold the gun from its side, IndexFault says so).
    // The hand does not move on the gun it holds, so it is solved once, in the game's hold. Only the boxes of the gun's meshes are
    // known and the hand is a nominal one, flat, whose knuckle lies a centimetre or two inside the box of a grip the real hand is
    // wrapped round; so the surface is taken from the knuckle itself: the game's grip has it on the gun, and the boxes count from as
    // deep as its skin lies in them (IndexShift). The finger starts where it lies flat, parallel to the gun's side (the side is flat
    // where most guns are held), and from IndexGive more open than that (its knuckle bent back, InspectHand.Lift at most) it closes
    // until it comes to a mesh no part of the motion moves (a cylinder that swings out leaves the finger on the frame behind it), at
    // most IndexGive past flat. So a receiver wider than the grip lifts it and a narrower frame takes it in, each by at most IndexGive
    // (6 degrees at the knuckle, about a centimetre at the fingertip): a box is a mesh's whole extent, a bolt handle or a port cover
    // included, and a finger that followed the boxes further would stand off the gun or curl into the guard.
    public const float IndexGive = 0.08f, IndexMost = 0.35f;
    public float IndexBend { get; private set; } = float.NaN;
    // What the look found about that finger that the player would see (null: nothing): for the log, once per gun and motion.
    public string? IndexFault { get; private set; }
    // Why a magazine held by its side is pinched, not clasped (Clasp, Solve; null: it is not): for the log, once per gun and motion.
    public string? MagNote { get; private set; }
    float _indexShift, _indexOff;

    void SolveIndex()
    {
        if (Motion.FromHold || _rig.Meshes is not { Length: > 0 }) return;
        var hand = _rig.Gun * _grip;
        var across = Vector3.Transform(Vector3.UnitY, _grip).X; // the right palm (+Y) faces the gun's side it lies on
        if (MathF.Abs(across) < 0.5f) { IndexFault = "the grip hand does not hold this gun from its side: its index finger keeps the game's grip"; return; }
        var still = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        GunPose(0f, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out _, out _, out _);
        Moved(0f, still);
        Span<Vector3> line = stackalloc Vector3[4];
        InspectHand.Line(1, 0f, line, right: true);
        _indexShift = MathF.Max(0f, InspectHand.Radius - Gap(_rig.GripAt + Vector3.Transform(line[0], hand), _rig.GunAt, _rig.Gun, still));
        const float step = 0.005f;
        var flat = -InspectHand.Lift;
        for (var b = flat; b <= IndexMost; b += step)
        {
            InspectHand.Line(1, b, line, right: true);
            if (Vector3.Transform(line[3] - line[0], _grip).X * -MathF.Sign(across) <= 0f) break;
            flat = b;
        }
        var (bend, most) = (MathF.Max(flat - IndexGive, -InspectHand.Lift), MathF.Min(flat + IndexGive, IndexMost));
        for (var b = bend; b <= most + 1e-4f && IndexOff(b, _rig.GripAt, hand, _rig.GunAt, _rig.Gun, still, fixedOnly: true) >= 0f; b += step) bend = b;
        IndexBend = bend;
        _indexOff = IndexOff(bend, _rig.GripAt, hand, _rig.GunAt, _rig.Gun, still, fixedOnly: true);
        var deep = -IndexOff(bend, _rig.GripAt, hand, _rig.GunAt, _rig.Gun, still, fixedOnly: false) - InspectHand.Radius;
        if (deep > InspectHand.Inside) IndexFault = $"the grip hand's index finger lies {deep * 100f:0.0} cm inside the gun's meshes along its side{(bend < 1e-3f - InspectHand.Lift ? ", its knuckle bent back as far as it goes" : "")}";
        else if (_indexOff > TouchGap) IndexFault = $"the grip hand's index finger lies {_indexOff * 100f:0.0} cm off the side of the gun";
    }

    // How far the skin of the grip hand's index finger, bent `bend`, is off the gun's meshes (negative: in them), the boxes counted
    // from the knuckle's own depth (IndexShift): the least over the points along its three bones, the hand at `grip`, `gripTurn`.
    // `fixedOnly`: the meshes no part of the motion moves.
    float IndexOff(float bend, Vector3 grip, Quaternion gripTurn, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved, bool fixedOnly)
    {
        Span<Vector3> line = stackalloc Vector3[4];
        InspectHand.Line(1, bend, line, right: true);
        var least = float.MaxValue;
        for (var k = 0; k < 3; k++)
            for (var s = 1; s <= 2; s++)
                least = MathF.Min(least, Gap(grip + Vector3.Transform(Vector3.Lerp(line[k], line[k + 1], s / 2f), gripTurn), gunAt, gun, moved, fixedOnly));
        return least + _indexShift - InspectHand.Radius;
    }

    // A twirl that stops in the hand (its key not Through) knocks the wrist: TwirlDip degrees, TwirlDrop metres.
    const float TwirlDip = 4f, TwirlDrop = 0.006f;

    static readonly InspectGunKey Whole = default(InspectGunKey) with { Fill = 1f };

    // A twirl: how far (metres) the trigger finger carries the guard off its place, and how far (degrees) the wrist rocks, once a turn.
    const float TwirlRadius = 0.02f, TwirlRock = 18f;

    // How far (metres) a gun reaches behind its grip hand before it counts as having a stock (GunPose's pin).
    const float StockBehind = 0.15f;

    // A grip hand under the stock (InspectGrip.Stock): this share of the way from the grip back to the end of the gun, this much higher.
    const float StockShare = 0.55f, StockRaise = 0.02f;

    // The trigger in the gun's space: where the tip of the grip hand's index finger is, on the gun's centre plane. The hand node is the
    // wrist (ForgeCore gun-hold.json: -X toward the fingers, +Z toward the thumb): the index knuckle lies a palm's length along the
    // fingers on the thumb's side (the top of the grip's back), and the finger reaches from there along the gun to the trigger, a little
    // below; the hand is a hand's size on every gun.
    static readonly Vector3 IndexKnuckle = new(-0.09f, 0f, 0.03f), KnuckleToTrigger = new(0f, -0.01f, 0.065f);

    static Vector3 Trigger(in InspectRig rig, Vector3 gripAt, Quaternion grip)
    {
        var finger = gripAt + Vector3.Transform(IndexKnuckle, grip) + KnuckleToTrigger;
        return new Vector3(rig.Centre.X, finger.Y, finger.Z);
    }

    // Set by GunPose: whether the gun is free of the grip hand this frame, the pose it is held in (where the grip hand stays) and the
    // grip arm's elbow swivel.
    public bool Free { get; private set; }
    public Vector3 HeldAt { get; private set; }
    public Quaternion HeldTurn { get; private set; }

    // Whether the last GunPose had the gun twirling on the trigger finger (free, about its trigger guard).
    public bool Twirling { get; private set; }
    public float HeldSwivel { get; private set; }

    // Where the grip arm's elbow is this frame (set by GunPose): the point the game's IK bends the arm toward.
    public Vector3 GripElbow() => _arm.Elbow(HeldAt + Vector3.Transform(_heldGripAt, HeldTurn), HeldSwivel);

    // The off arm's elbow swivel the last HandPose chose (0 with the hand on its grip), and where its elbow is with the hand's IK target
    // at `hand`: the point the game's IK bends the off arm toward.
    public float OffSwivel { get; private set; }
    public Vector3 OffElbow(Vector3 hand) => _off.Elbow(hand, OffSwivel);

    // Where the off arm's elbow is at `time` (faded by `weight`) with the hand's IK target at `hand`, the hand on its grip or not.
    public Vector3 OffElbowAt(float time, float weight, Vector3 hand) => _off.Elbow(hand, SwivelAt(time) * weight);

    // The off elbow's swivel through the look at Step: what OffSwivelFor asks each frame, swung toward ahead of time and let go after
    // at most SwivelRate degrees a 60th of a second (each way out of the rest plane half of it), so the elbow never jumps (the swivel
    // a frame asks for can flip from tucking in to lifting out, or start at once where a hand turns a part up to the eye).
    float[] _offSwivels = [];
    const float SwivelRate = 8f;

    void BakeSwivel()
    {
        var n = (int)(Seconds / Step) + 2;
        float[] lift = new float[n], tuck = new float[n];
        for (var f = 0; f < n; f++)
        {
            var time = f * Step;
            if (!HandOff(time)) continue;
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            OffPose(time, gunAt, gun, out _, out _);
            (lift[f], tuck[f]) = (MathF.Max(0f, OffSwivel), MathF.Max(0f, -OffSwivel));
        }
        var most = SwivelRate / 2f * Step * 60f;
        foreach (var way in new[] { lift, tuck })
        {
            for (var f = 1; f < n; f++) way[f] = MathF.Max(way[f], way[f - 1] - most);
            for (var f = n - 2; f >= 0; f--) way[f] = MathF.Max(way[f], way[f + 1] - most);
        }
        var swivels = new float[n];
        for (var f = 0; f < n; f++) swivels[f] = lift[f] - tuck[f];
        _offSwivels = swivels;
    }

    float SwivelAt(float time)
    {
        if (_offSwivels.Length == 0) return 0f;
        var at = Math.Clamp(time / Step, 0f, _offSwivels.Length - 1.001f);
        var i = (int)at;
        return _offSwivels[i] + (_offSwivels[i + 1] - _offSwivels[i]) * (at - i);
    }

    // The off elbow's swivel for the hand at `at` turned `turn` on the gun at `gunAt`, `gun`: none while the wrist is within its limits
    // and the forearm out of the gun; past them the elbow swings (lifting out or tucking in, within the arm's range) as little as brings
    // them back, or as near as it comes. The forearm turning with the elbow takes up what the wrist cannot (a hand holding a magazine up
    // to the eye, its palm toward the face) and swings clear of the gun under a hand that stays on a part.
    float OffSwivelFor(Vector3 at, Quaternion turn, Vector3 gunAt, Quaternion gun)
    {
        float Past(float swivel)
        {
            var sunk = MathF.Max(0f, Sunk(_off.Elbow(at, swivel), at, turn, gunAt, gun, _handMoved, _poseView, palm: false) - _offForearmSunk - SinkSlack + LiftPast);
            return _off.Past(at, turn, swivel) + SunkCost * sunk + SwivelCost * MathF.Abs(swivel);
        }
        if (Past(0f) <= 0f) return 0f;
        var (best, least) = (0f, Past(0f));
        for (var pass = 0; pass < 2; pass++)
        {
            float step = pass == 0 ? 2.5f : 0.25f, from = pass == 0 ? -InspectArm.SwivelIn : MathF.Max(-InspectArm.SwivelIn, best - 2.5f);
            float to = pass == 0 ? InspectArm.SwivelOut : MathF.Min(InspectArm.SwivelOut, best + 2.5f);
            for (var swivel = from; swivel <= to + 1e-4f; swivel += step)
                if (Past(swivel) is var cost && cost < least - 1e-5f) (best, least) = (swivel, cost);
        }
        return best;
    }
    // Degrees of wrist past its limit one degree of swivel is worth (the elbow moves only for what it takes back), and a metre of the
    // forearm sunk into the gun past R6's slack (the elbow swings the forearm out of the gun first).
    const float SwivelCost = 0.02f, SunkCost = 2000f;

    // The grip hand's IK target for a gun held at `at`, `turn`.
    public void GripPose(Vector3 at, Quaternion turn, out Vector3 hand, out Quaternion handTurn)
    {
        hand = at + Vector3.Transform(_heldGripAt, turn);
        handTurn = turn * _grip;
    }

    // Whether the off hand leaves its grip at `time`.
    public bool HandOff(float time)
    {
        Motion.HandSpan(time / Slow, out var a, out var b, out _);
        return Motion.Hand[a].On != InspectHold.Grip || Motion.Hand[b].On != InspectHold.Grip;
    }

    // The off hand's target at `time`, given the gun's pose this frame and the game's target on its grip. Between two places whose
    // straight way passes through the gun's bounds (a hand going from its grip or out of the view onto the gun's top or a part), it goes
    // round through a point AroundSide out from the gun's off-hand side and AroundLow of its half height below its middle.
    public void HandPose(float time, float weight, Vector3 gunAt, Quaternion gun, Vector3 liveAt, Quaternion live, out Vector3 at, out Quaternion turn)
    {
        Motion.HandSpan(time / Slow, out var a, out var b, out var s);
        if (a != b && Motion.ClaspRegrip(b, _held)) s = InspectMotion.RegripMove(s);
        (_keyedPress, _keyedSunk) = (0f, 0f);
        var authored = 0f;
        if (_handPress.Length > 0)
        {
            _keyedPress = _handPress[a] + (_handPress[b] - _handPress[a]) * s;
            _keyedSunk = _handSunk[a] + (_handSunk[b] - _handSunk[a]) * s;
            authored = MathF.Max(0f, _handPressed[a] + (_handPressed[b] - _handPressed[a]) * s - _keyedPress);
        }
        Place(a, time, weight, gunAt, gun, liveAt, live, out var atA, out var turnA);
        Place(b, time, weight, gunAt, gun, liveAt, live, out var atB, out var turnB);
        // (Both ends within the arm's reach before the way between them: clamped after, a way to a place out of reach stopped dead where
        // it left the reach, before its key, and the hand jerked to a halt there. The game's hold is where the game has the hand.)
        if (Motion.Hand[a].On != InspectHold.Grip) atA = _off.Within(atA);
        if (Motion.Hand[b].On != InspectHold.Grip) atB = _off.Within(atB);
        var way = Vector3.Lerp(atA, atB, s);
        if (a != b && s > 0f && s < 1f && !Rides(a))
        {
            var toGun = Quaternion.Inverse(gun);
            Vector3 ga = Vector3.Transform(atA - gunAt, toGun), gb = Vector3.Transform(atB - gunAt, toGun);
            Vector3 c = _rig.Centre, e = _rig.Extents;
            // (While the look is still being solved, before Around has decided the spans, this frame's places decide.)
            if (_around.Length > 0 ? _around[a] : Crosses(ga, gb, c - e + new Vector3(0.005f), c + e - new Vector3(0.005f)))
            {
                var round = gunAt + Vector3.Transform(new Vector3(c.X - e.X - AroundSide, c.Y - AroundLow * e.Y, Math.Clamp((ga.Z + gb.Z) / 2f, c.Z - e.Z, c.Z + e.Z)), gun);
                var bend = 2f * round - (atA + atB) / 2f; // the control point that puts the curve through `round` half way
                way = (1f - s) * (1f - s) * atA + 2f * s * (1f - s) * bend + s * s * atB;
            }
        }
        // (Its ends too: the gaps to the rides on either side were measured on the way round, RideEnds.)
        var regrip = a != b && Motion.Hand[b].Regrip;
        var regripTurn = Quaternion.Identity;
        if (regrip) Regrip(a, b, s, time, weight, gunAt, gun, out way, out regripTurn);
        var wayAt = Vector3.Lerp(liveAt, way, weight);
        at = _off.Within(wayAt);
        var placed = at;
        // To or from out of the view the forearm swings round with the hand: the wrist goes evenly from its turn at one place to its turn
        // at the other (in the forearm's frame where the hand is), not the hand's turn in the view, which bends the wrist far past both.
        var keyed = regrip ? regripTurn : a != b && (Motion.Hand[a].On == InspectHold.Away || Motion.Hand[b].On == InspectHold.Away)
            ? _off.Hand(at, Quaternion.Slerp(_off.Wrist(atA, turnA), _off.Wrist(atB, turnB), s))
            : Quaternion.Slerp(turnA, turnB, s);
        var wanted = Quaternion.Slerp(live, keyed, weight);
        if (_offSwivels.Length == 0) Moved(time, _handMoved);
        var swivel = OffSwivel = _offSwivels.Length > 0 ? SwivelAt(time) * weight : OffSwivelFor(at, wanted, gunAt, gun);
        turn = _off.Comfort(at, wanted, swivel);
        // The wrist kept within its limits turns the hand about the wrist; the palm stays where the place puts it, so the wrist moves.
        var palm = at + Vector3.Transform(Palm, wanted);
        at = _off.Within(palm - Vector3.Transform(Palm, turn));
        turn = _off.Comfort(at, turn, swivel);
        OffHeldBack = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(wanted), turn)))) * 180f / MathF.PI;
        // A hand holding a part carried out of the gun holds it as the key turns it, the part fixed in the hand (as much as the part is
        // carried this frame): the wrist's limits give way here, and the wrist rule (Wrings) holds the carry to what a wrist can do.
        // It stays on the part, and where the part is carried is the motion's to say.
        // So does a hand going with a part still in the gun (a magazine coming out, a slide going back): it holds the part as it took it
        // (RideHolds: the wrist within its limits, lifted off the gun as far as R6 asks) and goes with it.
        if (Rides(a) && !_takingHold)
        {
            var (shift, held) = _rideHold.Length > 0 ? _rideHold[a] : (Vector3.Zero, Quaternion.Identity);
            var hold = Quaternion.Normalize(wanted * Quaternion.Slerp(Quaternion.Identity, held, weight));
            at = _off.Within(placed + Vector3.Transform(shift * weight, wanted));
            palm = at + Vector3.Transform(Palm, hold);
            if (_carryFix.Length == 0) (_carryLimited, _carryWanted, _carryPalm, _carryHeld) = (_off.Comfort(at, hold, swivel), hold, palm, Held(Motion.Hand[a].Part, time));
            turn = hold;
            return;
        }
        // Leaving a ride on a part the hand starts from where the ride left it, and coming onto one it ends where the ride takes it
        // (RideEnds), so the hand never steps where it takes or lets go of the part; the lift below keeps it out of the gun.
        if (_leave.Length > 0 && a != b)
        {
            void Ease((Vector3 At, Quaternion Turn) gap, float share, ref Vector3 at, ref Quaternion turn)
            {
                if (gap.Turn == default) return;
                at = _off.Within(at + Vector3.Transform(gap.At, gun) * share);
                turn = Quaternion.Normalize(gun * Quaternion.Slerp(Quaternion.Identity, gap.Turn, share) * Quaternion.Inverse(gun) * turn);
            }
            Ease(_leave[a], (1f - s) * weight, ref at, ref turn);
            Ease(_enter[a], s * weight, ref at, ref turn);
        }
        // On the gun the forearm lies along the face the palm is on, and on the way to it the palm passes close by: as far as the arm
        // would sink deeper into the gun than in the game's own hold (OffDeeper, R6), the hand lifts toward its back (a palm on a face then
        // stands off it by as much more). A few rounds: lifting it swings the forearm a little. The palm is let as near as its places
        // put it (_keyedPress), and the lift leaves what a key's offset presses in (the motion's fault: R6 says so).
        if ((OnGun(Motion.Hand[a].On) || OnGun(Motion.Hand[b].On)) && !_unlifted)
        {
            Moved(time, _handMoved);
            var back = -Vector3.Transform(_palmFaces, turn);
            var (unlifted, unturned) = (at, turn);
            for (var k = 0; k < 8; k++)
            {
                var deeper = OffDeeper(at, turn, gunAt, gun, _handMoved, _poseView) - authored;
                if (deeper <= SinkSlack) break;
                at = _off.Within(at + back * (deeper - SinkSlack + LiftPast));
                turn = _off.Comfort(at, turn, swivel);
            }
            // The palm on what it touches stands off it no more than R6 asks (R1 holds a touch within TouchGap, and the palm already
            // lies PalmSkin off the surface): of the way the rounds lifted it, the least share that keeps R6.
            if (at != unlifted)
            {
                var (lo, hi) = (0f, 1f);
                for (var k = 0; k < LiftSearch; k++)
                {
                    var mid = (lo + hi) / 2f;
                    if (OffDeeper(Vector3.Lerp(unlifted, at, mid), Quaternion.Slerp(unturned, turn, mid), gunAt, gun, _handMoved, _poseView) - authored <= SinkSlack) hi = mid;
                    else lo = mid;
                }
                (at, turn) = (Vector3.Lerp(unlifted, at, hi), Quaternion.Slerp(unturned, turn, hi));
            }
        }
        // A hand on a clasped magazine, or changing to or from that hold, keeps its fingers out of the gun: as far as one of them, closed
        // only until it meets a mesh (Stopped), lies deeper in the meshes than ClaspSink, the hand lifts toward its back by the least
        // that keeps them out (Solve then holds the look to its touch and its pads, ClaspBreaks).
        if (ClaspSpan(a, b))
        {
            Moved(time, _handMoved);
            // (On the way between the holds the hand is open, and the open hand is what must be clear: stopped on the meshes it would
            // change from frame to frame, and the frames between the baked ones would sink.)
            Motion.FingersAt(time / Slow, out var shape, _held);
            var moving = a != b && Motion.ClaspRegrip(b, _held) && s > 0f && s < 1f;
            float Deepest(Vector3 wrist, Quaternion hand)
            {
                var stopped = moving ? InspectMotion.Opened : Stopped(shape, wrist, hand, gunAt, gun, _handMoved, ClaspSquash);
                var deepest = float.MinValue;
                for (var k = 0; k < 5; k++) deepest = MathF.Max(deepest, FingerIn(k, stopped, wrist, hand, gunAt, gun, _handMoved) - InspectHand.Radius);
                return deepest;
            }
            if (Deepest(at, turn) > ClaspSink)
            {
                var back = Vector3.Transform(Vector3.UnitY, turn); // the back of the hand (its palm faces -y)
                var (lo, hi) = (0f, ClaspLift);
                for (var k = 0; k < LiftSearch; k++)
                {
                    var mid = (lo + hi) / 2f;
                    if (Deepest(_off.Within(at + back * mid), turn) > ClaspSink) lo = mid;
                    else hi = mid;
                }
                at = _off.Within(at + back * hi);
            }
        }
        // Leaving the game's hold and coming back to it the hand starts and ends exactly where the game has it (where the look lets the
        // hand go, the game's target is the hand): what the look does to the way (the reach, the wrist's limits, the lift off the gun)
        // comes in and goes out with the way, not at once on the frame the hand leaves or takes its grip, which made it twitch there.
        if (a != b && (Motion.Hand[a].On == InspectHold.Grip || Motion.Hand[b].On == InspectHold.Grip))
        {
            var share = Motion.Hand[a].On == InspectHold.Grip ? s : 1f - s;
            (at, turn) = (Vector3.Lerp(wayAt, at, share), Quaternion.Normalize(Quaternion.Slerp(wanted, turn, share)));
        }
    }

    // How deep a finger's middle line may lie in the gun's meshes on a clasped magazine before the hand lifts (half of rule F's
    // InspectHand.Inside, the rest the baked fingers' give between their frames), and the most it lifts.
    const float ClaspSink = InspectHand.Inside / 2f, ClaspLift = 0.04f;

    // The off hand's size at `time` as drawn (InspectHandKey.Scale, the look's `weight` of it), and its wrist's target `at` (turned
    // `turn`) moved so that the hand drawn at that size about its wrist keeps its palm's middle where the look placed it.
    public float OffScale(float time, float weight, ref Vector3 at, Quaternion turn)
    {
        var scale = 1f + (Motion.ScaleAt(MotionTime(time)) - 1f) * weight;
        at += Vector3.Transform(Palm, turn) * (1f - scale);
        return scale;
    }

    // The off hand at `time` with the gun at `gunAt`, `gun` as the look solves it: the game's target on its grip goes with the gun.
    void OffPose(float time, Vector3 gunAt, Quaternion gun, out Vector3 at, out Quaternion turn)
    {
        var toGun = Quaternion.Inverse(_rig.Gun);
        HandPose(time, 1f, gunAt, gun, gunAt + Vector3.Transform(Vector3.Transform(_rig.OffAt - _rig.GunAt, toGun), gun), gun * toGun * _rig.Off, out at, out turn);
    }

    // Which way the off hand's palm faces, in the hand's own frame (toward the gun in the game's hold, the way Facing turns it to a face);
    // how deep the off forearm sinks into the gun in the game's hold (Sunk) and how much nearer its palm is than touching; the parts'
    // moves HandPose measures against.
    Vector3 _palmFaces;
    float _offForearmSunk, _offPalmSunk;

    // How much deeper the off arm at `wrist`, `hand` sinks into the gun than in the game's hold where the item camera (as the last
    // GunPose set it) sees it (R6): its forearm (a capsule of ForearmRadius), and its palm nearer the gun than touching it (PalmSkin off
    // its surface), each against the game's hold. How far past SinkSlack the hand lifts, so it settles within it.
    float OffDeeper(Vector3 wrist, Quaternion hand, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved, float view) =>
        MathF.Max(Sunk(_off.Elbow(wrist, OffSwivel), wrist, hand, gunAt, gun, moved, view, palm: false) - MathF.Max(_offForearmSunk, _keyedSunk),
            wrist + Vector3.Transform(Palm, hand) is var p && Seen(p, view) ? PalmSkin - Gap(p, gunAt, gun, moved) - MathF.Max(_offPalmSunk, _keyedPress) : 0f);
    // How much nearer than touching the gun the off palm is at its two keys' places, between them by the span (HandPose sets it): a
    // place on a part lies in the boxes of the gun's other meshes where the part does (a charging handle's in the receiver's), and the
    // hand there is held to no deeper than the place puts it. Per key, measured on the gun at rest where the place is without the key's
    // own offset (an offset into the gun is the motion's, and R6 says so); the game's hold on its grip, none out of the view.
    // Likewise the forearm: a hand on a face of a part lays its wrist over the gun's boxes there (a charging handle on top of the
    // receiver), as far as the place does with the gun as the game holds it.
    float _keyedPress, _keyedSunk;
    float[] _handPress = [], _handPressed = [], _handSunk = [];

    void Presses(in InspectRig rig, Vector3 offAt, Quaternion off)
    {
        var hands = Motion.Hand;
        var presses = new float[hands.Length];
        var pressed = new float[hands.Length];
        var sunk = new float[hands.Length];
        for (var i = 0; i < hands.Length; i++)
        {
            if (hands[i].On is InspectHold.Away or InspectHold.Open) continue;
            if (hands[i].On == InspectHold.Grip) { (presses[i], pressed[i], sunk[i]) = (_offPalmSunk, _offPalmSunk, _offForearmSunk); continue; }
            var (at, turn) = (_handAt[i], _hand[i]);
            if (hands[i].Part >= 0) Anchor(hands[i] with { Offset = default }, rig, offAt, off, out at, out turn);
            presses[i] = MathF.Max(0f, PalmSkin - Gap(at + Vector3.Transform(Palm, turn), Vector3.Zero, Quaternion.Identity, _handMoved));
            pressed[i] = MathF.Max(0f, PalmSkin - Gap(_handAt[i] + Vector3.Transform(Palm, _hand[i]), Vector3.Zero, Quaternion.Identity, _handMoved));
            var wrist = rig.GunAt + Vector3.Transform(at, rig.Gun);
            sunk[i] = Sunk(_off.Elbow(wrist), wrist, rig.Gun * turn, rig.GunAt, rig.Gun, _handMoved, float.PositiveInfinity, palm: false);
        }
        (_handPress, _handPressed, _handSunk) = (presses, pressed, sunk);
    }
    const float LiftPast = 0.0025f;
    const int LiftSearch = 8;
    (Quaternion R, Vector3 T)[] _handMoved = [];

    const float AroundSide = 0.06f, AroundLow = 0.5f;

    // Per span of the off hand's keys: whether it goes round the gun, decided once from where its two places are on the gun half way
    // through the span (deciding it each frame would switch the way on and off as the gun turns, and the hand would jump across).
    bool[] _around = [];

    void Around()
    {
        var hands = Motion.Hand;
        _around = new bool[hands.Length];
        Vector3 c = _rig.Centre, e = _rig.Extents;
        var offInGun = Vector3.Transform(_rig.OffAt - _rig.GunAt, Quaternion.Inverse(_rig.Gun));
        var offTurnInGun = Quaternion.Inverse(_rig.Gun) * _rig.Off;
        for (var a = 0; a + 1 < hands.Length; a++)
        {
            if ((hands[a].On == InspectHold.Grip && hands[a + 1].On == InspectHold.Grip) || Rides(a)) continue;
            var time = (hands[a].Time + hands[a + 1].Time) / 2f * Slow;
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            Vector3 live = gunAt + Vector3.Transform(offInGun, gun);
            Place(a, time, 1f, gunAt, gun, live, gun * offTurnInGun, out var atA, out _);
            Place(a + 1, time, 1f, gunAt, gun, live, gun * offTurnInGun, out var atB, out _);
            var toGun = Quaternion.Inverse(gun);
            _around[a] = Crosses(Vector3.Transform(atA - gunAt, toGun), Vector3.Transform(atB - gunAt, toGun), c - e + new Vector3(0.005f), c + e - new Vector3(0.005f));
        }
    }

    // Whether the off hand stays on one part through span `a` (it goes with the part, a carried one out of the gun too, never round).
    bool Rides(int a) => a + 1 < Motion.Hand.Length && Motion.Hand[a].Part >= 0 && Motion.Hand[a].Part == Motion.Hand[a + 1].Part && !Motion.Hand[a + 1].Regrip;

    // A declared regrip (InspectHandKey.Regrip) from hand key `a` to `b` at `s` of the way: the palm goes round the part's mesh box from
    // one hold to the other, off its surface along the way from the box's centre by as much as the two holds stand off it, the hand turning between the two holds; in
    // the part's own space at rest (where both holds were placed), then moved as the part is at `time`.
    void Regrip(int a, int b, float s, float time, float weight, Vector3 gunAt, Quaternion gun, out Vector3 at, out Quaternion turn)
    {
        var part = Motion.Hand[a].Part;
        var box = _rig.Parts![part];
        Vector3 c = (box.Lo + box.Hi) / 2f, e = Vector3.Max((box.Hi - box.Lo) / 2f, new Vector3(1e-4f));
        var held = Quaternion.Slerp(_hand[a], _hand[b], s);
        Vector3 from = _handAt[a] + Vector3.Transform(Palm, _hand[a]) - c, to = _handAt[b] + Vector3.Transform(Palm, _hand[b]) - c;
        float Surface(Vector3 d) => MathF.Min(e.X / MathF.Max(MathF.Abs(d.X), 1e-4f), MathF.Min(e.Y / MathF.Max(MathF.Abs(d.Y), 1e-4f), e.Z / MathF.Max(MathF.Abs(d.Z), 1e-4f)));
        var way = Vector3.Normalize(Vector3.Lerp(Vector3.Normalize(from), Vector3.Normalize(to), s));
        // (Each hold's own stand-off, at least the palm's: a hand that holds the part in its fingers has its palm off it, and the
        // palm leaves the part late and comes to it early, so the hand is on it through the change.)
        float Off(Vector3 held) => MathF.Max(PalmSkin, held.Length() - Surface(Vector3.Normalize(held)));
        float left = Off(from), come = Off(to);
        var palm = c + way * (Surface(way) + (left <= come ? left + (come - left) * s * MathF.Sqrt(s) : come + (left - come) * (1f - s) * MathF.Sqrt(1f - s)));
        PartWorld(part, time, weight, out var r, out var t);
        at = gunAt + Vector3.Transform(Vector3.Transform(palm - Vector3.Transform(Palm, held), r) + t, gun);
        turn = gun * r * held;
    }

    // How far part `part` is carried at `time`: 1 at and between carried keys, 0 on the gun, between the two on the way.
    float CarriedShare(int part, float time)
    {
        Motion.PartSpan(part, time / Slow, out var ka, out var kb, out var ks);
        var keys = Motion.Parts[part].Keys;
        return (keys[ka].Carry != null ? 1f - ks : 0f) + (keys[kb].Carry != null ? ks : 0f);
    }

    // Whether the segment p..q passes through the box lo..hi from outside it to outside it (a place inside the bounds is on the gun, in a
    // hollow of its outline under the stock or by the magazine: the hand goes from there straight).
    static bool Crosses(Vector3 p, Vector3 q, Vector3 lo, Vector3 hi)
    {
        static bool Inside(Vector3 v, Vector3 lo, Vector3 hi) => v.X > lo.X && v.Y > lo.Y && v.Z > lo.Z && v.X < hi.X && v.Y < hi.Y && v.Z < hi.Z;
        if (Inside(p, lo, hi) || Inside(q, lo, hi)) return false;
        float t0 = 0f, t1 = 1f;
        for (var i = 0; i < 3; i++)
        {
            float o = i == 0 ? p.X : i == 1 ? p.Y : p.Z, d = (i == 0 ? q.X : i == 1 ? q.Y : q.Z) - o, l = i == 0 ? lo.X : i == 1 ? lo.Y : lo.Z, h = i == 0 ? hi.X : i == 1 ? hi.Y : hi.Z;
            if (MathF.Abs(d) < 1e-9f) { if (o < l || o > h) return false; continue; }
            float a = (l - o) / d, b = (h - o) / d;
            if (a > b) (a, b) = (b, a);
            t0 = MathF.Max(t0, a);
            t1 = MathF.Min(t1, b);
            if (t0 > t1) return false;
        }
        return true;
    }

    void Place(int key, float time, float weight, Vector3 gunAt, Quaternion gun, Vector3 liveAt, Quaternion live, out Vector3 at, out Quaternion turn)
    {
        var hold = Motion.Hand[key];
        switch (hold.On)
        {
            case InspectHold.Grip: at = liveAt; turn = live; return;
            case InspectHold.Away: at = _handAt[key]; turn = _hand[key]; return;
            // Under the gun as it was held, not as it flies: where the hand takes it back.
            case InspectHold.Open: at = HeldAt + Vector3.Transform(_handAt[key], HeldTurn); turn = HeldTurn * _hand[key]; return;
        }
        var place = _handAt[key];
        var held = _hand[key];
        if (hold.Part >= 0) WithPart(key, time, weight, ref place, ref held);
        at = gunAt + Vector3.Transform(place, gun);
        turn = gun * held;
    }

    // Hand key `key`'s place on its part (in the gun's space, the part at rest) where the part has moved it at `time` (a carried
    // part's, held in the view, with it). A part that spins and looks the same turned (a cylinder) spins on once the hand has let it
    // go: off the ride (the run of keys on it this key is in), the place is where the part's own turn had it at the ride's nearer
    // end, and only what carries the part moves it.
    void WithPart(int key, float time, float weight, ref Vector3 place, ref Quaternion held)
    {
        var part = Motion.Hand[key].Part;
        var own = time;
        if (Motion.Parts[part].Symmetry < 360f)
        {
            var (first, last) = (key, key);
            while (first > 0 && Rides(first - 1)) first--;
            while (Rides(last)) last++;
            own = Math.Clamp(time, Motion.Hand[first].Time * Slow, Motion.Hand[last].Time * Slow);
        }
        PartWorld(part, time, weight, out var r, out var t, own: own);
        (place, held) = (Vector3.Transform(place, r) + t, r * held);
    }

    // How part `part` moves the gun's points at `time` (`weight` 1: as authored, 0: at rest): a point p of it goes to r p + t, in the gun's
    // space. Its own move turns it about its pivot and shifts it; a part under or following another goes with that one too. Into and out
    // of a carried key it goes between its pose at the key before and at the key after, its box centre on a straight line.
    // (`own`: the time its own move is taken at, where that is not `time`.)
    void PartWorld(int part, float time, float weight, out Quaternion r, out Vector3 t, int depth = 0, float own = float.NaN)
    {
        PartPose(part, time, weight, out r, out t, depth, float.IsNaN(own) ? time : own);
        if (!CarryFixed(part, time, out var fix, out var palm)) return;
        // Turned about the palm with the hand (CarryFix), in the gun's space as the last GunPose put it.
        var toGun = Quaternion.Inverse(_poseTurn);
        var turn = Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(toGun * fix * _poseTurn), weight);
        var about = Vector3.Transform(palm - _poseAt, toGun);
        (r, t) = (Quaternion.Normalize(turn * r), Vector3.Transform(t - about, turn) + about);
    }

    // A part held up in the hand turns no further than the hand holding it can (HandPose: the elbow swings first, OffSwivelFor, then
    // the wrist keeps within its limits): where a carried key's turn of the part asks more of the wrist, the part turns back with the
    // hand, about the palm (it stays in the hand), as far as the part is carried. Solved once the follow is (in the view, at Step); the
    // part (and what goes with it) takes it in PartWorld.
    (int Part, Quaternion Turn, Vector3 Palm)[] _carryFix = [];

    // Per off hand key that starts a span on one part (Rides): how the hand holds the part, as a shift and a turn of the hand from the
    // key's place (in the hand's keyed frame): where HandPose puts a hand at that place when the span starts (the wrist within its
    // limits, the elbow swung first, and lifted off the gun as far as R6 asks), kept through the span.
    (Vector3 Shift, Quaternion Turn)[] _rideHold = [];
    bool _takingHold;

    void RideHolds()
    {
        var holds = new (Vector3, Quaternion)[Motion.Hand.Length];
        var toGun = Quaternion.Inverse(_rig.Gun);
        _takingHold = true;
        for (var a = 0; a < holds.Length; a++)
        {
            holds[a] = (Vector3.Zero, Quaternion.Identity);
            if (!Rides(a)) continue;
            // Going on with the part from the span before (no regrip between): the same hold, so the hand does not shift on it at the key.
            if (a > 0 && Rides(a - 1)) { holds[a] = holds[a - 1]; continue; }
            var time = Motion.Hand[a].Time * Slow + 1e-4f;
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            OffPose(time, gunAt, gun, out var at, out var turn);
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out gunAt, out gun, out _);
            Place(a, time, 1f, gunAt, gun, gunAt + Vector3.Transform(Vector3.Transform(_rig.OffAt - _rig.GunAt, toGun), gun), gun * toGun * _rig.Off, out var keyedAt, out var keyed);
            var back = Quaternion.Inverse(keyed);
            holds[a] = (Vector3.Transform(at - _off.Within(keyedAt), back), Quaternion.Normalize(back * turn));
        }
        _takingHold = false;
        _rideHold = holds;
    }

    // Per span of the off hand next to a ride on a part, not on it: how far the hand where the ride before it leaves off (_leave) or
    // where the ride after it starts (_enter) is from where HandPose places it at that end of the span before the lift (_unlifted), a
    // shift and a turn in the gun's frame, which the span takes off or puts on as it goes (none elsewhere).
    (Vector3 At, Quaternion Turn)[] _leave = [], _enter = [];
    bool _unlifted;

    void RideEnds()
    {
        var (leave, enter) = (new (Vector3, Quaternion)[Motion.Hand.Length], new (Vector3, Quaternion)[Motion.Hand.Length]);
        for (var a = 1; a + 1 < leave.Length; a++)
        {
            var key = Motion.Hand[a].Time * Slow;
            if (Rides(a - 1) && !Rides(a)) leave[a] = Gap(key - 1e-4f, key + 1e-4f);
            if (!Rides(a - 1) && Rides(a)) enter[a - 1] = Gap(key + 1e-4f, key - 1e-4f);
        }
        (_leave, _enter) = (leave, enter);

        // The ride's hand at `ride` against the hand placed (before the lift) at `placed`, in the gun's frame there.
        (Vector3, Quaternion) Gap(float ride, float placed)
        {
            GunPose(ride, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            OffPose(ride, gunAt, gun, out var rideAt, out var rideTurn);
            GunPose(placed, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out gunAt, out gun, out _);
            _unlifted = true;
            OffPose(placed, gunAt, gun, out var at, out var turn);
            _unlifted = false;
            var toGun = Quaternion.Inverse(gun);
            var d = Quaternion.Normalize(toGun * rideTurn * Quaternion.Inverse(turn) * gun);
            return (Vector3.Transform(rideAt - at, toGun), d.W < 0f ? -d : d);
        }
    }
    Quaternion _carryLimited, _carryWanted;
    Vector3 _carryPalm;
    float _carryHeld;

    // How far part `part` is out of the gun at `time` for its turn with the hand (CarryFix): wholly where it is carried, and as it comes
    // out of its place (its box centre 1 to 3 cm from where it sits: a magazine leaving its well turns in the hand, not in the gun).
    float Held(int part, float time)
    {
        PartPose(part, time, 1f, out var r, out var t, 0, time);
        var c = (_rig.Parts![part].Lo + _rig.Parts[part].Hi) / 2f;
        return MathF.Max(CarriedShare(part, time), Math.Clamp((Vector3.Distance(Vector3.Transform(c, r) + t, c) - 0.01f) / 0.02f, 0f, 1f));
    }

    void CarryFix()
    {
        var fix = new (int Part, Quaternion Turn, Vector3 Palm)[(int)(Seconds / Step) + 2];
        for (var f = 0; f < fix.Length; f++)
        {
            var time = f * Step;
            fix[f] = (-1, Quaternion.Identity, Vector3.Zero);
            Motion.HandSpan(time / Slow, out var a, out _, out _);
            if (!Rides(a) || Held(Motion.Hand[a].Part, time) <= 0f) continue;
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            _carryHeld = 0f;
            OffPose(time, gunAt, gun, out _, out _);
            if (_carryHeld <= 0f) continue;
            var turn = Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(_carryLimited * Quaternion.Inverse(_carryWanted)), _carryHeld);
            fix[f] = (Motion.Hand[a].Part, turn.W < 0f ? -turn : turn, _carryPalm);
        }
        // A span next to a ride on a part still out of the gun (the hand letting go of it, or changing its hold on it): the part's turn
        // with the hand goes from where the ride before left it to where the ride after takes it up (none where there is no such ride),
        // so the part does not turn at once where the hand lets go or takes hold.
        var rides = (int[])[.. fix.Select((x, f) => x.Part >= 0 ? f : -1).Where(f => f >= 0)];
        for (var f = 0; f < fix.Length; f++)
        {
            var time = f * Step;
            Motion.HandSpan(time / Slow, out var a, out var b, out var s);
            var leaving = a > 0 && Rides(a - 1);
            if (a == b || Rides(a) || !(leaving || Rides(b))) continue;
            var part = leaving ? Motion.Hand[a - 1].Part : Motion.Hand[b].Part;
            if (Held(part, time) <= 0f) continue;
            var before = leaving ? Array.FindLast(rides, r => r < f) : -1;
            var after = Rides(b) && Motion.Hand[b].Part == part ? Array.Find(rides, r => r > f) : -1;
            // (Only the ride's own last and first frames: a ride that ends or starts with the part in the gun turns it with no hand.)
            (Quaternion Turn, Vector3 Palm)? from = before >= 0 && fix[before].Part == part && Motion.Hand[a].Time * Slow - before * Step <= 2f * Step ? (fix[before].Turn, fix[before].Palm) : null;
            (Quaternion Turn, Vector3 Palm)? to = after > 0 && fix[after].Part == part && after * Step - Motion.Hand[b].Time * Slow <= 2f * Step ? (fix[after].Turn, fix[after].Palm) : null;
            if (from == null && to == null) continue;
            var (ta, pa) = from ?? (Quaternion.Identity, to!.Value.Palm);
            var (tb, pb) = to ?? (Quaternion.Identity, pa);
            fix[f] = (part, Quaternion.Slerp(ta, tb, s), Vector3.Lerp(pa, pb, s));
        }
        _carryFix = fix;
    }

    bool CarryFixed(int part, float time, out Quaternion turn, out Vector3 palm)
    {
        (turn, palm) = (Quaternion.Identity, Vector3.Zero);
        if (_carryFix.Length == 0) return false;
        var at = Math.Clamp(time / Step, 0f, _carryFix.Length - 1.001f);
        var i = (int)at;
        var (a, b) = (_carryFix[i], _carryFix[i + 1]);
        if (a.Part != part && b.Part != part) return false;
        var s = at - i;
        var ta = a.Part == part ? a.Turn : Quaternion.Identity;
        var tb = b.Part == part ? b.Turn : Quaternion.Identity;
        turn = Quaternion.Slerp(ta, tb, s);
        palm = a.Part != part ? b.Palm : b.Part != part ? a.Palm : Vector3.Lerp(a.Palm, b.Palm, s);
        return true;
    }

    void PartPose(int part, float time, float weight, out Quaternion r, out Vector3 t, int depth, float own)
    {
        var p = Motion.Parts[part];
        Motion.PartSpan(part, time / Slow, out var a, out var b, out var s);
        if (p.Keys[a].Carry == null && p.Keys[b].Carry == null)
        {
            Own(part, Motion.PartAt(part, own / Slow), weight, out r, out t);
            Parent(part, time, weight, depth, ref r, ref t);
            return;
        }
        KeyPose(part, a, time, depth, out var ra, out var ta);
        KeyPose(part, b, time, depth, out var rb, out var tb);
        var c0 = (_rig.Parts![part].Lo + _rig.Parts[part].Hi) / 2f;
        var centre = Vector3.Lerp(Vector3.Transform(c0, ra) + ta, Vector3.Transform(c0, rb) + tb, s);
        r = Quaternion.Slerp(Quaternion.Identity, Quaternion.Slerp(ra, rb, s), weight);
        t = Vector3.Lerp(c0, centre, weight) - Vector3.Transform(c0, r);
    }

    void Parent(int part, float time, float weight, int depth, ref Quaternion r, ref Vector3 t)
    {
        var parent = Motion.Parts[part].Follow >= 0 ? Motion.Parts[part].Follow : _rig.Parts![part].Under;
        if (parent < 0 || depth > 8) return;
        PartWorld(parent, time, weight, out var pr, out var pt, depth + 1);
        (r, t) = (pr * r, Vector3.Transform(t, pr) + pt);
    }

    // Part key `key` of part `part` as a pose this frame: held in the view (Carried), or its own move with its parents' at `time`.
    void KeyPose(int part, int key, float time, int depth, out Quaternion r, out Vector3 t)
    {
        var k = Motion.Parts[part].Keys[key];
        if (k.Carry != null) { Carried(part, key, out r, out t); return; }
        Own(part, new InspectPartKey(k.Time, k.Move, k.Out, Motion.Parts[part].Turns[key], k.Show, Clear: k.Clear), 1f, out r, out t);
        Parent(part, time, 1f, depth, ref r, ref t);
    }

    // A part's own move by `key`: turned about its pivot, shifted, and out of its well along its down axis (Clear: by its own length).
    void Own(int part, in InspectPartKey key, float weight, out Quaternion r, out Vector3 t)
    {
        var rig = _rig.Parts![part];
        var length = Vector3.Dot(Vector3.Abs(rig.Hi - rig.Lo), Vector3.Abs(rig.Down));
        // The look's weight takes off what is left past the nearest whole turn of its symmetry, not that turn (it looks the same), so a
        // spun cylinder fading out does not wind back; the plugin puts the node back when the look ends, where that is not seen.
        var sym = Motion.Parts[part].Symmetry;
        var whole = MathF.Round(key.Turn / sym) * sym;
        r = Quaternion.CreateFromAxisAngle(Motion.Parts[part].Axis, (whole + (key.Turn - whole) * weight) * MathF.PI / 180f);
        t = rig.Pivot - Vector3.Transform(rig.Pivot, r) + (key.Move + (key.Out + key.Clear * length) * rig.Down) * weight;
    }

    // A carried key's pose in the gun's space this frame (the gun where the last GunPose put it): the part's box centre at the key's
    // point of the view (the item camera as the look started) and distance, its top toward the eye or tipped up from that line by the
    // key's tip (a part glanced at in the hand, not held up to the eye), its length turned about the line to the eye by the key's
    // roll, or, held by the off hand, by the turn nearest that roll that leaves the hand's wrist most at ease (CarryRoll): the hand
    // shows the part to the eye the way an arm can, whatever way the gun's own hold turns the hand.
    void Carried(int part, int key, out Quaternion r, out Vector3 t)
    {
        CarriedInView(part, Motion.Parts[part].Keys[key].Carry!.Value, CarryRoll(part, key), out var turn, out var at);
        var toGun = Quaternion.Inverse(_poseTurn);
        r = Quaternion.Normalize(toGun * turn);
        t = Vector3.Transform(at - _poseAt, toGun);
    }

    // The carried part's move in the view: a point p of it (the gun's space at rest) is at `turn` p + `at`.
    void CarriedInView(int part, InspectCarry carry, float roll, out Quaternion turn, out Vector3 at)
    {
        var rig = _rig.Parts![part];
        var c0 = (rig.Lo + rig.Hi) / 2f;
        var eye = Vector3.Normalize(new Vector3(carry.At.X * _rig.TanHalf * _rig.Aspect, carry.At.Y * _rig.TanHalf, 1f));
        static Quaternion Frame(Vector3 forward, Vector3 up)
        {
            var f = forward - up * Vector3.Dot(forward, up);
            if (f.LengthSquared() < 1e-6f) f = Vector3.Cross(up, Vector3.UnitY);
            return Look(f, up);
        }
        var rest = Frame(Vector3.UnitZ, Vector3.Normalize(-rig.Down));
        // The top's way: at the eye, or that many degrees from it toward the view's up.
        var over = Vector3.Normalize(Vector3.UnitY - eye * eye.Y);
        var top = -eye * MathF.Cos(carry.Tip * MathF.PI / 180f) + over * MathF.Sin(carry.Tip * MathF.PI / 180f);
        var held = Frame(new Vector3(MathF.Cos(roll * MathF.PI / 180f), MathF.Sin(roll * MathF.PI / 180f), 0f), top);
        turn = Quaternion.Normalize(held * Quaternion.Inverse(rest));
        at = eye * carry.Dist - Vector3.Transform(c0, turn);
    }

    float[][] _carryRolls = [];
    const float CarryRollStep = 15f;

    float CarryRoll(int part, int key)
    {
        var k = Motion.Parts[part].Keys[key];
        var carry = k.Carry!.Value;
        if (_carryRolls.Length == 0) _carryRolls = [.. Motion.Parts.Select(p => Enumerable.Repeat(float.NaN, p.Keys.Length).ToArray())];
        if (!float.IsNaN(_carryRolls[part][key])) return _carryRolls[part][key];
        Motion.HandSpan(k.Time, out var a, out _, out _);
        if (Motion.Hand[a].Part != part) return _carryRolls[part][key] = carry.Roll;
        var (best, least) = (carry.Roll, float.MaxValue);
        // Nearest the key's roll first, so of two turns as easy the nearer is taken.
        for (var step = 0; step < (int)(360f / CarryRollStep); step++)
        {
            var roll = carry.Roll + (step + 1) / 2 * CarryRollStep * (step % 2 == 0 ? 1f : -1f);
            CarriedInView(part, carry, roll, out var turn, out var at);
            var (hand, handAt) = (turn * _hand[a], _off.Within(at + Vector3.Transform(_handAt[a], turn)));
            var past = float.MaxValue;
            for (var swivel = -InspectArm.SwivelIn; swivel <= InspectArm.SwivelOut; swivel += 10f)
                past = MathF.Min(past, _off.Past(handAt, hand, swivel) + SwivelCost * MathF.Abs(swivel));
            if (past < least - 0.5f) (best, least) = (roll, past);
        }
        return _carryRolls[part][key] = best;
    }

    // What the plugin writes on part `part`'s nodes at `time`: the move of a node from its pose at rest, in the gun's space (a point p of
    // it goes to r p + t). A node under another part's node gets its move from that part's (its parent carries it); one following another
    // part, or under none, gets the whole. Show: whether a part the look adds is drawn.
    public void PartDelta(int part, float time, float weight, out Quaternion r, out Vector3 t, out bool show)
    {
        PartWorld(part, time, weight, out r, out t);
        var under = _rig.Parts![part].Under;
        if (Motion.Parts[part].Follow < 0 && under >= 0)
        {
            PartWorld(under, time, weight, out var pr, out var pt);
            var back = Quaternion.Inverse(pr);
            (r, t) = (Quaternion.Normalize(back * r), Vector3.Transform(t - pt, back));
        }
        show = Motion.PartAt(part, time / Slow).Show * weight >= 0.5f;
    }

    // A magazine whose own down axis leans back more than this (sine) is raked (a handgun's): the hand takes its bottom at its back.
    // How far behind an upright magazine's front edge the palm's middle lies (the knuckles a little past the edge), and how far in
    // from its side the hand's middle line (the thumb's root a little outside it): a nominal hand's (InspectHand).
    const float RakedMagazine = 0.087f, MagKnuckles = 0.04f, MagThumb = 0.02f, MagYaw = 30f;
    // A magazine held by its side, round its body as a bottle is: the widest the hand takes so (what lies between the thumb's pad and
    // the curled fingers of the nominal hand; a wider one, a drum or a box too, is held by its base); how far the thumb is bent where
    // its pad lies on the left face (MagThumbPad), and how far behind the front edge that pad lies, so the knuckles are past the edge
    // and the fingers curl round it onto the right face. A nominal hand's (InspectHand), measured on its lines.
    const float MagSpan = 0.043f, MagThumbBend = 1f, MagThumbBack = 0.005f;
    static readonly Vector3 MagThumbPad = LastBone(0, MagThumbBend);
    // The hand's middle across (half way from its index knuckle to its little finger's, toward the thumb), in the hand's frame.
    static readonly float MagHandMiddle = (Knuckle(1).Z + Knuckle(4).Z) / 2f;

    static Vector3 Knuckle(int finger)
    {
        Span<Vector3> line = stackalloc Vector3[4];
        InspectHand.Line(finger, 0f, line);
        return line[0];
    }

    // The palm's middle from the off hand's wrist (ForgeCore gun-hold.json: -X toward the fingers), and a fingertip's.
    static readonly Vector3 Palm = new(-0.06f, 0f, 0f), Fingertip = new(-0.12f, 0f, 0f);

    // Where a hand that touches with its fingertips (a key's `tips`) meets the surface: between the middles of the last bones of its
    // index and middle fingers bent a little (TipBend), as they reach for it, in the hand's frame.
    const float TipBend = 0.1f;
    static readonly Vector3 Pads = (LastBone(1, TipBend) + LastBone(2, TipBend)) / 2f;

    // The middle of finger `finger`'s last bone bent `bend`, in the hand's frame: its pad.
    static Vector3 LastBone(int finger, float bend)
    {
        Span<Vector3> line = stackalloc Vector3[4];
        InspectHand.Line(finger, bend, line);
        return (line[2] + line[3]) / 2f;
    }

    // A magazine's own box. The box of its meshes is measured along the gun's axes, and a magazine that leans in the gun (its own down
    // axis off the gun's) fills only part of that: its length along its down axis and its depth across it are what a box leaning that
    // much must have to give the measured one (exact for a straight magazine; one that curves comes out a little deep). `up` and
    // `front` are its own axes (its width is along the gun's x), `half` its half width, length and depth about the measured centre.
    static void MagBox(Vector3 lo, Vector3 hi, Vector3 down, out Vector3 centre, out Vector3 up, out Vector3 front, out Vector3 half)
    {
        centre = (lo + hi) / 2f;
        var size = hi - lo;
        (up, front, half) = (Vector3.UnitY, Vector3.UnitZ, size / 2f);
        var lean = new Vector3(0f, -down.Y, -down.Z);
        if (lean.LengthSquared() < 1e-6f) return;
        lean = Vector3.Normalize(lean);
        float c = lean.Y, s = MathF.Abs(lean.Z), k = c * c - s * s;
        if (c < 0.7f || s < 0.03f || k < 0.3f) return;
        float length = (size.Y * c - size.Z * s) / k, depth = (size.Z * c - size.Y * s) / k;
        if (depth < 0.01f || length < depth) return;
        (up, front, half) = (lean, Vector3.Cross(Vector3.UnitX, lean), new Vector3(size.X / 2f, length / 2f, depth / 2f));
    }

    // A magazine wider than the hand spans (MagSpan), held by its side: cradled with its length (its longest side) across the palm, along
    // the palm's crease and square to the fingers, the palm on its thinnest face and the four fingers curling over the far edge of the
    // other side until they meet it (FingerStop), the thumb on the near side; the palm's middle MagKnuckles in from that edge. The fingers
    // point to its front, under its base or to its right, whichever side they go over. A key's offset is to the magazine's left, up it
    // and to its front; its spin turns the hand about the palm's way.
    // How much thicker than the other the left face's side may be and still take the palm.
    const float MagLeftFirst = 1.25f;
    static void MagCradle(Vector3 mc, Vector3 up, Vector3 front, Vector3 half, in InspectHandKey key, out Vector3 at, out Quaternion turn)
    {
        Vector3[] axes = [Vector3.UnitX, up, front];
        float[] h = [half.X, half.Y, half.Z];
        var length = h[0] >= h[1] && h[0] >= h[2] ? 0 : h[1] >= h[2] ? 1 : 2;
        int i = (length + 1) % 3, j = (length + 2) % 3;
        // (The left face, which the left arm comes to, unless the other is clearly the thinner: MagLeftFirst.)
        static float Thin(int axis, float[] h) => axis == 0 ? h[axis] / MagLeftFirst : h[axis];
        var (palm, over) = Thin(i, h) <= Thin(j, h) ? (i, j) : (j, i);
        var fingers = over == 1 ? -up : axes[over];
        turn = Axes(-fingers, -axes[palm], Vector3.Cross(-fingers, -axes[palm]));
        if (key.Spin != 0f) turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(-axes[palm], key.Spin * MathF.PI / 180f) * turn);
        var place = mc - axes[palm] * (h[palm] + PalmSkin) + fingers * MathF.Max(-h[over], h[over] - MagKnuckles)
            - Vector3.UnitX * key.Offset.X + up * key.Offset.Y + front * key.Offset.Z;
        at = place - Vector3.Transform(Palm, turn);
    }

    // A magazine held by its side as a bottle is, clasped: a narrow face against the palm, the palm's middle at most ClaspPalm off it;
    // the thumb's pad on one big face and the pads of three fingers or more on the other, each pad's skin at most ClaspPad off its face;
    // nothing of the hand more than ClaspSquash into it; the hand's middle at its middle. Where the hand goes on a magazine of a size,
    // and how its joints bend (ForgeCore FingerBend by joint), is searched once per size (Clasp): the thumb's face Along along the fingers
    // from the wrist and the narrow face Palmward toward the palm, the magazine's width turned Lean from across the fingers toward the
    // palm; the thumb's root spread and turned and its three joints bent, each finger's knuckle and its other two joints bent alike, in
    // steps; the places that so hold it in order of the palm nearest, then the least lean (Clasped picks one). A finger whose pad no
    // step lays on the face is given closed, and FingerStop stops it on the meshes. Pad: the thumb's pad, in the hand's frame. A
    // magazine none of the places holds, or whose clasp the solved look breaks (Solve), keeps the pinch (Anchor, MagNote).
    readonly record struct MagClasp(float Along, float Palmward, float Lean, ForgeVisual.FingerBend Shape, Vector3 Pad, int Fingers, float Score, bool Hook = false);
    const float ClaspPalm = 0.015f, ClaspPad = 0.006f, ClaspSquash = 0.002f;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(Vector3 Half, int Least), Lazy<MagClasp[]>> Clasps = new();
    MagClasp?[] _clasps = [];
    bool[] _clasped = [];

    // What a key that clasps a part holds, in the gun's space at rest: the box's centre, the way the fingers point across it (from the
    // thumb's face to the fingers' face), the way the palm faces (into the face it lies on), and the box's half size in the clasp's own
    // terms (Clasp: across the fingers, along the thumb, toward the palm). A magazine by its side (MagSide): its own box (MagBox), the
    // palm on its back, the fingers on its left face and the thumb on its right. A part a `clasp` key names: its mesh box, held one of
    // Ways ways (`way`): on a left or right face (a charging handle) the fingers up over it, the palm either against the gun's side or
    // behind the part (the forearm comes from under the gun either way: never down over its top); on a top or bottom face the fingers
    // across it to the right. And how many fingers' pads besides the thumb's the clasp must lay on the part: a magazine is held in the
    // whole hand, a small part between the thumb and a finger (or hooked by two fingers, Clasp).
    bool ClaspTarget(in InspectHandKey key, int way, out Vector3 centre, out Vector3 fingers, out Vector3 palm, out Vector3 half, out int least)
    {
        (centre, fingers, palm, half, least) = (default, default, default, default, 3);
        if (Support(key))
        {
            if (way >= SupportWays.Length) return false;
            SupportBox(key, float.NaN, out centre, out fingers, out palm, out half, out _, out _, way);
            return true;
        }
        if (key.Part < 0 || _rig.Parts is not { } parts) return false;
        var rig = parts[key.Part];
        if (MagSide(key))
        {
            MagBox(rig.Lo, rig.Hi, rig.Down, out centre, out var up, out var front, out half);
            (fingers, palm) = (-Vector3.UnitX, front);
            return way == 0;
        }
        if (key.On != InspectHold.Node || !key.Clasp || rig.Lo == rig.Hi || way >= Ways(key)) return false;
        (centre, least) = ((rig.Lo + rig.Hi) / 2f, 1);
        var side = key.Face == InspectFace.Right ? -1f : 1f;
        (palm, fingers) = key.Face switch
        {
            InspectFace.Top => (-Vector3.UnitY, Vector3.UnitX),
            InspectFace.Bottom => (Vector3.UnitY, Vector3.UnitX),
            _ => (way == 0 ? Vector3.UnitX * side : Vector3.UnitZ, Vector3.UnitY),
        };
        var e = (rig.Hi - rig.Lo) / 2f;
        static float Along(Vector3 e, Vector3 axis) => MathF.Abs(Vector3.Dot(e, axis));
        half = new Vector3(Along(e, fingers), Along(e, Vector3.Cross(fingers, palm)), Along(e, palm));
        return true;
    }

    // A hand under, beside or on top of the gun (the off hand supporting it), which wraps the gun's body like a magazine's clasp.
    static bool Support(in InspectHandKey key) => key.On is InspectHold.Under or InspectHold.Side or InspectHold.Top;

    // The gun's body where such a hand holds it, as the box a clasp takes (ClaspTarget): the section of the meshes no part moves at its
    // place along the gun (Section; `along`, else the key's place; under or on top, those over the gun's middle line), SupportReach
    // either way along it; the palm on the face the hold puts it on and the fingers round the far side (under it: across to the right;
    // beside it: up over the top; on top: across to the right), turned about the palm as the game's own hold turns them (the forearm
    // then comes as it does in the game's hold); the box's half size is what it spans along the hand's own axes.
    const float SupportReach = 0.045f, SupportFace = 0.03f;
    Vector3 _offAtGun;
    Quaternion _offGun = Quaternion.Identity;
    float[] _supportZ = [];
    readonly IReadOnlySet<int> _dropped = new HashSet<int>(); // the support holds (by slot) that keep their plain hold

    // The ways (share of the turn from straight across the gun to the game's own fingers) a support hold's fingers may point, in order.
    static readonly float[] SupportWays = [0.5f, 0.3f, 0.7f, 0f];

    void SupportBox(in InspectHandKey key, float along, out Vector3 centre, out Vector3 fingers, out Vector3 palm, out Vector3 half, out Vector3 lo, out Vector3 hi, int way = -1)
    {
        if (way < 0) way = SlotOf(key) is var slot and >= 0 && slot < _ways.Length ? _ways[slot] : 0;
        Vector3 c = _rig.Centre, e = _rig.Extents;
        var z = !float.IsNaN(along) ? along : float.IsNaN(key.Along) ? _offAtGun.Z : c.Z - e.Z + key.Along * 2f * e.Z;
        Section(_rig, z, true, out lo, out hi, key.On == InspectHold.Side ? float.NaN : _rig.Centre.X);
        // (Of that section, the meshes whose face the palm lies on comes within SupportFace of the section's own: a sight or a rail
        // farther round the gun is not what the hand closes round.)
        var on = key.On;
        float Face(InspectBox b) => on == InspectHold.Side ? b.Lo.X : on == InspectHold.Top ? -b.Hi.Y : b.Lo.Y;
        var (slo, shi, face) = (lo, hi, on == InspectHold.Side ? lo.X : on == InspectHold.Top ? -hi.Y : lo.Y);
        (lo, hi) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var b in _rig.Meshes ?? [])
            if (b.Part < 0 && b.Lo.Z <= z && z <= b.Hi.Z && b.Lo.X <= shi.X && b.Hi.X >= slo.X && b.Lo.Y <= shi.Y && b.Hi.Y >= slo.Y && Face(b) <= face + SupportFace
                && (on == InspectHold.Side || (b.Lo.X - SectionAcross <= _rig.Centre.X && _rig.Centre.X <= b.Hi.X + SectionAcross)))
                (lo, hi) = (Vector3.Min(lo, b.Lo), Vector3.Max(hi, b.Hi));
        if (lo.X > hi.X) (lo, hi) = (slo, shi);
        (lo.Z, hi.Z) = (MathF.Max(lo.Z, z - SupportReach), MathF.Min(hi.Z, z + SupportReach));
        centre = (lo + hi) / 2f;
        (fingers, palm) = key.On switch
        {
            InspectHold.Side => (Vector3.UnitY, Vector3.UnitX),
            InspectHold.Top => (Vector3.UnitX, -Vector3.UnitY),
            _ => (Vector3.UnitX, Vector3.UnitY),
        };
        var game = Vector3.Transform(-Vector3.UnitX, _offGun);
        if (game - palm * Vector3.Dot(game, palm) is var toward && toward.LengthSquared() > 0.04f)
        {
            var to = Vector3.Normalize(toward);
            var angle = MathF.Acos(Math.Clamp(Vector3.Dot(fingers, to), -1f, 1f)) * SupportWays[way];
            var axis = Vector3.Cross(fingers, to);
            if (axis.LengthSquared() > 1e-6f) fingers = Vector3.Normalize(Vector3.Transform(fingers, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle)));
        }
        var size = (hi - lo) / 2f;
        static float Span(Vector3 size, Vector3 axis) => size.X * MathF.Abs(axis.X) + size.Y * MathF.Abs(axis.Y) + size.Z * MathF.Abs(axis.Z);
        half = new Vector3(Span(size, fingers), Span(size, Vector3.Cross(fingers, palm)), Span(size, palm));
    }

    int PartsLen => _rig.Parts?.Length ?? 0;

    // Where Clasped keeps a key's clasp: a part's by the part (its keys share it), a support hold's by the key, after the parts.
    int SlotOf(in InspectHandKey k)
    {
        if (!Support(k)) return k.Part;
        var (time, on) = (k.Time, k.On);
        var h = Array.FindIndex(Motion.Hand, x => x.Time == time && x.On == on);
        return h < 0 ? -1 : PartsLen + h;
    }

    static int Ways(in InspectHandKey key) => key.Clasp && key.Face is InspectFace.Left or InspectFace.Right ? 2 : 1;

    // The way (ClaspTarget) each part's clasp holds it, as Clasped chose.
    int[] _ways = [];

    bool ClaspTarget(in InspectHandKey key, out Vector3 centre, out Vector3 fingers, out Vector3 palm, out Vector3 half, out int least) =>
        ClaspTarget(key, key.Part >= 0 && key.Part < _ways.Length ? _ways[key.Part] : 0, out centre, out fingers, out palm, out half, out least);

    // Of the places that clasp a part (Clasp, the search's order, each way it may be held), the `_rank`th: a thumb before a hook, then
    // the most fingers on its face and then, for a carried magazine, the one that leaves the wrist most at ease where the part is
    // carried (the turn the carry's own roll gives it, as CarryRoll weighs it; the wrist's folding after). Solve tries the first few
    // ranks at once. None: the pinch. The search knows only the part's own box: here each place is put on the gun at rest and every
    // finger closed until it meets any mesh (FingerStop); a place whose palm lies in the gun's other meshes, or whose thumb or finger
    // still lies more than InspectHand.Inside in them, is not one, and a finger whose pad then lies off the part is not counted.
    readonly int _rank;

    MagClasp? Clasped(int slot)
    {
        var count = PartsLen + Motion.Hand.Length;
        if (_clasps.Length == 0) (_clasps, _clasped, _ways) = (new MagClasp?[count], new bool[count], new int[count]);
        if (_clasped[slot]) return _clasps[slot];
        _clasped[slot] = true;
        var keyed = slot >= PartsLen ? slot - PartsLen : Array.FindIndex(Motion.Hand, h => h.Part == slot && (MagSide(h) || h.Clasp));
        if (keyed < 0) return null;
        var key0 = Motion.Hand[keyed] with { Offset = default, Spin = 0f };
        // (The places are the same for every rank Solve tries on one rig: they are found once for its meshes. A support hold takes its
        // best place in every rank: the ranks are the part's.)
        var ranked = _rig.Meshes is { } meshes
            ? ClaspPlaces.GetOrCreateValue(meshes).GetOrAdd((Motion, slot), _ => new Lazy<(MagClasp Clasp, int Way)[]>(() => Places(slot, key0))).Value
            : Places(slot, key0);
        var rank = slot >= PartsLen ? 0 : _rank;
        if (ranked.Length <= rank) return null;
        _ways[slot] = ranked[rank].Way;
        return _clasps[slot] = ranked[rank].Clasp;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InspectBox[], System.Collections.Concurrent.ConcurrentDictionary<(InspectMotion, int), Lazy<(MagClasp Clasp, int Way)[]>>> ClaspPlaces = new();

    (MagClasp Clasp, int Way)[] Places(int slot, InspectHandKey key0)
    {
        var support = Support(key0);
        var part = support ? -2 : slot;
        Vector3 boxLo, boxHi;
        if (support) SupportBox(key0, float.NaN, out _, out _, out _, out _, out boxLo, out boxHi);
        else (boxLo, boxHi) = (_rig.Parts![part].Lo, _rig.Parts[part].Hi);
        var tries = new List<(MagClasp Clasp, int Way, Vector3 Mc, Vector3 Fingers, Vector3 Palm, Vector3 Half, int Least)>();
        for (var way = 0; ClaspTarget(key0, way, out var mc, out var fingers, out var palm, out var half, out var least); way++)
            foreach (var clasp in Clasps.GetOrAdd((half, least), k => new Lazy<MagClasp[]>(() => Clasp(k.Half, k.Least))).Value)
                tries.Add((clasp, way, mc, fingers, palm, half, least));
        // (Each place is tried on its own thread, kept in the search's order: the ranks Solve tries all wait on these.)
        var kept = new (MagClasp Clasp, int Way, float Past, float Fold)?[tries.Count];
        Parallel.For(0, tries.Count, t =>
        {
            var (clasp, way, mc, fingers, palm, half, least) = tries[t];
            Span<Vector3> line = stackalloc Vector3[4];
            {
                ClaspAnchor(clasp, key0, mc, fingers, palm, half, out var at, out var turn);
                // (A magazine is clasped out of its well, where the gun is not round it: its clasp is held to the solved look, ClaspBreaks.)
                var (thumbOff, onPart) = (false, clasp.Fingers);
                if (!MagSide(key0) && Gap(at + Vector3.Transform(PalmSurface, turn), Vector3.Zero, Quaternion.Identity, _handMoved, except: part) < -ClaspSquash) return;
                if (!MagSide(key0)) onPart = 0;
                for (var f = 0; f < 5 && !thumbOff && !MagSide(key0); f++)
                {
                    var closed = Closed(f, clasp.Shape, FingerStop(f, clasp.Shape, at, turn, Vector3.Zero, Quaternion.Identity, _handMoved, ClaspSquash));
                    InspectHand.Line(f, closed, line);
                    var on = BoxGap(at + Vector3.Transform((line[2] + line[3]) / 2f, turn), boxLo, boxHi) <= InspectHand.Radius + ClaspPad
                        && FingerIn(f, closed, at, turn, Vector3.Zero, Quaternion.Identity, _handMoved) - InspectHand.Radius <= InspectHand.Inside;
                    if (f == 0) thumbOff = !on && !clasp.Hook;
                    else if (on) onPart++;
                }
                if (thumbOff || onPart < (clasp.Hook ? Math.Max(2, least) : least)) return;
                var (past, fold) = (0f, 0f);
                foreach (var k in support ? [] : Motion.Parts[part].Keys)
                {
                    if (k.Carry is not { } carry) continue;
                    Motion.HandSpan(k.Time, out var a, out _, out _);
                    if (!MagSide(Motion.Hand[a]) || Motion.Hand[a].Part != part) continue;
                    ClaspAnchor(clasp, Motion.Hand[a], mc, fingers, palm, half, out var handAt, out var handTurn);
                    CarriedInView(part, carry, carry.Roll, out var carried, out var carriedAt);
                    var (hand, wrist) = (carried * handTurn, _off.Within(carriedAt + Vector3.Transform(handAt, carried)));
                    var (p, fo) = (float.MaxValue, float.MaxValue);
                    for (var swivel = -InspectArm.SwivelIn; swivel <= InspectArm.SwivelOut; swivel += 10f)
                        if (_off.Past(wrist, hand, swivel) + SwivelCost * MathF.Abs(swivel) is var cost && cost < p) (p, fo) = (cost, _off.Fold(wrist, hand, swivel));
                    (past, fold) = (MathF.Max(past, p), MathF.Max(fold, fo));
                }
                // (A hand on the gun's body: how far its wrist goes past its limits with the gun where the game holds it.)
                if (support)
                {
                    var (hand, wrist) = (_rig.Gun * turn, _off.Within(_rig.GunAt + Vector3.Transform(at, _rig.Gun)));
                    (past, fold) = (float.MaxValue, float.MaxValue);
                    for (var swivel = -InspectArm.SwivelIn; swivel <= InspectArm.SwivelOut; swivel += 10f)
                        if (_off.Past(wrist, hand, swivel) + SwivelCost * MathF.Abs(swivel) is var cost && cost < past) (past, fold) = (cost, _off.Fold(wrist, hand, swivel));
                }
                kept[t] = (clasp with { Fingers = onPart }, way, past, fold);
            }
        });
        // (A support hold puts the wrist's ease first: the gun turns under it through the motion, and three fingers round it hold it.)
        var clear = kept.OfType<(MagClasp Clasp, int Way, float Past, float Fold)>();
        return support ? [.. clear.OrderBy(c => MathF.Round(c.Past)).ThenBy(c => c.Fold).Select(c => (c.Clasp, c.Way))]
            : [.. clear.OrderBy(c => c.Clasp.Hook).ThenByDescending(c => c.Clasp.Fingers).ThenBy(c => MathF.Round(c.Past)).ThenBy(c => c.Fold).Select(c => (c.Clasp, c.Way))];
    }
    // A clasp's hand on a box of centre `mc` (ClaspTarget: the fingers pointing `fingers` across it, the palm facing `palm`, half size
    // `half` in the clasp's terms), for key `key` (its offset to the left, up and to the front, of a magazine its own axes; its spin
    // about the way the fingers point, about the box's middle).
    static void ClaspAnchor(in MagClasp clasp, in InspectHandKey key, Vector3 mc, Vector3 fingers, Vector3 palm, Vector3 half, out Vector3 at, out Quaternion turn)
    {
        float cos = MathF.Cos(clasp.Lean * MathF.PI / 180f), sin = MathF.Sin(clasp.Lean * MathF.PI / 180f);
        var thumb = Vector3.Cross(fingers, palm);
        turn = Axes(-fingers * cos + palm * sin, -fingers * sin - palm * cos, thumb);
        at = mc + fingers * key.Offset.X + thumb * key.Offset.Y + palm * key.Offset.Z - Vector3.Transform(new Vector3(-clasp.Along - half.X, -clasp.Palmward - half.Z, MagHandMiddle), turn);
        if (key.Spin == 0f) return;
        var spin = Quaternion.CreateFromAxisAngle(fingers, key.Spin * MathF.PI / 180f);
        (turn, at) = (Quaternion.Normalize(spin * turn), mc + Vector3.Transform(at - mc, spin));
    }
    // The search's finger lines, worked out once: the thumb's at every step of its spread and three joints (ThumbLines, 5^4 of them),
    // each other finger's at every step of its knuckle and its other two joints together (FingerLines, 11^2 per finger).
    static readonly Vector3[] ThumbLines = SearchLines(true), FingerLines = SearchLines(false);

    static Vector3[] SearchLines(bool thumb)
    {
        var (joints, line) = (new float[15], new Vector3[4]);
        var lines = new List<Vector3>();
        if (thumb)
            for (var spread = 0; spread <= 4; spread++)
                for (var r = 0; r <= 4; r++)
                    for (var m = 0; m <= 4; m++)
                        for (var i = 0; i <= 4; i++)
                        {
                            (joints[0], joints[1], joints[2]) = (r / 4f, m / 4f, i / 4f);
                            InspectHand.Line(0, ForgeVisual.FingerBend.ByJoints(joints, spread / 4f), line);
                            lines.AddRange(line);
                        }
        else
            for (var f = 1; f < 5; f++)
                for (var m = 0; m <= 10; m++)
                    for (var q = 0; q <= 10; q++)
                    {
                        Array.Clear(joints);
                        (joints[f * 3], joints[f * 3 + 1], joints[f * 3 + 2]) = (m / 10f, q / 10f, q / 10f);
                        InspectHand.Line(f, ForgeVisual.FingerBend.ByJoints(joints), line);
                        lines.AddRange(line);
                    }
        return [.. lines];
    }

    static MagClasp[] Clasp(Vector3 half, int least)
    {
        var skin = Palm - Vector3.UnitY * PalmSkin;
        // (Each lean and place along is searched on its own thread; the places keep the search's order.)
        var found = new List<MagClasp>[7 * 17];
        Parallel.For(0, found.Length, n =>
        {
            var (line, joints) = (new Vector3[4], new float[15]);
            var (lean, along) = (-30 + n / 17 * 10, 20 + n % 17 * 5);
            found[n] = [];
            // The magazine's width and depth in the hand's frame (its length runs toward the thumb).
            float cos = MathF.Cos(lean * MathF.PI / 180f), sin = MathF.Sin(lean * MathF.PI / 180f);
            Vector3 width = new(-cos, -sin, 0f), depth = new(-sin, cos, 0f);
            {
                for (var palmward = -10; palmward <= (least >= 3 ? 40 : 70); palmward += 5)
                {
                    var c = new Vector3(-along / 1000f - half.X, -palmward / 1000f - half.Z, MagHandMiddle);
                    Vector3 Local(Vector3 h) => new(Vector3.Dot(h - c, width), h.Z - c.Z, Vector3.Dot(h - c, depth));
                    float Gap(Vector3 h) => BoxGap(Local(h), -half, half);
                    var palm = Gap(skin);
                    var face = Vector3.Abs(Local(skin)) - half;
                    // (A pinch, a clasp of fewer than three fingers: the thumb and the fingers alone hold it, the palm anywhere clear of it.)
                    if (palm < -ClaspSquash || (least >= 3 && (palm > ClaspPalm || face.Z < face.X || face.Z < face.Y))) continue;
                    // Finger `finger` of `joints`: whether it stays out of the box and lays its pad on face `side`, and how far its pad is.
                    float Pad(Vector3[] lines, int at, float side)
                    {
                        Array.Copy(lines, at * 4, line, 0, 4);
                        for (var k = 0; k < 3; k++)
                            for (var s = 1; s <= 2; s++)
                                if (Gap(Vector3.Lerp(line[k], line[k + 1], s / 2f)) < InspectHand.Radius - ClaspSquash) return float.NaN;
                        var p = (line[2] + line[3]) / 2f;
                        var (g, l) = (Gap(p), Local(p));
                        return MathF.Abs(l.Z) < half.Z && MathF.Abs(l.Y) < half.Y && l.X * side > half.X && g <= InspectHand.Radius + ClaspPad ? g : float.NaN;
                    }
                    Array.Clear(joints);
                    var (thumb, spreadAt, near) = (Vector3.Zero, 0f, float.MaxValue);
                    for (var spread = 0; spread <= 4; spread++)
                        for (var r = 0; r <= 4; r++)
                            for (var m = 0; m <= 4; m++)
                                for (var i = 0; i <= 4; i++)
                                {
                                    if (Pad(ThumbLines, ((spread * 5 + r) * 5 + m) * 5 + i, -1f) is var g && g < near) (near, thumb, spreadAt) = (g, new Vector3(r, m, i) / 4f, spread / 4f);
                                }
                    // (No thumb on the near face: a magazine is not held; a small part is still hooked by two fingers over it, the
                    // thumb left open beside them.)
                    var hook = near == float.MaxValue;
                    if (hook && least >= 3) continue;
                    if (hook) (thumb, spreadAt) = (Vector3.Zero, 0f);
                    var shape = new float[15];
                    (shape[0], shape[1], shape[2]) = (thumb.X, thumb.Y, thumb.Z);
                    var right = 0;
                    for (var f = 1; f < 5; f++)
                    {
                        var (knuckle, rest, padAt) = (1f, 1f, float.MaxValue);
                        for (var m = 0; m <= 10; m++)
                            for (var q = 0; q <= 10; q++)
                            {
                                if (Pad(FingerLines, ((f - 1) * 11 + m) * 11 + q, 1f) is var g && g < padAt) (padAt, knuckle, rest) = (g, m / 10f, q / 10f);
                            }
                        if (padAt < float.MaxValue) right++;
                        (shape[f * 3], shape[f * 3 + 1], shape[f * 3 + 2]) = (knuckle, rest, rest);
                    }
                    var score = -palm * 100f - MathF.Abs(lean) / 30f;
                    if (right < (hook ? Math.Max(2, least) : least)) continue;
                    Array.Copy(shape, joints, 15);
                    InspectHand.Line(hook ? 1 : 0, ForgeVisual.FingerBend.ByJoints(joints, spreadAt), line);
                    found[n].Add(new MagClasp(along / 1000f, palmward / 1000f, lean, ForgeVisual.FingerBend.ByJoints(shape, spreadAt), (line[2] + line[3]) / 2f, right, hook ? score - 100f : score, hook));
                }
            }
        });
        return [.. found.SelectMany(f => f).OrderByDescending(c => c.Score)];
    }

    // A hand's turn from where its axes point: x away from the fingers, y away from the left palm, z toward the thumb.
    static Quaternion Axes(Vector3 x, Vector3 y, Vector3 z) =>
        Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f)));

    // `along`: where along the gun (its z) a hand under, beside, on top of or at the muzzle of the gun goes instead of the key's place
    // (a clasp's support place).
    void Anchor(in InspectHandKey key, in InspectRig rig, Vector3 offAt, Quaternion off, out Vector3 at, out Quaternion turn, float along = float.NaN)
    {
        Vector3 c = rig.Centre, e = rig.Extents;
        float Along(float f) => c.Z - e.Z + f * 2f * e.Z;
        var measured = rig;
        turn = off;
        // A hand under, beside or on top of the gun wraps its body where a clasp takes it (SupportBox), else it lies on it as below.
        if (Support(key) && ClaspOf(key) is { } wrap)
        {
            SupportBox(key, along, out var wc, out var wf, out var wp, out var wh, out var wlo, out var whi);
            ClaspAnchor(wrap, key, wc, wf, wp, wh, out at, out turn);
            if (SlotOf(key) - PartsLen is var wk && wk >= 0 && wk < _supportZ.Length) _supportZ[wk] = !float.IsNaN(along) ? along : (wlo.Z + whi.Z) / 2f;
            return;
        }
        switch (key.On)
        {
            // Lowered out of the view, the hand hanging straight on its forearm (the wrist neither bent nor twisted).
            case InspectHold.Away:
                at = rig.OffArm.Shoulder + AwayFromShoulder;
                turn = rig.OffArm.Hand(at, Quaternion.Identity);
                return;
            // Under the gun at the key's place: the hand turned as the game holds it, its palm's middle PalmSkin under the bottom of the
            // meshes over the hand there (across, within their width), touching it whatever gap the game's own hold leaves.
            case InspectHold.Under:
            case InspectHold.Open: at = Settle(Below(!float.IsNaN(along) ? along : float.IsNaN(key.Along) ? offAt.Z : Along(key.Along)), off, -Vector3.UnitY); return;
            case InspectHold.Node:
            case InspectHold.Mag:
                {
                    // On a face of the part's mesh box: the hand turned about the bore until its palm faces it, the palm over the key's point
                    // of the face, its middle PalmSkin off it, then shifted by the key's offset (out, across, along).
                    var part = rig.Parts![key.Part];
                    Vector3 lo = part.Lo, hi = part.Hi;
                    // A magazine in the hand (a `mag` key that names no place, `at`), on its own box (MagBox: one that leans in the gun is
                    // held along its own axes); the key's offset is to the magazine's left, up it and to its front.
                    // By its side (no `face`, and no wider than MagSpan), round its body as a bottle is held: clasped where it takes
                    // a clasp (Clasped, ClaspAnchor: its back on the palm, the thumb on its right face, the fingers on its left).
                    // Otherwise (MagNote) the pinch: the palm toward its left face, the fingers toward its front and the thumb's
                    // side up it, the hand's middle (between its first and last knuckles) at its middle; the thumb's pad (MagThumbPad)
                    // a finger's half thickness off the left face and MagThumbBack behind the front edge, so the knuckles lie past that
                    // edge and the fingers, closing until they meet it (FingerStop), curl round the front onto the right face; the
                    // key's spin turns the hand about the way out of the left face, about the thumb's pad.
                    // By its base (`face` `bottom`, or a wider one: a drum, a box): the base on the palm, the fingers forward and
                    // MagYaw across (the left arm comes from the left) with the knuckles just past its front edge, so they close up
                    // its front, and the hand's middle line MagThumb in from its left face, so the thumb's root lies beside that
                    // face and the thumb goes up it; the key's spin turns the hand under it.
                    // A part a `clasp` key names, held between the thumb and the fingers where a clasp holds it (ClaspTarget, Clasped);
                    // where none does the look cannot play (Unplayable), and the hand lies on the face as a plain key's would meanwhile.
                    if (key.Clasp && ClaspOf(key) is { } held && ClaspTarget(key, out var hc, out var hf, out var hp, out var hh, out _))
                    {
                        ClaspAnchor(held, key, hc, hf, hp, hh, out at, out turn);
                        return;
                    }
                    if (key.On == InspectHold.Mag && key.Face is InspectFace.Left or InspectFace.Bottom && float.IsNaN(key.U))
                    {
                        MagBox(lo, hi, part.Down, out var mc, out var up, out var front, out var half);
                        if (ClaspOf(key) is { } clasp && ClaspTarget(key, out var cc, out var cf, out var cp, out var ch, out _))
                        {
                            ClaspAnchor(clasp, key, cc, cf, cp, ch, out at, out turn);
                            return;
                        }
                        if (key.Face == InspectFace.Left && half.X * 2f <= MagSpan)
                        {
                            turn = Axes(-front, -Vector3.UnitX, up);
                            var pad = mc - Vector3.UnitX * (half.X + InspectHand.Radius + key.Offset.X) + front * (half.Z - MagThumbBack + key.Offset.Z)
                                + up * (MagThumbPad.Z - MagHandMiddle + key.Offset.Y);
                            if (key.Spin != 0f) turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(-Vector3.UnitX, key.Spin * MathF.PI / 180f) * turn);
                            at = pad - Vector3.Transform(MagThumbPad, turn);
                            return;
                        }
                        // Wider than the hand spans, by its side: cradled with its length across the palm (MagCradle).
                        if (key.Face == InspectFace.Left)
                        {
                            MagCradle(mc, up, front, half, key, out at, out turn);
                            return;
                        }
                        // (By its base the length across the palm too: a magazine longest front to back or across, a box, has the
                        // fingers across it to the right instead of along it.)
                        var reach = half.Z > half.Y && half.Z >= half.X ? Vector3.UnitX
                            : half.X > half.Y && half.X > half.Z ? front
                            : Vector3.Normalize(front * MathF.Cos(MagYaw * MathF.PI / 180f) + Vector3.UnitX * MathF.Sin(MagYaw * MathF.PI / 180f));
                        turn = Axes(-reach, -up, Vector3.Cross(reach, up));
                        if (key.Spin != 0f) turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(-up, key.Spin * MathF.PI / 180f) * turn);
                        var knuckles = reach == Vector3.UnitX ? Vector3.UnitX * MathF.Max(-half.X, half.X - MagKnuckles) : front * MathF.Max(-half.Z, half.Z - MagKnuckles);
                        var place = mc - up * half.Y + knuckles + (reach == Vector3.UnitX ? Vector3.Zero : Vector3.UnitX * MathF.Min(0f, MagThumb - half.X));
                        at = place - up * PalmSkin - Vector3.UnitX * key.Offset.X + up * key.Offset.Y + front * key.Offset.Z - Vector3.Transform(Palm, turn);
                        return;
                    }
                    var u = !float.IsNaN(key.U) ? key.U : key.On == InspectHold.Mag && part.Down.Z < -RakedMagazine ? 0.125f : 0.5f;
                    float z = lo.Z + (hi.Z - lo.Z) * u, v = key.V;
                    (Vector3 n, Vector3 across, Vector3 point) = key.Face switch
                    {
                        InspectFace.Right => (Vector3.UnitX, Vector3.UnitY, new Vector3(hi.X, lo.Y + (hi.Y - lo.Y) * v, z)),
                        InspectFace.Top => (Vector3.UnitY, Vector3.UnitX, new Vector3(lo.X + (hi.X - lo.X) * v, hi.Y, z)),
                        InspectFace.Bottom => (-Vector3.UnitY, Vector3.UnitX, new Vector3(lo.X + (hi.X - lo.X) * v, lo.Y, z)),
                        _ => (-Vector3.UnitX, Vector3.UnitY, new Vector3(lo.X, lo.Y + (hi.Y - lo.Y) * v, z)),
                    };
                    turn = Facing(rig, offAt, off, new Vector2(n.X, n.Y));
                    if (key.Spin != 0f) turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(n, key.Spin * MathF.PI / 180f) * turn);
                    // (`tips`: the pads of its index and middle fingers over the point instead, the fingers' own thickness off the face:
                    // the palm stays a hand's length back from it and touches nothing.)
                    var palm = Vector3.Transform(key.Tips ? Pads : Palm, turn);
                    at = point + n * ((key.Tips ? InspectHand.Radius : PalmSkin) + key.Offset.X) + across * key.Offset.Y + Vector3.UnitZ * key.Offset.Z - palm;
                    return;
                }
            // Under the muzzle end of the gun's body (its meshes no part moves) where it is, the palm up against it.
            case InspectHold.Muzzle:
                {
                    var z = !float.IsNaN(along) ? along : Along(0.97f);
                    Section(rig, z, true, out var lo, out var hi, offAt.X);
                    turn = Facing(rig, offAt, off, -Vector2.UnitY);
                    at = Settle(new Vector3((lo.X + hi.X) / 2f, lo.Y - PalmSkin, z) - Vector3.Transform(Palm, turn), turn, -Vector3.UnitY);
                    return;
                }
            case InspectHold.Side:
            case InspectHold.Top:
                {
                    // On the left or the top of the gun's body (its meshes that no part moves) where it is at the key's place, not of its bounds.
                    var top = key.On == InspectHold.Top;
                    var z = !float.IsNaN(along) ? along : Along(key.Along);
                    Section(rig, z, true, out var lo, out var hi);
                    turn = Facing(rig, offAt, off, top ? Vector2.UnitY : -Vector2.UnitX);
                    at = Settle((top ? new Vector3((lo.X + hi.X) / 2f, hi.Y + PalmSkin, z) : new Vector3(lo.X - PalmSkin, (lo.Y + hi.Y) / 2f, z)) - Vector3.Transform(Palm, turn),
                        turn, top ? Vector3.UnitY : -Vector3.UnitX);
                    return;
                }
            default: at = offAt; return;
        }

        Vector3 Below(float z)
        {
            var palm = Vector3.Transform(Palm, off);
            Section(measured, z, true, out var lo, out var hi, offAt.X + palm.X);
            return new Vector3(Math.Clamp(offAt.X + palm.X, lo.X, hi.X), lo.Y - PalmSkin, z) - palm;
        }
    }

    // A hand placed on a face of a section's box (`n` out of it), its palm brought in until its middle is PalmSkin off the nearest mesh
    // where the box's face leaves it farther (the meshes there narrower than the section's box: a pistol's slide over its frame); never
    // past SettleMost, and only while that brings it nearer.
    const float SettleMost = 0.05f;
    Vector3 Settle(Vector3 at, Quaternion turn, Vector3 n)
    {
        float Off(Vector3 wrist) => Gap(wrist + Vector3.Transform(Palm, turn), Vector3.Zero, Quaternion.Identity, []) - PalmSkin;
        var start = at;
        for (var k = 0; k < 4; k++)
        {
            var gap = Off(at);
            if (gap <= 0.002f) break;
            var next = at - n * MathF.Min(gap, SettleMost - Vector3.Distance(at, start));
            if (Off(next) >= gap) break;
            at = next;
        }
        return at;
    }

    // The gun's meshes (`body`: those no part moves, if any) across its length at `z`, as one box: where the hand finds its surface there.
    // Where no mesh crosses `z` (or none is measured), the gun's bounds.
    // `x`: only meshes over or under that point across the gun (within SectionAcross; a spent shell hanging off to the side is not
    // under the barrel), where any are.
    const float SectionAcross = 0.02f;
    static void Section(in InspectRig rig, float z, bool body, out Vector3 lo, out Vector3 hi, float x = float.NaN)
    {
        (lo, hi) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        for (var pass = 0; pass < 4 && lo.X > hi.X; pass++)
            foreach (var box in rig.Meshes ?? [])
                if (box.Lo.Z <= z && z <= box.Hi.Z && (!body || pass % 2 == 1 || box.Part < 0)
                    && (pass >= 2 || float.IsNaN(x) || (box.Lo.X - SectionAcross <= x && x <= box.Hi.X + SectionAcross)))
                    (lo, hi) = (Vector3.Min(lo, box.Lo), Vector3.Max(hi, box.Hi));
        if (lo.X > hi.X) (lo, hi) = (rig.Centre - rig.Extents, rig.Centre + rig.Extents);
    }

    // The off hand's grip turned about the bore until its palm faces a face of the gun whose way out is `toward` (in the gun's x, y):
    // the palm's own facing across the bore turned onto the way in; a palm that faces along the bore (none across it to speak of)
    // by where the hand is from the gun's middle instead.
    static Quaternion Facing(in InspectRig rig, Vector3 offAt, Quaternion off, Vector2 toward)
    {
        var palm = Vector3.Transform(-Vector3.UnitY, off);
        float rx = offAt.X - rig.Centre.X, ry = offAt.Y - rig.Centre.Y, dx = toward.X, dy = toward.Y;
        if (palm.X * palm.X + palm.Y * palm.Y > 0.25f) (rx, ry) = (-palm.X, -palm.Y);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(rx * dy - ry * dx, rx * dx + ry * dy)) * off;
    }

    // The gun's weight in the hand (the kind's feel, InspectFeel), the same for every motion: per FollowStep of the look, how far the
    // pose shown is from the keyed one (the grip point's shift in view space and the turn about it). Three things make it:
    // the carried weight, a spring (F, Zeta) between the hand and the gun that only the keys' acceleration drives, so the gun hangs
    // back as the hand starts, goes past where the hand stops and settles, and at a steady rate is where the keys have it (a key is
    // reached when it says, with no lead to make up); the draw back before a large turn (Winds); and a hold that is never dead still
    // (Sway). Each knock (Knocks) pushes it down and dips the muzzle. It strays at most about FollowMost degrees from the keyed turn,
    // softly, never takes the grip wrist past its limits or past where its keys have it (WristKept), and is back on the keyed pose
    // over the last FollowLast seconds.
    Vector3[]? _followAt;
    Quaternion[]? _followTurn;
    const float FollowStep = 1f / 240f, FollowMost = 14f, FollowLast = 0.15f;

    void Follow()
    {
        if (!(_feel.F > 0f)) return;
        float f = _feel.F, z = _feel.Zeta, w = 2f * MathF.PI * f, k1 = 2f * z / w, k2 = 1f / (w * w), h = FollowStep, carry = _feel.Carry;
        var k2s = MathF.Max(k2, MathF.Max(h * h / 2f + h * k1 / 2f, h * k1));
        // How far one push of speed v carries it at most: v / w times this (an underdamped spring's impulse response at its height).
        var damped = MathF.Sqrt(MathF.Max(1e-3f, 1f - MathF.Min(z, 0.99f) * MathF.Min(z, 0.99f)));
        var height = MathF.Exp(-MathF.Min(z, 0.99f) / damped * MathF.Atan2(damped, MathF.Min(z, 0.99f)));
        var n = (int)MathF.Ceiling(Seconds / h) + 2;
        var followAt = new Vector3[n];
        var followTurn = new Quaternion[n];
        var knocks = Knocks();
        var winds = Winds();
        var next = 0;
        var toGame = Quaternion.Inverse(_rig.Gun);
        // What the hand does at `time`: the keyed pose (`keyed`, `keyedAt`) with the draw back before a large turn laid on its turn.
        Vector3 Hand(float time, Vector3 lastTurn, out Vector3 turned, out Quaternion keyed, out Vector3 keyedAt)
        {
            Keyed(Math.Clamp(time, 0f, Seconds), 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out _, out keyedAt, out keyed, out _, out _, out _, out _);
            var turn = keyed;
            foreach (var wind in winds)
                if (wind.Share(time) is var share && share > 0f) turn = Quaternion.Normalize(Exp(wind.Back * share) * turn);
            turned = Unwind(Log(turn * toGame), lastTurn);
            return keyedAt + Vector3.Transform(_gripAt, keyed);
        }
        var y = Hand(0f, Vector3.Zero, out var yTurn, out _, out _);
        var input = y;
        var inputTurn = yTurn;
        Vector3 speed = Vector3.Zero, spin = Vector3.Zero;
        for (var i = 0; i < n; i++)
        {
            var time = i * h;
            var x = Hand(time, inputTurn, out var xTurn, out var keyedQ, out var keyedAt);
            Vector3 dx = (x - input) / h, dxTurn = (xTurn - inputTurn) / h;
            (input, inputTurn) = (x, xTurn);
            for (; next < knocks.Count && knocks[next].Time <= time; next++)
            {
                speed -= Vector3.UnitY * (knocks[next].Metres * w / height);
                spin += Vector3.Transform(Vector3.UnitX, keyedQ) * (knocks[next].Degrees * MathF.PI / 180f * w / height);
            }
            y += h * speed;
            yTurn += h * spin;
            // The spring holds the gun to the hand and the damper to the hand's own rate, so only a change of that rate moves it off.
            speed += h * (x - y + k1 * (dx - speed)) / k2s;
            spin += h * (xTurn - yTurn + k1 * (dxTurn - spin)) / k2s;
            var back = time > Seconds - FollowLast ? Smooth((Seconds - time) / FollowLast) : 1f;
            var dq = Quaternion.Normalize(Exp(xTurn + (yTurn - xTurn) * carry) * _rig.Gun * Quaternion.Inverse(keyedQ));
            if (dq.W < 0f) dq = -dq;
            var angle = 2f * MathF.Acos(Math.Min(1f, dq.W)) * 180f / MathF.PI;
            var most = FollowMost * MathF.Tanh(angle / FollowMost);
            dq = Quaternion.Slerp(Quaternion.Identity, dq, angle > 1e-4f ? most * back / angle : back);
            var dp = (y - x) * (carry * back);
            var alive = back * Smooth(time / SwayIn) * Smooth((Seconds - time) / SwayIn);
            dq = Quaternion.Normalize(Exp(SwayTurn(time) * alive) * dq);
            followTurn[i] = Quaternion.Slerp(Quaternion.Identity, dq, WristKept(keyedAt, keyedQ, dp, dq));
            followAt[i] = dp + SwayAt(time) * alive;
        }
        (_followAt, _followTurn) = (followAt, followTurn);
    }

    // The share (0..1) of a turn `offset` about the grip point, laid over the keyed pose with the grip point shifted by `shift`, that
    // the grip wrist takes: all of it while three times the turn would still leave the wrist within WristOver degrees of its limits
    // (InspectArm.Past; or of where the keyed pose itself has it, past them), and less and less of it from there on, so a gun
    // that arrives at the end of the wrist's reach is slowed into it instead of stopped dead. (The last Keyed call set where the hand
    // holds the gun and its elbow's swivel.) Twice WristOver (the follow's and a toss's cock, one over the other) is inside what rule W
    // lets a wrist past its limits (WristPast).
    const float WristOver = 0.9f;

    float WristKept(Vector3 keyedAt, Quaternion keyed, Vector3 shift, Quaternion offset)
    {
        var grip = keyedAt + Vector3.Transform(_gripAt, keyed) + shift;
        float Past(float share)
        {
            var turn = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, offset, share) * keyed);
            return _arm.Past(grip - Vector3.Transform(_gripAt, turn) + Vector3.Transform(_heldGripAt, turn), turn * _grip, HeldSwivel);
        }
        var most = Past(0f) + WristOver;
        bool Within(float share) => Past(share) <= most;
        if (Within(3f)) return 1f;
        var (lo, hi) = (0f, 3f);
        for (var k = 0; k < 7; k++)
        {
            var mid = (lo + hi) / 2f;
            if (Within(mid)) lo = mid;
            else hi = mid;
        }
        return lo * (6f - lo) / 9f;
    }

    // Anticipation: before a turn of at least LargeTurn degrees between two keys the gun stops at (not into a toss or a twirl, whose
    // hand has its own, nor out of a key it runs through), the hand draws the gun back against the turn by WindShare of it, at most
    // the kind's Wind degrees: rising over WindBefore before the key it leaves (while the pose before it is still held), at its height
    // as the turn starts and gone WindAfter later, as the turn takes over. Measured on the way the keys take, so a turn that goes
    // the long way round is drawn back against the way it goes.
    public const float LargeTurn = 45f;
    const float WindShare = 0.04f, WindBefore = 0.16f, WindAfter = 0.14f;

    readonly record struct Wind(float From, float Peak, Vector3 Back)
    {
        public float Share(float time) => time <= From || time >= Peak + WindAfter ? 0f
            : time < Peak ? Smooth((time - From) / (Peak - From)) : 1f - Smooth((time - Peak) / WindAfter);
    }

    List<Wind> Winds()
    {
        var winds = new List<Wind>();
        var keys = Motion.Gun;
        if (!(_feel.Wind > 0f)) return winds;
        Quaternion At(float time)
        {
            Keyed(Math.Clamp(time, 0f, Seconds), 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out _, out _, out var turn, out _, out _, out _, out _);
            return turn;
        }
        for (var k = 0; k + 1 < keys.Length; k++)
        {
            if (keys[k + 1].Toss > 0f || Motion.IsTwirl(k + 1) || keys[k].Through) continue;
            float from = keys[k].Time * Slow, to = keys[k + 1].Time * Slow, angle = 0f;
            const int Steps = 8;
            var last = At(from);
            var first = Vector3.Zero;
            for (var j = 1; j <= Steps; j++)
            {
                var now = At(from + (to - from) * j / Steps);
                var step = Log(Quaternion.Normalize(now * Quaternion.Inverse(last)));
                // (The way starts at no speed: its direction is read a quarter of the way in.)
                if (j == 2) first = step;
                angle += step.Length() * 180f / MathF.PI;
                last = now;
            }
            if (angle < LargeTurn || first.LengthSquared() < 1e-10f) continue;
            var rise = MathF.Max(0f, from - WindBefore);
            if (k > 0) rise = MathF.Max(rise, (keys[k - 1].Time * Slow + from) / 2f);
            winds.Add(new Wind(rise, MathF.Max(from, rise + 0.1f), -Vector3.Normalize(first) * (MathF.Min(_feel.Wind, WindShare * angle) * MathF.PI / 180f)));
        }
        return winds;
    }

    // A hold that lives: the hand is never dead still. A slow sway of the gun about its grip (SwayDegrees) and of the hand
    // (SwayMetres), three unrelated rates each so it does not read as a cycle, growing in over SwayIn and gone by the look's end.
    const float SwayDegrees = 0.6f, SwayMetres = 0.0015f, SwayIn = 0.4f;

    static Vector3 SwayTurn(float time)
    {
        const float Turn = 2f * MathF.PI;
        return new Vector3(MathF.Sin(Turn * 0.43f * time + 0.6f), 0.7f * MathF.Sin(Turn * 0.31f * time + 2.1f), 0.8f * MathF.Sin(Turn * 0.53f * time + 4f)) * (SwayDegrees * MathF.PI / 180f);
    }

    static Vector3 SwayAt(float time)
    {
        const float Turn = 2f * MathF.PI;
        return new Vector3(0.6f * MathF.Sin(Turn * 0.37f * time + 1.3f), MathF.Sin(Turn * 0.47f * time + 0.2f), 0f) * SwayMetres;
    }

    static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    // The follow's shift and turn at `time`, between its steps.
    void Followed(float time, out Vector3 shift, out Quaternion turn)
    {
        var at = Math.Clamp(time / FollowStep, 0f, _followAt!.Length - 1.001f);
        var i = (int)at;
        var s = at - i;
        shift = Vector3.Lerp(_followAt[i], _followAt[i + 1], s);
        turn = Quaternion.Slerp(_followTurn![i], _followTurn[i + 1], s);
    }

    // A rotation as a vector (axis times angle in radians), and back; Unwind picks of its two ways round the one nearer `last`.
    static Vector3 Log(Quaternion q)
    {
        if (q.W < 0f) q = -q;
        var half = MathF.Acos(Math.Min(1f, q.W));
        var sin = MathF.Sin(half);
        return sin < 1e-6f ? Vector3.Zero : new Vector3(q.X, q.Y, q.Z) * (2f * half / sin);
    }

    static Quaternion Exp(Vector3 v)
    {
        var angle = v.Length();
        return angle < 1e-6f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(v / angle, angle);
    }

    static Vector3 Unwind(Vector3 v, Vector3 last)
    {
        var angle = v.Length();
        if (angle < 1e-6f) return v;
        var other = v - v / angle * (2f * MathF.PI);
        return Vector3.DistanceSquared(other, last) < Vector3.DistanceSquared(v, last) ? other : v;
    }

    // The knocks the gun takes (real seconds into the look, degrees the muzzle dips, metres it drops): a part slamming into a stop (a
    // spring key) and a key that is a hit, at the kind's push, and a twirl stopping in the hand.
    const float SameKnock = 0.02f;
    List<(float Time, float Degrees, float Metres)> Knocks()
    {
        var list = new List<(float, float, float)>();
        foreach (var part in Motion.Parts)
            foreach (var key in part.Keys)
                // Parts slamming home together (a bolt with its handle) are one knock.
                if (key.Ease == InspectEase.Spring && !list.Any(k => MathF.Abs(k.Item1 - key.Time * Slow) < SameKnock)) list.Add((key.Time * Slow, _feel.HitDegrees, _feel.HitMetres));
        for (var i = 0; i < Motion.Gun.Length; i++)
        {
            if (Motion.Gun[i].Hit) list.Add((Motion.Gun[i].Time * Slow, _feel.HitDegrees, _feel.HitMetres));
            if (Motion.IsTwirl(i) && !Motion.Gun[i].Through) list.Add((Motion.Gun[i].Time * Slow, TwirlDip, TwirlDrop));
        }
        list.Sort((p, q) => p.Item1.CompareTo(q.Item1));
        return list;
    }

    // The rules of a look that shows a gun in the hands, on the followed pose, the first broken (named R1, R3, R5, R6, R7 or speed) and when
    // (`at`, NaN when none is); R1, R3 and R6 also at every hand and part key's own time:
    // R1, where both keys of the off hand's span put it on the gun, it is at most TouchGap from what it touches (ContactGap);
    // R3, a part out of its place the off hand holds moves at most SlipMost in the hand from where the hold began, except over a
    // declared regrip (InspectHandKey.Regrip), after which the hold begins again;
    // R5, every pose shown (a key of the motion's own the gun does not go through, with the keys after it that turn it by at most
    // PoseTurn degrees in all, move its centre by at most PoseAt of the view and lift it by at most PoseRise) holds still (turning under
    // StillTurn degrees and moving under StillMove metres a second) for StillFor seconds, between PoseBefore before its first key and
    // the key after its last;
    // R6, neither forearm (a capsule of ForearmRadius) nor palm (a ball of PalmRadius) sinks into a mesh of the gun more than SinkSlack
    // deeper than in the game's own hold;
    // R7, in the centre of the view (CentreBox of its half width and height, Rays by Rays rays) the arms cover at most ArmsShown of it
    // while the gun holds still (a pose shown) and the off hand is idle there (Idle: neither on the gun nor moving); hands touching the
    // gun, working a part or on their way may hide it (the gun itself is not counted: the middle of the view is where the look shows
    // it); a part held in the view
    // covers at most HeldMost of the centre and its centre stays HeldNear from the eye (an inspect may hide the middle of the view a
    // while, as any input stops it within FadeOut);
    // speed, a gun in the hand (not tossed or twirled) turns at most its kind's Spin degrees a second.
    public const float PoseTurn = 10f, PoseAt = 0.1f, PoseRise = 0.02f, StillTurn = 25f, StillMove = 0.06f, StillFor = 0.4f, PoseBefore = 0.3f,
        ForearmRadius = 0.035f, PalmRadius = 0.045f, SinkSlack = 0.015f, CentreBox = 0.3f, ArmsShown = 0.3f,
        HeldMost = 0.4f, HeldNear = 0.3f, SlipMost = 0.005f;
    const int Rays = 20;

    string? Rules(out float at)
    {
        at = float.NaN;
        var frames = (int)(Seconds / Step) + 1;
        var still = new bool[frames];
        var offInGun = Vector3.Transform(_rig.OffAt - _rig.GunAt, Quaternion.Inverse(_rig.Gun));
        var offTurnInGun = Quaternion.Inverse(_rig.Gun) * _rig.Off;
        var moved = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        // How deep each arm sinks into the gun in the game's own hold.
        GunPose(0f, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
        Moved(0f, moved);
        var gripSunk = Sunk(_rig.GripArm.Elbow(_rig.GripAt), _rig.GripAt, _rig.Grip, gunAt, gun, moved, float.PositiveInfinity);
        var lastCentre = Vector3.Zero;
        var lastTurn = Quaternion.Identity;
        // The rules of where the arms are (R6, R1, R3) need no even frames: they are measured every other frame and at every key's own
        // time as well (a pose that breaks only on a key's frame is caught), in time order; the rules of motion below keep even frames.
        var (touchAt, touch) = (float.NaN, (string?)null);
        var (slipPart, slipFrom) = (-1, Vector3.Zero); // R3: where the part the hand holds sits in the hand when the hold began
        var times = Enumerable.Range(0, frames).Where(f => f % 2 == 0).Select(f => f * Step)
            .Concat(Motion.Hand.Select(k => k.Time * Slow)).Concat(Motion.Parts.SelectMany(p => p.Keys).Select(k => k.Time * Slow))
            .Where(t => t <= Seconds).Distinct().OrderBy(t => t);
        foreach (var time in times)
            if (Touch(time) is { } broken) { (touchAt, touch) = (time, broken); break; }
        string? Touch(float time)
        {
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out var view);
            Moved(time, moved);
            GripPose(HeldAt, HeldTurn, out var grip, out var gripTurn);
            Vector3 off;
            Quaternion offTurn;
            var offHand = HandOff(time);
            if (offHand) OffPose(time, gunAt, gun, out off, out offTurn);
            else (off, offTurn) = (gunAt + Vector3.Transform(offInGun, gun), gun * offTurnInGun);
            if (HandSeen(grip, gripTurn, view) && WristBreaks(_arm, grip, gripTurn, HeldSwivel, "grip", _arm.Fold(_rig.GripAt, _rig.Grip)) is { } gw) return gw;
            if (offHand && HandSeen(off, offTurn, view) && WristBreaks(_off, off, offTurn, OffSwivel, "off", _off.Fold(_rig.OffAt, _rig.Off)) is { } ow) return ow;

            if (!Free && Sunk(GripElbow(), grip, gripTurn, gunAt, gun, moved, view) - gripSunk is var gs && gs > SinkSlack) return $"R6: the grip arm sinks {gs * 100f:0.0} cm deeper into the gun than in the game's hold";
            if (offHand && OffDeeper(off, offTurn, gunAt, gun, moved, view) is var os && os > SinkSlack) return $"R6: the off arm sinks {os * 100f:0.0} cm deeper into the gun than in the game's hold";
            if (offHand && ContactGap(time, off, offTurn, gunAt, gun) is var cg && cg > TouchGap) return $"R1: the off hand is {cg * 100f:0.0} cm from what it touches";
            // (A part out of its place: one at rest is the gun's, and a hand may wipe over it.)
            var holding = offHand ? ContactOf(time) : -2;
            if (holding >= 0 && Held(holding, time) <= 0f) holding = -2;
            Motion.HandSpan(time / Slow, out var ha, out var hb, out _);
            if (holding < 0 || (ha != hb && Motion.Hand[hb].Regrip)) slipPart = -1;
            else
            {
                var box = _rig.Parts![holding];
                var inHand = Vector3.Transform(gunAt + Vector3.Transform(Vector3.Transform((box.Lo + box.Hi) / 2f, moved[holding].R) + moved[holding].T, gun) - off, Quaternion.Inverse(offTurn));
                if (slipPart != holding) (slipPart, slipFrom) = (holding, inHand);
                else if (Vector3.Distance(inHand, slipFrom) is var slip && slip > SlipMost)
                    return $"R3: the {Motion.Parts[holding].Node} slips {slip * 100f:0.0} cm in the hand (a change of hold is a regrip)";
            }
            return null;
        }
        // The first broken rule, of either kind, is the one reported.
        string First(float time, string rule, out float when)
        {
            (when, rule) = touch != null && touchAt <= time ? (touchAt, touch) : (time, rule);
            return rule;
        }
        // A key that asks the grip wrist past its limits shows the gun held back from it (Solve): that key is not what the player sees.
        for (var k = 0; k < KeyHeldBack.Length; k++)
            if (KeyHeldBack[k] > KeyBack) { at = Motion.Gun[k].Time * Slow; return $"W: gun key {k} asks the grip wrist {KeyHeldBack[k]:0} degrees past its limits (the gun is held back)"; }
        var inertia = Inertia(first: true);
        for (var f = 0; f < frames; f++)
        {
            var time = f * Step;
            if (inertia.Count > 0 && inertia[0].Time <= time) return First(inertia[0].Time, inertia[0].Rule, out at);
            var idle = f % 2 == 0 && Idle(time);
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out gunAt, out gun, out var view);
            var centre = gunAt + Vector3.Transform(_centre, gun);
            if (f > 0)
            {
                var dq = Quaternion.Normalize(Quaternion.Inverse(lastTurn) * gun);
                var turning = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(dq.W))) * 180f / MathF.PI / Step;
                still[f] = turning < StillTurn && Vector3.Distance(centre, lastCentre) / Step < StillMove;
                if (!Free && !Beating && !Twirling && _feel.Spin > 0f && turning > _feel.Spin + 1e-3f)
                    return First(time, $"speed: the gun turns {turning:0} degrees a second in the hand, past its kind's {_feel.Spin:0}", out at);
            }
            (lastCentre, lastTurn) = (centre, gun);
            if (f % 2 != 0) continue;
            Moved(time, moved);
            GripPose(HeldAt, HeldTurn, out var grip, out _);
            var gripElbow = GripElbow();
            Vector3 off;
            if (HandOff(time)) OffPose(time, gunAt, gun, out off, out _);
            else (off, OffSwivel) = (gunAt + Vector3.Transform(offInGun, gun), SwivelAt(time));
            var offElbow = _off.Elbow(off, OffSwivel);
            bool Arms(Vector3 d) => HitsCapsule(d, _rig.GripArm.Shoulder, gripElbow, 0.05f) || HitsCapsule(d, gripElbow, grip, 0.04f) || HitsCapsule(d, grip, grip, 0.055f)
                || HitsCapsule(d, _rig.OffArm.Shoulder, offElbow, 0.05f) || HitsCapsule(d, offElbow, off, 0.04f) || HitsCapsule(d, off, off, 0.055f);
            var arms = Cover(view, Arms);
            if (still[f] && idle && arms > ArmsShown) return First(time, $"R7: the arms cover {arms:P0} of the middle of the view, the off hand idle while the gun is shown", out at);
            for (var p = 0; p < Motion.Parts.Length; p++)
            {
                if (CarriedShare(p, time) < 0.5f) continue;
                var rig = _rig.Parts![p];
                PartWorld(p, time, 1f, out var pr, out var pt);
                var heldTurn = gun * pr;
                var heldAt = gunAt + Vector3.Transform(pt, gun);
                var heldCentre = heldAt + Vector3.Transform((rig.Lo + rig.Hi) / 2f, heldTurn);
                if (heldCentre.Length() < HeldNear - 1e-3f) return First(time, $"R7: the {Motion.Parts[p].Node} held up comes {heldCentre.Length():0.00} m from the eye", out at);
                var share = Cover(view, d => HitsBox(d, heldAt, heldTurn, (rig.Lo + rig.Hi) / 2f, (rig.Hi - rig.Lo) / 2f, _rig.Near));
                if (share > HeldMost) return First(time, $"R7: the {Motion.Parts[p].Node} held up covers {share:P0} of the middle of the view", out at);
            }
        }
        if (touch != null) { at = touchAt; return touch; }
        // (An inertia fault at the motion's very end, past the last frame sampled.)
        if (inertia.Count > 0) { at = inertia[0].Time; return inertia[0].Rule; }
        // R5: each pose shown holds still long enough.
        var keys = Motion.Gun;
        static bool Shown(InspectGunKey k) => !k.Rest && !k.Through;
        for (var i = 0; i < keys.Length; i++)
        {
            if (!Shown(keys[i]) || (i > 0 && Shown(keys[i - 1]) && Same(keys[i - 1], keys[i]))) continue;
            var last = i;
            while (last + 1 < keys.Length && Shown(keys[last + 1]) && Same(keys[last], keys[last + 1])) last++;
            float from = keys[i].Time * Slow - PoseBefore, to = keys[Math.Min(last + 1, keys.Length - 1)].Time * Slow;
            float run = 0f, best = 0f;
            for (var f = Math.Max(0, (int)(from / Step)); f < frames && f * Step <= to; f++)
                best = MathF.Max(best, run = still[f] ? run + Step : 0f);
            if (best < StillFor - 1e-3f)
            {
                at = keys[i].Time * Slow;
                return $"R5: the pose at {keys[i].Time:0.00} s of the motion holds still for {best:0.00} s";
            }
        }
        at = float.NaN;
        return null;
    }

    // W, the wrist (InspectArm.Joint): neither hand bends further on its forearm than the wrist's comfortable limits (InspectArm: flexion,
    // extension, radial and ulnar deviation, each at least as far as the game's own hold bends it) nor twists the forearm more than
    // InspectArm.Twist from that hold, give or take WristPast degrees. Measured where R1, R3 and R6 are (every 30th of a second and at
    // every hand and part key's own time).
    public const float WristPast = 2f;
    // And neither a gun key nor the way between two asks the grip wrist more than KeyBack degrees past its limits (the gun is held back by
    // as much, Solve and Wrist): the solve picks a key's place within 2 of them where one is (Cost), and no motion of the table is held
    // back more than 2 at a key or on the way.
    public const float KeyBack = 5f;
    // And the off hand is turned no more than OffBack degrees off the turn its hold asks (the wrist's limits turn it back about the wrist,
    // OffPose): past that the hand visibly lets go of the shape of what it holds. The motions the owner holds good turn it back by 0;
    // OffBack is a judgement (half the wrist's smaller bend range), not a measured human bound.
    public const float OffBack = 30f;
    // And neither hand lies further off its forearm's line than a right angle (InspectArm.Fold: the owner's bound, no wrist bent past
    // 90 degrees), or than the game's own hold has it where that is more (the first-person arms hold some guns with the hand 100 to
    // 120 degrees off the forearm): the joint's angles above are counted from that hold, so a hand at their limits from a hold
    // already folded hangs off its forearm at more than a right angle, which they do not see.
    public const float WristFold = 90f;

    string OffTurnedOff() => $"W: the off hand is turned {OffHeldBack:0} degrees off its hold (its wrist cannot follow the gun)";

    static string? WristBreaks(in InspectArm arm, Vector3 hand, Quaternion turn, float swivel, string which, float held)
    {
        var (f, d, r) = arm.Joint(hand, turn, swivel);
        var (flex, ext, rad, uln) = (InspectArm.Flexion, InspectArm.Extension, InspectArm.Radial, InspectArm.Ulnar);
        if (f > flex + WristPast) return $"W: the {which} wrist flexes {f:0} degrees (its limit {flex:0})";
        if (-f > ext + WristPast) return $"W: the {which} wrist extends {-f:0} degrees (its limit {ext:0})";
        if (d > rad + WristPast) return $"W: the {which} wrist bends {d:0} degrees toward the thumb (its limit {rad:0})";
        if (-d > uln + WristPast) return $"W: the {which} wrist bends {-d:0} degrees toward the little finger (its limit {uln:0})";
        if (MathF.Abs(r) > InspectArm.Twist + WristPast) return $"W: the {which} forearm turns {MathF.Abs(r):0} degrees from the game's hold (its limit {InspectArm.Twist:0})";
        if (arm.Fold(hand, turn, swivel) is var fold && fold > MathF.Max(WristFold, held) + WristPast)
            return $"W: the {which} hand lies {fold:0} degrees off its forearm's line (at most {MathF.Max(WristFold, held):0})";
        return null;
    }

    // I, inertia, on the gun as the player sees it (the followed pose) and on every part the motion moves, at 60 a second: a body that
    // moves does not start, stop or turn back at once. From one 60th of a second to the next the gun's angular velocity changes by at
    // most TurnAccel degrees a second per second and its centre's velocity by at most MoveAccel metres a second per second, and that
    // change itself changes by at most TurnJerk and MoveJerk a second (the jerk: a speed that starts or stops in one frame has a
    // spike there however small the speed); neither turns back within one frame while it moves faster than ReverseSpeed degrees (or
    // ReverseMove metres) a second. A part moves on the gun the same way (its box centre and its turn, PartAccel, PartJerk). A knock
    // (a hit key, a spring part slamming home, a twirl stopping, a toss landing in the hand) is an impact and is let through for
    // KnockFor after it, and a gun free of the hand (tossed, twirled) moves by its own physics. And a turn between two keys of at least
    // LargeTurn degrees draws back before it (anticipation) or goes past its end and settles back (overshoot), by at least LeastLead of
    // the turn either way.
    // The gun's bounds are 1.5 times the most the motions the owner holds good reached outside a knock under the earlier follow (the
    // classic left and right look of each kind and the revolver's twirl, on the test rigs and every game gun of their kinds: 1724
    // degrees a second per second, 6.8 m a second per second, 44535 degrees and 287 m a second cubed; their turns went past their
    // ends by 0.9% at least). The parts' are a hand's
    // own quick movement, a minimum-jerk reach (Flash and Hogan, The coordination of arm movements, J Neurosci 5:1688-1703, 1985: peak
    // acceleration 5.77 d/T², peak jerk 60 d/T³): 10 cm in 0.2 s, and a quarter turn in 0.15 s.
    public const float TurnAccel = 2600f, MoveAccel = 10f, TurnJerk = 67000f, MoveJerk = 430f, ReverseSpeed = 30f, ReverseMove = 0.1f,
        PartAccel = 15f, PartJerk = 750f, PartTurnAccel = 23000f, KnockFor = 0.2f, LeastLead = 0.005f;

    // The inertia faults of the look in time order (only the first when `first`), each with its rule text.
    List<(float Time, string Rule)> Inertia(bool first) => Inertia(first, out _);

    // The same, and the largest of each measure outside a knock (for the bounds above): turn and move acceleration and jerk, part
    // acceleration and jerk.
    public List<(float Time, string Rule)> Inertia(bool first, out (float TurnAccel, float MoveAccel, float TurnJerk, float MoveJerk, float PartAccel, float PartJerk, float PartTurnAccel, float Lead) peak)
    {
        var faults = new List<(float, string)>();
        peak = default;
        var n = (int)(Seconds / Step) + 1;
        var knocks = Knocks().Select(k => k.Time).ToList();
        for (var k = 1; k < Motion.Gun.Length; k++) if (Motion.Gun[k].Toss > 0f) knocks.Add(Motion.Gun[k].Time * Slow);
        bool Knocked(float time) => knocks.Any(k => time >= k - Step && time <= k + KnockFor);
        var centres = new Vector3[n];
        var turns = new Quaternion[n];
        var free = new bool[n];
        int parts = Motion.Parts.Length;
        var partAt = new Vector3[n, parts];
        var partTurn = new Quaternion[n, parts];
        for (var f = 0; f < n; f++)
        {
            var time = f * Step;
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            (centres[f], turns[f], free[f]) = (gunAt + Vector3.Transform(_centre, gun), gun, Free || Beating || Twirling);
            for (var p = 0; p < parts; p++)
            {
                var box = _rig.Parts![p];
                PartWorld(p, time, 1f, out var r, out var t);
                (partAt[f, p], partTurn[f, p]) = (Vector3.Transform((box.Lo + box.Hi) / 2f, r) + t, r);
            }
        }
        void Fault(float time, string rule) { if (!first || faults.Count == 0) faults.Add((time, rule)); }
        const float Deg = 180f / MathF.PI;
        Vector3 Spin(Quaternion from, Quaternion to) => Log(Quaternion.Normalize(to * Quaternion.Inverse(from))) * (Deg / Step);
        // A body at f from its places at f-3..f: velocity, its change (acceleration) and the change of that (jerk).
        for (var f = 3; f < n; f++)
        {
            var time = f * Step;
            if (Knocked(time) || Knocked(time - 2f * Step)) continue;
            if (!(free[f] || free[f - 1] || free[f - 2] || free[f - 3]))
            {
                Vector3 w0 = Spin(turns[f - 3], turns[f - 2]), w1 = Spin(turns[f - 2], turns[f - 1]), w2 = Spin(turns[f - 1], turns[f]);
                Vector3 v0 = (centres[f - 2] - centres[f - 3]) / Step, v1 = (centres[f - 1] - centres[f - 2]) / Step, v2 = (centres[f] - centres[f - 1]) / Step;
                float ta = (w2 - w1).Length() / Step, ma = (v2 - v1).Length() / Step;
                float tj = ((w2 - w1) - (w1 - w0)).Length() / (Step * Step), mj = ((v2 - v1) - (v1 - v0)).Length() / (Step * Step);
                peak = peak with { TurnAccel = MathF.Max(peak.TurnAccel, ta), MoveAccel = MathF.Max(peak.MoveAccel, ma), TurnJerk = MathF.Max(peak.TurnJerk, tj), MoveJerk = MathF.Max(peak.MoveJerk, mj) };
                if (Vector3.Dot(w1, w2) < 0f && w1.Length() > ReverseSpeed && w2.Length() > ReverseSpeed) Fault(time, $"I: the gun turns back at {w1.Length():0} degrees a second within one frame");
                else if (Vector3.Dot(v1, v2) < 0f && v1.Length() > ReverseMove && v2.Length() > ReverseMove) Fault(time, $"I: the gun moves back at {v1.Length():0.00} m a second within one frame");
                else if (ta > TurnAccel) Fault(time, $"I: the gun's turn changes by {ta:0} degrees a second per second (at most {TurnAccel:0})");
                else if (ma > MoveAccel) Fault(time, $"I: the gun's speed changes by {ma:0.0} m a second per second (at most {MoveAccel:0.0})");
                else if (tj > TurnJerk) Fault(time, $"I: the gun starts or stops turning at once (jerk {tj:0} degrees a second cubed, at most {TurnJerk:0})");
                else if (mj > MoveJerk) Fault(time, $"I: the gun starts or stops moving at once (jerk {mj:0} m a second cubed, at most {MoveJerk:0})");
            }
            for (var p = 0; p < parts; p++)
            {
                // (A part slamming home is a knock; Knocks counts its spring key.)
                Vector3 v0 = (partAt[f - 2, p] - partAt[f - 3, p]) / Step, v1 = (partAt[f - 1, p] - partAt[f - 2, p]) / Step, v2 = (partAt[f, p] - partAt[f - 1, p]) / Step;
                Vector3 w1 = Spin(partTurn[f - 2, p], partTurn[f - 1, p]), w2 = Spin(partTurn[f - 1, p], partTurn[f, p]);
                float pa = (v2 - v1).Length() / Step, pj = ((v2 - v1) - (v1 - v0)).Length() / (Step * Step), pt = (w2 - w1).Length() / Step;
                peak = peak with { PartAccel = MathF.Max(peak.PartAccel, pa), PartJerk = MathF.Max(peak.PartJerk, pj), PartTurnAccel = MathF.Max(peak.PartTurnAccel, pt) };
                if (pa > PartAccel) Fault(time, $"I: the {Motion.Parts[p].Node} changes speed by {pa:0.0} m a second per second (at most {PartAccel:0.0})");
                else if (pj > PartJerk) Fault(time, $"I: the {Motion.Parts[p].Node} starts or stops at once (jerk {pj:0} m a second cubed, at most {PartJerk:0})");
                else if (pt > PartTurnAccel) Fault(time, $"I: the {Motion.Parts[p].Node}'s turn changes by {pt:0} degrees a second per second (at most {PartTurnAccel:0})");
            }
        }
        // Anticipation or overshoot on every large turn between two keys the gun stops at (not a toss or a twirl: their own physics).
        var keys = Motion.Gun;
        var minLead = float.MaxValue;
        for (var k = 0; k + 1 < keys.Length; k++)
        {
            if (keys[k + 1].Through || keys[k + 1].Toss > 0f || Motion.IsTwirl(k + 1) || (k > 0 && keys[k].Through)) continue;
            int fa = Math.Clamp((int)MathF.Round(keys[k].Time * Slow / Step), 0, n - 1), fb = Math.Clamp((int)MathF.Round(keys[k + 1].Time * Slow / Step), 0, n - 1);
            Keyed(keys[k].Time * Slow, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out _, out _, out var qa, out _, out _, out _, out _);
            Keyed(keys[k + 1].Time * Slow, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out _, out _, out var qb, out _, out _, out _, out _);
            var way = Log(Quaternion.Normalize(qb * Quaternion.Inverse(qa)));
            var angle = way.Length() * Deg;
            if (angle < LargeTurn) continue;
            var axis = Vector3.Normalize(way);
            float Progress(int f) => Vector3.Dot(Unwind(Log(Quaternion.Normalize(turns[f] * Quaternion.Inverse(qa))), way), axis) * Deg / angle;
            // Drawn back before it (from a little before the key it leaves to half way) or past its end (from half way to the next key or
            // half a second after it).
            float back = 0f, past = 0f;
            int mid = (fa + fb) / 2, end = Math.Min(n - 1, k + 2 < keys.Length ? (int)(keys[k + 2].Time * Slow / Step) : fb + (int)(0.5f / Step));
            for (var f = Math.Max(0, fa - (int)(0.1f / Step)); f <= mid; f++) back = MathF.Max(back, -Progress(f));
            for (var f = mid; f <= end; f++) past = MathF.Max(past, Progress(f) - 1f);
            var lead = MathF.Max(back, past);
            minLead = MathF.Min(minLead, lead);
            if (lead < LeastLead) Fault(keys[k + 1].Time * Slow, $"I: the {angle:0}-degree turn into {keys[k + 1].Time:0.00} s neither draws back ({back:P1}) nor goes past and settles ({past:P1}); at least {LeastLead:P1}");
        }
        peak = peak with { Lead = minLead };
        faults.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return first && faults.Count > 1 ? faults.GetRange(0, 1) : faults;
    }

    // F, the grip hand's fingers. The thumb and the three fingers round the grip: the look has no surface of the gun to stop them on
    // (only the boxes of its meshes, and a grip sits inside its frame's box), so the one pose known to lie on the grip is the game's
    // own: a hand that holds the gun keeps it, and the look only opens them from it, for a gun that is leaving or coming back.
    // Reported: the look moving them on a hand that holds the gun still (more than FingersKept of the way off the game's grip,
    // outside HandlingBefore before and HandlingAfter after a twirl or a toss); a shape of the look's own that closes them (its mean
    // bend over FingersClosed: a generic curl goes through whatever it closes on); and fingers less than FingersOpen open while a
    // tossed gun turns into the hand. The index finger, which the look lays along the gun's side (IndexBend): reported once a look,
    // where it lies deepest, its middle line more than InspectHand.Inside inside a mesh's box as the motion's parts are moved then
    // (IndexOff: the boxes counted from the knuckle's own depth), and its skin more than TouchGap off every mesh no part moves.
    // The off hand's fingers have lines (InspectHand) and stop on the gun's meshes (FingerStop); reported: one whose middle line lies
    // more than InspectHand.Inside inside a mesh's box (OffFingerSunk), which only a hand placed into the gun does.
    public const float FingersKept = 0.05f, FingersClosed = 0.45f, FingersOpen = 0.6f, HandlingBefore = 0.1f, HandlingAfter = 0.35f;

    string? FingersBreak(float time)
    {
        var weight = GripFingersBaked(time, out var bend);
        if (weight > 0.3f && Mean(bend) > FingersClosed) return $"F: the look closes the grip hand's fingers in a shape of its own (bend {Mean(bend):0.00}, {weight:P0} over the game's grip), which no surface of the gun stops";
        var keys = Motion.Gun;
        var handling = false;
        for (var k = 1; k < keys.Length; k++)
        {
            float from = keys[k - 1].Time * Slow, to = keys[k].Time * Slow;
            if (keys[k].Toss > 0f)
            {
                var (release, _) = Flight(k);
                if (time > to - 0.05f && time < to + TossRide && weight < FingersOpen) return $"F: the grip hand's fingers are {weight:P0} open as the tossed gun turns into the hand";
                from = release;
            }
            else if (!Motion.IsTwirl(k)) continue;
            handling |= time > from - HandlingBefore && time < to + HandlingAfter;
        }
        return !handling && weight > FingersKept ? $"F: the look moves the grip hand's fingers {weight:P0} of the way off the game's grip while the hand holds the gun" : null;
    }

    // Rule F, the index finger: how deep its middle line lies in the gun's meshes at `time` where it lies along the gun's side, the
    // hand holding the gun (not on the way there, in the guard for a twirl or open for a toss: NaN).
    float IndexDeep(float time, Vector3 grip, Quaternion gripTurn, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved) =>
        !float.IsNaN(IndexBend) && IndexFinger(time, out var bend) > 0.999f && MathF.Abs(bend - IndexBend) < 1e-3f
            ? -IndexOff(bend, grip, gripTurn, gunAt, gun, moved, fixedOnly: false) - InspectHand.Radius : float.NaN;

    // Every W, I, F, R6 and P fault of the look (for the offline audit: the frame rules stop at the first). R6 here is every sample
    // an arm sinks into the gun past the game's hold; P is the same of a gun free of the hand (tossed or twirled) and the grip arm:
    // it passes through the hand or the forearm. P also: a tossed item leaves the hand upward and comes back down into it, so where
    // it leaves and where it lands the palm does not face down by more than BackDown degrees (the item would go up through the back
    // of the hand and be caught on it).
    public const float BackDown = 30f;

    void ThroughBack(List<(float Time, string Rule)> list)
    {
        var up = _rig.Up.LengthSquared() > 1e-6f ? Vector3.Normalize(_rig.Up) : Vector3.UnitY;
        for (var k = 1; k < Motion.Gun.Length; k++)
        {
            if (Motion.Gun[k].Toss <= 0f) continue;
            var (release, _) = Flight(k);
            foreach (var (time, what) in new[] { (release, "leaves"), (Motion.Gun[k].Time * Slow, "lands") })
            {
                GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out _, out _, out _);
                GripPose(HeldAt, HeldTurn, out _, out var hand);
                var down = MathF.Asin(Math.Clamp(-Vector3.Dot(Vector3.Transform(Vector3.UnitY, hand), up), -1f, 1f)) * 180f / MathF.PI;
                if (down > BackDown) list.Add((time, $"P: the tossed item {what} through the back of the hand (the palm faces {down:0} degrees down)"));
            }
        }
    }
    // J, jerking fingers (the owner's: the fingers twitch, the hand moves in hitches): a joint of either hand, as the game shows it (its
    // shape over the game's grip by the share the look takes, both as the game plays them between the finger samples), whose speed
    // changes by more than JerkSpeed degrees a second from one frame to the next, the frames ShownRate apart. A finger that moves
    // smoothly changes its speed a little each frame (closing in a fifth of a second it changes by well under that); one that jumps,
    // or stops and starts at once (held still and then moving, as a finger stopped sample by sample does), changes it all at once.
    // A twitch (turning and straight back) is two such frames. Reported once per hand and joint, with how many frames.
    public const float JerkSpeed = 150f, ShownRate = 1f / 60f;

    public void Twitches(List<(float Time, string Rule)> list)
    {
        if (_offBend.Length < 3) return;
        string[] hands = ["off", "grip"];
        for (var h = 0; h < 2; h++)
            for (var j = 0; j < 16; j++)
            {
                if (h == 0 && j == 15) continue; // (the index's lift is the grip hand's alone)
                var count = Jerks(h, j, 0f, Seconds, out var first);
                if (count > 0)
                    list.Add((first, $"J: the {hands[h]} hand's {(j < 15 ? InspectHand.Names[j / 3] : "index finger's lift")}{(j < 15 ? $" joint {j % 3}" : "")} jerks {count} times (its speed changes by more than {JerkSpeed:0} degrees a second from one frame to the next)"));
            }
    }

    // How many frames joint `j` of hand `h` jerks (Twitches) from `from` to `to` seconds into the look, and when first.
    // (From two frames before the look to two after it, where the game's fingers show alone: the hand-over counts.)
    public int Jerks(int h, int j, float from, float to, out float first)
    {
        from = MathF.Max(-2f * ShownRate, from);
        var frames = (int)((MathF.Min(to, Seconds + 2f * ShownRate) - from + 1e-4f) / ShownRate) + 1;
        var (track, times) = (new float[Math.Max(0, frames)], new float[Math.Max(0, frames)]);
        for (var f = 0; f < track.Length; f++) track[f] = Shown(h, j, times[f] = from + f * ShownRate);
        var count = Jerks(track, times, out var at);
        first = at < 0 ? float.NaN : times[at];
        return count;
    }

    // How many frames of `track` (a joint's angle in degrees frame by frame, at `times` seconds: as the look shows it, or as read
    // back in game) change its speed by more than JerkSpeed degrees a second from the frame before, and at which frame first (-1).
    public static int Jerks(ReadOnlySpan<float> track, ReadOnlySpan<float> times, out int first)
    {
        var (count, speed) = (0, float.NaN);
        first = -1;
        for (var f = 1; f < track.Length; f++)
        {
            var now = (track[f] - track[f - 1]) / MathF.Max(times[f] - times[f - 1], 1e-4f);
            if (!float.IsNaN(speed) && MathF.Abs(now - speed) > JerkSpeed && count++ == 0) first = f;
            speed = now;
        }
        return count;
    }

    // How many times the off hand's joints twitch from `from` to `to` seconds into the look: turn one way and straight back from one
    // finger sample to the next, both ways faster than TwitchSpeed degrees a second (a finger stopped sample by sample that falls open
    // and closes again as the hand sways over an edge). What a clasp is weighed by against the plain hold (Calmest, Solve); a jerk,
    // a change of speed however it goes, is J's.
    public const float TwitchSpeed = 200f;

    int Twitches(float from, float to)
    {
        var count = 0;
        for (var i = Math.Max(1, (int)(from / FingerRate)); i + 1 < _offBend.Length && i * FingerRate <= to; i++)
            for (var j = 0; j < 15; j++)
            {
                float v1 = (FingerDegrees(0, j, i) - FingerDegrees(0, j, i - 1)) / FingerRate, v2 = (FingerDegrees(0, j, i + 1) - FingerDegrees(0, j, i)) / FingerRate;
                if (v1 * v2 < 0f && MathF.Abs(v1) > TwitchSpeed && MathF.Abs(v2) > TwitchSpeed) count++;
            }
        return count;
    }

    // Joint `j` of hand `h` at `time`, in degrees, as the game shows it (FingerDegrees between the samples, as it plays them).
    float Shown(int h, int j, float time)
    {
        if (time < 0f || time > Seconds) return 0f;
        // (The shape from the clip, ClipRate apart and a straight line between; the share each frame, as InspectFingers weighs it.)
        var k = MathF.Floor(time / ClipRate);
        var s = time / ClipRate - k;
        float Shape(float at) => h == 1 && j == 15 ? Between(_indexBend, at) * 90f : ShapeOf(h == 0 ? _offBend : _gripBend, at).Joint(j / 3, j % 3) * ClipRange(j);
        var shape = Shape(k * ClipRate) * (1f - s) + Shape((k + 1) * ClipRate) * s;
        return shape * (h == 1 && j == 15 ? Between(_indexWeight, time) : Between(h == 0 ? _offWeight : _gripWeight, time));
    }

    ForgeVisual.FingerBend ShapeOf(ForgeVisual.FingerBend[] bends, float time)
    {
        Baked(bends, _offWeight, time, out var bend);
        return bend;
    }

    // S, still fingers (the owner's: the fingers move in every motion): the look's fingers as shown (FingerDegrees), of both hands,
    // no joint of which goes through more than FingersMove degrees from its least to its most over the look. The index finger's lift
    // off the trigger, which every look makes, is not counted.
    public const float FingersMove = 15f;

    public void StillFingers(List<(float Time, string Rule)> list)
    {
        var most = 0f;
        for (var h = 0; h < 2; h++)
            for (var j = 0; j < 15; j++)
            {
                var (lo, hi) = (float.MaxValue, float.MinValue);
                for (var i = 0; i < _offBend.Length; i++)
                    if (FingerDegrees(h, j, i) is var d) (lo, hi) = (MathF.Min(lo, d), MathF.Max(hi, d));
                most = MathF.Max(most, hi - lo);
            }
        if (most <= FingersMove) list.Add((0f, $"S: no finger moves in the look (the most any joint turns is {most:0} degrees, at least {FingersMove:0} wanted)"));
    }

    // Joint `j` of hand `h` (0 off, 1 grip; the grip hand's 16th is the index finger's lift) at finger sample `i`, in degrees, as the
    // look shows it: its shape over the game's grip by the share the look takes.
    float FingerDegrees(int h, int j, int i) => h == 0 ? (j < 15 ? _offBend[i].Joint(j / 3, j % 3) * ClipRange(j) * _offWeight[i] : 0f)
        : j < 15 ? _gripBend[i].Joint(j / 3, j % 3) * ClipRange(j) * _gripWeight[i] : _indexBend[i] * 90f * _indexWeight[i];

    // A joint's range in degrees (ForgeCore ClipFingers: a finger's 90, 110, 80, the thumb's 45, 60, 80).
    static float ClipRange(int joint) => joint / 3 == 0 ? (joint % 3 == 0 ? 45f : joint % 3 == 1 ? 60f : 80f) : (joint % 3 == 0 ? 90f : joint % 3 == 1 ? 110f : 80f);

    // T, a toss turns the item as its keys ask (one flip of a gun, two of a knife: the flip and spin the toss key adds to the key
    // before it): per toss, how far the item turns from where the hand starts to throw to where it has caught it (the frame to frame
    // turns summed, at 240 a second, so the way it goes counts and not where it ends), more than TossSlack off what the keys ask (the
    // wind-up and the settle in the hand add up to about 40 degrees); and every pair of neighbouring frames whose turns lie the long
    // way apart (a negative dot product: a renderer blending them would go round the long way).
    public const float TossSlack = 60f;

    public void Tosses(List<(float Time, string Rule)> list)
    {
        const float Rate = 1f / 240f;
        for (var k = 1; k < Motion.Gun.Length; k++)
        {
            if (Motion.Gun[k].Toss <= 0f) continue;
            var (release, _) = Flight(k);
            float from = release - InspectMotion.TossLead, to = Motion.Gun[k].Time * Slow + TossSink;
            var (turned, longWay, last) = (0f, 0, Quaternion.Identity);
            for (var f = 0; from + f * Rate <= to; f++)
            {
                GunPose(from + f * Rate, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out _, out var turn, out _);
                if (f > 0)
                {
                    var dot = Quaternion.Dot(last, turn);
                    if (dot < 0f) longWay++;
                    turned += 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(dot))) * 180f / MathF.PI;
                }
                last = turn;
            }
            var asked = MathF.Abs(Motion.Gun[k].Flip - Motion.Gun[k - 1].Flip) + MathF.Abs(Motion.Gun[k].Spin - Motion.Gun[k - 1].Spin);
            if (MathF.Abs(turned - asked) > TossSlack) list.Add((Motion.Gun[k].Time * Slow, $"T: the toss into {Motion.Gun[k].Time:0.00} s turns the item {turned:0} degrees ({turned / 360f:0.0} turns, its keys ask {asked / 360f:0.#})"));
            if (longWay > 0) list.Add((Motion.Gun[k].Time * Slow, $"T: the toss into {Motion.Gun[k].Time:0.00} s has {longWay} frames whose turns lie the long way from the frame before"));
        }
    }

    // K, a hand that jumps (the owner's: where a motion starts and ends and between its segments the hand twitches back and forth, and
    // the hand and its fingers snap from one place or shape to the next): frame to frame at JumpRate, from two frames before the look to
    // two after it (where the game's hold shows), either hand's target where the item camera sees it moving faster than JumpMove metres
    // a second or turning faster than JumpTurn degrees a second, or a joint of either hand's fingers as the game shows it (Shown) faster
    // than JumpFinger degrees a second. No hand of a look moves that fast on its own; a way that does not reach where the next one starts,
    // or a switch of how the hand is placed, does it in a frame. The grip hand is not counted while the gun is free of it (spun or tossed:
    // it follows the gun round on purpose). Reported once per hand and what jumps, with how many frames and the fastest.
    public const float JumpMove = 2.5f, JumpTurn = 1200f, JumpFinger = 1500f, JumpRate = 1f / 240f;

    public void Jumps(List<(float Time, string Rule)> list)
    {
        var toGun = Quaternion.Inverse(_rig.Gun);
        var (offInGun, offTurnInGun) = (Vector3.Transform(_rig.OffAt - _rig.GunAt, toGun), toGun * _rig.Off);
        var frames = (int)(Seconds / JumpRate) + 5;
        const int Joints = 31; // the off hand's 15, the grip hand's 15 and its index finger's lift
        var (count, first, most) = (new int[4 + Joints], new float[4 + Joints], new float[4 + Joints]);
        var lastJoint = new float[Joints];
        Vector3 lastOff = default, lastGrip = default;
        Quaternion lastOffTurn = default, lastGripTurn = default;
        var (lastSeen, lastFree) = (false, false);
        void Count(int what, float speed, float limit, float time)
        {
            if (speed <= limit) return;
            if (count[what]++ == 0) first[what] = time;
            most[what] = MathF.Max(most[what], speed);
        }
        static float Angle(Quaternion a, Quaternion b) => 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))))) * 180f / MathF.PI;
        for (var f = 0; f < frames; f++)
        {
            var time = (f - 2) * JumpRate;
            Vector3 off, grip;
            Quaternion offTurn, gripTurn;
            float view;
            var free = false;
            // (Before the look and after it the game's own hold.)
            if (time < 0f || time > Seconds) (off, offTurn, grip, gripTurn, view) = (_rig.OffAt, _rig.Off, _rig.GripAt, _rig.Grip, _rig.TanHalf);
            else
            {
                GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out view);
                free = Free || Twirling;
                GripPose(HeldAt, HeldTurn, out grip, out gripTurn);
                if (HandOff(time)) OffPose(time, gunAt, gun, out off, out offTurn);
                else (off, offTurn) = (gunAt + Vector3.Transform(offInGun, gun), gun * offTurnInGun);
            }
            var seen = HandSeen(off, offTurn, view);
            if (f > 0)
            {
                if (seen || lastSeen)
                {
                    Count(0, Vector3.Distance(off, lastOff) / JumpRate, JumpMove, time);
                    Count(1, Angle(offTurn, lastOffTurn) / JumpRate, JumpTurn, time);
                }
                if (!free && !lastFree)
                {
                    Count(2, Vector3.Distance(grip, lastGrip) / JumpRate, JumpMove, time);
                    Count(3, Angle(gripTurn, lastGripTurn) / JumpRate, JumpTurn, time);
                }
            }
            for (var j = 0; j < Joints; j++)
            {
                var now = j < 15 ? Shown(0, j, time) : Shown(1, j - 15, time);
                if (f > 0) Count(4 + j, MathF.Abs(now - lastJoint[j]) / JumpRate, JumpFinger, time);
                lastJoint[j] = now;
            }
            (lastOff, lastOffTurn, lastGrip, lastGripTurn, lastSeen, lastFree) = (off, offTurn, grip, gripTurn, seen, free);
        }
        string[] what = ["off hand moves", "off hand turns", "grip hand moves", "grip hand turns"];
        for (var k = 0; k < 4; k++)
            if (count[k] > 0) list.Add((first[k], $"K: the {what[k]} by a jump {count[k]} times (at most {most[k]:0.0} {(k % 2 == 0 ? "m" : "degrees")} a second, at most {(k % 2 == 0 ? JumpMove : JumpTurn):0.#} wanted)"));
        for (var j = 0; j < Joints; j++)
        {
            if (count[4 + j] == 0) continue;
            var (hand, joint) = j < 15 ? ("off", j) : ("grip", j - 15);
            var name = joint < 15 ? $"{InspectHand.Names[joint / 3]} joint {joint % 3}" : "index finger's lift";
            list.Add((first[4 + j], $"K: the {hand} hand's {name} snaps {count[4 + j]} times (at most {most[4 + j]:0} degrees a second, at most {JumpFinger:0} wanted)"));
        }
    }

    // D, the off hand drifting on the gun (the owner's: the hand under the gun is pulled back along it as the gun turns, and goes into it):
    // over a run of keys holding the gun up (under, beside or on top of it), its palm's middle in the gun's own space moves more than
    // DriftMost from where it took hold. A hand that holds a gun keeps its place on it as the gun turns.
    public const float DriftMost = 0.02f;

    public void Drifts(List<(float Time, string Rule)> list)
    {
        var hands = Motion.Hand;
        for (var a = 0; a < hands.Length; a++)
        {
            if (!Support(hands[a]) || (a > 0 && Support(hands[a - 1]))) continue;
            var last = a;
            while (last + 1 < hands.Length && Support(hands[last + 1])) last++;
            if (last == a) continue;
            var (from, worst, when) = ((Vector3?)null, 0f, 0f);
            for (var time = hands[a].Time * Slow; time <= hands[last].Time * Slow + 1e-4f; time += Step)
            {
                GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
                OffPose(time, gunAt, gun, out var off, out var offTurn);
                var palm = Vector3.Transform(off + Vector3.Transform(Palm, offTurn) - gunAt, Quaternion.Inverse(gun));
                from ??= palm;
                if (Vector3.Distance(palm, from.Value) is var d && d > worst) (worst, when) = (d, time);
            }
            if (worst > DriftMost) list.Add((when, $"D: the off hand holding the gun from {hands[a].Time:0.00} s to {hands[last].Time:0.00} s moves {worst * 100f:0.0} cm on it (at most {DriftMost * 100f:0} wanted)"));
        }
    }

    // G, a magazine held in the hand (the owner's: the magazine is not between the thumb and the fingers but outside the hand; a big one
    // lies with its length along the fingers): while the off hand holds the magazine (both keys on it, not changing its hold), the
    // magazine's middle in the hand's own frame lies on the palm's side of it, within the curled fingers' reach, and over the hand (from
    // the wrist to past the fingertips, within a hand's breadth across), and its length (its longest side, where that is HoldLong times the next) lies no nearer the
    // fingers' way than HoldAcross degrees. Each reported once, with how many samples.
    public const float HoldAcross = 45f, HoldLong = 1.3f;

    public void MagInHand(List<(float Time, string Rule)> list)
    {
        var moved = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        var (outside, along, outAt, alongAt, worstAlong) = (0, 0, 0f, 0f, 90f);
        foreach (var time in AuditTimes())
        {
            if (!HandOff(time) || ContactOf(time) is var part && (part < 0 || Motion.Parts[part].Node != InspectPart.Magazine)) continue;
            Motion.HandSpan(MotionTime(time), out var a, out var b, out _);
            if (a != b && Motion.Hand[b].Regrip) continue;
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            Moved(time, moved);
            OffPose(time, gunAt, gun, out var off, out var offTurn);
            var box = _rig.Parts![part];
            if (box.Lo == box.Hi) continue;
            MagBox(box.Lo, box.Hi, box.Down, out var mc, out var up, out var front, out var half);
            var toHand = Quaternion.Inverse(offTurn);
            Vector3 Point(Vector3 p) => Vector3.Transform(gunAt + Vector3.Transform(Vector3.Transform(p, moved[part].R) + moved[part].T, gun) - off, toHand);
            Vector3 Way(Vector3 d) => Vector3.Transform(Vector3.Transform(Vector3.Transform(d, moved[part].R), gun), toHand);
            // (The hand's frame: x from the fingers to the wrist, y out of the back of the hand, z toward the thumb.)
            var c = Point(mc) - PalmSurface;
            if (c.Y > 0f || c.Y < -HoldDepth || c.X > -Palm.X + 0.01f || c.X < Fingertip.X - Palm.X - 0.02f || MathF.Abs(c.Z) > HoldBreadth)
                if (outside++ == 0) outAt = time;
            float[] h = [half.X, half.Y, half.Z];
            var sorted = h.OrderByDescending(v => v).ToArray();
            if (sorted[0] < sorted[1] * HoldLong) continue;
            var length = Way(h[0] == sorted[0] ? Vector3.UnitX : h[1] == sorted[0] ? up : front);
            var off90 = MathF.Acos(Math.Clamp(MathF.Abs(length.X), 0f, 1f)) * 180f / MathF.PI; // from the fingers' way
            if (off90 < HoldAcross)
            {
                if (along++ == 0) alongAt = time;
                worstAlong = MathF.Min(worstAlong, off90);
            }
        }
        if (outside > 0) list.Add((outAt, $"G: the magazine lies outside the hand's hold, not between the thumb and the fingers in front of the palm ({outside} samples)"));
        if (along > 0) list.Add((alongAt, $"G: the magazine's length runs along the fingers ({worstAlong:0} degrees off them at the nearest, at least {HoldAcross:0} wanted; {along} samples)"));
    }

    // (Within a hand's breadth across, and no farther out of the palm than the curled fingers reach: InspectHand's fingers closed.)
    const float HoldBreadth = 0.06f, HoldDepth = 0.08f;

    // V, the off hand filling the view (the owner's: working a part the hand is big and close and covers the gun and the part it shows):
    // where the item camera sees the off hand while it works a part, its palm's middle or its fingertips nearer the eye than HandNear, or
    // the hand (drawn at its size, InspectMotion.ScaleAt, about its palm's middle) and forearm covering more than HandCovers of the middle
    // of the view (Cover). Each reported once with the nearest and the most.
    public const float HandNear = 0.2f, HandCovers = 0.4f;

    public void HandInView(List<(float Time, string Rule)> list)
    {
        var (near, nearAt, nearest, covers, coverAt, most) = (0, 0f, float.MaxValue, 0, 0f, 0f);
        for (var f = 0; f * Step * 2f <= Seconds; f++)
        {
            var time = f * Step * 2f;
            if (!HandOff(time) || ContactOf(time) < 0) continue;
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out var view);
            OffPose(time, gunAt, gun, out var off, out var offTurn);
            if (!HandSeen(off, offTurn, view)) continue;
            var scale = Motion.ScaleAt(MotionTime(time));
            var palm = off + Vector3.Transform(Palm, offTurn);
            Vector3 wrist = palm + (off - palm) * scale, tip = palm + Vector3.Transform(Fingertip - Palm, offTurn) * scale;
            var d = MathF.Min(palm.Length(), tip.Length());
            if (d < HandNear)
            {
                if (near++ == 0) nearAt = time;
                nearest = MathF.Min(nearest, d);
            }
            var elbow = _off.Elbow(off, OffSwivel);
            var share = Cover(view, r => HitsCapsule(r, wrist, tip, PalmRadius * scale) || HitsCapsule(r, palm, palm, 0.055f * scale) || HitsCapsule(r, elbow, wrist, ForearmRadius));
            if (share > HandCovers)
            {
                if (covers++ == 0) coverAt = time;
                most = MathF.Max(most, share);
            }
        }
        if (near > 0) list.Add((nearAt, $"V: the off hand working a part comes {nearest:0.00} m from the eye (at least {HandNear:0.00} wanted; {near} samples)"));
        if (covers > 0) list.Add((coverAt, $"V: the off hand working a part covers {most:P0} of the middle of the view (at most {HandCovers:P0} wanted; {covers} samples)"));
    }

    // The times the audit (and Snug's measures) look at: every other frame, and every hand and part key.
    IEnumerable<float> AuditTimes() => Enumerable.Range(0, (int)(Seconds / Step) + 1).Where(f => f % 2 == 0).Select(f => f * Step)
        .Concat(Motion.Hand.Select(k => k.Time * Slow)).Concat(Motion.Parts.SelectMany(p => p.Keys).Select(k => k.Time * Slow))
        .Where(t => t <= Seconds).Distinct().OrderBy(t => t);

    public List<(float Time, string Rule)> Audit()
    {
        var list = Inertia(first: false);
        ThroughBack(list);
        GunPose(0f, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var restAt, out var rest, out _);
        var still = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        Moved(0f, still);
        var gripSunk = Sunk(_rig.GripArm.Elbow(_rig.GripAt), _rig.GripAt, _rig.Grip, restAt, rest, still, float.PositiveInfinity);
        for (var k = 0; k < KeyHeldBack.Length; k++)
            if (KeyHeldBack[k] > KeyBack) list.Add((Motion.Gun[k].Time * Slow, $"W: gun key {k} asks the grip wrist {KeyHeldBack[k]:0} degrees past its limits (the gun is held back)"));
        var moved = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        var (indexAt, indexDeep) = (0f, InspectHand.Inside);
        foreach (var time in AuditTimes())

        {
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out var view);
            GripPose(HeldAt, HeldTurn, out var grip, out var gripTurn);
            if (HandSeen(grip, gripTurn, view) && WristBreaks(_arm, grip, gripTurn, HeldSwivel, "grip", _arm.Fold(_rig.GripAt, _rig.Grip)) is { } g) list.Add((time, g));
            var free = Free;
            Moved(time, moved);
            if (Sunk(GripElbow(), grip, gripTurn, gunAt, gun, moved, view) - gripSunk is var gs && gs > SinkSlack)
                list.Add((time, free ? $"P: the gun, free of the hand, passes {gs * 100f:0.0} cm into the grip hand or forearm" : $"R6: the grip arm sinks {gs * 100f:0.0} cm deeper into the gun than in the game's hold"));
            if (FingersBreak(time) is { } fingers) list.Add((time, fingers));
            if (!free && IndexDeep(time, grip, gripTurn, gunAt, gun, moved) is var deep && deep > indexDeep) (indexAt, indexDeep) = (time, deep);
            if (!HandOff(time)) continue;
            OffPose(time, gunAt, gun, out var off, out var offTurn);
            if (HandSeen(off, offTurn, view) && WristBreaks(_off, off, offTurn, OffSwivel, "off", _off.Fold(_rig.OffAt, _rig.Off)) is { } o) list.Add((time, o));
            if (HandSeen(off, offTurn, view) && OffHeldBack > OffBack) list.Add((time, OffTurnedOff()));
            if (OffDeeper(off, offTurn, gunAt, gun, moved, view) is var os && os > SinkSlack) list.Add((time, $"R6: the off arm sinks {os * 100f:0.0} cm deeper into the gun than in the game's hold"));
            if (ContactGap(time, off, offTurn, gunAt, gun) is var cg && cg > TouchGap) list.Add((time, $"R1: the off hand is {cg * 100f:0.0} cm from what it touches"));
            if (OffFingerSunk(time, off, offTurn, gunAt, gun, moved) is var sunk && sunk.Deep > InspectHand.Inside)
                list.Add((time, $"F: the off hand's {InspectHand.Names[sunk.Finger]} lies {sunk.Deep * 100f:0.0} cm inside the gun"));
        }
        Twitches(list);
        StillFingers(list);
        Tosses(list);
        Jumps(list);
        Drifts(list);
        MagInHand(list);
        HandInView(list);
        if (indexDeep > InspectHand.Inside) list.Add((indexAt, $"F: the grip hand's index finger lies {indexDeep * 100f:0.0} cm inside the gun along its side"));
        if (!float.IsNaN(IndexBend) && _indexOff > TouchGap) list.Add((IndexTime, $"F: the grip hand's index finger lies {_indexOff * 100f:0.0} cm off the side of the gun"));
        list.Sort((x, y) => x.Time.CompareTo(y.Time));
        return list;
    }

    // The wrists' angles at `time` (InspectArm.Joint), each with whether the item camera sees that wrist: the grip hand's and the off
    // hand's (none while it is on its grip).
    public ((float F, float D, float R) Joint, bool Seen) GripJoint(float time)
    {
        GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out _, out _, out var view);
        GripPose(HeldAt, HeldTurn, out var grip, out var gripTurn);
        return (_arm.Joint(grip, gripTurn, HeldSwivel), HandSeen(grip, gripTurn, view));
    }

    // Whether the item camera sees a hand at `wrist` turned `turn`: its wrist or its palm's middle.
    bool HandSeen(Vector3 wrist, Quaternion turn, float view) => Seen(wrist, view) || Seen(wrist + Vector3.Transform(Palm, turn), view);

    public ((float F, float D, float R) Joint, bool Seen)? OffJoint(float time)
    {
        if (!HandOff(time)) return null;
        GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out var view);
        OffPose(time, gunAt, gun, out var off, out var offTurn);
        return (_off.Joint(off, offTurn, OffSwivel), HandSeen(off, offTurn, view));
    }

    // Whether the way from gun key `a` to the next, `b`, holds one pose: neither is the game's, the gun is not tossed or spun between
    // them and they show it alike.
    static bool Holds(in InspectGunKey a, in InspectGunKey b) => !a.Rest && !b.Rest && b.Toss == 0f && a.Spin == b.Spin && a.Flip == b.Flip && Same(a, b);

    static bool Same(in InspectGunKey a, in InspectGunKey b) =>
        MathF.Abs(a.Turn - b.Turn) + MathF.Abs(a.Tilt - b.Tilt) + MathF.Abs(a.Roll - b.Roll) <= PoseTurn
        && MathF.Abs(Aim(a).X - Aim(b).X) <= PoseAt && MathF.Abs(Aim(a).Y - Aim(b).Y) <= PoseAt && MathF.Abs(a.Rise - b.Rise) <= PoseRise;

    // Whether the off hand is idle at `time`: not on the gun (its grip, a face, a part: both keys of its span put it there) and not
    // moving (its span goes between two places, or its IK target goes StillMove a second or more over the last 60th of a second). R7 holds the arms out of the middle of
    // the view only then: a hand idling in front of the gun hides it for nothing. The offline audit asks the same. (It poses the gun and
    // the hand at the two times: call it before posing the frame.)
    public bool Idle(float time)
    {
        Motion.HandSpan(MotionTime(time), out var a, out var b, out _);
        static bool Touches(in InspectHandKey k) => k.On is not (InspectHold.Away or InspectHold.Open);
        if (Touches(Motion.Hand[a]) && Touches(Motion.Hand[b])) return false;
        // On its way between two places (an easing starts it at no speed).
        if (a != b && Motion.Hand[a] with { Time = 0f, Cue = null, Fingers = null } != Motion.Hand[b] with { Time = 0f, Cue = null, Fingers = null }) return false;
        if (time < 1f / 60f) return true;
        Vector3 OffAt(float when)
        {
            GunPose(when, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
            OffPose(when, gunAt, gun, out var at, out _);
            return at;
        }
        return Vector3.Distance(OffAt(time), OffAt(time - 1f / 60f)) * 60f < StillMove;
    }

    // Every part's move at `time` (InspectLook.PartWorld), for the meshes it carries.
    void Moved(float time, (Quaternion R, Vector3 T)[] moved)
    {
        for (var p = 0; p < moved.Length; p++)
        {
            PartWorld(p, time, 1f, out var r, out var t);
            moved[p] = (r, t);
        }
    }

    // How far a point (view space) is from the nearest drawn mesh of the gun posed at `gunAt`, `gun` with its parts `moved` (negative:
    // inside one). `fixedOnly`: of the meshes no part of the motion moves; `part` (0 or more): of that part's meshes only.
    float Gap(Vector3 point, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved, bool fixedOnly = false, int part = -1, int except = -2)
    {
        var inGun = Vector3.Transform(point - gunAt, Quaternion.Inverse(gun));
        var best = float.MaxValue;
        if (_rig.Meshes is not { Length: > 0 } meshes) return BoxGap(inGun, _rig.Centre - _rig.Extents, _rig.Centre + _rig.Extents);
        _own ??= [.. meshes.Select(Own)];
        _round ??= [.. meshes.Select(Round)];
        // (Each box lies within the ball round its middle: a box whose ball is no nearer than the nearest box yet is no nearer either.)
        _ball ??= [.. meshes.Select((box, i) => _own[i] is { } own ? (own.Centre, own.Half.Length()) : ((box.Lo + box.Hi) / 2f, (box.Hi - box.Lo).Length() / 2f))];
        for (var i = 0; i < meshes.Length; i++)
        {
            var box = meshes[i];
            if ((fixedOnly && box.Part >= 0) || (part >= 0 && box.Part != part) || box.Part == except) continue;
            var p = box.Part >= 0 && box.Part < moved.Length ? Vector3.Transform(inGun - moved[box.Part].T, Quaternion.Inverse(moved[box.Part].R)) : inGun;
            if (Vector3.Distance(p, _ball[i].Centre) - _ball[i].Radius >= best) continue;
            var gap = _own[i] is { } own ? BoxGap(Vector3.Transform(p - own.Centre, own.Back), -own.Half, own.Half) : BoxGap(p, box.Lo, box.Hi);
            if (_round[i] is { } round && p - round.At is var d) gap = MathF.Max(gap, (d - round.Axis * Vector3.Dot(d, round.Axis)).Length() - round.Radius);
            best = MathF.Min(best, gap);
        }
        return best;
    }

    // The meshes of a part that spins and looks the same turned (a symmetry under a whole turn: a revolver's cylinder), and of a part
    // that follows it (its rounds), are round: they lie within the cylinder about the part's axis that its box turns in (its radius
    // the least way from the axis to a side of the box), so a finger stops on the cylinder's own surface all the way round it, not on
    // the corners of its box as they sweep past. Null: the mesh's box alone.
    (Vector3 At, Vector3 Axis, float Radius)?[]? _round;

    (Vector3, Vector3, float)? Round(InspectBox box)
    {
        if (box.Part < 0 || box.Part >= Motion.Parts.Length || _rig.Parts is not { } parts) return null;
        var spins = Motion.Parts[box.Part].Symmetry < 360f ? box.Part : Motion.Parts[box.Part].Follow;
        if (spins < 0 || !(Motion.Parts[spins].Symmetry < 360f) || !(Motion.Parts[spins].Axis.LengthSquared() > 1e-6f)) return null;
        var axis = Vector3.Normalize(Motion.Parts[spins].Axis);
        Vector3 lo = parts[spins].Pivot - parts[spins].Lo, hi = parts[spins].Hi - parts[spins].Pivot;
        var radius = float.MaxValue;
        if (MathF.Abs(axis.X) < 0.5f) radius = MathF.Min(radius, MathF.Min(lo.X, hi.X));
        if (MathF.Abs(axis.Y) < 0.5f) radius = MathF.Min(radius, MathF.Min(lo.Y, hi.Y));
        if (MathF.Abs(axis.Z) < 0.5f) radius = MathF.Min(radius, MathF.Min(lo.Z, hi.Z));
        return radius > 0f && radius < float.MaxValue ? (parts[spins].Pivot, axis, radius) : null;
    }

    // A magazine's meshes are met on their own boxes (MagBox), not on the boxes the gun's axes give a leaning one: a finger stops on
    // the magazine and not on the air beside it. Null: the mesh's measured box.
    (Vector3 Centre, Quaternion Back, Vector3 Half)?[]? _own;
    (Vector3 Centre, float Radius)[]? _ball;

    (Vector3, Quaternion, Vector3)? Own(InspectBox box)
    {
        if (box.Part < 0 || box.Part >= Motion.Parts.Length || Motion.Parts[box.Part].Node != InspectPart.Magazine || _rig.Parts is not { } parts) return null;
        MagBox(box.Lo, box.Hi, parts[box.Part].Down, out var centre, out var up, out var front, out var half);
        return up == Vector3.UnitY ? null : (centre, Quaternion.Inverse(Axes(Vector3.UnitX, up, front)), half);
    }

    // What the off hand touches at `time` by its keys: both keys of its span put it on one part (that part's index), on the gun (-1: its
    // meshes, any of them), or not (-2: its grip, lowered, open, or going from one thing to another).
    public int ContactOf(float time)
    {
        Motion.HandSpan(MotionTime(time), out var a, out var b, out _);
        InspectHandKey x = Motion.Hand[a], y = Motion.Hand[b];
        static bool OnGun(in InspectHandKey k) => k.On is not (InspectHold.Grip or InspectHold.Away or InspectHold.Open);
        return !OnGun(x) || !OnGun(y) ? -2 : x.Part >= 0 && x.Part == y.Part ? x.Part : x.Part < 0 && y.Part < 0 ? -1 : -2;
    }

    // R1: how far the off hand (wrist `off`, turned `offTurn`, the gun at `gunAt`, `gun`) is at `time` from what its keys say it touches:
    // the nearer of its palm and fingertips to the part's mesh box as the part is moved then, or to the gun's meshes; NaN where it
    // touches nothing (ContactOf). Absolute: the game's own hold is not taken off it, so a hand the game holds off the gun is not
    // counted as touching it. A touch is at most TouchGap. Between two `tips` keys it is what those keys say touches that is measured:
    // the pads of the index and middle fingers (Pads), not the palm.
    public float ContactGap(float time, Vector3 off, Quaternion offTurn, Vector3 gunAt, Quaternion gun)
    {
        var part = ContactOf(time);
        if (part < -1) return float.NaN;
        var moved = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        Moved(time, moved);
        Vector3 palm = off + Vector3.Transform(Palm, offTurn), tip = off + Vector3.Transform(Fingertip, offTurn);
        if (part < 0) return MathF.Max(0f, MathF.Min(Gap(palm, gunAt, gun, moved), Gap(tip, gunAt, gun, moved)));
        var box = _rig.Parts![part];
        if (box.Lo == box.Hi) return float.NaN;
        var back = Quaternion.Inverse(moved[part].R);
        float On(Vector3 p) => BoxGap(Vector3.Transform(Vector3.Transform(p - gunAt, Quaternion.Inverse(gun)) - moved[part].T, back), box.Lo, box.Hi);
        Motion.HandSpan(MotionTime(time), out var a, out var b, out _);
        if (Motion.Hand[a].Tips && Motion.Hand[b].Tips) return MathF.Max(0f, On(off + Vector3.Transform(Pads, offTurn)));
        if (Pinched(time, out var pad)) return MathF.Max(0f, On(off + Vector3.Transform(pad, offTurn)));
        return MathF.Max(0f, MathF.Min(On(palm), On(tip)));
    }

    // Where the solved look breaks its clasp (null: nowhere), at every ClaspStep of the frames where the off hand holds a magazine by
    // clasp or changes to or from it (the fingers change between their samples): a finger more than InspectHand.Inside in the gun's
    // meshes, the magazine's among them (rule F); the wrist past its limits (rule W); on the way between the holds, no part of the hand
    // within ClaspPad of the magazine, so it would hang in the air (ClaspTouch); in the hold, the thumb's pad or fewer than three
    // fingers' pads more than ClaspPad off it, or the palm more than ClaspPalm (the clasp as Clasp found it, ClaspPads).
    // (A support hold's clasp that breaks goes into `holds`, once, and the rest of the look is checked on: Solve lets it go alone.)
    string? ClaspBreaks(Dictionary<int, string> holds)
    {
        if (_held.All(h => h == null)) return null;
        var moved = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        for (var f = 0; f * ClaspStep <= Seconds; f++)
        {
            var time = f * ClaspStep;
            Motion.HandSpan(MotionTime(time), out var a, out var b, out _);
            if (!ClaspSpan(a, b)) continue;
            GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out var view);
            Moved(time, moved);
            OffPose(time, gunAt, gun, out var off, out var offTurn);
            var slot = SlotOf(Motion.Hand[_held[a] != null ? a : b]);
            if (holds.ContainsKey(slot)) continue;
            var held = Motion.Hand[a];
            var why = OffFingerSunk(time, off, offTurn, gunAt, gun, moved) is var sunk && sunk.Deep > InspectHand.Inside
                ? $"puts the off hand's {InspectHand.Names[sunk.Finger]} {sunk.Deep * 100f:0.0} cm into the gun at {time:0.000} s"
                : HandSeen(off, offTurn, view) && WristBreaks(_off, off, offTurn, OffSwivel, "off", _off.Fold(_rig.OffAt, _rig.Off)) is { } wrist
                ? $"breaks the off wrist at {time:0.000} s ({wrist})"
                : a != b && Motion.ClaspRegrip(b, _held) && ClaspTouch(time, Motion.Hand[b].Part, off, offTurn, gunAt, gun, moved).Gap is var touch && touch > ClaspPad
                ? $"changing its hold the off hand comes {touch * 100f:0.0} cm off what it holds at {time:0.000} s"
                : _held[a] != null && _held[b] != null && ClaspPads(time, held.Part, MagSide(held) || Support(held) ? 3 : ClaspOf(held)?.Hook == true ? 2 : 1, ClaspOf(held)?.Hook == true, off, offTurn, gunAt, gun, moved) is { } loose
                ? $"leaves {loose} at {time:0.000} s" : null;
            if (why == null) continue;
            if (slot < PartsLen) return why;
            holds[slot] = why;
        }
        return null;
    }

    const float ClaspStep = 1f / 240f;

    // What of the hand at `off`, `offTurn` with its fingers as baked holding magazine `part` lies off it (null: nothing): the thumb's
    // pad, or the pads of all but two or fewer of the fingers, their skin more than ClaspPad off it; the palm's skin more than ClaspPalm.
    string? ClaspPads(float time, int part, int least, bool hook, Vector3 off, Quaternion offTurn, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved)
    {
        if (least >= 3 && Gap(off + Vector3.Transform(PalmSurface, offTurn), gunAt, gun, moved, part: part) is var palm && palm > ClaspPalm)
            return $"the palm {palm * 100f:0.0} cm off it";
        OffFingers(time, out var bend);
        Span<Vector3> line = stackalloc Vector3[4];
        var on = 0;
        for (var k = 0; k < 5; k++)
        {
            InspectHand.Line(k, bend, line);
            var pad = Gap(off + Vector3.Transform((line[2] + line[3]) / 2f, offTurn), gunAt, gun, moved, part: part) - InspectHand.Radius;
            if (k == 0 && pad > ClaspPad && !hook) return $"the thumb's pad {pad * 100f:0.0} cm off it";
            if (k > 0 && pad <= ClaspPad) on++;
        }
        return on >= least ? null : $"only {on} fingers' pads on it";
    }

    // The palm's skin at its middle, in the hand's frame (its palm faces -y).
    static readonly Vector3 PalmSurface = Palm + new Vector3(0f, -PalmSkin, 0f);

    // How near the off hand at `off`, `offTurn` with its fingers as baked comes to magazine `part`: the least gap of its palm's skin
    // and of the skin along its fingers' bones to the magazine's meshes, and which (-1 the palm, else the finger).
    (float Gap, int By) ClaspTouch(float time, int part, Vector3 off, Quaternion offTurn, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved)
    {
        var (gap, by) = (Gap(off + Vector3.Transform(PalmSurface, offTurn), gunAt, gun, moved, part: part), -1);
        OffFingers(time, out var bend);
        Span<Vector3> line = stackalloc Vector3[4];
        for (var k = 0; k < 5; k++)
        {
            InspectHand.Line(k, bend, line);
            for (var j = 0; j < 3; j++)
                for (var s = 1; s <= 2; s++)
                    if (Gap(off + Vector3.Transform(Vector3.Lerp(line[j], line[j + 1], s / 2f), offTurn), gunAt, gun, moved, part: part) - InspectHand.Radius is var g && g < gap)
                        (gap, by) = (g, k);
        }
        return (gap, by);
    }

    // Whether both keys of the off hand's span at `time` hold a magazine by its side (Anchor): the hand touches it with the thumb's pad
    // and the closed fingers, so the thumb's pad (`pad`, in the hand's frame: the clasp's, else MagThumbPad) is what touches.
    bool Pinched(float time, out Vector3 pad)
    {
        Motion.HandSpan(MotionTime(time), out var a, out var b, out _);
        pad = ClaspOf(Motion.Hand[b]) is { } clasp ? clasp.Pad : MagThumbPad;
        return (MagSide(Motion.Hand[a]) || Motion.Hand[a].Clasp) && (MagSide(Motion.Hand[b]) || Motion.Hand[b].Clasp);
    }

    // The clasp a key holds its magazine by (Clasped), if it does: a key by the magazine's side, in a look that clasps (Solve).
    MagClasp? ClaspOf(in InspectHandKey k)
    {
        if (Support(k)) return SlotOf(k) is var s and >= 0 && !_dropped.Contains(s) && _rig.Meshes is { Length: > 0 } ? Clasped(s) : null;
        return _clasp && (MagSide(k) || (k.On == InspectHold.Node && k.Clasp)) ? Clasped(k.Part) : null;
    }

    // Per hand key, the clasped magazine's shape of the fingers that key takes (null: the key's own).
    ForgeVisual.FingerBend?[] _held = [];

    // Whether the off hand's span from key `a` to `b` holds a clasped magazine or changes to or from that hold.
    bool ClaspSpan(int a, int b) => a < _held.Length && b < _held.Length && (_held[a] != null || _held[b] != null);

    bool MagSide(in InspectHandKey k) => k.On == InspectHold.Mag && k.Face == InspectFace.Left && float.IsNaN(k.U) && _rig.Parts![k.Part].Hi.X - _rig.Parts[k.Part].Lo.X <= MagSpan;

    static float BoxGap(Vector3 p, Vector3 lo, Vector3 hi)
    {
        var q = Vector3.Abs(p - (lo + hi) / 2f) - (hi - lo) / 2f;
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
    }

    // How deep an arm (elbow, wrist and hand turn in view space) sinks into the gun where the item camera (tangent of half its field of
    // view `view`) sees it: its forearm and (`palm`) its palm. A point of it out of the view does not count.
    float Sunk(Vector3 elbow, Vector3 wrist, Quaternion hand, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved, float view, bool palm = true)
    {
        var p = wrist + Vector3.Transform(Palm, hand);
        var deepest = palm && Seen(p, view) ? PalmRadius - Gap(p, gunAt, gun, moved) : 0f;
        for (var k = 0; k <= 5; k++)
            if (Vector3.Lerp(elbow, wrist, k / 5f) is var q && Seen(q, view)) deepest = MathF.Max(deepest, ForearmRadius - Gap(q, gunAt, gun, moved));
        return MathF.Max(0f, deepest);
    }

    // Whether a point (view space) is in the item camera's view (an infinite `view`: anywhere, the whole arm counts).
    bool Seen(Vector3 p, float view) => float.IsPositiveInfinity(view) || p.Z > _rig.Near && MathF.Abs(p.X) <= p.Z * view * _rig.Aspect && MathF.Abs(p.Y) <= p.Z * view;

    // The share of the centre of the view (CentreBox of its half width and height) whose rays from the eye `hits` says hit something.
    float Cover(float view, Func<Vector3, bool> hits)
    {
        var hit = 0;
        for (var i = 0; i < Rays; i++)
            for (var j = 0; j < Rays; j++)
            {
                float x = CentreBox * (2f * (i + 0.5f) / Rays - 1f), y = CentreBox * (2f * (j + 0.5f) / Rays - 1f);
                if (hits(Vector3.Normalize(new Vector3(x * view * _rig.Aspect, y * view, 1f)))) hit++;
            }
        return hit / (float)(Rays * Rays);
    }

    // Whether the ray from the eye along unit `d` (from `near` on) passes within `r` of the segment a..b.
    static bool HitsCapsule(Vector3 d, Vector3 a, Vector3 b, float r)
    {
        var u = b - a;
        var uu = Vector3.Dot(u, u);
        var t = 0f;
        // The segment's point nearest the ray: alternate projections settle within a few steps for these short segments.
        for (var k = 0; k < 3; k++)
        {
            var s = MathF.Max(0.05f, Vector3.Dot(a + u * t, d));
            t = uu < 1e-10f ? 0f : Math.Clamp(Vector3.Dot(d * s - a, u) / uu, 0f, 1f);
        }
        var p = a + u * t;
        return Vector3.Distance(d * MathF.Max(0.05f, Vector3.Dot(p, d)), p) <= r;
    }

    // Whether the ray from the eye along `d` hits the box (centre c, half size e) posed at `at`, `turn`.
    static bool HitsBox(Vector3 d, Vector3 at, Quaternion turn, Vector3 c, Vector3 e, float near)
    {
        var inv = Quaternion.Inverse(turn);
        var o = Vector3.Transform(-at, inv) - c;
        var dir = Vector3.Transform(d, inv);
        float t0 = near, t1 = 100f;
        for (var i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? dir.X : i == 1 ? dir.Y : dir.Z, ei = i == 0 ? e.X : i == 1 ? e.Y : e.Z;
            if (MathF.Abs(di) < 1e-9f) { if (MathF.Abs(oi) > ei) return false; continue; }
            float a = (-ei - oi) / di, b = (ei - oi) / di;
            if (a > b) (a, b) = (b, a);
            t0 = MathF.Max(t0, a);
            t1 = MathF.Min(t1, b);
            if (t0 > t1) return false;
        }
        return true;
    }

    // The fingers, sampled every FingerRate of the look: the off hand's shape (its keys', InspectMotion.FingersAt, relaxed toward
    // InspectMotion.Relaxed as the nearer of its palm and fingertips goes from PalmSkin to TouchGap off the gun's meshes: absolute, as R1,
    // so a hand the game holds off the gun does not close on air) and how far it is taken (wholly where it touches nothing), and the grip
    // hand's (GripFingers). Each finger of the off hand then closes only as far as it comes to a mesh of the gun (FingerStop): it
    // stops on what the hand holds instead of passing into it. A look that clasps a magazine samples them ClaspFingerRate: its hand
    // opens and closes on the magazine within a tenth of a second (InspectMotion.RegripOpen), which a 30th would blur into it.
    public float FingerRate { get; private set; } = 1f / 30f;
    public const float TouchGap = 0.02f, ClaspFingerRate = 1f / 120f;

    // How fast a finger the gun held back closes again toward its shape (FingerStop, a share of its bend a second: under TwitchSpeed
    // degrees a second over a finger's 110), and how fast the look takes the off hand's fingers over from the game's grip or gives
    // them back (its weight a second).
    public const float FingerBack = 1.5f, WeightSpeed = 8f;
    // How fast a finger opens ahead of a mesh that opens it (shares of its bend a second: a finger's 110 degrees in a sixth of a second,
    // half of K's JumpFinger).
    public const float FingerAhead = 6f;

    // How deep the off hand's finger `finger` of shape `bend` lies in the gun's meshes (the hand at `off`, `offTurn`): its half
    // thickness less the least gap of the points along its three bones (negative: clear of them all).
    float FingerIn(int finger, in ForgeVisual.FingerBend bend, Vector3 off, Quaternion offTurn, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved)
    {
        Span<Vector3> line = stackalloc Vector3[4];
        InspectHand.Line(finger, bend, line);
        var least = float.MaxValue;
        for (var k = 0; k < 3; k++)
            for (var s = 1; s <= 2; s++)
                least = MathF.Min(least, Gap(off + Vector3.Transform(Vector3.Lerp(line[k], line[k + 1], s / 2f), offTurn), gunAt, gun, moved));
        return InspectHand.Radius - least;
    }

    // Each finger of shape `bend` closed only as far as it comes to a mesh (FingerStop): a whole bend up to the shape's, a shape by
    // joint up to the whole of it, its joints in its proportions; `give`: how far its skin may press into the mesh (a clasp's, ClaspSquash).
    // `backs` (when given): the share of its shape the mesh held each finger back by at the sample before (NaN: none; FingerStop), and on
    // return at this one.
    ForgeVisual.FingerBend Stopped(in ForgeVisual.FingerBend bend, Vector3 off, Quaternion offTurn, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved, float give = 0f, float[]? backs = null)
    {
        Span<float> share = stackalloc float[5];
        for (var f = 0; f < 5; f++)
        {
            share[f] = FingerStop(f, bend, off, offTurn, gunAt, gun, moved, give, backs?[f] ?? float.NaN);
            if (backs != null) backs[f] = bend.ByJoint ? 1f - share[f] : bend[f] > 1e-4f ? 1f - share[f] / bend[f] : 0f;
        }
        if (!bend.ByJoint) return new ForgeVisual.FingerBend(share[0], share[1], share[2], share[3], share[4], bend.Spread, bend.Oppose);
        var joints = new float[15];
        for (var f = 0; f < 5; f++)
            for (var k = 0; k < 3; k++) joints[f * 3 + k] = bend.Joint(f, k) * share[f];
        return ForgeVisual.FingerBend.ByJoints(joints, bend.Spread * share[0], bend.Oppose * share[0]);
    }

    // Finger `finger` of shape `bend` closed `share` of the way: its bend, for a whole bend; its joints, for a shape by joint, and the
    // thumb's root with them (an open thumb lies beside the fingers).
    static ForgeVisual.FingerBend Closed(int finger, in ForgeVisual.FingerBend bend, float share)
    {
        if (!bend.ByJoint) return new ForgeVisual.FingerBend(share, share, share, share, share, bend.Spread, bend.Oppose);
        Span<float> joints = stackalloc float[15];
        for (var k = 0; k < 3; k++) joints[finger * 3 + k] = bend.Joint(finger, k) * share;
        var root = finger == 0 ? share : 1f;
        return ForgeVisual.FingerBend.ByJoints(joints, bend.Spread * root, bend.Oppose * root);
    }

    // How far the finger closes, up to the shape's (FingerStop's share): until it comes to a mesh (a finger that lies clear of them
    // open), or, for one that lies in a mesh open (the hand laid against the gun so that only bent fingers go round it), as far as takes
    // it least deep. With a `give` (a clasp), wholly where the whole shape presses in no further than that.
    // With `back` (the share of its shape the mesh held the finger back by at the sample before), it closes back toward its shape at
    // most FingerBack a second: it opens at once where it would go into a mesh, and closes again slowly when the mesh lets it. A
    // finger that slips past an edge one sample and comes to it again the next then opens and stays open a little instead of
    // flicking shut and open (which made the fingers twitch from one sample to the next).
    float FingerStop(int finger, in ForgeVisual.FingerBend shape, Vector3 off, Quaternion offTurn, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved, float give = 0f, float back = float.NaN)
    {
        var (whole, most) = (shape, shape.ByJoint ? 1f : shape[finger]);
        float In(float bend) => FingerIn(finger, Closed(finger, whole, bend), off, offTurn, gunAt, gun, moved) - give;
        var steps = (int)MathF.Ceiling(most * InspectHand.Steps);
        if (steps <= 0) return most;
        if (!float.IsNaN(back))
        {
            float was = most * Math.Clamp(1f - back, 0f, 1f), top = MathF.Min(most, was + most * _backRate * FingerRate);
            if (In(top) <= 0f) return top;
            var (fit, bestAt) = (float.NaN, top);
            var best = In(top);
            var count = Math.Max(2, (int)MathF.Ceiling(top * InspectHand.Steps));
            for (var s = 1; s <= count; s++)
            {
                var b = MathF.Max(0f, top - top * s / count);
                var deep = In(b);
                if (deep <= 0f) { fit = b; break; }
                if (deep < best - 1e-4f || (deep < best + 1e-4f && MathF.Abs(b - was) < MathF.Abs(bestAt - was))) (bestAt, best) = (b, deep);
            }
            if (float.IsNaN(fit)) return bestAt;
            // (Between the fitting share and the one above it that does not.)
            var (ok, past) = (fit, MathF.Min(top, fit + top / count));
            for (var k = 0; k < 3; k++)
            {
                var mid = (ok + past) / 2f;
                if (In(mid) > 0f) past = mid;
                else ok = mid;
            }
            return ok;
        }
        // (A clasp's finger lies in its shape round the magazine's edge, which closing all its joints at once would meet on the way.)
        if (give > 0f && In(most) <= 0f) return most;
        if (In(0f) > 0f)
        {
            var (best, least) = (0f, In(0f));
            for (var s = 1; s <= steps; s++)
                if (MathF.Min(most, s / (float)InspectHand.Steps) is var b && In(b) is var deep && deep < least - 1e-4f) (best, least) = (b, MathF.Max(0f, deep));
            return best;
        }
        // The most it closes and stays out of the meshes, searched down from the whole shape: as the hand sways on what it holds, a finger
        // whose whole shape just meets it closes a little less instead of falling back to where its sweep first met the mesh (which
        // made the fingers twitch from one sample to the next).
        if (In(most) <= 0f) return most;
        var (lo, hi) = (0f, most);
        for (var s = steps - 1; s >= 1; s--)
        {
            var b = s / (float)InspectHand.Steps;
            if (In(b) <= 0f) { lo = b; break; }
            hi = b;
        }
        for (var k = 0; k < 3; k++)
        {
            var mid = (lo + hi) / 2f;
            if (In(mid) > 0f) hi = mid;
            else lo = mid;
        }
        return lo;
    }

    // Rule F, the off hand: the finger that lies deepest in the gun's meshes at `time` by its middle line, and how deep (metres),
    // with the fingers as baked. A hand on its grip, leaving it or going back to it has the game's own fingers, wholly or in part,
    // which the look does not know.
    (int Finger, float Deep) OffFingerSunk(float time, Vector3 off, Quaternion offTurn, Vector3 gunAt, Quaternion gun, (Quaternion R, Vector3 T)[] moved)
    {
        var (finger, deep) = (-1, 0f);
        if (Motion.FingersAt(time / Slow, out _, _held) < 1f) return (finger, deep);
        OffFingers(time, out var bend);
        for (var k = 0; k < 5; k++)
            if (FingerIn(k, bend, off, offTurn, gunAt, gun, moved) - InspectHand.Radius is var d && d > deep) (finger, deep) = (k, d);
        return (finger, deep);
    }
    ForgeVisual.FingerBend[] _offBend = [], _gripBend = [];
    float[] _offWeight = [], _gripWeight = [], _indexWeight = [], _indexBend = [];

    void BakeFingers()
    {
        var n = (int)MathF.Ceiling(Seconds / FingerRate) + 1;
        (_offBend, _gripBend, _offWeight, _gripWeight, _indexWeight, _indexBend) = (new ForgeVisual.FingerBend[n], new ForgeVisual.FingerBend[n], new float[n], new float[n], new float[n], new float[n]);
        var moved = new (Quaternion R, Vector3 T)[Motion.Parts.Length];
        GunPose(0f, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out var gunAt, out var gun, out _);
        Moved(0f, moved);
        // Each sample's hand where its fingers were stopped on the gun (for Steady), and how far their skin may press in.
        var posed = new (Vector3 Off, Quaternion OffTurn, Vector3 GunAt, Quaternion Gun, (Quaternion R, Vector3 T)[] Moved, float Give)?[n];
        var keyedBend = new ForgeVisual.FingerBend[n]; // each posed sample's shape before the meshes stopped it
        float Touch(float time, Vector3 wrist, Quaternion hand, Vector3 at, Quaternion turn) => Pinched(time, out var pad)
            ? MathF.Max(0f, Gap(wrist + Vector3.Transform(pad, hand), at, turn, moved))
            : MathF.Max(0f, MathF.Min(Gap(wrist + Vector3.Transform(Palm, hand), at, turn, moved), Gap(wrist + Vector3.Transform(Fingertip, hand), at, turn, moved)));
        var shares = new float[5];
        Array.Fill(shares, float.NaN);
        var touched = float.NaN;
        for (var i = 0; i < n; i++)
        {
            var time = MathF.Min(i * FingerRate, Seconds);
            _offWeight[i] = Motion.FingersAt(time / Slow, out var bend, _held);
            var posing = false;
            if (_offWeight[i] > 0f && HandOff(time))
            {
                GunPose(time, 1f, _rig.GunAt, _rig.Gun, _rig.TanHalf, out gunAt, out gun, out _);
                Moved(time, moved);
                OffPose(time, gunAt, gun, out var off, out var offTurn);
                // (A clasped magazine is held, and touched on the way to or from that hold, by construction: Solve holds it to that.)
                Motion.HandSpan(time / Slow, out var ha, out var hb, out _);
                var touching = ClaspSpan(ha, hb) ? 1f : 1f - Math.Clamp((Touch(time, off, offTurn, gunAt, gun) - PalmSkin) / (TouchGap - PalmSkin), 0f, 1f);
                // (It lets go at once and takes hold again at most FingerBack a second, as each finger does: FingerStop.)
                if (!_snap && !float.IsNaN(touched)) touching = MathF.Min(touching, touched + _backRate * FingerRate);
                touched = touching;
                if (Mean(bend) > Mean(InspectMotion.Relaxed)) bend = ForgeVisual.FingerBend.Lerp(InspectMotion.Relaxed, bend, touching);
                // A hand off its grip and touching nothing takes its fingers over from the game's grip at once (open on the way, not half
                // closed by the grip it left or is going back to).
                _offWeight[i] = MathF.Max(_offWeight[i], 1f - touching);
                var give = ClaspSpan(ha, hb) ? ClaspSquash : 0f;
                // (A clasp's fingers close on what it holds as Clasp placed them, at once: not held back from the sample before, but on
                // the way off it.)
                var placed = give > 0f && !(ha != hb && _held[ha] != null && _held[hb] == null);
                if (placed) Array.Fill(shares, float.NaN);
                keyedBend[i] = bend;
                bend = Stopped(bend, off, offTurn, gunAt, gun, moved, give, placed || _snap ? null : shares);
                posed[i] = (off, offTurn, gunAt, gun, ((Quaternion, Vector3)[])moved.Clone(), give);
                posing = true;
            }
            if (!posing)
            {
                touched = float.NaN;
                Array.Fill(shares, float.NaN);
            }
            // (The weight moves at most WeightSpeed a second: no snap between the game's fingers and the look's.)
            if (i > 0 && !_snap) _offWeight[i] = Math.Clamp(_offWeight[i], _offWeight[i - 1] - WeightSpeed * FingerRate, _offWeight[i - 1] + WeightSpeed * FingerRate);
            _offBend[i] = bend;
            _gripWeight[i] = GripFingers(time, out _gripBend[i], out _indexWeight[i], out _indexBend[i]);
        }
        // (And it is back to none, the game's fingers, at the look's end: it starts giving them back soon enough to, as fast as above.
        // Held back from the sample before alone, a hand that took its grip on the last key still had the look's fingers when the look
        // ended, and they snapped to the game's.)
        if (!_snap)
        {
            _offWeight[n - 1] = Motion.FingersAt(Seconds / Slow, out _, _held) is var end && !HandOff(Seconds) ? end : _offWeight[n - 1];
            for (var i = n - 2; i >= 0; i--) _offWeight[i] = MathF.Min(_offWeight[i], _offWeight[i + 1] + WeightSpeed * FingerRate);
        }
        // The fingers' flicker taken out (Steady), each finger kept as stopped where steadied it would press further into the gun.
        var steady = Steady(_offBend, FingerRate);
        Span<float> joints = stackalloc float[15];
        for (var i = 0; i < n; i++)
        {
            if (posed[i] is not { } p) { _offBend[i] = steady[i]; continue; }
            var (raw, kept) = (_offBend[i], steady[i]);
            var (spread, oppose) = (kept.Spread, kept.Oppose);
            for (var f = 0; f < 5; f++)
            {
                var deep = FingerIn(f, kept, p.Off, p.OffTurn, p.GunAt, p.Gun, p.Moved);
                var own = deep - p.Give > 1e-4f && deep > FingerIn(f, raw, p.Off, p.OffTurn, p.GunAt, p.Gun, p.Moved) + 1e-4f;
                for (var k = 0; k < 3; k++) joints[f * 3 + k] = own ? raw.Joint(f, k) : kept.Joint(f, k);
                if (own && f == 0) (spread, oppose) = (raw.Spread, raw.Oppose);
            }
            _offBend[i] = ForgeVisual.FingerBend.ByJoints(joints, spread, oppose);
        }
        // A clasp's fingers close on what it holds as placed, at once (so not held back as above); on the way to it each joint
        // opens at most FingerBack a second, sooner where a mesh opens it ahead: it never closes past the stop, and passing an edge
        // on the way does not flick it open and shut.
        var open = _backRate * FingerRate;
        // (A sample on the way from a hold without a clasp to one with it, its hand posed on the gun.)
        bool Into(int i)
        {
            if (posed[i] is null) return false;
            Motion.HandSpan(MathF.Min(i * FingerRate, Seconds) / Slow, out var a, out var b, out _);
            return a != b && _held[a] == null && _held[b] != null;
        }
        for (var i = n - 2; i >= 0 && !_snap; i--)
        {
            if (!Into(i) || !Into(i + 1)) continue;
            var (here, next) = (_offBend[i], _offBend[i + 1]);
            for (var j = 0; j < 15; j++) joints[j] = MathF.Min(here.Joint(j / 3, j % 3), next.Joint(j / 3, j % 3) + open);
            _offBend[i] = ForgeVisual.FingerBend.ByJoints(joints, MathF.Min(here.Spread, next.Spread + open), MathF.Min(here.Oppose, next.Oppose + open));
        }
        // Everywhere else a finger the gun opens (it lets go at once where it would go into a mesh, FingerStop) opens at most FingerAhead
        // a second, so sooner where a mesh opens it ahead: it never closes past where it was stopped, and does not snap open in a frame
        // (the owner's: the fingers jump from one shape to the next). Only what the mesh opens: the keys' own shapes go between keys on
        // their smooth step already, and a key that asks a snap gets it (rule J says so).
        var ahead = FingerAhead * FingerRate;
        for (var i = n - 2; i >= 0; i--)
        {
            if (posed[i] is null || posed[i + 1] is null) continue;
            var (here, next, keyedNext) = (_offBend[i], _offBend[i + 1], keyedBend[i + 1]);
            float Ahead(float h, float s, float k) => MathF.Min(h, s + ahead + MathF.Max(0f, h - k));
            for (var j = 0; j < 15; j++) joints[j] = Ahead(here.Joint(j / 3, j % 3), next.Joint(j / 3, j % 3), keyedNext.Joint(j / 3, j % 3));
            _offBend[i] = ForgeVisual.FingerBend.ByJoints(joints, Ahead(here.Spread, next.Spread, keyedNext.Spread), Ahead(here.Oppose, next.Oppose, keyedNext.Oppose));
        }
        // Last, every track of both hands made smooth (Smoothed): no finger jumps or stops and starts at once.
        var half = Math.Max(1, (int)MathF.Round(SmoothFor / FingerRate));
        // (A clasp's fingers lie on what it holds as placed. Where it holds still (both keys of the span clasp it and it does not regrip
        // between them) the placed shape stays as it is; over the rest of its spans (its regrips, the way to it) the shape is only
        // meaned, not first taken to the least, so a regrip becomes a smooth ramp and not a shape more open than the clasp; each blended
        // in within twice SmoothFor, its share moving as the means move.)
        var (clasped, still) = (new float[n], new float[n]);
        for (var i = 0; i < n; i++)
        {
            Motion.HandSpan(MathF.Min(i * FingerRate, Seconds) / Slow, out var ha, out var hb, out _);
            clasped[i] = posed[i] != null && ClaspSpan(ha, hb) ? 1f : 0f;
            still[i] = clasped[i] > 0f && _held[ha] != null && _held[hb] != null && !(ha != hb && Motion.ClaspRegrip(hb, _held)) ? 1f : 0f;
        }
        float[] Near(float[] on, int reach)
        {
            var near = new float[n];
            for (var i = 0; i < n; i++)
                for (var k = Math.Max(0, i - reach); k <= Math.Min(n - 1, i + reach) && near[i] == 0f; k++) near[i] = on[k];
            Smoothed(near, half, under: false);
            return near;
        }
        var (nearClasp, nearStill) = (Near(clasped, 2 * half), Near(still, 2 * half));
        var (placedBend, meanedBend) = (_offBend, Smoothed(_offBend, half, under: false));
        _offBend = Smoothed(_offBend, half);
        for (var i = 0; i < n; i++)
        {
            if (nearClasp[i] > 0f) _offBend[i] = ForgeVisual.FingerBend.Lerp(_offBend[i], meanedBend[i], nearClasp[i]);
            if (nearStill[i] > 0f) _offBend[i] = ForgeVisual.FingerBend.Lerp(_offBend[i], placedBend[i], nearStill[i]);
        }
        // (Where a finger made smooth lies deeper in the gun than as stopped by more than SmoothDeeper, and deeper than its hold lets it press in or than InspectHand.Inside, the
        // stopped shape comes back there, blended in as above: the smooth shape is the stopped one moved in time, and the hand has
        // moved on since.)
        var deeper = new float[n];
        for (var i = 0; i < n; i++)
        {
            if (posed[i] is not { } p) continue;
            for (var f = 0; f < 5 && deeper[i] == 0f; f++)
                if (FingerIn(f, _offBend[i], p.Off, p.OffTurn, p.GunAt, p.Gun, p.Moved) > MathF.Max(FingerIn(f, placedBend[i], p.Off, p.OffTurn, p.GunAt, p.Gun, p.Moved) + SmoothDeeper, MathF.Max(p.Give, InspectHand.Inside)))
                    deeper[i] = 1f;
        }
        var nearDeeper = Near(deeper, 2 * half);
        for (var i = 0; i < n; i++)
            if (nearDeeper[i] > 0f) _offBend[i] = ForgeVisual.FingerBend.Lerp(_offBend[i], placedBend[i], nearDeeper[i]);
        _gripBend = Smoothed(_gripBend, half);
        Smoothed(_indexBend, half, under: true);
        Smoothed(_offWeight, half, under: false);
        Smoothed(_gripWeight, half, under: false);
        Smoothed(_indexWeight, half, under: false);
    }

    // The fingers made smooth (the owner's: they twitched, the hand moved in hitches), every joint of both hands, the thumb's spread and
    // opposition, the index finger's lift and each hand's share over the game's grip: a shape track first takes the least of its
    // samples within twice SmoothFor either side, then the mean within SmoothFor either side, twice. The least keeps every finger
    // where it was stopped or more open (the means of the least within that reach are never more closed than the sample itself: no
    // finger closes into the gun), and two means make a jump a ramp whose speed itself rises and falls smoothly. A share (how far
    // the look takes the fingers over the game's) has no stop to keep: only the two means. Shared by every hand the look shapes.
    public const float SmoothFor = 0.05f, SmoothDeeper = 0.001f;

    static ForgeVisual.FingerBend[] Smoothed(ForgeVisual.FingerBend[] bends, int half, bool under = true)
    {
        var n = bends.Length;
        var tracks = new float[17][];
        for (var j = 0; j < 17; j++)
        {
            tracks[j] = new float[n];
            for (var i = 0; i < n; i++) tracks[j][i] = j < 15 ? bends[i].Joint(j / 3, j % 3) : j == 15 ? bends[i].Spread : bends[i].Oppose;
            Smoothed(tracks[j], half, under);
        }
        var smooth = new ForgeVisual.FingerBend[n];
        Span<float> joints = stackalloc float[15];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < 15; j++) joints[j] = tracks[j][i];
            smooth[i] = ForgeVisual.FingerBend.ByJoints(joints, tracks[15][i], tracks[16][i]);
        }
        return smooth;
    }

    // One track made smooth in place (Smoothed); `under`: no sample comes out above what it was.
    static void Smoothed(float[] track, int half, bool under)
    {
        var n = track.Length;
        if (n < 2) return;
        var work = new float[n];
        void Pass(float[] from, float[] into, int reach, bool least)
        {
            for (var i = 0; i < n; i++)
            {
                var (sum, low) = (0f, float.MaxValue);
                for (var k = i - reach; k <= i + reach; k++)
                {
                    var v = from[Math.Clamp(k, 0, n - 1)];
                    (sum, low) = (sum + v, MathF.Min(low, v));
                }
                into[i] = least ? low : sum / (2 * reach + 1);
            }
        }
        if (under) { Pass(track, work, 2 * half, true); Array.Copy(work, track, n); }
        Pass(track, work, half, false);
        Pass(work, track, half, false);
    }

    // Each joint of a run of finger shapes (and the thumb's spread and opposition) the median of its samples within SteadyFor either side:
    // a finger stopped on the gun sample by sample flickers where the hand sways over an edge, which a median takes out while it keeps
    // every change that goes one way (a hand opening or closing) exactly. Shared by every hand the look shapes.
    public const float SteadyFor = 0.05f;

    static ForgeVisual.FingerBend[] Steady(ForgeVisual.FingerBend[] bends, float rate)
    {
        var half = Math.Max(1, (int)MathF.Round(SteadyFor / rate));
        var n = bends.Length;
        if (n < 3) return bends;
        var tracks = new float[17][];
        for (var j = 0; j < 17; j++)
        {
            tracks[j] = new float[n];
            for (var i = 0; i < n; i++) tracks[j][i] = j < 15 ? bends[i].Joint(j / 3, j % 3) : j == 15 ? bends[i].Spread : bends[i].Oppose;
        }
        var window = new float[2 * half + 1];
        var steady = new ForgeVisual.FingerBend[n];
        Span<float> joints = stackalloc float[15];
        var at = new float[17];
        for (var i = 0; i < n; i++)
        {
            int from = Math.Max(0, i - half), to = Math.Min(n - 1, i + half), count = to - from + 1;
            for (var j = 0; j < 17; j++)
            {
                Array.Copy(tracks[j], from, window, 0, count);
                Array.Sort(window, 0, count);
                at[j] = (count & 1) == 1 ? window[count / 2] : (window[count / 2 - 1] + window[count / 2]) / 2f;
            }
            for (var j = 0; j < 15; j++) joints[j] = Math.Clamp(at[j], 0f, 1f);
            steady[i] = ForgeVisual.FingerBend.ByJoints(joints, Math.Clamp(at[15], 0f, 1f), Math.Clamp(at[16], 0f, 1f));
        }
        return steady;
    }

    static float Mean(in ForgeVisual.FingerBend b) => (b.Thumb + b.Index + b.Middle + b.Ring + b.Pinky) / 5f;

    // The off hand's and the grip hand's finger shapes at `time` as baked, and how far each is taken over the game's grip.
    public float OffFingers(float time, out ForgeVisual.FingerBend bend) => Baked(_offBend, _offWeight, time, out bend);

    public float GripFingersBaked(float time, out ForgeVisual.FingerBend bend) => Baked(_gripBend, _gripWeight, time, out bend);

    // How far the grip hand's index finger is taken over the game's grip at `time`, and its bend there (InspectHand: below 0 its
    // knuckle bent back, which GripFingersBaked's shape, ForgeCore's, cannot say: InspectHand.Lifted gives those joints' turns).
    public float IndexFinger(float time, out float bend)
    {
        bend = Between(_indexBend, time);
        return Between(_indexWeight, time);
    }

    // Between the finger samples on a monotone cubic (Steffen): its speed goes on smoothly through each sample, where a straight line
    // from sample to sample turns at once (at a 30th of a second, a hitch the eye sees), and it never goes past either sample it lies
    // between, so a finger stopped on the gun at both is stopped between them too.
    float Baked(ForgeVisual.FingerBend[] bends, float[] weights, float time, out ForgeVisual.FingerBend bend)
    {
        Span<float> joints = stackalloc float[15];
        var at = Math.Clamp(time / FingerRate, 0f, bends.Length - 1.001f);
        var i = (int)at;
        var s = at - i;
        int a = Math.Max(i - 1, 0), c = i + 1, d = Math.Min(i + 2, bends.Length - 1);
        for (var j = 0; j < 15; j++) joints[j] = Steffen(bends[a].Joint(j / 3, j % 3), bends[i].Joint(j / 3, j % 3), bends[c].Joint(j / 3, j % 3), bends[d].Joint(j / 3, j % 3), s);
        bend = ForgeVisual.FingerBend.ByJoints(joints, Steffen(bends[a].Spread, bends[i].Spread, bends[c].Spread, bends[d].Spread, s),
            Steffen(bends[a].Oppose, bends[i].Oppose, bends[c].Oppose, bends[d].Oppose, s));
        return Between(weights, time);
    }

    float Between(float[] track, float time)
    {
        var at = Math.Clamp(time / FingerRate, 0f, track.Length - 1.001f);
        var i = (int)at;
        return Steffen(track[Math.Max(i - 1, 0)], track[i], track[i + 1], track[Math.Min(i + 2, track.Length - 1)], at - i);
    }

    // The monotone cubic through y1 (s = 0) and y2 (s = 1), y0 and y3 the samples either side, all one step apart.
    static float Steffen(float y0, float y1, float y2, float y3, float s)
    {
        static float Slope(float before, float after) =>
            (MathF.Sign(before) + MathF.Sign(after)) * MathF.Min(MathF.Min(MathF.Abs(before), MathF.Abs(after)), 0.25f * MathF.Abs(before + after));
        float s0 = y1 - y0, s1 = y2 - y1, s2 = y3 - y2, m1 = Slope(s0, s1), m2 = Slope(s1, s2);
        float s2t = s * s, s3t = s2t * s;
        return (2f * s3t - 3f * s2t + 1f) * y1 + (s3t - 2f * s2t + s) * m1 + (-2f * s3t + 3f * s2t) * y2 + (s3t - s2t) * m2;
    }

    // The rate the game plays the fingers at (InspectFingers' clips, sampled from the cubic above): the game draws a straight line
    // between two of them, short enough here that its turns are no hitch (Jerks counts them as the game shows them).
    public const float ClipRate = 1f / 120f;

    // Whether any frame of the look shapes the off hand's or the grip hand's fingers.
    public bool ShapesOff => _offWeight.Any(w => w > 0f);
    public bool ShapesGrip => _gripWeight.Any(w => w > 0f) || _indexWeight.Any(w => w > 0f);

    // How far the grip hand may move from where the game holds the gun, in metres; in a motion that tosses it, TossMove: the item is
    // thrown and caught where it is held, with the hand's own wind-up and sink, not carried out to arm's length first.
    const float Move = 0.3f, TossMove = 0.12f;

    // Where the grip hand stays: in the lower right of the view (at least HandRight of the half width right of the middle and at most
    // HandHigh of the half height above it, in the game's view, or as far as the game's own hold is), and at most Deeper metres farther
    // or Nearer metres nearer than the game holds it: a little up and forward to bring more of the gun in, never the middle of the view.
    const float HandRight = 0.2f, HandHigh = 0f, Deeper = 0.18f, Nearer = 0.08f;

    // Whether a grip hand at `hand` keeps its place, with `slack` (metres and shares) of give.
    static bool HandKept(in InspectRig rig, Vector3 hand, float slack)
    {
        float near = rig.Near, tan = rig.TanHalf, aspect = rig.Aspect;
        Vector2 Screen(Vector3 p) => new(p.X / (MathF.Max(p.Z, near) * tan * aspect), p.Y / (MathF.Max(p.Z, near) * tan));
        Vector2 at = Screen(hand), game = Screen(rig.GripAt);
        return at.X >= MathF.Min(HandRight, game.X) - slack && at.Y <= MathF.Max(HandHigh, game.Y) + slack
            && hand.Z <= rig.GripAt.Z + Deeper + slack && hand.Z >= rig.GripAt.Z - Nearer - slack;
    }

    // The item as the game holds it, turned with the grip hand toward where the hand's palm (the right hand's +Y) faces up, the
    // shortest way and at most PalmMost degrees (what a forearm turns from a hold without the wrist at its end: a fist held with its
    // back up comes round to a palm that faces sideways and a little up, as a gun's grip hand has it).
    const float PalmMost = 75f;

    static Quaternion PalmUp(in InspectRig rig)
    {
        var up = rig.Up.LengthSquared() > 1e-6f ? Vector3.Normalize(rig.Up) : Vector3.UnitY;
        var palm = Vector3.Transform(Vector3.UnitY, rig.Grip);
        var axis = Vector3.Cross(palm, up);
        var turn = axis.LengthSquared() > 1e-8f ? Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Min(PalmMost * MathF.PI / 180f, MathF.Acos(Math.Clamp(Vector3.Dot(palm, up), -1f, 1f))))
            : Vector3.Dot(palm, up) > 0f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Transform(Vector3.UnitX, rig.Grip), MathF.PI);
        return Quaternion.Normalize(turn * rig.Gun);
    }

    // One gun key: the place of the grip hand and the swivel of its elbow that cost least (Cost), the turn kept within the wrist's limits
    // there, how many degrees that held the gun back from the key's turn (`back`) and the view its frames need (Frames).
    static void Solve(in InspectGunKey key, bool fromHold, bool turning, in InspectRig rig, Vector3 gripAt, Quaternion grip, List<Vector3> touches, float move, out Vector3 at, out Quaternion turn, out float view, out float swivel, out float back)
    {
        const float Degrees = MathF.PI / 180f;
        // From the gun held level and pointing ahead (its own axes on the view's; the game's ready pose), not from the game's pose this
        // frame, which may be lowered or swaying; or, for a motion that says so (InspectMotion.FromHold), from the hold measured as the
        // look started.
        turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, key.Turn * Degrees) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -key.Tilt * Degrees)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -key.Roll * Degrees); // turning +x toward +y takes the top left
        if (fromHold) turn = Quaternion.Normalize(turn * (key.Palm ? PalmUp(rig) : rig.Gun));
        var want = turn;
        var hold = turn * grip;
        var offset = Vector3.Transform(gripAt, turn);
        var arm = rig.GripArm;
        var best = float.MaxValue;
        var hand = rig.GripAt;
        swivel = 0f;
        // Where the hand holds the gun and how far the elbow swings out: within Move of where the game holds it, in front of the eye, in
        // the arm's reach and the swivel's range; a coarse sweep, then a fine one round the best.
        for (var pass = 0; pass < 2; pass++)
        {
            float step = pass == 0 ? 0.05f : 0.0125f, span = pass == 0 ? MathF.Min(Move, MathF.Ceiling(move / 0.05f) * 0.05f) : 0.05f;
            float turnStep = pass == 0 ? 10f : 2.5f, from = pass == 0 ? -InspectArm.SwivelIn : MathF.Max(-InspectArm.SwivelIn, swivel - 5f);
            float to = pass == 0 ? InspectArm.SwivelOut : MathF.Min(InspectArm.SwivelOut, swivel + 5f);
            var around = pass == 0 ? rig.GripAt : hand;
            var n = (int)MathF.Round(2f * span / step) + 1; // places per axis
            var m = (int)MathF.Round((to - from) / turnStep) + 1; // swivels
            for (var i = 0; i < n * n * n * m; i++)
            {
                var c = i / m;
                var p = around + new Vector3(c % n, c / n % n, c / (n * n)) * step - new Vector3(span);
                if (Vector3.Distance(p, arm.Shoulder) > arm.Reach || Vector3.Distance(p, rig.GripAt) > move || !HandKept(rig, p, 0f)) continue;
                var s = from + i % m * turnStep;
                var cost = Cost(key, rig, arm, p, s, p - offset, want, hold, gripAt, touches);
                // The frame rules (InspectShot) weigh above everything else; measured only for a place that could still win.
                // The hand is placed for the gun as held: a rise lifts it from there (weighing the lifted frame here would put the hand lower by as much).
                if (cost < best) cost += 10f * Frames(key with { Rise = 0f }, turning && key.Rise == 0f, rig, want, p - offset, gripAt, grip, Margin, out _);
                if (cost < best) (best, hand, swivel) = (cost, p, s);
            }
        }
        var kept = arm.Comfort(hand, hold, swivel);
        back = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(kept, hold)))) * 180f / MathF.PI;
        turn = Quaternion.Normalize(kept * Quaternion.Inverse(grip));
        at = hand - Vector3.Transform(gripAt, turn);
        Frames(key, turning, rig, turn, at, gripAt, grip, 0f, out view);
    }

    // How far the frames a key shows fall short of the frame rules (0: they keep them) and the item camera view they need: the gun held
    // at `at`, `turn`, lifted by the key's rise, and when it is `turning` (spun or flipped about its bounds centre) at every 30 degrees of it.
    static float Frames(in InspectGunKey key, bool turning, in InspectRig rig, Quaternion turn, Vector3 at, Vector3 gripAt, Quaternion grip, float margin, out float view)
    {
        at.Y += key.Rise;
        // A twirl (no rise) turns about the trigger guard, a spin with a rise about the bounds centre (GunPose).
        var about = turning && key.Rise == 0f ? Trigger(rig, gripAt, grip) : rig.Centre;
        var pivot = at + Vector3.Transform(about, turn);
        var frames = turning ? 24 : 1;
        Span<Quaternion> turns = stackalloc Quaternion[frames];
        for (var k = 0; k < frames; k++)
            turns[k] = Quaternion.Normalize(turn * Quaternion.CreateFromAxisAngle(k < 12 ? Vector3.UnitX : Vector3.UnitZ, k % 12 * MathF.PI / 6f));
        view = 0f;
        foreach (var q in turns) view = MathF.Max(view, ViewFor(key, rig, q, pivot - Vector3.Transform(about, q), gripAt));
        var shortBy = 0f;
        if (about == rig.Centre)
        {
            foreach (var q in turns) shortBy = MathF.Max(shortBy, Short(Shot(rig, pivot - Vector3.Transform(about, q), q, view, gripAt), margin));
            return shortBy;
        }
        // A twirl is watched from as near as the gun is held (InspectShot.Breaks): the view and the frame rules are the held pose's, and
        // of its turns only that none brings the gun too near the eye.
        view = ViewFor(key, rig, turn, at, gripAt);
        shortBy = Short(Shot(rig, at, turn, view, gripAt), margin);
        for (var k = 0; k < 12; k++)
            shortBy = MathF.Max(shortBy, MathF.Max(0f, InspectShot.MinDepth * (1f + margin) - Shot(rig, pivot - Vector3.Transform(about, turns[k]), turns[k], view, gripAt).Depth));
        return shortBy;
    }

    // The item camera's view for a pose: what its shown part needs, never narrower than the game's, at most WideDegrees.
    static float ViewFor(in InspectGunKey key, in InspectRig rig, Quaternion turn, Vector3 at, Vector3 gripAt) =>
        MathF.Min(MathF.Max(rig.TanHalf, Needed(key, rig, turn, at, gripAt)), MathF.Tan(WideDegrees * MathF.PI / 360f));

    // How far a frame falls short of the frame rules, by `margin` (a share of each bound) stricter, summed. A key is solved a margin
    // inside the rules so the frames between keys keep them.
    const float Margin = 0.1f;

    static float Short(in InspectShot shot, float margin) =>
        MathF.Max(0f, InspectShot.MinInside * (1f + margin) - shot.Inside) + MathF.Max(0f, shot.Cover - InspectShot.MaxCover * (1f - margin))
        + MathF.Max(0f, InspectShot.MinDepth * (1f + margin) - shot.Depth)
        + MathF.Min(10f, MathF.Max(0f, shot.Centre - 1f)) + MathF.Min(10f, MathF.Max(0f, shot.Shown - 1f));
    // How a place of the grip hand and a swivel of its elbow serve a key, lower better: the view the shown part of the gun needs (tangent
    // of half of it), three times as much for every bit of that past the game's own view (widening is the last resort), how far the hand
    // is from where the game holds it (an arm reaching across the view looks wrong), a little for every degree the elbow swings from rest,
    // the centre's distance from the key's point of the view, every metre a place the off hand touches at this key is out of its arm's
    // reach (OutOfReach), and far more for every degree the wrist would have to bend past its limits to hold the turn there.
    static float Cost(in InspectGunKey key, in InspectRig rig, in InspectArm arm, Vector3 hand, float swivel, Vector3 at, Quaternion want, Quaternion hold, Vector3 gripAt, List<Vector3> touches)
    {
        var bend = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(Quaternion.Dot(arm.Comfort(hand, hold, swivel), hold)))) * 180f / MathF.PI;
        if (bend > 2f) return 100f + bend;
        var centre = at + Vector3.Transform(rig.Centre, want);
        if (centre.Z <= rig.Near) return 99f;
        var off = new Vector2(centre.X / (centre.Z * rig.TanHalf * rig.Aspect), centre.Y / (centre.Z * rig.TanHalf)) - Aim(key);
        var need = Needed(key, rig, want, at, gripAt);
        var cost = need + 3f * MathF.Max(0f, need - rig.TanHalf) + 1.5f * Vector3.Distance(hand, rig.GripAt) + 0.002f * MathF.Abs(swivel) + (float.IsNaN(key.At.X) ? AtFree : AtHeld) * off.Length();
        // (Past the arm by as much as the game holds the hand at rest counts as in reach.)
        var reach = MathF.Max(rig.OffArm.Reach, Vector3.Distance(rig.OffAt, rig.OffArm.Shoulder));
        foreach (var t in touches) cost += OutOfReach * MathF.Max(0f, Vector3.Distance(at + Vector3.Transform(t, want), rig.OffArm.Shoulder) - reach);
        return cost;
    }
    // What a metre of an off hand's place out of its arm's reach weighs: ten times the grip hand's distance from its hold, so the gun is
    // brought to where the off hand keeps its place on it as the gun turns (the owner's: the hand under the gun stays where it holds
    // it); the place is never slid along the gun, so a hand the gun is not brought near enough to falls short rather than moving on it.
    const float OutOfReach = 40f;
    // How much a unit of the centre's distance from the key's point of the view weighs: a key that writes `at` holds the gun there (within
    // about 0.1 of the half view, giving way to the frame rules and the hand's place), one that does not leans a little toward the middle.
    const float AtHeld = 1f, AtFree = 0.05f;

    // The point of the view a key asks for its centre: its `at`, else the middle.
    internal static Vector2 Aim(in InspectGunKey key) => float.IsNaN(key.At.X) ? Vector2.Zero : key.At;

    // A corner of the part of the gun's bounds that is shown: ahead of and above the grip hand (receiver to muzzle); the stock behind the
    // hand and the grip and magazine below it may leave the view.
    static Vector3 Corner(in InspectRig rig, int i, Vector3 gripAt)
    {
        Vector3 lo = rig.Centre - rig.Extents, hi = rig.Centre + rig.Extents;
        float back = Math.Clamp(gripAt.Z, lo.Z, hi.Z), low = Math.Clamp(gripAt.Y, lo.Y, hi.Y);
        return new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? low : hi.Y, (i & 4) == 0 ? back : hi.Z);
    }

    // The tangent of half the vertical view that shows every corner of the shown part inside the key's fill of the view.
    static float Needed(in InspectGunKey key, in InspectRig rig, Quaternion turn, Vector3 at, Vector3 gripAt)
    {
        var need = 0f;
        for (var i = 0; i < 8; i++)
        {
            var p = at + Vector3.Transform(Corner(rig, i, gripAt), turn);
            if (p.Z < rig.Near + 0.02f) return 50f;
            need = MathF.Max(need, MathF.Max(MathF.Abs(p.X) / rig.Aspect, MathF.Abs(p.Y)) / (p.Z * key.Fill));
        }
        return need;
    }
}
