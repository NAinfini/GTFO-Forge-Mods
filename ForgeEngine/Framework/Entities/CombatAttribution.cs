namespace ForgeEngine.Framework;

/// <summary>Which exact equipment made the native attack that is settling right now, as the provider that sees it
/// publishes it and as the providers that observe a damage application read it.
///
/// This is a cross-provider seam and not a fact: the weapon package is the only code that sees a weapon `Fire` and
/// therefore the only code that knows the equipment life behind a hit, while the damage facts are published by the
/// package that owns the damaged subject. Neither package may name the other, so the correlation they share lives
/// here, beside the entities it is keyed by.
///
/// The unit is the synchronous native attack call. Its exact weapon instance supplies the equipment life;
/// packets, held slots and gear categories cannot create an attribution. The caller saves and restores the
/// enclosing pair around nested native calls, including calls with an unknown source. Only the innermost call
/// is visible, so no dictionary, previous-shot history or per-call allocation is needed.</summary>
internal static class CombatShotAttribution
{
    public static EntityReference? CurrentSource { get; private set; }
    private static EntityReference? _equipment;

    /// <summary>Opens one native attack: this equipment is what its synchronous damage applications came from. Called
    /// from the attack window itself, before any hit lands, because those hits are applied inside
    /// the same native call the shot was opened in.</summary>
    public static void RecordShot(EntityReference shooter, EntityReference equipment)
    {
        // Two lives of one world, never one: a caller that names nothing, or names the same reference for both, is
        // naming the wrong thing, and the table would otherwise answer a weapon as its own shooter.
        if (shooter == null || equipment == null || shooter == equipment)
            throw new RuntimeContractException("shot-attribution-argument",
                "A shot names its shooter and its equipment, and the two are different lives.");
        CurrentSource = shooter;
        _equipment = equipment;
    }

    /// <summary>The equipment life of this native call, or null (unavailable) for another actor, world or life.
    /// An unknown packet never inherits an enclosing attacker's equipment.</summary>
    public static EntityReference? EquipmentOf(EntityReference? shooter)
        => shooter != null && shooter == CurrentSource ? _equipment : null;

    /// <summary>Closes the currently attributed native call. Its caller restores the saved enclosing pair.</summary>
    public static void Forget(EntityReference shooter)
    {
        if (shooter != null && shooter == CurrentSource) { CurrentSource = null; _equipment = null; }
    }
}
