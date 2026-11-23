namespace ForgeEngine.Tests;

/// <summary>Entry of the core suites; verify.py selects one by its name.</summary>
internal static class EngineCoreSuites
{
    private static int Main(string[] args) => (args.Length == 0 ? "" : args[0]) switch
    {
        "Framework" => FrameworkSuite.Run(args[1..]),
        "Network" => ForgeEngine.Network.Tests.Program.Main(args[1..]),
        _ => Usage()
    };

    private static int Usage() { Console.Error.WriteLine("Usage: <suite> [arguments]"); return 2; }
}
