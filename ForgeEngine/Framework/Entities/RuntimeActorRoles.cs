using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// The one table that says which slot of a trigger event each explicit actor role is read from, and how the two
/// spellings of one role relate. Nothing infers a role from another: a role whose slot the event does not carry is
/// absent, and reading it is what refuses — never a guess at a neighbouring slot.
///
/// A role is delivered to an `evaluate` handler through <see cref="EvaluationContext.Actors"/>. Four roles are read
/// from one payload port each — `source`, `instigator` and `owner` from the port of the same name, `event-target`
/// from the payload's `target` — and a port the event names but answers null is that role's absence. `self` is not
/// read from the payload at all: it is the entity the plan's own mount accepted for the event being dispatched, so
/// it is handed in rather than looked up. The contract's own selectors never allow that role to be absent, and a
/// mount that accepted several different entities leaves it unreadable instead of answering with a choice.
/// </summary>
internal static class RuntimeActorRoles
{
    /// <summary>One role, the capability port that spells it, and the payload port it is read from. A null payload
    /// port means the event cannot carry the role, which is what makes `self` the dispatch's own subject.</summary>
    private sealed record Slot(string Role, string PortId, string? PayloadPort);
    /// <summary>The table itself: the roles are the wire vocabulary's `recipient_anchor` members, in that order. A
    /// capability port spells a role with underscores. `self` has no payload port: the one entity the plan's mount
    /// accepted for this dispatch is its slot, and no port of the event is consulted for it. `event-target` is read
    /// from the payload's `target`, every other role from the port of its own name.</summary>
    private static readonly Slot[] Table = RuntimeGraphContracts.EnumSets["recipient_anchor"].Select(role => new Slot(role,
        role.Replace('-', '_'), role switch { "self" => null, "event-target" => "target", _ => role })).ToArray();

    private static readonly IReadOnlyDictionary<string, Slot> ByRole =
        Table.ToDictionary(slot => slot.Role, StringComparer.Ordinal);

    /// <summary>The role spelled the way a capability port spells it: `event_target`, not `event-target`.</summary>
    public static IReadOnlyList<string> PortIds { get; } =
        Array.AsReadOnly(Table.Select(slot => slot.PortId).ToArray());

    /// <summary>The role spelled the way the actor context and every capability that names one spells it.</summary>
    public static IReadOnlyList<string> Roles { get; } =
        Array.AsReadOnly(Table.Select(slot => slot.Role).ToArray());

    /// <summary>False for a name that is not one of the five roles, so a caller can ask rather than catch.</summary>
    public static bool IsRole(string? role) => role != null && ByRole.ContainsKey(role);

    /// <summary>Whether a port id spells one of the five roles. This is the one test the contract validator asks
    /// before it applies <see cref="RequireRolePort"/>, so the port names are spelled here and nowhere else.</summary>
    internal static bool IsRolePort(string? portId) => RoleForPort(portId) != null;

    /// <summary>The one rule about a context-role port, applied to both sides of a graph: a role port may be
    /// optional — the row's own contract then says the role can be absent, which is how `combat.damage.source`
    /// states that environmental damage has no dealer — but never nullable, because a wire that may hand the row
    /// nobody is ambiguous and nothing fills a context role in implicitly. An input is refused by this at
    /// registration; an output that carries either flag is not a role the capability demands (see
    /// <see cref="Required"/>).</summary>
    internal static void RequireRolePort(JsonElement port, string id)
        => RuntimeJson.Require(!RuntimeJson.Flag(port, "nullable"), "context-role-port", id);

    /// <summary>Whether a role-named port demands its role at dispatch: a port that may be absent (optional) or may
    /// answer null (nullable) reports an absence the framework must not turn into a refusal. The same two flags
    /// <see cref="RequireRolePort"/> reads, from the side that answers rather than the side that is wired.</summary>
    private static bool RequiresRole(JsonElement port)
        => !RuntimeJson.Flag(port, "optional") && !RuntimeJson.Flag(port, "nullable");

    /// <summary>The role a capability port carries, or null for a name outside the table. This is the only place the
    /// two spellings of one role are related: a selector is named by its port, the context is read by its role.</summary>
    public static string? RoleForPort(string? portId)
    {
        foreach (var slot in Table) if (slot.PortId == portId) return slot.Role;
        return null;
    }

    /// <summary>The capability port a role is written as. A name outside the table is refused by the actor context,
    /// not answered with a stranger's port.</summary>
    public static string PortFor(string role)
    {
        var slot = TryGet(role) ?? throw new RuntimeContractException("actor-role", "Unknown actor role: " + role);
        return slot.PortId;
    }

