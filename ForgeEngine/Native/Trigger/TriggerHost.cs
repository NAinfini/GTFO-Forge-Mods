using System;
using System.Collections.Generic;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEngine.Trigger.Native;

/// <summary>
/// The host side of the trigger provider (`forge.module.trigger`): registers the production module with the
/// kernel's own registration and keeps the area-field table in step with the world. The provider is part of this package, so it takes the Runtime's log level and has no cfg,
/// plugin or package of its own. Start and Stop run once per process, from the host plugin's Load and from its
/// startup rollback.
/// </summary>
internal static class TriggerHost
{
    private static RuntimeModuleHandle? _registration;
    private static AreaFieldPump? _areaFields;

    /// <summary>The provider's registration; null before Start and after Stop.</summary>
    internal static RuntimeModuleHandle? Registration => _registration;
    /// <summary>The install's area-field definitions for the current world: the one table the trigger rows, the Player
    /// teleport and the Enemy move_to measure against. Null before Start and after Stop.</summary>
    internal static AreaFieldSession? Fields { get; private set; }


    /// <summary>Registers the provider while the kernel's registration window is open. The pump's lifecycle
    /// subscription replays the current world after registration, so the first load and every later world change
    /// read the field documents through one path.</summary>
    internal static void Start(RuntimeKernel kernel)
    {
        if (_registration != null) throw new InvalidOperationException("Trigger Start is single-attempt; restart after failure.");
        var fields = Fields = new AreaFieldSession(AreaFieldTable.Empty);
        var registration = kernel.RegisterBuiltinModule(ModuleDefinition.Create(fields, LevelSight.Visible));
        _registration = registration;
        TriggerLog.Attach(registration);
        Action<string> refused = message => Report(RuntimeLogLevel.Error, TriggerLogCodes.AreaFieldRefused, message);
        _areaFields = new AreaFieldPump(registration, fields, ContentPackages.ReadResources(Paths.BepInExRootPath, global::ForgeEngine.Plugin.PluginGuid,
            AreaFieldDocument.ResourceKind, AreaFieldCodes.Set, refused), refused);
        if (registration.Logs(RuntimeLogLevel.Info))
            Report(RuntimeLogLevel.Info, TriggerLogCodes.Startup,
                "Forge Engine registered trigger provider " + ModuleDefinition.ProviderId + " with " + fields.Current.Count
                + " area field definition(s).");
    }

    /// <summary>Releases whatever Start acquired, in reverse order, and reports every failure together. Safe after a
    /// partial Start and after no Start at all.</summary>
    internal static void Stop()
    {
        var failures = new List<Exception>();
        var registration = _registration;
        Attempt(() => _areaFields?.Dispose(), failures);

        Attempt(() => registration?.Dispose(), failures);
        if (registration != null) TriggerLog.Detach(registration);
        _areaFields = null;
        Fields = null;
        _registration = null;
        if (failures.Count != 0) throw new AggregateException("Trigger cleanup failed.", failures);
    }

    private static void Attempt(Action cleanup, List<Exception> failures)
    {
        try { cleanup(); }
        catch (Exception failure) { failures.Add(failure); }
    }

    private static void Report(RuntimeLogLevel level, string code, string message)
    {
        if (_registration is not { } registration || !registration.Logs(level)) return;
        var facts = new RuntimeLogFacts { Detail = message };
        registration.Log(level, code, in facts);
    }
}
