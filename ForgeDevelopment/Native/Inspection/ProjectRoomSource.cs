using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeDevelopment.Native;

/// <summary>
/// The game-bound half of the development diagnostics' room resolution: the adapter that hands the one room
/// resolver to the scan, translated into the plain answer the scan speaks.
///
/// The rule itself lives with Forge Engine's map provider, asked through its author read
/// (<c>ForgeEngine.Authoring.AuthorReads.ResolveRoom</c>, reached by name): it names an authored room by its complete
/// locator and the generation receipt in the Engine's current world, so this file owns no
/// matching, no prefab identity and no second answer to the same question. It exists only because the scan is
/// the game-independent half of the report and must not name a native type: a build that installs no adapter
/// leaves the scan with no resolver, and a scan with no resolver refuses every authored room instead of
/// substituting a rule of its own.
///
/// The resolver's own refusal reason is translated, not swallowed: a zone the level does not have, a zone that
/// holds no such room and a build that cannot ask the question at all are three different things, and the report
/// says which one happened.
/// </summary>
internal static class ProjectRoomSource
{
    /// <summary>Every generated room this world holds for one authored room reference inside one zone, or the one
    /// reason the reference names no room of it. A refusal that counted several rooms is carried as those rooms,
    /// so the scan's own cardinality rule reports them as the candidates an author can look at.</summary>
    internal static ProjectRoomAnswer Resolve(long worldEpoch, ProjectRoomLocator locator)
    {
        string[] answer;
        try
        {
            answer = EngineLookup.Call<string[]>("ResolveRoom", locator.SourcePrefab, locator.Zone.Dimension, locator.Zone.Layer,
                locator.Zone.LocalZoneIndex, locator.ZoneAuthorId, locator.Room.Id, locator.Room.Revision, locator.InstanceId ?? "");
        }
        catch (InvalidOperationException) { return ProjectRoomAnswer.Refused(ProjectReferenceReason.CreationContextUnverified); }
        if (answer.Length == 1 && answer[0].StartsWith("!", StringComparison.Ordinal))
            return ProjectRoomAnswer.Refused(Reason(answer[0][1..]));
        var hits = new List<ProjectRoomHit>(answer.Length);
        foreach (var room in answer)
        {
            var bar = room.IndexOf('|');
            hits.Add(new ProjectRoomHit(int.Parse(room[..bar], CultureInfo.InvariantCulture), int.Parse(room[(bar + 1)..], CultureInfo.InvariantCulture)));
        }
        return ProjectRoomAnswer.Answered(hits.AsReadOnly());
    }

    /// <summary>The report's own word for one refusal. The two room-specific reasons keep their own codes; a world
    /// the resolver cannot be asked in and a path that loads no prefab object remain "the source identity of this
    /// reference could not be established", which is what they are. An empty answer the resolver did not refuse is
    /// still no candidate: the zone held no such room.</summary>
    private static ProjectReferenceReason Reason(string refusal) => refusal switch
    {
        "zone-unresolved" => ProjectReferenceReason.ZoneUnresolved,
        "no-room" => ProjectReferenceReason.NoCandidate,
        _ => ProjectReferenceReason.CreationContextUnverified
    };
}
