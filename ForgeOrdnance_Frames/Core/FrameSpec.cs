using System;
using System.Globalization;
using System.Text.Json;
using ForgeCore;

namespace ForgeOrdnanceFrames;

/// <summary>The suit's gun: a game gear block (<see cref="Gear"/>, an offline gear persistentID of a special-slot weapon) lent
/// into the pilot's special (secondary) slot while the suit is worn. Its hits deal the gear's own damage times
/// <see cref="DamageMultiplier"/>. <see cref="Magazine"/> rounds are loaded (at most the gear's own clip) and <see cref="Reserve"/>
/// rounds follow; the suit keeps what is left between pilots and the game's ammunition never refills it. The suit's handling steadies
/// every gun the pilot holds while piloting (the suit gun and the pilot's own guns alike): recoil (view kick up and sideways, crosshair
/// bloom, weapon kick) is <see cref="RecoilMultiplier"/> of the gun's own and the reload takes <see cref="ReloadMultiplier"/> of its
/// own time (<see cref="GunHandling"/>).</summary>
public sealed record WeaponSpec(uint Gear, float DamageMultiplier, int Magazine, int Reserve, float RecoilMultiplier, float ReloadMultiplier);

/// <summary>How the worn suit moves, through the game's own locomotion. A suit is an enhancement: every multiplier is at least 1.
/// <see cref="Walk"/> scales every speed the game asks for (it responds as fast as the player on foot: no added inertia), the
/// game's sprint is a further <see cref="Sprint"/> times faster for at most <see cref="SprintSeconds"/> before its heat must cool
/// (<see cref="SprintRecovery"/> seconds from full), and the game's jump reaches <see cref="Jump"/> times its height.</summary>
public sealed record MovementSpec(float Walk, float Sprint, float Jump, float SprintSeconds, float SprintRecovery);

/// <summary>One moving part of the suit's opening: the bone <see cref="Node"/>, a rigid piece of armour. How it moves is the model's
/// own "open" and "close" clips (every part's rigid path, posed by the owner in the cut file and baked by
/// <c>tools/build_frame.py</c>); the runtime plays them. <see cref="Seam"/> marks a part beside the back's middle seam: steam
/// vents from the seam when the first of them unlatches (<see cref="PlateTimeline"/>).</summary>
public sealed record OpeningPart(string Node, bool Seam);
/// <summary>How the suit opens to be boarded and left: the <see cref="Parts"/> its "open" clip moves (the sequence timing is
/// <see cref="FrameSequence"/>; the pilot's own motion is <see cref="PilotMotion"/>).</summary>
public sealed record OpeningSpec(OpeningPart[] Parts);

/// <summary>The game's first-person body bones a suit part can be worn on (Unity's humanoid bone names).</summary>
public enum FrameBodyBone
{
    Hips, Spine, Chest, UpperChest, Neck, Head,
    LeftShoulder, LeftUpperArm, LeftLowerArm, LeftHand, RightShoulder, RightUpperArm, RightLowerArm, RightHand,
    LeftUpperLeg, LeftLowerLeg, LeftFoot, LeftToes, RightUpperLeg, RightLowerLeg, RightFoot, RightToes,
}

/// <summary>One part of the pilot's first-person shell: the suit model's bone <see cref="Bone"/> (with the mesh it skins) is worn
/// on the game's first-person body bone <see cref="Body"/> and moves only with the game's own animation of that bone.</summary>
public sealed record ShellBone(string Bone, FrameBodyBone Body);

/// <summary>The suit's lamp: the pilot's own flashlight (its key, its sync), reshaped while worn: range, cone angle and intensity
/// times the multipliers, tinted <see cref="R"/>/<see cref="G"/>/<see cref="B"/>. The shell's light strips (and its lamps) glow in
/// <see cref="GlowR"/>/<see cref="GlowG"/>/<see cref="GlowB"/> while all is well; <see cref="FrameGlow"/> gives the other states.</summary>
public sealed record LightSpec(float RangeMultiplier, float AngleMultiplier, float IntensityMultiplier, float R, float G, float B,
    float GlowR, float GlowG, float GlowB);

