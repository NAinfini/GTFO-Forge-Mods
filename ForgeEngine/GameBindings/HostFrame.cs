using System;
using ForgeCore;

namespace ForgeEngine.GameBindings;

/// <summary>The host's per-frame work on ForgeCore's one driver (<see cref="Frame"/>): phase work and mandatory polling every Update,
/// placed-model drawing in LateUpdate, the kernel in FixedUpdate, the host stop at shutdown. Frame callbacks stay for the process, so a
/// failed startup only switches them off.</summary>
internal static class HostFrame
{
    private static bool _on, _registered;

    internal static void Start()
    {
        _on = true;
        if (_registered) return;
        _registered = true;
        Frame.OnUpdate(Plugin.PluginGuid, Update);
        Frame.OnLateUpdate(Plugin.PluginGuid, LateUpdate);
        Frame.OnFixedUpdate(Plugin.PluginGuid, FixedUpdate);
        Frame.OnQuit(Plugin.PluginGuid, Quit);
    }

    internal static void Stop() => _on = false;

    private static void Update()
    {
        if (!_on) return;
        GameRuntimeBridge.Guard(RuntimeWorkPhases.Pump);
        // Mandatory host polling runs every Update, even when phase work fails or exhausts its budget.
        GameRuntimeBridge.Guard(GameRuntimeBridge.PollDiagnostics);
        GameRuntimeBridge.Guard(SuspendNotice.Pump);
        // The workshop's reload requests (contract I-RELOAD): checked once a second, answered in the lobby.
        GameRuntimeBridge.Guard(ContentReloadHandoff.Poll);
        StartPlayingCatchUp.Update();
    }

    // Drawing models is not the rules' work: a failed draw skips that frame's models and never pauses the triggers.
    private static void LateUpdate()
    {
        if (!_on) return;
        try { ForgeVisual.InstancedMeshBatches.Draw(); }
        catch (Exception error) { Log.For(Plugin.PluginGuid).ErrorOnce("engine-model-draw-failed", "Drawing the placed models failed: " + error); }
    }
    private static void FixedUpdate() { if (_on) GameRuntimeBridge.Guard(GameRuntimeBridge.FixedTick); }
    private static void Quit() { if (_on) GameRuntimeBridge.Stop(); }
}
