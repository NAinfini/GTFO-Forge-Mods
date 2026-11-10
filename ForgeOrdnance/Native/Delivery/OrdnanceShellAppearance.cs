using System;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>
/// What a call-in's turret shell carries and how it settles: return one from <see cref="IOrdnancePayload.GetShellAppearance"/> on every peer
/// (late joiners build it from the definition alone). Forge owns the fall, the boring in, the rise, the barrel's turn and the
/// sinking. Two models: the base, <c>Ordnance:turret-shell</c> (nodes <c>body</c>, <c>well</c>, a dark disc under the rim that shows once the lids have gone, and the two lids <c>lid_a</c> and <c>lid_b</c>, thrown off when it has bored in; lowest point the drill tip, origin on the vertical axis; <see cref="BurrowSpec.BaseScale"/> sizes it to the upper part), and the
/// upper part, <see cref="Head"/>, which is hidden inside the base with its origin on the centre of the base rim when it stands.
/// </summary>
public sealed class OrdnanceShellAppearance
{
    public OrdnanceShellAppearance(BurrowSpec burrow, float lifetime)
    {
        Burrow = burrow ?? throw new ArgumentNullException(nameof(burrow));
        if (burrow.Problem() is { } problem) throw new ArgumentException(problem + ".", nameof(burrow));
        if (!float.IsFinite(lifetime) || lifetime < 0f) throw new ArgumentException("Lifetime must be 0 or more.", nameof(lifetime));
        Lifetime = lifetime;
    }

    public BurrowSpec Burrow { get; }
    /// <summary>Seconds the head works once it is ready, then it stows; 0 keeps it until the level ends.</summary>
    public float Lifetime { get; }
    /// <summary>The upper part standing on the base's top (a turret look or a device), or null when nothing is drawn by the shell.</summary>
    public ForgeVisual.ModelHandle? Head { get; set; }
    /// <summary>World scale of <see cref="Head"/>.</summary>
    public float HeadScale { get; set; } = 1f;
    /// <summary>Turns the head model's own front to +Z (exported models face -Z).</summary>
    public Quaternion HeadFront { get; set; } = Quaternion.Euler(0f, 180f, 0f);
    /// <summary>The part of <see cref="Head"/> whose barrel turns from straight up to level, or null for a head with no gun. A gun's
    /// head is handed to the game's sentry when it has turned down: the platform keeps drawing it until that sentry's own look stands.</summary>
    public string? Gun { get; set; }
    /// <summary>Called every frame on every peer with the seconds since the device was ready: how far the upper part is lowered back into the
    /// base, 0 (standing) to 1 (wholly inside, <see cref="BurrowSpec.RiseHeight"/> down), so a device can go in and come up again while it works.
    /// The base does not move. Must depend on the seconds alone, so a late joiner draws the same pose; null for none. Not used once it stows.</summary>
    public Func<float, float>? Dive { get; set; }
    /// <summary>The turret look id that <see cref="OrdnanceTurrets.Deployed"/> reports; empty when none.</summary>
    public string LookId { get; set; } = "";
}
