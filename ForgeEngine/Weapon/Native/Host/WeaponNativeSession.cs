using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeWeapon.Native;

/// <summary>Native equipment observation lifetime on the host's single Runtime, started by <see cref="Plugin"/>.
/// Owners are never created here: the adapter asks ForgePlayer's player directory for the player references it
/// records, and the identity session re-checks them against the kernel's current lives.</summary>
internal sealed partial class WeaponNativeSession : IDisposable
{
    /// <summary>The native build's thermal sights (Native/Thermal/ThermalSight.cs).</summary>
    static partial void NativeThermalSights(WeaponFieldSet fields, Action<string> report, WeaponLog log);

    /// <summary>The native build's shot-rule and weapon-projectile bodies. Their bodies are in `WeaponShotRules.cs`
    /// and `WeaponBulletProjectiles.cs`, which only the Native project compiles: they patch the weapon's own ray
    /// cast and fly a projectile prefab, game types the offline doubles do not carry. Where those files are not
    /// compiled every call below compiles away, and the registration declares neither row's binding.</summary>
    static partial void NativeShotBodies(IForgeEngine kernel, IPlayerDirectory players, EquipmentNativeAdapter adapter, Func<bool> canExecute,
        Func<bool> canExecuteOwner, WeaponLog log, ref CommandHandler? shotRule,
        ref Func<EntityReference, string?>? shotRuleHolders, ref CommandHandler? bulletProjectile);
    /// <summary>The registration the shot rule casts its handles through, handed over once it exists.</summary>
    static partial void NativeShotAttach(RuntimeModuleHandle registration);
    /// <summary>The lifecycle the two bodies end their rules and flights on: a tick advance, a world boundary, a
    /// checkpoint reload and a runtime that stopped.</summary>
    static partial void NativeShotLifecycle(RuntimeLifecycleEvent value);
    static partial void NativeInteractionLifecycle(RuntimeLifecycleEvent value);
    /// <summary>One equipment life ended: the rules standing on it end with it.</summary>
    static partial void NativeShotLifeEnded(EntityReference reference);
    /// <summary>The session is going away: every rule and flight is given up and nothing answers either row.</summary>
    static partial void NativeShotDetach();
    /// <summary>The native build's bio-tracker device writer, registered on ForgePlayer's device switchboard. Its body
    /// is in `BiotrackerDevice.cs`, which only the Native project compiles; where it is absent the category simply
    /// has no writer on this machine.</summary>
    partial void NativeDeviceWriters(IForgeEngine kernel, IPlayerDirectory players);
    /// <summary>The writer leaves the switchboard, putting back the scanner it holds off.</summary>
    partial void NativeDeviceDetach();

    partial void NativeContentRegister();
    partial void NativeContentDetach();
    // Once per simulation tick: settles added-part requests that joined a shard load another caller started.
    partial void PollExtraShards();
    partial void PollThermalSight();

    private static WeaponNativeSession? _current;

    /// <summary>The live session. Setting it also points <see cref="NativeCallFailure"/> at this session's log.</summary>
    internal static WeaponNativeSession? Current
    {
        get => _current;
        private set
        {
            _current = value;
            NativeCallFailure.Sink = value == null ? null : (code, text) => value.Log.Text(RuntimeLogLevel.Error, code, text);
        }
    }
    // The functions this process withdrew, each with the refusal that did it. A withdrawn function's hook bodies are inert.
    private readonly Dictionary<WeaponFeature, string> _withdrawn = new();
    // Derived admission cache; the dictionary retains the cold diagnostic reasons.
    private uint _withdrawnFeatures;
    private WeaponDamageObservers? _damage;

    /// <summary>The live session when <paramref name="feature"/> is available in it; null otherwise, so a hook body of a
    /// withdrawn function reads nothing and publishes nothing while every other function keeps running.</summary>
    internal static WeaponNativeSession? Offer(WeaponFeature feature)
    {
        var session = Current;
        return session != null && (uint)feature <= (uint)WeaponFeature.Lifetime
            && (session._withdrawnFeatures & (1u << (int)feature)) == 0 ? session : null;
    }

    internal IReadOnlyDictionary<WeaponFeature, string> Withdrawn => _withdrawn;

    /// <summary>Withdraws one function on the first refusal of its hook. The refusal is logged once at error level; the
    /// process keeps running with every other function.</summary>
    internal void Withdraw(WeaponFeature feature, string reason)
    {
        if ((uint)feature > (uint)WeaponFeature.Lifetime) throw new ArgumentOutOfRangeException(nameof(feature));
        if (_withdrawn.ContainsKey(feature)) return;
        _withdrawn[feature] = reason;
        _withdrawnFeatures |= 1u << (int)feature;
        try { Log.Reason(RuntimeLogLevel.Error, WeaponLogCodes.CapabilityUnavailable, WeaponFeatures.Label(feature) + ": " + reason); }
        catch (Exception logging) { LastReporterFailure = logging.GetType().Name; }
    }

    /// <summary>Takes Runtime's refusals for the declared hook classes, withdraws each function that owns a refused class,
    /// and observes the enemy damage receivers for the current world.</summary>
    private void ApplyDeclarations(IReadOnlyDictionary<WeaponFeature, string> refusals, long worldEpoch)
    {
        foreach (var (feature, reason) in refusals) Withdraw(feature, reason);
        foreach (var (feature, reason) in WeaponFeatures.Withdrawn) Withdraw(feature, reason);
        _damage = new WeaponDamageObservers(this);
        if (worldEpoch >= 0) _damage.Renew(worldEpoch);
    }

