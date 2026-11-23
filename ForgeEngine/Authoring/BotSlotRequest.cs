using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using Gear;
using Player;

namespace ForgeEngine.Authoring;

/// <summary>An author-only bot-slot request on Player's production slot adapter, started by <see cref="AuthorReads.BotSlot"/>.
/// It keeps the exact loaded GearManager key input, then delegates every permission, identity, character, equip and readback
/// operation to the adapter.</summary>
public sealed class BotSlotRequest
{
    private readonly IRuntimeBotSlotRequest _request;
    private BotSlotRequest(IRuntimeBotSlotRequest request) => _request = request;

    internal static BotSlotRequest Begin(IForgeEngine kernel, string json)
    {
        using var document = JsonDocument.Parse(json);
        return Begin(kernel, document.RootElement);
    }

    private static BotSlotRequest Begin(IForgeEngine kernel, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw Refuse("request-invalid");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in input.EnumerateObject())
            if (!keys.Add(field.Name) || field.Name is not ("slot" or "bot" or "character" or "loadout")) throw Refuse("request-invalid");
        if (!input.TryGetProperty("slot", out var slotNode) || slotNode.ValueKind != JsonValueKind.Number || !slotNode.TryGetInt32(out var slot))
            throw Refuse("slot-invalid");
        if (!input.TryGetProperty("bot", out var botNode) || botNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Refuse("request-invalid");
        bool enabled = botNode.GetBoolean();
        int? character = null;
        if (input.TryGetProperty("character", out var characterNode))
        {
            if (!enabled || characterNode.ValueKind != JsonValueKind.Number || !characterNode.TryGetInt32(out var index))
                throw Refuse("character-invalid");
            character = index;
        }
        var loadout = new List<RuntimeBotGear>();
        if (input.TryGetProperty("loadout", out var gearNode))
        {
            if (!enabled || gearNode.ValueKind != JsonValueKind.Object) throw Refuse("loadout-invalid");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in gearNode.EnumerateObject())
            {
                if (!seen.Add(field.Name) || field.Name is not ("GearStandard" or "GearSpecial" or "GearClass" or "GearMelee")
                    || field.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.Value.GetString()))
                    throw Refuse("loadout-invalid");
                var inventorySlot = Enum.Parse<InventorySlot>(field.Name);
                if (!GearManager.TryGetGear(field.Value.GetString()!, out var gear) || gear == null)
                    throw Refuse("gear-missing");
                var item = ItemDataBlock.GetBlock(gear.GetCompID(eGearComponent.BaseItem));
                if (item == null || item.inventorySlot != inventorySlot) throw Refuse("gear-slot-mismatch");
                loadout.Add(new(inventorySlot, gear));
            }
        }
        return new(PlayerBotSlotsService.Require(kernel)(slot, enabled, character, loadout));
    }

    public bool Ready => _request.Ready;
    public bool? ReadyReceipt => Ready ? true : null;
    public bool? AppliedReceipt => Applied ? true : null;
    public bool Applied => _request.Applied;
    public string Readback => _request.Readback;
    public BotSlotRequest Apply() { _request.Apply(); return this; }

    private static InvalidOperationException Refuse(string code) => new("native-bot." + code);
}
