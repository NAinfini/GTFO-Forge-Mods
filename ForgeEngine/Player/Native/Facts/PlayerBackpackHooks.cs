using HarmonyLib;
using Player;
using SNetwork;

namespace ForgePlayer.Native;

[HarmonyPatch(typeof(PlayerBackpackManager), nameof(PlayerBackpackManager.AddItem))]
internal static class PlayerBackpackAdded
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(pItemData_WithOwner dataWithOwner, out PlayerBackpackChange __state)
        => __state = dataWithOwner.owningPlayer.TryGetPlayer(out var owner)
            ? PlayerBackpackFacts.Installed?.Before(owner, (int)dataWithOwner.data.slot) ?? default : default;
    [HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void Postfix(PlayerBackpackChange __state) => PlayerBackpackFacts.Installed?.After(__state);
}

// The packet overload forwards to this overload. Observe only the actual per-owner removal to avoid duplicates.
[HarmonyPatch(typeof(PlayerBackpackManager), nameof(PlayerBackpackManager.RemoveItem), new[] { typeof(SNet_Player), typeof(pItemData) })]
internal static class PlayerBackpackRemoved
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(SNet_Player fromPlayer, pItemData itemData, out PlayerBackpackChange __state)
        => __state = PlayerBackpackFacts.Installed?.Before(fromPlayer, (int)itemData.slot) ?? default;
    [HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void Postfix(PlayerBackpackChange __state) => PlayerBackpackFacts.Installed?.After(__state);
}

[HarmonyPatch(typeof(PlayerBackpackManager), nameof(PlayerBackpackManager.OnItemStatusChange))]
internal static class PlayerBackpackStatusChanged
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(pInventoryItemStatus data, out PlayerBackpackChange __state)
        => __state = data.sourcePlayer.TryGetPlayer(out var owner)
            ? PlayerBackpackFacts.Installed?.Before(owner, (int)data.slot) ?? default : default;
    [HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void Postfix(PlayerBackpackChange __state) => PlayerBackpackFacts.Installed?.After(__state);
}
