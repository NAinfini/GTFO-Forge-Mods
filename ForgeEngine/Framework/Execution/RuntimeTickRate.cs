using System;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>The one tick rate (ticks per second) the whole product converts by: catalog/forge-release/tick-rate.json,
/// embedded here and read by the website from the same file. The host compares the rate it measures from
/// <c>Time.fixedDeltaTime</c> against it at every world start.</summary>
internal static class RuntimeTickRate
{
    internal static readonly double TicksPerSecond = Load();

    private static double Load()
    {
        using var stream = typeof(RuntimeTickRate).Assembly.GetManifestResourceStream("ForgeEngine.Framework.TickRate.json")
            ?? throw new InvalidOperationException("No tick rate is embedded.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("ticksPerSecond").GetDouble();
    }
}
