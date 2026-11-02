namespace ForgeOrdnance;

/// <summary>What the player's class tool slot looks like: the carried item, whether the slot is marked deployed and its
/// inventory status. A summoned turret must leave all three exactly as they were.</summary>
internal readonly record struct ToolSlotState(string Item, bool Deployed, int Status)
{
    internal string Describe() => Item + " (deployed " + (Deployed ? "yes" : "no") + ", status " + Status + ")";
}

/// <summary>While a summoned turret runs its own spawn, pick-up, despawn or cleanup, the game's sentry code marks the
/// owner's class slot deployed or free again as if the owner had placed a carried sentry. Inside this guard that write
/// is refused, so the summoned turret never touches the owner's tool slot.</summary>
internal sealed class ToolSlotGuard
{
    private int _depth;

    internal bool Active => _depth > 0;

    internal void Enter() => _depth++;

    internal void Exit()
    {
        if (_depth > 0) _depth--;
    }

    internal bool AllowsDeployChange(bool classSlot) => !(Active && classSlot);
}
