using System;
using System.Collections.Generic;

namespace ForgeOrdnance;

/// <summary>How a turret look model is built: the names of its four moving parts and its size. A look is a model with a
/// <see cref="Base"/> that stands still on the ground, a <see cref="Yaw"/> part that turns with the sentry's aim, a <see cref="Head"/>
/// that also pitches (the barrels) and a <see cref="Muzzle"/> point at the barrel tip where shots, flash and tracers start. Origins:
/// base and yaw on the ground at the yaw axis, head on the pitch axis; the model's own front is −Z.</summary>
public sealed class TurretLookSpec
{
    public string Base { get; set; } = "base";
    public string Yaw { get; set; } = "yaw";
    public string Head { get; set; } = "head";
    public string Muzzle { get; set; } = "muzzle";
    /// <summary>The look's world scale about its floor point, whatever scale the game's sentry object has (the game's sentry is scaled
    /// 1.5, which a local scale would be multiplied by).</summary>
    public float WorldScale { get; set; } = 1f;

    /// <summary>What is wrong with this spec, or null when it is usable.</summary>
    internal string? Problem()
    {
        var names = new[] { Base, Yaw, Head, Muzzle };
        foreach (var name in names)
            if (string.IsNullOrWhiteSpace(name)) return "a part name is empty";
        if (new HashSet<string>(names, StringComparer.Ordinal).Count != names.Length) return "two parts have the same name";
        if (!float.IsFinite(WorldScale) || WorldScale < 0.1f || WorldScale > 5f) return "WorldScale must be between 0.1 and 5";
        return null;
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
        if (string.IsNullOrEmpty(id)) return false;
        var colon = id.IndexOf(':');
        if (colon <= 0 || colon == id.Length - 1 || id.IndexOf(':', colon + 1) >= 0) return false;
        foreach (var c in id)
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
