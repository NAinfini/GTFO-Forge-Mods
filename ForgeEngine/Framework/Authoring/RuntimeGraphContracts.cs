using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeEngine.Framework;

/// <summary>
/// Forge Standard v0.2 graph metadata: validation, variable port expansion and the dense
/// slot layout a compiled plan carries. It neither binds nor executes nodes. The vocabulary
/// tables are <see cref="Wire"/>, generated from the one source the website reads too; array
/// order is part of the wire because compiled layouts store indices into it.
/// </summary>
internal static class RuntimeGraphContracts
{
    internal const int MaximumVariadicPorts = 32;
    /// <summary>Mirrors Web/src/forge/graph-schema.ts DOMAIN_REASON_CODE_MAX_LENGTH / DOMAIN_REASON_CODE_PATTERN.</summary>
    internal const int DomainReasonCodeMaxLength = 64;
    private static readonly Regex DomainReasonCodePattern = new(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    /// <summary>The four columns every result schema starts with, in this order and with these exact types.
    /// `status` uses a subset of execution_outcome; `committed` uses the commit_state set.</summary>
    internal static readonly string[] FixedResultFields = { "target", "status", "committed", "code" };
    /// <summary>The input-only interface to a concrete action result. It declares no replacement row shape:
    /// the producer keeps its schema, recipient identity and full descriptor.</summary>
    internal const string CommonResultSchema = "forge.result.common";
    internal const int MaximumResultFields = 32;
    /// <summary>The kinds whose owner holds no authored revision: native-resident game data and tables the runtime
    /// builds itself. A plan's compiled reference to one of these is checked by id alone. Every other kind
    /// (`area_field`, `audio`, `profile`) is authored content, and its owner must answer a revision.</summary>
    internal static readonly HashSet<string> RevisionExemptResourceKinds = new(StringComparer.Ordinal)
    {
        "map", "room", "enemy", "weapon", "tool", "consumable", "item", "objective", "encounter", "wave", "spawn_pool",
        "behavior_graph", "ability", "path", "animation", "effect", "material", "model", "pool", "chained-puzzle", "zone",
        "text", "generator"
    };
    /// <summary>The kinds whose lost file only costs the cue: a plan naming one that no owner holds still loads and runs, and the action
    /// that would use it plays nothing and says so once. Every other kind's dangling reference stays `stale-resource`.</summary>
    internal static readonly HashSet<string> MissingTolerantResourceKinds = new(StringComparer.Ordinal) { "audio" };
    /// <summary>The shared member sets by name. A set's position in <see cref="Wire.EnumSets"/> is an enum port's compiled
    /// valueSet, and a member's position its compiled value.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> EnumSets =
        Wire.EnumSets.ToDictionary(x => x.Name, x => x.Members, StringComparer.Ordinal);
    /// <summary>The one identity a trigger's own fact may declare besides being an event: the fact belongs to one
    /// shot, which is what a `shot`-scoped gate books its threshold, cap and clock against. A trigger that
    /// declares none publishes no such identity, so a `shot` gate on it is refused at plan load rather than
    /// silently booked against the mount instance. Mirrors Web/src/forge/contracts.ts `graphFactIdentities`.</summary>
    internal const string ShotFactIdentity = "shot";
    /// <summary>The set names in declaration order: the members a structural `enum_set` parameter chooses from, so a
    /// row that covers every shared set — `forge.condition.predicate.compare`, `forge.control.flow.enum_switch` — reads
    /// this list instead of restating it. Published through <see cref="RuntimeEnumSets"/>.</summary>
    internal static readonly string[] EnumSetNames = Wire.EnumSets.Select(set => set.Name).ToArray();
    /// <summary>Whether a name is a member of one of the shared sets, asked by name so a caller that reads a
    /// member from outside a port (an observation field) can check it without a port to index the set through.</summary>
    internal static bool IsEnumMember(string set, string? member)
        => member != null && EnumSets.TryGetValue(set, out var members) && Array.IndexOf(members, member) >= 0;
    /// <summary>Q3: an enum's runtime/wire value is a member-set index, never its name. A structural parameter's
    /// inline `values` narrows the index basis to that list; everything else (promoted parameters, ports) indexes
    /// the full named set.</summary>
    internal static string[] EnumMembers(JsonElement enumDefinition) => enumDefinition.TryGetProperty("values", out var inline)
        ? RuntimeJson.Strings(inline) : EnumSets[RuntimeJson.Text(enumDefinition, enumDefinition.TryGetProperty("set", out _) ? "set" : "schema")];
    /// <summary>The value type a parameter-only type is written as in a frame. An `object-address` is the canonical
    /// address text the provider's own address record parses, so its constant is a string slot and a selector reads
    /// it as a string; the frame table resolves a type through here and nowhere else.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ParameterFrameTypes =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["object-address"] = "string" };
    internal static string FrameValueType(string type) => ParameterFrameTypes.TryGetValue(type, out var carried) ? carried : type;
    /// <summary>The handle kind a structural member names, or null for a member that is a plain value class. A
    /// member outside both lists is not one this vocabulary offers.</summary>
    internal static string? GuardedHandleKind(string member)
    {
        if (!member.StartsWith("handle:", StringComparison.Ordinal)) return null;
        var qualified = member.Substring("handle:".Length);
        var kind = qualified.Split(':')[0];
        return Wire.GuardedHandleLifetimes.ContainsKey(qualified) && Wire.HandleKinds.Contains(kind) ? kind : null;
    }
    /// <summary>The resource kind a structural member names. Its existing kind owns the complete schema;
    /// neither an opaque resource id nor the declared placeholder type can supply that identity.</summary>
    internal static string? GuardedResourceKind(string member)
    {
        if (!member.StartsWith("resource:", StringComparison.Ordinal)) return null;
        var kind = member.Substring("resource:".Length);
        return Wire.ResourceKinds.Contains(kind) ? kind : null;
    }
    /// <summary>Every member a `value_type` parameter may offer: the value classes, plus guardable handles
    /// and named resource kinds under their two-part spelling.</summary>
    internal static bool IsValueTypeMember(string member)
        => Wire.ValueTypeParameterTypes.Contains(member) || GuardedHandleKind(member) != null || GuardedResourceKind(member) != null;

    /// <summary>One port's physical form inside a frame: a kind it never occupies has width 0 and no slot.
    /// "execution" carries no value. A "many" port is one head slot plus <see cref="Wire.MaxSetWidth"/>
    /// elements at the element type's own width. A resource is its kind index in the head slot and its id in the
    /// next one; an event is the row index of the dispatch's own event; a result row is the sum of its declared
    /// fields and has no head slot of its own.</summary>
    internal static (ValueKind Kind, int Width) FramePort(JsonElement port)
    {
        var type = RuntimeJson.Text(port, "type");
        if (type == "execution") return (ValueKind.Missing, 0);
        if (type == "result")
        {
            // A row is one row: it is already a region of its own declared fields, and there is no such thing as a
            // collection of rows a port could name.
            RuntimeJson.Require(!Many(port), "unsupported-port", RuntimeJson.Text(port, "id") + " is declared as a collection of result");
            // A common input reserves one opaque reference cell, not a fabricated row. Its source's descriptor
            // still owns every column and is the only descriptor a downstream read uses.
            if (IsCommonResultPort(port)) return (ValueKind.Result, 1);
            return (ValueKind.Result, ResultRowWidth(port));
        }
        var (kind, width) = type switch
        {
            "handle" => (ValueKind.Handle, 1),
            "resource" => (ValueKind.Resource, RuntimeFrames.ResourceWidth),
            "event" => (ValueKind.Event, 1),
            "policy" => (ValueKind.Missing, 0),
            _ => Wire.RuntimeValueTypes.Contains(type) ? (RuntimeFrames.KindOf(type), RuntimeFrames.ValueWidth(type)) : (ValueKind.Missing, 0)
        };
        if (!Many(port)) return (kind, width);
        // Only a kind with an element slot has a collection form. The reference and row kinds are refused here
        // rather than reserved a width nothing could fill; the plan loader refuses them one step earlier, where
        // it knows which side of the boundary the port sits on.
        RuntimeJson.Require(RuntimeFrames.Segmented(kind),
            "unsupported-port", RuntimeJson.Text(port, "id") + " is declared as a collection of " + type);
        return (kind, RuntimeFrames.SegmentWidth(kind));
    }

    /// <summary>The slot width of one result row: its declared fields at their own widths, in declaration order.
    /// There is no head slot, so the four fixed columns make the row at least four slots wide and a later vector3
    /// column widens it by three. A resource target is one column whose value is two slots — its kind index and its
    /// id — and an entity column is one whose value is three, the id it names and its two epochs.</summary>
    internal static int ResultRowWidth(JsonElement port)
    {
        var width = 0;
        var fields = ResultFields(port);
        for (var index = 0; index < fields.Length; index++) width += ResultColumnWidth(fields[index], index);
        return width;
    }
    /// <summary>
    /// The slots one row column occupies: an entity column's identity (its id text and its two epochs), the resource
    /// target's kind index and id string, three for a vector3, one for every other implemented kind.
    ///
    /// An entity column is the one row column whose form is wider than its port form, and it is the row's own: a row
    /// keeps the reference the handler named — its id text and both epochs — while a port that carries an entity as a
    /// value keeps the single fixed-width cell <see cref="RuntimeFrames.ValueWidth"/> describes. A cell cannot hold
    /// the id text and two safe integers at once, and a row that narrowed or re-spelled them would report an identity
    /// the handler never returned.
    /// </summary>
    internal static int ResultColumnWidth(JsonElement field, int index)
    {
        var type = RuntimeJson.Text(field, "type");
        if (type == "entity") return ResultEntityWidth;
        return index == 0 && type == "resource" ? RuntimeFrames.ResourceWidth : RuntimeFrames.ValueWidth(type);
    }
    /// <summary>The slots one row's entity column occupies: the id's own string slot and one safe-integer slot for
    /// each epoch. It is the smallest form that keeps an admitted entity value exactly as the contract admits it.</summary>
    internal const int ResultEntityWidth = 3;
    /// <summary>The declared columns of one result row, in declaration order.</summary>
    internal static JsonElement[] ResultFields(JsonElement port)
    {
        RuntimeJson.Require(port.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array,
            RuntimeAbiCodes.ResultSchema, RuntimeJson.Text(port, "id"));
        return RuntimeJson.Elements(fields);
    }
    /// <summary>The frame kind of one declared `target` column: the three world-reference kinds and nothing else. A
    /// row whose target names another type is refused where the port is validated, so nothing downstream has to ask
    /// whether a target could be a plain value.</summary>
    internal static ValueKind TargetKindOf(string type)
    {
        RuntimeJson.Require(Wire.RecipientTargets.Contains(type), RuntimeAbiCodes.ResultSchema, "target:" + type);
        return type switch { "resource" => ValueKind.Resource, "handle" => ValueKind.Handle, _ => ValueKind.Entity };
    }
    /// <summary>The kinds one port declares, or an empty list for a port that declares none. The declaration is the
    /// one place kinds come from, so the compiled target copies this list rather than deriving it from a value.</summary>
    internal static string[] DeclaredEntityKinds(JsonElement port)
    {
        if (!port.TryGetProperty("entityKinds", out var kinds) || kinds.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var result = new string[kinds.GetArrayLength()];
        var at = 0;
        foreach (var kind in kinds.EnumerateArray()) result[at++] = kind.GetString() ?? string.Empty;
        return result;
    }
    /// <summary>The resource kind's index in the shared kind table, or -1 on a port that names none.</summary>
    internal static int ResourceKindIndex(JsonElement port)
        => Array.IndexOf(Wire.ResourceKinds, OptionalText(port, "resourceKind"));
    /// <summary>The handle kind's index in the shared kind table, or -1 on a port that names none.</summary>
    internal static int HandleKindIndex(JsonElement port)
        => Array.IndexOf(Wire.HandleKinds, OptionalText(port, "handleKind"));
    /// <summary>The handle lifetime's index in the shared lifetime table, or -1 on a port that names none.</summary>
    internal static int LifetimeIndex(JsonElement port)
        => Array.IndexOf(Wire.HandleLifetimes, OptionalText(port, "lifetime"));
    /// <summary>One port of a contract by side and id, or null where the contract declares none.</summary>
    internal static JsonElement? PortOf(JsonElement contract, string side, string id)
    {
        foreach (var port in RuntimeJson.Rows(contract, side))
            if (RuntimeJson.Text(port, "id") == id) return port;
        return null;
    }
    /// <summary>A declared port field that may legitimately be absent, read as text only when it is a string.</summary>
    internal static string? OptionalText(JsonElement port, string key)
        => port.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>Whether one text is a well-formed domain reason code, which is the only shape a row's `code` may
    /// have: the pattern the catalog's own code lists are validated against.</summary>
    internal static bool IsDomainReasonCode(string code) => DomainReasonCodePattern.IsMatch(code);
    /// <summary>The result port a capability's own recipients declaration names: the one port its rows are written
    /// through and the one port a later step reads them from. Empty where the contract declares no recipient set,
    /// which is the shape a step that writes no rows has.</summary>
    internal static string ResultPortName(JsonElement contract)
    {
        if (contract.ValueKind != JsonValueKind.Object || !contract.TryGetProperty("recipients", out var recipients)) return "";
        return recipients.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString()! : "";
    }
    /// <summary>True for the result-row port of a capability: the row frame's own port, never a value in a step frame.</summary>
    internal static bool IsResult(JsonElement port) => RuntimeJson.Text(port, "type") == "result";

    internal static bool IsCommonResultPort(JsonElement port)
        => IsResult(port) && OptionalText(port, "schema") == CommonResultSchema;

    internal static bool IsName(string value) => Regex.IsMatch(value, @"^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
    private static string? Optional(JsonElement value, string key) => value.TryGetProperty(key, out var field) ? field.GetString() : null;
    internal static string Cardinality(JsonElement port) => Optional(port, "cardinality") ?? "one";
    internal static bool Many(JsonElement port) => Cardinality(port) == "many";
    /// <summary>Full value contract equality, used by repeated ports and by plan input wiring.</summary>
    internal static bool SameValue(JsonElement a, JsonElement b)
        => ValueTypeMatches(a, b) && Cardinality(a) == Cardinality(b);
    /// <summary>Value contract equality ignoring cardinality: a wired plan input may pair a
    /// non-nullable "one" output with a "many" input, wrapped into a one-element collection at dispatch.
    /// Every other dimension must still match exactly — except entity kinds, which narrow: see
    /// <see cref="EntityKindsNarrow"/>.</summary>
    internal static bool ValueTypeMatches(JsonElement a, JsonElement b)
        => RuntimeJson.Text(a, "type") == RuntimeJson.Text(b, "type")
           && new[] { "schema", "unit", "resourceKind", "handleKind", "lifetime" }.All(key => Optional(a, key) == Optional(b, key))
           && EntityKindsNarrow(a, b);
    /// <summary>What one edge may deliver into a plan input, which is the compiler's own rule and not equality:
    /// an integer widens to a number, entity kinds narrow, and a unit travels only where the two sides agree or
    /// where one of them declares none. The identities a widened value would silently retarget — schema,
    /// resource kind, handle kind, lifetime — still have to agree. Mirrors
    /// Web/src/forge/graph-schema.ts `assignableGraphPort`, which is what lowered the wires this kernel loads;
    /// cardinality is judged separately, where the one-to-many wrap is applied.</summary>
    internal static bool AssignableValue(JsonElement output, JsonElement input)
    {
        var from = RuntimeJson.Text(output, "type");
        var into = RuntimeJson.Text(input, "type");
        // The common interface is a sink, never a producer or a cast to a concrete schema. A concrete source
        // must still prove all of its own fixed fields and target identity; this is not an `any` value port.
        if (IsCommonResultPort(output)) return false;
        if (IsCommonResultPort(input))
        {
            if (from != "result" || Many(output) || string.IsNullOrEmpty(OptionalText(output, "schema"))
                || Many(input) || RuntimeJson.Flag(input, "nullable") || RuntimeJson.Flag(input, "optional")
                || input.TryGetProperty("fields", out _) || input.TryGetProperty("codes", out _)) return false;
            try
            {
                ValidateResultFields(output, RuntimeJson.Text(output, "id"));
                return true;
            }
            catch (RuntimeContractException) { return false; }
        }
        return (from == into || (from == "integer" && into == "number"))
               && new[] { "schema", "resourceKind", "handleKind", "lifetime" }.All(key => Optional(output, key) == Optional(input, key))
               && (Optional(output, "unit") == null || Optional(input, "unit") == null
                   || Optional(output, "unit") == Optional(input, "unit"))
               && EntityKindsNarrow(output, input);
    }
    /// <summary>Kind compatibility between a producer (`output`) and a consumer (`input`): every kind the
    /// output may carry has to be one the input accepts — the same kind, or a subkind whose parent it accepts —
    /// so a narrow port wires into a wide one and never the other way. An input that declares none accepts them
    /// all, which is the one direction that makes the generic ports universal. Mirrors
    /// Web/src/forge/graph-schema.ts assignableGraphPort.
    ///
    /// An output whose kind a written map object address names (<see cref="KindsDeferred"/>) is the one output
    /// this rule cannot read a carried set from: the address's category spelling is the provider's grammar, and the
    /// loader does not infer a kind from it (contract §3.2 I-OPERATOR, `entityKindsFromParameter`). Such an edge is judged by
    /// the consumer's declared kinds, and the provider's refusal of a reference that is not its own kind is what
    /// holds at run time. An address nobody wrote is not deferred: the plan resolver drops the deferral, the port
    /// is unbound, and an unbound port stays every kind here.</summary>
    internal static bool EntityKindsNarrow(JsonElement output, JsonElement input)
    {
        var accepted = DeclaredEntityKinds(input);
        if (accepted.Length == 0 || KindsDeferred(output)) return true;
        // Unspecified means any known entity kind, never an implicit narrowing cast.
        var declared = DeclaredEntityKinds(output);
        var carried = declared.Length > 0 ? declared : EntityKinds.All;
        return carried.All(kind => Accepted(kind, accepted));
    }
    /// <summary>True for an entity output whose kind is deferred to the address its node was compiled with: it still
    /// carries `entityKindsFromParameter` after resolution, which only happens where that address was written, and
    /// it declares no kind list of its own. A glue output that forwards such a port keeps the same deferral
    /// (<see cref="RuntimeEntityKindFlow.Resolve"/>).</summary>
    internal static bool KindsDeferred(JsonElement port)
        => RuntimeJson.Text(port, "type") == "entity" && port.TryGetProperty("entityKindsFromParameter", out _)
           && !port.TryGetProperty("entityKinds", out _);
    /// <summary>The kinds an entity id can carry when a port declares <paramref name="declared"/>. A subkind is a port
    /// type only: the snapshot and the instance table publish the parent kind, so an id's first segment never reads
    /// `gtfo.map_object.door` — a door is `gtfo.map_object:door/...`. A runtime check of an id against a port's
    /// declared kinds therefore compares the published kinds, or every id of a subkind-only port would be refused.</summary>
    internal static string[] PublishedEntityKinds(string[] declared)
    {
        var published = new List<string>(declared.Length);
        foreach (var kind in declared)
        {
            var carried = kind;
            foreach (var (subkind, parent) in Wire.EntityKindParents)
                if (subkind == kind) { carried = parent; break; }
            if (!published.Contains(carried)) published.Add(carried);
        }
        return published.ToArray();
    }
    /// <summary>One carried kind against one accepted set: the kind itself, or its parent when it has one.</summary>
    private static bool Accepted(string kind, string[] accepted)
    {
        if (accepted.Contains(kind, StringComparer.Ordinal)) return true;
        foreach (var (subkind, parent) in Wire.EntityKindParents)
            if (subkind == kind) return accepted.Contains(parent, StringComparer.Ordinal);
        return false;
    }

    internal static void ValidatePort(JsonElement port, string id)
    {
        RuntimeJson.Shape(port, "id type", "cardinality schema resourceKind handleKind lifetime unit minimum maximum nullable optional codes fields valueTypeParameter schemaParameter entityKinds entityKindsFrom entityKindsFromParameter when actorRole");
        RuntimeJson.Require(IsName(RuntimeJson.Text(port, "id")), "port-name", id);
        var type = RuntimeJson.Text(port, "type");
        RuntimeJson.Require(Wire.PortTypes.Contains(type), "port-type", id);
        // A trigger's own single-entity output may say which actor role it also is (the player who opened the door is
        // the event's instigator); the role table reads the role from that port instead of from a second copy of it.
        if (port.TryGetProperty("actorRole", out var actorRole))
            RuntimeJson.Require(type == "entity" && !Many(port) && RuntimeActorRoles.IsDeclarableRole(RuntimeJson.Text(actorRole)),
                "context-role-port", id);
        if (port.TryGetProperty("when", out var when))
        {
            RuntimeJson.Shape(when, "parameter value");
            RuntimeJson.Require(IsName(RuntimeJson.Text(when, "parameter")), "port-condition", id);
            RuntimeJson.Text(when, "value");
            RuntimeJson.Require(type is not ("execution" or "result"), "port-condition", id);
        }
        // The lowest and highest values the row accepts, declared on the port so an editor converting an author's
        // value (seconds to ticks) clamps to the row's own floor. Each is a number on a numeric port and nothing else.
        foreach (var bound in new[] { "minimum", "maximum" })
            if (port.TryGetProperty(bound, out var limit))
                RuntimeJson.Require(type is "integer" or "number" && limit.ValueKind == JsonValueKind.Number
                    && limit.TryGetDouble(out var edge) && double.IsFinite(edge)
                    && (type == "number" || edge == Math.Floor(edge)), bound == "minimum" ? "port-minimum" : "port-maximum", id);
        if (port.TryGetProperty("minimum", out var minimum) && port.TryGetProperty("maximum", out var maximum))
            RuntimeJson.Require(minimum.GetDouble() <= maximum.GetDouble(), "port-bounds", id);
        foreach (var flag in new[] { "nullable", "optional" })
            if (port.TryGetProperty(flag, out var value))
                RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "port-flag", id);
        foreach (var key in new[] { "cardinality", "schema", "resourceKind", "handleKind", "lifetime", "unit" })
            if (port.TryGetProperty(key, out var text)) RuntimeJson.Text(text);
        if (port.TryGetProperty("cardinality", out _)) RuntimeJson.Require(Wire.Cardinalities.Contains(Cardinality(port)), "port-cardinality", id);
        // Kinds belong to an entity port alone: any other port promising them would be naming a kind it cannot
        // deliver. An empty or repeating list is the same authoring mistake, and a member outside the table is
        // not a kind this runtime observes.
        if (port.TryGetProperty("entityKinds", out var entityKinds))
        {
            RuntimeJson.Require(type == "entity" && entityKinds.ValueKind == JsonValueKind.Array, "port-entity-kinds", id);
            var kinds = entityKinds.EnumerateArray().Select(kind => kind.ValueKind == JsonValueKind.String ? kind.GetString() : null).ToArray();
            RuntimeJson.Require(kinds.Length > 0 && kinds.All(kind => kind != null && EntityKinds.All.Contains(kind)), "port-entity-kinds", id);
            RuntimeJson.Require(kinds.Distinct(StringComparer.Ordinal).Count() == kinds.Length, "port-entity-kinds", id);
        }
        // A typed port declares only its class and defers its concrete type to the structural member an author
        // chooses: the member's own rules are applied where it resolves (`TypedPort`), because the declaration
        // cannot know which of them will be the port's. The class itself is still refused here when no member
        // could ever be one.
        // A schema parameter is the same deferral for the port's identity: the enum set arrives from the member the
        // plan compiles, so the declaration carries the parameter and never a set of its own beside it.
        var deferredSchema = port.TryGetProperty("schemaParameter", out var schemaParameter);
        if (deferredSchema)
            RuntimeJson.Require(IsName(RuntimeJson.Text(schemaParameter)) && !port.TryGetProperty("schema", out _), "port-type", id);
        if (port.TryGetProperty("valueTypeParameter", out var parameter))
        {
            RuntimeJson.Require(IsName(RuntimeJson.Text(parameter)) && Wire.ValueTypeParameterTypes.Contains(type),
                "port-type", id);
            return;
        }
        if (deferredSchema) return;
        RequirePortTypeContract(port, type, id);
    }

    /// <summary>The rules a port's own type carries, in one body because a port and the port a typed declaration
    /// resolves to are the same port: the schema its carrier needs, the kind a resource or a handle names, the unit
    /// only a numeric carries, the codes of a result row. Mirrors Web/src/forge/graph-schema.ts requirePortTypeContract.</summary>
    private static void RequirePortTypeContract(JsonElement port, string type, string id)
    {
        var schema = Optional(port, "schema");
        if (type is "event" or "result" or "resource" or "policy") RuntimeJson.Require(schema != null, "port-schema", id);
        if (type == "enum") RuntimeJson.Require(schema != null && EnumSets.ContainsKey(schema), "port-enum-set", id);
        var resourceKind = Optional(port, "resourceKind");
        RuntimeJson.Require(type == "resource" ? resourceKind != null && Wire.ResourceKinds.Contains(resourceKind) : resourceKind == null, "port-resource-kind", id);
        var handleKind = Optional(port, "handleKind"); var lifetime = Optional(port, "lifetime");
        RuntimeJson.Require(type == "handle"
            ? handleKind != null && Wire.HandleKinds.Contains(handleKind) && lifetime != null && Wire.HandleLifetimes.Contains(lifetime)
            : handleKind == null && lifetime == null, "port-handle-kind", id);
        if (type == "execution")
            RuntimeJson.Require(!port.TryGetProperty("unit", out _) && schema == null && !RuntimeJson.Flag(port, "nullable")
                && !port.TryGetProperty("cardinality", out _), "execution-port", id);
        if (port.TryGetProperty("unit", out _)) RuntimeJson.Require(type is "number" or "integer" or "vector3", "port-unit", id);
        if (port.TryGetProperty("codes", out var codes))
        {
            RuntimeJson.Require(type == "result", "port-codes", id);
            RuntimeJson.Require(codes.ValueKind == JsonValueKind.Array, "port-codes", id);
            var items = codes.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : null).ToArray();
            RuntimeJson.Require(items.All(c => c != null && c.Length <= DomainReasonCodeMaxLength && DomainReasonCodePattern.IsMatch(c)), "port-codes", id);
            RuntimeJson.Require(items.Distinct(StringComparer.Ordinal).Count() == items.Length, "port-codes", id);
        }
        if (type == "result")
        {
            if (IsCommonResultPort(port))
                RuntimeJson.Require(!port.TryGetProperty("fields", out _) && !port.TryGetProperty("codes", out _)
                    && !RuntimeJson.Flag(port, "nullable") && !RuntimeJson.Flag(port, "optional") && !Many(port),
                    RuntimeAbiCodes.ResultSchema, id);
            else ValidateResultFields(port, id);
        }
    }

    /// <summary>
    /// The row shape of one result schema. Rows are declared here, on the result port, and never in a plan: the row
    /// is a property of the schema (several actions reuse one schema), the four shared columns come first in a fixed
    /// order, and every later column is a value the row may carry.
    ///
    /// The `target` column is not hard-coded to `entity`. It declares one of the three world-reference kinds and the
    /// identity metadata that kind is validated by — `entityKinds` on an entity target, `resourceKind` and `schema`
    /// on a resource one, `handleKind` and `lifetime` on a handle one — and the recipient contract is what decides
    /// which of them an action may declare: <see cref="ValidateRecipientChecks"/> requires the two declarations to
    /// agree, so a row can never promise a kind its own recipient port cannot deliver. A later column carries a
    /// plain value; the world-reference keys belong to the target alone, because a second world reference in a row
    /// would be an identity with no recipient to be isomorphic with.
    /// </summary>
    private static void ValidateResultFields(JsonElement port, string id)
    {
        RuntimeJson.Require(port.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array, RuntimeAbiCodes.ResultSchema, id);
        var rows = RuntimeJson.Elements(fields);
        RuntimeJson.Require(rows.Length is >= 4 and <= MaximumResultFields, RuntimeAbiCodes.ResultSchema, id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Length; index++)
        {
            var field = rows[index];
            RuntimeJson.Shape(field, "id type", "unit schema nullable entityKinds resourceKind handleKind lifetime");
            var name = RuntimeJson.Text(field, "id");
            RuntimeJson.Require(IsName(name) && seen.Add(name), RuntimeAbiCodes.ResultSchema, id + "." + name);
            var type = RuntimeJson.Text(field, "type");
            RuntimeJson.Require(Wire.ValueTypeParameterTypes.Contains(type), RuntimeAbiCodes.ResultSchema, id + "." + name);
            RuntimeJson.Require(index == 0 || type is not ("resource" or "handle"), RuntimeAbiCodes.ResultSchema, id + "." + name);
            if (field.TryGetProperty("nullable", out var nullable))
                RuntimeJson.Require(nullable.ValueKind is JsonValueKind.True or JsonValueKind.False, RuntimeAbiCodes.ResultSchema, id + "." + name);
            if (field.TryGetProperty("unit", out var unit))
            {
                RuntimeJson.Text(unit);
                RuntimeJson.Require(type is "number" or "integer" or "vector3", RuntimeAbiCodes.ResultSchema, id + "." + name);
            }
            var schema = Optional(field, "schema");
            RuntimeJson.Require(type == "enum" ? schema != null && EnumSets.ContainsKey(schema)
                : type == "resource" ? schema != null : schema == null, RuntimeAbiCodes.ResultSchema, id + "." + name);
            ValidateResultColumnIdentity(field, type, index == 0, id + "." + name);
        }
        // target | status | committed | code: fixed ids, fixed types, fixed sets, never nullable, always first.
        var first = rows.Take(FixedResultFields.Length).Select(f => RuntimeJson.Text(f, "id")).ToArray();
        RuntimeJson.Require(first.SequenceEqual(FixedResultFields), RuntimeAbiCodes.ResultSchema, id);
        RequireField(rows[0], RuntimeJson.Text(rows[0], "type"), Optional(rows[0], "schema"), id);
        RuntimeJson.Require(Wire.RecipientTargets.Contains(RuntimeJson.Text(rows[0], "type")), RuntimeAbiCodes.ResultSchema, id + ".target");
        RequireField(rows[1], "enum", "execution_outcome", id);
        RequireField(rows[2], "enum", "commit_state", id); RequireField(rows[3], "string", null, id);
    }

    /// <summary>
    /// The identity metadata one row column declares. The target column declares exactly the keys its own kind is
    /// validated by and no others: an entity target may narrow with `entityKinds`, a resource target must name its
    /// `resourceKind` and the schema that kind's instances are authored against, and a handle target must name both
    /// halves of its contract — its kind and its lifetime. Every other column declares none of them: it is a value,
    /// and a world reference in a later column would be a second identity in a row whose identity is its recipient.
    /// </summary>
    private static void ValidateResultColumnIdentity(JsonElement field, string type, bool target, string at)
    {
        var entityKinds = field.TryGetProperty("entityKinds", out var kinds);
        var resourceKind = Optional(field, "resourceKind"); var handleKind = Optional(field, "handleKind"); var lifetime = Optional(field, "lifetime");
        if (!target)
            RuntimeJson.Require(!entityKinds && resourceKind == null && handleKind == null && lifetime == null, RuntimeAbiCodes.ResultSchema, at);
        if (type != "entity")
            RuntimeJson.Require(!entityKinds, RuntimeAbiCodes.ResultSchema, at);
        if (entityKinds)
        {
            RuntimeJson.Require(kinds.ValueKind == JsonValueKind.Array, RuntimeAbiCodes.ResultSchema, at);
            var members = kinds.EnumerateArray().Select(kind => kind.ValueKind == JsonValueKind.String ? kind.GetString() : null).ToArray();
            RuntimeJson.Require(members.Length > 0 && members.All(kind => kind != null && EntityKinds.All.Contains(kind)), RuntimeAbiCodes.ResultSchema, at);
            RuntimeJson.Require(members.Distinct(StringComparer.Ordinal).Count() == members.Length, RuntimeAbiCodes.ResultSchema, at);
        }
        RuntimeJson.Require(type == "resource" ? resourceKind != null && Wire.ResourceKinds.Contains(resourceKind) : resourceKind == null,
            RuntimeAbiCodes.ResultSchema, at);
        RuntimeJson.Require(type == "handle"
            ? handleKind != null && Wire.HandleKinds.Contains(handleKind) && lifetime != null && Wire.HandleLifetimes.Contains(lifetime)
            : handleKind == null && lifetime == null, RuntimeAbiCodes.ResultSchema, at);
    }
    private static void RequireField(JsonElement field, string type, string? schema, string id)
        => RuntimeJson.Require(RuntimeJson.Text(field, "type") == type && Optional(field, "schema") == schema && !RuntimeJson.Flag(field, "nullable"),
            RuntimeAbiCodes.ResultSchema, id + "." + RuntimeJson.Text(field, "id"));

    /// <summary>The name of the structural enum parameter that types this port, or null on a port that carries its
    /// own type. A typed port is resolved per node instance, so the concrete type is known only once a plan names a
    /// member — and is then the contract's own type, not the class this port declares.</summary>
    internal static string? ValueTypeParameter(JsonElement port)
        => Optional(port, "valueTypeParameter");

    /// <summary>The structural enum a typed port defers to, checked against the member list the port could ever
    /// resolve to: a missing, promotable or non-enum parameter is refused, and so is a member that is not a value
    /// class, because the row would otherwise promise a type the contract cannot accept. A `pure` capability has no
    /// world port in its frame at all, so no member it offers may be one either. Mirrors
    /// Web/src/forge/graph-schema.ts portTypeParameter.</summary>
    private static void ValidateTypeParameter(JsonElement port, JsonElement[] parameters, string execution, string id)
    {
        var name = ValueTypeParameter(port);
        if (name == null) return;
        var parameter = parameters.FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        var detail = id + "." + RuntimeJson.Text(port, "id") + "." + name;
        RuntimeJson.Require(parameter.ValueKind == JsonValueKind.Object && RuntimeJson.Text(parameter, "type") == "enum"
            && RuntimeJson.Text(parameter, "role") == "structural", "port-type", detail);
        var members = EnumMembers(parameter);
        RuntimeJson.Require(members.Length > 0, "port-type", detail);
        foreach (var member in members)
        {
            RuntimeJson.Require(IsValueTypeMember(member), "port-type", detail + "." + member);
            // A resource reference is a kind and an id the frame carries, resolved by its owner and never by the row, so
            // a pure row may type a port as one; an entity or a handle needs the live world.
            RuntimeJson.Require(execution != "pure" || !(PureWorldPort(member) || GuardedHandleKind(member) != null),
                RuntimeAbiCodes.PureWorldPort, detail + "." + member);
        }
    }

    /// <summary>The structural enum a port's `schemaParameter` defers to, checked against the members it could ever
    /// resolve to: every member names a shared member set, because the resolved port's schema is an identity both
    /// sides compare and a member without one would compile a port nothing could read. Mirrors
    /// Web/src/forge/graph-schema.ts portSchemaParameter.</summary>
    private static void ValidateSchemaParameter(JsonElement port, JsonElement[] parameters, string id)
    {
        var name = Optional(port, "schemaParameter");
        if (name == null) return;
        var parameter = parameters.FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        var detail = id + "." + RuntimeJson.Text(port, "id") + "." + name;
        RuntimeJson.Require(parameter.ValueKind == JsonValueKind.Object && RuntimeJson.Text(parameter, "type") == "enum"
            && RuntimeJson.Text(parameter, "role") == "structural", "port-type", detail);
        var members = EnumMembers(parameter);
        RuntimeJson.Require(members.Length > 0, "port-type", detail);
        foreach (var member in members) RuntimeJson.Require(EnumSets.ContainsKey(member), "port-type", detail + "." + member);
    }

    /// <summary>Every port a contract declares, the port-group slots included: a typed port
    /// is checked against its parameter wherever it is declared, not only on the two fixed sides.</summary>
    private static JsonElement[] DeclaredPorts(JsonElement graph)
    {
        var ports = RuntimeJson.Rows(graph, "inputs").Concat(RuntimeJson.Rows(graph, "outputs")).ToList();
        if (graph.TryGetProperty("portGroups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var group in groups.EnumerateArray())
                if (group.ValueKind == JsonValueKind.Object) ports.AddRange(RuntimeJson.Rows(group, "slots"));
        return ports.ToArray();
    }

    /// <summary>The reads a contract declares, in declaration order: empty on a contract that declares none.</summary>
    internal static string[] ReadDeclarations(JsonElement graph)
        => graph.TryGetProperty("reads", out var reads) ? RuntimeJson.Strings(reads, unique: false) : Array.Empty<string>();

    /// <summary>The closed list, checked where the contract is registered: an unknown source or a repeated one is a
    /// contract error, not a capability that quietly reads less than it says. `reads` describes an observation, so
    /// only the two evaluated tiers may declare it — an executed capability that reads the world does so through
    /// its handler, and would otherwise be able to claim an observation's budget.</summary>
    private static void ValidateReadDeclarations(JsonElement graph, string execution, string id)
    {
        var reads = ReadDeclarations(graph);
        if (reads.Length == 0 && !graph.TryGetProperty("reads", out _)) return;
        RuntimeJson.Require(reads.Length > 0, "read-declaration", id);
        foreach (var source in reads) RuntimeJson.Require(Wire.ReadSources.Contains(source), "read-source", id + "." + source);
        RuntimeJson.Require(reads.Distinct(StringComparer.Ordinal).Count() == reads.Length, "read-source", id);
        RuntimeJson.Require(execution is "pure" or "query", "read-declaration", id);
        // A pure evaluation reads no world state at all, and a declaration is a read even when no port carries one.
        RuntimeJson.Require(!(execution == "pure" && reads.Length > 0), RuntimeAbiCodes.PureWorldRead, id);
    }

    /// <summary>True for the port types that exist only in a live world: an entity, a resource reference or a
    /// handle. A selector, condition or modifier that declares one — or that declares a read — is an observation.</summary>
    internal static bool WorldPort(string type) => type is "entity" or "resource" or "handle";

    /// <summary>The port types a `pure` step may not carry: those that exist only in a live world. A resource
    /// reference is a plain value of the frame, so a pure row may pass one on.</summary>
    internal static bool PureWorldPort(string type) => type is "entity" or "handle";

    /// <summary>True for the port types a collection may carry: the value types with an element form plus a handle.
    /// A resource, an event and a result row are a reference or a row rather than a value, so none of them has a
    /// collection form — the fact itself is <see cref="RuntimeFrames.Segmented"/>, asked here by port type.</summary>
    internal static bool Segmented(string type)
        => Wire.RuntimeValueTypes.Contains(type) && RuntimeFrames.Segmented(RuntimeFrames.KindOf(type));

    /// <summary>One port of a capability graph by side (`inputs`/`outputs`) and id, for the consumers that address a
    /// declared port by name rather than by slot.</summary>
    internal static JsonElement? Port(JsonElement capability, string side, string id)
    {
        if (!capability.TryGetProperty("graph", out var graph)) return null;
        foreach (var port in RuntimeJson.Rows(graph, side))
            if (RuntimeJson.Text(port, "id") == id) return port;
        return null;
    }

    /// <summary>Forge Standard v0.2 metadata plus the capability-kind rules every registry applies.</summary>
    internal static void ValidateCapability(string kind, JsonElement graph, string id)
    {
        // The row's own id is what every check below reports; the code the check refused with travels in the
        // message as well so a registration failure names the rule and not only the row.
        try { ValidateCapabilityChecks(kind, graph, id); }
        catch (RuntimeContractException error)
        { throw new RuntimeContractException(error.Code, id + ": " + error.Message + " (" + error.Code + ")"); }
    }

    private static void ValidateCapabilityChecks(string kind, JsonElement graph, string id)
    {
        // Asked before the field-set rule, so a row that writes a permission list of its own is refused by name
        // rather than as an unknown field: permissions live on the binding, and "the graph carries a second list"
        // is the mistake the refusal should point at.
        RuntimeJson.Require(!graph.TryGetProperty("requires", out _), "row-shape",
            id + ": a row declares no `requires`; its permissions are its binding's.");
        RuntimeJson.Shape(graph, "domains execution inputs outputs parameters", "reads recipients portGroups factIdentity subject");
        var domains = RuntimeJson.Strings(graph.GetProperty("domains"));
        RuntimeJson.Require(domains.Length > 0 && domains.All(Wire.Domains.Contains), "graph-domain", id);
        var execution = RuntimeJson.Text(graph, "execution");
        RuntimeJson.Require(Wire.ExecutionTiers.Contains(execution), "graph-authority", id);
        // A fact identity is a fact about the event a trigger publishes, so only a trigger may declare one and
        // only a member of the closed list is one. It is what a `shot`-scoped gate books against.
        if (graph.TryGetProperty("factIdentity", out var identity))
        {
            RuntimeJson.Require(kind == "trigger", "fact-identity-owner", id);
            RuntimeJson.Require(RuntimeJson.Text(identity) == ShotFactIdentity, "fact-identity", id);
        }
        var inputs = RuntimeJson.Rows(graph, "inputs"); var outputs = RuntimeJson.Rows(graph, "outputs");
        foreach (var ports in new[] { inputs, outputs })
        {
            foreach (var port in ports) ValidatePort(port, id);
            RuntimeJson.Require(ports.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == ports.Length, "duplicate-port", id);
        }
        RuntimeJson.Require(!outputs.Any(IsCommonResultPort), RuntimeAbiCodes.ResultSchema, id);
        if (graph.TryGetProperty("subject", out var subject))
        {
            RuntimeJson.Require(kind == "trigger", "subject-contract", id);
            RuntimeJson.Require(subject.ValueKind == JsonValueKind.String && IsName(subject.GetString()!)
                && subject.GetString() == subject.GetString()!.Trim(),
                "subject-contract", id);
            var port = SubjectPort(graph);
            // An end_on carries no trigger parameters. Its subject must be a concrete, existing entity output,
            // not a generated name or a port whose type/kinds need another node's inputs or author parameters.
            RuntimeJson.Require(port.ValueKind == JsonValueKind.Object && RuntimeJson.Text(port, "type") == "entity"
                && !port.TryGetProperty("valueTypeParameter", out _)
                && !port.TryGetProperty("schemaParameter", out _)
                && !port.TryGetProperty("entityKindsFrom", out _)
                && !port.TryGetProperty("entityKindsFromParameter", out _), "subject-contract", id);
        }
        // A context role may only be left unwired where the row's own contract says so (rule 146.4g makes
        // `combat.damage.source` optional: environmental damage has no dealer, and leaving the port out is the
        // author's own statement of that). Nullable stays refused on every row — a wire that may hand the row
        // nobody is ambiguous, and nothing may fill a context role in implicitly. Which ports are roles, and that
        // one rule about them, both live in the role table: no implicit source/owner fallback, one criterion.
        foreach (var port in inputs.Where(p => RuntimeActorRoles.IsRolePort(RuntimeJson.Text(p, "id"))))
            RuntimeActorRoles.RequireRolePort(port, id);
        if (graph.TryGetProperty("recipients", out var recipients)) ValidateRecipients(recipients, inputs, outputs, id);
        RuntimeEntityKindFlow.Validate(graph, id);
        var parameters = RuntimeJson.Rows(graph, "parameters");
        RuntimeJson.Require(parameters.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == parameters.Length, "duplicate-parameter", id);
        foreach (var parameter in parameters) ValidateParameter(parameter, inputs, id);
        // A typed port hands its concrete type rules to the member it resolves to, so what is checked here is the
        // parameter itself and every member it offers — a variable template and a port-group slot included.
        foreach (var port in DeclaredPorts(graph))
        {
            ValidateTypeParameter(port, parameters, execution, id);
            ValidateSchemaParameter(port, parameters, id);
            ValidatePortCondition(graph, port, parameters, id);
        }
        ValidatePortGroups(graph, id);
        ValidateReadDeclarations(graph, execution, id);
        // I-CATALOG: an observation that touches the world is a `query`; a value-only observation stays `pure`
        // and is refused the moment it declares an entity, resource or handle port, a world-typed variable port, or
        // any read at all. Executable kinds are never either tier — they run on the host and are dispatched, not
        // evaluated on demand. A `pure` capability that reads is refused as `pure-world-port` before the tier rule
        // runs: that is the more specific fact.
        var worldPorts = DeclaredPorts(graph).Any(p => WorldPort(RuntimeJson.Text(p, "type")));
        if (execution == "pure")
        {
            RuntimeJson.Require(!inputs.Concat(outputs).Any(p => RuntimeJson.Text(p, "type") == "execution"), "pure-execution", id);
            RuntimeJson.Require(!worldPorts, RuntimeAbiCodes.PureWorldPort, id);
        }
        // The evaluated families. `state` is the value row's own kind: a read-only answer about the world that is
        // asked for on demand and never published, which is the same authority rule the three older families
        // follow — a row that names a world port or declares a read is a query, the rest are pure.
        if (kind is "selector" or "condition" or "modifier" or "state")
            RuntimeJson.Require(execution == (worldPorts || ReadDeclarations(graph).Length > 0 ? "query" : "pure"), RuntimeAbiCodes.QueryAuthority, id);
        if (kind is "trigger" or "action" or "control") RuntimeJson.Require(execution is "host" or "owner" or "presentation", "executable-authority", id);
        // An owner step runs on the holder's machine; a handle it minted would live on the holder's kernel where the
        // plan that dispatched the step cannot cancel it.
        if (execution == "owner")
            RuntimeJson.Require(!outputs.Any(p => RuntimeJson.Text(p, "type") == "handle") && !(recipients.ValueKind == JsonValueKind.Object && recipients.TryGetProperty("handle", out _)),
                "executable-authority", id);
        if (kind == "trigger") RuntimeJson.Require(!inputs.Any(p => RuntimeJson.Text(p, "type") == "execution"), "trigger-input", id);
        RuntimeJson.Require(kind == "action" || recipients.ValueKind == JsonValueKind.Undefined, "recipient-owner", id);
    }

    /// <summary>The trigger's explicitly declared subject output. Absence means it cannot be an end_on, never
    /// that its source, first entity output or only entity output should be substituted. Registration validates the
    /// reference before this metadata is resolved into the load-time descriptor.</summary>
    internal static JsonElement SubjectPort(JsonElement graph)
    {
        if (graph.ValueKind != JsonValueKind.Object || !graph.TryGetProperty("subject", out var subject)
            || subject.ValueKind != JsonValueKind.String) return default;
        var name = subject.GetString();
        foreach (var port in RuntimeJson.Rows(graph, "outputs"))
            if (RuntimeJson.Text(port, "id") == name) return port;
        return default;
    }
    private static void ValidateRecipients(JsonElement spec, JsonElement[] inputs, JsonElement[] outputs, string id)
    {
        // The row's own id is what every check above reports, which is not enough to tell `recipient-port` from
        // `recipient-cardinality` when a row fails to register; the code the check refused with travels in the
        // message as well. The checks themselves are unchanged.
        try { ValidateRecipientChecks(spec, inputs, outputs, id); }
        catch (RuntimeContractException error)
        { throw new RuntimeContractException(error.Code, error.Message + " (" + error.Code + ")"); }
    }

    private static void ValidateRecipientChecks(JsonElement spec, JsonElement[] inputs, JsonElement[] outputs, string id)
    {
        RuntimeJson.Shape(spec, "input target cardinality requires result", "handle");
        var target = RuntimeJson.Text(spec, "target"); var cardinality = RuntimeJson.Text(spec, "cardinality");
        RuntimeJson.Require(Wire.RecipientTargets.Contains(target), "recipient-target", id);
        RuntimeJson.Require(Wire.Cardinalities.Contains(cardinality), "recipient-cardinality", id);
        var inputName = RuntimeJson.Text(spec, "input");
        var input = inputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == inputName);
        // An optional recipient port is the row's statement that the recipients do not decide what the action
        // reaches; left unwired, the handler runs with no recipient and writes no row. A nullable one stays refused.
        RuntimeJson.Require(input.ValueKind == JsonValueKind.Object && RuntimeJson.Text(input, "type") == target
            && !RuntimeJson.Flag(input, "nullable"), "recipient-port", id);
        RuntimeJson.Require(Cardinality(input) == cardinality, "recipient-cardinality", id);
        var requirements = RuntimeJson.Strings(spec.GetProperty("requires"));
        RuntimeJson.Require(requirements.Length <= 128 && requirements.All(RuntimeJson.IsId), "recipient-requirements", id);
        var resultName = RuntimeJson.Text(spec, "result");
        var result = outputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == resultName);
        RuntimeJson.Require(result.ValueKind == JsonValueKind.Object && RuntimeJson.Text(result, "type") == "result"
            && !RuntimeJson.Flag(result, "optional") && !RuntimeJson.Flag(result, "nullable"), "recipient-result", id);
        RequireResultTarget(result, input, target, id);
        if (!spec.TryGetProperty("handle", out _)) return;
        var handleName = RuntimeJson.Text(spec, "handle");
        RuntimeJson.Require(outputs.Any(p => RuntimeJson.Text(p, "id") == handleName && RuntimeJson.Text(p, "type") == "handle"), "recipient-handle", id);
    }

    /// <summary>
    /// The result row's `target` column against the recipient the action declared: the two are one identity, so a
    /// row can neither promise a kind its recipient port cannot deliver nor restate the recipient's constraints
    /// differently. This is what replaced the fixed `target: entity` assertion — the descriptor a plan compiles
    /// copies the recipient input's scalar descriptor, and a registration that disagrees with it is refused here.
    ///
    /// The column declares the recipient kind and copies exactly the metadata that kind is validated by:
    /// `entityKinds` on an entity, `resourceKind` + `schema` on a resource, `handleKind` + `lifetime` on a handle.
    /// There is no metadata-absent tolerance — a bare `entity` column paired with a resource or handle recipient
    /// is refused, because accepting it would let the declared identity be restated as another kind. Every
    /// producer writes the copied column (<c>Tools/Forge/graph-schema.ts::resultTargetField</c> is the one
    /// derivation), so a registration that still declares a bare column is the disagreement this refuses.
    /// </summary>
    private static void RequireResultTarget(JsonElement result, JsonElement input, string target, string id)
    {
        var column = ResultFields(result)[0];
        RuntimeJson.Require(RuntimeJson.Text(column, "type") == target, RuntimeAbiCodes.ResultSchema, id + ".result.target");
        RuntimeJson.Require(DeclaredEntityKinds(column).SequenceEqual(DeclaredEntityKinds(input)), RuntimeAbiCodes.ResultSchema, id + ".result.target");
        RuntimeJson.Require(ResourceKindIndex(column) == ResourceKindIndex(input), RuntimeAbiCodes.ResultSchema, id + ".result.target");
        RuntimeJson.Require(OptionalText(column, "schema") == OptionalText(input, "schema"), RuntimeAbiCodes.ResultSchema, id + ".result.target");
        RuntimeJson.Require(HandleKindIndex(column) == HandleKindIndex(input), RuntimeAbiCodes.ResultSchema, id + ".result.target");
        RuntimeJson.Require(LifetimeIndex(column) == LifetimeIndex(input), RuntimeAbiCodes.ResultSchema, id + ".result.target");
    }
    private static void ValidateParameter(JsonElement parameter, JsonElement[] inputs, string id)
    {
        // The same diagnostic rule the recipient contract follows: the row's own id is not enough to tell which
        // check refused a parameter, so the code it refused with travels in the message as well.
        try { ValidateParameterChecks(parameter, inputs, id); }
        catch (RuntimeContractException error)
        { throw new RuntimeContractException(error.Code, error.Message + " (" + error.Code + ")"); }
    }

    private static void ValidateParameterChecks(JsonElement parameter, JsonElement[] inputs, string id)
    {
        RuntimeJson.Shape(parameter, "id type role required", "minimum maximum values set unit resourceKind");
        var name = RuntimeJson.Text(parameter, "id"); var type = RuntimeJson.Text(parameter, "type"); var role = RuntimeJson.Text(parameter, "role");
        RuntimeJson.Require(IsName(name), "parameter-name", id);
        RuntimeJson.Require(Wire.ParameterTypes.Contains(type), "parameter-type", id);
        RuntimeJson.Require(role is "value" or "structural", "parameter-role", id);
        // A resource parameter is the one compile-time reference a plan may write into a step's constant frame, so
        // it names the kind that reference is read through: the frame encoder indexes the kind table by it and the
        // value validator checks the reference against it, so a reference that named no kind could not compile at
        // all. A parameter of any other type names no kind.
        var resourceKind = Optional(parameter, "resourceKind");
        RuntimeJson.Require(type == "resource" ? resourceKind != null && Wire.ResourceKinds.Contains(resourceKind) : resourceKind == null,
            "parameter-resource-kind", id);
        RuntimeJson.Require(parameter.GetProperty("required").ValueKind is JsonValueKind.True or JsonValueKind.False, "parameter-required", id);
        foreach (var bound in new[] { "minimum", "maximum" }) if (parameter.TryGetProperty(bound, out var value))
        {
            RuntimeJson.Require(type is "number" or "integer", "parameter-bound-type", id);
            RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number), "parameter-bound", id);
            if (type == "integer") RuntimeJson.Integer(value, -RuntimeJson.MaxSafeInteger);
        }
        if (parameter.TryGetProperty("minimum", out var minimum) && parameter.TryGetProperty("maximum", out var maximum))
            RuntimeJson.Require(minimum.GetDouble() <= maximum.GetDouble(), "parameter-bounds", id);
        var hasValues = parameter.TryGetProperty("values", out var values); var set = Optional(parameter, "set");
        if (type != "enum") RuntimeJson.Require(!hasValues && set == null, "parameter-values", id);
        else
        {
            // A promotable enum names a whole shared set (a promoted port carries the set, not a subset).
            // A structural enum inlines its members, names a whole set, or narrows one to a proper subset in set order.
            RuntimeJson.Require(set == null ? role == "structural" : EnumSets.ContainsKey(set), "parameter-set", id);
            RuntimeJson.Require(!hasValues || role == "structural", "parameter-set", id);
            if (hasValues)
            {
                var members = RuntimeJson.Strings(values);
                // A member may carry one `:` segment for the members whose value class is two words: a guarded
                // handle writes its kind after the class (`handle:effect`, ruling 158.4). Mirrors Web/src/forge/
                // graph-schema.ts MEMBER, which allows the same single segment and upper-case native member names
                // (`ZONE_0`).
                RuntimeJson.Require(members.Length > 0 && members.Distinct(StringComparer.Ordinal).Count() == members.Length
                    && members.All(m => Regex.IsMatch(m, @"^[A-Za-z][A-Za-z0-9_-]*(?::[a-z][a-z0-9_-]*){0,2}$", RegexOptions.CultureInvariant)), "enum-values", id);
                if (set != null)
                {
                    var positions = members.Select(m => Array.IndexOf(EnumSets[set], m)).ToArray();
                    RuntimeJson.Require(positions.All(p => p >= 0) && positions.Zip(positions.Skip(1)).All(p => p.First < p.Second)
                        && positions.Length < EnumSets[set].Length, "enum-values", id);
                }
            }
        }
        if (parameter.TryGetProperty("unit", out var unit))
        { RuntimeJson.Text(unit); RuntimeJson.Require(type is "integer" or "number" or "vector3", "parameter-unit", id); }
        // A promoted value parameter becomes an input port with the same id.
        if (role == "value") RuntimeJson.Require(!inputs.Any(p => RuntimeJson.Text(p, "id") == name), "parameter-collision", id);
    }
    /// <summary>The count of a repeated tuple; one declared tuple is already a complete node.</summary>
    private static (long Minimum, long Maximum) CountParameter(JsonElement graph, string name, string id)
    {
        var count = RuntimeJson.Rows(graph, "parameters").FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        RuntimeJson.Require(count.ValueKind == JsonValueKind.Object && RuntimeJson.Text(count, "type") == "integer"
            && RuntimeJson.Text(count, "role") == "structural" && !RuntimeJson.Flag(count, "required"), "variadic-count-parameter", id);
        RuntimeJson.Require(count.TryGetProperty("minimum", out _) && count.TryGetProperty("maximum", out _), "variadic-count-bounds", id);
        var lower = RuntimeJson.Integer(count.GetProperty("minimum"), 1, MaximumVariadicPorts - 1);
        return (lower, RuntimeJson.Integer(count.GetProperty("maximum"), lower + 1, MaximumVariadicPorts));
    }
    private static void CheckTemplate(JsonElement template, string executionAuthority, string id)
    {
        ValidatePort(template, id);
        RuntimeJson.Require(!RuntimeJson.Flag(template, "optional") && !RuntimeJson.Flag(template, "nullable"), "variadic-template", id);
        RuntimeJson.Require(executionAuthority != "pure" || RuntimeJson.Text(template, "type") != "execution", "pure-execution", id);
    }
    private static void CheckAdded(HashSet<string> ids, string added, string id)
    {
        RuntimeJson.Require(added.Length <= 256, "variadic-port-name-budget", id);
        RuntimeJson.Require(!ids.Contains(added), "variadic-port-conflict", id);
    }
    private static string PortId(string template, long index) => template + "_" + index.ToString(CultureInfo.InvariantCulture);
    /// <summary>The id of the first port of a group: the first declared base port, or the first slot's own numbered id when
    /// the group declares none. Every reader of a group's layout starts from this one answer.</summary>
    internal static string GroupFirstPortId(JsonElement group, JsonElement[] slots)
        => group.TryGetProperty("basePortIds", out var baseIds) ? RuntimeJson.Text(baseIds[0]) : PortId(RuntimeJson.Text(slots[0], "id"), 1);
    /// <summary>A typed tuple repeated as a block; the whole block exists at minimum and is declared, not implied.</summary>
    private static void ValidatePortGroups(JsonElement graph, string id)
    {
        if (!graph.TryGetProperty("portGroups", out _)) return;
        var groups = RuntimeJson.Rows(graph, "portGroups", 2);
        RuntimeJson.Require(groups.Length > 0, "port-groups", id);
        var execution = RuntimeJson.Text(graph, "execution");
        var sides = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in groups)
        {
            RuntimeJson.Shape(spec, "id side parameter minimum maximum slots", "basePortIds");
            RuntimeJson.Require(IsName(RuntimeJson.Text(spec, "id")) && names.Add(RuntimeJson.Text(spec, "id")), "port-group-name", id);
            var side = RuntimeJson.Text(spec, "side");
            RuntimeJson.Require(side is "inputs" or "outputs", "variadic-side", id);
            RuntimeJson.Require(sides.Add(side), "port-group-side", id);
            var slots = RuntimeJson.Rows(spec, "slots", 8);
            RuntimeJson.Require(slots.Length > 0 && slots.Select(s => RuntimeJson.Text(s, "id")).Distinct(StringComparer.Ordinal).Count() == slots.Length, "port-group-slots", id);
            foreach (var slot in slots) CheckTemplate(slot, execution, id);
            var (lower, upper) = CountParameter(graph, RuntimeJson.Text(spec, "parameter"), id);
            // The group's `minimum` is the declared base block and the count a plan gets when it names none; the count
            // parameter may reach lower, and a smaller count drops the last base tuples (Resolve).
            var baseCount = RuntimeJson.Integer(spec.GetProperty("minimum"), 0, MaximumVariadicPorts);
            RuntimeJson.Require(baseCount >= lower && baseCount < upper
                && RuntimeJson.Integer(spec.GetProperty("maximum"), 0, MaximumVariadicPorts) == upper, "variadic-count-bounds", id);
            var baseIds = spec.TryGetProperty("basePortIds", out var declared) ? RuntimeJson.Strings(declared) : null;
            RuntimeJson.Require(baseIds == null || baseIds.Length == baseCount * slots.Length && baseIds.All(IsName), "port-group-base", id);
            var ports = RuntimeJson.Rows(graph, side);
            var start = Array.FindIndex(ports, p => RuntimeJson.Text(p, "id") == GroupFirstPortId(spec, slots));
            RuntimeJson.Require(start >= 0 && start + baseCount * slots.Length <= ports.Length, "port-group-base", id);
            for (var i = 1; i <= baseCount; i++)
                for (var j = 0; j < slots.Length; j++)
                {
                    var port = ports[start + (i - 1) * slots.Length + j];
                    RuntimeJson.Require(RuntimeJson.Text(port, "id") == (baseIds?[(int)(i - 1) * slots.Length + j] ?? PortId(RuntimeJson.Text(slots[j], "id"), i)), "port-group-base", id);
                    RuntimeJson.Require(SameValue(port, slots[j]) && !RuntimeJson.Flag(port, "optional") && !RuntimeJson.Flag(port, "nullable"), "variadic-port-contract", id);
                }
            var ids = ports.Select(p => RuntimeJson.Text(p, "id")).ToHashSet(StringComparer.Ordinal);
            for (var i = baseCount + 1; i <= upper; i++)
                foreach (var slot in slots) CheckAdded(ids, PortId(RuntimeJson.Text(slot, "id"), i), id);
        }
    }

    /// <summary>A conditional port is selected by one declared structural enum member. A value input, a missing
    /// parameter or a member outside the enum cannot change a node's port shape.</summary>
    private static void ValidatePortCondition(JsonElement graph, JsonElement port, JsonElement[] parameters, string id)
    {
        if (!port.TryGetProperty("when", out var when)) return;
        RuntimeJson.Require(RuntimeJson.Text(graph, "execution") is "pure" or "query"
            && (!graph.TryGetProperty("portGroups", out var groups) || groups.ValueKind == JsonValueKind.Array && groups.GetArrayLength() == 0),
            "port-condition", id);
        var parameter = parameters.FirstOrDefault(value => RuntimeJson.Text(value, "id") == RuntimeJson.Text(when, "parameter"));
        RuntimeJson.Require(parameter.ValueKind == JsonValueKind.Object && RuntimeJson.Text(parameter, "type") == "enum"
            && RuntimeJson.Text(parameter, "role") == "structural" && RuntimeJson.Flag(parameter, "required"), "port-condition", id);
        RuntimeJson.Require(EnumMembers(parameter).Contains(RuntimeJson.Text(when, "value"), StringComparer.Ordinal),
            "port-condition", id);
    }

    private static bool PortConditionEnabled(JsonElement graph, JsonElement port, JsonElement parameters)
    {
        if (!port.TryGetProperty("when", out var when)) return true;
        var name = RuntimeJson.Text(when, "parameter");
        var definition = RuntimeJson.Rows(graph, "parameters").FirstOrDefault(value => RuntimeJson.Text(value, "id") == name);
        RuntimeJson.Require(definition.ValueKind == JsonValueKind.Object && RuntimeJson.Text(definition, "type") == "enum"
            && RuntimeJson.Text(definition, "role") == "structural" && RuntimeJson.Flag(definition, "required"), "port-condition", name);
        RuntimeJson.Require(EnumMembers(definition).Contains(RuntimeJson.Text(when, "value"), StringComparer.Ordinal),
            "port-condition", name);
        RuntimeJson.Require(parameters.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null,
            "missing-constant", name);
        var member = EnumMemberName(definition, value);
        RuntimeJson.Require(EnumMembers(definition).Contains(member, StringComparer.Ordinal), "invalid-enum", name);
        return member == RuntimeJson.Text(when, "value");
    }

    private static JsonElement WithoutPortCondition(JsonElement port)
        => !port.TryGetProperty("when", out _) ? port
            : RuntimeJson.From(port.EnumerateObject().Where(field => field.Name != "when")
                .ToDictionary(field => field.Name, field => field.Value.Clone(), StringComparer.Ordinal));

    /// <summary>Expands port groups, appends promoted value inputs, then resolves conditional
    /// ports. The condition is authoring metadata: a resolved contract and plan never carry it.</summary>
    internal static JsonElement Resolve(JsonElement graph, JsonElement parameters, IReadOnlySet<string>? promoted = null)
    {
        var resolved = graph.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        long Size(string name, long fallback)
        {
            var definition = RuntimeJson.Rows(graph, "parameters").Single(p => RuntimeJson.Text(p, "id") == name);
            return parameters.TryGetProperty(name, out var value)
                ? RuntimeJson.Integer(value, RuntimeJson.Integer(definition.GetProperty("minimum")), RuntimeJson.Integer(definition.GetProperty("maximum"))) : fallback;
        }
        JsonElement Port(JsonElement template, long index)
        {
            var fields = template.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            fields["id"] = RuntimeJson.From(PortId(RuntimeJson.Text(template, "id"), index));
            return RuntimeJson.From(fields);
        }
        if (graph.TryGetProperty("portGroups", out var groups))
            foreach (var group in groups.EnumerateArray())
            {
                var side = RuntimeJson.Text(group, "side"); var slots = RuntimeJson.Rows(group, "slots");
                var lower = RuntimeJson.Integer(group.GetProperty("minimum"));
                var count = Size(RuntimeJson.Text(group, "parameter"), lower);
                var ports = resolved[side].EnumerateArray().ToList();
                var first = GroupFirstPortId(group, slots);
                var start = ports.FindIndex(p => RuntimeJson.Text(p, "id") == first);
                // A count below the declared base block drops its last tuples; above it, numbered tuples follow it.
                if (count < lower) ports.RemoveRange(start + (int)count * slots.Length, (int)(lower - count) * slots.Length);
                else
                {
                    var added = new List<JsonElement>();
                    for (var i = lower + 1; i <= count; i++) added.AddRange(slots.Select(slot => Port(slot, i)));
                    ports.InsertRange(start + (int)lower * slots.Length, added);
                }
                resolved[side] = RuntimeJson.From(ports);
            }
        if (promoted is { Count: > 0 })
        {
            var definitions = RuntimeJson.Rows(graph, "parameters");
            // Declaration order, not authoring order: the compiled frame must not depend on click order.
            var moved = definitions.Where(p => promoted.Contains(RuntimeJson.Text(p, "id"))).ToArray();
            var names = string.Join(" ", moved.Select(p => RuntimeJson.Text(p, "id")));
            RuntimeJson.Require(moved.Length == promoted.Count && moved.All(p => RuntimeJson.Text(p, "role") == "value"), "promotion-role", names);
            var inputs = resolved["inputs"].EnumerateArray().Concat(moved.Select(PromotedPort)).ToArray();
            RuntimeJson.Require(inputs.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == inputs.Length, "promotion-collision", names);
            resolved["inputs"] = RuntimeJson.From(inputs);
            resolved["parameters"] = RuntimeJson.From(definitions.Where(p => !promoted.Contains(RuntimeJson.Text(p, "id"))).ToArray());
        }
        // Typed ports come last, on both sides and in declaration order: every port a node instance declares is
        // already expanded by then, so the contract a plan is compiled against carries concrete types throughout.
        foreach (var side in new[] { "inputs", "outputs" })
            resolved[side] = RuntimeJson.From(resolved[side].EnumerateArray()
                .Where(port => PortConditionEnabled(graph, port, parameters))
                .Select(port => TypedPort(graph, WithoutPortCondition(port), parameters)).ToArray());
        return RuntimeJson.From(resolved);
    }
    private static JsonElement TypedPort(JsonElement graph, JsonElement port, JsonElement parameters)
    {
        port = ValueTypePort(graph, port, parameters);
        return KindParameterPort(SchemaTypePort(graph, port, parameters), parameters);
    }

    /// <summary>The kind dimension of a port an address parameter types. A written address keeps the deferral: its
    /// category names the kind, and that category is the provider's grammar, so the port stays deferred rather than
    /// being given a kind this assembly would have to invent (<see cref="EntityKindsNarrow"/>). An address nobody
    /// wrote names nothing, so the deferral is dropped and the port is unbound — every kind — which is what refuses
    /// its wire into a consumer that takes one kind only. Mirrors Web/src/forge/graph-schema.ts
    /// entityKindParameterPort, which resolves the written case to the category's kind itself.</summary>
    private static JsonElement KindParameterPort(JsonElement port, JsonElement parameters)
    {
        var name = Optional(port, "entityKindsFromParameter");
        if (name == null || (parameters.TryGetProperty(name, out var written) && written.ValueKind != JsonValueKind.Null)) return port;
        var fields = port.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        fields.Remove("entityKindsFromParameter");
        return RuntimeJson.From(fields);
    }

    /// <summary>The same resolution for the schema dimension: the member the author chose becomes the port's
    /// concrete enum set, and the port's own type rules then apply to it exactly as if the set had been written on
    /// the row. This is where a row that covers every shared set becomes a contract naming one, long before a plan
    /// exists. Mirrors Web/src/forge/graph-schema.ts schemaTypePort.</summary>
    private static JsonElement SchemaTypePort(JsonElement graph, JsonElement port, JsonElement parameters)
    {
        var name = Optional(port, "schemaParameter");
        if (name == null) return port;
        var detail = RuntimeJson.Text(port, "id") + "." + name;
        var parameter = RuntimeJson.Rows(graph, "parameters").FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        RuntimeJson.Require(parameter.ValueKind == JsonValueKind.Object && RuntimeJson.Text(parameter, "type") == "enum"
            && RuntimeJson.Text(parameter, "role") == "structural", "port-type", detail);
        var fields = port.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        // A port whose type another parameter chose carries a member set only when that type is an enum: the same
        // port typed as a number or an entity has no set, and the set parameter is not read.
        if (RuntimeJson.Text(port, "type") != "enum")
        {
            fields.Remove("schemaParameter");
            return RuntimeJson.From(fields);
        }
        var written = parameters.TryGetProperty(name, out var given);
        RuntimeJson.Require(written || !RuntimeJson.Flag(parameter, "required"), "port-type", detail);
        var member = EnumMember(parameter, written ? given : RuntimeJson.From(EnumMembers(parameter)[0]));
        RuntimeJson.Require(EnumSets.ContainsKey(member), "port-type", detail + "." + member);
        fields["schema"] = RuntimeJson.From(member);
        var resolved = RuntimeJson.From(fields);
        RequirePortTypeContract(resolved, RuntimeJson.Text(resolved, "type"), RuntimeJson.Text(port, "id"));
        return resolved;
    }

    /// <summary>The port a typed contract resolves to: the class the port declares is replaced by the member its
    /// structural parameter names, and that member's own type is then the port's — its metadata rules included, so a
    /// member the declared metadata cannot carry is refused here rather than promised by the row. An unknown member, a
    /// member the parameter does not offer and a required parameter left unwritten are all the same authoring mistake.
    /// An optional parameter the author left unwritten is not: it resolves to the first member of its own list, which
    /// is the member the declaration order names as the default — that is what lets a glue row like `present` be
    /// dropped on a wire without a second answer to a question the wire already answered. Mirrors
    /// Web/src/forge/graph-schema.ts valueTypePort.</summary>
    private static JsonElement ValueTypePort(JsonElement graph, JsonElement port, JsonElement parameters)
    {
        var name = ValueTypeParameter(port);
        if (name == null) return port;
        var detail = RuntimeJson.Text(port, "id") + "." + name;
        var parameter = RuntimeJson.Rows(graph, "parameters").FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        RuntimeJson.Require(parameter.ValueKind == JsonValueKind.Object && RuntimeJson.Text(parameter, "type") == "enum"
            && RuntimeJson.Text(parameter, "role") == "structural", "port-type", detail);
        var written = parameters.TryGetProperty(name, out var given);
        RuntimeJson.Require(written || !RuntimeJson.Flag(parameter, "required"), "port-type", detail);
        var member = EnumMember(parameter, written ? given : RuntimeJson.From(EnumMembers(parameter)[0]));
        RuntimeJson.Require(IsValueTypeMember(member) && EnumMembers(parameter).Contains(member, StringComparer.Ordinal),
            "port-type", detail + "." + member);
        var handleKind = GuardedHandleKind(member);
        var resourceKind = GuardedResourceKind(member);
        var type = handleKind != null ? "handle" : resourceKind != null ? "resource" : member;
        var fields = port.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        fields["type"] = RuntimeJson.From(type);
        if (handleKind != null)
        {
            // Both halves of the handle's contract travel with the resolved port, or the port would name a kind
            // the value it carries does not have.
            fields["handleKind"] = RuntimeJson.From(handleKind);
            fields["lifetime"] = RuntimeJson.From(Wire.GuardedHandleLifetimes[member.Substring("handle:".Length)]);
        }
        if (resourceKind != null)
        {
            fields["resourceKind"] = RuntimeJson.From(resourceKind);
            fields["schema"] = RuntimeJson.From("forge.resource." + resourceKind);
        }
        // A typed port that resolves to an entity, a handle or a named resource may be absent: these are world
        // identities a read publishes as "not there yet" (ruling 158.5), which is what a when-present step guards.
        // A pure row only passes a value on (a listed resource, say), so what it hands out is always there.
        if ((handleKind != null || resourceKind != null || member == "entity") && RuntimeJson.Text(graph, "execution") != "pure") fields["nullable"] = RuntimeJson.From(true);
        // The member is the port's class, so the kind flow a declared entity port carried goes with that class:
        // a port resolved away from `entity` that kept `entityKindsFrom` would name a kind it cannot carry, and
        // the load-time flow check refuses exactly that. Mirrors Web/src/forge/graph-schema.ts valueTypePort.
        if (type != "entity") { fields.Remove("entityKindsFrom"); fields.Remove("entityKindsFromParameter"); }
        var resolved = RuntimeJson.From(fields);
        // An option value takes its member set from the port's schema parameter, which SchemaTypePort resolves next
        // and checks there; until then the port has no set of its own to check.
        if (!(type == "enum" && Optional(port, "schemaParameter") != null))
            RequirePortTypeContract(resolved, type, RuntimeJson.Text(port, "id"));
        return resolved;
    }

    /// <summary>One enum parameter value in either of the two spellings it has on the wire: the member name an
    /// authored call passes, and the member-set index a compiled plan carries in `layout.constants`. The index basis
    /// is the parameter's own member list — its inline `values` when it narrows, its named set otherwise — which is
    /// the basis the frame encoder validates the same constant against and the website compiles it with, so a
    /// narrowed parameter can never be read through the whole set it came from.
    /// Mirrors Web/src/forge/graph-schema.ts enumMembersOf.</summary>
    private static string EnumMember(JsonElement parameter, JsonElement value)
    {
        var id = RuntimeJson.Text(parameter, "id");
        if (value.ValueKind == JsonValueKind.String) return RuntimeJson.Text(value);
        var members = EnumMembers(parameter);
        var index = -1d;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out index)
            && double.IsFinite(index) && index == Math.Truncate(index) && index >= 0 && index < members.Length,
            "invalid-enum", id);
        return members[(int)index];
    }
    /// <summary>The member name one compiled enum constant names, in either wire spelling. The loader carries a
    /// trigger's structural enum members on the entry so the provider that observes the trigger can read the exact
    /// subscription set the loaded plans asked for (`forge.trigger.input.key`'s `key`), instead of polling every
    /// member of the set.</summary>
    internal static string EnumMemberName(JsonElement parameter, JsonElement value) => EnumMember(parameter, value);
    /// <summary>A literal moved onto an input keeps its value contract; bounds and members are rechecked per dispatch.</summary>
    private static JsonElement PromotedPort(JsonElement parameter)
    {
        var type = RuntimeJson.Text(parameter, "type");
        var port = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["id"] = parameter.GetProperty("id").Clone(),
            ["type"] = RuntimeJson.From(type == "recipient-policy" ? "policy" : type)
        };
        if (type == "recipient-policy") port["schema"] = RuntimeJson.From("forge.policy.recipient");
        else if (Optional(parameter, "set") is { } set) port["schema"] = RuntimeJson.From(set);
        if (parameter.TryGetProperty("unit", out var unit)) port["unit"] = unit.Clone();
        if (!RuntimeJson.Flag(parameter, "required")) port["optional"] = RuntimeJson.From(true);
        return RuntimeJson.From(port);
    }
    /// <summary>Dense slot frame of one resolved side, identical to the website's compiledNodeLayout.</summary>
    internal static JsonElement Layout(JsonElement resolved, string side)
        => RuntimeJson.From(RuntimeJson.Rows(resolved, side).Select((port, index) => new
        {
            index,
            type = Array.IndexOf(Wire.PortTypes, RuntimeJson.Text(port, "type")),
            cardinality = Array.IndexOf(Wire.Cardinalities, Cardinality(port)),
            valueSet = ValueSet(port),
            lifetime = Optional(port, "lifetime") is { } lifetime ? Array.IndexOf(Wire.HandleLifetimes, lifetime) : -1,
            optional = RuntimeJson.Flag(port, "optional"),
            nullable = RuntimeJson.Flag(port, "nullable")
        }).ToArray());
    private static int ValueSet(JsonElement port) => RuntimeJson.Text(port, "type") switch
    {
        // `FindIndex` answers 0 for a member that is not there, which is a valid index: a name outside the table
        // has to read as absent, or the plan's own layout and the re-derived one would disagree on an index both
        // sides called "no set".
        "enum" => Math.Max(-1, Array.FindIndex(Wire.EnumSets, set => set.Name == Optional(port, "schema"))),
        "resource" => Array.IndexOf(Wire.ResourceKinds, Optional(port, "resourceKind")),
        "handle" => Array.IndexOf(Wire.HandleKinds, Optional(port, "handleKind")),
        _ => -1
    };
}

/// <summary>The shared enum vocabulary as a package reads it: the names of the member sets the runtime knows, in the
/// declaration order that is every set's compiled index basis. A row that covers all of them at once — the glue
/// rows whose `enum_set` parameter chooses one set per node instance — has to spell that list in its own contract,
/// and spelling it a second time in a package is how the two copies drift.</summary>
internal static class RuntimeEnumSets
{
    public static IReadOnlyList<string> Names => RuntimeGraphContracts.EnumSetNames;
    /// <summary>One set's members, in the order its compiled values index.</summary>
    public static IReadOnlyList<string> Members(string set)
        => RuntimeGraphContracts.EnumSets.TryGetValue(set, out var members)
            ? members : throw new RuntimeContractException("port-enum-set", "Unknown enum set: " + set);
}