/// <summary>Assault special: a short sprint that breaks weak doors in the way and shoves enemies aside.</summary>
public sealed record BoostSpec(float Seconds, float SpeedMultiplier, float Cooldown, bool BreachDoors, float Shove);

/// <summary>Firepower special: plant the suit; while planted it cannot move, its own gun hits <see cref="DamageMultiplier"/> times
/// harder and fires <see cref="FireRateMultiplier"/> times as fast (that gun's own shot timer), any gun the pilot holds reloads in
/// <see cref="ReloadMultiplier"/> of its time and kicks <see cref="RecoilMultiplier"/> as much again (on top of the model's
/// <see cref="WeaponSpec.ReloadMultiplier"/> and <see cref="WeaponSpec.RecoilMultiplier"/>), and its armour stops a further <see cref="DamageReduction"/> of every hit. A
/// suit with a <see cref="ShieldSpec.WhileSieged"/> barrier raises it only while planted.</summary>
public sealed record SiegeSpec(float DeploySeconds, float DamageMultiplier, float DamageReduction, float FireRateMultiplier, float ReloadMultiplier, float RecoilMultiplier);

/// <summary>Tank special: an energy barrier around the suit (a dome of <see cref="Radius"/> metres that moves with it). Friendly
/// bullets and players pass; enemy attacks and enemies do not. Enemy attacks wear down <see cref="Capacity"/>; emptied it
/// breaks, and after <see cref="RegenDelay"/> seconds without a hit it refills at <see cref="RegenRate"/> per second, standing
/// again once it holds <see cref="RaiseFraction"/> of its capacity. With <see cref="WhileSieged"/> (a suit with a siege mode) the barrier
/// stands only while the suit is planted; it keeps its charge and refills either way.</summary>
public sealed record ShieldSpec(float Capacity, float RegenDelay, float RegenRate, float Radius, float RaiseFraction, bool WhileSieged);

/// <summary>Scout special: tags every enemy around the suit the way the bio tracker does, every <see cref="Interval"/> seconds.</summary>
public sealed record ScanSpec(float Radius, float Interval);

/// <summary>Firepower extra: a salvo of <see cref="Salvo"/> shoulder rockets at the aimed point (the rocket key), each a game
/// explosion of <see cref="Damage"/> within <see cref="Radius"/> metres, scattered within <see cref="Spread"/> metres of the
/// point, then <see cref="Cooldown"/> seconds before the next salvo. Rockets fly at <see cref="Speed"/> m/s up to
/// <see cref="Range"/> metres, out of the tubes of the suit's <see cref="Launcher"/>.</summary>
public sealed record RocketsSpec(int Salvo, float Damage, float Radius, float Spread, float Cooldown, float Range, float Speed, RocketLauncherSpec Launcher);

/// <summary>The rockets' look: one launcher, the prefab <see cref="Pod"/> (<c>Pack:item</c>; its tubes are its children
/// <c>muzzle_1</c>..<c>muzzle_N</c>, each at a tube's mouth facing +Z), and the rocket prefab <see cref="Round"/> (nose +Z): one
/// sits in every loaded tube and one flies each launch (<see cref="FrameRocketRack"/>). The launcher rides the model's bone
/// <see cref="Bone"/> (a plate that lifts when the suit opens carries it along), placed at (<see cref="X"/>, <see cref="Y"/>,
/// <see cref="Z"/>) metres in the suit's own space at its rest (bind) pose (x right, y up, z forward, from its feet) and turned by
/// <see cref="Pitch"/>, <see cref="Yaw"/> and <see cref="Roll"/> degrees; never scaled or mirrored.</summary>
public sealed record RocketLauncherSpec(string Pod, string Round, string Bone, float X, float Y, float Z, float Pitch, float Yaw, float Roll);

