using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The world read behind <see cref="WeaponGateContract"/>. Implemented by this package's native half,
/// which is the only place a game object is reached: the row body below holds this interface and never a game
/// type. A read that cannot be answered returns false with the code it refused by rather than a substituted
/// boolean, so a plan can never read a `false` the game never reported.</summary>
internal interface IWeaponGateSource
{
    /// <summary>One declared gate of one equipment life, or false with the code the read refused by: a life this
    /// machine does not answer for right now, a member this build cannot answer as a boolean, or a native read
    /// that failed.</summary>
    bool TryGate(EntityReference equipment, int gateIndex, out bool value, out string code);
}

/// <summary>The gate row's body: a pure function of the evaluated node and the source the host attached, which is
/// what an `observe` row over a `state` capability is. The port is read from the node's own inputs, the equipment
/// the row is about is charged to the plan's query budget and checked current by the kernel, and the answer comes
/// from the attached source or the row refuses by name. Nothing is cached between evaluations, so a
/// re-evaluation reads the game again.</summary>
internal sealed class WeaponGateReads
{
    /// <summary>No source is attached. The export process and any host that declared this provider without its
    /// game half answer the read with this code.</summary>
    public const string SourceUnavailableCode = "weapon-gate-source-unavailable";
    /// <summary>A required port of the node is absent or not the kind the row declares.</summary>
    public const string MissingFieldCode = "missing-field";
    /// <summary>The `gate` parameter is not one of the five declared members.</summary>
    public const string GateCode = "gate-unsupported";
    /// <summary>The equipment reference is not one this machine answers for right now.</summary>
    public const string StaleEquipmentCode = "stale-equipment";
    /// <summary>The native property read threw, so the gate is unknown rather than false.</summary>
    public const string ReadFailedCode = "gate-read-failed";

    private readonly IWeaponGateSource _source;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private WeaponGateReads(IWeaponGateSource source) => _source = source;

    /// <summary>The source of the host this package is loaded into, or null while none is attached.</summary>
    public static WeaponGateReads? Installed { get; private set; }

    /// <summary>Attaches one host's source and answers the reads a session detaches with.</summary>
    public static WeaponGateReads Attach(IWeaponGateSource source)
        => Installed = new WeaponGateReads(source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>Detaches exactly the reads one attach returned, so a disposal cannot detach a machine that
    /// replaced it.</summary>
    public static void Detach(WeaponGateReads reads)
    {
        if (ReferenceEquals(Installed, reads)) Installed = null;
    }

    /// <summary>The evaluator table the registration carries.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [WeaponGateContract.HandlerName] = GateRow
    };

    /// <summary>`forge.query.weapon.gate`: the equipment life the node names and the member it asks for, answered
    /// as the one boolean the game itself would act on.</summary>
    private static void GateRow(EvaluationContext context, EvaluationOutputs outputs)
    {
        var reads = Require();
        // The kernel resolves an enum parameter's compiled index to its member name before any evaluator runs
        // (`RuntimeJson.ResolveEnumParameters`), so the member arrives by name.
        if (!context.Parameters.TryGetProperty("gate", out var member) || member.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException(GateCode, "gate");
        int gate = GateIndex(member.GetString()!);
        if (gate < 0) throw new RuntimeContractException(GateCode, "gate");
        if (!context.Inputs.TryGetProperty("equipment", out var value) || value.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException(MissingFieldCode, "equipment");
        var equipment = RuntimeJson.Entity(value);
        // The budgeted world read every row starts with: the entity is charged to the plan's query budget and
        // checked against the kernel's own entity table before any native object is touched.
        if (!context.Query.TrySnapshot(equipment, out _, out var code))
            throw new RuntimeContractException(code, "Entity read failed: " + code);
        Gate(reads._source, equipment, gate, outputs);
    }

    /// <summary>The member's position in <see cref="WeaponGateContract.Gates"/>, the index the native source is
    /// addressed by, or -1 for a member this row does not declare.</summary>
    internal static int GateIndex(string member)
    {
        for (int i = 0; i < WeaponGateContract.Gates.Count; i++)
            if (string.Equals(WeaponGateContract.Gates[i], member, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>The read's answer as a function of the source, the life it is about and the member asked for. A
    /// source that refuses keeps its own code rather than being answered with a boolean.</summary>
    internal static void Gate(IWeaponGateSource source, EntityReference equipment, int gateIndex, EvaluationOutputs outputs)
    {
        if (!source.TryGate(equipment, gateIndex, out bool value, out var code))
            throw new RuntimeContractException(code, "Weapon gate read refused: " + code);
        outputs.Boolean(0, value);
    }

    /// <summary>The installed source, refused by name while none is attached and only read from its own thread.
    /// </summary>
    private static WeaponGateReads Require()
    {
        if (Installed is not { } reads)
            throw new RuntimeContractException(SourceUnavailableCode, "No weapon gate source is attached.");
        if (Environment.CurrentManagedThreadId != reads._threadId)
            throw new RuntimeContractException(SourceUnavailableCode, "The weapon gate source is read off its own thread.");
        return reads;
    }
}
