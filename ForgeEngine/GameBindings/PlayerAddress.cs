using System;
using System.Globalization;
using SNetwork;

namespace ForgeEngine.GameBindings;

/// <summary>
/// The one spelling of the address a requested step reaches a player at: the game's own player slot,
/// <c>SNet_Player.PlayerSlotIndex</c>, as invariant text. The host's command routing compares a request's endpoint
/// with the address of the machine that receives it, and ForgePlayer's player directory names the address of every
/// other player; both read it here, so the two sides can never spell one player two ways. It is deliberately not the
/// account id (<c>SNet_Player.Lookup</c>): that value is the transport's session identity and is never an address.
/// </summary>
internal static class PlayerAddress
{
    /// <summary>The address of one player, or null when the game cannot name its slot. Null is a refusal: a step
    /// addressed to nobody is not a broadcast.</summary>
    public static string? Of(SNet_Player? player)
    {
        if (player == null || player.Pointer == IntPtr.Zero) return null;
        try
        {
            int slot = player.PlayerSlotIndex();
            return slot < 0 ? null : slot.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception error)
        {
            // Il2Cpp getter on a player the game may have freed; null is the documented "no address" answer. The failure is
            // still written (1st, 10th, 100th ... time), so another kind of error does not turn into a silent "no address".
            NativeReadFailures.Report("PlayerAddress.Of " + error.GetType().Name, error.Message);
            return null;
        }
    }
}

/// <summary>A native read that answered "nobody" because it threw: written once per kind and then at growing counts, as a Warning.</summary>
internal static class NativeReadFailures
{
    private static readonly ForgeCore.OnceReporter Written = new(line => Plugin.PluginLog.LogWarning(line));

    internal static void Report(string site, string detail) => Written.Report(site, Plugin.PluginName + ": native read failed at " + site + ": " + detail);
}
