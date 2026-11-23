namespace ForgeEngine.Tests;

/// <summary>Entry of the native-evidence suites; verify.py and the import tool select one by its name.</summary>
internal static class EngineEvidenceSuites
{
    private static int Main(string[] args) => (args.Length == 0 ? "" : args[0]) switch
    {
        "NativeHandoverEvidence" => NativeHandoverEvidenceSuite.Run(args[1..]),
        _ => Usage()
    };

    private static int Usage() { Console.Error.WriteLine("Usage: <suite> [arguments]"); return 2; }
}