    private static Slot? TryGet(string? role)
        => role != null && ByRole.TryGetValue(role, out var slot) ? slot : null;

    /// <summary>
    /// The actors one trigger event carries for one dispatch, read through this table: `self` from the entities
    /// <paramref name="subjects"/> names — the distinct entities the plan's own mount targets accepted for this
    /// event, empty where none accepted one — and every other role from its own payload port. A role is present only
    /// where its port holds an entity; every other role is simply absent from the context, and a handler that needs
    /// it fails by name instead of receiving a placeholder. Exactly one accepted entity is the subject. Two or more
    /// leave `self` unreadable rather than answered with one of them or with an absence, and none leaves it absent.
    /// </summary>
    /// <remarks>The event is one the kernel admitted, so its payload is its cells and each role's cell was resolved
    /// with the trigger's registration (<see cref="PayloadCells"/>).</remarks>
    public static RuntimeActorContext FromTriggerEvent(RuntimeEvent value, IReadOnlyList<EntityReference> subjects)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(subjects);
        var actors = new Dictionary<string, EntityReference>(StringComparer.Ordinal);
        if (value.Cells is { } cells && value.CellBinding is { } binding)
        {
            var roles = binding.ActorCells;
            for (var slot = 0; slot < roles.Length; slot++)
                if (roles[slot] >= 0 && RuntimeKernel.CellEntity(cells[roles[slot]]) is { } actor) actors.Add(Table[slot].Role, actor);
        }
        var distinct = subjects.Distinct().ToArray();
        if (distinct.Length == 1) actors["self"] = distinct[0];
        return new RuntimeActorContext(actors, distinct.Length > 1);
    }

    /// <summary>The entity one admitted event carries for one role, or null where it carries none.</summary>
    internal static EntityReference? Actor(RuntimeEvent value, string role)
    {
        if (value.Cells is not { } cells || value.CellBinding is not { } binding) return null;
        var slot = Array.FindIndex(Table, entry => entry.Role == role);
        var cell = binding.ActorCells[slot];
        return cell >= 0 ? RuntimeKernel.CellEntity(cells[cell]) : null;
    }

    /// <summary>The cell each role of the table is read from in one trigger's events, by the trigger's own value
    /// outputs: -1 for a role the trigger declares no port for, and for `self`, which no port carries.</summary>
    /// <remarks>A port of another name carries a role only where its own contract says so (`actorRole`): the player
    /// a door trigger answers as `actor` is that event's instigator. One role has one port, so a trigger that both
    /// names a port after the role and declares the role on another, or declares it twice, is refused.</remarks>
    internal static int[] PayloadCells(PortView[] outputs)
    {
        var cells = new int[Table.Length];
        for (var slot = 0; slot < Table.Length; slot++)
        {
            cells[slot] = -1;
            if (Table[slot].PayloadPort is not { } port) continue;
            for (var index = 0; index < outputs.Length; index++)
            {
                if (outputs[index].Id != port && outputs[index].ActorRole != Table[slot].Role) continue;
                RuntimeJson.Require(cells[slot] < 0, "context-role-port", outputs[index].Id);
                cells[slot] = index;
            }
        }
        return cells;
    }

    /// <summary>The roles a trigger output of another name may declare itself to be. `self` is the mount's subject
    /// and `event-target` and `source` are read from the ports of their own names only.</summary>
    internal static bool IsDeclarableRole(string? role) => role is "instigator" or "owner";

    /// <summary>
    /// The roles a capability cannot be evaluated without: every role it answers with, declared as a non-optional,
    /// non-nullable output port. The contract's five role selectors are the case — four of them report an absent
    /// role as null, `self` has no absence to report — and such a capability is refused as `actor-missing` when the
    /// event does not carry the role, rather than being handed a placeholder to write. A role-named *input* is not
    /// one of these: it is an ordinary value whose satisfaction the plan's wiring decides, so demanding the role
    /// again would refuse a step an earlier step had already handed the value to.
    /// </summary>
    internal static IReadOnlyList<string> Required(JsonElement capability)
    {
        var required = new List<string>();
        if (!capability.TryGetProperty("graph", out var graph)) return required;
        if (!graph.TryGetProperty("outputs", out var ports) || ports.ValueKind != JsonValueKind.Array) return required;
        foreach (var port in ports.EnumerateArray())
        {
            if (port.ValueKind != JsonValueKind.Object || !port.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String) continue;
            var role = RoleForPort(id.GetString());
            if (role == null || !RequiresRole(port)) continue;
            if (!required.Contains(role)) required.Add(role);
        }
        return required;
    }
}
