using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>What one detonation attempt did. A refusal is a device nothing was called on; a commit is a trigger
/// the game's own state shows went off; an unknown is a trigger that was submitted and whose outcome the game
/// does not let this build read back. The three are distinct because the middle one is the only one this row may
/// report as a success.</summary>
internal enum DetonateOutcome
{
    /// <summary>Nothing was called: the device is not one this row can detonate, or it is already going off.</summary>
    Rejected,
    /// <summary>The trigger was called and the device's own state shows the detonation started.</summary>
    Committed,
    /// <summary>The trigger was called and the outcome could not be read back, so it is neither claimed nor
    /// retried.</summary>
    Unknown
}

/// <summary>The one native half behind <see cref="DetonateContract"/>, kept behind an interface so the row's body
/// holds no game type and a process without this package's native half can still declare the row and refuse it by
/// code.
///
/// The source resolves one reference to the native device and calls that device's own trigger. What it answers is
/// one of the three outcomes above with the code that says which one it was; a refusal is never a silent
/// success, and an outcome the game cannot show is never reported as a commit.</summary>
internal interface IDetonateSource
{
    /// <summary>Detonates one deployed device, or answers what stopped it.</summary>
    DetonateOutcome Detonate(EntityReference device, out string code);
}

/// <summary>The `detonate` row's body. It is the same shape the debug-switch and profile rows of this package
/// use: a source the native half attaches for as long as its session is the live one, a thread the source is only
/// ever reached from, and a refusal by name whenever an answer would otherwise be invented.
///
/// The per-recipient loop is the inventory family's own: every device gets its own result row and is handled on
/// its own, whatever the devices before it answered.
/// </summary>
internal sealed class DetonateWrites
{
    private readonly IDetonateSource _source;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private DetonateWrites(IDetonateSource source) => _source = source;

    /// <summary>The source of the host this package is loaded into, or null while none is attached.</summary>
    public static DetonateWrites? Installed { get; private set; }

    /// <summary>Attaches one host's source and answers the requests a session detaches with.</summary>
    public static DetonateWrites Attach(IDetonateSource source)
        => Installed = new DetonateWrites(source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>Detaches exactly the writes one attach returned, so a disposal cannot detach a machine that
    /// replaced it.</summary>
    public static void Detach(DetonateWrites writes)
    {
        if (ReferenceEquals(Installed, writes)) Installed = null;
    }

    /// <summary>The handler table the registration carries: one name, the one the contract declares.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers() => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
    {
        [DetonateContract.HandlerName] = DetonateRow
    };

    /// <summary>`forge.action.inventory.detonate`: one trigger call per device the step named, through the action
    /// skeleton. Every refusal that can be decided without the game is decided here or by the skeleton (the count,
    /// a device of another world), and the native half decides the rest and says which of the three outcomes it
    /// reached. The device row's own column is the number of devices the step named, so a reader can tell a partial
    /// run from a complete one.</summary>
    private static CommandResult DetonateRow(CommandContext context)
    {
        if (Installed is not { } writes) return Outcome.Rejected(ResultCode.DetonateSourceUnavailable);
        if (Environment.CurrentManagedThreadId != writes._threadId)
            return Outcome.Rejected(ResultCode.DetonateSourceUnavailable).WithDetail("The detonate source is written off its own thread.");
        var source = writes._source;
        return context.EachTarget(ref source, Step, maximum: CommandResult.MaximumFacts, exceptionCode: ResultCode.NativeCommitException,
            countColumn: Blocks.ActionInventoryDetonate.RowTargetCount);
    }

    /// <summary>One device: the reference is resolved by the native half, which owns the placement table, so a
    /// reference of another shape resolves to no device there and is refused on its own row.</summary>
    private static readonly TargetStep<IDetonateSource> Step = static (ref IDetonateSource source, EntityReference device, int index, RowWriter rows) =>
    {
        var outcome = source.Detonate(device, out var code);
        switch (outcome)
        {
            case DetonateOutcome.Committed: rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed); break;
            case DetonateOutcome.Rejected: rows.End(Status.Rejected, Commit.None, code); break;
            default: rows.End(Status.Failed, Commit.Unknown, code); break;
        }
    };
}
