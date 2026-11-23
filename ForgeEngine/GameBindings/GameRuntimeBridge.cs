using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.Logging;
using ForgeEngine.Network;
using HarmonyLib;
using SNetwork;
using UnityEngine;
using ForgeCore;

namespace ForgeEngine.GameBindings;

internal static partial class GameRuntimeBridge
{
    private const byte MigrationBufferA = 4;
    private const byte MigrationBufferB = 5;
    internal const string GameBuild = "20403457";
    // Not a const: the compiled metadata check reads it as a field. A mismatch harness cannot replace this readonly
    // field once the type is initialized, so it points the game root at another binary instead.
    internal static readonly string GameAssemblySha256 = "C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55";
    /// <summary>Reason code for a host that refused to start; the only reason raised before the kernel exists.</summary>
    internal const string StartupFailed = "startup-failed";
    internal static RuntimeKernel? Kernel { get; private set; }
    private static ContentPlanAdmission? _contentPlans;
    private static IReadOnlyList<PlanCandidate>? _admittedPlans;
    /// <summary>The runtime latched as suspended, so no package may execute anything. Read by packages through
    /// <see cref="Plugin.IsSuspended"/>; a suspended host still publishes its kernel, which is how a package tells
    /// "suspended" apart from "the host never loaded".</summary>
    internal static string? Suspension { get; private set; }
    private static RuntimeLogWriter? _log;
    private static bool _inLevel, _suspended, _wasHost, _blockedUntilLobby, _dispatching, _pendingStop;
    // A client's old kernel may stay paused until it receives the next authoritative world. The migration
    // window itself belongs to this expedition and is closed by cleanup, independently of that retained kernel.
    private static bool _migrationStarted, _migrationCompleted;
    // Network callbacks can arrive inside any kernel lifecycle observer, including startup, migration and
    // checkpoint restoration. Apply their world and state only after the enclosing bridge transaction finishes.
    internal static bool IsDispatching => _dispatching;
    internal static Action? NetworkSafePoint { get; set; }
    private static bool _boundaryFailed;

    /// <summary>One allocation-free, nest-safe boundary shared by dispatch and native lifecycle transactions.
    /// Inner work cannot reopen receive admission or drain a partially updated bridge; only the outer owner does.
    /// The existing ordering remains deliberate: stop first, then authenticated received state, then a deferred
    /// local world transition. No incoming command or state is relabelled while it waits.</summary>
    internal struct DispatchBoundary : IDisposable
    {
        private readonly bool _outermost;
        private bool _completed;
        private bool _drainReceived;
        internal DispatchBoundary(bool alreadyDispatching)
        {
            _outermost = !alreadyDispatching;
            _completed = false;
            _drainReceived = true;
            if (_outermost) _boundaryFailed = false;
            _dispatching = true;
        }

        // Completion is explicit: disposal during exception unwind must not apply received state before Guard
        // has suspended the failed transaction. A failed inner boundary also closes the outer receive drain.
        internal void Complete(bool drainReceived = true)
        {
            _completed = true;
            _drainReceived = drainReceived;
        }

        public void Dispose()
        {
            if (!_completed) _boundaryFailed = true;
            if (!_outermost) return;
            _dispatching = false;
            if (_pendingStop) Stop();
            else if (!_boundaryFailed && _drainReceived)
            {
                NetworkSafePoint?.Invoke();
                if (_pendingWorldChange.Take(out var transition, out var detail)) InvalidateWorld(transition, detail);
            }
            // A contract refusal may already have been handled by Suspend inside this boundary. Finish that
            // explicit local cleanup, but do not consume its received state as part of an unsuccessful restore.
            else if (_boundaryFailed && _suspended && _pendingWorldChange.Take(out var transition, out var detail))
                InvalidateWorld(transition, detail);
        }
    }

    internal static DispatchBoundary BeginDispatchBoundary() => new(_dispatching);

    private static bool _resumeAdmissionAfterGeneration;
    private static long _suspensionWorldEpoch;
    /// <summary>ForgeCore dropped its world since the kernel last followed one: the next world is a session's first.</summary>
    private static bool _enteredSession;
    private static long _epoch, _tick;
    private static byte? _recalledMigrationBuffer;
    /// <summary>What the last checkpoint of this expedition carried: every variable value and `g-once` latch the
    /// world held when the game saved. A reload puts these back instead of suspending the runtime, so the same
    /// expedition keeps running with the behaviour re-armed. Null until this expedition saved one.</summary>
    private static string? _checkpoint;
    /// <summary>A world transition that arrived while a command was dispatching. The kernel moves at its safe point,
    /// so the transition is remembered here and replayed once, at the epoch the kernel really reached.</summary>
    private static PendingWorldChange _pendingWorldChange;
    internal static bool CanExecute => GameplayReady && _wasHost && NativeAuthority;

    /// <summary>Native writes for a host or the kernel's currently admitted holder callback. The owner scope
    /// is established only after the existing plan, receiver and input validation; it never grants event
    /// publication or ordinary client execution, and every shared lifecycle gate remains in force.</summary>
    internal static bool CanExecuteOwner => GameplayReady && (Kernel!.OwnerInvocation ? NativeOwnerAuthority : _wasHost && NativeAuthority);

    private static bool GameplayReady => Kernel is { } kernel
        && kernel.Lifecycle.StartupState == RuntimeStartupState.Ready
        && NativeContentRefusal.Stopped == null && _inLevel && !_suspended
        && !_pendingWorldChange.Pending && !_pendingStop && !kernel.HostMigrationPaused;

    /// <summary>The native role and game state are shared by both gates and read again only when the game says they
    /// may have changed: a session event (<see cref="AuthorityChanged"/>, from the network binding), a game state change,
    /// and the bridge's own lifecycle and migration hooks. Migration is the one read each fixed tick, because the
    /// bridge's migration detection is that read. The managed gates above are always current. A role change that has not
    /// completed the bridge's migration also closes owner admission.</summary>
    private static bool _authoritySampled, _nativeHost, _nativeInLevel, _nativeMigrating;
    private static void SampleAuthority()
    {
        if (_authoritySampled) return;
        _nativeHost = Session.IsHost;
        _nativeInLevel = GameStateManager.CurrentStateName == eGameStateName.InLevel;
        _nativeMigrating = ReadMigrating();
        _authoritySampled = true;
    }
    private static bool ReadMigrating() => SNet.MasterManagement != null && SNet.MasterManagement.IsMigrating;
    private static bool NativeAuthority
    {
        get { SampleAuthority(); return _nativeHost && _nativeInLevel && !_nativeMigrating; }
    }
    private static bool NativeOwnerAuthority
    {
        get { SampleAuthority(); return _nativeInLevel && !_nativeMigrating && _nativeHost == _wasHost; }
    }
    private static void ResampleAuthority() => _authoritySampled = false;
    /// <summary>The session's host or membership may have changed; the next gate read samples the role again.</summary>
    internal static void AuthorityChanged() => _authoritySampled = false;
    private static void GameStateChanged(eGameStateName previous, eGameStateName next) => _authoritySampled = false;

    /// <summary>The one discovery of the installed packages, run from Plugin.Load once the log level is raised.</summary>
    internal static void DiscoverContent() => ContentPackages.Discover(Paths.BepInExRootPath);

