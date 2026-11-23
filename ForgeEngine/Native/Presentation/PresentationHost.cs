using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEngine.Presentation.Native;

/// <summary>
/// The host side of the presentation provider (`forge.module.presentation`): registers the shared presentation rows
/// on the kernel's own registration, routes their viewers through the Runtime's player directory, and drives the
/// HUD on Runtime's frame phase; the effects and temporary lights it triggers are drawn by ForgeVisual. The provider is part of this package, so it
/// takes the Runtime's log level and has no cfg, plugin or package of its own. Start and Stop run once per process,
/// from the host plugin's Load and from its startup rollback.
///
/// Its frame work joins the domains' shared frame (<see cref="DomainWorkPhases"/>) after them: the sounds every frame,
/// the HUD once per kernel tick.
/// </summary>
internal static class PresentationHost
{
    internal const string FrameConsumer = "forge.presentation.frame";
    internal const string HudConsumer = "forge.presentation.hud";

    private static RuntimeModuleHandle? _registration;

    private static HudHost? _hud;
    private static ForgeAudioSounds? _sounds;
    private static RuntimeLifecycleSubscription? _worlds;
    private static DomainWorkPhases? _work, _hudWork;

    internal static void Start(RuntimeKernel kernel)
    {
        if (_registration != null) throw new InvalidOperationException("Presentation Start is single-attempt; restart after failure.");
        ArgumentNullException.ThrowIfNull(kernel);

        var players = ForgePlayer.Native.PlayerHost.Directory;
        Action<string> report = PresentationLog.Report;
        var effects = new EffectActions(new VisualEffectPlayer(report), report);
        var sounds = InstallSounds(report);
        var audio = new AudioActions(
            reference => players.AgentOf(reference) is { WasCollected: false } agent ? agent.gameObject : null,
            () => ForgeMap.Native.MapHost.Session?.SoundPlaces, report);
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [PresentationContract.EffectHandler] = effects.Handle,
            [PresentationContract.PointLightHandler] = PointLightActions.Handle,
            [PresentationContract.AudioHandler] = audio.Play,
            [PresentationContract.AudioStopHandler] = audio.Stop
        };
        var hud = new HudHost(kernel, players, report);
        hud.AddHandlers(handlers);
        var resources = new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
        {
            [PresentationContract.AudioResourceKind] = CustomSoundLibrary.Provider()
        };
        var registration = kernel.RegisterBuiltinModule(PresentationContract.Module(handlers, players.SessionsOf, resources));
        _registration = registration;

        _sounds = sounds;
        _hud = hud;
        PresentationLog.Attach(registration);
        hud.Attach();
        _worlds = registration.ObserveLifecycle(OnLifecycle, replayCurrent: false);
        _work = new DomainWorkPhases(FrameConsumer, 3, registration, kernel, sounds.Sync);
        _hudWork = new DomainWorkPhases(HudConsumer, 4, registration, kernel, hud.Frame, perTick: true);
    }

    /// <summary>Installs the ForgeAudio player of the custom sounds the adopted plans and the packages above name
    /// (<see cref="CustomSoundLibrary"/>), which it installs into the library whenever that set changes.</summary>
    private static ForgeAudioSounds InstallSounds(Action<string> report)
    {
        var sounds = new ForgeAudioSounds(report);
        CustomSounds.Live = sounds;
        return sounds;
    }

    private static void ReleaseSounds()
    {
        AudioActions.ResetSilentCues();
        try { LibraryPointSounds.Clear(); }
        catch (Exception error) { PresentationLog.Contained("presentation.point-sounds", error); }
    }

    private static void OnLifecycle(RuntimeLifecycleEvent change)
    {
        if (change.Kind is RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored)
            ReleaseSounds();
        _hud?.OnLifecycle(change);
    }

    /// <summary>Releases whatever Start acquired, in reverse order, and reports every failure together. Safe after a
    /// partial Start and after no Start at all.</summary>
    internal static void Stop()
    {
        var failures = new List<Exception>();
        var registration = _registration;
        Attempt(() => _worlds?.Dispose(), failures);
        Attempt(() => _work?.Dispose(), failures);
        Attempt(() => _hudWork?.Dispose(), failures);

        Attempt(() => _hud?.Dispose(), failures);
        Attempt(LibraryPointSounds.Clear, failures);
        Attempt(() => _sounds?.Dispose(), failures);
        CustomSounds.Live = null;
        CustomSoundLibrary.Clear();
        _sounds = null;
        Attempt(() => registration?.Dispose(), failures);
        if (registration != null) PresentationLog.Detach(registration);
        _worlds = null;
        _work = null;
        _hudWork = null;
        _hud = null;
        _registration = null;
        if (failures.Count != 0) throw new AggregateException("Presentation cleanup failed.", failures);
    }

    private static void Attempt(Action cleanup, List<Exception> failures)
    {
        try { cleanup(); }
        catch (Exception failure) { failures.Add(failure); }
    }
}
