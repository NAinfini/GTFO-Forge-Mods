using System.IO;
using BepInEx;

namespace ForgeDevelopment.Native;

internal static class DevelopmentPaths
{
    internal static string Root => Path.Combine(Paths.BepInExRootPath, "ForgeDevelopment");
}
