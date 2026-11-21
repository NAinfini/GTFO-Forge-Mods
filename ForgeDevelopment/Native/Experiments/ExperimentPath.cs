namespace ForgeDevelopment.Native;

/// <summary>
/// The path grammar shared by <c>read</c>, <c>set</c> and <c>wait.path</c>: dotted member names, where a
/// segment is either a member name or a numeric element index for an array, list or dictionary.
/// </summary>
internal static class ExperimentPath
{
    internal const int MaximumLength = 256;
    internal const int MaximumSegments = 16;

    internal static bool IsValid(string path)
    {
        if (path.Length == 0 || path.Length > MaximumLength) return false;
        var segments = path.Split('.');
        if (segments.Length > MaximumSegments) return false;
        foreach (var segment in segments)
            if (!IsSegment(segment)) return false;
        return true;
    }

    /// <summary>A call target is the same grammar, with the last segment being the method name.</summary>
    internal static bool IsValidCall(string name) => name == "configure-native-bot-slot" || IsValid(name);

    private static bool IsSegment(string segment)
    {
        if (segment.Length == 0) return false;
        if (int.TryParse(segment, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)) return true;
        var first = segment[0];
        if (!char.IsLetter(first) && first != '_' && first != '<') return false;
        foreach (var c in segment)
            if (!(char.IsLetterOrDigit(c) || c is '_' or '<' or '>' or '`')) return false;
        return true;
    }

    internal static string Describe() => "a dotted path like \"m_sync.m_stateReplicator.State\", at most " +
        MaximumSegments.ToString(System.Globalization.CultureInfo.InvariantCulture) + " segments";
}
