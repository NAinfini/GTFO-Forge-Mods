namespace ForgeMap.Native;

/// <summary>How far one action executor got. The three states are the three things an executor can stand
/// behind: a native entry point was invoked, the state the action asks for already holds so nothing had to be
/// written, or the action was refused before anything native was written. There is deliberately no "succeeded":
/// the entry points a door or a terminal offers return nothing about the state they will end in (an interaction
/// entry starts an animation, a lock setup returns a status this layer does not publish), so the object's real
/// state is published by the observation path and never claimed by the action that asked for it.</summary>
internal enum MapActionCommit
{
    /// <summary>Nothing was written; the outcome's code names the one reason.</summary>
    Refused,
    /// <summary>Nothing was written because the state the action asks for already holds.</summary>
    AlreadyInState,
    /// <summary>The native entry point was invoked with the arguments the action resolved.</summary>
    Issued
}

/// <summary>The result of one action: how far it got, plus the fixed code that names the outcome. The code is
/// part of the binding's vocabulary rather than a sentence — an action never reports a cause it did not check,
/// and a refused action reports the check that refused it, so a result row and a log line can name the same
/// decision without either of them re-deriving it.</summary>
internal readonly struct MapActionOutcome
{
    private MapActionOutcome(MapActionCommit commit, string code)
    {
        Commit = commit;
        Code = code;
    }

    internal MapActionCommit Commit { get; }
    internal string Code { get; }

    internal static MapActionOutcome Issued(string code) => new(MapActionCommit.Issued, code);
    internal static MapActionOutcome AlreadyInState(string code) => new(MapActionCommit.AlreadyInState, code);
    internal static MapActionOutcome Refused(string code) => new(MapActionCommit.Refused, code);
}
