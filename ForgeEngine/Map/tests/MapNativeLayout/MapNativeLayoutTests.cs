using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ForgeMap.Tests.MapNativeLayout;

// Compiled-assembly metadata only: proves boundaries, plugin identity and hook shape, not native call timing.
// Inputs come from the environment (see ConfiguredInputs); the suite is excluded by the Native trait by default.
[Trait("Category", "Native")]
public sealed class MapNativeLayoutTests
{
    private static readonly string BepInEx = Environment.GetEnvironmentVariable("FORGE_MAP_BEPINEX")
        ?? Environment.GetEnvironmentVariable("GTFO_BEPINEX_PATH") ?? "";
    private static readonly string Artifacts = Environment.GetEnvironmentVariable("FORGE_ARTIFACTS") ?? "";
    private static readonly string Spec = Environment.GetEnvironmentVariable("FORGE_MAP_HOOK_SPEC")
        ?? Path.Combine("ForgeEngine", "Map", "evidence", "map-hooks.json");

    /// <summary>The resolver every assembly of this audit is opened with. A hook's `[HarmonyPatch]` attribute
    /// carries a `HarmonyLib.ArgumentType[]`, so Cecil has to resolve `0Harmony` — which sits in `BepInEx/core`
    /// beside the interop assemblies — while it decodes the attribute's constructor arguments. It is one
    /// resolver for the whole audit and it is never disposed, because a patch row is read after the assembly it
    /// came from is released.</summary>
    private static readonly DefaultAssemblyResolver Resolver = CreateResolver();

    private static DefaultAssemblyResolver CreateResolver()
    {
        var resolver = new DefaultAssemblyResolver();
        if (!string.IsNullOrWhiteSpace(BepInEx))
        {
            resolver.AddSearchDirectory(Path.Combine(BepInEx, "core"));
            resolver.AddSearchDirectory(Path.Combine(BepInEx, "interop"));
        }
        return resolver;
    }

    /// <summary>The reader parameters every `ReadAssembly` of this suite uses: the one resolver above, so an
    /// attribute's own type arguments resolve instead of throwing `AssemblyResolutionException`.</summary>
    private static ReaderParameters Reader() => new() { AssemblyResolver = Resolver };

    /// <summary>Opens one assembly with the reader above. The resolver is the one thing that makes a
    /// `[HarmonyPatch]` attribute decodable at all, and every assembly here is read with it.</summary>
    private static AssemblyDefinition Read(string path) => AssemblyDefinition.ReadAssembly(path, Reader());

    private static string Artifact(string relative) => Path.Combine(Artifacts, relative);

    private static void RequireInputs()
    {
        Assert.False(string.IsNullOrWhiteSpace(BepInEx) || string.IsNullOrWhiteSpace(Artifacts),
            "Set FORGE_MAP_BEPINEX (or GTFO_BEPINEX_PATH) and FORGE_ARTIFACTS to an existing build output.");
    }

    [Fact]
    public void map_native_layout_checks_all_pass()
    {
        RequireInputs();
        var checks = Audit();
        Dump(checks);
        string failed = string.Join(" | ", checks.Where(c => !c.Passed).Select(c => c.Id + ": " + c.Detail));
        Assert.True(checks.Any(c => c.Id == "native.no-gameplay-gate"), "The audit did not run to completion: " + failed);
        Assert.True(checks.Count(c => !c.Passed) == 0, failed);
        Assert.True(checks.Count >= 30, "Expected the full layout set, observed " + checks.Count + " checks.");
    }




    // Mutations start with the real compiled literal arrays. The reader must fail closed rather than
    // executing a factory, guessing an overload, accepting a same-named foreign type or losing a slot.