/// <summary>One exosuit model, read from a definition's <c>Payload</c>. <see cref="Armor"/> is the suit's only health: every hit
/// on the pilot lands on it, it never regenerates and no healing item restores it; at zero the suit is wrecked. Specials are
/// present or null; <see cref="CarryHeavy"/> lets the suit carry heavy objects without slowing down. The armour takes only
/// 1 - <see cref="DamageReduction"/> of each hit. <see cref="Opening"/> lists the parts that open for boarding (none: the suit
/// does not visibly open); <see cref="Shell"/> the suit bones worn on the pilot's own first-person body (none: nothing is worn). The
/// pilot's arms and hands are never replaced: the game's own arms stay (tinted to the suit's look), so the two upper arms in the list only
/// line the suit's skeleton up with the game's, and the other listed bones (hips, legs) are the parts the pilot sees looking down.</summary>
public sealed record FrameSpec(string Model, float Armor, float DamageReduction, float EyeHeight,
    MovementSpec Movement, WeaponSpec Weapon, LightSpec Light, BoostSpec? Boost, SiegeSpec? Siege,
    ScanSpec? Scan, ShieldSpec? Shield, bool CarryHeavy, RocketsSpec? Rockets, OpeningSpec? Opening, ShellBone[] Shell);

/// <summary>Strict reader for the <c>Exosuit</c> payload. Unknown fields, wrong types and values out of range are errors that
/// name the field; nothing is silently clamped.</summary>
public static class FrameSpecParser
{
    public const string TypeName = "frames.Exosuit";

    private static readonly string[] TypeField = { "Type" };
    private static readonly string[] Fields = { "Model", "Armor", "DamageReduction", "EyeHeight",
        "Movement", "Weapon", "Light", "Boost", "Siege", "Scan", "Shield", "CarryHeavy", "Rockets", "Opening", "Shell" };
    private static readonly string[] WeaponFields = { "Gear", "DamageMultiplier", "Magazine", "Reserve", "RecoilMultiplier", "ReloadMultiplier" };
    private static readonly string[] MovementFields = { "Walk", "Sprint", "Jump", "SprintSeconds", "SprintRecovery" };
    private static readonly string[] OpeningFields = { "Parts" };
    private static readonly string[] PartFields = { "Node", "Seam" };
    private static readonly string[] ShellFields = { "Bone", "Body" };
    public const int MaxOpeningParts = 64, MaxShellBones = 24;
    private static readonly string[] LightFields = { "RangeMultiplier", "AngleMultiplier", "IntensityMultiplier", "Colour", "Glow" };
    private static readonly string[] BoostFields = { "Seconds", "SpeedMultiplier", "Cooldown", "BreachDoors", "Shove" };
    private static readonly string[] SiegeFields = { "DeploySeconds", "DamageMultiplier", "DamageReduction", "FireRateMultiplier", "ReloadMultiplier", "RecoilMultiplier" };
    private static readonly string[] ScanFields = { "Radius", "Interval" };
    private static readonly string[] RocketsFields = { "Salvo", "Damage", "Radius", "Spread", "Cooldown", "Range", "Speed", "Launcher" };
    private static readonly string[] LauncherFields = { "Pod", "Round", "Bone", "Position", "Rotation" };
    private static readonly string[] ShieldFields = { "Capacity", "RegenDelay", "RegenRate", "Radius", "RaiseFraction", "WhileSieged" };

