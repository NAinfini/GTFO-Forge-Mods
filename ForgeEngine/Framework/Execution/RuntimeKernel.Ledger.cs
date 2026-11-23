using System;
using System.Globalization;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    /// <summary>How often, in ticks, the ledger forgets the rows the horizon allows. A full ledger also sweeps, at
    /// most once per tick, before it refuses.</summary>
    internal const int EventHistorySweepTicks = 256;
    /// <summary>How much of the capacity one eviction frees when the horizon alone cannot: an eighth, so a world
    /// publishing at its full per-tick budget pays one table scan per few thousand admissions, not one per event.</summary>
    internal const int EventHistoryEvictionRows = MaximumEventHistory / 8;

    /// <summary>The three identity spaces the replay ledger keeps. A lease and a schedule are remembered beside the
    /// events of the same provider, under their own space, so an event id can never collide with either.</summary>
    internal enum LedgerSpace : byte { Event, Lease, Schedule }

    /// <summary>One replay-ledger identity. It holds the provider and id strings the caller already owns, so an
    /// entry allocates no text of its own; <see cref="Text"/> is the spelling the migration image carries.</summary>
    internal readonly record struct LedgerKey(string Provider, LedgerSpace Space, string Id)
    {
        internal string Text => Space switch
        {
            LedgerSpace.Lease => Provider + "\0lease\0" + Id,
            LedgerSpace.Schedule => Provider + "\0schedule\0" + Id,
            _ => Provider + "\0" + Id
        };

        /// <summary>Reads <see cref="Text"/> back. A provider and an id never contain a control character
        /// (<see cref="RuntimeJson.Text(string)"/>), so the separators are unambiguous.</summary>
        internal static LedgerKey Parse(string text)
        {
            var split = text.IndexOf('\0');
            RuntimeJson.Require(split > 0 && split < text.Length - 1, "migration-history", "Invalid event ledger key.");
            var provider = text[..split]; var rest = text[(split + 1)..];
            var space = LedgerSpace.Event;
            if (rest.StartsWith("lease\0", StringComparison.Ordinal)) { space = LedgerSpace.Lease; rest = rest[6..]; }
            else if (rest.StartsWith("schedule\0", StringComparison.Ordinal)) { space = LedgerSpace.Schedule; rest = rest[9..]; }
            RuntimeJson.Text(provider); RuntimeJson.Text(rest);
            return new LedgerKey(provider, space, rest);
        }
    }

    /// <summary>The 128-bit value a repeat is compared with. Numeric, so a row carries no string; its hex spelling
    /// is only written when a migration image is taken.</summary>
    internal readonly record struct LedgerHash(ulong Low, ulong High)
    {
        internal string Text => Low.ToString("x16", CultureInfo.InvariantCulture) + High.ToString("x16", CultureInfo.InvariantCulture);

        internal static LedgerHash Parse(string text)
        {
            ulong low = 0, high = 0;
            RuntimeJson.Require(text.Length == 32
                && ulong.TryParse(text.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out low)
                && ulong.TryParse(text.AsSpan(16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out high),
                "migration-history", "Invalid event ledger fingerprint.");
            return new LedgerHash(low, high);
        }
    }

    /// <summary>One replay-ledger identity's row: the fingerprint a repeat is compared with, and the tick the horizon
    /// is measured from. That tick is the event's own simulation tick, or for a lease or schedule the last tick a
    /// sweep saw it active.</summary>
    internal readonly record struct LedgerRow(LedgerHash Fingerprint, long Tick);
}
