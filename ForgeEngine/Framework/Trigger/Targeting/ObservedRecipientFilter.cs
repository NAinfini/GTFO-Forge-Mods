using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>One relation selection over explicit current references. This is a read-only answer, not permission,
/// cost reservation or a committed effect: the references it returns are re-checked by the kernel before a
/// consumer sees them, exactly like the candidates it was handed.</summary>
internal sealed class RecipientFilterSelection
{
    internal RecipientFilterSelection(EntityReference[] selected, int requested, int distinct)
    {
        Selected = Array.AsReadOnly(selected); Requested = requested; Distinct = distinct;
    }
    public IReadOnlyList<EntityReference> Selected { get; }
    public int Requested { get; }
    public int Distinct { get; }
    public int Matched => Selected.Count;
}

/// <summary>One filter row's structural parameters: the relation the candidates must have to the row's own
/// `anchor` input, the entity states and object categories the row accepts, and whether the anchor itself may be
/// one of the targets. There is no policy object here — the shape a plan compiles is the shape the capability
/// declares, and a field no row declares cannot be read by one. Every member is validated where it is read, so a
/// member the row does not declare answers the same refusal whether it arrived from a parameter bag or from a
/// caller.</summary>
internal sealed record RecipientFilterRequest(string? Relation, string State, int? Dimension,
    bool DamageableDoors, bool IncludeSelf, int? PlayerSlot = null, bool IncludeBots = true,
    string SlotOverflow = "none", string Kind = "any")
{
    public static readonly string[] Kinds = { "any", "enemy", "player" };
    /// <summary>The entity kind a non-`any` `kind` member requires of every candidate.</summary>
    internal string? RequiredEntityKind => EntityKindOf(Kind);
    /// <summary>The entity kind a `kind` member names, or null for `any` (or no member). The plan loader narrows the
    /// row's `targets` port by the same answer (<see cref="RuntimeEntityKindFlow.FilterKind"/>).</summary>
    internal static string? EntityKindOf(string? kind) => kind switch { "enemy" => EntityKinds.Enemy, "player" => EntityKinds.Player, _ => null };
    public const string KindCode = "recipient-kind";
    /// <summary>Every member of the catalog's `recipient_relation` set, in its declared order.</summary>
    public static readonly string[] Relations = { "self", "ally", "hostile", "neutral", "unknown" };
    /// <summary>The row's `state` members, in the catalog's own order: `any` accepts every candidate, `sleeping`
    /// the ones whose own behaviour state is the sleeping one, `awake` every candidate whose state was read and is
    /// not it.</summary>
    public static readonly string[] States = { "any", "sleeping", "awake" };
    /// <summary>The row's `slot_overflow` members: `none` leaves a named slot with nobody in it empty, and
    /// `fill_present` hands the selection to the candidates that are present instead — the "whole team" overflow
    /// the ledger row names. The member order is the catalog's.</summary>
    public static readonly string[] SlotOverflows = { "none", "fill_present" };

    /// <summary>The highest player slot the game has: slots are `0`..`3`, so a slot outside that range is refused
    /// rather than answered as an empty selection.</summary>
    public const int MaximumPlayerSlot = 3;

    /// <summary>The `ai_state` member a sleeping candidate is in — the state an enemy group publishes while it is
    /// asleep (`RuntimeGraphContracts.EnumSets["ai_state"]`). The one member this row names by hand, because
    /// "asleep" is a reading of the shared set and not a set of its own.</summary>
    internal const string SleepingState = "hibernating";

    /// <summary>The label a map object's own provider publishes its category under, which is how a candidate says
    /// it is a door or a terminal without the filter knowing any category by name.</summary>
    internal const string CategoryTag = "map-object.category=";

    /// <summary>The label a player's own provider publishes for a robot life, and the prefix of the label it
    /// publishes a slot under (`player.slot-2`). Both are the player provider's own spellings, read here exactly
    /// as the map-object category label above is: the filter asks the snapshot what its provider said about it
    /// instead of keeping a second table of players.</summary>
    internal const string BotTag = "player.bot";
    internal const string SlotTagPrefix = "player.slot-";

    /// <summary>A candidate whose own behaviour state this runtime cannot read, which the `state` members refuse
    /// rather than answer as awake.</summary>
    public const string StateUnknownCode = "recipient-state-unknown";
    /// <summary>A candidate whose own dimension cannot be read from the zone its provider places it in.</summary>
    public const string DimensionCode = "recipient-dimension";
    /// <summary>The row's `player_slot` names a slot the game does not have.</summary>
    public const string SlotCode = "recipient-slot";
    /// <summary>The row's `slot_overflow` names a member the row does not declare.</summary>
    public const string SlotOverflowCode = "recipient-slot-overflow";

    /// <summary>One request read from a row's resolved parameter frame, whose enums arrive as member names and
    /// whose numbers and flags arrive as the values the author wrote. A parameter the plan left out filters
    /// nothing — no relation, `any` for the state, no dimension, no slot, doors and bots and the source kept,
    /// no overflow — and a name outside its list is refused rather than answered with an empty selection, because
    /// "no candidate has a relation nobody declared" would look exactly like a world in which none of them
    /// matched. The relation is the catalog's one genuinely optional selection rule: a walk that names none keeps
    /// every candidate the other rules accept, which is the same meaning the weapon-graph compiler gives an
    /// unwritten parameter.</summary>
    public static RecipientFilterRequest Read(string? relation = null, string? state = null,
        int? dimension = null, bool damageableDoors = true, bool includeSelf = true, int? playerSlot = null,
        bool includeBots = true, string? slotOverflow = null, string? kind = null)
        => new(OptionalMember(relation, Relations, "recipient-relation"), Member(state, States, "recipient-state", 0),
            dimension, damageableDoors, includeSelf, playerSlot, includeBots,
            Member(slotOverflow, SlotOverflows, SlotOverflowCode, 0), Member(kind, Kinds, KindCode, 0));

    /// <summary>Refuses a request whose own members are not the ones the row declares, before any world read: a
    /// request assembled by hand carries the same refusal a compiled frame does. The relation may be absent —
    /// "not asked" is a value the row declares — but a relation outside the catalog's set is refused.</summary>
    internal void Validate()
    {
        foreach (var (member, members, code) in new[]
                 {
                     (State, States, "recipient-state"), (SlotOverflow, SlotOverflows, SlotOverflowCode),
                     (Kind, Kinds, KindCode)
                 })
            if (member == null || !members.Contains(member, StringComparer.Ordinal))
                throw new RuntimeContractException(code, "Unknown member: " + member);
        if (Relation != null && !Relations.Contains(Relation, StringComparer.Ordinal))
            throw new RuntimeContractException("recipient-relation", "Unknown member: " + Relation);
        if (PlayerSlot is { } slot && (slot < 0 || slot > MaximumPlayerSlot))
            throw new RuntimeContractException(SlotCode, "A player slot is 0.." + MaximumPlayerSlot + ": " + slot);
    }

    /// <summary>Whether one observed candidate is of the state the row asked for. A candidate whose behaviour
    /// state no observer publishes is refused: "asleep" and "awake" are readings, and a row that answered an
    /// unreadable candidate as either of them would be filtering on a value nobody read.</summary>
    internal bool AcceptsState(RuntimeEntitySnapshot snapshot)
    {
        if (State == "any") return true;
        if (snapshot.AiState == null)
            throw new RuntimeContractException(StateUnknownCode, "The behaviour state of " + snapshot.Ref.Id + " could not be read.");
        return State == "sleeping" ? snapshot.AiState == SleepingState : snapshot.AiState != SleepingState;
    }

    /// <summary>Whether one observed candidate survives the door rule: a map object is a door or a terminal the
    /// level placed, which its own provider says by publishing the category label, and a candidate that carries no
    /// such label is not one.</summary>
    internal bool AcceptsDoors(RuntimeEntitySnapshot snapshot)
        => DamageableDoors || !snapshot.Tags.Any(tag => tag.StartsWith(CategoryTag, StringComparison.Ordinal));

    /// <summary>Whether one observed candidate survives the robot rule: a candidate whose own provider published
    /// the robot label is one, and `include_bots = false` drops it. The label is the player provider's, read the
    /// same way the door rule reads the map object's category.</summary>
    internal bool AcceptsBots(RuntimeEntitySnapshot snapshot)
        => IncludeBots || !snapshot.Tags.Contains(BotTag, StringComparer.Ordinal);

    /// <summary>Whether one observed candidate stands in the player slot the row named. A slot is a fact about the
    /// candidate, so it is read from the candidate's own snapshot and never from the anchor or the event.</summary>
    internal bool InSlot(RuntimeEntitySnapshot snapshot, int slot)
        => snapshot.Tags.Contains(SlotTagPrefix + slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparer.Ordinal);

    private static string Member(string? member, string[] members, string code, int whenAbsent)
    {
        if (member == null)
        {
            if (whenAbsent >= 0) return members[whenAbsent];
            throw new RuntimeContractException(code, "A member is required.");
        }
        if (!members.Contains(member, StringComparer.Ordinal))
            throw new RuntimeContractException(code, "Unknown member: " + member);
        return member;
    }

    /// <summary>One optional member: absent stays absent, but a member outside the list is refused exactly as a
    /// required one would be. An unwritten relation is not a member this table invents — it means the row asks
    /// for no relation rule at all.</summary>
    private static string? OptionalMember(string? member, string[] members, string code)
    {
        if (member == null) return null;
        if (!members.Contains(member, StringComparer.Ordinal))
            throw new RuntimeContractException(code, "Unknown member: " + member);
        return member;
    }
}

