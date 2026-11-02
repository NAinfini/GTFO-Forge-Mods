using System;
using Agents;

namespace ForgeCore;

/// <summary>The game's modifier table of one agent, kept by a caller that must later check removal against exactly the table the
/// modification was added to, even if the agent's current table has since been replaced.</summary>
public sealed class ModifierTable
{
    private readonly ModifierInstance _table;
    internal ModifierTable(ModifierInstance table) { _table = table; Pointer = table.Pointer; }

    public IntPtr Pointer { get; }

    /// <summary>True while this is still the same live table.</summary>
    public bool IsLive => _table.Pointer == Pointer;

    /// <summary>Whether the table still holds modification <paramref name="id"/>. Throws when the table is gone or unreadable,
    /// because then nothing can be said about the id.</summary>
    public bool Holds(uint id)
    {
        if (!IsLive) throw new InvalidOperationException("The game's modifier table was replaced.");
        var values = _table.m_idToModificationInstanceValue ?? throw new InvalidOperationException("The game's modifier table is unavailable.");
        return values.ContainsKey(id);
    }
}

/// <summary>What the game's modifier table holds for one synced modification: the value as submitted, the value the game cached
/// after its own limits and the time stamp.</summary>
public readonly record struct ModifierReading(float Original, float Cached, float TimeStamp);

/// <summary>Agent attribute modifiers through the game's own synced entry, <c>AgentModifierManager.AddSyncedModifierValue</c> and
/// <c>ClearSyncedModifierChange</c>, for players and enemies alike. A modification is a contribution to the agent's aggregate, so
/// it is read back after it is added and cleared by its id; this class only calls and reads, the caller decides amounts, caps and
/// when to clear. Main thread only.</summary>
public static class AgentModifiers
{
    /// <summary>Adds <paramref name="amount"/> to <paramref name="modifier"/> of <paramref name="agent"/> with no decay and returns
    /// the modification's id, or 0 when the game registered nothing (its ids ran out).</summary>
    public static uint Add(Agent agent, AgentModifier modifier, float amount)
    {
        if (agent == null) throw new ArgumentNullException(nameof(agent));
        return AgentModifierManager.AddSyncedModifierValue(agent, modifier, amount, 0f);
    }

    /// <summary>The agent's modifier table, or null when it has none yet.</summary>
    public static ModifierTable? TableOf(Agent agent)
        => AgentModifierManager.TryGetModifierInstance(agent, out var table) && table != null && table.Pointer != IntPtr.Zero ? new ModifierTable(table) : null;

    /// <summary>The game's record of modification <paramref name="id"/>: false when the agent has no table or the table holds no such
    /// id. Throws when the entry is not what <see cref="Add"/> made (another modifier, or a decay), because then the id no longer
    /// names our contribution.</summary>
    public static bool TryRead(Agent agent, uint id, AgentModifier modifier, out ModifierReading reading)
    {
        reading = default;
        if (!AgentModifierManager.TryGetModifierInstance(agent, out var table) || table == null || table.Pointer == IntPtr.Zero)
            return false;
        var values = table.m_idToModificationInstanceValue ?? throw new InvalidOperationException("The game's modifier table is unavailable.");
        if (!values.TryGetValue(id, out var value)) return false;
        if (value.Modifier != modifier || value.DeltaPerSec != 0f)
            throw new InvalidOperationException("The game's modifier entry " + id + " is not the contribution that was added.");
        reading = new ModifierReading(value.OriginalValue, value.CachedValue, value.TimeStamp);
        return true;
    }

    /// <summary>Clears modification <paramref name="id"/> through the game's synced entry (peers clear it too).</summary>
    public static void Clear(uint id) => AgentModifierManager.ClearSyncedModifierChange(id);
}
