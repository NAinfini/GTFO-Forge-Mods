using ForgeEngine.Framework;

/// <summary>
/// The one offline registration of every provider the game registers: the Runtime's own contract modules first, then the
/// four domains of ForgeEngine in the order the game-side host starts them. The release export (the published runtime
/// manifest) and the offline package admission (the plans a package carries) both build their kernel here, so the
/// manifest a plan is compiled against and the registry an uploaded plan is loaded against are one registration.
/// Every body that belongs to the game-bound assembly is the refusing stand-in below: the rows, shapes and handler names
/// are the declarations' own, and nothing in either process dispatches a command.
/// </summary>
internal static class OfflineRuntime
{
    /// <summary>The host identity id: the kernel is constructed with it and the ForgeEngine package declares it as its
    /// provider id in release.json. The host also registers its own session binding under this id.</summary>
    internal const string HostProviderId = "forge.runtime";
    /// <summary>The reader name ForgeEngine hands its content out under (`ForgeEngine.Plugin.PluginGuid`).</summary>
    private const string EnginePluginGuid = "NAinfini.ForgeEngine";

    /// <summary>The runtime resources of one kind an install ships, as ForgeEngine's one content discovery hands them out
    /// (`ContentPackages.ReadResources`): reader name, kind and refusal codes in, the files out; <c>Refused</c> receives
    /// every file the game itself would refuse.</summary>
    internal sealed record Install(Func<string, string, PublishedResourceSetCodes, Action<string>, IReadOnlyList<PublishedResource>> Resources,
        Action<string> Refused);