    /// <summary>Checks the native GameAssembly identity and builds the kernel plus its writer. A hash mismatch is not a
    /// startup exception: the kernel is latched as suspended, one runtime.suspended record is written, and dependent
    /// packages keep loading against a runtime that never executes anything.</summary>
    internal static void Initialize(RuntimeLogLevel logLevel)
    {
        if (Kernel != null) throw new InvalidOperationException("Forge Engine is already initialized.");
        ResampleAuthority();
        _pendingStop = false;
        _resumeAdmissionAfterGeneration = false;
        _suspensionWorldEpoch = 0;
        _enteredSession = false;
        _inLevel = _suspended = _wasHost = _blockedUntilLobby = _dispatching = false; _epoch = 1; _tick = 0; _builtWorld = 0;
        _pendingWorldChange = default;
        _checkpoint = null;
        _planIndex = null; _levelRead = null; _levelAdopted = false; _adoptedLevel = null;
        _gateParts = -1;
        ResetMigrationState();
        Suspension = null;
        _releaseChecked = false;
        // The content entries are the first participant of every generation; the domains register theirs at their Start.
        ResetGenerations();
        // The writer carries the Runtime provider id itself: a `log.dropped` record written before the first accepted record
        // has no level table to read it from.
        _log = new RuntimeLogWriter(Path.Combine(Paths.BepInExRootPath, RuntimeLogWriter.DirectoryName), "forge.runtime", Plugin.PluginLog, RuntimeLogLimits.Default);
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", Plugin.PluginVersion, RuntimeKernel.ApiVersion, GameBuild), new RuntimeLimits(), _log, logLevel);
        Kernel = kernel;
        // The one discovery of the installed packages (I-RESOLVE): every base package that loads after ForgeEngine is handed
        // the documents it reads from it through ContentPackages and never walks plugins/ itself.
        ContentPackages.Record = (code, values) =>
        {
            if (kernel.RuntimeLogs(RuntimeLogLevel.Info)) kernel.LogRuntime(RuntimeLogLevel.Info, code, new RuntimeLogFacts { Values = values });
        };
        ContentPackages.Refused = (code, path, reason) => ContentPackStartup.Rejected(kernel, code, path, null, path, reason);
        // The discovery itself runs from Plugin.Load once Authoring has elevated the log level, so `content.discovered` is written.
        NativeHookRegistration.BindWorld(() => Kernel is { StartupState: not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped) } current
                && Suspension == null ? current.WorldEpoch : -1,
            ReportNativeObserverFailure, LogNativeHookDeclared,
            static (row, boundary) => NativeHookRegistration.LogSharedDiagnostic(Kernel, row, boundary));
        DomainStateChannel.BindNativeState(kernel);
        // The Runtime's own providers carry the Runtime cfg level; only packages with their own plugin pass one.
        kernel.RegisterBuiltinModule(CombatContracts.Module());
        kernel.RegisterBuiltinModule(ControlContracts.Module());
        // The variable, level-object and message rows are the kernel's own too: a plan pins them like any other
        // binding, and no package registers them on the runtime's behalf.
        kernel.RegisterBuiltinModule(VariableContracts.Module());
        // Every Trigger shape the runtime publishes, before any domain package registers the binding that
        // publishes it: a binding whose capability is not registered yet is refused as `missing-capability`.
        kernel.RegisterBuiltinModule(TriggerContracts.Module());
        // The two rows the kernel answers for itself: the ending fact it publishes and the dispel step it walks.
        // They are registered after the trigger rows because their capabilities are their own provider's, and
        // before any domain package, whose plans may mount on the ending binding.
        kernel.RegisterBuiltinModule(StatusContracts.Module());
        // The rows that read any entity a wire already carries: the kernel answers them for every provider's own
        // entity kind, so no domain package declares them and this is where they are registered.
        kernel.RegisterBuiltinModule(ObservationContracts.Module());
        // The one binding the host observes itself: a player leaving the session. Its row is the trigger contract's,
        // registered above; this registration carries only the binding, on the host's own provider id.
        var session = kernel.RegisterBuiltinModule(SessionContract.Module());
        // Source declarations only satisfy explicit content prerequisites; no source graph is mounted here.
        BehaviorContentRegistration.Register(session);
        var mismatch = HashMismatch();
        if (mismatch != null) { SuspendStartup(kernel, mismatch); return; }
        kernel.BeginWorld(_epoch); kernel.SetFactionRelations(RuntimeFactionRelations.Gameplay());
        // No session exists yet; the binding only subscribes to SNet here, and a session's own host is built when
        // SNet reports one, so the plan snapshot it compares is the one discovered after this point.
        NetworkBinding.Start();
        GTFO.API.EventAPI.OnGameStateChanged += GameStateChanged;
        // Departures are published by the host alone, and only while the execute gate is open: a client, a lobby or
        // a migrating session publishes nothing.
        SessionBinding.Start(new SessionFacts(kernel, session, () => CanExecute,
            detail => Plugin.PluginLog.LogWarning(Plugin.PluginName + ": " + detail)));
    }

    /// <summary>The actual GameAssembly hash when it differs from the supported binding, null when it matches.</summary>
    private static string? HashMismatch()
    {
        try
        {
            // Read once per changed file across launches (ForgeCore FileHashes); still compared with the binding every launch.
            var actual = ForgeCore.FileHashes.Sha256Hex(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
            return string.Equals(actual, VerifiedGameImage.Sha256, StringComparison.Ordinal) ? null : actual;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return "unreadable (" + error.Message + ")"; }
    }

    /// <summary>No StartRuntime and no world: the binding does not support this binary, so nothing derived from it may run.</summary>
    private static void SuspendStartup(RuntimeKernel kernel, string actual)
        => SuspendStartup(kernel, StartupFailed, actual.StartsWith("unreadable", StringComparison.Ordinal)
            ? "GameAssembly could not be read: " + actual
            : "GameAssembly hash " + actual + " does not match the supported binding " + GameAssemblySha256 + " (game build " + GameBuild + ").");

    private static void SuspendStartup(RuntimeKernel kernel, string code, string reason)
    {
        Suspension = code;
        _suspended = true;
        SuspendNotice.Request(SuspendNotice.Unsupported);
        // Always written: with this pause no package registers, and the cfg level below may hide the record.
        Plugin.PluginLog.LogError(Plugin.PluginName + " suspended (" + code + "): " + reason);
        kernel.SetFactAdmissionSuspended(true);
        // The one record this host writes. It is gated exactly like every other suspension, so the cfg level decides.
        kernel.LogSuspended(code, reason);
    }

    /// <summary>Plugins are all discovered by the first fixed tick, before registration closes: the installed Forge packages must be the
    /// release this host was built with. A mismatch suspends the host before it starts anything.</summary>
    private static void CheckRelease(RuntimeKernel kernel)
    {
        if (_releaseChecked || InstalledPluginVersion is not { } installed) return;
        _releaseChecked = true;
        var release = ContentDependencyLocks.ReleasePlugins;
        if (release == null) { SuspendStartup(kernel, ReleaseCoherence.Code, "The embedded release identity is unreadable."); return; }
        var mismatch = ReleaseCoherence.Mismatch(release, installed);
        if (mismatch == null) return;
        SuspendStartup(kernel, ReleaseCoherence.Code, mismatch);
    }
    private static bool _releaseChecked;
    /// <summary>The installed version of a plugin by GUID (null when it is not installed); the plugin entry supplies the BepInEx chainloader's
    /// table, so hosts without one (offline harnesses) skip the release check.</summary>
    internal static Func<string, string?>? InstalledPluginVersion { get; set; }

    internal static void ReportNativeObserverFailure(string consumer, Exception error)
        => Kernel?.ReportNativeObserverFailure(consumer, error);

    /// <summary>`native-hook.declared`: one writer's declaration batch was resolved (writer, callback classes declared,
    /// distinct targets they named, callback classes refused).</summary>
    private static void LogNativeHookDeclared(NativeHookDeclared declared)
    {
        var kernel = Kernel;
        if (kernel == null || !kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.NativeHookDeclared, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("writer", declared.Writer), RuntimeLogValue.Of("declared", declared.Callbacks),
                RuntimeLogValue.Of("targets", declared.Targets), RuntimeLogValue.Of("refused", declared.Refused) }
        });
    }

    internal static void PollDiagnostics()
    {
        using var frameCost = HookCounters.Measure(HookSection.RuntimeDiagnosticsPoll);
        if (FirstFrames.Active) SampleFirstFrameTiming(Kernel, System.Diagnostics.Stopwatch.GetTimestamp());
        Kernel?.PollDiagnosticSummaries();
    }

    internal static void FixedTick()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.RuntimeFixedFrame);
        var kernel = Kernel;
        if (kernel == null) return;
        _nativeMigrating = ReadMigrating();
        if (kernel.StartupState == RuntimeStartupState.Registering && Suspension == null) CheckRelease(kernel);
        // A suspended host never starts: it exports no manifest and loads no plan from a binary the binding does not support.
        if (Suspension != null) return;
        if (NativeContentRefusal.Stopped is { } audit && kernel.StartupState == RuntimeStartupState.Ready)
        {
            // A late failed ownership audit closes CanExecute immediately above. At this safe boundary use
            // the existing suspension/world cleanup and terminal lifecycle; a lobby transition cannot resume it.
            // The player is told in game that Forge content is off until the game restarts (contract I-RELOAD).
            Suspend("content-native-state-unknown", audit.ToString(), notice: SuspendNotice.ContentStopped);
            kernel.StopRuntime();
            return;
        }
        // Native content must see initialized tables. Keep the one startup attempt pending until then.
        if (kernel.StartupState == RuntimeStartupState.Registering && GameData.GameDataInit.IsLoaded)
        {
            using var timing = new ForgeCore.StartupTiming(Plugin.PluginGuid,
                message => { if (ForgeCore.Log.Developer) Plugin.PluginLog.LogInfo(message); }, "content-plan-startup");
            using var boundary = BeginDispatchBoundary();
            kernel.StartRuntime(() =>
            {
                if (NativeContentRefusal.Stopped is { } stopped)
                    throw new InvalidOperationException("Native tables failed their ownership audit before startup.", stopped);
                // The empty generation is committed into generation 1 (contract I-RELOAD), the path a reload takes: the
                // content entries install first, the domains' participants (Map's host callback publishes the Rundowns)
                // follow, and the plan index is adopted last. Neither plan adoption nor Ready can precede that commit or
                // bypass its native rollback failure gate.
                timing.Mark("content-generation");
                CommitFirstGeneration(kernel);
            });
            boundary.Complete();
            timing.Complete(kernel.StartupState == RuntimeStartupState.Ready ? "loaded" : "not-ready");
        }
        // Startup's safe point may have honored a reentrant stop or replaced this kernel. Never continue the
        // frame through the captured instance after its boundary has retired it.
        if (!ReferenceEquals(kernel, Kernel) || Suspension != null || kernel.StartupState != RuntimeStartupState.Ready) return;
        if (kernel.RuntimeLogs(RuntimeLogLevel.Trace)) TraceGate(kernel);
        // The chosen level's plans, once read off the game thread, are adopted here between levels only.
        if (!_inLevel && _levelRead is { IsCompleted: true })
        {
            AdoptLevelPlans(kernel, wait: false);
            if (!ReferenceEquals(kernel, Kernel) || Suspension != null) return;
        }
        // The level's content snapshot is hashed on a worker thread; the plans it belongs to are announced here, in the
        // lobby as well as in a level, the frame it lands.
        if (NetworkBinding.ContentPending)
        {
            Guard(NetworkBinding.PumpContent);
            if (!ReferenceEquals(kernel, Kernel) || Suspension != null) return;
        }
        if (!_inLevel || _suspended) return;
        if (_nativeMigrating)
        {
            if (!_migrationCompleted) { _migrationStarted = true; kernel.BeginHostMigration(); }
            return;
        }
        if ((_migrationAwaitingRecall || _migrationAwaitingWorld) && ++_migrationRecallWait > MigrationRecallWaitTicks)
        {
            _migrationAwaitingRecall = _migrationAwaitingWorld = false;
            CompleteHostMigration(wait: false); // No recall or no world came: refused by name as a migration without its state.
            return;
        }
        if (_migrationCompleted) _migrationCompleted = false;
        // Session teardown can change IsMaster before LevelAPI's cleanup postfix. A role change is not a
        // migration completion: only vanilla's completion hook may adopt a recalled world. Also stop advancing
        // outside native gameplay even while the managed level lifetime has not been cleaned up yet.
        if (kernel.HostMigrationPaused || !NativeOwnerAuthority) return;
        using (var boundary = BeginDispatchBoundary())
        {
            var tick = kernel.Advance(checked(++_tick), _nativeHost, receipts: false);
            using var network = HookCounters.Measure(HookSection.TickNetwork);
            // The presentation steps this tick decided go out to the players their provider named. Sending them
            // here, inside the dispatch guard, is what keeps one advance's intents with the epoch they belong to:
            // a world that ends while they are on the wire ends with them.
            NetworkBinding.Present(tick.Presentations);
            // The owner steps follow the same rule and the same guard: each is addressed to the one session that
            // holds what it changes, and a world that ends while one is on the wire ends with it.
            NetworkBinding.Own(tick.OwnerCommands);
            // The host's variable state follows the same rule: one body per advance, sent after it, so the values a
            // client applies are the ones the advance it just executed produced.
            NetworkBinding.SyncVariables();
            boundary.Complete();
        }
    }

    /// <summary>GTFO-API <c>LevelAPI.OnBuildStart</c>: level generation began, so the previous world is dropped and a
    /// suspension that waited for a fresh expedition is released. This is the only release point: the host binds the
    /// points a level crosses rather than game state names, and a new build is a stronger guarantee than a state name,
    /// so a session that never starts another expedition stays suspended instead of resuming inside the old one. The
    /// world's own start is also where the tick rate is read and recorded, because the ticks this world converts by
    /// begin here (contract §3.2, 时间单位).</summary>
    internal static void BeginGeneration()
    {
        _builtWorld = 0;
        ResampleAuthority();
        // A startup suspension is process-wide: no lifecycle event may begin a world on a kernel that never started.
        var kernel = Kernel;
        if (kernel == null || Suspension != null) return;
        ResetFirstFrameTiming();
        var measure = kernel.RuntimeLogs(RuntimeLogLevel.Info);
        var startedAt = measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        // A new expedition starts: the previous one's checkpoint names a world that is gone.
        _checkpoint = null;
        ResetMigrationState();
        _resumeAdmissionAfterGeneration |= _suspended && !_blockedUntilLobby;
        InvalidateWorld(WorldTransition.NewGeneration, "level generation started");
        if (!ReferenceEquals(kernel, Kernel)) return;
        // A move still waiting for its safe point is this build's own world, which begins as every world does.
        if (!_pendingWorldChange.Pending) _builtWorld = kernel.WorldEpoch;
        // The level's plans are in place before the game builds into it: a read still running is waited for here.
        AdoptLevelPlans(kernel, wait: true);
        if (!ReferenceEquals(kernel, Kernel) || Suspension != null) return;
        // A client may receive the authoritative epoch before or after its native build callback. Both
        // facts are required, and a repeated callback must not erase a still-pending recovery.
        if (!Session.IsHost && !_dispatching && kernel.WorldEpoch > _suspensionWorldEpoch) CompleteGenerationAdmission();
        _inLevel = false;
        NativeModelRuntime.BeginLevel(ModelReport);
        ModelDeviations.Reset();
        // A tick is one host FixedUpdate (contract §3.2), and this is the world whose ticks begin here: read the step
        // the engine runs them with and record the rate the whole session converts by. This side is the only one that
        // may read Unity, so the read and the reciprocal happen here and the kernel writes the one line; reading at the
        // world's start rather than in the frame loop also keeps it out of the per-tick path.
        ReportTickRate(kernel);
        if (measure) kernel.LogRuntime(RuntimeLogLevel.Info, "level.load-work", new RuntimeLogFacts
        {
            Detail = "Runtime world invalidation and generation admission; excludes native factory work.",
            Values = new[] { RuntimeLogValue.Of("work", "runtime-world-begin"), RuntimeLogValue.Of("phase", "generation"),
                RuntimeLogValue.Of("elapsedMs", (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency) }
        });
    }

    /// <summary>GTFO-API <c>LevelAPI.OnEnterLevel</c>: the local player can move, the point at which the game state
    /// reaches InLevel. The host baseline is taken once per world, so a later promotion still reads as a migration.</summary>
    internal static void EnterLevel()
    {
        ResampleAuthority();
        if (Kernel == null || Suspension != null) return;
        if (!_inLevel) _wasHost = Session.IsHost;
        if (!_inLevel)
        {
            BeginFirstFrameTiming(Kernel, System.Diagnostics.Stopwatch.GetTimestamp());
            NativeHookRegistration.LogSharedDiagnostics(Kernel, "level-entered");
        }
        _inLevel = true;
        NetworkBinding.RefreshSession();
    }

    /// <summary>GTFO-API <c>LevelAPI.OnLevelCleanup</c>: the level is gone, which replaces the previous
    /// <c>GameStateManager.OnLevelCleanup</c> and <c>OnResetSession</c> prefixes.</summary>
    internal static void LeaveLevel()
    {
        ResampleAuthority();
        _inLevel = false;
        _builtWorld = 0;
        ResetFirstFrameTiming();
        NativeModelRuntime.EndLevel(ModelReport);
        if (Kernel == null || Suspension != null) return;
        _resumeAdmissionAfterGeneration = false;
        Kernel.FlushDiagnosticSummaries();
        // Cleanup satisfies the lobby wait; only the next build releases the actual suspension.
        _blockedUntilLobby = false;
        ResetMigrationState();
        InvalidateWorld(WorldTransition.LevelCleanup, "level cleanup");
    }

    /// <summary>
    /// Postfix of vanilla <c>SNet_Capture.CaptureGameState(Migration_A/B)</c>. The kernel image is taken inside the same
    /// native call boundary and kept in the engine's migration table, where every player holds it; the next capture sends
    /// only the parts that changed.
    /// </summary>
    internal static void CaptureHostMigration(byte buffer)
    {
        var kernel = Kernel;
        if (kernel == null || Suspension != null || !_inLevel || !Session.IsHost
            || buffer is not (MigrationBufferA or MigrationBufferB)) return;
        using var boundary = BeginDispatchBoundary();
        try
        {
            NetworkBinding.PublishMigration(buffer, kernel.CaptureHostMigration(), _checkpoint);
            boundary.Complete();
        }
        catch (RuntimeContractException error)
        {
            // A capture is taken every period whether or not a migration ever happens, and it can meet a state with no
            // portable image (a live wave handle, a pending holder result). That is not a fault of the world: this half
            // is dropped, the reason is logged, and only a real migration that recalls this half is refused, as
            // `host-migration-state-missing`. Suspending here would stop every package until the lobby for nothing.
            NetworkBinding.DropMigration(buffer);
            if (ForgeCore.Log.Developer) Plugin.PluginLog.LogInfo(Plugin.PluginName + ": migration capture of buffer " + buffer + " refused (" + error.Code + "): " + error.Message);
            kernel.LogCallbackFailed("host-migration-capture-refused " + error.Code, error);
            boundary.Complete();
        }
    }

    /// <summary>Postfix of vanilla's two public migration starts. Reading the native flag here closes the interval
    /// before the next Forge FixedUpdate: events raised after SNet entered migration are buffered rather than being
    /// rejected on a client or admitted to the predecessor's ordinary queue.</summary>
    internal static void BeginNativeHostMigration()
    {
        ResampleAuthority();
        if (Kernel is { } kernel && Suspension == null && _inLevel
            && SNet.MasterManagement != null && SNet.MasterManagement.IsMigrating)
        {
            _migrationStarted = true;
            kernel.BeginHostMigration();
        }
    }

    /// <summary>Postfix of vanilla <c>SNet_Capture.RecallBuffer</c>: remembers which half of the double buffer vanilla
    /// actually restored, and completes a migration whose new master was found before the new master's recall command arrived.</summary>
    internal static void RecallHostMigration(byte buffer)
    {
        if (buffer is not (MigrationBufferA or MigrationBufferB)) return;
        _recalledMigrationBuffer = buffer;
        if (!_migrationAwaitingRecall) return;
        _migrationAwaitingRecall = false;
        CompleteHostMigrationAndPublish();
    }

    /// <summary>How many fixed ticks a found master may take to send its recall command before the migration counts as having no recalled
    /// state (about 30 s at the game's step). Vanilla's own failure path, ForceExitToLobby, can come first.</summary>
    private const int MigrationRecallWaitTicks = 1500;
    private static bool _migrationAwaitingRecall, _migrationAwaitingWorld;
    private static int _migrationRecallWait;

    /// <summary>
    /// Once vanilla has found the host and recalled its world (<c>SNet_SyncManager.OnFoundNewMasterDuringMigration</c>, ForgeCore's
    /// bracket, then the new master's <c>RecallBuffer</c>) and the new host's world number has reached this machine, Forge
    /// restores the image it holds of the recalled buffer under that world. Every machine restores the same image, so the
    /// behaviors that were running go on; no gameplay action is replayed here. A machine whose world has not arrived waits for it.
    /// </summary>
    internal static void CompleteHostMigration(bool wait = true)
    {
        ResampleAuthority();
        var kernel = Kernel;
        // Completion belongs to a migration that the native start hook or IsMigrating tick already opened.
        // An unrelated completion notification must not turn ordinary session teardown into a migration.
        if (kernel == null || Suspension != null || !_inLevel || !_migrationStarted || _migrationCompleted || !kernel.HostMigrationPaused) return;
        using var boundary = BeginDispatchBoundary();
        var newEpoch = NetworkBinding.WorldBase;
        if (newEpoch <= kernel.WorldEpoch && wait)
        {
            _migrationAwaitingWorld = true;
            _migrationRecallWait = 0;
            boundary.Complete();
            return;
        }
        if (newEpoch <= kernel.WorldEpoch || _recalledMigrationBuffer is not { } buffer
            || !NetworkBinding.TryReadMigration(buffer, out var runtime, out var checkpoint))
        {
            Suspend("host-migration-state-missing",
                "Vanilla selected a replacement host, but the matching Forge migration image or the new host's world is unavailable.", true);
            return;
        }
        var migratedCheckpoint = RuntimeKernel.RebaseMigrationCheckpoint(checkpoint, kernel.WorldEpoch, newEpoch);
        try { _tick = kernel.RestoreHostMigration(runtime, newEpoch, Session.IsHost); }
        catch (RuntimeContractException error)
        {
            Suspend(error.Code, error.Message, true);
            return;
        }
        _epoch = newEpoch;
        _checkpoint = migratedCheckpoint;
        _wasHost = Session.IsHost;
        _migrationCompleted = true;
        _recalledMigrationBuffer = null;
        NetworkBinding.LogPoints?.Epoch("moved", newEpoch, "host-migration");
        NetworkBinding.CompleteHostMigration();
        boundary.Complete();
    }

    /// <summary>The native completion postfix: completes the migration, then tells upstream observers once, and only
    /// when this call is the one that completed it.</summary>
    internal static void CompleteHostMigrationAndPublish()
    {
        // In a real migration vanilla finds the master first; the recall reaches this machine as the new master's buffer command.
        if (_recalledMigrationBuffer == null && _migrationStarted && !_migrationCompleted && NetworkBinding.HoldsMigration
            && Kernel is { HostMigrationPaused: true } && Suspension == null)
        {
            _migrationAwaitingRecall = true;
            _migrationRecallWait = 0;
            return;
        }
        var alreadyCompleted = _migrationCompleted;
        CompleteHostMigration();
        if (_migrationCompleted && !alreadyCompleted) RuntimeUpstreamEvents.Publish(RuntimeUpstreamKind.HostMigrated);
    }

    /// <summary>Prefix of vanilla's migration timeout fallback. The world is explicitly ended and diagnosed before
    /// vanilla returns every player to the lobby.</summary>
    internal static void HostMigrationFailed()
    {
        ResampleAuthority();
        var kernel = Kernel;
        if (kernel == null || (!kernel.HostMigrationPaused
            && (SNet.MasterManagement == null || !SNet.MasterManagement.IsMigrating))) return;
        _inLevel = false;
        _migrationCompleted = false;
        Suspend("host-migration-failed", "Vanilla host migration timed out and called ForceExitToLobby.", true);
        ResetMigrationState();
    }

    private static void ResetMigrationState()
    {
        _migrationStarted = _migrationCompleted = _migrationAwaitingRecall = _migrationAwaitingWorld = false;
        _recalledMigrationBuffer = null;
    }

    /// <summary>ForgeCore's world moved on this machine: the host minted it or announced it, or this client moved ahead of the
    /// announcement at its own game event. A checkpoint generation keeps the kernel's world. A new host inside a level is a
    /// migration: the kernel pauses and restores the image it holds once vanilla recalled its buffer. Every other number is
    /// the world the kernel moves to.</summary>
    internal static void NetworkWorldChanged(NetWorld world)
    {
        var kernel = Kernel;
        if (kernel == null || Suspension != null || world.Number == 0) return;
        // A checkpoint generation keeps the kernel's world. A client that built its level before it heard the host's number is
        // not in that world yet: it follows as it does when the host's last move was the build (see FollowWorld).
        var built = !Session.IsHost && _builtWorld == kernel.WorldEpoch && kernel.WorldEpoch / NetworkProtocol.WorldSpan != world.Number;
        if (world.Reason == NetWorldReason.Checkpoint && !built) return;
        if (world.Reason == NetWorldReason.Host && _inLevel)
        {
            if (!_migrationCompleted) { _migrationStarted = true; kernel.BeginHostMigration(); }
            if (_migrationAwaitingWorld)
            {
                _migrationAwaitingWorld = false;
                CompleteHostMigrationAndPublish();
            }
            return;
        }
        // Inside a session the kernel's epoch only rises. A lower number there is the host's answer to a number this
        // client moved to ahead of it and the host never minted: this level cannot be followed, the player is told
        // so, and Forge stays off for it until the next level begins. The first world of a session is not that case:
        // InvalidateWorld starts the count over at it.
        if (_inLevel && !_enteredSession && !Session.IsHost && kernel.WorldEpoch / NetworkProtocol.WorldSpan > world.Number)
        {
            Suspend(NetworkCodes.StaleWorld, "The host's world " + world.Number + " is behind one this game already left; Forge stays off until the next level.");
            return;
        }
        InvalidateWorld(world.Reason is NetWorldReason.Build or NetWorldReason.Checkpoint ? WorldTransition.NewGeneration : WorldTransition.LevelCleanup,
            "the session's world moved");
    }

    /// <summary>ForgeCore dropped its world: this machine left its session or is entering one, and the first world the
    /// next host announces may be numbered below the ones this process already left.</summary>
    internal static void NetworkSessionEnded() => _enteredSession = true;

    /// <summary>The host stepped its kernel inside the current world number (it suspended itself); this client follows.</summary>
    internal static void NetworkStep(long epoch)
    {
        var kernel = Kernel;
        if (kernel == null || Suspension != null || Session.IsHost || epoch <= kernel.WorldEpoch
            || epoch / NetworkProtocol.WorldSpan != kernel.WorldEpoch / NetworkProtocol.WorldSpan) return;
        MoveWorld(kernel, epoch, WorldTransition.HostSuspend);
    }

    /// <summary>
    /// The kernel moves to a new world, once, at the point where no dispatch is running. The kernel's epoch is ForgeCore's world
    /// number times <see cref="NetworkProtocol.WorldSpan"/>, so every machine of a session names a world alike: a game event
    /// (a build, a cleanup) and ForgeCore's announcement of the number it minted for that event both arrive here, and whichever
    /// comes second finds the kernel already there. Two moves have no new number: a machine in no session, and a host that
    /// suspends itself, which steps one epoch inside its number and tells its clients. A transition that arrives mid-dispatch
    /// is remembered instead, because the kernel must not move under a running step.
    /// </summary>
    internal static void InvalidateWorld(WorldTransition transition, string detail)
    {
        ResampleAuthority();
        if (Kernel == null) return;
        if (_dispatching) { _pendingWorldChange.Remember(transition, detail); return; }
        if (Kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        var kernel = Kernel;
        var world = NetworkBinding.WorldBase;
        // The first world of a session is the only one that may start the count over; every later one only rises.
        var enteredSession = _enteredSession && world != 0;
        if (world != 0) _enteredSession = false;
        if (world > kernel.WorldEpoch) { if (!FollowWorld(kernel, world, transition)) MoveWorld(kernel, world, transition); }
        else if (world == 0 || transition == WorldTransition.HostSuspend && Session.IsHost)
        {
            MoveWorld(kernel, checked(kernel.WorldEpoch + 1), transition);
            if (world != 0 && ReferenceEquals(kernel, Kernel)) NetworkBinding.AnnounceStep(kernel.WorldEpoch);
        }
        // A client that entered this session after its process left a later world than the host's (it played another
        // session before, or stepped on its own outside one) starts its count over at the host's number, so it names
        // the host's world as the host does; a step the host took inside that number follows as its `step`.
        // A host migration is never that: the kernel it paused is restored under the new host's number, which is higher.
        else if (enteredSession && world < kernel.WorldEpoch && !Session.IsHost && !_migrationStarted && !kernel.HostMigrationPaused)
        {
            _suspensionWorldEpoch = 0;
            NetworkBinding.RestartWorldCount();
            NetworkBinding.LogPoints?.Epoch("restarted", world, "session-restart");
            kernel.RestartWorldCount();
            if (!FollowWorld(kernel, world, transition)) MoveWorld(kernel, world, transition);
        }
        // The kernel is already in this world; a suspension that waited for this build ends with it.
        else if (transition == WorldTransition.NewGeneration && kernel.WorldEpoch > _suspensionWorldEpoch) CompleteGenerationAdmission();
    }

    /// <summary>The world this client's own build callback began the level under, 0 outside one. While the kernel is still in
    /// it, a build the host announces is that same level.</summary>
    private static long _builtWorld;

    /// <summary>
    /// A client can build its level before it hears which world its host counts that level under: its session attached after
    /// the build began, or the host's number is not the one this client moved to ahead of it. The level, its rooms and
    /// everything placed in it stay, so the kernel and every provider take the host's number the way they do after a host
    /// migration instead of beginning a world, which would clear what only a build can place again. False when this is not that
    /// case: the caller then begins the world as any other. When the kernel holds something it cannot carry over, it begins
    /// the new world itself, as one whose level stays (<see cref="KeepsBuiltLevel"/>), and returns true.
    /// </summary>
    private static bool FollowWorld(RuntimeKernel kernel, long epoch, WorldTransition transition)
    {
        if (transition != WorldTransition.NewGeneration || Session.IsHost || _builtWorld == 0 || _builtWorld != kernel.WorldEpoch) return false;
        var refused = false;
        using (var boundary = BeginDispatchBoundary())
        {
            // A world that never advanced has no tick yet; its first one is counted as a new world's is.
            try { _tick = Math.Max(kernel.FollowWorld(epoch), 0); }
            // Whatever refused, the kernel rolled back to the world it was in; it begins the new one below.
            catch (Exception error)
            {
                kernel.LogCallbackFailed("world-follow-refused " + ((error as RuntimeContractException)?.Code ?? error.GetType().Name), error);
                refused = true;
            }
            if (!refused)
            {
                _epoch = _builtWorld = epoch;
                // A checkpoint names a world that is gone.
                _checkpoint = null;
                ResetMigrationState();
                CompleteGenerationAdmission();
            }
            boundary.Complete(drainReceived: false);
        }
        // The kernel's own state starts over, but the level and the build still placing it stay.
        if (refused) { MoveWorld(kernel, epoch, transition, keepsLevel: true); return true; }
        if (!ReferenceEquals(kernel, Kernel) || kernel.WorldEpoch != epoch) return true;
        ResampleAuthority();
        NetworkBinding.LogPoints?.Epoch("followed", epoch, "new-generation");
        NetworkSafePoint?.Invoke();
        if (_pendingWorldChange.Take(out var pendingTransition, out var pendingDetail)) InvalidateWorld(pendingTransition, pendingDetail);
        return true;
    }

    /// <summary>True only while the kernel begins a world under a level that stays: a client's follow of its host's number
    /// was refused (see <see cref="FollowWorld"/>), so the kernel's own state starts over, but the level its build placed,
    /// and the build still placing it, do not. A lifecycle observer reads it on that one <c>WorldChanged</c>.</summary>
    internal static bool KeepsBuiltLevel { get; private set; }

    private static void MoveWorld(RuntimeKernel kernel, long epoch, WorldTransition transition, bool keepsLevel = false)
    {
        using (var boundary = BeginDispatchBoundary())
        {
            kernel.FlushDiagnosticSummaries();
            NativeHookRegistration.LogSharedDiagnostics(kernel, "world-ending");
            // BeginWorld is the lifecycle boundary: it retires handles and tells all native identity providers to retire the old world.
            KeepsBuiltLevel = keepsLevel;
            try { kernel.BeginWorld(epoch); }
            finally { KeepsBuiltLevel = false; }
            // The level this client built stays, so the world it is now in is still that build's.
            if (keepsLevel) _builtWorld = epoch;
            kernel.SetFactionRelations(RuntimeFactionRelations.Gameplay()); _epoch = epoch; _tick = 0;
            // A new world forgets its rate; this side's step is the one it keeps running, so every world records it as it begins.
            ReportTickRate(kernel);
            // A checkpoint and a migration image name a world that is gone.
            _checkpoint = null;
            ResetMigrationState();
            if (transition == WorldTransition.NewGeneration) CompleteGenerationAdmission();
            // Release the lifecycle guard, but keep received work queued until the world is announced below.
            boundary.Complete(drainReceived: false);
        }
        // A lifecycle callback that already retired the kernel or moved it again leaves nothing to report here.
        if (!ReferenceEquals(kernel, Kernel) || kernel.WorldEpoch != epoch) return;
        ResampleAuthority();
        NetworkBinding.LogPoints?.Epoch("moved", epoch, keepsLevel ? "level-kept" : transition == WorldTransition.NewGeneration ? "new-generation"
            : transition == WorldTransition.LevelCleanup ? "level-cleanup" : "host-suspend");
        NetworkSafePoint?.Invoke();
        if (_pendingWorldChange.Take(out var pendingTransition, out var pendingDetail)) InvalidateWorld(pendingTransition, pendingDetail);
    }

    /// <summary>Reads the engine's fixed step once and records it with its reciprocal as the world's tick rate.</summary>
    private static void ReportTickRate(RuntimeKernel kernel)
    {
        var step = Time.fixedDeltaTime;
        kernel.ReportTickRate(step, 1f / step);
    }

    private static void CompleteGenerationAdmission()
    {
        if (!_resumeAdmissionAfterGeneration || _blockedUntilLobby) return;
        _resumeAdmissionAfterGeneration = false;
        _suspended = false;
        Kernel?.SetFactAdmissionSuspended(false);
    }

    /// <summary>
    /// GTFO-API <c>EventAPI.OnCheckpointReached</c>: <c>CheckpointManager.OnStateChange</c> reported a state change
    /// that is not a recall and whose door lock position moved, which is the game's own save point. What it captures is
    /// the variables and `g-once` latches of the world being saved; only the host has them (a client's copy is the
    /// host's snapshot), which is what the execute gate says.
    /// </summary>
    internal static void CaptureCheckpoint()
    {
        var kernel = Kernel;
        if (kernel == null || Suspension != null) return;
        using var boundary = BeginDispatchBoundary();
        if (!CanExecute)
        {
            LogCheckpoint(kernel, "capture-skipped", "cannot-execute", null, null, 0);
            boundary.Complete();
            return;
        }
        _checkpoint = kernel.CaptureCheckpoint();
        LogCheckpoint(kernel, "captured", "checkpoint-reached", _checkpoint, null, 0);
        boundary.Complete();
    }

    /// <summary>
    /// GTFO-API <c>EventAPI.OnCheckpointReloaded</c>: <c>CheckpointManager.OnStateChange</c> reported a recall of a
    /// reloading checkpoint, so the level goes back to the checkpoint, the behaviour is re-armed and the saved values
    /// are put back — the runtime is not suspended and the world epoch does not move, because this is the same
    /// expedition. A checkpoint this expedition never saved restores nothing.
    /// </summary>
    internal static void RestoreCheckpoint()
    {
        var kernel = Kernel;
        if (kernel == null || Suspension != null || !_inLevel) return;
        using var boundary = BeginDispatchBoundary();
        if (_checkpoint == null)
        {
            LogCheckpoint(kernel, "restore-skipped", "no-checkpoint", null, null, 0);
            boundary.Complete();
            return;
        }
        var restored = kernel.RestoreCheckpoint(_checkpoint,
            _admittedPlans ?? throw new InvalidOperationException("Admitted plan snapshot is unavailable."));
        // The state read back right after the restore is what the in-game check compares with what was saved. It is
        // read only while the record is written: enemy-scoped values and per-instance gate sets are dropped by the
        // restore by design, so a level holding either reads back smaller than it saved.
        if (kernel.RuntimeLogs(RuntimeLogLevel.Info))
            LogCheckpoint(kernel, "restored", "checkpoint-reloaded", _checkpoint, kernel.CaptureCheckpoint(), restored.Count);
        boundary.Complete();
    }

    private static IReadOnlyList<PlanCandidate> AdmittedPlans(IReadOnlyList<PlanCandidate> candidates,
        IReadOnlyList<PlanLoadOutcome> outcomes)
    {
        var admitted = new HashSet<string>(outcomes.Where(outcome => outcome.Loaded).Select(outcome => outcome.Path), StringComparer.Ordinal);
        return candidates.Where(candidate => admitted.Contains(candidate.Path)).ToArray();
    }

    /// <summary>Explicit simulation-thread author command. Content prerequisites remain frozen at startup;
    /// checkpoint callbacks never discover files, in either active mode.</summary>
    internal static CommandResult ReloadAuthoringPlans(RuntimeMode mode)
    {
        if (mode != RuntimeMode.Authoring)
            return CommandResult.Rejected("authoring-required", "Plan reload requires Authoring mode.");
        var kernel = Kernel;
        if (kernel == null || Suspension != null || !CanExecute || _dispatching || _contentPlans == null || _admittedPlans == null
            || ForgeCore.Puppets.Playback)
            return CommandResult.Rejected("runtime-not-ready", "Plan reload requires an idle, ready host in a level that is not a recording's playback.");
        using var boundary = BeginDispatchBoundary();
        try
        {
            // A reload is a new discovery, a new index and this level's files read again from it.
            ContentPackages.Rediscover(Paths.BepInExRootPath);
            _planIndex = PlanDiscovery.Index(Paths.BepInExRootPath, kernel);
            _levelRead = null;
            var candidates = Admit(kernel, PlanDiscovery.Scan(Paths.BepInExRootPath, kernel, _planIndex.LevelFiles(_adoptedLevel)));
            var outcomes = kernel.RestoreCheckpoint(kernel.CaptureCheckpoint(), candidates);
            _admittedPlans = AdmittedPlans(candidates, outcomes);
            NetworkBinding.AdoptPlans(_planIndex);
            var rejected = outcomes.Count(outcome => !outcome.Loaded);
            var result = CommandResult.Create(rejected == 0 ? CommandStatuses.Succeeded : CommandStatuses.Partial,
                CommitStates.Confirmed, rejected == 0 ? "plans-reloaded" : "plans-reloaded-with-rejections",
                "Per-file outcomes are recorded in plan.loaded and plan.rejected.",
                RuntimeJson.From(new { files = outcomes.Count, loaded = outcomes.Count - rejected, rejected, plans = kernel.LoadedPlans }));
            boundary.Complete();
            return result;
        }
        catch (Exception error)
        {
            Suspend("plan-reload-failed", error.Message);
            return CommandResult.FailedUnknown("plan-reload-failed", "Plan reload failed; the runtime is suspended.");
        }
    }

    /// <summary>The checkpoint record (`runtime.checkpoint`, info): the phase, why, and the saved document's size and
    /// digest; on a restore also what reads back and whether it is the saved document. Nothing is built at a closed
    /// level.</summary>
    private static void LogCheckpoint(RuntimeKernel kernel, string phase, string reason, string? saved, string? restored, int plans)
    {
        if (!kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        var values = new List<RuntimeLogValue>(8) { RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("reason", reason) };
        if (saved != null)
        {
            values.Add(RuntimeLogValue.Of("savedBytes", saved.Length));
            values.Add(RuntimeLogValue.Of("savedDigest", Digest(saved)));
        }
        if (restored != null)
        {
            values.Add(RuntimeLogValue.Of("restoredBytes", restored.Length));
            values.Add(RuntimeLogValue.Of("restoredDigest", Digest(restored)));
            values.Add(RuntimeLogValue.Of("matched", string.Equals(saved, restored, StringComparison.Ordinal)));
            values.Add(RuntimeLogValue.Of("plansReloaded", plans));
        }
        kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.Checkpoint, new RuntimeLogFacts { Values = values });
    }

    /// <summary>The first sixteen hex digits of a document's SHA-256: enough to tell two documents apart in a log line.</summary>
    private static string Digest(string document)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document)), 0, 8);

    /// <summary>The gate's parts as last written by <see cref="TraceGate"/>; -1 before the first line.</summary>
    private static int _gateParts = -1;

    /// <summary>
    /// The execution gate's parts at this tick (`runtime.gate`, trace), written when any of them moved: the bridge's
    /// own flags and the three native reads behind <see cref="NativeAuthority"/>, taken apart so a line says which
    /// part closed the gate, plus the game state's own name. Only called while trace is open, so a player's install
    /// never makes the separate native reads.
    /// </summary>
    private static void TraceGate(RuntimeKernel kernel)
    {
        var master = Session.IsHost;
        var state = GameStateManager.CurrentStateName;
        var migrating = SNet.MasterManagement != null && SNet.MasterManagement.IsMigrating;
        var parts = (_inLevel ? 1 : 0) | (_suspended ? 2 : 0) | (_pendingWorldChange.Pending ? 4 : 0) | (_pendingStop ? 8 : 0)
            | (_wasHost ? 16 : 0) | (master ? 32 : 0) | (state == eGameStateName.InLevel ? 64 : 0) | (migrating ? 128 : 0)
            | ((int)state << 8);
        if (parts == _gateParts) return;
        _gateParts = parts;
        kernel.LogRuntime(RuntimeLogLevel.Trace, RuntimeHostLogCodes.Gate, new RuntimeLogFacts
        {
            Values = new[]
            {
                RuntimeLogValue.Of("open", CanExecute), RuntimeLogValue.Of("inLevel", _inLevel),
                RuntimeLogValue.Of("suspended", _suspended), RuntimeLogValue.Of("pendingWorld", _pendingWorldChange.Pending),
                RuntimeLogValue.Of("pendingStop", _pendingStop), RuntimeLogValue.Of("wasHost", _wasHost),
                RuntimeLogValue.Of("master", master), RuntimeLogValue.Of("state", state.ToString()),
                RuntimeLogValue.Of("migrating", migrating)
            }
        });
    }

    /// <summary>Pauses Forge until the next level. <paramref name="notice"/> is the chat line the local player gets: a
    /// failure by default, or <see cref="SuspendNotice.HostMismatch"/> for a session whose host runs other content.</summary>
    internal static void Suspend(string code, string detail, bool untilLobby = false, string notice = SuspendNotice.Failed)
    {
        ResampleAuthority();
        _resumeAdmissionAfterGeneration = false;
        _blockedUntilLobby |= untilLobby;
        if (_suspended) return;
        _suspensionWorldEpoch = Kernel?.WorldEpoch ?? 0;
        _suspended = true;
        SuspendNotice.Request(notice);
        Kernel?.SetFactAdmissionSuspended(true);
        InvalidateWorld(WorldTransition.HostSuspend, detail);
        // The pause is a kernel record written through the same sink as every other one; the host adds no BepInEx line.
        if (Kernel is { } kernel && kernel.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped))
            kernel.LogSuspended(code, detail);
    }

    internal static void Guard(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            // The kernel's own record follows the cfg log level; a fault that pauses every package is always written.
            // A contract refusal keeps its own code so the suspension can be told apart by it.
            var code = error is RuntimeContractException contract ? contract.Code : "bridge-exception";
            Plugin.PluginLog.LogError(Plugin.PluginName + ": " + code + ": " + error);
            Suspend(code, error.ToString());
        }
    }

    internal static void Stop()
    {
        ResampleAuthority();
        ResetFirstFrameTiming();
        _inLevel = false; _suspended = true;
        _resumeAdmissionAfterGeneration = false;
        NativeModelRuntime.EndLevel(ModelReport);
        NativeModelRuntime.ClearNextLevelClosure();
        Kernel?.StopFactAdmission();
        // Native teardown may occur inside a handler. Block commits immediately, clean the kernel at its safe point.
        if (_dispatching) { _pendingStop = true; return; }
        using var boundary = BeginDispatchBoundary();
        NativeHookRegistration.LogSharedDiagnostics(Kernel, "runtime-stopping");
        var retiringLog = _log;
        SessionBinding.Stop();
        NetworkBinding.Stop();
        GTFO.API.EventAPI.OnGameStateChanged -= GameStateChanged;
        NetworkSafePoint = null;
        Kernel?.StopRuntime();
        if (ReferenceEquals(_log, retiringLog)) _log = null;
        retiringLog?.Dispose();
        _pendingStop = false;
        _pendingWorldChange = default;
        ResetMigrationState();
        Kernel = null;
        _contentPlans = null;
        _admittedPlans = null;
        _planIndex = null; _levelRead = null; _levelAdopted = false; _adoptedLevel = null;
        boundary.Complete();
    }

    // Normal outcomes of the model runtime (a source served from cache, a batch built, a gun model's view changing)
    // are Debug; readbacks of what a placed model got (its colliders, colliders built from a mesh) are Info; a model the
    // package does not ship is an Error (the placement draws without it); every other report is a deviation and stays a
    // Warning. Debug and Info are written only with ForgeCore's developer logs; a Warning always, once per kind per level.
    private static readonly string[] RoutineModelReports =
        { "model-source-cached:", "model-imported-batched:", "model-instanced-batched:", "model-draw-view:" };
    private static readonly string[] ReadbackModelReports = { "model-collision:", "model-collision-from-mesh:", "model-shard-joined:" };
    private static readonly string[] FailedModelReports = { "model-imported-missing:" };

    private static readonly ForgeCore.OnceReporter ModelDeviations = new(line => global::ForgeEngine.Plugin.PluginLog.LogWarning(line));

    private static void ModelReport(string message)
    {
        try
        {
            var line = "Forge Engine native model: ";
            if (Array.Exists(FailedModelReports, prefix => message.StartsWith(prefix, StringComparison.Ordinal))) global::ForgeEngine.Plugin.PluginLog.LogError(line + message);
            // Routine reports and readbacks are developer lines; a deviation is a Warning, once per kind per level.
            else if (Array.Exists(RoutineModelReports, prefix => message.StartsWith(prefix, StringComparison.Ordinal))) { if (ForgeCore.Log.Developer) global::ForgeEngine.Plugin.PluginLog.LogDebug(line + message); }
            else if (Array.Exists(ReadbackModelReports, prefix => message.StartsWith(prefix, StringComparison.Ordinal))) { if (ForgeCore.Log.Developer) global::ForgeEngine.Plugin.PluginLog.LogInfo(line + message); }
            else ModelDeviations.Report(message[..Math.Max(message.IndexOf(':'), 0)], line + message);
        }
        catch (Exception reporter)
        { Console.Error.WriteLine("Forge Engine native model: " + message + " (host log failed: " + reporter.GetType().Name + ")"); }
    }
}

