using System;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>What a ruled shot does with the world geometry between the muzzle and a target. `block` is the game's
/// own ray; the two pierce members let the ray pass walls but only reach a target the level's own course graph
/// connects to the shooter.</summary>
internal enum ShotRuleWalls
{
    Block,
    /// <summary>The target's course node is reachable from the shooter's through portals the graph itself calls
    /// traversable.</summary>
    PierceReachable,
    /// <summary>The same walk, where every portal that carries a door also needs that door open or destroyed.</summary>
    PierceOpenDoors
}

/// <summary>Which point of the `aim_at` target the ray is turned toward.</summary>
internal enum ShotRuleAimPart
{
    /// <summary>The agent's own aim target, the point the game's own aim assistance uses.</summary>
    AimTarget,
    /// <summary>The agent's own body aim point.</summary>
    Body,
    /// <summary>The intact weak-spot limb closest to the shot's own heading.</summary>
    Weakspot,
    /// <summary>The intact limb of any kind closest to the shot's own heading.</summary>
    ClosestLimb
}

/// <summary>One node's structural parameters, resolved once per dispatch from the plan's constant frame.</summary>
internal struct ShotRuleParameters
{
    public ShotRuleWalls Walls;
    public ShotRuleAimPart AimPart;
    public bool Suppress;
}

/// <summary>The answer of one reachability walk. `Budget` is a walk that ran out of queue before it could decide,
/// which the caller treats as not reachable and reports: a target is never assumed reachable.</summary>
internal enum ShotReach
{
    Reachable,
    Unreachable,
    Budget
}

/// <summary>The level graph one reachability walk reads. The native half answers it from the game's own course
/// nodes and portals; a test answers it from a hand-built graph. Nodes are compared through <see cref="Same"/>
/// because the native side hands out a fresh wrapper per read, so reference identity says nothing.</summary>
internal interface IShotRuleGraph<TNode> where TNode : class
{
    /// <summary>Marks a node visited for the current walk and answers whether it was new.</summary>
    bool Visit(TNode node);
    bool Same(TNode left, TNode right);
    int PortalCount(TNode node);
    /// <summary>The node on the other side of one portal, with the two facts the wall policy reads: whether the
    /// graph calls the portal traversable, and whether the door it carries (if any) is open or destroyed. A portal
    /// with no door answers <paramref name="doorOpen"/> true.</summary>
    TNode? Portal(TNode node, int index, out bool traversable, out bool doorOpen);
}

/// <summary>One limb a weak-spot or closest-limb aim can choose between: its world point, its kind and whether it
/// is already destroyed. Filled by the native half into a buffer it owns, so nothing here allocates per shot.</summary>
internal struct ShotLimbCandidate
{
    public double X, Y, Z;
    public bool Weakspot;
    public bool Destroyed;
}

/// <summary>
/// The `forge.action.weapon.shot_rule` row: a standing rule on one equipment life that rewrites every shot the
/// weapon's own firing body makes while the rule lives. The binding, the support line and the handler shape are
/// this provider's own; the row — ports, units and the owner tier — is the generated block table's
/// (<see cref="Blocks"/>), never copied here.
///
/// The row runs on the holder's machine (`execution: owner`), because the ray the rule rewrites is cast by the
/// holder's own weapon body. The pure halves of the rule live here so they are tested without the game: the
/// parameter reader, the reachability walk over the level graph and the limb choice.
/// </summary>
internal static class ShotRuleContract
{
    public const string CapabilityId = Blocks.ActionWeaponShotRule.Id;
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.shot_rule";
    public const string Handler = "gtfo.weapon.shot_rule";
    /// <summary>The rule rewrites what the held weapon does, which is the same write the instance-override rows
    /// are submitted under.</summary>
    public const string Permission = WeaponOverrideContract.WieldWritePermission;

    private static readonly JsonElement Row = Blocks.Capability(Blocks.ActionWeaponShotRule.Index);
    private static readonly string[] WallsMembers = ShotParameters.Members(Row, "walls");
    private static readonly string[] AimPartMembers = ShotParameters.Members(Row, "aim_part");

    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("equipment", "aim_at", "duration")
        .Outputs("result")
        .Parameters("walls", "aim_part", "suppress");
    public static readonly HandlerSlot EquipmentSlot = Shape.InputSlot("equipment");
    public static readonly HandlerSlot AimAtSlot = Shape.InputSlot("aim_at");
    public static readonly HandlerSlot DurationSlot = Shape.InputSlot("duration");
    public static readonly HandlerSlot WallsSlot = Shape.ParameterSlot("walls");
    public static readonly HandlerSlot AimPartSlot = Shape.ParameterSlot("aim_part");
    public static readonly HandlerSlot SuppressSlot = Shape.ParameterSlot("suppress");

    /// <summary>Reads the handler's pre-resolved parameter slots directly from its execution frame.</summary>
    public static bool TryRead(CommandContext context, out ShotRuleParameters parsed, out string code)
    {
        parsed = default;
        if (context.Kind(WallsSlot) is ValueKind.Missing or ValueKind.Null)
        { code = ResultCode.ParameterInvalid; return false; }
        if (!ShotParameters.TryEnum(context, WallsSlot, WallsMembers, out var walls, out code)) return false;
        parsed.Walls = walls switch
        {
            "block" => ShotRuleWalls.Block,
            "pierce_reachable" => ShotRuleWalls.PierceReachable,
            _ => ShotRuleWalls.PierceOpenDoors
        };
        if (!ShotParameters.TryEnum(context, AimPartSlot, AimPartMembers, out var aim, out code)) return false;
        parsed.AimPart = aim switch
        {
            "aim_target" => ShotRuleAimPart.AimTarget,
            "body" => ShotRuleAimPart.Body,
            "weakspot" => ShotRuleAimPart.Weakspot,
            _ => ShotRuleAimPart.ClosestLimb
        };
        return ShotParameters.TryFlag(context, SuppressSlot, false, out parsed.Suppress, out code);
    }

