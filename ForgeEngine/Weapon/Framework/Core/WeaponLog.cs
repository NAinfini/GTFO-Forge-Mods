using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The weapon provider's diagnostic codes in the Forge Engine log (contract §3.2 I-DIAG, 包内部原生诊断码). Each code
/// has one fixed level, written beside it. A fact the kernel accepted or refused is never logged here: the kernel
/// already writes `trigger.fired` for every queued event and `event.rejected` / `budget.exceeded` for every refusal,
/// under this provider's binding, so a line of this package's own for the same fact would record it twice. What is
/// left is what only this package sees: a refusal it made before the kernel was reached, a native event it could not
/// turn into a fact, the life of an equipment or placement, and the per-shot diagnostics that exist at trace only.
/// </summary>
internal static partial class WeaponLogCodes
{
    // The constants are generated from catalog/authoring/diagnostic-codes.json (artifacts/generated/diagnostics/WeaponLogCodes.g.cs).
}

/// <summary>
/// The weapon package's one way into the Forge Engine log: the registration's own <see cref="RuntimeModuleHandle.Log"/>,
/// reached through one object every observer of the session shares. Several observers are built before the
/// registration exists (the registration carries their bodies), so the handle is attached once it does; nothing is
/// written before that, and every observer asks <see cref="Logs"/> before it builds a value array, so a closed level
/// allocates nothing.
/// </summary>
internal sealed class WeaponLog
{
    private RuntimeModuleHandle? _handle;

    /// <summary>A log with no registration behind it: every level is closed. Offline suites that do not read the log
    /// pass this.</summary>
    public static WeaponLog Closed => new();

    /// <summary>Hands the registration over. Called once, right after the registration exists.</summary>
    public void Attach(RuntimeModuleHandle handle) => _handle = handle ?? throw new System.ArgumentNullException(nameof(handle));

    /// <summary>Whether a record at <paramref name="level"/> would be written: one comparison once attached.</summary>
    public bool Logs(RuntimeLogLevel level) => _handle != null && _handle.Logs(level);

    /// <summary>One record with the facts already built. Callers that build a value array ask <see cref="Logs"/> first.</summary>
    public void Write(RuntimeLogLevel level, string code, in RuntimeLogFacts facts) => _handle?.Log(level, code, in facts);

    /// <summary>A record whose facts are a short text rather than named values: the loadout policy's decisions, which
    /// the pure filter states as lines. The caller asks <see cref="Logs"/> first when building the text costs anything.</summary>
    public void Text(RuntimeLogLevel level, string code, string? detail, string? entity = null)
    {
        if (!Logs(level)) return;
        Write(level, code, new RuntimeLogFacts { Entity = entity, Detail = detail });
    }

    private readonly ForgeCore.OnceReporter _skips = new();

    /// <summary>Trace <see cref="WeaponLogCodes.FactSkipped"/> for a publication path that ended without a queued fact,
    /// written on the 1st, 10th, 100th ... occurrence of each (binding, reason, detail), so a path that runs on every
    /// pool update or swing cannot flood the log. `count` is the running total. Nothing is allocated unless trace is open.</summary>
    public void Skipped(string? binding, string reason, string? detail = null, string? entity = null)
    {
        if (!Logs(RuntimeLogLevel.Trace)) return;
        var key = binding + "|" + reason + "|" + detail;
        if (!_skips.ShouldReport(key)) return;
        Write(RuntimeLogLevel.Trace, WeaponLogCodes.FactSkipped, new RuntimeLogFacts
        {
            Entity = entity,
            Binding = binding,
            Detail = detail,
            Values = new[] { RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Of("count", _skips.Count(key)) }
        });
    }

    /// <summary>The kernel's answer to a published fact when it did not queue it (`ignored/no-consumer`,
    /// `ignored/attachment-mismatch`, `duplicate/...`): the kernel records refusals itself but stays silent about an
    /// ignored fact, so this is the only place a fact that no mounted plan claimed shows up.</summary>
    public void Answered(string binding, DispatchResult result, string? entity = null)
    {
        if (result.Status != "queued" && Logs(RuntimeLogLevel.Trace)) Skipped(binding, "kernel-" + result.Status + "/" + result.Code, entity: entity);
    }


    /// <summary>The common shape: a code, the reason it happened, and the entity and binding it is about. Nothing is
    /// allocated unless the level is open.</summary>
    /// <summary>A fact refused because its entity's life already ended (an event of the old life arriving late) is the
    /// identity check working, not a fault; every other refusal stays an Error.</summary>
    public static RuntimeLogLevel RefusalLevel(string reason)
        => reason is "stale-entity" or "stale-equipment" or "stale-world" or "no-wielded-gear" ? RuntimeLogLevel.Info : RuntimeLogLevel.Error;

    public void Reason(RuntimeLogLevel level, string code, string reason, string? entity = null, string? binding = null)
    {
        if (!Logs(level)) return;
        Write(level, code, new RuntimeLogFacts
        {
            Entity = entity,
            Binding = binding,
            Values = new[] { RuntimeLogValue.Of("reason", reason) }
        });
    }
}