/// <summary>
/// The host deliberately owns no attribute-discovered patch class: domain layout audits require the host and each
/// domain package to expose disjoint hook sets. These five registrations are installed explicitly by the host's one
/// Harmony owner and still target only vanilla migration boundaries no other package patches. Completion is shared with
/// other packages, so ForgeCore patches it (<see cref="ForgeCore.GameHooks.MasterMigrationFound"/>) and the host subscribes.
/// </summary>
internal static class HostMigrationPatches
{
    private static IDisposable? _masterFound;

    internal static void Install(Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        Postfix(harmony, typeof(SNet_MasterManager), nameof(SNet_MasterManager.ForceMigration), nameof(MigrationStarted));
        Postfix(harmony, typeof(SNet_MasterManager), nameof(SNet_MasterManager.SearchForMigrationMaster), nameof(MigrationStarted));
        Postfix(harmony, typeof(SNet_Capture), nameof(SNet_Capture.CaptureGameState), nameof(AfterCaptureGameState));
        Postfix(harmony, typeof(SNet_Capture), nameof(SNet_Capture.RecallBuffer), nameof(AfterRecallBuffer));
        Prefix(harmony, typeof(SNet_MasterManager), "ForceExitToLobby", nameof(MigrationFailed));
        _masterFound = ForgeCore.GameHooks.MasterMigrationFound.After(static (_, _) => MigrationCompleted());
    }

