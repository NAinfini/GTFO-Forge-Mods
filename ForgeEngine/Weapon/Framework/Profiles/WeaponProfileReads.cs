using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>One reading of a weapon instance's own block, as the native half answers it: the three spread numbers
/// and the four recoil numbers, each read from the block member it names and none derived from another.</summary>
internal readonly record struct WeaponProfileSample(double SpreadCone, double SpreadMovementScale, double SpreadAimScale,
    double RecoilHorizontal, double RecoilVertical, double RecoilRecovery, double RecoilCameraKick);

/// <summary>The world read behind <see cref="WeaponProfileReadContract"/>. Implemented by this package's native
/// half, which is the only place a game object is reached: the row body below holds this interface and never a
/// game type. A read that cannot be answered returns false with the code it refused by rather than a substituted
/// number, so a plan can never read a zero the instance never carried. It is spelled apart from
/// <see cref="IWeaponProfileSource"/>, which is the write's own half: the two answer different questions about the
/// same instance and one interface with two methods would let a half supply one and be asked the other.</summary>
internal interface IWeaponProfileReadSource
{
    /// <summary>One instance's own block, or false with the code the read refused by: a life this machine does
    /// not answer for right now, an archetype whose block is gone, or a recoil half the block's own
    /// `RecoilDataID` does not resolve.</summary>
    bool TryProfile(EntityReference equipment, out WeaponProfileSample sample, out string code);
}

/// <summary>The profile row's body: a pure function of the evaluated node and the source the host attached, which
/// is what an `observe` row over a `state` capability is. The port is read from the node's own inputs, the
/// equipment the row is about is charged to the plan's query budget and checked current by the kernel, and the
/// answer comes from the attached source or the row refuses by name. Nothing is cached between evaluations, so a
/// re-evaluation reads the instance again.
///
/// <see cref="Installed"/> is the attached source and its own thread: the native half installs it from the thread
/// that owns the game objects it reads, and a read from any other thread is refused rather than answered through
/// interop off the game's thread.</summary>
internal sealed class WeaponProfileReads
{
    /// <summary>No source is attached. The export process and any host that declared this provider without its
    /// game half answer the read with this code. It is the profile write's own word for the same state — one
    /// profile half, one spelling — and not a second code for it.</summary>
    public const string SourceUnavailableCode = ResultCode.WeaponProfileSourceUnavailable;
    /// <summary>A required port of the node is absent or not the kind the row declares. The profile write's own
    /// word for the same refusal.</summary>
    public const string MissingFieldCode = ResultCode.MissingField;

    private readonly IWeaponProfileReadSource _source;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private WeaponProfileReads(IWeaponProfileReadSource source) => _source = source;

    /// <summary>The source of the host this package is loaded into, or null while none is attached.</summary>
    public static WeaponProfileReads? Installed { get; private set; }

    /// <summary>Attaches one host's source and answers the reads a session detaches with.</summary>
    public static WeaponProfileReads Attach(IWeaponProfileReadSource source)
        => Installed = new WeaponProfileReads(source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>Detaches exactly the reads one attach returned, so a disposal cannot detach a machine that
    /// replaced it.</summary>
    public static void Detach(WeaponProfileReads reads)
    {
        if (ReferenceEquals(Installed, reads)) Installed = null;
    }

    /// <summary>The evaluator table the registration carries: one name, the one the contract declares.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [WeaponProfileReadContract.HandlerName] = ProfileRow
    };

    /// <summary>`forge.query.weapon.profile`: the equipment life the node names, answered as the seven numbers its
    /// own block carries.</summary>
    private static void ProfileRow(EvaluationContext context, EvaluationOutputs outputs)
    {
        var reads = Require();
        if (!context.Inputs.TryGetProperty("equipment", out var value) || value.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException(MissingFieldCode, "equipment");
        var equipment = RuntimeJson.Entity(value);
        // The budgeted world read every row starts with: the entity is charged to the plan's query budget and
        // checked against the kernel's own entity table before any native object is touched.
        Snapshot(context, equipment);
        Profile(reads._source, equipment, outputs);
    }

    /// <summary>The read's answer as a function of the source and the life it is about. A source that refuses
    /// keeps its own code rather than being answered with numbers.</summary>
    internal static void Profile(IWeaponProfileReadSource source, EntityReference equipment, EvaluationOutputs outputs)
    {
        if (!source.TryProfile(equipment, out var sample, out var code))
            throw new RuntimeContractException(code, "Weapon profile read refused: " + code);
        WeaponProfileReadContract.Answer(sample, outputs);
    }

    /// <summary>The installed source, refused by name while none is attached and only read from its own thread.
    /// </summary>
    private static WeaponProfileReads Require()
    {
        if (Installed is not { } reads)
            throw new RuntimeContractException(SourceUnavailableCode, "No weapon profile source is attached.");
        if (Environment.CurrentManagedThreadId != reads._threadId)
            throw new RuntimeContractException(SourceUnavailableCode, "The weapon profile source is read off its own thread.");
        return reads;
    }

    /// <summary>The budgeted world read every row starts with: the entity the row is about is charged to the
    /// plan's query budget and checked against the kernel's own entity table before any native object is touched,
    /// so a life that is gone refuses here instead of being read from a stale handle.</summary>
    private static void Snapshot(EvaluationContext context, EntityReference reference)
    {
        if (!context.Query.TrySnapshot(reference, out _, out var code))
            throw new RuntimeContractException(code, "Entity read failed: " + code);
    }
}
