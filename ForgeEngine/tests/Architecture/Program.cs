using System.Text.Json;
using ForgeEngine.Framework;

internal static class ArchitectureSuite
{
    internal static int Run(string[] args)
    {

        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            checks++;
        }


        RuntimeModule PlayerModule()
        {
            var description = ForgePlayer.ModuleDefinition.Describe();
            var handlers = description.CommandHandlers.ToDictionary(name => name,
                name => (CommandHandler)(_ => throw new InvalidOperationException(
                    "The architecture check cannot execute the native " + name + " handler.")), StringComparer.Ordinal);
            var evaluators = description.EvaluatorHandlers.ToDictionary(name => name,
                name => (EvaluatorHandler)((_, _) => throw new InvalidOperationException(
                    "The architecture check cannot evaluate the native " + name + " handler.")), StringComparer.Ordinal);
            return ForgePlayer.ModuleDefinition.Create(handlers, evaluators);
        }

        RuntimeModule MapModule()
        {
            var description = ForgeMap.ModuleDefinition.Describe();
            var handlers = description.CommandHandlers.ToDictionary(name => name,
                name => (CommandHandler)(_ => throw new InvalidOperationException(
                    "The architecture check cannot execute the native " + name + " handler.")), StringComparer.Ordinal);
            var evaluators = description.EvaluatorHandlers.ToDictionary(name => name,
                name => (EvaluatorHandler)((_, _) => throw new InvalidOperationException(
                    "The architecture check cannot evaluate the native " + name + " handler.")), StringComparer.Ordinal);
            return ForgeMap.ModuleDefinition.Create(handlers, evaluators);
        }

        RuntimeModule EnemyModule()
        {
            var description = ForgeEnemy.ModuleDefinition.Describe();
            var handlers = description.CommandHandlers.ToDictionary(name => name,
                name => (CommandHandler)(_ => throw new InvalidOperationException(
                    "The architecture check cannot execute the native " + name + " handler.")), StringComparer.Ordinal);
            var evaluators = description.EvaluatorHandlers.ToDictionary(name => name,
                name => (EvaluatorHandler)((_, _) => throw new InvalidOperationException(
                    "The architecture check cannot evaluate the native " + name + " handler.")), StringComparer.Ordinal);
            return ForgeEnemy.ModuleDefinition.Create(handlers, evaluators, static _ => true);
        }