    /// <summary>Renews the damage windows for a new world: each window is leased to the world it was observed in.</summary>
    internal void RenewDamageObservers(long worldEpoch) => _damage?.Renew(worldEpoch);
    private readonly IForgeEngine _kernel;
    // _report is the console warning sink; _note the info sink for designed paths that are worth a line but are no problem.
    private readonly Action<string> _report, _note;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed, _faulted, _unwired, _missingPlayerDirectoryReported;
    internal EquipmentNativeAdapter Adapter { get; private set; } = null!;
    /// <summary>The weapon provider's record point in the Forge Engine log, shared by every observer of this
    /// session. It is attached to the registration the moment it exists; the per-shot and per-hit diagnostics are
    /// trace records, so they are built only in an elevated (Authoring) session.</summary>
    internal WeaponLog Log { get; } = new();
    /// <summary>One startup or session message, written to the package's Runtime log under the given code as the record's
    /// message-only detail, like the other base packages' startup records. It is not a `reason` value, which the kernel
    /// limits to 256 characters: the startup timing line is built at run time from the plugin's phases and is longer.</summary>
    internal EquipmentIdentitySession Identity { get; private set; } = null!;
    internal WeaponCombatObserver Combat { get; private set; } = null!;
    /// <summary>The weapon-state facts and the impact records behind the hit-context read: one charge row, one
    /// aim row, the shot-resolution row, and the store the query is answered from.</summary>
    internal CombatFactsObserver CombatFacts { get; private set; } = null!;
    internal ClientWeaponReport ClientFacts { get; private set; } = null!;
    /// <summary>The attack-instance rows: the game's own empty-clip paths and the two ends of a burst sequence,
    /// each naming the same equipment life the shot observer counts and never counting a shot itself.</summary>
    internal AttackInstanceModule Attack { get; private set; } = null!;
    /// <summary>The deployed-device facts: one sentry firing pair, the glue gun's two launch bodies and the mine's
    /// own trigger, read against the placement table this session's adapter owns.</summary>
    internal WeaponDeployableFacts Facts { get; private set; } = null!;
    /// <summary>The holder provider: the reload and clip-set rows this package executes on the machine that holds
    /// the equipment, registered as its own module so a plan can address it.</summary>
    internal WeaponHolderModule Holder { get; private set; } = null!;
    /// <summary>The melee hits two native bodies report, deduped per target in the one observer.</summary>
    internal WeaponMeleeHitFacts Melee { get; private set; } = null!;
    /// <summary>The three equipment facts of this batch: the attached light being set, a throwable leaving the
    /// hand with its velocity, and the melee push's own three phases.</summary>
    internal EquipmentEventFacts Events { get; private set; } = null!;
    /// <summary>The two read rows' native source, attached for as long as this session is the live one: a plan
    /// evaluating an equipment-ammunition query or a held-item condition reads this machine's own tables, and a
    /// process with no session refuses both rows by code.</summary>
    internal InventoryQueryReads? Reads { get; private set; }
    /// <summary>The hit-context read's native source, attached for as long as this session is the live one: a plan
    /// evaluating `forge.query.combat.hit_context` reads the impacts this machine observed, and a process with no
    /// session refuses the read by code.</summary>
    internal HitContextReads? HitContexts { get; private set; }
    /// <summary>The availability-gate read's native source, attached for as long as this session is the live one:
    /// a plan evaluating `forge.query.weapon.gate` reads the item the named life currently holds, and a process
    /// with no session refuses the read by code.</summary>
    internal WeaponGateReads? Gates { get; private set; }
    /// <summary>The debug-switch write's native source, attached and detached with the session for the same
    /// reason: a process with no session refuses the write by code instead of setting a static nothing answers
    /// for.</summary>
    internal WeaponDebugSwitchWrites? DebugSwitches { get; private set; }
    /// <summary>The whole-profile write's native half: the one place a `profile` reference becomes values and the
    /// one place the change is named by an effect handle. It is attached with the session because both the handle
    /// it casts and the applier it writes through belong to a live session.</summary>
    internal WeaponProfileActions? Profiles { get; private set; }
    /// <summary>The profile read's native source, attached for as long as this session is the live one: a plan
    /// evaluating `forge.query.weapon.profile` reads the block the instance-override write swaps, and a process
    /// with no session refuses the read by code.</summary>
    internal WeaponProfileReads? ProfileReads { get; private set; }
    /// <summary>The body that answers the profile row, attached to the source above. The two travel together and
    /// are given up together, so a command can never reach a source no session stands behind.</summary>
    internal WeaponProfileWrites? ProfileBody { get; private set; }
    /// <summary>The deployed-device detonation's native half: one placed mine's own trigger, called through the
    /// interface both payloads implement.</summary>
    internal DetonateActions? Detonations { get; private set; }
    /// <summary>The body that answers the detonation row, attached to the source above.</summary>
    internal DetonateWrites? DetonateBody { get; private set; }
    private SentryRecallActions? _sentryRecall;
    private Action? _detachSentryRecall;
    /// <summary>The ammunition pair's two bodies: one write on a player's own pool with the readback that proves
    /// it, and the refusal a pool this machine does not own gets. Built before the registration for the same
    /// reason the mark row is.</summary>
    internal WeaponSupplyAdapter Supply { get; private set; } = null!;
    /// <summary>The inventory pair's bodies, give and consume. There is no third one: the node list has no drop
    /// node, so the drop row was deleted at integration (ruling 110.5) and its body followed it (ruling 133.3).
    /// The adapter is built before the registration for the same reason the mark row is.</summary>
    internal InventoryActionAdapter Inventory { get; private set; } = null!;
    /// <summary>The instance-override writer: the ledger every accepted field set is remembered in, and the only
    /// code in the package that writes a weapon instance's own archetype block.</summary>
    internal WeaponOverrideApplier Overrides { get; private set; } = null!;
    /// <summary>The three instance-override row bodies, answered on the machine the command reaches.</summary>
    internal WeaponActionAdapter OverrideActions { get; private set; } = null!;
    /// <summary>The one hitscan row body: the native shot group `forge.action.combat.hitscan_shot` submits
    /// through `Weapon.CastWeaponRay` and `BulletWeapon.BulletHit`.</summary>
    internal WeaponHitscanActions Hitscan { get; private set; } = null!;
    /// <summary>The authored gear-part poses, projected once in <see cref="Start"/> from the same document family
    /// every other Forge block field comes from, and never reloaded: the applier only ever writes what this
    /// snapshot holds.</summary>
    internal GearPartTransformApplier GearParts { get; private set; } = null!;
    internal WeaponModelAssemblies Models { get; private set; } = null!;
    /// <summary>The flashlight rows the documents point gears at, projected from the same snapshot and applied by
    /// the same holder hook as the part poses.</summary>
    internal GearPartFlashlightApplier Flashlights { get; private set; } = null!;
    /// <summary>The guns with a shot noise of their own, projected from the same snapshot and applied around the
    /// four `Fire` bodies.</summary>
    internal WeaponFireNoise FireNoise { get; private set; } = null!;
    /// <summary>The one reading of the authored Forge block fields of this install. The writer that applies them
    /// lives in `WeaponFieldHooks.cs`, which only the Native project compiles; where it is absent this snapshot
    /// is read and reported but no native member is written.</summary>
    internal WeaponFieldSet Fields { get; private set; } = WeaponFieldSet.Empty;

