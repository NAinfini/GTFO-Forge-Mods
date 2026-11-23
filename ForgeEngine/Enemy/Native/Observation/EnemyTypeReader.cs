using System;
using Enemies;

namespace ForgeEnemy.Native.Observation;

/// <summary>
/// The official enemy type an agent was built from: the <c>EnemyDataBlock.persistentID</c> a plan's
/// <c>enemy-type</c> mount names (interop `GameData.EnemyDataBlock Enemies.EnemyAgent::get_EnemyData()` plus
/// `System.UInt32 GameData.GameDataBlockBase`1&lt;GameData.EnemyDataBlock&gt;::get_persistentID()`, both frozen in
/// evidence/native-api-20403457.json). The read is explicit-instance and read-only: the agent is judged first,
/// its block is read once and read back, and an agent whose block cannot be read at all has no type here — an
/// unreadable type never matches a mount.
/// </summary>
internal static class EnemyTypeReader
{
    /// <summary>The read for an agent the spawn hook has just handed over: the agent need not report itself set up yet,
    /// only carry its data block. The module reads it once per life when it tracks the life (<c>EnemyModule.Entry.Type</c>);
    /// plan matching, the type query and the spawn fact answer from that capture. A second, stricter live read (set-up
    /// agent only, two getter results compared by managed reference) answered nothing for every killed life in the V1
    /// B1 run, which refused every type-filtered kill count; it is gone.</summary>
    internal static uint? ReadSpawned(EnemyAgent enemy)
    {
        try
        {
            if (enemy == null || enemy.Pointer == IntPtr.Zero) return null;
            var block = enemy.EnemyData;
            return block == null ? null : block.persistentID;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return null; }
    }
}
