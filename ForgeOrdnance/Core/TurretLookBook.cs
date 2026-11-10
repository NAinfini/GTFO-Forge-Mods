using System;
using System.Collections.Generic;

namespace ForgeOrdnance;

/// <summary>How a turret look model is built: the names of its four moving parts and its size. A look is a model with a
/// <see cref="Base"/> that stands still on the ground, a <see cref="Yaw"/> part that turns with the sentry's aim, a <see cref="Head"/>
/// that also pitches (the gun), a <see cref="Muzzle"/> point at the barrel tip where shots, flash and tracers start, and optionally a <see cref="Barrel"/> under the head. Origins:
/// base and yaw on the ground at the yaw axis, head on the pitch axis; the model's own front is −Z. The whole model rises out of the
/// turret's drop base once its lids have gone, the <see cref="Head"/> turning from barrel up to level (<see cref="Burrow"/>), before the game's sentry stands.</summary>
public sealed class TurretLookSpec
{
    public string Base { get; set; } = "base";
    public string Yaw { get; set; } = "yaw";
    public string Head { get; set; } = "head";
    public string Muzzle { get; set; } = "muzzle";
    /// <summary>Optional part under <see cref="Head"/>, its origin on the bore axis: what slides back on a shot and spins. A model without it recoils as a whole head.</summary>
    public string Barrel { get; set; } = "barrel";
    /// <summary>The look's world scale about its floor point, whatever scale the game's sentry object has (the game's sentry is scaled
    /// 1.5, which a local scale would be multiplied by).</summary>
    public float WorldScale { get; set; } = 1f;
    /// <summary>How the turret settles in and goes out (see <see cref="BurrowSpec"/>): the look's own height decides <c>RiseHeight</c>, the
    /// barrel of its <see cref="Head"/> is what turns down, so keep <c>TurnSeconds</c> above 0.</summary>
    public BurrowSpec Burrow { get; set; } = new() { TurnSeconds = 0.8f };
    /// <summary>How the look moves while it stands (see <see cref="TurretMotionSpec"/>).</summary>
    public TurretMotionSpec Motion { get; set; } = new();

    /// <summary>What is wrong with this spec, or null when it is usable.</summary>
    internal string? Problem()
    {
        if (Burrow == null) return "Burrow is missing";
        if (Burrow.Problem() is { } burrow) return burrow;
        if (Motion == null) return "Motion is missing";
        if (Motion.Problem() is { } motion) return motion;
        var names = new[] { Base, Yaw, Head, Muzzle };
        foreach (var name in names)
            if (string.IsNullOrWhiteSpace(name)) return "a part name is empty";
        if (new HashSet<string>(names, StringComparer.Ordinal).Count != names.Length || Array.IndexOf(names, Barrel) >= 0) return "two parts have the same name";
        if (!float.IsFinite(WorldScale) || WorldScale < 0.1f || WorldScale > 5f) return "WorldScale must be between 0.1 and 5";
        return null;
    }
}

/// <summary>A standing turret's own movement, drawn on top of the game's aim: the idle scan, the kick of a shot and the barrels.</summary>
public sealed class TurretMotionSpec
{
    /// <summary>Degrees to each side and seconds per cycle of the idle scan; 0 degrees keeps it still.</summary>
    public float ScanDegrees { get; set; } = 25f;
    public float ScanSeconds { get; set; } = 7f;
    /// <summary>Metres the head slides back and degrees it tips up on one shot; quick shots add up to <see cref="MaxRecoil"/> metres and four shots' worth of tip.</summary>
    public float Recoil { get; set; } = 0.03f;
    public float Kick { get; set; } = 1f;
    public float MaxRecoil { get; set; } = 0.08f;
    /// <summary>How fast the kick returns: the part left after one second is e^-this.</summary>
    public float RecoilReturn { get; set; } = 12f;
    /// <summary>Metres the barrel alone slides back on one shot.</summary>
    public float BarrelRecoil { get; set; }
    /// <summary>Degrees a second the barrel spins while firing (a rotary gun), running down over <see cref="SpinDownSeconds"/>; 0 for a barrel that does not spin.</summary>
    public float BarrelSpin { get; set; }
    public float SpinDownSeconds { get; set; } = 0.9f;

    /// <summary>What is wrong with these numbers, or null when they are usable.</summary>
    public string? Problem()
    {
        string? Bad(string field, float v, float min, float max)
            => float.IsFinite(v) && v >= min && v <= max ? null : field + " must be between " + min + " and " + max;
        return Bad("ScanDegrees", ScanDegrees, 0f, 80f) ?? Bad("ScanSeconds", ScanSeconds, 1f, 30f) ?? Bad("Recoil", Recoil, 0f, 0.5f) ?? Bad("Kick", Kick, 0f, 20f)
            ?? Bad("MaxRecoil", MaxRecoil, 0f, 0.5f) ?? Bad("RecoilReturn", RecoilReturn, 1f, 60f) ?? Bad("BarrelRecoil", BarrelRecoil, 0f, 0.5f)
            ?? Bad("BarrelSpin", BarrelSpin, 0f, 5000f) ?? Bad("SpinDownSeconds", SpinDownSeconds, 0.05f, 5f);
    }
}

/// <summary>The turret looks this install knows, by their <c>Pack:item</c> id (the name of the model in ForgeVisual) and by the 32-bit
/// key that travels in a sentry's marker so every peer picks the same look. A definition's <c>Look</c> must name a registered one.</summary>
internal sealed class TurretLookBook
{
    private readonly Dictionary<string, TurretLookSpec> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _byKey = new();

    /// <summary>"Pack:item": two non-empty parts joined by one colon, letters, digits, '_', '-' and '.' only.</summary>
    internal static bool IsLookId(string? id)
    {
        if (!ForgeCore.PackItem.TrySplit(id, out _, out _)) return false;
        foreach (var c in id!)
            if (c != ':' && !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')) return false;
        return true;
    }

    /// <summary>Adds a look. Returns null on success, else why it was refused.</summary>
    internal string? Register(string lookId, TurretLookSpec spec)
    {
        if (!IsLookId(lookId)) return "the look id must be written Pack:item (letters, digits, '_', '-', '.')";
        if (spec == null) return "no spec was given";
        if (spec.Problem() is { } problem) return problem;
        if (_byId.ContainsKey(lookId)) return "the look is already registered";
        var key = ForgeCore.Fnv.Hash32(lookId);
        if (key == 0 || _byKey.ContainsKey(key)) return "the look id has the same network key as another registered look; choose another name";
        _byId.Add(lookId, spec);
        _byKey.Add(key, lookId);
        return null;
    }

    internal bool IsRegistered(string lookId) => _byId.ContainsKey(lookId);

    internal static uint KeyOf(string lookId) => ForgeCore.Fnv.Hash32(lookId);

    internal bool TryGet(string lookId, out TurretLookSpec spec) => _byId.TryGetValue(lookId, out spec!);

    internal bool TryGetByKey(uint key, out string lookId) => _byKey.TryGetValue(key, out lookId!);
}
