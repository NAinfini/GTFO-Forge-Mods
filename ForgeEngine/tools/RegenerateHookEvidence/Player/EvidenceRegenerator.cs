using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.HookEvidence;
using Mono.Cecil;
using static ForgeEngine.HookEvidence.HookRows;

namespace ForgePlayer.Tests.PlayerNativeLayout;

/// <summary>
/// Rewrites the member rows of `evidence/player-hooks.json` from the compiled plugin and the interop assemblies:
/// one readback row per game member the plugin reads, one write row per non-getter member it calls, and one hook
/// row per Harmony patch class with the target the interop assembly really declares.
///
/// The audit in this project is what decides whether those rows are right, and it compares the whole set both
/// ways, so this is not a second opinion: it is the same reading of the same assemblies, written down. A row that
/// already exists keeps its prose and its `rva`; a member the plugin stopped calling loses its row.
/// </summary>
internal static class EvidenceRegenerator
{
    /// <summary>Everything the rows are written from: the interop types, and every game member the plugin calls
    /// together with the plugin's own patch classes.</summary>
    internal sealed record Inputs(IReadOnlyList<TypeDefinition> GameTypes, IReadOnlyList<TypeDefinition> NativeTypes,
        IReadOnlyList<Call> Calls, IReadOnlyList<JsonObject> Hooks);

    /// <summary>Reads the plugin and the interop assemblies the way the audit does and answers the sets the rows
    /// are written from. The assemblies are the caller's: this only reads them.</summary>
    internal static Inputs Read(AssemblyDefinition engine, AssemblyDefinition framework, IReadOnlyList<AssemblyDefinition> gameAssemblies, JsonDocument spec)
    {
        var gameTypes = GameTypes(gameAssemblies);
        var nativeTypes = ForgeTestInputs.DomainTypes.Native(engine, framework, "Player", "ForgeEngine.GameBindings.GameCourseNodes");
        var calls = Calls(nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>(), gameTypes, gameAssemblies);
        var hooks = spec.RootElement.GetProperty("hooks").EnumerateArray().Select(x => JsonNode.Parse(x.GetRawText())!.AsObject()).ToArray();
        return new Inputs(gameTypes, nativeTypes, calls, hooks);
    }

