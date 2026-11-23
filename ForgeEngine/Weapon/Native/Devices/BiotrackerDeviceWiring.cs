using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeWeapon.Native;

/// <summary>The native build's wiring of the bio-tracker device writer: registered on Runtime's shared device
/// switchboard after every package has had its registration window, removed when the session ends, and its wield
/// hook installed with the package's own list. Only the Native project compiles this file; the offline adapter compiles the writer itself
/// (`BiotrackerDevice.cs`) and drives it directly, so a session there carries no bio-tracker writer and needs no
/// switchboard.</summary>
internal sealed partial class WeaponNativeSession
{
    private IDisposable? _biotracker;
    private BiotrackerDevice? _biotrackerWriter;
    private ForgePlayer.DeviceSwitchboard? _biotrackerBoard;
    private RuntimeLifecycleSubscription? _biotrackerLifecycle;
    private bool _biotrackerRegistrationAttempted;

    partial void NativeDeviceWriters(IForgeEngine kernel, IPlayerDirectory players)
    {
        var writer = new BiotrackerDevice(players);
        _biotrackerWriter = writer;
        _biotrackerLifecycle = Identity.Registration.ObserveLifecycle(value =>
        {
            if (!_biotrackerRegistrationAttempted && value.Kind == RuntimeLifecycleKind.StartupChanged
                && value.Current.StartupState == RuntimeStartupState.Ready)
            {
                _biotrackerRegistrationAttempted = true;
                var resolved = ForgePlayer.Native.PlayerHost.Session?.Devices;
                if (resolved == null) return;
                try
                {
                    _biotracker = resolved.Register(writer);
                    _biotrackerBoard = resolved;
                }
                catch (Exception error)
                {
                    ReportBiotracker("weapon.biotracker-registration-failed: " + RuntimeDiagnostics.Describe(error));
                    return;
                }
            }
            var active = _biotrackerBoard;
            if (value.Kind is RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored
                    || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                if (active != null) writer.ReleaseAll(target => active.Restore(writer, target));
        }, replayCurrent: false);
    }

    private void ReportBiotracker(string message)
    {
        try { _report(message); }
        catch (Exception error) { LastReporterFailure = error.GetType().Name; }
    }

    partial void NativeDeviceDetach()
    {
        List<Exception>? errors = null;
        void Release(Action action)
        {
            try { action(); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        }
        Release(() => { _biotrackerLifecycle?.Dispose(); _biotrackerLifecycle = null; });
        Release(() =>
        {
            _biotracker?.Dispose();
            _biotracker = null;
            _biotrackerBoard = null;
        });
        if (_biotracker == null)
            Release(() => { _biotrackerWriter?.Dispose(); _biotrackerWriter = null; });
        if (errors != null) throw new AggregateException("Bio tracker detach failed.", errors);
    }
}

internal static partial class WeaponNativeHooks
{
    static partial void NativeDeviceHookTypes(List<Type> types) => types.AddRange(BiotrackerDevice.Types);
}