    /// <summary>
    /// One node's structural parameters. `walls` is required by the card, so a frame without it is refused rather
    /// than defaulted; `aim_part` absent is the card's first member and `suppress` absent is false. An enum arrives
    /// as the member's index in a compiled plan and as its name in a hand-built frame; any other value is refused
    /// with <see cref="ResultCode.ParameterInvalid"/>.
    /// </summary>
    public static bool TryRead(JsonElement parameters, out ShotRuleParameters parsed, out string code)
    {
        parsed = default;
        var bag = parameters.ValueKind == JsonValueKind.Object ? parameters : default;
        if (bag.ValueKind != JsonValueKind.Object || !bag.TryGetProperty("walls", out var wallValue)
            || wallValue.ValueKind == JsonValueKind.Null)
        { code = ResultCode.ParameterInvalid; return false; }
        if (!ShotParameters.TryEnum(bag, "walls", WallsMembers, out var walls, out code)) return false;
        parsed.Walls = walls switch
        {
            "block" => ShotRuleWalls.Block,
            "pierce_reachable" => ShotRuleWalls.PierceReachable,
            _ => ShotRuleWalls.PierceOpenDoors
        };
        if (!ShotParameters.TryEnum(bag, "aim_part", AimPartMembers, out var aim, out code)) return false;
        parsed.AimPart = aim switch
        {
            "aim_target" => ShotRuleAimPart.AimTarget,
            "body" => ShotRuleAimPart.Body,
            "weakspot" => ShotRuleAimPart.Weakspot,
            _ => ShotRuleAimPart.ClosestLimb
        };
        return ShotParameters.TryFlag(bag, "suppress", false, out parsed.Suppress, out code);
    }

    /// <summary>
    /// Whether <paramref name="to"/> is reachable from <paramref name="from"/> through the level graph under one
    /// wall policy: a breadth-first walk over course nodes, crossing a portal only when the graph calls it
    /// traversable and, for <see cref="ShotRuleWalls.PierceOpenDoors"/>, only when the door it carries is open or
    /// destroyed. The queue is the caller's own buffer; a walk that would need more room than it has answers
    /// <see cref="ShotReach.Budget"/> instead of guessing. `block` never walks — the game's own ray already decides
    /// — and is answered unreachable.
    /// </summary>
    public static ShotReach Reach<TNode, TGraph>(TGraph graph, TNode from, TNode to, ShotRuleWalls walls, TNode?[] queue)
        where TNode : class where TGraph : IShotRuleGraph<TNode>
    {
        if (walls == ShotRuleWalls.Block || queue.Length == 0) return ShotReach.Unreachable;
        if (graph.Same(from, to)) return ShotReach.Reachable;
        var head = 0;
        var tail = 0;
        graph.Visit(from);
        queue[tail++] = from;
        var answer = ShotReach.Unreachable;
        while (head < tail && answer != ShotReach.Reachable)
        {
            var node = queue[head++]!;
            var portals = graph.PortalCount(node);
            for (var index = 0; index < portals; index++)
            {
                var next = graph.Portal(node, index, out var traversable, out var doorOpen);
                if (next == null || !traversable) continue;
                if (walls == ShotRuleWalls.PierceOpenDoors && !doorOpen) continue;
                if (graph.Same(next, to)) { answer = ShotReach.Reachable; break; }
                if (!graph.Visit(next)) continue;
                if (tail == queue.Length) { answer = ShotReach.Budget; continue; }
                queue[tail++] = next;
            }
        }
        // The buffer is the caller's and outlives the walk, so it holds no node past it.
        for (var index = 0; index < tail; index++) queue[index] = null;
        return answer;
    }

    /// <summary>
    /// The limb an aim part chooses: of the intact candidates (weak spots only for
    /// <see cref="ShotRuleAimPart.Weakspot"/>), the one whose direction from the origin makes the smallest angle
    /// with the shot's own heading. `-1` is no candidate at all — the caller keeps the game's own heading and
    /// reports it, rather than turning the shot toward something the part does not name.
    /// </summary>
    public static int PickLimb(ReadOnlySpan<ShotLimbCandidate> limbs, double originX, double originY, double originZ,
        double headingX, double headingY, double headingZ, bool weakspotOnly)
    {
        var headingLength = Math.Sqrt(headingX * headingX + headingY * headingY + headingZ * headingZ);
        if (!(headingLength > 1e-9d)) return -1;
        var best = -1;
        var bestCos = double.NegativeInfinity;
        for (var index = 0; index < limbs.Length; index++)
        {
            var limb = limbs[index];
            if (limb.Destroyed || (weakspotOnly && !limb.Weakspot)) continue;
            var dx = limb.X - originX;
            var dy = limb.Y - originY;
            var dz = limb.Z - originZ;
            var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!(length > 1e-9d)) continue;
            var cos = (dx * headingX + dy * headingY + dz * headingZ) / (length * headingLength);
            if (cos > bestCos)
            {
                bestCos = cos;
                best = index;
            }
        }
        return best;
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings(CommandHandler? handler) => handler != null ? new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, Handler, "execute", new[] { Permission }, Shape)
    } : Array.Empty<ProviderBinding>();
}
