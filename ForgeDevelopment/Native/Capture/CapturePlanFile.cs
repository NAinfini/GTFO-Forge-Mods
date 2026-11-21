using System;
using System.IO;
using System.Text.Json;

namespace ForgeDevelopment.Native;

// No persistent enabled cache. A missing, unreadable, disabled, or invalid file revokes
// the requested plan; its consumer reconciles native ownership on the main thread.
internal static class CapturePlanFile
{
    internal static T? Read<T>(string path, JsonSerializerOptions options, Func<T, bool> enabled,
        Action<T> validate, out string? text, out string? error) where T : class
    {
        text = null; error = null;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
            var plan = JsonSerializer.Deserialize<T>(text, options);
            if (plan == null || !enabled(plan)) return null;
            validate(plan);
            return plan;
        }
        catch (Exception exception) { error = exception.Message; return null; }
    }
}
