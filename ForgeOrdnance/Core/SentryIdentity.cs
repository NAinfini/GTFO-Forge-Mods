using System;
using System.Globalization;

namespace ForgeOrdnance;

internal enum SentryTimerResolution { Wait, Bind, Discard }

/// <summary>The compact identity copied through native gear spawn data, and the ownership rule for replicated expiry timers.</summary>
internal static class SentryIdentity
{
    private const string Prefix = "FO:";
    /// <summary>"FO:" + 8 hex identity + ":" + P or U + ":" + 8 hex look key.</summary>
    private const int MarkerLength = 22;

    /// <summary>The marker a spawned sentry carries in its public gear name (the game replicates it to every peer, and its limit is about
    /// 25 characters): its Ordnance <paramref name="identity"/> (a counter, below 2^32), whether it can be picked up, and the key of its
    /// look (<see cref="TurretLookBook"/>; 0 for the game's own look).</summary>
    internal static string Marker(ulong identity, bool pickable, uint lookKey = 0)
    {
        if (identity == 0 || identity > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(identity));
        return Prefix + identity.ToString("x8", CultureInfo.InvariantCulture) + ":" + (pickable ? "P" : "U") + ":" + lookKey.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>The identity of the sentry a call stands: the same on every peer from the call alone (a hash below 2^32, never 0), so a shell and its
    /// sentry, and the multipliers asked for, are tied by the call and not by where each stands.</summary>
    internal static ulong For(ulong summoner, uint throwId)
    {
        var hash = ForgeCore.Fnv.Mix32(ForgeCore.Fnv.Mix32(ForgeCore.Fnv.Offset32, summoner), throwId);
        return hash == 0 ? 1UL : hash;
    }

    internal static bool TryMarker(string? marker, out ulong identity, out bool pickable) => TryMarker(marker, out identity, out pickable, out _);

    internal static bool TryMarker(string? marker, out ulong identity, out bool pickable, out uint lookKey)
    {
        identity = 0;
        pickable = false;
        lookKey = 0;
        if (marker == null || marker.Length != MarkerLength || !marker.StartsWith(Prefix, StringComparison.Ordinal) || marker[11] != ':' || marker[13] != ':') return false;
        if (!ulong.TryParse(marker.Substring(3, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out identity) || identity == 0) return false;
        if (!uint.TryParse(marker.Substring(14, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out lookKey)) return false;
        if (marker[12] == 'P') pickable = true;
        else if (marker[12] != 'U') return false;
        return true;
    }
    /// <summary>What is known of a sentry Ordnance stood: its identity, whether pickup is locked and the key of its look.</summary>
    internal readonly record struct SentryMark(ulong Identity, bool NoPickup, uint LookKey);

    /// <summary>The mark to keep when a sentry is registered again. The replicated marker is read on every peer as the sentry spawns; the
    /// host's own spawn callback registers it once more afterwards. A second registration of the same identity never takes back what
    /// the first gave (a look, a pickup lock); a different identity is a different sentry and replaces the old mark.</summary>
    internal static SentryMark Merge(SentryMark? known, SentryMark incoming)
        => known is { } k && k.Identity == incoming.Identity
            ? new SentryMark(k.Identity, k.NoPickup || incoming.NoPickup, k.LookKey != 0 ? k.LookKey : incoming.LookKey)
            : incoming;

    /// <summary>A timer binds only to its exact spawn identity. An absent or reused native key may wait for network reordering,
    /// but it exits at its deadline instead of attaching to a later sentry.</summary>
    internal static SentryTimerResolution ResolveTimer(ulong expected, ulong? actual, double now, double expiresAt)
        => expected != 0 && actual == expected ? SentryTimerResolution.Bind
            : now < expiresAt ? SentryTimerResolution.Wait
            : SentryTimerResolution.Discard;
}
