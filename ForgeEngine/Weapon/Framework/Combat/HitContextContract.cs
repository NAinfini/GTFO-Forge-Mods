using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// One hit's own context, as the machine that fired the shot observed it: how much of the hit came from behind the
/// target, the angle it struck the surface at, how far the ray travelled, whether the shot reached its target
/// with geometry in between, the damage falloff the impact carried, where it landed, and where that landing sits
/// on the target now — the hit point expressed against the target's current position.
///
/// <see cref="Position"/> is the world point the ray reached; <see cref="AnchoredPosition"/> is the target's
/// current position plus the offset the hit point had from the target when the impact happened, which is the
/// reading a range query centred on a moving target needs. Both are three world coordinates in metres; the
/// anchored one is null when the target's own body could not be placed at the read, because a point measured
/// against a position nobody read would be an invented one.
/// </summary>
internal sealed record HitContextReading(double BackstabRatio, double AngleDegrees, double DistanceMetres,
    bool ThroughWall, double Falloff, double[] Position, double[]? AnchoredPosition);

/// <summary>The one world read behind <see cref="HitContextContract.CapabilityId"/>: the impact this machine
/// recorded for one hit. Implemented by this package's native half — the only place a game object is reached — so
/// the row's body holds no game type and is exercised against a double. A read that cannot be answered returns
/// false with the code it refused by rather than a substituted number, so a plan can never read a context the game
/// never reported.</summary>
internal interface IHitContextSource
{
    /// <summary>
    /// The impact of the hit its number names, or false with the code the read refused by. The number is the hit
    /// candidate's own `hit` port, one per hit — one per pellet of a shotgun shot — so a number no current record
    /// holds, or whose record names another shooter or target, is a refusal and never another hit that happens to
    /// match.
    /// </summary>
    bool TryHitContext(EntityReference source, EntityReference target, long hit, out HitContextReading reading, out string code);
}

/// <summary>
/// The `forge.query.combat.hit_context` row: the context of one hit, read after the fact from the impact the
/// weapon's own hit routine produced. The row is `state`/`query` — it reads the world on demand and writes nothing
/// — and it is answered from <see cref="IHitContextSource"/>, which the native half attaches for as long as its
/// session is the live one. A process without that half refuses the read by code instead of declaring a row
/// nothing answers.
///
/// <b>Why the ports are read from the target as well.</b> `backstab_ratio` is a relation between the shot and the
/// target, not a property of either: the native half measures it where both exist, which is the impact. The row's
/// own two entity inputs are still charged to the plan's query budget and checked current before the source is
/// asked, so a stale reference refuses here rather than being read from a record of a life that is gone.
///
/// <b>What each number is.</b> `distance` is the ray's own travelled length to the point it reached; `angle` is
/// the angle between the shot's direction and the surface normal it struck; `backstab_ratio` is `(1 - cos) / 2`
/// between the target's facing and the direction the shot came from — 1 is squarely from behind, 0 squarely from
/// the front; and `through_wall` reports whether the native hit body was asked to search past its first collider.
/// The evidence file `evidence/weapon-hit-context.json` names each native member and lists what is unverified.
/// </summary>
internal static class HitContextContract
{
    /// <summary>The row's id; the row itself is the generated block table's.</summary>
    public const string CapabilityId = Blocks.QueryCombatHitContext.Id;
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.hit_context";
    /// <summary>The evaluator name the binding resolves to. The row is answered on demand, so the name is also the
    /// shape key: a registration cannot supply an evaluator whose ports were never declared.</summary>
    public const string Handler = "gtfo.weapon.hit_context";

    /// <summary>The permission the read carries: it reads a shot this machine observed and the target it reached,
    /// which is the same combat reading the shot and hit-candidate rows already carry, and nothing here writes.
    /// </summary>
    public const string ReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The ports the evaluator really reads, resolved at registration against the row's own ports: the
    /// two references and the hit number the hit is addressed by, answered as the seven values the catalog declares.
    /// </summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("source", "target", "hit")
        .Outputs("backstab_ratio", "angle", "distance", "through_wall", "falloff", "position", "anchored_position");

    /// <summary>The evaluator table the registration carries, taken from the reads that own the body so a shape and
    /// its evaluator can never be registered apart.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => HitContextReads.Evaluators();

    /// <summary>The answer frame, built once from the reading so a port the catalog declares is never filled by a
    /// second spelling of the same number.</summary>
    public static void Answer(HitContextReading reading, EvaluationOutputs outputs)
    {
        outputs.Number(0, reading.BackstabRatio);
        outputs.Number(1, reading.AngleDegrees);
        outputs.Number(2, reading.DistanceMetres);
        outputs.Boolean(3, reading.ThroughWall);
        outputs.Number(4, reading.Falloff);
        Position(outputs, 5, reading.Position);
        Position(outputs, 6, reading.AnchoredPosition);

        // A position that is not three numbers goes to the port as it is, and the port's own rule refuses it.
        static void Position(EvaluationOutputs outputs, int output, double[]? value)
        {
            if (value == null) outputs.Null(output);
            else if (value.Length == 3) outputs.Vector(output, value[0], value[1], value[2]);
            else outputs.Json(output, RuntimeJson.From(value));
        }
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, Handler, "observe", new[] { ReadPermission }, Shape)
    };
}

