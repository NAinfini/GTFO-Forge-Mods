namespace ForgeEnemy.Tests;

/// <summary>Entry of the native-evidence suites; verify.py selects one by its name.</summary>
internal static class EnemyEvidenceSuites
{
    private static int Main(string[] args) => (args.Length == 0 ? "" : args[0]) switch
    {
        "NativeEvidence" => NativeEvidenceSuite.Run(args[1..]),
        "NativeLayout" => NativeLayoutSuite.Run(args[1..]),
        "HookInstall" => HookInstallSuite.Run(args[1..]),
        _ => Usage()
    };

    private static int Usage() { Console.Error.WriteLine("Usage: <suite> [arguments]"); return 2; }
}
