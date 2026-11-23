using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>Admits each content generation's map packages, then installs only documents owned by successfully
/// published Rundowns. It holds the committed generation's native room and model state, which it takes down when
/// the runtime fails or stops.</summary>
internal sealed class MapContentHost : IDisposable
{
    private readonly IForgeEngine runtime;
    private readonly RuntimeLifecycleSubscription lifecycle;
    private readonly Action<string> report;
    private IDisposable? startup;
    private MapPreparedPackages? current;
    private bool disposed, stopped;
    internal MapContentHost(IForgeEngine runtime, RuntimeModuleHandle registration, Action<string> report)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.report = report ?? throw new ArgumentNullException(nameof(report));
        lifecycle = registration.ObserveLifecycle(value =>
        {
            if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            {
                // Lifecycle observers may release native ownership, but must not unregister SDK providers.
                try { startup?.Dispose(); }
                finally { StopNative(); }
            }
        });
    }
    internal IDisposable RegisterStartup(Action<AdmittedContentSnapshot> activation,
        IReadOnlyList<RundownPack> discovered, Action<Exception> rollback)
    {
        ArgumentNullException.ThrowIfNull(activation); ArgumentNullException.ThrowIfNull(rollback);
        if (disposed || stopped || startup != null) throw new InvalidOperationException("Map startup registration is single-attempt.");
        startup = ContentPackHostStartup.Register(runtime, content =>
        {
            try { activation(content); }
            catch (Exception original)
            {
                CleanupFailure(original, "ForgeMap.MapContentCleanupFailure", StopNative);
                CleanupFailure(original, "ForgeMap.ContentStartupCleanupFailure", () => rollback(original));
                throw;
            }
        }, discovered);
        return startup;
    }
    /// <summary>Validates one generation's packages and prepares everything they need before any native write: model registrations
    /// (not committed), the hook groups their rooms and models need, their generation plans (<paramref name="prepareGeneration"/>
    /// refuses a plan's package alone) and their field plans.</summary>
    internal MapPreparedPackages PreparePackages(MapPackageAdmission packages, AdmittedContentSnapshot content,
        IReadOnlyList<RundownPack> installed, IReadOnlyList<ContentVanillaId> occupied, Action<RundownDiagnostic> rundownReport,
        Action<Generation.GenerationProgram> prepareGeneration)
    {
        if (disposed || stopped) throw new InvalidOperationException("Map content admission requires a live content host.");
        var accepted = packages.Prepare(content, installed, occupied, rundownReport);
        // Require the complete declared hook groups while native Rundown tables are still unchanged; a refusal grants none of its rows.
        RoomRecipeHost.Prepare(accepted.Content.Recipes.Count != 0, accepted.Content.PlacedModels.Any(model => model.Model != null));
        accepted = packages.PrepareGenerations(accepted, prepareGeneration);
        packages.PrepareFields(accepted.Content);
        return accepted;
    }

    /// <summary>Puts one generation's room recipes and placed-model sources in: the previous generation's room templates and
    /// captured parts (Unity objects) are destroyed first, so a reload never keeps a template of a recipe that changed.</summary>
    internal void Activate(MapPreparedContent content)
    {
        RoomRecipeHost.Reset(clearRecipes: true);
        if (content.Recipes.Count != 0) RoomRecipeHost.Activate(content.Recipes);
        if (content.PlacedModels.Any(model => model.Model != null)) RoomRecipeParts.ConfigurePlacedModels(content.PlacedModels);
    }

    /// <summary>The prepared packages whose model registrations the committed generation holds (null: none).</summary>
    internal void Committed(MapPreparedPackages? prepared) => current = prepared;

    private void StopNative()
    {
        stopped = true;
        try { RoomRecipeHost.Reset(clearRecipes: true); }
        finally
        {
            var held = current;
            current = null;
            if (held != null) Dispose(held);
        }
    }

    /// <summary>Disposes the model and carry registrations of <paramref name="prepared"/> (a generation that was replaced, or the
    /// committed one when the runtime stops).</summary>
    internal static void Dispose(MapPreparedPackages prepared)
    {
        var failures = new List<Exception>();
        foreach (var model in prepared.Content.ModelAssemblies)
            try { model.Dispose(); } catch (Exception error) { failures.Add(error); }
        foreach (var item in prepared.Content.CarryAssemblies)
            try { item.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Map model registration cleanup failed.", failures);
    }
    private void CleanupFailure(Exception original, string key, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error)
        {
            try { original.Data[key] = error; }
            catch (Exception dataError) { MapLog.Contained("map-content.cleanup-failure-data", dataError); }
            try { report(key + ": " + RuntimeDiagnostics.Describe(error)); }
            catch (Exception reportError) { MapLog.Contained("map-content.cleanup-failure-report", reportError); }
        }
    }
    public void Dispose()
    {
        if (disposed)
        {
            if (current != null) StopNative();
            return;
        }
        disposed = true;
        try { startup?.Dispose(); }
        finally
        {
            try { lifecycle.Dispose(); }
            finally { StopNative(); }
        }
    }
}
