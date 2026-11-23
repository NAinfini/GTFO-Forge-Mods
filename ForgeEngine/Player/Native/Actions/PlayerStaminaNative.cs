namespace ForgePlayer.Native;

/// <summary>The two requests the game itself has for changing a player's stamina, as one place the stamina row
/// calls them from. Neither sends a packet: they act on the `PlayerStamina` of the process that runs them, which is
/// why the row is an `owner` row.
///
/// `UseStamina(cost, deltaTime)` (RVA 0x12cd950) subtracts `cost * deltaTime` from the current value, clamps to
/// `0..1`, raises the result to the out-of-action minimum from `PlayerData` when the player is not in an action
/// state, keeps it at or below the previous value and writes it through `set_Stamina`; the two reset-timer flags of
/// the cost only decide whether `m_lastExertion` is restamped. `ResetStamina()` (RVA 0x12cd6f0) writes `1` through
/// the same setter. The sources are compiled against the game's own interop here and against a double in tests.</summary>
internal static class PlayerStaminaNative
{
    /// <summary>Spends the reserve through `UseStamina` with the game's own four cost fields: the amount out of
    /// combat, the amount in combat, and whether each case restamps the resting timer (delays natural recovery).
    /// The game chooses between the two halves inside the call from the player's own combat state, so the row
    /// never reads that state itself.</summary>
    internal static void Consume(PlayerStamina stamina, float cost, float costInCombat, bool resetRecovery, bool resetRecoveryInCombat)
        => stamina.UseStamina(new PlayerStamina.ActionCost
        {
            baseStaminaCostInCombat = costInCombat,
            baseStaminaCostOutOfCombat = cost,
            resetRestingTimerInCombat = resetRecoveryInCombat,
            resetRestingTimerOutOfCombat = resetRecovery
        }, 1f);

    /// <summary>Writes the reserve directly: the one member write for a value neither request can say.</summary>
    internal static void Write(PlayerStamina stamina, float value) => stamina.Stamina = value;

    /// <summary>Fills the reserve through `ResetStamina`.</summary>
    internal static void Reset(PlayerStamina stamina) => stamina.ResetStamina();
}
