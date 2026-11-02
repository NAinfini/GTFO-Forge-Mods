namespace ForgeOrdnance;

/// <summary>When this player's loadout goes to the host: once on entering a level or meeting a new host, and again after every change
/// (the lobby picker can be opened in a level). A change waits while a call is being typed, held or still flying: the host would
/// otherwise judge that call against the new loadout.</summary>
internal sealed class LoadoutSubmission
{
    private bool _pending = true;

    /// <summary>A new level or a new host has not seen this player's loadout yet.</summary>
    internal void Reset() => _pending = true;

    /// <summary>The player changed the picks.</summary>
    internal void Changed() => _pending = true;

    internal void Sent() => _pending = false;

    /// <summary>The loadout should be sent now.</summary>
    internal bool Due(bool hostReady, bool callInProgress) => _pending && hostReady && !callInProgress;
}
