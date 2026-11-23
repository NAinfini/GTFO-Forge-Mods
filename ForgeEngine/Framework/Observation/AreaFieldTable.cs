using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>
/// The `area_field` definitions one install ships, read from the published runtime resources every package writes
/// to `plugins/&lt;package&gt;/forge/resources/&lt;revision&gt;.resource.forge`. The kind is definition-only and
/// level-attached: a definition is a shape and its sizes, the centre is always the consumer's, and the provider
/// that owns the kind is the Trigger package (`FORGE-FRAMEWORK.md` §1 puts the persistent area field in
/// the trigger provider's own row).
///
/// The folder is shared by every resource kind a package ships, so the kind inside a document is what tells the
/// field documents apart: a resource of another kind is left alone rather than reported as a broken field. A
/// document of this kind that does not read is refused whole with its own diagnostic. PublishedResources admits
/// identical copies once and withdraws conflicting revisions or content before this table reads the payload.
///
/// A table is one snapshot of that folder for one world epoch. The provider that owns the kind keeps its snapshot
/// in step with the world (a new world re-reads the documents), and a package that only needs a definition's
/// geometry — the teleport row measuring whether a landing point is inside the field it was given — reads the same
/// documents through the same reader and holds its own snapshot. The kind's owner is the provider; this type is
/// the reader both sides share, so the accepted grammar and the duplicate rule exist once.
/// </summary>
internal sealed class AreaFieldTable
{
    private readonly Dictionary<string, AreaFieldDefinition> _definitions;
    private readonly Dictionary<string, string> _revisions;
    private readonly List<ResourceRef> _references;

    private AreaFieldTable(long worldEpoch, Dictionary<string, AreaFieldDefinition> definitions, Dictionary<string, string> revisions)
    {
        WorldEpoch = worldEpoch;
        _definitions = definitions;
        _revisions = revisions;
        _references = new List<ResourceRef>(definitions.Count);
        foreach (var id in definitions.Keys) _references.Add(new ResourceRef(AreaFieldDocument.ResourceKind, id));
        _references.Sort((left, right) => string.CompareOrdinal(left.ResourceId, right.ResourceId));
    }

    /// <summary>The world epoch this snapshot belongs to.</summary>
    public long WorldEpoch { get; }

    /// <summary>How many definitions the snapshot holds. Zero is the ordinary state of an install that ships no
    /// field, and every read of the kind is then a refusal rather than an empty volume.</summary>
    public int Count => _references.Count;

    /// <summary>An install that shipped no field document has none to answer, and a read of one is `stale-resource`
    /// at load rather than a plan that runs against nothing.</summary>
    public static AreaFieldTable Empty { get; } = new(0, new Dictionary<string, AreaFieldDefinition>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Every field definition the given documents (the install's `area_field` runtime resources, handed out
    /// once by ForgeEngine's content discovery) hold for one world, in ordinal id order. A document that does
    /// not read and an id with conflicting copies are reported through <paramref name="rejected"/> — a fact about the
    /// install an author wrote, not about a step that later names one.</summary>
    public static AreaFieldTable Load(IReadOnlyList<PublishedResource> discovered, long worldEpoch, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(rejected);
        var loaded = new Dictionary<string, AreaFieldDefinition>(StringComparer.Ordinal);
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in discovered)
        {
            if (!PublishedResources.TryRead(document, PublishedResources.MaximumResourceBytes, AreaFieldCodes.Read, rejected,
                out var text))
                continue;
            if (!AreaFieldDocument.TryKind(text!, out var kind, out var kindCode, out var kindReason))
            {
                rejected(PublishedResources.Diagnostic(document.Relative, kindCode!, kindReason!));
                continue;
            }
            if (kind != AreaFieldDocument.ResourceKind) continue;
            if (!AreaFieldDocument.TryRead(text!, out var id, out var revision, out var definition, out var code, out var reason))
            {
                rejected(PublishedResources.Diagnostic(document.Relative, code!, reason!));
                continue;
            }
            loaded.Add(id!, definition!);
            revisions.Add(id!, revision!);
        }
        return new AreaFieldTable(worldEpoch, loaded, revisions);
    }

    /// <summary>Every field definition this install ships. Discovery follows ownership: the provider that owns the
    /// kind answers with this list, and it is its own table rather than a scan of the world.</summary>
    public IReadOnlyList<ResourceRef> Enumerate() => _references;

    /// <summary>One definition by the id a plan's reference names. An id this install does not ship answers null,
    /// which is what makes a plan naming a field that is not installed fail as `stale-resource` at load instead of
    /// measuring a volume around nothing.</summary>
    public ResourceRef? Resolve(string? resourceId)
        => resourceId != null && _definitions.ContainsKey(resourceId)
            ? new ResourceRef(AreaFieldDocument.ResourceKind, resourceId) : null;

    /// <summary>The revision one field was published under, or null when this install does not ship it. A plan's
    /// compiled reference is only valid against the revision it was written for.</summary>
    public string? Revision(string? resourceId)
        => resourceId != null && _revisions.TryGetValue(resourceId, out var revision) ? revision : null;

    /// <summary>The parsed definition of one field, or null when this install does not ship it. A row asks this
    /// after the kernel resolved the reference, so the shape it measures is the one the plan named.</summary>
    public AreaFieldDefinition? Definition(string? resourceId)
        => resourceId != null && _definitions.TryGetValue(resourceId, out var definition) ? definition : null;
}