/// <summary>
/// The `forge.query.combat.hit_context` body and the native source it is answered from. <see cref="Installed"/> is
/// the attached source and its own thread: the native half installs it from the thread that owns the game objects
/// it read, and a read from any other thread is refused rather than answered through interop off the game's thread.
/// </summary>
internal sealed class HitContextReads
{
    /// <summary>No source is attached. The export process and any host that declared this provider without its game
    /// half answer every read with this code.</summary>
    public const string SourceUnavailableCode = "hit-context-source-unavailable";
    /// <summary>The hit the read names has no current record on this machine: it was never recorded, the table has
    /// dropped it, its record names another shooter or target, or the number is empty.</summary>
    public const string UnknownHitCode = "hit-context-unknown";
    /// <summary>A required port of the node is absent or not the kind the row declares.</summary>
    public const string MissingFieldCode = "missing-field";

    private readonly IHitContextSource _source;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private HitContextReads(IHitContextSource source) => _source = source;

    /// <summary>The source of the host this package is loaded into, or null while none is attached.</summary>
    public static HitContextReads? Installed { get; private set; }

    /// <summary>Attaches one host's source and answers the reads a session detaches with.</summary>
    public static HitContextReads Attach(IHitContextSource source)
        => Installed = new HitContextReads(source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>Detaches exactly the reads one attach returned: a session that is not the installed one leaves the
    /// installed one alone, so a disposal cannot detach a machine that replaced it.</summary>
    public static void Detach(HitContextReads reads)
    {
        if (ReferenceEquals(Installed, reads)) Installed = null;
    }

    /// <summary>The evaluator table the registration carries: one row, one body, one shape.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [HitContextContract.Handler] = Row
    };

    /// <summary>`forge.query.combat.hit_context`: the three inputs the node names, each checked against the row's
    /// own contract before the source is asked, answered as the four values the catalog declares. Both references
    /// are charged to the plan's query budget and checked against the kernel's entity table first, so a life that is
    /// gone refuses here instead of being read out of a record of a hit that no longer exists.</summary>
    private static void Row(EvaluationContext context, EvaluationOutputs outputs)
    {
        var reads = Require();
        var source = Reference(context, "source");
        var target = Reference(context, "target");
        Snapshot(context, source);
        Snapshot(context, target);
        HitContextContract.Answer(Context(reads._source, source, target, Hit(context.Inputs)), outputs);
    }

    /// <summary>The answer as a function of the source and the hit it is asked about: the row above supplies the
    /// ports, the budgeted currency checks and the attached source, and a source that refuses keeps its own code
    /// rather than being answered with a context the game never reported.</summary>
    internal static HitContextReading Context(IHitContextSource source, EntityReference from, EntityReference to, long hit)
    {
        if (!source.TryHitContext(from, to, hit, out var reading, out var code))
            throw new RuntimeContractException(code, "Hit context read refused: " + code);
        return reading;
    }

    /// <summary>The hit number the node names, from the hit candidate that triggered the behavior. The port is
    /// nullable — a candidate whose context this machine never records carries none — and an empty or absent number
    /// names no hit, so it is refused as an unknown hit rather than read as some other one.</summary>
    internal static long Hit(JsonElement inputs)
    {
        if (!inputs.TryGetProperty("hit", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var hit))
            throw new RuntimeContractException(UnknownHitCode, "hit");
        return hit;
    }

    /// <summary>The installed source, refused by name while none is attached and only read from its own thread.</summary>
    private static HitContextReads Require()
    {
        if (Installed is not { } reads)
            throw new RuntimeContractException(SourceUnavailableCode, "No hit context source is attached.");
        reads.CheckThread();
        return reads;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException(SourceUnavailableCode, "The hit context source is read off its own thread.");
    }

    /// <summary>The entity one port names. The port is declared by the row, so an input of another shape is a
    /// malformed node and is refused by the port's own name.</summary>
    private static EntityReference Reference(EvaluationContext context, string port)
    {
        if (!context.Inputs.TryGetProperty(port, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException(MissingFieldCode, port);
        return RuntimeJson.Entity(value);
    }

    /// <summary>The budgeted world read the row starts with for each reference it names.</summary>
    private static void Snapshot(EvaluationContext context, EntityReference reference)
    {
        if (!context.Query.TrySnapshot(reference, out _, out var code))
            throw new RuntimeContractException(code, "Entity read failed: " + code);
    }
}