        var definitions = new (Type Type, Func<RuntimeModule> Create)[]
        {
    (typeof(ForgeEngine.Trigger.ModuleDefinition), ForgeEngine.Trigger.ModuleDefinition.Create),
    // Player registers before the two packages that name its player rows.
    (typeof(ForgePlayer.ModuleDefinition), PlayerModule),
    (typeof(ForgeMap.ModuleDefinition), MapModule),
    (typeof(ForgeEnemy.ModuleDefinition), EnemyModule),
    // Weapon's declaration takes the bodies its native half supplies; this check registers the declaration
    // without any of them, so its rows are the observed ones and nothing here is executable. A method group
    // cannot bind those optional parameters, hence the lambda.
    (typeof(ForgeWeapon.ModuleDefinition), () => ForgeWeapon.ModuleDefinition.Create()),
        };
        Check(ForgeEnemy.ModuleDefinition.ProviderId == "forge.module.gtfo.enemy", "Enemy provider identity preserved");
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.architecture.check", "1.0.0", RuntimeKernel.ApiVersion, "offline-no-game"));
        var handles = new List<RuntimeModuleHandle>();
        // These are the same builtin contract providers a host registers before any domain package. Domain declarations
        // bind their canonical rows but do not copy them.
        handles.Add(kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off));
        handles.Add(kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off));
        handles.Add(kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off));
        handles.Add(kernel.RegisterModule(ObservationContracts.Module(), RuntimeLogLevel.Off));
        foreach (var definition in definitions)
        {
            var module = definition.Create();
            handles.Add(kernel.RegisterModule(module, RuntimeLogLevel.Off));
        }
        Check(handles.Select(h => h.ProviderId).Distinct().Count() == definitions.Length + 4, "all providers can coexist");
        var manifestBeforeDuplicate = kernel.ExportManifest();
        using (var manifest = JsonDocument.Parse(manifestBeforeDuplicate))
        {
            var registry = manifest.RootElement.GetProperty("registry");
            Check(registry.GetProperty("providers").GetArrayLength() == definitions.Length + 4, "one registry contains all providers");
            var weapon = ForgeWeapon.ModuleDefinition.ProviderId;
            var owners = registry.GetProperty("capabilities").EnumerateArray().Select(c => c.GetProperty("owner").GetString()).ToArray();
            // The count comes from the declaration under test, not from a number that has to be edited whenever a package
            // observes one more fact: what this file checks is that every declared row lands under its own provider.
            var declaredWeapon = JsonDocument.Parse(ForgeWeapon.ModuleDefinition.Create().Declaration.Registry.GetRawText()).RootElement
                .GetProperty("capabilities").GetArrayLength();
            Check(owners.Count(o => o == weapon) == declaredWeapon,
                "every capability the Weapon declaration carries is owned by Weapon [" + declaredWeapon + "]");
            var trigger = ForgeEngine.Trigger.ModuleDefinition.ProviderId;
            var declaredTriggerCapabilities = JsonDocument.Parse(ForgeEngine.Trigger.ModuleDefinition.Create().Declaration.Registry.GetRawText()).RootElement
                .GetProperty("capabilities").GetArrayLength();
            var declaredTriggerBindings = JsonDocument.Parse(ForgeEngine.Trigger.ModuleDefinition.Create().Declaration.Registry.GetRawText()).RootElement
                .GetProperty("bindings").EnumerateArray().ToArray();
            Check(owners.Count(o => o == trigger) == declaredTriggerCapabilities,
                "every capability the Trigger declaration carries is owned by Trigger [" + declaredTriggerCapabilities + "]");
            Check(registry.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("id").GetString() == Blocks.ConditionPredicateCompare.Id)
                .GetProperty("owner").GetString() == trigger, "Trigger now owns the canonical compare condition");
            var bindings = registry.GetProperty("bindings").EnumerateArray()
                .Select(b => (Provider: b.GetProperty("providerId").GetString(), Role: b.GetProperty("role").GetString(), Id: b.GetProperty("id").GetString(),
                    Handler: b.GetProperty("handler").GetString())).ToArray();
            Check(bindings.Count(b => b.Provider == trigger) == declaredTriggerBindings.Length
                && declaredTriggerBindings.All(row => bindings.Any(b => b.Provider == trigger && b.Id == row.GetProperty("id").GetString()
                    && b.Role == row.GetProperty("role").GetString())), "Trigger registers exactly the bindings its own declaration carries");
            // Weapon binds every canonical Trigger row it observes beside the rows it executes itself, so the comparison is
            // the declared and the exported membership row by row — `(provider, binding id, role)` — and not a count: a
            // count cannot see a row that was declared and never registered, or one registered under another role, which
            // is exactly what this check fell behind on when the three execute rows joined the declaration. Both
            // directions are compared, so a row the registration carries but the declaration does not is refused too.
            var weaponDeclaration = JsonDocument.Parse(ForgeWeapon.ModuleDefinition.Create().Declaration.Registry.GetRawText()).RootElement;
            var declaredWeaponRows = weaponDeclaration.GetProperty("bindings").EnumerateArray()
                .Select(row => (Provider: weapon, Id: row.GetProperty("id").GetString()!, Role: row.GetProperty("role").GetString()!,
                    Handler: row.GetProperty("handler").GetString()!)).ToArray();
            var exportedWeaponRows = bindings.Where(b => b.Provider == weapon).Select(b => (b.Provider, b.Id!, b.Role!, b.Handler!)).ToArray();
            string Row((string? Provider, string? Id, string? Role, string? Handler) row) => row.Provider + "/" + row.Id + "/" + row.Role;
            Check(declaredWeaponRows.Select(Row).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(exportedWeaponRows.Select(Row).OrderBy(x => x, StringComparer.Ordinal)),
                "Weapon registers exactly the bindings its own declaration carries [declared=" + declaredWeaponRows.Length
                + " exported=" + exportedWeaponRows.Length + " missing="
                + string.Join(", ", declaredWeaponRows.Select(Row).Except(exportedWeaponRows.Select(Row)))
                + " extra=" + string.Join(", ", exportedWeaponRows.Select(Row).Except(declaredWeaponRows.Select(Row))) + "]");
            // Every row the declaration marks executable must have reached the registration with its own body and shape:
            // a declared execute row without a handler is a row a plan can bind and nothing can answer.
            var weaponModule = ForgeWeapon.ModuleDefinition.Create();
            var executable = declaredWeaponRows.Where(row => row.Role == "execute").ToArray();
            Check(executable.Length != 0 && executable.All(row => weaponModule.Handlers.ContainsKey(row.Handler)
                && weaponModule.Shapes.ContainsKey(row.Handler)),
                "every declared execute row carries its handler and shape [" + string.Join(", ", executable.Select(Row)) + "]");
            // The source-backed execute rows are declared by every process and answer through a source only the game half
            // attaches. This process declared the provider without that half, so each of those bodies is the refusing one:
            // the check is that the registration carries them and that no source is attached here — a process with no game
            // half must refuse by code, which is the behaviour the ForgeWeapon suites invoke directly (see
            // tests/WeaponOverride/WeaponProfileRowTests and tests/InventoryActions/DetonateRowTests).
            Check(ForgeWeapon.WeaponProfileWrites.Installed == null && ForgeWeapon.DetonateWrites.Installed == null
                && ForgeWeapon.WeaponDebugSwitchWrites.Installed == null,
                "a process with no game half attached a native source to a source-backed row");
            Check(weaponModule.Handlers.ContainsKey(ForgeWeapon.WeaponProfileContract.HandlerName)
                && weaponModule.Handlers.ContainsKey(ForgeWeapon.DetonateContract.HandlerName)
                && weaponModule.Handlers.ContainsKey(ForgeWeapon.WeaponDebugSwitchContract.HandlerName),
                "a source-backed execute row is declared without the body that refuses when no source is attached");
            Check(manifest.RootElement.GetProperty("bindingSupport").EnumerateArray()
                .All(s => s.GetProperty("verification").GetString() == "implementation-only"), "no provider claims game verification");
        }
        try
        {
            kernel.RegisterModule(definitions[0].Create(), RuntimeLogLevel.Off);
            throw new Exception("FAIL: duplicate provider accepted");
        }
        catch (RuntimeContractException error)
        {
            Check(error.Code == "provider-conflict", "duplicate provider fails with the ownership error");
        }
        Check(kernel.ExportManifest() == manifestBeforeDuplicate, "rejected registration leaves the registry unchanged");
        kernel.BeginWorld(1);
        kernel.Advance(0, true);
        Check(kernel.LoadedPlans == 0 && kernel.QueuedEvents == 0, "registration starts no gameplay or background work");
        foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose();
        foreach (var handle in handles) handle.Dispose();
        Check(handles.All(h => !h.IsRegistered), "module disposal is idempotent");
        using (var manifest = JsonDocument.Parse(kernel.ExportManifest()))
            Check(manifest.RootElement.GetProperty("registry").GetProperty("providers").GetArrayLength() == 0, "all providers unregister cleanly");
        // The existing shared combat definitions moved into the SDK without claiming a receiver implementation.
        using (var combat = kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off))
        using (var control = kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off))
        using (var manifest = JsonDocument.Parse(kernel.ExportManifest()))
        {
            var registry = manifest.RootElement.GetProperty("registry");
            Check(combat.ProviderId == "forge.contract.combat", "existing canonical combat owner is preserved");
            var capabilities = registry.GetProperty("capabilities").EnumerateArray().ToArray();
            var bindings = registry.GetProperty("bindings").EnumerateArray().ToArray();
            // Both counts are what the two SDK declarations carry, so the vocabulary can grow without this file having to
            // restate a number; what is checked is that the registry holds exactly the declared rows and nothing else.
            int Declared(RuntimeModule module, string list)
                => JsonDocument.Parse(module.Declaration.Registry.GetRawText()).RootElement.GetProperty(list).GetArrayLength();
            Check(capabilities.Length == Declared(CombatContracts.Module(), "capabilities") + Declared(ControlContracts.Module(), "capabilities")
                && bindings.Length == Declared(ControlContracts.Module(), "bindings"),
                "shared combat and lifecycle definitions create exactly the control contract's own native bindings");
            Check(capabilities.Count(c => c.GetProperty("owner").GetString() == combat.ProviderId) == Declared(CombatContracts.Module(), "capabilities"),
                "every combat and enemy trigger definition belongs to the combat provider");
            Check(control.ProviderId == "forge.contract.control", "the branch capability has one native contract owner");
            var branch = capabilities.Single(c => c.GetProperty("id").GetString() == Blocks.ControlFlowBranch.Id);
            Check(branch.GetProperty("owner").GetString() == "forge.contract.control" && branch.GetProperty("kind").GetString() == "control",
                "the branch contract is a control capability owned by the control provider");
            var branchBinding = bindings.Single(b => b.GetProperty("id").GetString() == "forge.contract.control.binding.branch");
            Check(branchBinding.GetProperty("role").GetString() == "execute"
                && branchBinding.GetProperty("handler").GetString() == "runtime.control.branch", "the branch binding is registered without a handler function");
            Check(kernel.ExportManifest().Contains("runtime.control.branch", StringComparison.Ordinal), "the branch binding ships in the exported manifest");
        }
        // The melee hit ledger publishes one landed hit once. A key claimed again after its window must keep that newest
        // claim when the table is full: eviction drops the least recently claimed key, never the claim just made, and
        // the claim order holds one entry per key however often a key is claimed.
        {
            var ledger = new ForgeWeapon.MeleeHitLedger();
            EntityReference Player(int index) => new("gtfo.player:" + index, 1, 1);
            var target = new EntityReference("gtfo.enemy:1", 1, 1);
            Check(ledger.TryClaim(Player(0), target, 0, 0, 1), "a first hit is claimed");
            for (var other = 1; other < ForgeWeapon.MeleeHitLedger.Capacity - 1; other++) ledger.TryClaim(Player(other), target, 0, 1, 1);
            Check(ledger.TryClaim(Player(0), target, 0, 100, 1), "the same attacker, target and limb past the window is a new hit");
            Check(ledger.TryClaim(Player(-1), target, 0, 100, 1) && ledger.Count == ForgeWeapon.MeleeHitLedger.Capacity, "the ledger is full");
            Check(ledger.TryClaim(Player(-2), target, 0, 100, 1) && ledger.Count == ForgeWeapon.MeleeHitLedger.Capacity,
                "a full ledger takes a new hit by dropping one claim");
            Check(!ledger.TryClaim(Player(0), target, 0, 101, 1), "eviction kept the newest claim of a key claimed again");
            for (var tick = 200L; tick < 2200; tick += ForgeWeapon.MeleeHitLedger.WindowTicks) ledger.TryClaim(Player(0), target, 0, tick, 1);
            Check(ledger.OrderCount == ledger.Count, "repeated claims of one key do not grow the claim order");
        }
        // A client's reported impact names the map object it reached in the map domain's wire spelling. What is read back is
        // the reference that was written, of the world its message is read in and no other; a body that is not one is
        // refused rather than read as some object.
        {
            static byte[] Written(EntityReference reference)
            {
                var body = new MemoryStream();
                using (var writer = new BinaryWriter(body, System.Text.Encoding.UTF8, true)) ForgeMap.MapObjectWire.Write(writer, reference);
                return body.ToArray();
            }
            static EntityReference Read(byte[] body, long world)
                => ForgeMap.MapObjectWire.Read(new BinaryReader(new MemoryStream(body), System.Text.Encoding.UTF8), world);
            static bool Refused(byte[] body)
            {
                try { Read(body, 7); return false; }
                catch (Exception error) when (error is FormatException or EndOfStreamException) { return true; }
            }
            var door = new EntityReference(EntityKinds.MapObject + ":door/1/2/300/security", 7, 1);
            var model = new EntityReference(EntityKinds.MapObject + ":model/crate-a", 7, 3);
            var body = Written(door);
            Check(body.Length == 6 && Read(body, 7) == door && Read(Written(model), 7) == model,
                "a map object named in a message reads back as the reference that was written");
            Check(Read(body, 8) == door with { WorldEpoch = 8 } && Read(body, 8) != door,
                "a body carries no world of its own: read in another world it is that world's reference, not the shooter's");
            Check(Refused(body[..^1]) && Refused(body[..3]) && Refused(new byte[] { 9, 0, 0, 0, 1 })
                && Refused(new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0, 0, 1 }),
                "a cut body, a shape nobody writes and a negative coordinate are refused");
        }
        RecordPointProbe.Run(Check);
        // The walk above only proves anything while it still sees composition: these helpers compose exactly the way a record
        // point must not, one by concatenating and one through the handler an interpolated string compiles to. A comparison is
        // there to pin the other side of the rule, that reading a string is not composing one.
        Check(RecordPointProbe.BuildsText(typeof(RecordPointTextCases).GetMethod(nameof(RecordPointTextCases.Concatenating))!).Length > 0,
            "the record-point text walk missed a helper that concatenates");
        Check(RecordPointProbe.BuildsText(typeof(RecordPointTextCases).GetMethod(nameof(RecordPointTextCases.Interpolating))!).Length > 0,
            "the record-point text walk missed a helper that interpolates");
        Check(RecordPointProbe.BuildsText(typeof(RecordPointTextCases).GetMethod(nameof(RecordPointTextCases.Comparing))!).Length == 0,
            "the record-point text walk read a string comparison as composed text");
        Console.WriteLine($"Architecture checks: {checks} passed; 0 failed.");

        return Environment.ExitCode;
    }
}

static class RecordPointTextCases
{
    public static string Concatenating(string a, string b, int index) => a + b + index;
    public static string Interpolating(string name, int check) => $"{name} failed at {check}";
    public static bool Comparing(string a, string b) => a == b;
}
