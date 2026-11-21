using System;
using System.Text.RegularExpressions;

namespace ForgeDevelopment.Native;

internal static class DiagnosticLogClassifier
{
    private static readonly Regex ExceptionType = new(@"\b([A-Za-z_][\w.]*Exception)\b", RegexOptions.Compiled);
    private static readonly Regex StackMethod = new(@"(?:^|\n)\s*(?:at\s+)?([A-Za-z_][\w.`+]*[.:][\w.`+]+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Numbers = new(@"\b\d+\b", RegexOptions.Compiled);
    private static readonly Regex ForgeFrame = new(@"(?:^|\n)\s*(?:at\s+)?Forge\w*\.", RegexOptions.Compiled);
    internal static (string Type, string Source) Classify(string level, string logger, string message)
    {
        var exception = ExceptionType.Match(message);
        var method = StackMethod.Match(message);
        var firstLine = message.Split('\n', 2)[0].Trim();
        if (firstLine.Length > 160) firstLine = firstLine[..160];
        var source = method.Success ? method.Groups[1].Value : logger + ":" + Numbers.Replace(firstLine, "#");
        return (exception.Success ? exception.Groups[1].Value : "logged_" + level.ToLowerInvariant(), source);
    }
    /// <summary>Who a logged line belongs to: a Forge plugin's logger, or a Unity line whose stack runs through Forge code, is <c>forge</c>;
    /// any other Unity line is the <c>game</c>; any other BepInEx logger is another <c>mod</c>. Nothing is dropped here: every line is kept and counted under its origin.</summary>
    internal static string Origin(string logger, string message)
        => logger.StartsWith("Forge", StringComparison.Ordinal) || ForgeFrame.IsMatch(message) ? "forge" : logger == "Unity" ? "game" : "mod";
}
