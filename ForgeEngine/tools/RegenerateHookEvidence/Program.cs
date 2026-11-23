using System.Text.Json;
using ForgeEngine.HookEvidence;
using ForgeEngine.HookEvidence.Enemy;
using ForgeEngine.HookEvidence.Weapon;
using MapRegenerator = ForgeMap.Tests.MapNativeLayout.EvidenceRegenerator;
using PlayerRegenerator = ForgePlayer.Tests.PlayerNativeLayout.EvidenceRegenerator;

// The one regenerator of the hook evidence tables (evidence/*-hooks.json) of the four domain packages: it reads the built
// ForgeEngine.dll (the domain's types by namespace) and the game's interop assemblies and rewrites the domain's own table. Each package keeps its own
// table layout, so each domain has its own reader (Map/ and Player/EvidenceRegenerator.cs, EnemyEvidence.cs, WeaponEvidence.cs) over the shared HookRows.cs.
// enemy-behavior rewrites the Forge source references of Enemy/evidence/enemy-behavior-hooks.json (EnemyBehaviorEvidence.cs); it reads no dump.
const string Usage = "Usage: RegenerateHookEvidence <map|player|enemy|enemy-behavior|weapon> <BepInEx> <artifacts> <hooks.json> <dump.cs>";
if (args.Length != 5) { Console.Error.WriteLine(Usage); return 2; }
string bepInEx = args[1], artifacts = args[2], specPath = args[3], dumpPath = args[4];
switch (args[0])
{
    case "map":
        HookRows.WithAssemblies(bepInEx, artifacts,
            new[] { "Modules-ASM.dll", "SNet_ASM.dll", "GameData-ASM.dll", "Shards-ASM.dll", "Utils-ASM.dll", "CellSound-ASM.dll", "Wwise-ASM.dll", "DropServerSDK-ASM.dll" },
            (engine, framework, interop) =>
            {
                using var spec = JsonDocument.Parse(File.ReadAllText(specPath));
                MapRegenerator.Run(MapRegenerator.Read(engine, framework, interop, spec), specPath, specPath, dumpPath);
            });
        break;
    case "player":
        HookRows.WithAssemblies(bepInEx, artifacts, new[] { "Modules-ASM.dll", "SNet_ASM.dll", "Utils-ASM.dll", "GlobalFramework-ASM.dll" },
            (engine, framework, interop) =>
            {
                using var spec = JsonDocument.Parse(File.ReadAllText(specPath));
                PlayerRegenerator.Run(PlayerRegenerator.Read(engine, framework, interop, spec), specPath, specPath, dumpPath);
            });
        break;
    case "enemy": return EnemyEvidence.Run(args[1..]);
    case "enemy-behavior": EnemyBehaviorEvidence.Run(artifacts, specPath); return 0;
    case "weapon": WeaponEvidence.Run(bepInEx, artifacts, specPath, dumpPath); return 0;
    default: Console.Error.WriteLine(Usage); return 2;
}
Console.WriteLine("Regenerated hook metadata: " + specPath);
return 0;
