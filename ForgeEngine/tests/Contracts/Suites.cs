namespace ForgeEngine.Tests;

/// <summary>Entry of the contract suites; verify.py and the suite tools select one by its name.</summary>
internal static class EngineContractSuites
{
    private static int Main(string[] args) => (args.Length == 0 ? "" : args[0]) switch
    {
        "Architecture" => ArchitectureSuite.Run(args[1..]),
        _ => Usage()
    };

    private static int Usage() { Console.Error.WriteLine("Usage: <suite> [arguments]"); return 2; }
}