/// <summary>Keeps the candidates whose current relation to the row's own anchor is the requested one, using the
/// world's own faction relations and the step's own observation session. A row that names no relation applies no
/// relation rule and keeps every candidate the other rules accept — the catalog declares `relation` optional, so
/// "not asked" is a shape a plan really has.
///
/// The anchor is the explicit `anchor` input of the filter row, never a role looked up again: a deployable whose
/// owner is the thing its targets must relate to wires `owner` into that port, and the row measures the relation
/// against exactly what it was handed. A pair the world says nothing about resolves to `unknown`, which is a
/// relation like any other and therefore matches only when `unknown` was asked for; it is never treated as "no
/// match" or as a guess at neutrality. The answer is ordered by stable identity, so the same world and the same
/// candidates always produce the same set.</summary>
internal static class ObservedRecipientFilter
{
    public static RecipientFilterSelection Select(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference anchor, RuntimeFactionRelations relations, RecipientFilterRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(request);
        // A request the row cannot answer is refused before the world is read: a member the catalog does not
        // declare and a candidate list past the read budget are decidable from the step's own inputs, and a step
        // that could never have answered leaves no observation behind.
        var captured = candidates.ToArray();
        RuntimeEntityReferences.Validate(anchor);
        if (captured.Length > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Candidate input exceeds the public query limit.");
        request.Validate();
        var requested = captured.Contains(anchor) ? captured : captured.Append(anchor).ToArray();
        // One read for the candidates and their anchor together: the anchor's own current faction is part of the
        // answer, so observing it separately would be a second chance at a different world.
        var observed = ObservedSpaceNodes.ObserveCurrent(session, requested, anchor);
        var anchorSnapshot = observed.Single(row => row.Ref == anchor);
        var candidatesSet = new HashSet<EntityReference>(captured);
        // Every member is measured on the one observation the row already made: the anchor's own snapshot is the
        // relation basis, the candidate's own snapshot is its state, category, robot label and slot label, and its
        // dimension — the one reading a snapshot does not carry — is the zone its own provider places it in.
        var accepted = new List<RuntimeEntitySnapshot>();
        foreach (var row in observed)
        {
            if (!candidatesSet.Contains(row.Ref)) continue;
            if (request.RequiredEntityKind is { } required && row.Kind != required)
                throw new RuntimeContractException(RecipientFilterRequest.KindCode,
                    "The candidate " + row.Ref.Id + " is " + row.Kind + ", not " + required + ".");
            if (!request.IncludeSelf && row.Ref == anchor) continue;
            if (!request.AcceptsDoors(row) || !request.AcceptsBots(row) || !request.AcceptsState(row)) continue;
            accepted.Add(row);
        }
        // The dimension is read for the whole accepted set at once, as ZoneMembers does: one read per candidate
        // would spend the tick's query budget on a single filter over a level's enemies.
        var inDimension = request.Dimension is { } dimension ? InDimension(session, accepted, dimension) : null;
        var matched = new List<RuntimeEntitySnapshot>();
        for (var i = 0; i < accepted.Count; i++)
        {
            if (inDimension != null && !inDimension[i]) continue;
            if (request.Relation is { } relation
                && !string.Equals(relations.Resolve(anchorSnapshot, accepted[i]), relation, StringComparison.Ordinal)) continue;
            matched.Add(accepted[i]);
        }
        // The slot is applied last and to the whole set at once: a named slot with nobody in it is either empty
        // or, under `fill_present`, the candidates that are present instead — the "whole team" overflow the
        // ledger row names. The overflow never widens the set past what the other rules already accepted.
        if (request.PlayerSlot is { } slot)
        {
            var bySlot = matched.Where(row => request.InSlot(row, slot)).ToList();
            if (bySlot.Count > 0 || request.SlotOverflow != "fill_present") matched = bySlot;
        }
        return new RecipientFilterSelection(
            matched.Select(row => row.Ref).OrderBy(ReferenceCollections.OrderKey, StringComparer.Ordinal).ToArray(),
            captured.Length, candidatesSet.Count);
    }

    /// <summary>Whether each candidate stands in the dimension the row named. A zone is the only place an entity's
    /// dimension is published, and its id is the coordinates the runtime's own <see cref="RuntimeZones"/> formats,
    /// so the zones are read through the step's session like every other fact, as one set. A candidate no provider
    /// can place, or whose zone id is not those coordinates, is refused rather than answered as "another dimension".</summary>
    private static bool[] InDimension(RuntimeQuerySession session, List<RuntimeEntitySnapshot> rows, int dimension)
    {
        var found = new bool[rows.Count];
        if (rows.Count == 0) return found;
        var zones = new List<EntityZoneResolution>(rows.Count);
        if (!session.TryZones(rows.Select(row => row.Ref).ToArray(), zones, out var code))
            throw new RuntimeContractException(code, "The zones of the candidates could not be read: " + code);
        var prefix = EntityKinds.Zone + ":";
        for (var i = 0; i < rows.Count; i++)
        {
            var zone = zones[i].Zone;
            var segments = zone != null && zone.Id.StartsWith(prefix, StringComparison.Ordinal)
                ? zone.Id.Substring(prefix.Length).Split(':') : Array.Empty<string>();
            if (segments.Length != 3
                || !int.TryParse(segments[0], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw new RuntimeContractException(RecipientFilterRequest.DimensionCode,
                    "The zone of " + rows[i].Ref.Id + " does not carry the runtime's own coordinates.");
            found[i] = value == dimension;
        }
        return found;
    }
}
