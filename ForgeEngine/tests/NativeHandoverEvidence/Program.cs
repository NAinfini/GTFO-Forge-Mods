using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.Cecil;

internal static class NativeHandoverEvidenceSuite
{
    internal static int Run(string[] args)
    {

        // Read metadata as data, including the complete recall/interaction packets. Never load a game assembly
        // into the CLR. These signatures establish native structure, not successful recall or native ownership.
        if (args.Length != 3 || args[0] is not ("--check" or "--write"))
            throw new ArgumentException("Usage: NativeHandoverEvidence --check|--write <native-runtime.json> <QA BepInEx>");

        string catalogPath = Path.GetFullPath(args[1]), profile = Path.GetFullPath(args[2]);
        var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
        if (catalog["format"]!.GetValue<string>() != "infini-gtfo-native-runtime" || catalog["version"]!.GetValue<string>() != ForgeCore.FormatVersion.Current)
            throw new InvalidDataException("Unsupported native evidence catalog.");
        var sources = catalog["source"]!["assemblies"]!.AsArray();
        var interopIdentity = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(catalogPath)!, "../../Mods/ForgeEngine/evidence/interop-signatures-20403457.json")))!["assemblies"]!.AsArray();
        var types = catalog["types"]!.AsObject();

        string[] required =
        [
            "GuiManager", "NavMarkerLayer", "NavMarker", "NavMarkerOption", "ToolSyncManager",
    "Enemies.EnemyAgent", "Enemies.EnemyReplicator", "Enemies.pEnemySpawnData",
    "Agents.pEnemyAgent", "ToolSyncManager/pTagEnemy", "M_pTagEnemy", "M_pEnemySpawnData",
    "LevelGeneration.LG_SecurityDoor_Locks", "ChainedPuzzles.ChainedPuzzleInstance", "ChainedPuzzles.ChainedPuzzleManager",
    "ChainedPuzzles.pChainedPuzzleState", "ChainedPuzzles.pChainedPuzzleInteraction",
    "ChainedPuzzles.eChainedPuzzleStatus", "ChainedPuzzles.eChainedPuzzleInteraction", "M_ChainedPuzzleState",
    "WardenObjectiveManager", "Mastermind", "Mastermind/MastermindEvent", "SurvivalWave",
    "pSurvivalWaveSpawnData", "M_SurvivalWaveSpawnData", "SurvivalWave/eSurvivalWaveState",
    "GameData.ChainedPuzzleDataBlock", "GameData.SurvivalWaveSettingsDataBlock", "GameData.SurvivalWavePopulationDataBlock",
    "Item", "ItemEquippable", "Weapon", "Weapon/WeaponHitData", "Gear.BulletWeapon", "Gear.Shotgun",
    "Gear.BulletWeaponArchetype", "GameData.ArchetypeDataBlock", "GameData.RecoilDataBlock", "GameData.MinMaxValue", "eWeaponFireMode",
    "Gear.GearIDRange", "Gear.GearIDRange/pGearIDRange", "GearReplicator", "pGearSpawnData",
    "Player.pItemData", "Player.BackpackItem", "Player.PlayerBackpack",
    "Player.PlayerAgent", "Player.PlayerSync", "Player.PlayerLocomotion", "Player.PlayerLocomotion/PLOC_State",
    "Player.pPlayerControlsToggle", "Player.PlayerReplicator", "Player.pPlayerSpawnData", "Player.pPlayerSessionStatus",
    "M_PlayerSessionStatus", "SNetwork.SNet_Replicator", "SNetwork.SNet_DynamicReplicator`1",
    "SNetwork.SNet_StateReplicator`2", "SNetwork.SNet_ReplicationManager`1", "SNetwork.pReplicationData",
    "SNetwork.SNetStructs/pReplicator", "SNetwork.SNet_Capture", "SNetwork.eCapturePass",
    "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase",
    "pCheckpointState", "pCheckpointInteraction", "CheckpointManager", "Gear.BulletWeaponSynced",
    "Player.pPlayerLocationData", "Player.pPlayerResourceData", "Player.pPlayerLocomotion", "Player.pItemData_WithOwner", "Player.pItemData_Custom",
    "Enemies.EnemySync", "SNetwork.eBufferType", "SNetwork.pBufferData", "SNetwork.SNet_CaptureBuffer",
    "SNetwork.SNet_MasterManager", "SNetwork.SNet_SyncManager", "SNetwork.SNet_ReplicationManager",
    "SNetwork.SNet_ReplicationManager`2"
        ];

        // Large service types contribute only their actual H9 entry points. Packet/state types below retain every
        // ordinary member, so an omitted field cannot be mistaken for absence of a native recall obligation.
        var selected = new Dictionary<string, string[]>
        {
            ["GuiManager"] = ["NavMarkerLayer"],
            ["NavMarkerLayer"] = ["PlaceCustomMarker", "RemoveMarker", "m_markersActive", "RemoveMarkerForGO"],
            ["ToolSyncManager"] = ["Current", "m_tagEnemy", "OnLevelCleanup", "WantToTagEnemy", "DoTagEnemyValidation", "DoTagEnemy"],
            ["ChainedPuzzles.ChainedPuzzleManager"] = ["Current", "m_instances", "OnLevelCleanup", "CreatePuzzleInstance"],
            ["NavMarker"] = ["m_trackingObj", "PersistentBetweenRestarts", "SetColor"],
            ["WardenObjectiveManager"] = ["StopAlarms"],
            ["Item"] = ["Owner", "SyncID", "ReplicationWrapper"],
            ["ItemEquippable"] = ["ArchetypeData", "RecoilData", "GearIDRange"],
            ["Weapon"] = ["CastWeaponRay", "s_weaponRayData"],
            ["Player.PlayerAgent"] = ["RequestToggleControlsEnabled", "Owner", "Sync", "Locomotion", "PlayerSlotIndex"],
            ["Player.PlayerSync"] = ["SendToggleControlsEnabled", "IncomingSyncControlsToggle", "m_agent", "m_controlsTogglePacket", "Replicator", "TryCollectCaptureData",
        "m_locomotionPacket", "m_fireCountSyncRef", "FireCountSync", "FireCountCallBack", "m_locomotionData",
        "OnDespawn", "OnSpawn", "RegisterFiredBullets", "ResetBulletsFired", "SendLocomotion", "IncomingLocomotion"],
            ["Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase"] = ["Pointer", "WasCollected"],
            ["CheckpointManager"] = ["Current", "m_stateReplicator", "IsReloadingCheckpoint", "GetStateReplicator", "StoreCheckpoint",
        "ReloadCheckpoint", "AttemptInteract", "OnStateChange", "OnRecallComplete", "OnLevelCleanup"],
            ["Gear.BulletWeaponSynced"] = ["m_shotsToFire", "OnSyncCall", "OnGearSpawnComplete", "OnSyncFire", "OnSyncFireRegular",
        "OnSyncFireBurst", "Fire"],
            ["Enemies.EnemySync"] = ["m_agent", "Replicator", "Setup", "OnSpawn", "OnDespawn", "TryCollectCaptureData"],
            ["SNetwork.SNet_CaptureBuffer"] = ["PassCount", "isValid", "type", "data", "m_passes", "GetPass", "Clear"],
            ["SNetwork.SNet_MasterManager"] = ["IsMigrating", "OnMigrationStatusChange", "ForceMigration", "OnForceMigration",
        "FinalizeMigration", "ThisTheNewMasterFoundDuringMigration"],
            ["SNetwork.SNet_SyncManager"] = ["s_captureCallbackObjects", "IsInDropIn", "OnFoundNewMasterDuringMigration", "OnRecallStarted",
        "OnRecallDone", "IsRecallNeededForDropin", "MasterReplicationUpdate_Player", "StartRecall",
        "StartRecallCheckpointWithAllSyncedPlayers", "StartRecallWithAllSyncedPlayers", "RegisterCaptureCallback",
        "UnRegisterForDropInCallback"],
            ["SNetwork.SNet_ReplicationManager"] = ["Replicator", "OnStateCapture", "ClearAllLocal", "OnValidateMasterData"],
            ["SNetwork.SNet_ReplicationManager`2"] = ["m_replicators", "m_spawnPacket", "m_spawnRequestPacket", "m_despawnRequestPacket",
        "m_despawnPacket", "m_prefabs", "m_prefabsSyncVersion", "SpawnDespawnCallback", "Spawn",
        "InternalSpawnRequestFromSlaveCallback", "OnSpawnRequest", "InternalSpawnCallback", "LinkSupplier", "ClearAllLocal",
        "InternalDeSpawnRequestFromSlaveCallback", "InternalDeSpawnCallback_Slave", "UseSyncPrefab", "AddReplicator",
        "SetOwnership", "TryGetGameObjectReplicator", "GetInstanceReplicator", "OnSpawn", "OnDeSpawn", "OnStateCapture",
        "OnValidateMasterData"]
        };

        var assemblies = new List<(string Name, string Path, AssemblyDefinition Assembly)>();
        try
        {
            foreach (string name in new[] { "Modules-ASM", "GameData-ASM", "SNet_ASM", "Il2CppInterop.Runtime" })
            {
                string path = Path.Combine(profile, name == "Il2CppInterop.Runtime" ? "core" : "interop", name + ".dll");
                var source = sources.SingleOrDefault(row => row!["name"]!.GetValue<string>() == name);
                // An interop assembly is generated per profile: the profile's is held to its signature, the catalog to the generation it was captured from.
                var identity = interopIdentity.SingleOrDefault(row => row!["name"]!.GetValue<string>() == name);
                if (identity != null)
                {
                    if (!string.Equals(identity["signatureSha256"]!.GetValue<string>(), ForgeTestInputs.InteropSignature.Sha256(path), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Profile assembly differs from the build's interop signature: " + name);
                    if (source == null || !string.Equals(source["sha256"]!.GetValue<string>(), identity["catalogCaptureSha256"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Native catalog was captured from another interop generation: " + name);
                    assemblies.Add((name, path, AssemblyDefinition.ReadAssembly(path)));
                    continue;
                }
                string hash = ForgeCore.Hash.Sha256Hex(File.ReadAllBytes(path));
                if (source == null)
                {
                    if (args[0] != "--write" || name != "Il2CppInterop.Runtime")
                        throw new InvalidDataException("Missing native assembly provenance: " + name);
                    sources.Add(new JsonObject { ["name"] = name, ["sha256"] = hash, ["types"] = 1 });
                }
                else if (!string.Equals(source["sha256"]!.GetValue<string>(), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("QA assembly differs from native catalog provenance: " + name);
                assemblies.Add((name, path, AssemblyDefinition.ReadAssembly(path)));
            }

            int members = 0;
            foreach (string fullName in required)
            {
                var matches = assemblies.SelectMany(source => source.Assembly.MainModule.Types.SelectMany(Walk)
                    .Where(type => type.FullName == fullName).Select(type => (source.Name, Type: type))).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("Native type must resolve exactly once: " + fullName);
                var (assembly, type) = matches[0];
                bool Included(string name) => !selected.TryGetValue(fullName, out var names) || names.Contains(name);
                var fields = type.Fields.Where(field => !field.Name.StartsWith("Native", StringComparison.Ordinal) && Included(field.Name)).ToArray();
                var properties = type.Properties.Where(property => Included(property.Name)).ToArray();
                var methods = type.Methods.Where(method => !method.IsGetter && !method.IsSetter && !method.IsAddOn && !method.IsRemoveOn
                    && !method.Name.StartsWith("Native", StringComparison.Ordinal) && Included(method.Name)).ToArray();
                if (selected.TryGetValue(fullName, out var wanted))
                    foreach (string name in wanted)
                        if (!fields.Any(member => member.Name == name) && !properties.Any(member => member.Name == name)
                            && !methods.Any(member => member.Name == name))
                            throw new InvalidDataException("Required native member not declared on " + fullName + ": " + name);

                var evidence = JsonSerializer.SerializeToNode(new
                {
                    assembly,
                    fullName,
                    metadataToken = Token(type),
                    baseType = type.BaseType?.FullName,
                    fields = fields.Select(field => new
                    {
                        name = field.Name,
                        type = field.FieldType.FullName,
                        metadataToken = Token(field),
                        isStatic = field.IsStatic,
                        hasConstant = field.HasConstant,
                        constant = field.HasConstant ? field.Constant : null
                    }),
                    properties = properties.Select(property => new
                    {
                        name = property.Name,
                        type = property.PropertyType.FullName,
                        metadataToken = Token(property),
                        getter = property.GetMethod?.FullName,
                        setter = property.SetMethod?.FullName
                    }),
                    methods = methods.Select(method => new
                    {
                        name = method.Name,
                        signature = method.FullName,
                        metadataToken = Token(method),
                        isStatic = method.IsStatic,
                        returnType = method.ReturnType.FullName,
                        parameters = method.Parameters.Select(parameter => new
                        {
                            name = parameter.Name,
                            type = parameter.ParameterType.FullName,
                            isIn = parameter.IsIn,
                            isOut = parameter.IsOut,
                            hasDefault = parameter.HasConstant,
                            @default = parameter.HasConstant ? parameter.Constant : null
                        })
                    })
                }, json)!;
                members += fields.Length + properties.Length + methods.Length;
                if (args[0] == "--write")
                {
                    if (!types.TryGetPropertyValue(type.Name, out var entry))
                    {
                        entry = new JsonObject
                        {
                            ["ns"] = type.Namespace,
                            ["fields"] = JsonSerializer.SerializeToNode(fields.Select(field => field.Name).Concat(properties.Select(property => property.Name)).Distinct().Order()),
                            ["methods"] = JsonSerializer.SerializeToNode(methods.Select(method => method.Name).Distinct().Order())
                        };
                        types.Add(type.Name, entry);
                    }
                    entry!["clr"] = evidence;
                }
                else if (!types.TryGetPropertyValue(type.Name, out var entry) || !JsonNode.DeepEquals(entry!["clr"], evidence))
                    throw new InvalidDataException("Native CLR evidence is missing, incomplete or stale: " + fullName);
            }
            if (args[0] == "--write") File.WriteAllText(catalogPath, catalog.ToJsonString(json));
            Console.WriteLine($"H9 native CLR evidence {args[0][2..]}: {required.Length} types, {members} typed members; QA hashes match. Game/recall behavior unverified.");
        }
        finally { foreach (var source in assemblies) source.Assembly.Dispose(); }

        static IEnumerable<TypeDefinition> Walk(TypeDefinition type)
        {
            yield return type;
            foreach (var child in type.NestedTypes.SelectMany(Walk)) yield return child;
        }
        static string Token(IMetadataTokenProvider member) => $"0x{member.MetadataToken.ToInt32():X8}";

        return Environment.ExitCode;
    }
}
