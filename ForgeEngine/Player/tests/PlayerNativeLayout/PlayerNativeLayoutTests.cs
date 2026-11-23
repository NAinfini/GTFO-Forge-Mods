using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ForgeEngine.HookEvidence;

namespace ForgePlayer.Tests.PlayerNativeLayout;

// Compiled-assembly metadata only: proves boundaries, plugin identity and hook shape, not native call timing.
// Inputs come from the environment (see ConfiguredInputs); the suite is excluded by the Native trait by default.
[Trait("Category", "Native")]
public sealed class PlayerNativeLayoutTests
{
    private static readonly string BepInEx = Environment.GetEnvironmentVariable("FORGE_PLAYER_BEPINEX")
        ?? Environment.GetEnvironmentVariable("GTFO_BEPINEX_PATH") ?? "";
    private static readonly string Artifacts = Environment.GetEnvironmentVariable("FORGE_ARTIFACTS") ?? "";
    private static readonly string Spec = Environment.GetEnvironmentVariable("FORGE_PLAYER_HOOK_SPEC")
        ?? Path.Combine("ForgeEngine", "Player", "evidence", "player-hooks.json");

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
            "Set FORGE_PLAYER_BEPINEX (or GTFO_BEPINEX_PATH) and FORGE_ARTIFACTS to an existing build output.");
    }

    /// <summary>The game assemblies this module is compiled against, which are the only ones a row of it can
    /// resolve against: the game's own player, network and interaction types, the state-machine base the
    /// movement-state query reads its entry time from, and the framework assembly the game-event entry lives in.</summary>
    private static AssemblyDefinition[] GameAssemblies() => new[] { "Modules-ASM.dll", "SNet_ASM.dll", "Utils-ASM.dll", "GlobalFramework-ASM.dll" }
        .Select(file => Read(Path.Combine(BepInEx, "interop", file))).ToArray();

    [Fact]
    public void player_native_layout_checks_all_pass()
    {
        RequireInputs();
        var checks = Audit();
        Dump(checks);
        string failed = string.Join(" | ", checks.Where(c => !c.Passed).Select(c => c.Id + ": " + c.Detail));
        Assert.True(checks.Any(c => c.Id == "native.no-gameplay-gate"), "The audit did not run to completion: " + failed);
        Assert.True(checks.Count(c => !c.Passed) == 0, failed);
    }

    private static List<CheckRow> Audit()
    {
        var checks = new List<CheckRow>();
        void Check(string id, bool passed, string detail = "") => checks.Add(new(id, passed, detail));
        IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
        using var sdk = Read(Artifact("bin/ForgeEngine.Framework/release/ForgeEngine.Framework.dll"));
        using var host = Read(Artifact("bin/ForgeEngine/release/ForgeEngine.dll"));
        var game = GameAssemblies();
        using var specDocument = JsonDocument.Parse(File.ReadAllText(Spec));
        var specHooks = specDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
        var specReadbacks = specDocument.RootElement.GetProperty("readbacks").EnumerateArray().ToArray();
        // The action layer's write entries are declared the same way the reads are: one row per game member, in
        // the same spec.
        var specWrites = specDocument.RootElement.GetProperty("writes").EnumerateArray().ToArray();
        var nativeTypes = ForgeTestInputs.DomainTypes.Native(host, sdk, "Player", "ForgeEngine.GameBindings.GameCourseNodes");
        var playerTypes = ForgeTestInputs.DomainTypes.Managed(sdk, "Player");
        var gameTypes = game.SelectMany(a => a.MainModule.Types).SelectMany(Walk).ToArray();
        bool GameAssembly(string name) => name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("BepInEx", StringComparison.Ordinal)
            || name.Contains("Harmony", StringComparison.Ordinal) || name.StartsWith("Il2Cpp", StringComparison.Ordinal) || name.EndsWith("-ASM", StringComparison.Ordinal) || name.EndsWith("_ASM", StringComparison.Ordinal);
        var instructions = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
        var calls = instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        string Scope(MethodReference m) => m.DeclaringType.Scope is AssemblyNameReference scope ? scope.Name : "";
        string Member(MethodReference m) => (m.DeclaringType is GenericInstanceType g ? g.ElementType.FullName : m.DeclaringType.FullName) + "::" + m.Name;
        // Player declares native work to the host; it owns no Unity frame component or kernel clock.
        Check("native.no-update-loop", !nativeTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"));
        // The in-level frame is the Engine's shared domain work entry, which takes and renews the Runtime native work lease for this package.
        Check("native.runtime-work-lease", calls.Any(m => Member(m) == "ForgeEngine.GameBindings.DomainWorkPhases::.ctor")
            ); // the lease is released through IDisposable.Dispose on the stored handle
        var languageCalls = nativeTypes.Where(t => t.Name == "PlayerTextLanguageSession").SelectMany(t => t.Methods)
            .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
        // Each player's language is its own row on ForgePlayer's ForgeCore channel; the session owns no transport of its own.
        Check("native.runtime-language-channel", languageCalls.Any(m => Member(m) == "ForgeCore.NetChannel::Lobby")
            && !languageCalls.Any(m => m.DeclaringType.Namespace == "System.Reflection")
            && !nativeTypes.Where(t => t.Name == "PlayerTextLanguageSession").SelectMany(t => t.Methods).Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions).Any(i => i.Operand is string text && text.Contains("GTFO.API.NetworkAPI", StringComparison.Ordinal)));
        // The engine binds the one settings file; a domain reads no BepInEx config of its own.
        Check("native.runtime-settings", !calls.Any(m => Member(m).StartsWith("BepInEx.Configuration.ConfigFile::Bind", StringComparison.Ordinal)));

        // One provider identity: the native plugin composes ForgePlayer's compiled registration instead of declaring
        // a second registry. Compare ids carried by the two built assemblies; production source text is not evidence.
        var moduleIds = instructions.Select(i => i.Operand).OfType<string>()
            .SelectMany(text => Regex.Matches(text, @"forge\.module\.[A-Za-z0-9_.]+").Select(m => m.Value))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var managedModuleIds = playerTypes.SelectMany(t => t.Fields).Where(f => f.HasConstant).Select(f => f.Constant).OfType<string>()
            .Concat(playerTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Select(i => i.Operand).OfType<string>())
            .SelectMany(text => Regex.Matches(text, @"forge\.module\.[A-Za-z0-9_.]+").Select(m => m.Value))
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var inventedIds = moduleIds.Where(id => !managedModuleIds.Contains(id)).ToArray();
        Check("native.single-provider-source", calls.Any(m => Member(m) == "ForgePlayer.ModuleDefinition::Create")
            && !instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference c && c.DeclaringType.FullName == "ForgeEngine.Framework.RuntimeModule")
            && inventedIds.Length == 0,
            inventedIds.Length == 0 ? "" : "ids absent from built ForgePlayer: " + string.Join(", ", inventedIds));
        // The player's whole ownership surface on the one registration: the resolver, the instance lookup, the
        // observer and the candidate source that names the same kind.
        Check("native.player-resolver-instance-lookup-and-observer", calls.Any(m => Member(m) == "ForgeEngine.Framework.RuntimeModule::set_EntityResolvers")
            && calls.Any(m => Member(m) == "ForgeEngine.Framework.RuntimeModule::set_EntityInstanceResolvers")
            && calls.Any(m => Member(m) == "ForgeEngine.Framework.RuntimeModule::set_EntityObservers")
            && calls.Any(m => Member(m) == "ForgeEngine.Framework.RuntimeModule::set_EntityCandidates")
            && instructions.Any(i => i.Operand is string s && s == "gtfo.player"));
        // Only the spawn/despawn readback allocates lives; the SDK instance lookup reads the recorded table and nothing else.
        var lookup = nativeTypes.Where(t => t.Name == "PlayerIdentityModule").SelectMany(t => t.Methods).SingleOrDefault(m => m.Name == "ResolveInstance");
        var lookupCalls = lookup?.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray() ?? Array.Empty<MethodReference>();
        Check("native.instance-lookup-never-allocates", lookup != null && lookup.Parameters.Count == 1 && lookup.Parameters[0].ParameterType.FullName == "System.Object"
            && !lookup.Body.Instructions.Any(i => i.OpCode.Code is Code.Stfld or Code.Stsfld)
            && !lookupCalls.Any(m => m.Name is "Reconcile" or "Add" or "Remove" or "Clear" or "get_Lookup" or "get_PlayerAgentsInLevel"),
            string.Join(", ", lookupCalls.Select(Member).Distinct(StringComparer.Ordinal)));
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
        var engineCalls = gameCalls.Where(HookRows.Engine).ToArray();
        // The write spelling: a non-getter game call that is not one of the named pure queries. The player action
        // layer is a write layer by definition, so these calls are declared one by one below
        // and every one of them has to be declared: a non-getter call no row names is a write nobody reviewed.
        // The engine's own members are not part of that set — the read set below subtracts them for the same
        // reason — so the comparison is between the game calls and the rows, both sides engine-free.
        var writes = gameCalls.Where(c => !HookRows.Engine(c)).Where(c =>
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
                && m.Parameters.Select(HookRows.ParameterKey).SequenceEqual(call.Parameters.Select(HookRows.ParameterKey)));
            return declared == null ? call.FullName : declared.FullName;
        }
        // The action layer's write entries, declared by the spec the same way the reads are and resolved the same
        // way: each row names the game member the compiled plugin must call, and the set is compared both ways — an undeclared non-getter call fails, and a declared entry the plugin stopped
        // calling fails — so it cannot rot into a loosened whitelist.
        var declaredWrites = specWrites.Select(r => (Row: r, Member: Resolve(r))).ToArray();
        string unresolvedWrites = string.Join(", ", declaredWrites.Where(x => x.Member == null)
            .Select(x => x.Row.GetProperty("id").GetString()));
        var declaredWriteMembers = declaredWrites.Where(x => x.Member != null).Select(x => Member(x.Member!))
            .Distinct(StringComparer.Ordinal).ToArray();
        var declaredWriteSignatures = declaredWrites.Select(x => x.Row.GetProperty("signature").GetString()!)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        bool DeclaredWrite(string member) => declaredWriteMembers.Contains(member, StringComparer.Ordinal)
            || declaredWriteMembers.Contains(Resolved(member), StringComparer.Ordinal);
        // The comparison runs on the whole signature and not on the member name: a game name can be overloaded,
        // so a row that pinned the wrong form, or a call site that reached another one, is reported.
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
        // forbade. The list is the player action layer — the agent modifier, the player health, command, damage,
        // stamina, impulse and warp actions, the bot-order row, the input lock the action-lock row holds a player
        // under, and the teammate marker line a presentation row writes.
        var writeCallSites = nativeTypes.SelectMany(t => t.Methods.Select(m => (Type: t.FullName, Method: m)))
            .Where(x => x.Method.HasBody)
            .SelectMany(x => x.Method.Body.Instructions.Select(i => (x.Type, Call: i.Operand)))
            .Where(x => x.Call is MethodReference r && DeclaredWrite(Member(r)))
            // A lambda's closure class is numbered by the compiler from its position in the source, so any new method
            // above it renames it; the closure is named by the type that declares it instead.
            .Select(x => System.Text.RegularExpressions.Regex.Replace(x.Type, @"<>c__DisplayClass\d+_\d+", "<closure>"))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.writes-live-in-the-action-layer",
            writeCallSites.SequenceEqual(new[]
            {
                "ForgePlayer.Native.BotOrders", "ForgePlayer.Native.BotSlotProgram", "ForgePlayer.Native.FlashlightHoldGate/Hold",
                "ForgePlayer.Native.GenericPlayerActions", "ForgePlayer.Native.NativeBotSlotRequest",
                "ForgePlayer.Native.PlayerActionLockWrites",
                "ForgePlayer.Native.PlayerCommandActions",
                "ForgePlayer.Native.PlayerDeviceWriters/Flashlight", "ForgePlayer.Native.PlayerDeviceWriters/MapPage/Hold",
                "ForgePlayer.Native.PlayerHealthAction", "ForgePlayer.Native.PlayerHealthReceiver",
                "ForgePlayer.Native.PlayerStaminaNative"
            }),
            string.Join(", ", writeCallSites));
        // A declared write is not a read: both the raw spelling and the declaration it binds to are subtracted
        // from the read sets below, so the reads a spec row declares stay exactly the reads the module makes.
        var gameReads = gameCalls.Where(c => !HookRows.Engine(c) && !DeclaredWrite(c)).ToArray();
        // Every call this module makes to a game type is one of the two read sets below, and neither set may
        // grow without its evidence row: a member read but not declared, or declared but no longer read, fails.
        Check("native.reads-are-declared", gameReads.All(c => EvidenceRegenerator.GameRead(c) || EvidenceRegenerator.LevelRead(c)),
            string.Join(", ", gameReads.Where(c => !EvidenceRegenerator.GameRead(c) && !EvidenceRegenerator.LevelRead(c))));
        var identityReads = gameReads.Where(EvidenceRegenerator.GameRead).ToArray();
        var declaredReads = specReadbacks.Select(r => (Row: r, Member: Resolve(r))).ToArray();
        // Only the game's own reads are declared in the spec, so a row whose member the interop assemblies do
        // not declare is either a stale row or a spelling the audit cannot resolve — both reported as unresolved.
        string unresolved = string.Join(", ", declaredReads.Where(x => x.Member == null)
            .Select(x => x.Row.GetProperty("id").GetString()));
        var expectedReads = declaredReads.Where(x => x.Member != null && EvidenceRegenerator.GameRead(Member(x.Member!))).Select(x => Member(x.Member!))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.exact-player-reads", unresolved.Length == 0 && identityReads.SequenceEqual(expectedReads),
            unresolved.Length == 0 ? string.Join(", ", identityReads) : "unresolved: " + unresolved);
        // The level reads a player's zone, area and warp destination are answered through — the course node a
        // player stands on and the dimension a warp names — and the spec names every one of those reads too: a
        // member added without its evidence row fails here.
        var expectedLevelReads = declaredReads.Where(x => x.Member != null && EvidenceRegenerator.LevelRead(Member(x.Member!)))
            .Select(x => Member(x.Member!)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var levelReads = gameReads.Where(EvidenceRegenerator.LevelRead).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // The comparison runs on resolved members, not on the names the IL or the spec spell: a member declared
        // on a base type is the same read whichever type the call site or the evidence row names it through.
        var resolvedLevelReads = levelReads.Select(Resolved).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.exact-level-reads", resolvedLevelReads.SequenceEqual(expectedLevelReads),
            "rows=" + specReadbacks.Length + " resolved=" + declaredReads.Count(x => x.Member != null)
            + " actual=" + resolvedLevelReads.Length + " expected=" + expectedLevelReads.Length
            + " missing=" + string.Join(", ", resolvedLevelReads.Except(expectedLevelReads))
            + " extra=" + string.Join(", ", expectedLevelReads.Except(resolvedLevelReads)));
        Check("native.every-game-read-is-classified", gameReads.Length == identityReads.Length + levelReads.Length
            && engineCalls.Length > 0,
            "game reads=" + gameReads.Length + " player=" + identityReads.Length + " level=" + levelReads.Length
            + " engine=" + engineCalls.Length);
        foreach (var (row, member) in declaredReads)
        {
            string id = row.GetProperty("id").GetString()!;
            Check("readback." + id + ".game-member", member != null && member.IsStatic == row.GetProperty("isStatic").GetBoolean(),
                row.GetProperty("signature").GetString() + (member == null ? "" : " declared by " + member.DeclaringType.FullName));
        }
        foreach (var (row, _) in declaredWrites)
        {
            string id = row.GetProperty("id").GetString()!;
            string signature = row.GetProperty("signature").GetString()!;
            // A write row is pinned by its whole signature and not by its member name: an overloaded game name
            // has several forms, so the row has to name the declaration it means, with the static flag the
            // interop assembly carries.
            //
            // The signature is compared against the calls the plugin really makes (`writeSignatures`, resolved
            // above) rather than against the declaration's own `FullName`, because one declaration has two legal
            // spellings: a call through a generic instance names the closed instantiation with the call's own type
            // arguments (`SNet_StateReplicator`2<A,B>::set_State(!0)`), while the type definition names the open
            // one (`SNet_StateReplicator`2::set_State(T)`). A row that pinned an overload the plugin does not
            // call, or the wrong member of the declared type, still fails.
            var owner = gameTypes.FirstOrDefault(t => t.FullName == row.GetProperty("type").GetString());
            var declaredOnType = owner == null ? Array.Empty<MethodDefinition>()
                : Chain(owner).SelectMany(t => t.Methods).Where(m => m.Name == SignatureName(signature)).ToArray();
            Check("write." + id + ".game-member",
                declaredOnType.Length > 0
                && declaredOnType.All(m => m.IsStatic == row.GetProperty("isStatic").GetBoolean())
                && writeSignatures.Contains(signature, StringComparer.Ordinal),
                string.Join(" | ", declaredOnType.Select(m => m.FullName)));
        }
        var harmonyCalls = calls.Where(m => Scope(m).Contains("Harmony", StringComparison.Ordinal)).Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.harmony-declarations-only", harmonyCalls.Length == 0, string.Join(", ", harmonyCalls));
        Check("native.runtime-hook-declarations", calls.Any(m => Member(m) == "ForgeEngine.GameBindings.NativeHookRegistration::Declare"));

        bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
        var hooks = nativeTypes.Where(Patch).ToArray();
        Check("native.exact-hook-set", hooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(specHooks.Select(h => h.GetProperty("hook").GetString()!).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(", ", hooks.Select(t => t.Name)));
        // PostEvent and the two replication entries are frozen and audited, but ForgeCore patches them and Runtime binds them to its
        // world. Keep the Player hook set exact and require each shared target, Runtime's one subscription to ForgeCore's bracket and
        // Player's leased, non-patching observer.
        var shared = specDocument.RootElement.GetProperty("sharedObservations").EnumerateArray().ToArray();
        Check("native.shared-observation-set", shared.Select(row => row.GetProperty("hook").GetString()).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(new[] { "GamePlayerMembership.ReplicationDespawn", "GamePlayerMembership.ReplicationSpawn", "GamePostEvents" }));
        foreach (var row in shared)
        {
            var id = "shared." + row.GetProperty("hook").GetString();
            Check(id + ".owner", row.GetProperty("owner").GetString() == host.Name.Name);
            var channel = host.MainModule.Types.SingleOrDefault(t => t.FullName == row.GetProperty("channel").GetString());
            Check(id + ".channel", channel != null);
            if (channel == null) continue;
            var target = gameTypes.Where(t => t.FullName == row.GetProperty("type").GetString())
                .SelectMany(t => t.Methods).SingleOrDefault(m => m.FullName == row.GetProperty("signature").GetString());
            Check(id + ".target", target != null && target.IsStatic == row.GetProperty("isStatic").GetBoolean()
                && target.IsVirtual == row.GetProperty("isVirtual").GetBoolean());
            if (target == null) continue;
            var channelCode = new[] { channel }.Concat(channel.NestedTypes).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
            var initializer = channel.Methods.Single(m => m.IsConstructor && m.IsStatic).Body.Instructions;
            Check(id + ".target-lookup", initializer.Any(i => i.OpCode.Code == Code.Ldtoken && i.Operand is TypeReference t && t.FullName == target.DeclaringType.FullName)
                && initializer.Any(i => i.OpCode.Code == Code.Ldstr && Equals(i.Operand, target.Name)));
            // ForgeCore patches the method; the channel only subscribes to its bracket and writes no definition of its own.
            Check(id + ".forgecore-bracket", channelCode.Any(i => i.OpCode.Code == Code.Newobj && i.Operand is MethodReference m && m.DeclaringType.FullName == "ForgeEngine.GameBindings.SharedBracket")
                && !channelCode.Any(i => i.OpCode.Code == Code.Newobj && i.Operand is MethodReference m && m.DeclaringType.FullName == "ForgeEngine.GameBindings.NativeHookDefinition")
                && channelCode.Any(i => i.OpCode.Code == Code.Ldstr && Equals(i.Operand, row.GetProperty("writer").GetString()))
                && channelCode.Any(i => i.OpCode.Code == Code.Ldsfld && i.Operand is FieldReference f && f.DeclaringType.FullName == "ForgeCore.GameHooks" && f.Name == row.GetProperty("bracket").GetString()));
            // The observer only leases: it never patches, and the consumer it names reaches the channel's Subscribe.
            var observer = nativeTypes.SingleOrDefault(t => t.FullName == row.GetProperty("observer").GetString());
            var observerCode = observer == null ? Array.Empty<Instruction>()
                : new[] { observer }.Concat(observer.NestedTypes).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
            var subscriptions = observerCode.Where(i => i.Operand is MethodReference m && m.DeclaringType.FullName == channel.FullName && m.Name == "Subscribe").ToArray();
            Check(id + ".leased-player-observer", observer != null && !Patch(observer) && subscriptions.Length >= 1
                && observerCode.Any(i => i.OpCode.Code == Code.Ldstr && Equals(i.Operand, row.GetProperty("consumer").GetString())));
        }        // The parameters a postfix body loads, by the ldarg/ldarga that loads them. Whether a hook may read its
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
                if (!Injected(parameter, method)) yield return parameter;
            if (alias.Count == 0) yield break;
            foreach (var holder in new[] { method }.Concat(display.SelectMany(t => t.Methods)))
            {
                if (!holder.HasBody) continue;
                foreach (var instruction in holder.Body.Instructions)
                    if (instruction.OpCode.Code is Code.Ldfld or Code.Ldflda or Code.Ldsfld or Code.Ldsflda
                        && instruction.Operand is FieldReference field && alias.TryGetValue(field.Name, out var aliasName))
                    {
                        var captured = method.Parameters.Single(p => p.Name == aliasName);
                        if (!Injected(captured, method)) yield return captured;
                    }
            }
        }
        // Harmony's own injected parameters are not arguments of the patched method: `__instance` is the instance
        // the audit declares separately, `__result` is the return value a postfix may read, and `__state` is the
        // slot a prefix writes for its postfix. None of them is a value the native body was handed, so the read
        // set a row declares is the arguments alone.
        bool Injected(ParameterDefinition parameter, MethodDefinition method)
            => parameter.Name == "__result" || parameter.Name == "__state"
                || (parameter.Name == "__instance" && Patched(method) == parameter.ParameterType.FullName);
        // The type a patch class declares it patches, read off the class's own HarmonyPatch attribute. The
        // attribute's own arguments carry the type first and the member name second, so the first type reference
        // is the patched type.
        string? Patched(MethodDefinition method)
            => method.DeclaringType.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch")
                ?.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().FirstOrDefault()?.FullName;
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
            string name = spec.GetProperty("hook").GetString()!;
            var hook = hooks.SingleOrDefault(t => t.Name == name);
            if (hook == null) { Check("hook." + name + ".present", false); continue; }
            var patch = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
            var type = patch.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
            var method = patch.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
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
            // The class carries exactly one method of the declared kind: a prefix beside a postfix is Harmony's
            // own state capture rather than a second hook, and a class with two of the same kind would have one
            // body the audit never reads.
            var kindMethods = patches.Where(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "Harmony" + Kind(kind))).ToArray();
            var patchBody = kindMethods.Length == 1 && kindMethods[0].IsStatic ? kindMethods[0] : null;
            Check("hook." + name + ".single-" + kind, patchBody != null, string.Join(", ", patches.Select(p => p.Name)));
            // The patch shape is declared by the hook, not assumed by the audit (see patchVocabulary in the
            // spec): the identity readbacks take the patched instance and never read it, the
            // life and state readbacks take and read it, and the game-event entry has no instance but reads the
            // entry's own arguments. A declared instance must be the patched type, and every declared argument must be
            // declared with the exact interop type; the read set is compared both ways.
            var shape = spec.GetProperty(kind);
            string? instance = shape.GetProperty("instanceParameter").GetString();
            var expectedParameters = (instance == null ? Array.Empty<(string, string)>() : new[] { (instance, type.FullName) })
                .Concat(shape.GetProperty("arguments").EnumerateArray().Select(a => (a.GetProperty("name").GetString()!, a.GetProperty("type").GetString()!))).ToArray();
            // The declared shape is the patched method's own parameters, which is what a row documents: the
            // instance when the row declares one, then the method's arguments. Harmony's own slots — the state a
            // prefix writes and the result a postfix may read — are the patch's parameters and not the method's,
            // so a row declares neither.
            var parameters = patchBody?.Parameters.Where(p => p.Name is not ("__state" or "__result")).Select(p => (p.Name, p.ParameterType.FullName)).ToArray() ?? Array.Empty<(string, string)>();
            Check("hook." + name + "." + kind + "-parameters", parameters.SequenceEqual(expectedParameters),
                string.Join(", ", parameters.Select(p => p.Item1 + " " + p.Item2)));
            string[] reads = shape.GetProperty("reads").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string[] loaded = patchBody == null ? Array.Empty<string>()
                : parameterReads(patchBody).Select(p => p.Name).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Check("hook." + name + ".parameter-reads", loaded.SequenceEqual(reads), string.Join(", ", loaded));
            // The parameters a prefix changes, by the by-ref parameters its body stores through. A postfix
            // changes nothing, so its write set is empty by definition and a row that declared one fails here.
            string[] shapeWrites = shape.GetProperty("writes").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string[] stored = patchBody == null ? Array.Empty<string>()
                : patchBody.Parameters.Where(p => IsStored(patchBody, p)).Select(p => p.Name)
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
            // The gate is the session entry (`PlayerHost.Session`).
            string session = spec.GetProperty("session").GetString()!;
            var patchCalls = (patchBody == null ? Array.Empty<MethodDefinition>() : Callback(patchBody))
                .Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
            bool reachesSession = patchCalls.Any(m => Member(m) == "ForgePlayer.Native.PlayerHost::get_Session");
            string? declaredGuard = shape.GetProperty("guard").GetString();
            Check("hook." + name + ".guarded-by-session", session == "off"
                ? declaredGuard == null
                : reachesSession && patchCalls.Any(m => Member(m) == "ForgePlayer.Native.PlayerPluginSession::" + declaredGuard),
                session + " " + declaredGuard);
        }

        foreach (var assembly in game) assembly.Dispose();
        return checks;
    }

    /// <summary>Writes the whole audit to the file `FORGE_PLAYER_LAYOUT_DUMP` names, one check per line. The
    /// assertion message only carries the failures, and rebuilding the evidence file from them loses the detail
    /// of a check that passed with a list: this dump is the audit's own reading, in its own order.</summary>
    private static void Dump(List<CheckRow> checks)
    {
        string path = Environment.GetEnvironmentVariable("FORGE_PLAYER_LAYOUT_DUMP") ?? "";
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