    internal static void Run(Inputs inputs, string specPath, string outputPath, string dumpPath)
    {
        var spec = JsonNode.Parse(File.ReadAllText(specPath))!.AsObject();
        var (previous, previousWrites) = Previous(spec, inputs.GameTypes);
        var previousHooks = inputs.Hooks.Where(h => h["hook"] != null).ToDictionary(h => (string)h["hook"]!, StringComparer.Ordinal);
        var unresolved = new List<string>();
        // The dump is optional: the hook rows' RVAs are read out of it when the build it was made from is the
        // one the spec froze, and the rows keep the RVAs they already carry when it is not.
        var dump = Dump.Open(dumpPath, (string?)spec["dumpSha256"] ?? "", Console.WriteLine);

        // One write row per declaration and not per name: a name the game overloads has one row per form the
        // plugin calls, because the audit compares the whole signature set and a dropped form reads as a call
        // nobody reviewed.
        var writeCalls = inputs.Calls.Where(c => IsWrite(c, ReadSpellings, QueryMembers)).ToArray();
        var writeMembers = writeCalls.Select(c => c.Member).ToHashSet(StringComparer.Ordinal);
        var forms = writeCalls.GroupBy(c => c.Member, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var writes = new JsonArray(writeCalls.Select(call => (JsonNode)(previousWrites.TryGetValue(call.Signature, out var row)
            ? row.DeepClone() : WriteRow(call, forms[call.Member], inputs.GameTypes))).ToArray());
        var unclassified = new List<string>();
        var readbacks = Readbacks(inputs.Calls.Where(c => !writeMembers.Contains(c.Member)), previous, inputs.GameTypes,
            member => GameRead(member) || LevelRead(member), unclassified);

        spec["hooks"] = Hooks(inputs, previousHooks, dump, unresolved);
        spec["readbacks"] = readbacks;
        spec["writes"] = writes;
        Write(spec, outputPath, "");
        if (unresolved.Count != 0)
            Console.WriteLine("unresolved hooks (no interop method): " + string.Join(" | ", unresolved));
        if (unclassified.Count != 0)
            Console.WriteLine("game reads the audit does not classify: " + string.Join(" | ", unclassified));
    }

    /// <summary>One row per patch class the plugin carries, with the target, the static flag, the patch kind and
    /// that kind's parameter shape read off the compiled plugin and the interop assembly. A row that already
    /// existed keeps its readback prose, which is the reading of the binary this run does not repeat, and its
    /// `rva` when the dump it was read from is the one this run can open.</summary>
    private static JsonArray Hooks(Inputs inputs, IReadOnlyDictionary<string, JsonObject> previous, Dump? dump, List<string> unresolved)
    {
        var rows = new JsonArray();
        foreach (var hook in inputs.NativeTypes.Where(Patch).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var patch = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
            var type = patch.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
            var name = patch.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
            // The patch kind is what the plugin declares rather than what the schema prefers. A class that
            // carries a postfix is a postfix hook: the postfix is the body that reads the state the write left
            // behind, and a prefix beside it is Harmony's own `__state` capture rather than a second kind. Only a
            // class with no postfix at all is a prefix hook, which is where the patched body has to be
            // pre-empted.
            var prefix = hook.Methods.FirstOrDefault(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix"));
            var body = hook.Methods.FirstOrDefault(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix"))
                ?? prefix ?? throw new InvalidOperationException("No Harmony patch method on " + hook.Name);
            string kind = body.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix") ? "postfix" : "prefix";
            var owner = inputs.GameTypes.FirstOrDefault(t => t.FullName == type.FullName);
            var target = owner == null ? null : Target(inputs.GameTypes, owner, patch, name);
            // A patch whose target the interop assembly does not declare as a method cannot be installed by
            // Harmony at all: a name that is a field or a property's own backing member is not a target. A name
            // the interop assembly declares more than once is resolved to the form the patch itself names, and
            // one that stays ambiguous is reported the same way: a row for one form would claim a hook the other
            // form may be the target of.
            if (target == null)
            {
                unresolved.Add(hook.Name + " -> " + type.FullName + "::" + name);
                continue;
            }
            // The guard is the session entry the whole patch reaches the half through. A hook that guards inside
            // a lambda the compiler moved into the patch class's own display class still names the same entry, so
            // the search covers the body and every method under the class.
            // A hook that reaches the session names the guard its whole callback runs inside. A hook that only
            // maintains patch-level state — the damage-kind token a receive entry sets for the accept path to
            // read, and the native extra-information row a line of this model owns — has no guard: what it
            // touches is this package's own table, and it answers nothing while the half it belongs to is not
            // attached. `session: off` is how such a row says so instead of naming a guard it does not call.
            var guard = Guard(body, "ForgePlayer.Native.PlayerPluginSession", name => name.StartsWith("Guard", StringComparison.Ordinal));
            string? session = guard == null ? null : "on";
            var arguments = new JsonArray();
            var reads = new JsonArray();
            var writes = new JsonArray();
            foreach (var parameter in body.Parameters)
            {
                if (parameter.Name == "__instance" && parameter.ParameterType.FullName == type.FullName) continue;
                // `__state` and `__result` are Harmony's own slots rather than arguments of the patched method,
                // so they are not part of the parameter list a row declares.
                if (parameter.Name is "__state" or "__result") continue;
                arguments.Add(new JsonObject { ["name"] = parameter.Name, ["type"] = parameter.ParameterType.FullName });
            }
            // Harmony's own injected parameters are not arguments of the patched method: the instance, `__result`
            // and `__state` are not values the native body was handed, so the read set is the arguments alone.
            foreach (string read in Loaded(body, p => (p.Name == "__instance" && p.ParameterType.FullName == type.FullName)
                || p.Name is "__result" or "__state"))
                reads.Add(read);
            // The write set is the one the audit reads off the compiled body: a parameter the patched method
            // reads back changed. The only parameter a patch can change is a by-ref one, and only where the body
            // stores through it — a `ref` argument the body hands to a helper the audit does not open is not a
            // store the audit sees, so it is not a write this row may claim either. Harmony's own `__state` is not
            // an argument of the patched method at all — a prefix writes it for the postfix to read — so it is a
            // parameter of the patch and never a write of the patch's.
            foreach (var parameter in body.Parameters)
                if (parameter.ParameterType.IsByReference && parameter.Name != "__state" && Stored(body, parameter))
                    writes.Add(parameter.Name);
            previous.TryGetValue(hook.Name, out var known);
            var row = new JsonObject
            {
                ["hook"] = hook.Name,
                ["assembly"] = target.Module.Assembly.Name.Name + ".dll",
                ["type"] = target.DeclaringType.FullName,
                ["name"] = target.Name,
                ["signature"] = target.FullName,
                ["isVirtual"] = target.IsVirtual,
                ["isStatic"] = target.IsStatic,
                ["rva"] = dump?.Rva(target) ?? known?["rva"]?.DeepClone(),
                ["patch"] = kind,
                ["session"] = session ?? "off",
                [kind] = new JsonObject
                {
                    ["instanceParameter"] = body.Parameters.Count > 0 && body.Parameters[0].Name == "__instance"
                        && body.Parameters[0].ParameterType.FullName == type.FullName ? "__instance" : null,
                    ["arguments"] = arguments,
                    ["reads"] = reads,
                    ["writes"] = writes,
                    ["guard"] = guard
                }
            };
            // The `readback` prose names the native member the callback really reads. Deriving that from the
            // binary is what this run does not do — the reading runs through the half's own observers — so a row
            // that already carries a reading keeps it and a hook nobody has read yet carries no field at all. A
            // sentence invented here would be prose the evidence does not support, which is worse than silence.
            if (known?["readback"]?.DeepClone() is { } reading) row["readback"] = reading;
            // The shared-address vocabulary is a reviewed reading of the dump, not of the compiled plugin: a hook
            // whose target address holds more than one declaration has to name the others and say why the patch is
            // right for each. This run cannot re-derive that list, so it carries the row's own fields over rather
            // than dropping a reviewed fact on every regeneration. `MapNativeEvidence` is what checks them.
            foreach (string shared in new[] { "rvaShared", "dumpEntriesAtRva", "sharedWith", "sharedReason" })
                if (known?[shared]?.DeepClone() is { } value) row[shared] = value;
            rows.Add(row);
        }
        return rows;
    }

    private static bool Patch(TypeDefinition type)
        => type.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");

    /// <summary>Whether the audit classifies a member as one of the player reads: the agent, its owner and
    /// network identity, its damage, locomotion, stamina and downed state, the inventory a player carries and
    /// wields, the interaction a player completes, the input binding a key poll reads, the marker drawn over a
    /// teammate, the camera ray an aim is read from and the state-machine base the movement-state query reads
    /// its entry time from.</summary>
    internal static bool GameRead(string member) =>
        member is "Localization.ITextLocalizationService::get_CurrentLanguage" or "Localization.Text::get_TextLocalizationService" or "AgentModifierManager::ApplyModifier"
        || member.StartsWith("Player.", StringComparison.Ordinal)
        || member.StartsWith("SNetwork.", StringComparison.Ordinal) || member.StartsWith("Agents.", StringComparison.Ordinal)
        || member.StartsWith("Dam_", StringComparison.Ordinal) || member.StartsWith("PLOC_", StringComparison.Ordinal)
        || member.StartsWith("PlayerInventoryBase::", StringComparison.Ordinal) || member.StartsWith("ItemEquippable::", StringComparison.Ordinal)
        || member.StartsWith("Item::", StringComparison.Ordinal) || member.StartsWith("PlayerInteraction::", StringComparison.Ordinal)
        || member.StartsWith("Interact_Timed::", StringComparison.Ordinal) || member.StartsWith("Interact_Timed/", StringComparison.Ordinal) || member.StartsWith("NavMarker::", StringComparison.Ordinal)
        // The input mapper a key poll reads: the engine-level binding table the game declares itself, in no
        // namespace, which is why the player prefix does not reach it.
        || member.StartsWith("InputMapper::", StringComparison.Ordinal)
        || member.StartsWith("PlayerStamina::", StringComparison.Ordinal)
        || member == "Gear.GearIDRange::GetChecksum"
        // The lobby bot-slot program reads the game state, the active expedition and the catalogue identity of a gear.
        || member is "GameStateManager::get_CurrentStateName" or "RundownManager::get_ActiveExpedition"
        || member.StartsWith("Gear.GearIDRange::get_Playfab", StringComparison.Ordinal)
        || member.StartsWith("StateMachines.StateMachine`1::", StringComparison.Ordinal)
        || member.StartsWith("PlaceNavMarkerOnGO::", StringComparison.Ordinal)
        // The player's own first-person camera, declared in no namespace: the ray an aim read takes.
        || member.StartsWith("FPSCamera::", StringComparison.Ordinal)
        // The local player's own screen the device writers blank: the interface layer and its readouts, and the
        // map page, each declared by the game in no namespace or in `CellMenu`.
        || member.StartsWith("GuiManager::", StringComparison.Ordinal) || member.StartsWith("PlayerGuiLayer::", StringComparison.Ordinal)
        || member.StartsWith("CellMenu.CM_PageMap::", StringComparison.Ordinal);

    /// <summary>Whether the audit classifies a member as one of the level reads a player is answered through:
    /// the course node a player stands on, the zone, layer and area that node belongs to, and the player data
    /// block the regeneration delay is read from.</summary>
    internal static bool LevelRead(string member) => member.StartsWith("LevelGeneration.", StringComparison.Ordinal)
        || member.StartsWith("AIGraph.", StringComparison.Ordinal) || member.StartsWith("GameData.", StringComparison.Ordinal)
        // The world collision mask the cleanup pose probe aims its floor ray with, declared in no namespace.
        || member == "LayerManager::get_MASK_WORLD"
        // The dimension a warp names, declared in no namespace: its own landing point for a player.
        || member.StartsWith("LG_Dimension::", StringComparison.Ordinal);

    /// <summary>The pure query spellings: game members whose name does not start with `get_` and which are
    /// reads all the same — the cast and operator spellings the identity code uses and the one game accessor
    /// spelled without `get_`. The audit reads this list; there is no second copy.</summary>
    internal static readonly string[] ReadSpellings =
        { "TryCast", "op_Equality", "op_Inequality", "op_Implicit", "PlayerSlotIndex" };

    /// <summary>The query members whose names the spellings above do not catch: game members that return
    /// information without changing any, because the game spells them as a lookup, a question or a value
    /// construction. Each is named here rather than matched by a spelling rule, and each is declared as a read: a
    /// pure query counted as a write would make the observation half that calls it look like an action layer.
    /// The generator writes a readback row for each one instead of a write row, and the audit classifies by this
    /// same list — one list, so the two cannot disagree.</summary>
    internal static readonly string[] QueryMembers =
    {
        "AgentModifierManager::ApplyModifier",
        // Returned location reconstruction, not a player-state write.
        "Player.PlayerSync::GetPlayerLocationReconstruction",
        "Gear.GearIDRange::GetChecksum",
        "Player.PlayerBackpack::GetBackpackSlot",
        "Player.PlayerBackpack::TryGetBackpackItem",
        "SNetwork.SNetStructs/pPlayer::TryGetPlayer",
        "ItemEquippable::GetClassAmmoInPackAbs",
        "ItemEquippable::GetClassAmmoMaxCap",
        "Player.PlayerAgent::SampleWarpPosition",
        // The lookup of the dimension a warp names and that dimension's landing point for a player.
        "LevelGeneration.Dimension::GetDimension",
        "LG_Dimension::GetSpawnPoint",
        // The landing solver's own read-only gates, inspected only after it refuses a test capture.
        "AIGraph.AIG_GeomorphNodeVolume::TryGetGeomorphVolume",
        "AIGraph.AIG_VoxelNodeVolume::TryGetPillar",
        "AIGraph.AIG_VoxelNodePillar::TryGetVoxelNode",
        "AIGraph.AIG_NodeCluster::TryGetNodeCluster",
        // The bot's own question whether the game's registry of recently failed actions forbids a descriptor.
        "Player.PlayerAIBot::IsActionForbidden",
        // The key poll's own lookup of the local player it reads the binding for.
        "Player.PlayerManager::GetLocalPlayerAgent",
        "Player.PlayerBackpackManager::TryGetBackpack",
        // The health readback's own read of the healer an add-health packet names.
        "Agents.pAgent::TryGet"
    };
}
