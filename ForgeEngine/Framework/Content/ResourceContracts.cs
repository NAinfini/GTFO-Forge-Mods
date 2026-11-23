using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// One runtime resource reference: the kind it belongs to and the id the provider that owns that kind answers
/// with. The kind is a member of <see cref="Wire.ResourceKinds"/>, exactly like a resource port's
/// `resourceKind`; the id is the provider's own instance key and is never normalized here. A reference is a
/// carrier, not a claim: whether the resource is still there is what the owner's own resolve answers, which is why
/// a plan resolves its compiled references once at load and a provider resolves the ones it publishes at runtime.
/// </summary>
public sealed record ResourceRef
{
    public ResourceRef(string resourceKind, string resourceId)
    {
        var kind = resourceKind ?? "";
        RuntimeJson.Require(Wire.ResourceKinds.Contains(kind), RuntimeAbiCodes.ResourceKind, kind);
        var id = RuntimeJson.Text(resourceId);
        ResourceKind = kind;
        ResourceId = id;
    }
    /// <summary>The resource kind's own name, spelled the way the shared vocabulary spells resource kinds.</summary>
    public string ResourceKind { get; }
    /// <summary>The owning provider's instance key inside that kind. Opaque to the kernel.</summary>
    public string ResourceId { get; }
    /// <summary>The wire form a resource value takes in a command result and in a frame's two slots.</summary>
    public JsonElement ToJson() => RuntimeJson.From(new { resourceKind = ResourceKind, resourceId = ResourceId });
}

/// <summary>
/// One provider's own cost accounting for its resources: the three answers an action step that declares a cost
/// needs, and nothing more. <see cref="Reserve"/> holds an amount the provider can currently spend and answers
/// whether it could; <see cref="Commit"/> deducts what was held, once the command that reserved it proved it wrote;
/// <see cref="Release"/> gives back a reservation whose command wrote nothing. The balance itself is the
/// provider's — the kernel keeps no quantity of its own and never invents one — so a kind whose owner declares no
/// cost is a kind no step can charge, which the plan loader refuses by name.
///
/// The three travel as one object because they are one table: a reservation only means something against the same
/// owner that commits it, and a provider that answered one and not the others would not be a cost owner at all.
/// </summary>
public sealed record RuntimeResourceCost(Func<string, long, bool> Reserve, Action<string, long> Commit, Action<string, long> Release);

/// <summary>
/// One resource kind's owner, as the package that knows the kind registers it. Both answers are that provider's
/// own table and nothing else: the kernel never scans a world for resources, and a kind with no registered owner is
/// not silently empty — a read of it is refused with a code instead. <see cref="Enumerate"/> answers every resource
/// of the kind the provider currently holds; <see cref="Resolve"/> answers one id, or null when this provider does
/// not have it. Implementations routinely answer null before their world exists, which is what the load-time
/// resolution of a plan's compiled references reads: a reference nothing owns yet is `stale-resource`.
/// <see cref="Cost"/> is the same owner's accounting for the steps that declare a cost, or null for a kind whose
/// quantity nothing charges. <see cref="Revision"/> answers which revision of an id the owner holds: a plan's
/// compiled reference names `{id, revision}`, and one whose revision differs from the held one is a stale resource,
/// refused at load. A kind whose resources carry no authored revision leaves it null and is listed in
/// <c>RuntimeGraphContracts.RevisionExemptResourceKinds</c>; any other kind without it is refused.
/// </summary>
public sealed record RuntimeResourceProvider(Func<IReadOnlyList<ResourceRef>> Enumerate, Func<string, ResourceRef?> Resolve,
    RuntimeResourceCost? Cost = null, Func<string, string?>? Revision = null)
{
    /// <summary>Builds one owner from the answers it gives. The first two are required: a provider that could only
    /// list its resources could never resolve a compiled reference, and one that could only resolve could never
    /// answer a query step's enumeration. The cost port is optional because most kinds carry no quantity a step
    /// could spend.</summary>
    public static RuntimeResourceProvider Of(Func<IReadOnlyList<ResourceRef>> enumerate, Func<string, ResourceRef?> resolve,
        RuntimeResourceCost? cost = null, Func<string, string?>? revision = null)
        => new(enumerate ?? throw new ArgumentNullException(nameof(enumerate)),
            resolve ?? throw new ArgumentNullException(nameof(resolve)), cost, revision);
}

/// <summary>
/// One provider's native-object lookup, as the provider registers it, with the registration-order position that
/// decides who answers first. Like every other table a module hands over, it is owned: a provider that unregisters
/// takes its own lookups with it, so a package that is gone cannot keep answering for objects it used to know.
/// </summary>
public sealed record ObjectEntityResolver(Func<object, EntityReference?> Resolve)
{
    public static ObjectEntityResolver Of(Func<object, EntityReference?> resolve)
        => new(resolve ?? throw new ArgumentNullException(nameof(resolve)));
}

/// <summary>
/// One resource-kind read's complete answer, the same shape an entity enumeration has: a status, the code the
/// kernel or the provider refused with, the lifecycle it was read in, and the references themselves. The list is
/// empty exactly when the call was refused — never because a kind happened to hold nothing.
/// </summary>
public sealed class RuntimeResources
{
    public string Status { get; }
    public string Code { get; }
    public RuntimeLifecycleSnapshot Context { get; }
    public IReadOnlyList<ResourceRef> References { get; }
    public bool IsComplete => Status == "complete";
    internal RuntimeResources(string status, string code, RuntimeLifecycleSnapshot context, IEnumerable<ResourceRef> references)
    {
        Status = status; Code = code; Context = context;
        References = Array.AsReadOnly(references.ToArray());
    }
    /// <summary>The references, or a rejection: nothing here hands a caller a partial list that would look like a
    /// smaller world.</summary>
    public IReadOnlyList<ResourceRef> RequireComplete()
    {
        RuntimeJson.Require(IsComplete, Code, "Resource enumeration is not complete; no implicit partial selection is allowed.");
        return References;
    }
}
