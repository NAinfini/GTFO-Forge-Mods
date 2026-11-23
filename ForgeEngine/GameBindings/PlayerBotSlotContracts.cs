using System.Collections.Generic;
using ForgeEngine.Framework;
using Gear;
using Player;

namespace ForgeEngine.GameBindings;

public sealed record RuntimeBotGear(InventorySlot Slot, GearIDRange Gear);

/// <summary>One pre-drop lobby slot preparation. Each step advances only after the native membership or backpack
/// readback changes.</summary>
public interface IRuntimeBotSlotRequest
{
    bool Ready { get; }
    bool Applied { get; }
    string Readback { get; }
    IRuntimeBotSlotRequest Apply();
}

/// <summary>Begins preparing one of the host lobby's bot slots, supplied by the owner of the player domain. The saved roster of a
/// Rundown package and the explicit development request both go through the same vanilla permission, character and backpack path.</summary>
public delegate IRuntimeBotSlotRequest PlayerBotSlotBegin(int slot, bool enabled, int? character, IReadOnlyList<RuntimeBotGear> loadout);

public static class PlayerBotSlotsService
{
    public const string MissingCode = "player-bot-slots-missing";

    public static PlayerBotSlotBegin Require(IForgeEngine? kernel)
        => kernel != null && kernel.TryService<PlayerBotSlotBegin>(out var slots) ? slots
            : throw new RuntimeContractException(MissingCode, "The Player provider is not installed or has not registered its bot slots.");
}
