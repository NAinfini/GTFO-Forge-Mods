namespace ForgeCore.Tests;

internal static class Program
{
    private static int Main()
    {
        var errors = new List<string>();
        Log.Sinks = _ => errors.Add;
        var passed = 0; var failed = 0;
        void Check(bool ok, string label)
        {
            if (ok) passed++;
            else { failed++; Console.WriteLine("FAIL " + label); }
        }
        NetChecks.Run(Check, errors);
        NetEpochChecks.Run(Check);
        CoreChecks.Run(Check, errors);
        EnemyChecks.Run(Check);
        ColliderChecks.Run(Check);
        LiveEnemyChecks.Run(Check);
        LevelChecks.Run(Check);
        SlowChecks.Run(Check, errors);
        SlowZoneChecks.Run(Check);
        NavigationChecks.Run(Check);
        CombatChecks.Run(Check, errors);
        RayChecks.Run(Check);
        GunPoseChecks.Run(Check);
        SlotChecks.Run(Check);
        NoiseChecks.Run(Check);
        OnceChecks.Run(Check);
        CameraChecks.Run(Check);
        EventChecks.Run(Check, errors);
        RecordingChecks.Run(Check);
        FingerprintChecks.Run(Check);
        ContentFolderChecks.Run(Check);
        LevelIdentityChecks.Run(Check);
        JsonChecks.Run(Check);
        FrameChecks.Run(Check, errors);
        GameAssetsChecks.Run(Check);
        BracketChecks.Run(Check, errors);
        PuppetChecks.Run(Check);
        PatchTargetChecks.Run(Check);
        GearAssemblyChecks.Run(Check);
        VolumePickChecks.Run(Check);
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
