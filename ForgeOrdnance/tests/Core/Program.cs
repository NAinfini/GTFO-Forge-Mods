using System.Runtime.CompilerServices;
using ForgeOrdnance.Tests;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    checks++;
}

// The three parts' Core, one after the other; the line names each part's share of the checks.
CarrierChecks.Run(Check);
var carrier = checks;
ArsenalChecks.Run(Check);
var arsenal = checks - carrier;
FramesChecks.Run(Check);
Console.WriteLine("ForgeOrdnance core: " + checks + " checks passed (carrier " + carrier + ", Arsenal " + arsenal + ", Frames " + (checks - carrier - arsenal) + ").");

/// <summary>The mod's folder, from this source file's own location (the build output lives under artifacts/): the shipped
/// definition files are read from it.</summary>
internal static class ModFolder
{
    internal static readonly string Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Here())!, "..", ".."));

    private static string Here([CallerFilePath] string path = "") => path;
}