    /// <summary>Ends what <see cref="Install"/> subscribed; the patches themselves come off with the host's Harmony owner.</summary>
    internal static void Uninstall() { _masterFound?.Dispose(); _masterFound = null; }

    private static void Postfix(Harmony harmony, Type nativeType, string nativeName, string hookName)
        => harmony.Patch(Require(nativeType, nativeName), postfix: new HarmonyMethod(Require(typeof(HostMigrationPatches), hookName)));

    private static void Prefix(Harmony harmony, Type nativeType, string nativeName, string hookName)
        => harmony.Patch(Require(nativeType, nativeName), prefix: new HarmonyMethod(Require(typeof(HostMigrationPatches), hookName)));

    private static System.Reflection.MethodInfo Require(Type type, string name)
        => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static void MigrationStarted() => GameRuntimeBridge.Guard(GameRuntimeBridge.BeginNativeHostMigration);
    private static void AfterCaptureGameState(eBufferType buffer)
        => GameRuntimeBridge.Guard(() => GameRuntimeBridge.CaptureHostMigration((byte)buffer));
    private static void AfterRecallBuffer(eBufferType bufferType)
        => GameRuntimeBridge.Guard(() => GameRuntimeBridge.RecallHostMigration((byte)bufferType));
    private static void MigrationCompleted() => GameRuntimeBridge.Guard(GameRuntimeBridge.CompleteHostMigrationAndPublish);
    private static void MigrationFailed() => GameRuntimeBridge.Guard(GameRuntimeBridge.HostMigrationFailed);
}
