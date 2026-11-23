using System;

namespace ForgeEngine.Framework;

/// <summary>Bounded diagnostics shared by native providers.</summary>
internal static class RuntimeDiagnostics
{
    /// <summary>Describes a failure without allowing a hostile Message getter to hide it.</summary>
    public static string Describe(Exception error)
    {
        string message;
        try { message = error.Message ?? ""; }
        catch (Exception unavailable) { message = "<message unavailable: " + unavailable.GetType().Name + ">"; }
        if (message.Length > 1900) message = message[..1900];
        var description = error.GetType().Name + ": " + message;
        return description.Length <= 2048 ? description : description[..2048];
    }
}