    public static FrameSpec Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new StrictJsonException("Payload", "must be an object.");
        StrictJson.OnlyKnown(payload, "", Fields, TypeField);
        var model = Asset(payload, "", "Model");
        if (payload.TryGetProperty("Boost", out _) && payload.TryGetProperty("Siege", out _))
            throw new StrictJsonException("Siege", "cannot be combined with Boost: both use the special key.");
        if (payload.TryGetProperty("Shield", out var sw) && sw.ValueKind == JsonValueKind.Object && StrictJson.Bool(sw, "Shield.", "WhileSieged", false) && !payload.TryGetProperty("Siege", out _))
            throw new StrictJsonException("Shield.WhileSieged", "needs a Siege: the barrier stands while the suit is planted.");
        return new FrameSpec(model,
            (float)StrictJson.Number(payload, "", "Armor", null, 1, 100000),
            (float)StrictJson.Number(payload, "", "DamageReduction", 0, 0, 0.9),
            (float)StrictJson.Number(payload, "", "EyeHeight", 2.0, 1.0, 4.0),
            Movement(payload.TryGetProperty("Movement", out var mv) ? mv : default),
            Weapon(StrictJson.Object(payload, "", "Weapon")),
            Light(payload.TryGetProperty("Light", out var li) ? li : default),
            payload.TryGetProperty("Boost", out var b) ? Boost(b) : null,
            payload.TryGetProperty("Siege", out var s) ? Siege(s) : null,
            payload.TryGetProperty("Scan", out var c) ? Scan(c) : null,
            payload.TryGetProperty("Shield", out var d) ? Shield(d) : null,
            StrictJson.Bool(payload, "", "CarryHeavy", false),
            payload.TryGetProperty("Rockets", out var k) ? Rockets(k) : null,
            payload.TryGetProperty("Opening", out var o) ? Opening(o) : null,
            payload.TryGetProperty("Shell", out var sh) ? Shell(sh) : Array.Empty<ShellBone>());
    }

    private static WeaponSpec Weapon(JsonElement w)
    {
        const string p = "Weapon.";
        StrictJson.OnlyKnown(w, p, WeaponFields);
        return new WeaponSpec(
            (uint)StrictJson.Integer(w, p, "Gear", null, 1, uint.MaxValue),
            (float)StrictJson.Number(w, p, "DamageMultiplier", 1, 0.1, 20),
            (int)StrictJson.Integer(w, p, "Magazine", null, 1, 10000),
            (int)StrictJson.Integer(w, p, "Reserve", 0, 0, 100000),
            (float)StrictJson.Number(w, p, "RecoilMultiplier", 1, 0.05, 1),
            (float)StrictJson.Number(w, p, "ReloadMultiplier", 1, 0.2, 1));
    }

    /// <summary>Absent: a little faster and higher than on foot.</summary>
    private static MovementSpec Movement(JsonElement m)
    {
        const string p = "Movement.";
        if (m.ValueKind == JsonValueKind.Undefined) return new MovementSpec(1.1f, 1.2f, 1.3f, 5f, 8f);
        StrictJson.OnlyKnown(m, p, MovementFields);
        return new MovementSpec((float)StrictJson.Number(m, p, "Walk", 1.1, 1, 3), (float)StrictJson.Number(m, p, "Sprint", 1.2, 1, 3),
            (float)StrictJson.Number(m, p, "Jump", 1.3, 1, 4),
            (float)StrictJson.Number(m, p, "SprintSeconds", 5, 0.5, 120), (float)StrictJson.Number(m, p, "SprintRecovery", 8, 0.5, 120));
    }

    private static OpeningSpec Opening(JsonElement o)
    {
        const string p = "Opening.";
        StrictJson.OnlyKnown(o, p, OpeningFields);
        if (!o.TryGetProperty("Parts", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new StrictJsonException(p + "Parts", "is required and must be an array.");
        var count = list.GetArrayLength();
        if (count < 1 || count > MaxOpeningParts) throw new StrictJsonException(p + "Parts", "must list 1.." + MaxOpeningParts + " parts.");
        var parts = new OpeningPart[count];
        var i = 0;
        foreach (var item in list.EnumerateArray())
        {
            var q = p + "Parts[" + i + "].";
            StrictJson.OnlyKnown(item, q, PartFields);
            var node = Name(item, q, "Node");
            for (var j = 0; j < i; j++) if (parts[j].Node == node) throw new StrictJsonException(q + "Node", "'" + node + "' is listed twice.");
            var seam = item.TryGetProperty("Seam", out var s) && (s.ValueKind == JsonValueKind.True ? true
                : s.ValueKind == JsonValueKind.False ? false : throw new StrictJsonException(q + "Seam", "must be true or false."));
            parts[i++] = new OpeningPart(node, seam);
        }
        return new OpeningSpec(parts);
    }

    private static ShellBone[] Shell(JsonElement s)
    {
        const string p = "Shell";
        if (s.ValueKind != JsonValueKind.Array) throw new StrictJsonException(p, "must be an array.");
        var count = s.GetArrayLength();
        if (count < 1 || count > MaxShellBones) throw new StrictJsonException(p, "must list 1.." + MaxShellBones + " bones.");
        var shell = new ShellBone[count];
        var i = 0;
        foreach (var item in s.EnumerateArray())
        {
            var q = p + "[" + i + "].";
            StrictJson.OnlyKnown(item, q, ShellFields);
            var bone = Name(item, q, "Bone");
            var name = Name(item, q, "Body");
            if (!Enum.TryParse<FrameBodyBone>(name, false, out var body) || !Enum.IsDefined(body) || char.IsDigit(name[0]))
                throw new StrictJsonException(q + "Body", "'" + name + "' is not a body bone (known: " + string.Join(", ", Enum.GetNames<FrameBodyBone>()) + ").");
            for (var j = 0; j < i; j++)
            {
                if (shell[j].Bone == bone) throw new StrictJsonException(q + "Bone", "'" + bone + "' is listed twice.");
                if (shell[j].Body == body) throw new StrictJsonException(q + "Body", "'" + name + "' is listed twice.");
            }
            shell[i++] = new ShellBone(bone, body);
        }
        return shell;
    }

    private static string Name(JsonElement o, string prefix, string field)
    {
        var text = o.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (text.Length == 0 || text.Length > 64) throw new StrictJsonException(prefix + field, "is required: a name of 1-64 characters.");
        return text;
    }

    /// <summary>A ForgeVisual prefab name <c>Pack:item</c> (the pack a mod registered with ForgeVisual's <c>Bundles.Register</c>).</summary>
    private static string Asset(JsonElement o, string prefix, string field)
    {
        var text = Name(o, prefix, field);
        if (!FrameModelName.TrySplit(text, out _, out _)) throw new StrictJsonException(prefix + field, "'" + text + "' must be Pack:item, a prefab of a ForgeVisual pack.");
        return text;
    }

    /// <summary>Absent: twice the flashlight's reach, a wider cone, warm white; cyan light strips.</summary>
    private static LightSpec Light(JsonElement l)
    {
        const string p = "Light.";
        if (l.ValueKind == JsonValueKind.Undefined) l = JsonDocument.Parse("{}").RootElement;
        StrictJson.OnlyKnown(l, p, LightFields);
        var (r, g, b) = Colour(l, p, "Colour", "#FFEBD1");
        var (gr, gg, gb) = Colour(l, p, "Glow", "#22D3FF");
        return new LightSpec((float)StrictJson.Number(l, p, "RangeMultiplier", 2, 0.5, 5), (float)StrictJson.Number(l, p, "AngleMultiplier", 1.4, 0.5, 3),
            (float)StrictJson.Number(l, p, "IntensityMultiplier", 1.2, 0.2, 5), r, g, b, gr, gg, gb);
    }

    private static (float, float, float) Colour(JsonElement o, string prefix, string field, string fallback)
    {
        var text = fallback;
        if (o.TryGetProperty(field, out var v))
            text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : throw new StrictJsonException(prefix + field, "must be a colour \"#RRGGBB\".");
        if (text.Length != 7 || text[0] != '#' || !int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            throw new StrictJsonException(prefix + field, "must be a colour \"#RRGGBB\".");
        return (((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
    }

    private static BoostSpec Boost(JsonElement b)
    {
        const string p = "Boost.";
        StrictJson.OnlyKnown(b, p, BoostFields);
        return new BoostSpec((float)StrictJson.Number(b, p, "Seconds", null, 0.2, 30), (float)StrictJson.Number(b, p, "SpeedMultiplier", null, 1, 4),
            (float)StrictJson.Number(b, p, "Cooldown", null, 0, 600), StrictJson.Bool(b, p, "BreachDoors", false), (float)StrictJson.Number(b, p, "Shove", 0, 0, 100));
    }

    private static SiegeSpec Siege(JsonElement s)
    {
        const string p = "Siege.";
        StrictJson.OnlyKnown(s, p, SiegeFields);
        return new SiegeSpec((float)StrictJson.Number(s, p, "DeploySeconds", 1, 0, 10), (float)StrictJson.Number(s, p, "DamageMultiplier", 1, 1, 5),
            (float)StrictJson.Number(s, p, "DamageReduction", 0, 0, 0.9), (float)StrictJson.Number(s, p, "FireRateMultiplier", 1, 1, 3), (float)StrictJson.Number(s, p, "ReloadMultiplier", 1, 0.2, 1),
            (float)StrictJson.Number(s, p, "RecoilMultiplier", 1, 0.1, 1));
    }

    private static ScanSpec Scan(JsonElement s)
    {
        const string p = "Scan.";
        StrictJson.OnlyKnown(s, p, ScanFields);
        return new ScanSpec((float)StrictJson.Number(s, p, "Radius", null, 1, 100), (float)StrictJson.Number(s, p, "Interval", 5, 1, 120));
    }

    private static ShieldSpec Shield(JsonElement s)
    {
        const string p = "Shield.";
        StrictJson.OnlyKnown(s, p, ShieldFields);
        return new ShieldSpec((float)StrictJson.Number(s, p, "Capacity", null, 1, 100000), (float)StrictJson.Number(s, p, "RegenDelay", 6, 0, 120),
            (float)StrictJson.Number(s, p, "RegenRate", null, 0.01, 10000), (float)StrictJson.Number(s, p, "Radius", 2.75, 1, 6),
            (float)StrictJson.Number(s, p, "RaiseFraction", 0.5, 0.05, 1), StrictJson.Bool(s, p, "WhileSieged", false));
    }

    private static RocketsSpec Rockets(JsonElement s)
    {
        const string p = "Rockets.";
        StrictJson.OnlyKnown(s, p, RocketsFields);
        return new RocketsSpec((int)StrictJson.Integer(s, p, "Salvo", 4, 1, 8), (float)StrictJson.Number(s, p, "Damage", null, 0.1, 1000),
            (float)StrictJson.Number(s, p, "Radius", 4, 0.5, 15), (float)StrictJson.Number(s, p, "Spread", 1.5, 0, 10), (float)StrictJson.Number(s, p, "Cooldown", 12, 1, 300),
            (float)StrictJson.Number(s, p, "Range", 120, 5, 300), (float)StrictJson.Number(s, p, "Speed", 45, 5, 200), Launcher(s.TryGetProperty("Launcher", out var l) && l.ValueKind == JsonValueKind.Object ? l
                : throw new StrictJsonException(p + "Launcher", "is required and must be an object.")));
    }

    private static RocketLauncherSpec Launcher(JsonElement l)
    {
        const string p = "Rockets.Launcher.";
        StrictJson.OnlyKnown(l, p, LauncherFields);
        var at = Vector(l, p, "Position", 4);
        var turn = Vector(l, p, "Rotation", 180);
        return new RocketLauncherSpec(Asset(l, p, "Pod"), Asset(l, p, "Round"), Name(l, p, "Bone"), at[0], at[1], at[2], turn[0], turn[1], turn[2]);
    }

    /// <summary>Three finite numbers, each within +-<paramref name="limit"/>.</summary>
    private static float[] Vector(JsonElement o, string prefix, string field, double limit)
    {
        if (!o.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 3)
            throw new StrictJsonException(prefix + field, "is required: three numbers.");
        var result = new float[3];
        var i = 0;
        foreach (var n in v.EnumerateArray())
        {
            if (!StrictJson.Finite(n, out var x) || Math.Abs(x) > limit)
                throw new StrictJsonException(prefix + field, "must be three finite numbers within -" + limit.ToString(CultureInfo.InvariantCulture) + ".." + limit.ToString(CultureInfo.InvariantCulture) + ".");
            result[i++] = (float)x;
        }
        return result;
    }
}

/// <summary>A prefab name <c>Pack:item</c>, the way ForgeVisual names models. The suit's cockpit mark is the icon <c>mark-item</c> in
/// the ForgeUI icon pack of the same name as the model's pack.</summary>
public static class FrameModelName
{
    /// <summary>False unless <paramref name="name"/> is exactly one pack and one item, both non-empty (ForgeVisual checks the characters
    /// and that the pack and prefab exist).</summary>
    public static bool TrySplit(string? name, out string pack, out string item)
    {
        pack = item = "";
        if (string.IsNullOrEmpty(name)) return false;
        var colon = name.IndexOf(':');
        if (colon <= 0 || colon == name.Length - 1 || name.IndexOf(':', colon + 1) >= 0) return false;
        pack = name.Substring(0, colon);
        item = name.Substring(colon + 1);
        return true;
    }

    /// <summary>The icon name of the suit's cockpit mark: <c>Pack:mark-item</c>.</summary>
    public static string MarkIcon(string model) => TrySplit(model, out var pack, out var item) ? pack + ":mark-" + item : throw new ArgumentException("'" + model + "' is not Pack:item.", nameof(model));
}

/// <summary>A content key of a parsed model: every peer that loaded the same definition computes the same key, so the host can
/// name a suit's model in its state and a late joiner (who never saw the call arrive) finds the spec it parsed itself.</summary>
public static class FrameSpecKey
{
    public static ulong Of(FrameSpec s)
    {
        var h = Fnv.Offset64;
        void B(byte b) => h = Fnv.Mix64(h, b);
        void I(int v) { for (var i = 0; i < 4; i++) B((byte)(v >> (8 * i))); }
        void F(float v) => I(BitConverter.SingleToInt32Bits(v));
        void Z(bool v) => B(v ? (byte)1 : (byte)0);
        I(s.Model.Length);
        foreach (var c in s.Model) { B((byte)c); B((byte)(c >> 8)); }
        F(s.Armor); F(s.DamageReduction); F(s.EyeHeight);
        var m = s.Movement;
        F(m.Walk); F(m.Sprint); F(m.Jump); F(m.SprintSeconds); F(m.SprintRecovery);
        var w = s.Weapon;
        I(unchecked((int)w.Gear)); F(w.DamageMultiplier); I(w.Magazine); I(w.Reserve); F(w.RecoilMultiplier); F(w.ReloadMultiplier);
        var l = s.Light;
        F(l.RangeMultiplier); F(l.AngleMultiplier); F(l.IntensityMultiplier); F(l.R); F(l.G); F(l.B); F(l.GlowR); F(l.GlowG); F(l.GlowB);
        Z(s.Boost != null); if (s.Boost != null) { F(s.Boost.Seconds); F(s.Boost.SpeedMultiplier); F(s.Boost.Cooldown); Z(s.Boost.BreachDoors); F(s.Boost.Shove); }
        Z(s.Siege != null); if (s.Siege != null) { F(s.Siege.DeploySeconds); F(s.Siege.DamageMultiplier); F(s.Siege.DamageReduction); F(s.Siege.FireRateMultiplier); F(s.Siege.ReloadMultiplier); F(s.Siege.RecoilMultiplier); }
        Z(s.Scan != null); if (s.Scan != null) { F(s.Scan.Radius); F(s.Scan.Interval); }
        Z(s.Shield != null); if (s.Shield != null) { F(s.Shield.Capacity); F(s.Shield.RegenDelay); F(s.Shield.RegenRate); F(s.Shield.Radius); F(s.Shield.RaiseFraction); Z(s.Shield.WhileSieged); }
        Z(s.CarryHeavy);
        Z(s.Rockets != null); if (s.Rockets != null) { I(s.Rockets.Salvo); F(s.Rockets.Damage); F(s.Rockets.Radius); F(s.Rockets.Spread); F(s.Rockets.Cooldown); F(s.Rockets.Range); F(s.Rockets.Speed); }
        void S(string v) { I(v.Length); foreach (var c in v) { B((byte)c); B((byte)(c >> 8)); } }
        if (s.Rockets != null)
        {
            var launcher = s.Rockets.Launcher;
            S(launcher.Pod); S(launcher.Round); S(launcher.Bone);
            F(launcher.X); F(launcher.Y); F(launcher.Z); F(launcher.Pitch); F(launcher.Yaw); F(launcher.Roll);
        }
        Z(s.Opening != null);
        if (s.Opening != null)
        {
            I(s.Opening.Parts.Length);
            foreach (var part in s.Opening.Parts) { S(part.Node); Z(part.Seam); }
        }
        I(s.Shell.Length);
        foreach (var bone in s.Shell) { S(bone.Bone); I((int)bone.Body); }
        return h;
    }
}
