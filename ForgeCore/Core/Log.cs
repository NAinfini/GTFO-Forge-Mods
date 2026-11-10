using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeCore;

/// <summary>Logging for one mod, keyed by its BepInEx plugin GUID. Only errors are written, to that mod's own log source: a failed
/// feature, or data or configuration that leaves something unusable. A state the game or a player can legitimately be in is not
/// an error and is not written.</summary>
public sealed class ModLog
{
    private readonly Action<string> _sink;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    internal ModLog(string modId, Action<string> sink)
    {
        ModId = modId;
        _sink = sink;
    }

    public string ModId { get; }

    public void Error(string message) => Write(message);

    /// <summary>An error with its registered diagnostic code (<c>catalog/authoring/diagnostic-codes.json</c>, named package-event): written as <c>code: detail</c>.</summary>
    public void Error(string code, string detail) => Write(code + ": " + detail);

    /// <summary><see cref="ErrorOnce(string)"/> for an error with its registered diagnostic code, written as <c>code: detail</c>.</summary>
    public void ErrorOnce(string code, string detail) => ErrorOnce(code + ": " + detail);

    /// <summary>An error that can repeat: written the first time this exact <paramref name="message"/> occurs and never again, so a
    /// repeating failure stays visible once without flooding the log. Different failures from one place have different messages and
    /// are each written; put the exception (<c>error.ToString()</c>) in the message, not just its text.</summary>
    public void ErrorOnce(string message)
    {
        lock (_seen)
            if (!_seen.Add(message)) return;
        Write(message);
    }

    private void Write(string message)
    {
        try { _sink(message); }
        catch (Exception error) { Console.Error.WriteLine(ModId + ": " + message + " (log sink failed: " + error + ")"); }
    }
}
/// <summary>Per-mod logs (<see cref="For"/>) and the one way to write a number or a position into a message.</summary>
public static partial class Log
{
    /// <summary>A number for a message: at most two decimals, no trailing zeros, always a dot.</summary>
    public static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A position for a message: <c>(x, y, z)</c>, two decimals each, always a dot.</summary>
    public static string Pos(float x, float y, float z)
        => "(" + x.ToString("0.00", CultureInfo.InvariantCulture) + ", " + y.ToString("0.00", CultureInfo.InvariantCulture)
           + ", " + z.ToString("0.00", CultureInfo.InvariantCulture) + ")";

    private static readonly Dictionary<string, ModLog> Logs = new(StringComparer.Ordinal);

    /// <summary>The host (ForgeCore's plugin) turns a mod ID into its log source; offline it writes to the console.</summary>
    internal static Func<string, Action<string>> Sinks { get; set; } =
        id => message => Console.WriteLine("[Error: " + id + "] " + message);

    public static ModLog For(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(modId));
        lock (Logs)
        {
            if (Logs.TryGetValue(modId, out var log)) return log;
            log = new ModLog(modId, Sinks(modId));
            Logs.Add(modId, log);
            return log;
        }
    }
}