    private static List<CheckRow> Audit(string? configuredSpec = null)
    {
        var checks = new List<CheckRow>();
        void Check(string id, bool passed, string detail = "") => checks.Add(new(id, passed, detail));
        IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
        using var sdk = Read(Artifact("bin/ForgeEngine.Framework/release/ForgeEngine.Framework.dll"));
        using var host = Read(Artifact("bin/ForgeEngine/release/ForgeEngine.dll"));
        using var modules = Read(Path.Combine(BepInEx, "interop", "Modules-ASM.dll"));
        using var snet = Read(Path.Combine(BepInEx, "interop", "SNet_ASM.dll"));
        using var data = Read(Path.Combine(BepInEx, "interop", "GameData-ASM.dll"));
        // The asset table the room resolver loads an authored room reference through: the fourth game assembly
        // this module reads, and the one its readback row has to resolve against.
        using var shards = Read(Path.Combine(BepInEx, "interop", "Shards-ASM.dll"));
        // The state-machine base the movement-state query reads its entry time from, for the same reason.
        using var utils = Read(Path.Combine(BepInEx, "interop", "Utils-ASM.dll"));
        // The point emitter the positioned sound row posts through, for the same reason.
        using var sound = Read(Path.Combine(BepInEx, "interop", "CellSound-ASM.dll"));
        using var wwise = Read(Path.Combine(BepInEx, "interop", "Wwise-ASM.dll"));
        using var dropServer = Read(Path.Combine(BepInEx, "interop", "DropServerSDK-ASM.dll"));
        using var specDocument = JsonDocument.Parse(File.ReadAllText(configuredSpec ?? Spec));
        var specHooks = specDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
        var specReadbacks = specDocument.RootElement.GetProperty("readbacks").EnumerateArray().ToArray();
        // The action layer's write entries are declared the same way the reads are: one row per game member, in
        // the same spec, each documented by its own member row in evidence/door-terminal-hooks.json.
        var specWrites = specDocument.RootElement.GetProperty("writes").EnumerateArray().ToArray();
        var nativeTypes = ForgeTestInputs.DomainTypes.Native(host, sdk, "Map");
        var mapTypes = ForgeTestInputs.DomainTypes.Managed(sdk, "Map");
        var gameTypes = modules.MainModule.Types.Concat(snet.MainModule.Types).Concat(data.MainModule.Types)
            .Concat(shards.MainModule.Types).Concat(utils.MainModule.Types).Concat(sound.MainModule.Types).Concat(wwise.MainModule.Types).Concat(dropServer.MainModule.Types)
            .SelectMany(Walk).ToArray();
        bool GameAssembly(string name) => name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("BepInEx", StringComparison.Ordinal)
            || name.Contains("Harmony", StringComparison.Ordinal) || name.StartsWith("Il2Cpp", StringComparison.Ordinal) || name.EndsWith("-ASM", StringComparison.Ordinal) || name.EndsWith("_ASM", StringComparison.Ordinal);
        var instructions = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
        var calls = instructions.Select(i => i.Operand).OfType<MethodReference>()
            .Concat(nativeTypes.SelectMany(t => t.Methods).SelectMany(m => EvidenceRegenerator.MethodInfoCalls(m, gameTypes))).ToArray();
        string Scope(MethodReference m) => m.DeclaringType.Scope is AssemblyNameReference scope ? scope.Name : "";
        string Member(MethodReference m) => (m.DeclaringType is GenericInstanceType g ? g.ElementType.FullName : m.DeclaringType.FullName) + "::" + m.Name;
        // The package owns no per-frame entry at all: its per-tick native work is a callback of Runtime's frame phase
        // (`DomainWorkPhases`), so no component with an Update/FixedUpdate/LateUpdate exists in this assembly.
        Check("native.no-update-loop", !nativeTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"),
            string.Join(", ", nativeTypes.SelectMany(t => t.Methods).Where(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate")
                .Select(m => m.DeclaringType.Name + "::" + m.Name)));

        // One provider identity: the native plugin composes ForgeMap's compiled registration instead of declaring a
        // second registry. Compare ids carried by the two built assemblies; production source text is not evidence.
        var moduleIds = instructions.Select(i => i.Operand).OfType<string>()
            .SelectMany(text => Regex.Matches(text, @"forge\.module\.[A-Za-z0-9_.]+").Select(m => m.Value))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var managedModuleIds = mapTypes.SelectMany(t => t.Fields).Where(f => f.HasConstant).Select(f => f.Constant).OfType<string>()
            .Concat(mapTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Select(i => i.Operand).OfType<string>())
            .SelectMany(text => Regex.Matches(text, @"forge\.module\.[A-Za-z0-9_.]+").Select(m => m.Value))
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var inventedIds = moduleIds.Where(id => !managedModuleIds.Contains(id)).ToArray();
        Check("native.single-provider-source", calls.Any(m => Member(m) == "ForgeMap.ModuleDefinition::Create")
            && !instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference c && c.DeclaringType.FullName == "ForgeEngine.Framework.RuntimeModule")
            && inventedIds.Length == 0,
            inventedIds.Length == 0 ? "" : "ids absent from built ForgeMap: " + string.Join(", ", inventedIds));
        // Map declares no player identity. It acquires Runtime's late directory proxy at startup, so a Player
        // provider may register after Map and every actual read still resolves the current service.
        var stringPlayerLookups = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody
                && m.Body.Instructions.Any(i => i.Operand is MethodReference r && Member(r) is
                    "ForgeEngine.Framework.IForgeEngine::ResolveEntityInstance" or "ForgeEngine.Framework.RuntimeKernel::ResolveEntityInstance")
                && m.Body.Instructions.Any(i => i.Operand is string s && s == "gtfo.player"))
            .Select(m => m.DeclaringType.Name + "::" + m.Name).ToArray();
        Check("native.no-player-identity", !nativeTypes.Any(t => t.Name is "PlayerIdentityModule" or "PlayerSessions" or "PlayerSelector")
            && !mapTypes.Any(t => t.Name is "PlayerStateContract" or "PlayerSelectorContract")
            && !calls.Any(m => m.DeclaringType.Name == "PlayerIdentityModule")
            && stringPlayerLookups.Length == 0,
            string.Join(", ", stringPlayerLookups));
        Check("native.no-gameplay-gate", !calls.Any(m => m.Name == "get_CanExecuteGameplay"));
        // SNet_Player.Lookup is a Steam64 account ID: compared and used as a private dictionary key, never formatted or boxed.
        Check("native.account-lookup-never-formatted", !calls.Any(m => m.DeclaringType.FullName == "System.UInt64" && m.Name == "ToString")
            && !instructions.Any(i => i.OpCode == OpCodes.Box && i.Operand is TypeReference t && t.FullName == "System.UInt64")
            && !calls.Any(m => m is GenericInstanceMethod g && g.GenericArguments.Any(a => a.FullName == "System.UInt64"))
            && !calls.Any(m => m.Parameters.Any(p => p.ParameterType.FullName == "System.UInt64") && m.DeclaringType.FullName is "System.String" or "System.Convert"));

