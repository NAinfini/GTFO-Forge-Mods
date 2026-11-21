using System;

namespace ForgeDevelopment.Native;

/// <summary>
/// The one line the recorder core needs from the plugin's log. It is resolved by reflection because this file is
/// compiled on its own by the recorder's test project, where no BepInEx log source exists: the core's decisions are
/// testable, and the process that owns a log is the only one that pays for it.
/// </summary>
internal static class RecDiag
{
    private static bool _resolved;
    private static object? _source;

    /// <summary>One typed field of a structured record (text, integer or flag).</summary>
    internal readonly record struct Field(string Name, object Value);

    /// <summary>A coded record goes to the plugin log as its text line; the code and typed fields are already part of that line.</summary>
    internal static void Record(bool error, string code, string text, params Field[] fields)
    {
        if (error) Error(text); else Info(text);
    }

    internal static void Warn(string message) => Write("LogWarning", message);
    internal static void Info(string message) => Write("LogInfo", message);
    internal static void Error(string message) => Write("LogError", message);

    private static void Write(string method, string message)
    {
        try
        {
            if (!_resolved)
            {
                _resolved = true;
                var plugin = Type.GetType("ForgeDevelopment.Native.Plugin, ForgeDevelopment.Native", false);
                var log = plugin?.GetProperty("PluginLog", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
                if (log != null)
                {
                    _source = log;
                }
            }
            var sink = _source?.GetType().GetMethod(method, new[] { typeof(object) });
            if (sink == null) Console.Error.WriteLine(message);
            else sink.Invoke(_source, new object[] { message });
        }
        catch (Exception error)
        {
            Fallback(message + "; recorder logging failed: " + error);
        }
    }

    /// <summary>The console when the log sink failed. A console that also fails is dropped: there is nowhere left to report to.</summary>
    internal static void Fallback(string message)
    {
        try { Console.Error.WriteLine(message); }
        catch (Exception) { }
    }
}
