using System;
using System.Text;

namespace ForgeEngine.Logging;

/// <summary>
/// The one way to build a diagnostic code from parts. A pattern such as <c>map-test-{kind}-transition-restoration-unavailable</c>
/// is a key of <c>dynamicCodes</c> in <c>catalog/authoring/diagnostic-codes.json</c>, which lists every code its holes may
/// produce; <see cref="Of"/> fills the holes in order and refuses any result the table does not list, so a code that was
/// never registered cannot reach a log, a result or a player-visible report. The table is generated into
/// <c>DynamicDiagnosticCodes.Generated.cs</c> and the repository check rejects a call whose pattern is not registered.
/// </summary>
public static partial class DynamicDiagnosticCodes
{
    /// <summary>The code <paramref name="pattern"/> yields for <paramref name="holes"/>, in the order the holes appear.</summary>
    /// <exception cref="InvalidOperationException">The pattern is not registered, the hole count differs from the pattern's, or
    /// the produced code is not one of the registered expansions.</exception>
    public static string Of(string pattern, params string[] holes)
    {
        if (!Expansions.TryGetValue(pattern, out var registered))
            throw new InvalidOperationException("Dynamic diagnostic code pattern " + pattern + " is not registered.");
        var code = new StringBuilder(pattern.Length + 16);
        var used = 0;
        for (var at = 0; at < pattern.Length; at++)
        {
            if (pattern[at] != '{') { code.Append(pattern[at]); continue; }
            var close = pattern.IndexOf('}', at);
            if (close < 0 || used >= holes.Length)
                throw new InvalidOperationException("Dynamic diagnostic code pattern " + pattern + " does not match its " + holes.Length + " holes.");
            code.Append(holes[used++]);
            at = close;
        }
        if (used != holes.Length)
            throw new InvalidOperationException("Dynamic diagnostic code pattern " + pattern + " does not match its " + holes.Length + " holes.");
        var produced = code.ToString();
        if (!registered.Contains(produced))
            throw new InvalidOperationException("Dynamic diagnostic code " + produced + " is not a registered expansion of " + pattern + ".");
        return produced;
    }
}