    /// <summary>Registers every provider on <paramref name="kernel"/>. <paramref name="domain"/> sees each of the four
    /// domain modules before it is registered (the export checks the package it names against its release row).
    /// <paramref name="install"/> answers the kinds the game reads from the installed files rather than from the game
    /// (area fields and weapon property profiles: which ids exist and the revision each file holds), read exactly as
    /// the game reads them; without one those kinds have no instance and a plan naming one is `stale-resource`.
    /// <paramref name="audio"/> is the provider of the `audio` kind (the game-bound custom-sound library, which only a caller
    /// that can read the install's sound files has).</summary>
    internal static void Register(RuntimeKernel kernel, Func<RuntimeModule, RuntimeModule> domain, Install? install = null,
        RuntimeResourceProvider? audio = null)
    {
        // The area fields as the trigger host reads them (TriggerHost.Start, then AreaFieldPump at the world boundary).
        var fields = install == null ? ForgeEngine.Trigger.AreaFieldSession.Empty
            : new ForgeEngine.Trigger.AreaFieldSession(AreaFieldTable.Load(install.Resources(EnginePluginGuid, AreaFieldDocument.ResourceKind,
                AreaFieldCodes.Set, install.Refused), 0, install.Refused));
        // The weapon property profiles as the weapon session reads them (WeaponNativeSession.Start).
        var profiles = install == null ? null : ForgeWeapon.WeaponPropertyProfileTable.Load(install.Resources(EnginePluginGuid,
            ForgeWeapon.WeaponProfileContract.ProfileResourceKind, ForgeWeapon.WeaponPropertyProfileTable.SetCodes, install.Refused), install.Refused);
        // Replay the same Runtime-owned built-ins that GameRuntimeBridge registers in game. The configured log
        // level differs here, but no manifest row and no plan check depends on that level.
        kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        kernel.RegisterModule(VariableContracts.Module(), RuntimeLogLevel.Off);
        // Trigger/observation must still precede domain packages whose bindings name those rows.
        kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        kernel.RegisterModule(ObservationContracts.Module(), RuntimeLogLevel.Off);
        // The kernel's own two status rows travel with their bindings, exactly as the game-side bridge registers them:
        // the ending a card can mount on, and the dispel step the kernel walks itself.
        kernel.RegisterModule(StatusContracts.Module(), RuntimeLogLevel.Off);
        // The one binding the host observes itself (a player leaving the session), registered on the host's own
        // provider id exactly as GameRuntimeBridge registers it; its capability row is the trigger contract's above.
        kernel.RegisterModule(ForgeEngine.GameBindings.SessionContract.Module(), RuntimeLogLevel.Off);
        // The trigger provider is the Runtime package's own (TriggerHost): the game registers it on the kernel's built-in
        // path with the world's line-of-sight cast, which this process cannot offer, so the row is declared with a probe that refuses.
        kernel.RegisterModule(ForgeEngine.Trigger.ModuleDefinition.Create(fields,
            (_, _, _, _, _, _, _) => throw new InvalidOperationException("This offline process does not cast the line-of-sight row.")), RuntimeLogLevel.Off);
        // The shared presentation provider is the Runtime package's own too (PresentationHost): its rows and bindings are the
        // declaration's, and every body is the refusing stand-in, because the bodies draw in the game.
        kernel.RegisterModule(ForgeEngine.Presentation.PresentationContract.Module(ForgeEngine.Presentation.PresentationContract.Describe()
            .CommandHandlers.ToDictionary(handler => handler, RefusingHandler, StringComparer.Ordinal),
            resources: audio == null ? null : new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
            {
                [ForgeEngine.Presentation.PresentationContract.AudioResourceKind] = audio
            }), RuntimeLogLevel.Off);
        // Player goes ahead of Map and Weapon, whose rows name the player it owns. The rows whose duration is the plan's own
        // `effect` block carry the restore callback the game session registers (`PlayerPluginSession`, `EnemyModule`,
        // `WeaponNativeSession`); a row without one may only carry a periodic effect, so each is declared with the stand-in.
        kernel.RegisterModule(domain(PlayerModule() with { EffectRestores = Restores(ForgePlayer.AgentModifierContract.ApplyHandlerName) }), RuntimeLogLevel.Off);
        // The Forge block fields the map package's field appliers write, declared from the same list the
        // game declares at registration (`MapFieldHost.Start`). The mount and resource kinds are the ones the game-side
        // map session adds (`MapObjectModule`, `MapPluginSession.Wiring`), declared on the same provider.
        ForgeMap.ModuleDefinition.DeclareDefinitionFields(kernel.RegisterModule(domain(MapModule() with
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
            {
                ["map-object"] = DeclaredSubject,
                ["player"] = DeclaredSubject,
                ["level"] = DeclaredScope
            },
            ResourceProviders = Declared("map", ForgeEngine.Framework.RuntimeZones.ResourceKind, ForgeMap.GeneratorContract.GeneratorResourceKind,
                ForgeMap.LevelObjectiveValueContract.ObjectiveResourceKind, ForgeMap.AlarmWaveContract.ChainedPuzzleKind,
                ForgeMap.AlarmWaveContract.WaveKind, ForgeMap.ItemSpawnContract.ItemResourceKind, ForgeMap.TextResourceContract.ResourceKind)
        }), RuntimeLogLevel.Off));
        // The Forge block fields the enemy domain's own profile appliers write, declared on the handle the registration
        // just returned while the window is still open. The game declares exactly this list from the same constant
        // (`EnemyModule`'s constructor), and adds the `enemy-type` mount and the `ability` and `enemy` resource kinds there.
        ForgeEnemy.ModuleDefinition.DeclareDefinitionFields(kernel.RegisterModule(domain(EnemyModule() with
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal) { ["enemy-type"] = DeclaredSubject },
            ResourceProviders = Declared("ability", "enemy"),
            EffectRestores = Restores(ForgeEnemy.EnemyVolumeContract.VolumeHandler, ForgeEnemy.CombatDamageRuleContract.HandlerName,
                ForgeEnemy.GlueContract.FoamingHandler, ForgeEnemy.EnemyNativeStateContract.Handler(ForgeEngine.Framework.Blocks.ActionEnemyLimbSet.Id),
                ForgeEnemy.Native.EnemyProfileContract.PhaseSetHandler, ForgeEnemy.EnemyControlContract.MoveToHandler,
                ForgeEnemy.EnemyStationaryContract.Handler, ForgeEnemy.EnemyWakeLockContract.Handler)
        }), RuntimeLogLevel.Off));
        // The inventory row is give, whose negative quantity spends — `drop` declares no row at all (ruling 110.5) — and
        // the three override rows travel as the one handler table the contract builds.
        var weapon = kernel.RegisterModule(domain(ForgeWeapon.ModuleDefinition.Create(
            ammoAdd: RefusingHandler(ForgeWeapon.WeaponSupplyContract.AmmoAddHandler),
            overrides: ForgeWeapon.WeaponOverrideContract.Handlers(
                RefusingHandler(ForgeWeapon.WeaponOverrideContract.FireRateHandler),
                RefusingHandler(ForgeWeapon.WeaponOverrideContract.SpreadHandler),
                RefusingHandler(ForgeWeapon.WeaponOverrideContract.RecoilHandler),
                RefusingHandler(ForgeWeapon.WeaponOverrideContract.PropertyHandler)),
            inventoryGive: RefusingHandler(ForgeWeapon.InventoryActionContract.GiveHandler),
            equipGear: RefusingHandler(ForgeWeapon.GearEquipContract.Handler),
            projectileLaunch: RefusingHandler(ForgeWeapon.CombatPrimitiveContract.ProjectileLaunchHandler),
            hitscan: RefusingHandler(ForgeWeapon.HitscanShotContract.Handler),
            shotRule: RefusingHandler(ForgeWeapon.ShotRuleContract.Handler),
            bulletProjectile: RefusingHandler(ForgeWeapon.BulletProjectileContract.Handler),
            // The `weapon` and `tool` kinds and the `gear-block` mount the game-side weapon session adds
            // (`WeaponNativeSession`, `EquipmentIdentitySession`), declared on the same provider. The `profile` kind is
            // read from the install exactly as the game session reads it, so each compiled reference is checked
            // against the revision the installed file holds.
            gearResources: Declared(ForgeWeapon.GearEquipContract.WeaponKind, ForgeWeapon.GearEquipContract.ToolKind),
            profiles: profiles,
            overrideRestores: Restores(ForgeWeapon.WeaponOverrideContract.PropertyHandler, ForgeWeapon.WeaponOverrideContract.FireRateHandler,
                ForgeWeapon.WeaponOverrideContract.SpreadHandler, ForgeWeapon.WeaponOverrideContract.RecoilHandler)) with
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
            {
                [ForgeWeapon.ModuleDefinition.GearBlockAttachmentKind] = AttachmentMatcherRegistration.BySubject(static (_, _, _) => false, static _ => static _ => null)
            }
        }), RuntimeLogLevel.Off);
        // The game declares exactly this list from the same constant at its own registration (`EquipmentIdentitySession`).
        weapon.DeclareDefinitionFields(ForgeWeapon.WeaponDefinitionFields.All);
        // The holder tier is a second provider inside the weapon domain. Its session resolver and its two bodies belong
        // to the game-bound assembly, so the declaration-only shape is what is registered here.
        kernel.RegisterModule(domain(ForgeWeapon.WeaponHolderActionsContract.Module(
            holders: null,
            reload: RefusingHandler(ForgeWeapon.WeaponHolderActionsContract.ReloadHandler),
            clipSet: RefusingHandler(ForgeWeapon.WeaponHolderActionsContract.ClipSetHandler),
            autoFire: RefusingHandler(ForgeWeapon.WeaponHolderActionsContract.AutoFireHandler))), RuntimeLogLevel.Off);
    }

    /// <summary>The game-independent registration the Player plugin builds as well, every body replaced by the stand-in.</summary>
    private static RuntimeModule PlayerModule()
    {
        var description = ForgePlayer.ModuleDefinition.Describe();
        return ForgePlayer.ModuleDefinition.Create(description.CommandHandlers.ToDictionary(handler => handler, RefusingHandler, StringComparer.Ordinal),
            description.EvaluatorHandlers.ToDictionary(handler => handler, RefusingEvaluator, StringComparer.Ordinal));
    }

    /// <summary>The game-independent registration the game-side map session builds as well: the rows, capability rows,
    /// support lines, shape table and handler names are the shared declaration's own.</summary>
    private static RuntimeModule MapModule()
    {
        var description = ForgeMap.ModuleDefinition.Describe();
        return ForgeMap.ModuleDefinition.Create(description.CommandHandlers.ToDictionary(handler => handler, RefusingHandler, StringComparer.Ordinal),
            description.EvaluatorHandlers.ToDictionary(handler => handler, RefusingEvaluator, StringComparer.Ordinal));
    }

    /// <summary>The real Enemy declaration and public factory, with the refusing bodies.</summary>
    private static RuntimeModule EnemyModule()
    {
        var description = ForgeEnemy.ModuleDefinition.Describe();
        return ForgeEnemy.ModuleDefinition.Create(
            description.CommandHandlers.ToDictionary(handler => handler, RefusingHandler, StringComparer.Ordinal),
            description.EvaluatorHandlers.ToDictionary(handler => handler, RefusingEvaluator, StringComparer.Ordinal),
            static _ => true);
    }

    /// <summary>The owner of resource kinds only a game-bound half can read: the kind is declared on the provider the game
    /// registers it on, and which ids exist (and which revision each holds) is the game's answer when a level loads.
    /// Offline every id of such a kind resolves, so a plan is never refused for a world this process does not have.</summary>
    private static Dictionary<string, RuntimeResourceProvider> Declared(params string[] kinds)
        => kinds.ToDictionary(kind => kind, kind => RuntimeResourceProvider.Of(Array.Empty<ResourceRef>, id => new ResourceRef(kind, id)), StringComparer.Ordinal);

    /// <summary>A mount kind only a game-bound half can compare: declared on its provider, matching nothing here,
    /// because nothing in this process dispatches an event.</summary>
    private static readonly AttachmentMatcherRegistration DeclaredSubject = AttachmentMatcherRegistration.BySubject(static (_, _, _) => false);
    private static readonly AttachmentMatcherRegistration DeclaredScope = AttachmentMatcherRegistration.ByScope(static (_, _) => false);

    /// <summary>The body a handler whose implementation lives in the game-bound assembly is registered with here: it
    /// refuses the moment it would run. An invented body would be a second implementation.</summary>
    internal static CommandHandler RefusingHandler(string handler) => _ =>
        throw new InvalidOperationException("This offline process does not execute the " + handler + " handler.");

    /// <summary>The restore callbacks of the rows whose duration is the plan's own `effect` block, each the refusing
    /// stand-in: this process ends no effect, but a plan may carry one exactly where the game would let it.</summary>
    private static Dictionary<string, EffectRestoreHandler> Restores(params string[] handlers)
        => handlers.ToDictionary(handler => handler, handler => (EffectRestoreHandler)(_ =>
            throw new InvalidOperationException("This offline process does not restore the " + handler + " effect.")), StringComparer.Ordinal);

    /// <summary>The evaluator stand-in: this process answers no query at all.</summary>
    internal static EvaluatorHandler RefusingEvaluator(string handler) => (_, _) =>
        throw new InvalidOperationException("This offline process does not evaluate the " + handler + " row.");
}