    /// <summary>Hands the accepted documents to this build's field writer.</summary>
    static partial void NativeFieldApplierReady(WeaponFieldSet fields, WeaponLog log);
    /// <summary>The enemy scanner's per-frame bodies, put on when the documents carry a scanner block.</summary>
    static partial void NativeScannerHooks(WeaponFieldSet fields);
    /// <summary>The loadout policies accepted from disk, and the one pool narrowing this package performs. It is
    /// created last because it is what the hooks it installs reach for.</summary>
    internal GearLoadoutSession Loadout { get; private set; } = null!;
    internal bool Faulted => Down;
    /// <summary>A hook failure stops the session for the level it happened in; start failure and shutdown stop it for good.</summary>
    private readonly global::ForgeEngine.GameBindings.WorldFault _worldFault = new();
    private bool Down => _faulted || _worldFault.Holds(_kernel.WorldEpoch);
    internal bool IsDisposed => _disposed;

    internal string? LastFault { get; private set; }
    internal string? LastReporterFailure { get; private set; }

    private WeaponNativeSession(IForgeEngine kernel, Action<string> report, Action<string> note)
    {
        _kernel = kernel;
        _report = report;
        _note = note;
    }

    /// <summary>
    /// Starts the package's native half. <paramref name="installHooks"/> declares the native callbacks to Runtime
    /// after the session is current, so a declared callback reads a live session and never a half-built one.
    /// <paramref name="projectileLaunch"/> is the one body this file does not
    /// build for itself: the launch row's spawn call reaches a native prefab, so it is handed in as a factory and
    /// the row is declared only because a body was really supplied for it. A caller that supplies none is a
    /// process with no launch row, which the registration answers by not declaring the binding at all.
    /// </summary>
    internal static WeaponNativeSession Start(IForgeEngine kernel, RuntimeLogLevel logLevel, Func<bool> canExecute,
        Func<bool> canExecuteOwner, Action<string> report, Action<string> note,
        Func<IReadOnlyDictionary<WeaponFeature, string>> installHooks,
        string gearPartsRoot, Func<Func<bool>, CommandHandler>? projectileLaunch = null)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(canExecute);
        ArgumentNullException.ThrowIfNull(canExecuteOwner);
        ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(installHooks);
        ArgumentNullException.ThrowIfNull(gearPartsRoot);
        if (Current != null)
            throw new InvalidOperationException("Weapon native observation is single-instance per process.");
        if (!kernel.IsRegistrationOpen)
            throw new InvalidOperationException("Weapon must register during dependent plugin Load, before Runtime startup.");
        var players = ForgePlayer.Native.PlayerHost.Directory;
        var profiles = WeaponPropertyProfileTable.Load(ContentPackages.ReadResources(gearPartsRoot, ContentPackages.EngineReader,
            WeaponProfileContract.ProfileResourceKind, WeaponPropertyProfileTable.SetCodes, report), report);
        WeaponProfileActions.UseProfiles(profiles);
        var session = new WeaponNativeSession(kernel, report, note);
        session._unwired = true;
        try
        {
            var log = session.Log;
            // Keep observation on host authority even if a native owner write synchronously invokes hooks.
            session.Adapter = new EquipmentNativeAdapter(kernel, players, () => !session.Down && canExecute(),
                () => !session.Down && canExecuteOwner(), log);
            // The bodies this provider executes itself, built before the registration because the registration
            // carries them: the ammunition pair, the four instance-override rows and the inventory pair. Each
            // reads the player behind a player reference, and names a player back, through ForgePlayer's directory:
            // the owning domain answers both directions and this package never derives a player itself.
            session.Supply = new WeaponSupplyAdapter(kernel, () => !session.Down && canExecuteOwner(),
                reference => players.PlayerOf(reference), players.ReferenceOf, report);
            session.Inventory = new InventoryActionAdapter(kernel, () => !session.Down && canExecute(),
                WeaponSupplyItems.TryResolve, reference => players.PlayerOf(reference), players.ReferenceOf, report);
            // Action entry points enforce readiness. The retained ledger must also recompute surviving
            // writers during client lifecycle cleanup, after OwnerInvocation has ended. Resolve the exact
            // current local item read-only here; this does not admit a new action or publish native facts.
            session.Overrides = new WeaponOverrideApplier(reference => session.Adapter.LocalHolder(reference)
                    ? session.Adapter.EquippableOf(reference)?.Item.TryCast<Gear.BulletWeapon>() : null,
                session.Adapter.EntityOf, report, log,
                reference => session.Adapter.LocalHolder(reference) ? session.Adapter.EquippableOf(reference)?.Item.TryCast<Gear.MeleeWeaponFirstPerson>() : null);
            session.OverrideActions = new WeaponActionAdapter(kernel, () => !session.Down && canExecuteOwner(),
                session.Overrides, report);
            // The hitscan row's body. It is built here rather than handed in because it needs this session's own
            // adapter to resolve the source, and the adapter is the same one every other body of this session
            // reads through: a second lookup table would be a second answer to "which item does this life hold".
            session.Hitscan = new WeaponHitscanActions(kernel, session.Adapter,
                () => !session.Down && canExecute(), report);
            // The shot rule and the weapon projectile read through the same adapter; the rule's owner-tier
            // resolver is the directory's session of the player the holder channel says holds the equipment.
            CommandHandler? shotRule = null, bulletProjectile = null;
            Func<EntityReference, string?>? shotRuleHolders = null;
            NativeShotBodies(kernel, players, session.Adapter, () => !session.Down && canExecute(),
                () => !session.Down && canExecuteOwner(), log,
                ref shotRule, ref shotRuleHolders, ref bulletProjectile);
            // The gear-equip row runs on the recipient's own machine: the owner-tier resolver is that player's session, and the
            // body refuses on any other machine. Its game half is the one port below; the action itself is game-independent.
            var gearEquip = new GearEquipAction(new GearEquipPort(kernel, players, () => !session.Down && canExecuteOwner()), report);
            // Duplicate provider registration throws here, before any detour is attempted.
            // Entity snapshots run inside Runtime's read-only observer boundary. Resolve owners through the
            // owning domain, not a recursive kernel query (which is rejected as entity-observer-mutation).
            session.Identity = new EquipmentIdentitySession(kernel, logLevel, session.Adapter.IsNativeCurrent,
                reference => players.PlayerOf(reference) is { WasCollected: false },
                equipmentObserver: session.Adapter.ObserveEquipment, deployedObserver: session.Adapter.ObserveDeployable,
                deployedCurrent: session.Adapter.HoldsDeployable,
                gearBlockMatcher: session.Adapter.MatchesGearBlock,
                holderResolver: session.Adapter.ResolveHolderMount,
                equipmentInstanceResolver: instance => instance is Item item ? session.Adapter.EntityOf(item) : null,
                // A deployed device is an equipment entity too: a heal whose source is the medical turret reads its holder from it.
                equipmentInstance: reference => (object?)session.Adapter.EquippableOf(reference)?.Item
                    ?? session.Adapter.PlacedItemOf(reference)?.TryCast<ItemEquippable>(),
                module: ModuleDefinition.Create(
                    ammoAdd: session.Supply.HandleAdd,
                    ammoAddRecipients: reference => players.PlayerOf(reference) is { } holder ? players.SessionOf(holder) : null,
                    overrides: WeaponOverrideContract.Handlers(session.OverrideActions.FireRate,
                        session.OverrideActions.Spread, session.OverrideActions.Recoil,
                        session.OverrideActions.Property),
                    inventoryGive: session.Inventory.HandleGive,
                    equipGear: gearEquip.Handle,
                    equipGearRecipients: reference => players.PlayerOf(reference) is { } recipient ? players.SessionOf(recipient) : null,
                    // The `weapon` and `tool` kinds' owner: only this half reads the game's offline gear table.
                    gearResources: GearEquipPort.ResourceProviders(),
                    // All four override rows can use the shared effect clock and cancellation handle.
                    overrideRestores: new Dictionary<string, EffectRestoreHandler>(StringComparer.Ordinal)
                    {
                        [WeaponOverrideContract.PropertyHandler] = session.OverrideActions.RestoreProperty,
                        [WeaponOverrideContract.FireRateHandler] = session.OverrideActions.RestoreProperty,
                        [WeaponOverrideContract.SpreadHandler] = session.OverrideActions.RestoreProperty,
                        [WeaponOverrideContract.RecoilHandler] = session.OverrideActions.RestoreProperty
                    },
                    projectileLaunch: projectileLaunch?.Invoke(() => !session.Down && canExecute()),
                    hitscan: session.Hitscan.Launch,
                    // The `profile` kind's owner: the table read above, the same one the offline admission reads.
                    profiles: profiles,
                    shotRule: shotRule, shotRuleHolders: shotRuleHolders, bulletProjectile: bulletProjectile,
                    overrideHolders: equipment => session.Adapter.EquippableOf(equipment) is { } held ? players.SessionOf(held.Backpack.Owner) : null),
                observe: value =>
                {
                    session.Adapter.LevelSentries.ObserveLifecycle(value);
                    ObserveOverrides(session.Overrides.Ledger, session.Profiles, value);
                    if (value.Kind is RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored
                        || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                        session.OverrideActions.ForgetModifiers();
                    else if (value.Kind == RuntimeLifecycleKind.TickAdvanced)
                    {
                        session.OverrideActions.PruneModifiers();
                        session.PollExtraShards();
                        session.PollThermalSight();
                        WeaponDebugSwitchSource.Current?.KeepMagazineFull();
                        session.EnsureLazyHooks();
                        session.ClientFacts.Tick();
                    }
                });
            log.Attach(session.Identity.Registration);
            session.Adapter.Attach(session.Identity);
            session.OverrideActions.RestoreOwner = session.Identity.Registration.RestoreOwnerEffect;
            session.OverrideActions.Registration = session.Identity.Registration;
            // Native block baselines and each writer's contribution are private holder state, including
            // kernel-managed effects whose wire handle belongs to the host. Until recalled native identity
            // can prove that state, migration must refuse it rather than clear it at WorldChanged.
            session.Identity.Registration.RegisterProviderHandover("forge.weapon.native-obligations",
                _ => RuntimeJson.From(new { nativeOverrides = session.Overrides.AppliedCount, writers = session.Overrides.Ledger.RetainedCount }),
                PrepareNativeHandover);
            session.Overrides.Ledger.Expired = session.OverrideActions.Expired;
            NativeShotAttach(session.Identity.Registration);
            // The read rows' source goes on with the identity table they read through: a life is only nameable
            // while that table answers for it, so attaching the two together is what keeps a query from reading a
            // slot this session no longer follows.
            session.Reads = InventoryQueryReads.Attach(new InventoryQuerySource(session.Adapter));
            // The availability-gate read goes on with them, for the same reason and through the same identity
            // table. The debug-switch write is attached here too: it writes no equipment at all — the three
            // switches are the game's own process-wide statics — but it is the live session that decides whether
            // this machine may write them, so it lives and dies with the session like every other body.
            session.Gates = WeaponGateReads.Attach(new WeaponGateSource(session.Adapter));
            session.DebugSwitches = WeaponDebugSwitchWrites.Attach(new WeaponDebugSwitchSource(session.Adapter, kernel));
            // The whole-profile write and the detonation answer through their own attached sources, exactly like
            // the debug switch above: the profile casts its handle through this session's own registration and
            // writes through the same applier the named override rows use, and the detonation calls the placed
            // device's own trigger through the placement table the deployable facts already read.
            session.Profiles = WeaponProfileActions.Attach(session.Identity.Registration, session.Overrides,
                () => !session.Down && canExecuteOwner() && kernel.StartupState == RuntimeStartupState.Ready, report, log);
            session.ProfileBody = WeaponProfileWrites.Attach(session.Profiles);
            // The profile read goes on beside the applier it reads through: both answer about the same instance's
            // own block, so attaching them together is what keeps a read from reaching a weapon the write half
            // would refuse. It refreshes nothing — every evaluation re-reads the instance.
            session.ProfileReads = WeaponProfileReads.Attach(new WeaponProfileQuerySource(session.Adapter.WeaponOf));
            // One equipment life ending is the boundary the override ledger's own doc names and nothing else could
            // reach: the adapter knows the life is over before it drops the identity, so it is handed the one call
            // that ends this package's writes on that instance. The profile half goes first — it owns a writer
            // identity and a handle — and the ledger entry follows, which is what puts the instance's own block
            // back for the named rows too. Both are idempotent, so a life whose only writer was the profile is not
            // restored twice.
            session.Adapter.LifeEnded = session.EndOverridesAtLifeEnd;
            session.Detonations = new DetonateActions(kernel, session.Adapter.PlacedItemOf,
                () => !session.Down && canExecute() && kernel.Lifecycle.IsReadyHost, report);
            session.DetonateBody = DetonateWrites.Attach(session.Detonations);
            session._sentryRecall = new SentryRecallActions(kernel, players, session.Adapter.PlacedSentriesOf,
                () => !session.Down && canExecute() && kernel.Lifecycle.IsReadyHost, report);
            session._detachSentryRecall = SentryRecallContract.Attach(session._sentryRecall);
            session.Combat = new WeaponCombatObserver(session.Adapter, () => kernel, log);
            // The hit-context row is answered from the impact records the shot observer above keeps, so its source
            // is that same observer and not a second table of hits: the geometry is measured once, where the shot
            // was really resolved. It is attached with the session and detached with it, so a read after the
            // session is gone refuses by code instead of answering from a half-disposed observer.
            session.HitContexts = HitContextReads.Attach(session.Combat);
            // The weapon-state and shot-resolution facts. The shot observer is handed in rather than duplicated:
            // the shot's own tally is what both the resolution and the hit candidate describe, so there is one
            // place that counts what a shot reached.
            session.CombatFacts = CombatFactsObserver.Attach(session.Adapter, session.Combat, () => kernel,
                session.Adapter.EntityOf, session.Adapter.OwnerOf);
            session.ClientFacts = new ClientWeaponReport(session, kernel, log);
            // The attack-instance rows name the shot observer's own equipment life for their actor and equipment
            // ports; the module keeps no tally of shots and no ledger of hits of its own.
            session.Attack = new AttackInstanceModule(session.Adapter, () => kernel, log);
            // The deployed-device facts answer from the same placement table the life-cycle observation uses, so
            // the adapter's own lookups are handed in rather than a second index kept here.
            session.Facts = WeaponDeployableFacts.Attach(kernel, () => !session.Down && canExecute(),
                session.Adapter.DeployedEntityOf, session.Adapter.EntityOf,
                session.Adapter.OwnerOf, reference => reference != null && kernel.IsEntityCurrent(reference),
                session.Identity, log);
            // The melee hit reads the adapter's authority gate and publication path, exactly like the shot facts.
            session.Melee = WeaponMeleeHitFacts.Attach(session.Adapter, kernel, log);
            // The three equipment facts read the same authority gate and publish through the same registration.
            // The push half is built here rather than inside the melee observer because a push is not a swing:
            // the melee row skips exactly the branch this one publishes.
            session.Events = EquipmentEventFacts.Attach(session.Adapter, () => kernel, log);
            // The reload and inventory observers behind the reload family's hooks: they publish through the same
            // equipment registration, and their hook list is installed with the rest by the caller's own loop.
            ReloadInventoryHooks.Install(kernel, session.Identity, session.Adapter, log);
            // The holder tier's own provider: the reload and clip-set rows are registered here, and the channel
            // statics below are what its session resolver and its two handlers answer through.
            session.Holder = WeaponHolderModule.Register(kernel, logLevel, players, report, note);
            WeaponHolderChannel.Adapter = session.Adapter;
            WeaponHolderChannel.CanExecute = () => !session.Down && canExecuteOwner();
            // One reading of the package data, before any hook can ask for it; a rejected file is refused on its
            // own and reported once, and the process keeps working with the files that were accepted. The
            // gear-part poses are projected out of the very same documents, so there is one source for every
            // authored Forge block field and no second file family.
            var fields = WeaponFieldDocuments.Load(gearPartsRoot, report);
            session.Fields = session._baseFields = fields;
            NativeFieldApplierReady(fields, log);
            // The parts the author added are hung by this build's own spawner, which reads the game's part data
            // blocks and asset shards.
            session.GearParts = new GearPartTransformApplier(GearPartTransformData.Project(fields, report), report, note, log,
                new GearPartExtraAssets(report, note));
            session.Models = WeaponModelAssemblies.Empty(report, log);
            session.Flashlights = new GearPartFlashlightApplier(GearPartFlashlightData.Project(fields, report), report, log);
            session.FireNoise = new WeaponFireNoise(WeaponFireNoiseData.Project(fields, report), report, log);
            NativeThermalSights(fields, report, log);
            // The loadout policies are read from disk once, in this same pre-hook window and from the same install
            // root: a file that fails its own pins or bytes is refused and reported on its own, and the process
            // keeps working with the policies that were accepted. The hook bodies are reached only through `Guard`,
            // which stays inert until this assignment, so a hook cannot run over a half-built session.
            var loadouts = LoadoutPolicyData.Load(gearPartsRoot, LoadoutPolicyWiring.PinSource(),
                message => report(message));
            session.Loadout = new GearLoadoutSession(loadouts, report, log);
            session.NativeDeviceWriters(kernel, players);
            session.NativeContentRegister();
            Current = session;
            session.ApplyDeclarations(installHooks(), ForgeEngine.GameBindings.NativeHookRegistration.WorldEpoch);
            // The per-frame bodies the authored data needs go on now; with no such data they never do.
            if (session.FireNoise.BlockCount != 0) WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.NoiseSetter);
            NativeScannerHooks(fields);
            if ((fields.EntryCount | session.GearParts.BlockCount | session.Models.Count | session.Flashlights.BlockCount | session.FireNoise.BlockCount
                    | loadouts.PolicyCount) != 0
                && log.Logs(RuntimeLogLevel.Info))
                log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ContentLoaded, new RuntimeLogFacts
                {
                    Values = new[]
                    {
                        RuntimeLogValue.Of("fieldObjects", (long)fields.EntryCount),
                        RuntimeLogValue.Of("gearPartBlocks", (long)session.GearParts.BlockCount),
                        RuntimeLogValue.Of("modelAssemblies", (long)session.Models.Count),
                        RuntimeLogValue.Of("flashlightBlocks", (long)session.Flashlights.BlockCount),
                        RuntimeLogValue.Of("fireNoiseBlocks", (long)session.FireNoise.BlockCount),
                        RuntimeLogValue.Of("loadoutPolicies", (long)loadouts.PolicyCount)
                    }
                });
            session._unwired = false;
            return session;
        }
        catch (Exception original)
        {
            session._faulted = true;
            var errors = new List<Exception>();
            if (session.Loadout != null) DomainSession.Cleanup(session.Loadout.Dispose, errors);
            // The observers and the holder registration go with the session: a start that failed after the holder
            // was registered must not leave a provider behind that no session answers for.
            DomainSession.Cleanup(ClearChannel, errors);
            DomainSession.Cleanup(ReloadInventoryHooks.Clear, errors);
            if (session.Events != null) DomainSession.Cleanup(session.Events.Dispose, errors);
            if (session.Melee != null) DomainSession.Cleanup(session.Melee.Dispose, errors);
            if (session.Facts != null) DomainSession.Cleanup(session.Facts.Dispose, errors);
            // The combat facts go with the session for the same reason the other observers do: a start that
            // failed after they were built must not leave a store behind that no session answers for, and the
            // shots the shot observer correlated are given up with it for the same reason.
            if (session.ClientFacts != null) DomainSession.Cleanup(session.ClientFacts.Dispose, errors);
            if (session.CombatFacts != null) DomainSession.Cleanup(session.CombatFacts.Dispose, errors);
            if (session.Holder != null) DomainSession.Cleanup(session.Holder.Dispose, errors);
            if (session.Identity != null) DomainSession.Cleanup(session.Identity.Dispose, errors);
            if (session.HitContexts is { } hits) DomainSession.Cleanup(() => HitContextReads.Detach(hits), errors);
            if (session.Reads is { } reads) DomainSession.Cleanup(() => InventoryQueryReads.Detach(reads), errors);
            if (session.Gates is { } gates) DomainSession.Cleanup(() => WeaponGateReads.Detach(gates), errors);
            if (session.ProfileReads is { } profileReads) DomainSession.Cleanup(() => WeaponProfileReads.Detach(profileReads), errors);
            if (session.DebugSwitches is { } switches) DomainSession.Cleanup(() => WeaponDebugSwitchWrites.Detach(switches), errors);
            // The profile's attached body goes before the half it answers through, so no command can reach a
            // source that is being given up; the detonation's body follows the same order for the same reason.
            DomainSession.Cleanup(() => DetachProfile(session), errors);
            DomainSession.Cleanup(() => DetachDetonate(session), errors);
            DomainSession.Cleanup(() => NativeShotDetach(), errors);
            DomainSession.Cleanup(() => session.NativeDeviceDetach(), errors);
            DomainSession.Cleanup(() => session.NativeContentDetach(), errors);
            if (ReferenceEquals(Current, session)) Current = null;
            session._disposed = true;
            if (errors.Count != 0) DomainSession.PreserveCleanupFailure(original, "ForgeWeapon.CleanupFailures", new AggregateException(errors), report);
            throw;
        }
    }

    /// <summary>
    /// One equipment life ended. The writes this package made on that instance end with it: the whole-profile
    /// half gives up its own changes on the reference — writer identity, block restore and handle — and the
    /// ledger entry that the named rows write through is reverted as well, so an instance the game hands to a
    /// later life starts from its own block instead of this package's clone.
    ///
    /// The applier is asked after the ledger, and unconditionally: a write whose readback failed is recorded in no
    /// ledger entry at all, so the applier can own recovery state the ledger revert never reaches. `Restore` is
    /// idempotent — an entry the ledger revert already put back is gone, and the call is then the no-op success it
    /// is for an instance that carries nothing — so this is a retry and not a second cleanup. A life whose revert
    /// still cannot be confirmed is reported and stays owned, which is what keeps an unresolved clone from being
    /// silently inherited by the next life.
    /// </summary>
    private void EndOverridesAtLifeEnd(EntityReference reference)
    {
        NativeShotLifeEnded(reference);
        var (reverted, restored, ledgerCode, restoreCode) = RevertOverrides(reference);
        if (!reverted)
            _report("weapon.equipment-life-revert-unconfirmed id=" + (reference.Id ?? "") + " code=" + ledgerCode);
        if (!restored && reverted)
            _report("weapon.equipment-life-restore-unconfirmed id=" + (reference.Id ?? "") + " code=" + restoreCode);
    }

    /// <summary>The whole instance is put back first. A profile writer's own ending replaces the surviving writers' merged fields
    /// through a new write on the weapon, and the weapon of an ended life is no longer the current instance (`stale-entity`);
    /// once the ledger entry is gone that ending finds nothing left to write and only releases its identity and handle. The
    /// applier may own a failed write which never entered the ledger, and a second attempt may confirm a transient first
    /// failure, so that confirmation is reconciled before the original writer's receipt is released.</summary>
    internal (bool Reverted, bool Restored, string LedgerCode, string RestoreCode) RevertOverrides(EntityReference reference)
    {
        bool reverted = Overrides.Ledger.Revert(reference, out var ledgerCode);
        bool restored = Overrides.Restore(reference, out var restoreCode);
        if (!reverted && restored) reverted = Overrides.Ledger.Revert(reference, out ledgerCode);
        Profiles?.EndFor(reference);
        OverrideActions.EndFor(reference, reverted && restored, !reverted ? ledgerCode : restoreCode);
        return (reverted, restored, ledgerCode, restoreCode);
    }

    /// <summary>Refuses native holder obligations that the game cannot yet uniquely identify after recall.</summary>
    private static ProviderHandoverAdoption PrepareNativeHandover(JsonElement context)
    {
        // Registering the provider's data contract must not accidentally make its existing native/callback
        // handles portable. Profile, fire-rate and shot-rule handles still need a real native rebind.
        if (context.GetProperty("handles").GetArrayLength() != 0)
            throw new RuntimeContractException("migration-native-handle",
                "Weapon native handles cannot be uniquely reconstructed after native recall.");
        // A promoted host can hold only the authority's portable proxy/effect metadata while the native
        // baseline still belongs to another peer. The source's unfinished applications are obligations too.
        if (HasUnfinishedNativeApplications(context.GetProperty("owner").GetProperty("issued")))
            throw new RuntimeContractException("migration-provider-data",
                "An outstanding weapon holder application has no proven native handover.");
        foreach (var name in new[] { "source", "retained" })
        {
            var side = context.GetProperty(name);
            // Absence is not an empty native ledger. A predecessor without this capture contract cannot
            // prove that no baseline, writer or failed cleanup must survive the recalled world.
            if (side.ValueKind != JsonValueKind.Object
                || !side.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.String
                || schema.GetString() != "forge.weapon.native-obligations"
                || !side.TryGetProperty("payload", out _))
                throw new RuntimeContractException("migration-provider-data",
                    "Weapon handover requires explicit source and retained native ownership evidence.");
            var payload = side.GetProperty("payload");
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("nativeOverrides", out var native)
                || native.ValueKind != JsonValueKind.Number || !native.TryGetInt32(out var nativeCount) || nativeCount != 0
                || !payload.TryGetProperty("writers", out var writers)
                || writers.ValueKind != JsonValueKind.Number || !writers.TryGetInt32(out var writerCount) || writerCount != 0)
                throw new RuntimeContractException("migration-provider-data",
                    "Weapon override baselines and writer ownership cannot be uniquely reconstructed after native recall.");
            var properties = 0;
            foreach (var property in payload.EnumerateObject())
            {
                properties++;
                if (property.Name is not ("nativeOverrides" or "writers"))
                    throw new RuntimeContractException("migration-provider-data", "Unknown weapon native handover state.");
            }
            if (properties != 2)
                throw new RuntimeContractException("migration-provider-data", "Repeated weapon native handover state.");
        }
        return new ProviderHandoverAdoption(Array.Empty<ProviderHandleAdoption>(), static () => { }, static () => { });
    }

    private static bool HasUnfinishedNativeApplications(JsonElement ledger)
    {
        // The kernel validated these compact request/result tables before invoking provider preflight.
        var templates = ledger.GetProperty("templates");
        var results = ledger.GetProperty("results");
        bool Confirmed(JsonElement index) => index.ValueKind != JsonValueKind.Null
            && results[index.GetInt32()].GetProperty("status").GetString() == CommandStatuses.Succeeded
            && results[index.GetInt32()].GetProperty("committed").GetString() == CommitStates.Confirmed;
        bool Ended(JsonElement entry, string slot)
        {
            foreach (var control in entry.GetProperty("controls").EnumerateArray())
            {
                var request = templates[control.GetProperty("request").GetProperty("template").GetInt32()];
                if (request.GetProperty("data").GetProperty("cleanupSlot").GetString() == slot
                    && Confirmed(control.GetProperty("result"))) return true;
            }
            foreach (var ending in entry.GetProperty("endings").EnumerateArray())
                if (ending.GetProperty("slot").GetString() == slot && Confirmed(ending.GetProperty("result"))) return true;
            return false;
        }
        foreach (var entry in ledger.GetProperty("entries").EnumerateArray())
        {
            if (entry.GetProperty("suppressed").GetBoolean()) continue;
            var request = templates[entry.GetProperty("request").GetProperty("template").GetInt32()];
            if (request.GetProperty("provider").GetString() != ModuleDefinition.ProviderId) continue;
            var binding = request.GetProperty("bindingId").GetString();
            if (binding != WeaponOverrideContract.FireRateBinding && binding != WeaponOverrideContract.PropertyBinding
                && binding != WeaponOverrideContract.SpreadBinding && binding != WeaponOverrideContract.RecoilBinding
                && binding != WeaponProfileContract.BindingId && binding != ShotRuleContract.BindingId) continue;
            var resultIndex = entry.GetProperty("result");
            if (resultIndex.ValueKind == JsonValueKind.Null) return true; // The holder may already have applied it.
            var result = results[resultIndex.GetInt32()];
            if (result.GetProperty("committed").GetString() == CommitStates.None) continue;
            bool obligation = false;
            var terminal = Ended(entry, "cancel-command");
            foreach (var declaration in request.GetProperty("data").GetProperty("cleanupSlots").EnumerateArray())
            {
                var slot = declaration.GetString()!;
                if (slot == "restore")
                {
                    obligation = true;
                    if (!terminal && !Ended(entry, slot)) return true;
                }
                else if (slot.StartsWith("handle:", StringComparison.Ordinal)
                    && result.GetProperty("outputs").TryGetProperty(slot[7..], out var handle)
                    && handle.ValueKind == JsonValueKind.Object)
                {
                    obligation = true;
                    if (!terminal && !Ended(entry, slot)) return true;
                }
            }
            // Plain property/spread/recoil writes can own a baseline without returning a handle. A terminal
            // request with no restore slot cannot prove that baseline was restored; don't treat it as instant.
            if (!obligation) return true;
        }
        return false;
    }

    /// <summary>
    /// The instance-override ledger's own level lifecycle, observed through the identity session's one
    /// subscription. A world the game starts or ends is the unconditional revert ruling 84 makes it: the previous
    /// epoch's entries are given up and every instance still held is restored. A checkpoint reload is the same
    /// revert without a new epoch — the level's gear is rebuilt from the game's data, so an override the running
    /// world accepted no longer exists on any instance — and a runtime that fails or stops ends the world
    /// outright. Nothing here reads the game: the ledger keeps only what a plan asked for.
    ///
    /// The whole-profile half is handed the same events, because its own changes are the ledger's: a tick advance
    /// is what ends a profile whose duration has passed, a world boundary drops its table with the ledger's
    /// (the instances it named went with the level), and a checkpoint reload does the same because the ledger has
    /// already put every block back.
    /// </summary>
    private static void ObserveOverrides(WeaponOverrideLedger ledger, WeaponProfileActions? profiles, RuntimeLifecycleEvent value)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.WeaponLifecycle);
        if (value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged)
        {
            if (ledger.BeginWorld(value.Current.WorldEpoch)) ReloadSpeedSlots.ClearAll();
            profiles?.Forget();
            Current?.RenewDamageObservers(value.Current.WorldEpoch);
        }
        if (value.Kind == RuntimeLifecycleKind.CheckpointRestored)
        {
            ReloadSpeedSlots.ClearAll();
            ledger.RestoreAll();
            profiles?.Forget();
        }
        if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
        {
            ReloadSpeedSlots.ClearAll();
            ledger.EndWorld();
            profiles?.Forget();
        }
        if (value.Kind == RuntimeLifecycleKind.TickAdvanced)
        {
            profiles?.Sweep(value.Current.SimulationTick);
            ledger.Sweep(value.Current.SimulationTick);
        }
        NativeShotLifecycle(value);
        NativeInteractionLifecycle(value);
    }

    /// <summary>True when no hook body may run: the session is gone, faulted or still being wired, or the runtime it
    /// serves failed or stopped.</summary>
    internal bool Inert => _disposed || Down || _unwired || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped;

    internal void Guard(Action<WeaponNativeSession> callback)
    {
        DomainSession.CheckThread(_threadId, "Weapon");
        ArgumentNullException.ThrowIfNull(callback);
        if (Inert) return;
        try { callback(this); }
        catch (RuntimeContractException error) when (error.Code == PlayerDirectoryService.MissingCode)
        { RejectMissingPlayerDirectory(error.Code); }
        catch (Exception error) { Fault(error); }
    }

    // WHY: per-frame hooks pass their value as state to a static lambda, so no closure is allocated per frame.
    internal void Guard<T>(Action<WeaponNativeSession, T> callback, T state)
    {
        DomainSession.CheckThread(_threadId, "Weapon");
        ArgumentNullException.ThrowIfNull(callback);
        if (Inert) return;
        try { callback(this, state); }
        catch (RuntimeContractException error) when (error.Code == PlayerDirectoryService.MissingCode)
        { RejectMissingPlayerDirectory(error.Code); }
        catch (Exception error) { Fault(error); }
    }

    // WHY: a guarded read for a prefix; `inert` is returned when the session is inert or the read faulted.
    internal TResult Guard<T, TResult>(Func<WeaponNativeSession, T, TResult> callback, T state, TResult inert)
    {
        DomainSession.CheckThread(_threadId, "Weapon");
        ArgumentNullException.ThrowIfNull(callback);
        if (Inert) return inert;
        try
        {
            return callback(this, state);
        }
        catch (RuntimeContractException error) when (error.Code == PlayerDirectoryService.MissingCode)
        { RejectMissingPlayerDirectory(error.Code); return inert; }
        catch (Exception error) { Fault(error); return inert; }
    }

    /// <summary>The Player domain is not running when its own start failed. Refuse only the observation that needed it,
    /// and report the unresolved contract once so repeated native callbacks cannot flood the log.</summary>
    private void RejectMissingPlayerDirectory(string code)
    {
        if (_missingPlayerDirectoryReported) return;
        _missingPlayerDirectoryReported = true;
        try { Log.Reason(RuntimeLogLevel.Error, WeaponLogCodes.ObservationRejected, code); }
        catch (Exception logging) { LastReporterFailure = logging.GetType().Name; }
        try { _report(WeaponLogCodes.ObservationRejected + ": reason=" + code); }
        catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
    }

    private void Fault(Exception error)
    {
        // Contract rejections are handled per observation inside the adapter or the guard; anything reaching here
        // is unexpected and stops observation for the rest of this level rather than leaving partly tracked native
        // state live. The content, device and test halves stay attached: they are session-wide, every hook body is
        // inert while the fault holds, and the next world starts them again from their own world-change release.
        _worldFault.Mark(_kernel.WorldEpoch);
        var errors = new List<Exception>();
        DomainSession.Cleanup(Adapter.LevelSentries.Clear, errors);
        DomainSession.Cleanup(Adapter.Clear, errors);
        // The level-end boundary is guarded, so it will not run for this level: drop the holders it would have dropped.
        DomainSession.Cleanup(() => Models?.LevelEnded(), errors);
        // No reload started in this level can end through the inert hooks; the multipliers it armed are dropped
        // here rather than left for the game's getter to divide.
        ReloadSpeedSlots.ClearAll();
        LastFault = RuntimeDiagnostics.Describe(error);
        // Keep the originating native call site. A type/message alone cannot identify which collected wrapper
        // failed, and cleanup can itself produce unrelated native errors.
        Log.Text(RuntimeLogLevel.Error, "weapon.native-fault", error.ToString());
        try { _report("Weapon equipment observation disabled for this level: " + LastFault + "; cleanupErrors=" + errors.Count); }
        catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
    }

    public void Dispose()
    {
        DomainSession.CheckThread(_threadId, "Weapon");
        if (_disposed) return;
        Overrides.EndSession();
        // The whole-profile half gives its changes back through the ledger, so it goes before the registration
        // that named them: a handle cancelled after its module was unregistered would be a name for nothing. The
        // detonation holds no state at all and goes with it for the same one-session-one-answer reason.
        DetachProfile(this);
        DetachDetonate(this);
        // The shot rules and flights hold handles and projectile objects of this registration, so they are given
        // up before it is unregistered, for the same reason the profile half is.
        NativeShotDetach();
        // The bio-tracker writer leaves the switchboard while it can still put its scanner back.
        NativeDeviceDetach();
        NativeContentDetach();
        // Unregister first; Runtime rejects disposal during dispatch and the session stays intact.
        Identity.Dispose();
        // The holder provider is a second registration and goes with the first, before the hooks that could reach
        // it are removed.
        Holder.Dispose();
        _faulted = true;
        // Nothing answers the holder channel once the session is going away, so its statics are cleared: a
        // resolver asked during shutdown answers null rather than a half-disposed adapter.
        ClearChannel();
        // The reload and inventory observers answer nothing once the session is going away, and the hooks that
        // reach them are removed last, so a hook firing in that window finds no observer rather than a half-dead one.
        ReloadInventoryHooks.Clear();
        // The read rows' source is detached with the observers and before the adapter is cleared, so a plan
        // evaluating after this point refuses by code instead of reading a half-disposed adapter.
        if (HitContexts is { } hits) HitContextReads.Detach(hits);
        if (Reads is { } reads) InventoryQueryReads.Detach(reads);
        if (Gates is { } gates) WeaponGateReads.Detach(gates);
        if (ProfileReads is { } profileReads) WeaponProfileReads.Detach(profileReads);
        if (DebugSwitches is { } switches) WeaponDebugSwitchWrites.Detach(switches);
        ClientFacts.Dispose();
        CombatFacts.Dispose();
        Events.Dispose();
        Melee.Dispose();
        Facts.Dispose();
        // The shot correlations this session published into the cross-package table go with it, before the
        // equipment lives they name are dropped by `Adapter.Clear` below.
        // The narrowed gear pool is the game's own state, so it is put back before the hooks that could narrow it
        // again are removed; nothing is reported here because the process is going away.
        Loadout.Dispose();
        Adapter.LevelSentries.Clear();
        Adapter.Clear();
        var errors = new List<Exception>();
        _disposed = true;
        _damage?.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
        if (errors.Count != 0) throw new AggregateException("Weapon shutdown cleanup failed.", errors);
    }

    /// <summary>The whole-profile half's body and source, given up together: the body first, so no command can
    /// reach a source that is going away, and then the source itself, which puts back every change it still
    /// holds. A session that was never wired that far has nothing to give up.</summary>
    private static void DetachProfile(WeaponNativeSession session)
    {
        // The retirement hook goes first: a life that ends while this session is being given up must not reach a
        // half that is already detached. It is cleared unconditionally, because a session that never got as far as
        // attaching the profile half has nothing to detach but must still stop answering for the adapter.
        session.Adapter.LifeEnded = null;
        if (session.ProfileBody is { } body)
        {
            WeaponProfileWrites.Detach(body);
            session.ProfileBody = null;
        }
        if (session.Profiles is not { } profiles) return;
        WeaponProfileActions.Detach(profiles);
        session.Profiles = null;
    }

    /// <summary>The detonation's body and source, given up in the same order. The half holds no state of its own
    /// — a detonation is one call — so there is nothing to put back.</summary>
    private static void DetachDetonate(WeaponNativeSession session)
    {
        session._detachSentryRecall?.Invoke();
        session._detachSentryRecall = null;
        session._sentryRecall?.Dispose();
        session._sentryRecall = null;
        if (session.DetonateBody is { } body)
        {
            DetonateWrites.Detach(body);
            session.DetonateBody = null;
        }
        if (session.Detonations is not { } detonations) return;
        detonations.Dispose();
        session.Detonations = null;
    }

    /// <summary>The holder channel's statics, put back to the answers a machine with no session gives. It is one
    /// call because both teardown paths — a failed start and a disposal — have to leave the same inert state
    /// behind, and a resolver or handler reached afterwards must find no adapter to write through. The pending
    /// reload multipliers go with them: a multiplier belongs to a reload the holder channel started, so a channel
    /// that is going away leaves none armed for the game's own getter to keep dividing.</summary>
    private static void ClearChannel()
    {
        WeaponHolderChannel.Adapter = null;
        WeaponHolderChannel.CanExecute = () => false;
        ReloadSpeedSlots.ClearAll();
    }

}