        // Native state is read, and the only writes are the action layer's declared entries below: identity reads
        // only the player key and agent links, and no name or slot is ever read or written.
        var gameCalls = calls.Where(m => GameAssembly(Scope(m)) && !Scope(m).StartsWith("BepInEx", StringComparison.Ordinal)
                && !Scope(m).Contains("Harmony", StringComparison.Ordinal))
            .Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // The classifiers are EvidenceRegenerator's: the generator writes the rows this audit then checks, so
        // a member the two disagreed about would be a row written and refused in the same run. There is one
        // definition of each and both sides read it.
        var engineCalls = gameCalls.Where(EvidenceRegenerator.Engine).ToArray();
        // The write spelling: a non-getter game call that is not one of the named pure queries. The door and
        // terminal execution layer is a write layer by definition, so these calls are declared one by one below
        // and every one of them has to be declared: a non-getter call no row names is a write nobody reviewed.
        // The engine's own members are not part of that set — the read set below subtracts them for the same
        // reason — so the comparison is between the game calls and the rows, both sides engine-free.
        var writes = gameCalls.Where(c => !EvidenceRegenerator.Engine(c)).Where(c =>
        {
            var name = c[(c.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
            return !name.StartsWith("get_", StringComparison.Ordinal)
                && !EvidenceRegenerator.ReadSpellings.Contains(name) && !EvidenceRegenerator.QueryMembers.Contains(c);
        }).ToArray();
        // Which game member a readback or write row names is resolved against the interop assemblies by member
        // name along the declaring type's base chain: a member named on a derived type may be declared on its
        // base. Types are compared by name, never resolved, because the interop assemblies reference game
        // assemblies this audit does not open. The resolved member — not the spec's spelling — is what each set
        // is compared against. Walking the chain in order makes the first declaration found the most derived
        // one, which is the declaration a call site on the named type binds to.
        IEnumerable<TypeDefinition> Chain(TypeDefinition declared)
        {
            for (var current = declared; current != null;)
            {
                yield return current;
                var name = current.BaseType?.FullName;
                current = name == null ? null : gameTypes.FirstOrDefault(t => t.FullName == name);
            }
        }
        MethodDefinition? Resolve(JsonElement row)
        {
            var owner = gameTypes.FirstOrDefault(t => t.FullName == row.GetProperty("type").GetString());
            if (owner == null) return null;
            var name = SignatureName(row.GetProperty("signature").GetString()!);
            return Chain(owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == name);
        }
        // The declaration an actual read binds to, resolved from the member name the IL spells along its
        // declaring type's base chain the same way a spec row is: the call site names the type the reader called
        // through, the spec names the type it documented, and a member declared on a base type is one read either
        // way. A name this audit cannot resolve against the interop assemblies is compared as it was spelled.
        string Resolved(string member)
        {
            int split = member.LastIndexOf("::", StringComparison.Ordinal);
            if (split < 0) return member;
            var owner = gameTypes.FirstOrDefault(t => t.FullName == member[..split]);
            var declared = owner == null ? null
                : Chain(owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == member[(split + 2)..]);
            return declared == null ? member : Member(declared);
        }
        // The whole signature of an actual call, with the declaration it binds to resolved along the base chain
        // by member name and parameter types: a call spelled on a derived type still compares equal to the row
        // that documents the base declaration, and an overloaded name resolves to the one form the call used.
        string CallSignature(MethodReference call)
        {
            var ownerName = call.DeclaringType is GenericInstanceType g ? g.ElementType.FullName : call.DeclaringType.FullName;
            var owner = gameTypes.FirstOrDefault(t => t.FullName == ownerName);
            var declared = owner == null ? null : Chain(owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == call.Name
                && m.Parameters.Select(EvidenceRegenerator.ParameterKey).SequenceEqual(call.Parameters.Select(EvidenceRegenerator.ParameterKey)));
            return declared == null ? call.FullName : declared.FullName;
        }
        // The action layer's write entries, declared by the spec the same way the reads are and resolved the same
        // way: each row names the game member the compiled plugin must call, and each is documented by a member
        // row of evidence/door-terminal-hooks.json that carries the build's own RVA and the bytes at it. The set
        // is compared both ways — an undeclared non-getter call fails, and a declared entry the plugin stopped
        // calling fails — so it cannot rot into a loosened whitelist.
        var declaredWrites = specWrites.Select(r => (Row: r, Member: Resolve(r))).ToArray();
        // GetGridCenterPosition and the warden objective lookups are read-only queries despite their
        // non-getter names. Keep their declared members and exact call-site audits below, but do not
        // label an observation caller — the map test world reads the active objective — a writer.
        var readOnlyNonGetters = declaredWrites
            .Where(x => x.Row.GetProperty("id").GetString() is
                "cellgroupingbase`2-getgridcenterposition" or
                "wardenobjectivemanager-checkwardenobjectivecompleted" or
                "wardenobjectivemanager-getwardenobjectivecountforlayer" or
                "wardenobjectivemanager-trygetwardenobjectivedataforlayer" or
                // The built room volume lookups: the native geomorph volume query and the voxel pillar read answer values and write nothing.
                "aig-geomorphnodevolume-trygetgeomorphvolume" or "aig-voxelnodevolume-getpillar")
            .Select(x => x.Row.GetProperty("signature").GetString()!).ToArray();
        string unresolvedWrites = string.Join(", ", declaredWrites.Where(x => x.Member == null)
            .Select(x => x.Row.GetProperty("id").GetString()));
        var declaredWriteMembers = declaredWrites.Where(x => x.Member != null).Select(x => Member(x.Member!))
            .Distinct(StringComparer.Ordinal).ToArray();
        var declaredWriteSignatures = declaredWrites.Select(x => x.Row.GetProperty("signature").GetString()!)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        bool DeclaredWrite(string member) => declaredWriteMembers.Contains(member, StringComparer.Ordinal)
            || declaredWriteMembers.Contains(Resolved(member), StringComparer.Ordinal);
        // The comparison runs on the whole signature and not on the member name: the terminal's AddLine is
        // overloaded, so a row that pinned the wrong form, or a call site that reached another one, is reported.
        var writeSignatures = calls.Where(m => DeclaredWrite(Member(m))).Select(CallSignature)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.declared-writes-exact", unresolvedWrites.Length == 0 && writeSignatures.SequenceEqual(declaredWriteSignatures),
            "declared=" + declaredWriteSignatures.Length + " actual=" + writeSignatures.Length
            + (unresolvedWrites.Length == 0 ? "" : " unresolved: " + unresolvedWrites)
            + " missing=" + string.Join(", ", declaredWriteSignatures.Except(writeSignatures))
            + " extra=" + string.Join(", ", writeSignatures.Except(declaredWriteSignatures)));
        var undeclaredWrites = writes.Where(c => !DeclaredWrite(c)).ToArray();
        Check("native.game-writes-are-declared", gameCalls.Length > 0 && undeclaredWrites.Length == 0,
            undeclaredWrites.Length == 0 ? string.Join(", ", writes) : string.Join(", ", undeclaredWrites));
        // The declared entries are the action layer's own: the observation and query halves of this module write
        // nothing, so a write called from anywhere else would be exactly the state change the read-only rule
        // forbade. The list is the level's action layer — the map-object commands and presentations, the level
        // events, the HUD rows, the session and map-state rows, the wave tuning and light fade, the door, terminal,
        // objective and alarm rows, the item spawn row, the
        // light-mode row this provider's power half holds, and the two spawn rows together with
        // the one placement both they and the fixed-placement half spawn through, and the fog repeller switch a placed sphere is started and
        // stopped by, the success page whose music the success-screen row overrides, and the gate flags the
        // door-build applier sets on a gate before the game builds it, and the system object appliers' own
        // distribution jobs, elevator cargo spawns and level sentry gear spawn — together with the compiler generated
        // closure classes the level-event and environment rows build their own display values in and the scan
        // coverage session writes its owned player requirement through, and the one conversion that turns a card's stored Text block into
        // the game's own `LocalizedText`.
        // A lambda's compiler ordinal is not an owner. Retain the enclosing action's identity instead.
        var writeCallSites = nativeTypes.SelectMany(t => t.Methods.Select(m =>
            (Type: t.Name == "<>c" || t.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal)
                ? t.DeclaringType.Name : t.Name, Method: m)))
            .Where(x => x.Method.HasBody)
            .SelectMany(x => x.Method.Body.Instructions.Select(i => (x.Type, Call: i.Operand))
                .Concat(EvidenceRegenerator.MethodInfoCalls(x.Method, gameTypes).Select(m => (x.Type, Call: (object)m))))
            .Where(x => x.Call is MethodReference r && DeclaredWrite(Member(r))
                && !readOnlyNonGetters.Contains(CallSignature(r), StringComparer.Ordinal))
            .Select(x => x.Type).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.writes-live-in-the-action-layer",
            writeCallSites.SequenceEqual(new[]
            {
                "AlarmActions", "AlarmWaveActions", "AnimationRegion",
                // The briefing window's ALL CLEARED icon is stopped and hidden on a level without both layers.
                "BriefingSectorIcons",
                "BulkheadKeyPlacement", "CarryItemRestrictions",
                // M12 is the contract's generation-time writer exception; installation stays in Runtime.
                "CompiledMapGenerator",
                "DimensionEnvironmentActions", "DoorActions",
                // The door-alarm card's swap puts a prebuilt scan on a door's lock (SetupForChainedPuzzle) or puts its own back.
                "DoorAlarmSwaps",
                "DoorBuildApplier", "DoorFoamActions", "DoorLocksNative", "DoorTerminalActions", "ElevatorCargoApplier",
                // The random group pool of the level's own Rundown package, built by the game's own `EnemyGroupRandomizer` constructor.
                "EnemyGroupPool",
                // The start elevator's local seat exit sets the camera offset native Exit subtracts, so the feet land on the floor.
                "EntryLandingApplier",
                "EnvironmentActions",
                // The extraction point host attaches the level's one exit host and writes its area and scan alignment;
                // its session owns the exact factory batch subscription.
                "ExtractionMoveLedger", "ExtractionPointHost",
                "FixedContainerPlacement", "FixedEnemyPlacement", "FixedEnemySpawnInitialized",
                // An authored task object's point: one marker spawner made at the authored pose (`LG_MarkerSpawner..ctor`), handed to
                // its own claimed request only, before the native builder builds that request.
                "FixedFunctionPoints",
                "FixedLandingPoints", "FixedPlacementSession", "FogRepellerActions",
                // The generator reset row removes a cell through `LG_PowerGenerator_Core.AttemptInteract(RemoveCell)`.
                "GeneratorResetActions",
                // Requeued native pickup requests preserve their typed factory recipe before native marker/floor build.
                "GenericFunctionItemsHooks",
                "GlobalWaveApplier", "InteractionPointSession",
                // The interior-door binding owns its exact factory start/done subscriptions and removes them on disposal.
                "ItemSpawnApplier",
                "LevelEventActions",
                "LightColorFade",
                // The cached main camera subscribes to the game-state change while a session holds it and gives the subscription back.
                "MapLabels", "MapSoundPlaces", "MapStateActions",
                "NativeMarkers",
                // NativeWaveLifetime cancels the observed native member waves of one run through their original StopEvent.
                "NativeWaveLifetime", "ObjectiveActions", "ObjectiveEventActions",
                // The presentation session owns the native objective panel rows it adds, updates and removes.
                "ObjectivePresentationSession",
                // Per-round objectives move the native stamps, waits and receiver seed and replay a held native command.
                "ObjectiveRoundsNative",
                "PlacedItemSpawn", "PlacedModelSession", "PowerStateActions",
                // The native factory failure boundary and the room socket writer introduced by the room recipe host.
                "RoomGenerationFailure", "RoomRecipeBuilder", "RoomRecipeDoorSelection", "RoomRecipeElevatorSelection",
                // One room instance drops the parts its connection's door replaces, before native builds its spawners, and rereads the selectors they took with them.
                "RoomRecipeHost",
                // The recipe ladder writes the width, the enemy-only flag and the top floor of the LG_Ladder it adds, before the native late scan builds it.
                "RoomRecipeLadders",
                // The recipe marker enables the function (`LG_MarkerProducer.m_allowFunction`) of the producer it adds to the room it builds.
                "RoomRecipeMarkers",
                // The recipe room sizes its node volume's override (`AIG_GeomorphNodeVolume.m_sizeOverrideTransform`) before PrepareBuild.
                "RoomRecipeNavigationBounds",
                "RoomRecipeParts",
                "RoomRecipeSockets",
                // The scan source is moved off a prop onto open floor before the native down-ray probe.
                "RoomRecipeSourceClearance",
                // The menu's declared native result source is RundownManager.GetLocalRundownKey.
                "RundownMenu",
                // The scan beat lease writes and restores the temporary speed slots, player requirement and last player count.
                "ScanBeatFrame",
                // The scan colour lease writes and restores owned colour containers and entries, then refreshes the native colour mode.
                "ScanColorInput", "ScanCoverageSession",
                // Each coverage target owns and restores its scanner requirement and radius fields.
                "ScanCoverageTarget",
                // The scan graphics state writes the authored ring label and restores the original while it still shows.
                "ScanGraphicsState",
                // The scan speed lease assigns its private speed table and restores the original only while still owned.
                "ScanSpeedInput", "SessionActions",
                // The space-point spawn row draws the group's facing through the native `EnemyGroup.GetRandomRotation`, the same call the native zone spawn makes.
                "SpacePointActions",
                // The spawn-rule session owns its factory batch-done subscription and removes it on disposal.
                "SpawnRuleSession",
                "SuccessScreenActions", "TaskObjectPlacement", "TerminalActions",
                // The terminal content ledger applies the copied log and command records the content rows author.
                "TerminalContentActions", "TerminalContentLedger",
                // The vanilla-world-object action half switches exact interactables and animation triggers.
                "VanillaWorldObjects", "WorldEventFacts",
                // A team warp's zone landing object is set up and registered the way the native collection job does.
                "ZoneLandingObjects",
                // The device row's zone-light writer, nested in `DeviceActions`: `LG_Light.SetEnabled`.
                "ZoneLights", "ZoneOwnership"
            }),
            string.Join(", ", writeCallSites));
        // A declared write is not a read: both the raw spelling and the declaration it binds to are subtracted
        // from the read sets below, so the reads a spec row declares stay exactly the reads the module makes.
        var gameReads = gameCalls.Where(c => !EvidenceRegenerator.Engine(c) && !DeclaredWrite(c)).ToArray();
        // Every call this module makes to a game type is one of the two read sets below, and neither set may
        // grow without its evidence row: a member read but not declared, or declared but no longer read, fails.
        Check("native.reads-are-declared", gameReads.All(c => EvidenceRegenerator.GameRead(c) || EvidenceRegenerator.MapObjectRead(c)),
            string.Join(", ", gameReads.Where(c => !EvidenceRegenerator.GameRead(c) && !EvidenceRegenerator.MapObjectRead(c))));
        var identityReads = gameReads.Where(EvidenceRegenerator.GameRead).ToArray();
        var declaredReads = specReadbacks.Select(r => (Row: r, Member: Resolve(r))).ToArray();
        // Only the game's own reads are declared in the spec, so a row whose member the interop assemblies do
        // not declare is either a stale row or a spelling the audit cannot resolve — both reported as unresolved.
        string unresolved = string.Join(", ", declaredReads.Where(x => x.Member == null)
            .Select(x => x.Row.GetProperty("id").GetString()));
        var expectedReads = declaredReads.Where(x => x.Member != null && EvidenceRegenerator.GameRead(Member(x.Member!))).Select(x => Member(x.Member!)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.exact-player-reads", unresolved.Length == 0 && identityReads.SequenceEqual(expectedReads),
            unresolved.Length == 0 ? string.Join(", ", identityReads) : "unresolved: " + unresolved);
        // The map-object readers read the level's zone table and the objects in it, and the spec names every one
        // of those reads too: a coordinate or a member added without its evidence row fails here.
        var expectedMapReads = declaredReads.Where(x => x.Member != null && EvidenceRegenerator.MapObjectRead(Member(x.Member!)))
            .Select(x => Member(x.Member!)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var mapReads = gameReads.Where(EvidenceRegenerator.MapObjectRead).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // The comparison runs on resolved members, not on the names the IL or the spec spell: a member declared
        // on a base type is the same read whichever type the call site or the evidence row names it through.
        var resolvedMapReads = mapReads.Select(Resolved).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.exact-map-object-reads", resolvedMapReads.SequenceEqual(expectedMapReads),
            "rows=" + specReadbacks.Length + " resolved=" + declaredReads.Count(x => x.Member != null)
            + " actual=" + resolvedMapReads.Length + " expected=" + expectedMapReads.Length
            + " missing=" + string.Join(", ", resolvedMapReads.Except(expectedMapReads))
            + " extra=" + string.Join(", ", expectedMapReads.Except(resolvedMapReads)));
        Check("native.every-game-read-is-classified", gameReads.Length == identityReads.Length + mapReads.Length
            && engineCalls.Length > 0,
            "game reads=" + gameReads.Length + " player=" + identityReads.Length + " map-object=" + mapReads.Length
            + " engine=" + engineCalls.Length);
        foreach (var (row, member) in declaredReads)
        {
            string id = row.GetProperty("id").GetString()!;
            Check("readback." + id + ".game-member", member != null && member.IsStatic == row.GetProperty("isStatic").GetBoolean(),
                row.GetProperty("signature").GetString() + (member == null ? "" : " declared by " + member.DeclaringType.FullName));
        }
        // A lambda's compiler-generated display class and ordinal are not an owner: a write inside
        // `<ApplyStaticInputs>b__0` still belongs to ApplyStaticInputs, and the next code change that
        // shifts the compiler's numbering must not make the same call site look like a different one.
        // The enclosing method's own signature is the canonical spelling on both sides of the comparison.
        string CallSite(string site)
        {
            int colon = site.IndexOf("::", StringComparison.Ordinal);
            if (colon < 0) return site;
            int open = site.IndexOf('<', colon), close = site.IndexOf('>', open + 1);
            if (open != colon + 2 || close < 0) return site;
            int space = site.LastIndexOf(' ', colon);
            string ownerName = site[(space + 1)..colon];
            int generated = ownerName.LastIndexOf("/<>", StringComparison.Ordinal);
            if (generated >= 0) ownerName = ownerName[..generated];
            string enclosing = site[(open + 1)..close];
            var owner = nativeTypes.FirstOrDefault(t => t.FullName == ownerName);
            var declared = owner?.Methods.Where(m => m.Name == enclosing).ToArray();
            return declared?.Length == 1 ? declared[0].FullName : ownerName + "::" + enclosing;
        }
        var callSitesBySignature = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Concat(EvidenceRegenerator.MethodInfoCalls(m, gameTypes))
                .Where(r => DeclaredWrite(Member(r))).Select(r => (Caller: CallSite(m.FullName), Signature: CallSignature(r))))
            .GroupBy(x => x.Signature, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => g.Select(x => x.Caller).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (var (row, _) in declaredWrites)
        {
            string id = row.GetProperty("id").GetString()!;
            string signature = row.GetProperty("signature").GetString()!;
            var actualSites = callSitesBySignature.GetValueOrDefault(signature) ?? Array.Empty<string>();
            var declaredSites = row.GetProperty("callSites").EnumerateArray().Select(s => CallSite(s.GetString()!))
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Check("write." + id + ".call-sites", actualSites.Length != 0 && actualSites.SequenceEqual(declaredSites),
                "actual=" + string.Join(", ", actualSites) + " declared=" + string.Join(", ", declaredSites));
            // A write row is pinned by its whole signature and not by its member name: the terminal's AddLine has
            // three forms and only one takes a line kind, so the row has to name the declaration it means, with
            // the static flag the interop assembly carries.
            //
            // The signature is compared against the calls the plugin really makes (`writeSignatures`, resolved
            // above) rather than against the declaration's own `FullName`, because one declaration has two legal
            // spellings: a call through a generic instance names the closed instantiation with the call's own type
            // arguments (`SNet_StateReplicator`2<A,B>::set_State(!0)`), while the type definition names the open
            // one (`SNet_StateReplicator`2::set_State(T)`). A row that pinned an overload the plugin does not
            // call, or the wrong member of the declared type, still fails.
            var owner = gameTypes.FirstOrDefault(t => t.FullName == row.GetProperty("type").GetString());
            var declaredOnType = owner == null ? Array.Empty<MethodDefinition>()
                : Chain(owner).SelectMany(t => t.Methods).Where(m => m.FullName == signature).ToArray();
            Check("write." + id + ".game-member",
                declaredOnType.Length == 1
                && declaredOnType.All(m => m.IsStatic == row.GetProperty("isStatic").GetBoolean())
                && writeSignatures.Contains(signature, StringComparer.Ordinal),
                string.Join(" | ", declaredOnType.Select(m => m.FullName)));
        }
        var harmonyCalls = calls.Where(m => Scope(m).Contains("Harmony", StringComparison.Ordinal)).Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.no-harmony-installation", harmonyCalls.Length == 0, string.Join(", ", harmonyCalls));

        bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
        var hooks = nativeTypes.Where(Patch).ToArray();
        Check("native.exact-hook-set", hooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(specHooks.Select(h => h.GetProperty("hook").GetString()!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(", ", hooks.Select(t => t.Name)));
        // The classes handed to Runtime (`MapNativeHooks.Declared`: its own list and the world-object families') are
        // read from the loads of their type tokens in the two static initializers. Each must be a shape Runtime
        // admits — one patch attribute, at most one prefix, postfix and finalizer, and none of the forms that make a
        // class resolve its own target or install itself — and none may be handed over twice.
        var declaredClasses = EvidenceRegenerator.DeclaredHookClasses(nativeTypes);
        var declaredShapeFaults = declaredClasses.Select(name => hooks.SingleOrDefault(t => t.FullName == name)).Select((type, at) =>
        {
            if (type == null) return declaredClasses[at] + " has no patch attribute";
            var attributes = type.Methods.SelectMany(m => m.CustomAttributes).Select(a => a.AttributeType.Name).ToArray();
            int Count(string kind) => attributes.Count(a => a == kind);
            if (type.CustomAttributes.Count(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch") != 1) return type.Name + " has not exactly one patch attribute";
            if (Count("HarmonyPrefix") > 1 || Count("HarmonyPostfix") > 1 || Count("HarmonyFinalizer") > 1) return type.Name + " has two callbacks of one kind";
            if (attributes.Any(a => a is "HarmonyTranspiler" or "HarmonyTargetMethod" or "HarmonyTargetMethods" or "HarmonyPrepare" or "HarmonyCleanup"))
                return type.Name + " carries a form Runtime refuses";
            return null;
        }).Where(fault => fault != null).ToArray();
        Check("native.declared-hook-classes", declaredClasses.Length > 0 && declaredShapeFaults.Length == 0
            && declaredClasses.ToHashSet(StringComparer.Ordinal).SetEquals(hooks.Select(type => type.FullName)),
            "declared=" + declaredClasses.Length + " patch classes=" + hooks.Length + " " + string.Join(" | ", declaredShapeFaults));
        Check("native.runtime-installation-evidence", specHooks.All(row =>
                row.TryGetProperty("installationOwner", out var owner) && owner.GetString() == "ForgeEngine"
                && row.TryGetProperty("declaration", out var declaration) && declaration.GetString() == "MapNativeHooks.Declared")
            && specHooks.Select(row => row.GetProperty("hook").GetString()!).ToHashSet(StringComparer.Ordinal)
                .SetEquals(hooks.Select(type => type.Name)),
            "declared=" + declaredClasses.Length + " evidence=" + specHooks.Length);
        var actualHookTargets = hooks.SelectMany(h => EvidenceRegenerator.HookTargets(gameTypes, h)
            .Select(t => EvidenceRegenerator.HookKey(h.Name, t.FullName))).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var declaredHookTargets = specHooks.Select(h => EvidenceRegenerator.HookKey(h.GetProperty("hook").GetString()!,
            h.GetProperty("signature").GetString()!)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.exact-hook-targets", actualHookTargets.SequenceEqual(declaredHookTargets),
            "missing=" + string.Join(", ", actualHookTargets.Except(declaredHookTargets))
            + " extra=" + string.Join(", ", declaredHookTargets.Except(actualHookTargets)));
        // The parameters a postfix body loads, by the ldarg/ldarga that loads them. Whether a hook may read its
        // arguments is declared per hook, so an undeclared read fails that hook's own row instead of passing.
        IEnumerable<ParameterDefinition> parameterReads(MethodDefinition method)
        {
            // A parameter a lambda in the body captures is not loaded by the body itself: the compiler hoists it
            // into the body's own display class and passes that instance to the lambda, so the assembly spells the
            // read as a field of the display class. One alias per hoisted parameter is gathered here — where the
            // body's own instructions store the parameter into its display class — and every name the body or a
            // lambda on that class loads is then answered as the parameter it stands for.
            var alias = new Dictionary<string, string>(StringComparer.Ordinal);
            var display = method.DeclaringType.NestedTypes.Where(t => t.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal)).ToArray();
            var arguments = new List<ParameterDefinition>();
            foreach (var instruction in method.Body.Instructions)
            {
                var parameter = instruction.OpCode.Code switch
                {
                    Code.Ldarg_0 => method.Parameters.ElementAtOrDefault(0),
                    Code.Ldarg_1 => method.Parameters.ElementAtOrDefault(1),
                    Code.Ldarg_2 => method.Parameters.ElementAtOrDefault(2),
                    Code.Ldarg_3 => method.Parameters.ElementAtOrDefault(3),
                    Code.Ldarg or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S => instruction.Operand as ParameterDefinition,
                    _ => null
                };
                if (parameter != null)
                {
                    arguments.Add(parameter);
                    continue;
                }
                // The store into the display class is the parameter's other spelling: the argument loaded just
                // before it is the one being hoisted. Whether it was read is decided by the loads below, because
                // a field the body only ever writes is not a read.
                if (instruction.OpCode.Code == Code.Stfld && instruction.Operand is FieldReference field
                    && display.Any(t => t.FullName == field.DeclaringType.FullName) && arguments.Count != 0)
                    alias[field.Name] = arguments[^1].Name;
            }
            foreach (var parameter in arguments)
                if (!Injected(parameter)) yield return parameter;
            if (alias.Count == 0) yield break;
            foreach (var holder in new[] { method }.Concat(display.SelectMany(t => t.Methods)))
            {
                if (!holder.HasBody) continue;
                foreach (var instruction in holder.Body.Instructions)
                    if (instruction.OpCode.Code is Code.Ldfld or Code.Ldflda or Code.Ldsfld or Code.Ldsflda
                        && instruction.Operand is FieldReference field && alias.TryGetValue(field.Name, out var aliasName))
                    {
                        var captured = method.Parameters.Single(p => p.Name == aliasName);
                        if (!Injected(captured)) yield return captured;
                    }
            }
        }
        // Harmony's own injected parameters are not arguments of the patched method: `__instance` is the instance
        // the audit declares separately, `__result` is the return value a postfix may read, and `__state` is the
        // slot a prefix writes for its postfix. None of them is a value the native body was handed, so the read
        // set a row declares is the arguments alone.
        // __instance may be base-typed for a multi-target factory. Every row still checks its declared
        // instance shape and compatibility independently; the injected slot never becomes a native argument.
        bool Injected(ParameterDefinition parameter)
            => parameter.Name is "__instance" or "__result" or "__state" || EvidenceRegenerator.FinalizerInjected(parameter);
        // Whether a patch body changes a parameter it declares, which is the write side of the same declaration:
        // a by-ref parameter the body stores into is one the patched method reads back changed, and a parameter
        // the body only loads is one it observed. The store is found by the same argument bound the reads use, so
        // a parameter the body never touches is in neither set.
        bool IsStored(MethodDefinition method, ParameterDefinition parameter)
        {
            foreach (var instruction in method.Body.Instructions)
            {
                bool bound = instruction.OpCode.Code switch
                {
                    Code.Ldarg_0 => method.Parameters.ElementAtOrDefault(0) == parameter,
                    Code.Ldarg_1 => method.Parameters.ElementAtOrDefault(1) == parameter,
                    Code.Ldarg_2 => method.Parameters.ElementAtOrDefault(2) == parameter,
                    Code.Ldarg_3 => method.Parameters.ElementAtOrDefault(3) == parameter,
                    Code.Ldarg or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S => instruction.Operand == parameter,
                    _ => false
                };
                if (bound && instruction.OpCode.Code is Code.Starg or Code.Starg_S) return true;
                if (bound && instruction.OpCode.Code is Code.Ldarga or Code.Ldarga_S)
                {
                    // The address of the argument is taken, so the store is an indirect one: `stind` on the next
                    // instruction writes through it. Any other use of the address is a read of a by-ref argument
                    // by the callee, not a change of it.
                    int at = method.Body.Instructions.IndexOf(instruction);
                    if (at + 1 < method.Body.Instructions.Count
                        && method.Body.Instructions[at + 1].OpCode.Code is Code.Stind_I or Code.Stind_I1 or Code.Stind_I2
                            or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_R4 or Code.Stind_R8 or Code.Stind_Ref or Code.Stobj)
                        return true;
                }
                // A by-ref parameter carries its address, so it is loaded with the plain `ldarg` forms and a store
                // into it is the element type's indirect store, with the value pushed in between (`visible = false`
                // is `ldarg.1`, `ldc.i4.0`, `stind.i1`). That is the same change of the argument the `ldarga`
                // spelling makes for a by-value one, and the `starg` above never appears for a by-ref parameter.
                if (bound && parameter.ParameterType.IsByReference)
                {
                    int at = method.Body.Instructions.IndexOf(instruction);
                    for (int next = at + 1; next < method.Body.Instructions.Count && next <= at + 3; next++)
                        if (method.Body.Instructions[next].OpCode.Code is Code.Stind_I or Code.Stind_I1 or Code.Stind_I2
                            or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_R4 or Code.Stind_R8 or Code.Stind_Ref or Code.Stobj)
                            return true;
                }
            }
            return false;
        }
        // The methods one patch's whole callback runs: the patch body itself, the helpers of its own class it
        // calls by name, and the lambdas the compiler moved into that class's own display classes. A guard the
        // patch reaches through one of those is the same guard the callback runs inside, so a row that declares
        // one is answered by this set rather than by the body alone.
        IEnumerable<MethodDefinition> Callback(MethodDefinition body)
        {
            var closures = body.DeclaringType.NestedTypes.Where(t => t.Name.StartsWith("<", StringComparison.Ordinal));
            var helpers = body.DeclaringType.Methods.Where(m => m.HasBody && m != body
                && body.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                    .Any(c => c.Name == m.Name && c.DeclaringType.FullName == body.DeclaringType.FullName));
            return new[] { body }.Concat(helpers).Concat(closures.SelectMany(t => t.Methods));
        }
        foreach (var spec in specHooks)
        {
            string hookName = spec.GetProperty("hook").GetString()!;
            string name = EvidenceRegenerator.HookKey(hookName, spec.GetProperty("signature").GetString()!);
            var hook = hooks.SingleOrDefault(t => t.Name == hookName);
            if (hook == null) { Check("hook." + name + ".present", false); continue; }
            var declaredTarget = EvidenceRegenerator.HookTargets(gameTypes, hook)
                .SingleOrDefault(t => t.FullName == spec.GetProperty("signature").GetString());
            if (declaredTarget == null) { Check("hook." + name + ".declared-target", false); continue; }
            var type = declaredTarget.DeclaringType;
            var method = declaredTarget.Name;
            Check("hook." + name + ".declared-target", type.FullName == spec.GetProperty("type").GetString() && method == spec.GetProperty("name").GetString(),
                type.FullName + "::" + method);
            // The target is pinned by its whole signature and not by its name: `PostEvent` is overloaded, so a
            // row that named the wrong form would otherwise pass on a member the plugin never patched.
            var targets = gameTypes.Where(t => t.FullName == type.FullName).SelectMany(t => t.Methods.Where(m => m.Name == method))
                .Where(m => m.FullName == spec.GetProperty("signature").GetString()).ToArray();
            Check("hook." + name + ".unique-game-method", targets.Length == 1
                && targets[0].IsVirtual == spec.GetProperty("isVirtual").GetBoolean() && targets[0].IsStatic == spec.GetProperty("isStatic").GetBoolean(),
                string.Join(" | ", gameTypes.Where(t => t.FullName == type.FullName).SelectMany(t => t.Methods.Where(m => m.Name == method)).Select(m => m.FullName)));
            var patches = hook.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Namespace == "HarmonyLib"
                && a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyTranspiler" or "HarmonyFinalizer")).ToArray();
            // The kind is declared by the row and not assumed by the audit: the module carries postfixes
            // wherever the native body may run first and prefixes where the body has to be pre-empted, and each
            // row says which one it is so a patch that silently changed kind fails here.
            string kind = spec.GetProperty("patch").GetString()!;
            var originalRunObservers = patches.Where(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyFinalizer")
                && p.Parameters.Any(parameter => parameter.Name == "__runOriginal")).ToArray();
            string actualKind = originalRunObservers.Length != 0 ? "finalizer"
                : patches.Any(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix")) ? "postfix"
                : patches.Any(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix")) ? "prefix" : "finalizer";
            Check("hook." + name + ".primary-stage", originalRunObservers.Length <= 1 && kind == actualKind, actualKind);
            // The class carries exactly one method of the declared kind: a prefix beside a postfix is Harmony's
            // own state capture rather than a second hook, and a class with two of the same kind would have one
            // body the audit never reads.
            var kindMethods = patches.Where(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "Harmony" + Kind(kind))).ToArray();
            var patchBody = kindMethods.Length == 1 && kindMethods[0].IsStatic ? kindMethods[0] : null;
            Check("hook." + name + ".single-" + kind, patchBody != null, string.Join(", ", patches.Select(p => p.Name)));
            if (kind != "prefix")
            {
                var prefixes = patches.Where(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix")).ToArray();
                Check("hook." + name + ".prefix-metadata", prefixes.Length == 0
                    ? !spec.TryGetProperty("prefix", out _)
                    : prefixes.Length == 1 && prefixes[0].IsStatic
                        && spec.TryGetProperty("prefix", out var declaredPrefixShape)
                        && System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(declaredPrefixShape.GetRawText()),
                            EvidenceRegenerator.PrefixShape(prefixes[0])));
            }
            // The patch shape is declared by the hook, not assumed by the audit (see patchVocabulary in the
            // spec): the player readbacks take the patched instance and never read it, the door and terminal state
            // readbacks take and read it, and the terminal command readback has no instance but reads the entry's
            // own arguments. A declared instance must be the patched type, and every declared argument must be
            // declared with the exact interop type; the read set is compared both ways.
            var shape = spec.GetProperty(kind);
            string? instance = shape.GetProperty("instanceParameter").GetString();
            string? instanceType = shape.GetProperty("instanceType").GetString();
            Check("hook." + name + ".instance-compatible", instance == null ? instanceType == null
                : instanceType != null && EvidenceRegenerator.InstanceCompatible(gameTypes, declaredTarget, instanceType));
            var expectedParameters = (instance == null ? Array.Empty<(string, string)>() : new[] { (instance, instanceType!) })
                .Concat(shape.GetProperty("arguments").EnumerateArray().Select(a => (a.GetProperty("name").GetString()!, a.GetProperty("type").GetString()!))).ToArray();
            // The declared shape is the patched method's own parameters, which is what a row documents: the
            // instance when the row declares one, then the method's arguments. Harmony's own slots — the state a
            // prefix writes and the result a postfix may read — are the patch's parameters and not the method's,
            // so a row declares neither.
            var parameters = patchBody?.Parameters.Where(p => p.Name is not ("__state" or "__result") && !EvidenceRegenerator.FinalizerInjected(p)).Select(p => (p.Name, p.ParameterType.FullName)).ToArray() ?? Array.Empty<(string, string)>();
            Check("hook." + name + "." + kind + "-parameters", parameters.SequenceEqual(expectedParameters),
                string.Join(", ", parameters.Select(p => p.Item1 + " " + p.Item2)));
            if (kind == "finalizer" || kind == "postfix" && patchBody?.Parameters.Any(p => p.Name == "__runOriginal") == true)
            {
                string? observationError = patchBody == null ? "missing observer" : kind == "finalizer"
                    ? EvidenceRegenerator.FinalizerObservationError(patchBody) : EvidenceRegenerator.PostfixObservationError(patchBody);
                Check("hook." + name + "." + kind + "-observer", observationError == null, observationError ?? "");
                var declaredInjections = shape.TryGetProperty("injectedArguments", out var injected)
                    ? injected.EnumerateArray().Select(p => (p.GetProperty("name").GetString()!, p.GetProperty("type").GetString()!)).ToArray()
                    : Array.Empty<(string, string)>();
                var actualInjections = patchBody?.Parameters.Where(p => EvidenceRegenerator.FinalizerInjected(p) || p.Name == "__state")
                    .Select(p => (p.Name, p.ParameterType.FullName)).ToArray() ?? Array.Empty<(string, string)>();
                Check("hook." + name + "." + kind + "-injections", shape.TryGetProperty("injectedArguments", out _)
                    && actualInjections.SequenceEqual(declaredInjections));
                Check("hook." + name + "." + kind + "-return", shape.TryGetProperty("returnType", out var returnType)
                    && returnType.GetString() == patchBody?.ReturnType.FullName && returnType.GetString() == "System.Void");
                if (patchBody?.Parameters.Any(p => p.Name == "__runOriginal") == true)
                {
                    var observationPrefixes = patches.Where(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix")).ToArray();
                    bool instanceCapture = patchBody.Parameters.Any(p => p.Name == "__instance");
                    Check("hook." + name + ".observation-prefix", instanceCapture
                        ? observationPrefixes.Length == 1
                            && observationPrefixes[0].CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPriority")
                            && spec.TryGetProperty("observationPrefix", out var declaredPrefix)
                            && System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(declaredPrefix.GetRawText()),
                                EvidenceRegenerator.ObservationSignature(observationPrefixes[0]))
                        : observationPrefixes.Length == 0 && !spec.TryGetProperty("observationPrefix", out _));
                }
            }
            string[] reads = shape.GetProperty("reads").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string[] loaded = patchBody == null ? Array.Empty<string>()
                : parameterReads(patchBody).Select(p => p.Name).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Check("hook." + name + ".parameter-reads", loaded.SequenceEqual(reads), string.Join(", ", loaded));
            // The parameters a prefix changes, by the by-ref parameters its body stores through. A postfix
            // changes nothing, so its write set is empty by definition and a row that declared one fails here.
            string[] shapeWrites = shape.GetProperty("writes").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string[] stored = patchBody == null ? Array.Empty<string>()
                // Harmony's private state slot is not a parameter of the native target. Return and argument
                // writes remain audited, matching EvidenceRegenerator's existing state exclusion.
                : patchBody.Parameters.Where(p => p.Name != "__state" && IsStored(patchBody, p)).Select(p => p.Name)
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Check("hook." + name + ".parameter-writes", stored.SequenceEqual(shapeWrites), string.Join(", ", stored));
            var priority = patchBody?.CustomAttributes.SingleOrDefault(a => a.AttributeType.Name == "HarmonyPriority");
            // Every patch declares its own priority explicitly: the module's convention is that a patch which
            // reads what the body left behind runs last and a patch which has to pre-empt the body runs first,
            // and a hook that follows neither has to say so rather than inherit a default nobody chose.
            Check("hook." + name + ".priority", priority != null);

            // Whether a hook reaches a guard is declared by the row, because the module has two kinds: a readback
            // whose whole callback runs inside a guard method, and a patch-level table that is this package's own
            // state and answers nothing while its half is not attached. A row that declares a guard must call it
            // somewhere in the callback — the patch body, a helper of the patch class it calls, or a lambda the
            // compiler moved into that class's own display class — and a row that declares none must not name one.
            // The gate is either the session entry (`Plugin.Session`) or the half that owns its own lifecycle
            // (`DoorTerminalFacts.Current`), which the session creates and drops with itself.
            string session = spec.GetProperty("session").GetString()!;
            var patchCalls = (patchBody == null ? Array.Empty<MethodDefinition>() : Callback(patchBody))
                .Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
            bool reachesSession = patchCalls.Any(m => Member(m) is "ForgeMap.Native.MapHost::get_Session" or "ForgeMap.Native.DoorTerminalFacts::get_Current");
            string? declaredGuard = shape.GetProperty("guard").GetString();
            Check("hook." + name + ".guarded-by-session", session == "off"
                ? declaredGuard == null
                : reachesSession && patchCalls.Any(m => Member(m) == "ForgeMap.Native.MapPluginSession::" + declaredGuard
                    || Member(m) == "ForgeMap.Native.DoorTerminalFacts::" + declaredGuard),
                session + " " + declaredGuard);
        }

        return checks;
    }

    /// <summary>Writes the whole audit to the file `FORGE_MAP_LAYOUT_DUMP` names, one check per line. The
    /// assertion message only carries the failures, and rebuilding the evidence file from them loses the detail
    /// of a check that passed with a list: this dump is the audit's own reading, in its own order.</summary>
    private static void Dump(List<CheckRow> checks)
    {
        string path = Environment.GetEnvironmentVariable("FORGE_MAP_LAYOUT_DUMP") ?? "";
        if (string.IsNullOrWhiteSpace(path)) return;
        File.WriteAllLines(path, checks.Select(c => (c.Passed ? "PASS " : "FAIL ") + c.Id + "\t" + c.Detail));
    }

    private sealed record CheckRow(string Id, bool Passed, string Detail);

    /// <summary>The Harmony attribute name a declared patch kind is spelled with: the rows carry the kind in
    /// lower case, the attributes capitalize it.</summary>
    private static string Kind(string kind) => char.ToUpperInvariant(kind[0]) + kind[1..];

    /// <summary>The member name a dump-style signature declares: everything after the last `::`, without the
    /// parameter list. The interop assembly's own member name is what the audit compares against.</summary>
    private static string SignatureName(string signature)
    {
        int start = signature.LastIndexOf("::", StringComparison.Ordinal) + 2;
        int end = signature.IndexOf('(', start);
        return end < 0 ? signature[start..] : signature[start..end];
    }
}
