using ForgeCore;

namespace ForgeEngine.GameBindings;

/// <summary>Tells the local player once per pause that Forge is off, and why in one word of three: this game version is
/// not supported (for the whole run), the host runs other content (for this session), or something failed (until the
/// next level), or Forge content was stopped until the game restarts because the game's tables no longer match what Forge put
/// in (contract I-RELOAD), and once when a level could not be built and the game went back to the lobby. One chat line, posted
/// as soon as there is a local player (a pause at start-up happens before one exists). The full reason is in the game log.</summary>
internal static class SuspendNotice
{
    /// <summary>The language keys of the five lines.</summary>
    internal const string Unsupported = "suspended.unsupported", HostMismatch = "suspended.host-mismatch", Failed = "suspended",
        ContentStopped = "suspended.content-stopped", GenerationFailed = "level.generation-failed";

    private static string? _pending;

    internal static void Request(string key) => _pending = key;

    internal static void Pump()
    {
        if (_pending == null || Players.Local == null) return;
        var key = _pending;
        _pending = null;
        ForgeUI.UIKit.PostChatNotice(ForgeUI.UIKit.Mod(Plugin.PluginGuid).Texts.Text(key));
    }
}
