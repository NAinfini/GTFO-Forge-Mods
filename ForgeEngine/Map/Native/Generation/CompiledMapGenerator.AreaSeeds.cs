using System;
using ForgeMap.Generation;
using LevelGeneration;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    /// <summary>Changes the seed argument before LG_Area.Setup consumes it; never writes a result field afterward.</summary>
    private static void AreaSeedBeforeSetup(LG_Area __instance, LG_Geomorph __0, ref uint __3)
    {
        if (!Live(out var run) || run.Failed) return;
        try
        {
            if (run.SettingUpRoom is not { } room) return;
            var definition = run.Program.Rooms[room]; int resource = definition.Resource;
            if (__0.m_geoPrefab?.Pointer != Asset(run, resource).Pointer)
            {
                throw new GenerationRefusal("gen-area-identity", definition.Id + "/setup-root");
            }
            var actual = __0.m_areas; int target = -1;
            for (int i = 0; i < actual.Length; i++) if (actual[i].Pointer == __instance.Pointer)
            { if (target >= 0) throw new GenerationRefusal("gen-area-identity", definition.Id); target = i; }
            if (target < 0) throw new GenerationRefusal("gen-area-identity", definition.Id + "/setup-area");
            if ((uint)target >= (uint)definition.AreaSeeds.Length) throw new GenerationRefusal("gen-area-identity", definition.Id + "/setup-area-seeds");
            if (definition.AreaSeeds[target] is { } seed) __3 = seed.Apply(__3);
            run.Record("area-seed-before-setup", definition.Id + "/" + target, new { area = target, seed = __3, nativeArea = __instance.Pointer.ToInt64() });
        }
        catch (Exception error) { Refuse(run, error); }
    }
}
