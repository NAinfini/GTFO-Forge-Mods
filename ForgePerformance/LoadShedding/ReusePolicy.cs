using System;
using System.Collections.Generic;

namespace ForgePerformance.LoadShedding;

// Pure rules of the leak fixes and the melee packet reuse. No game types, so they run offline.
internal static class ReusePolicy
{
    internal enum MeleePacketAction
    {
        // Leave the packet Setup just created alone.
        Keep,
        // The created packet becomes the one every later melee weapon shares.
        Adopt,
        // Point the created packet's slot and the weapon at the shared packet. The slot itself stays, so no
        // packet index changes on this machine or differs from another machine's.
        Alias
    }

    // Removes every entry whose pointer is in targets and returns how many were removed. Scans from the end so
    // an index stays valid after a removal.
    internal static int RemoveMatching(int count, Func<int, IntPtr> pointerAt, Action<int> removeAt, HashSet<IntPtr> targets)
    {
        if (pointerAt is null) throw new ArgumentNullException(nameof(pointerAt));
        if (removeAt is null) throw new ArgumentNullException(nameof(removeAt));
        if (targets is null) throw new ArgumentNullException(nameof(targets));
        var removed = 0;
        for (var i = count - 1; i >= 0; i--)
        {
            if (!targets.Contains(pointerAt(i))) continue;
            removeAt(i);
            removed++;
        }
        return removed;
    }

    // What to do with the melee hit packet a Setup call just created.
    //  - shared: the packet of the first Setup of this network session, or zero when there is none or its
    //    replicator is gone (a new session registers every packet again).
    //  - createdHasSlot: the created packet sits at its own index in one of the action manager's replicators.
    internal static MeleePacketAction DecideMeleePacket(IntPtr created, IntPtr shared, bool createdHasSlot)
    {
        if (created == IntPtr.Zero) return MeleePacketAction.Keep;
        if (shared == IntPtr.Zero) return MeleePacketAction.Adopt;
        if (created == shared || !createdHasSlot) return MeleePacketAction.Keep;
        return MeleePacketAction.Alias;
    }
}
