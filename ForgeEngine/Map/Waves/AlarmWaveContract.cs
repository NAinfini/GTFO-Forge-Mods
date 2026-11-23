using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The scan, wave and alarm actions of the `forge.action.map` family: their bindings, handler shapes,
/// permissions and resource kinds. The capability rows themselves come from the generated block table.
///
/// Every one of them is an `execute` row: the handler writes through a native entry the game itself runs, and the
/// result row reports whether that write was issued, was already in the state the request asked for, or was
/// refused by name.
///
/// The alarm is that same chained puzzle: the game has one instance kind, the catalog expresses starting it with
/// `scan_start`, and the puzzle's own data block (`TriggerAlarmOnActivate`) is what makes the activation raise an
/// alarm. The two alarm rows the rulings deleted are therefore not declared here, and nothing takes their place —
/// a level's alarm is started by the scan row that names the same instance.</summary>
internal static class AlarmWaveContract
{
    /// <summary>The provider every row of this family belongs to, written out rather than read from
    /// `ModuleDefinition` so the contract and the native half behind it compile on their own — which is what lets
    /// the focused test project exercise the rows without the game-independent assembly's other halves. The
    /// integration batch wires the rows into that definition and is what keeps the two spellings one.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    /// <summary>The port the wave start row publishes its handle on, and the lifetime every handle this family
    /// mints carries: a wave lives for the encounter. The scan row publishes no handle — nothing read one, so the
    /// rulings had it deleted rather than kept as a port no plan can use.</summary>
    public const string WaveHandlePort = "wave_handle";
    public const string HandleLifetime = "encounter";

    /// <summary>The port the alarm row publishes its handles on, one element per recipient in the request's own
    /// order. It is a collection rather than a single handle because a batch of doors is a batch of instances: the
    /// alarm behind each door is its own puzzle, and a port carrying one handle could only ever name the first of
    /// them. A recipient whose door holds no puzzle carries the null the port declares, and the `level` scope
    /// publishes nothing at all because it names no instance.</summary>
    public const string AlarmHandlePort = "alarm_handle";

    /// <summary>The handler names the native half supplies, one per row. They are this provider's own vocabulary:
    /// a binding names the handler the registration must carry, and the runtime refuses an implemented binding
    /// whose handler is missing rather than dispatching into nothing.</summary>
    public const string ScanStateHandler = "gtfo.map.scan_state";
    public const string WaveStartHandler = "gtfo.map.wave_start";
    public const string WaveStopHandler = "gtfo.map.wave_stop";
    public const string AlarmHandler = "gtfo.map.alarm";

    /// <summary>The permission a plan needs to drive a scan, and the one it needs to drive waves. Both are the
    /// resources' own control permission, spelled the way the catalog's `recipients.requires` spells it, so a plan
    /// that pins one of these bindings declares exactly what the row says it must.</summary>
    public const string ScanControlPermission = "scan.control";
    public const string WaveControlPermission = "wave.control";
    /// <summary>The permission the alarm switch writes under: the catalog's own recipient contract for
    /// `forge.action.map.alarm` names `alarm.control`, and it is the switch over objects rather than the right to
    /// drive one named puzzle.</summary>
    public const string AlarmControlPermission = "alarm.control";

    /// <summary>One handler's own port set, resolved at registration against the canonical capability. The port
    /// lists are the catalog's, in the catalog's order: an execute binding ends in `result`, and the wave start
    /// row additionally declares the handle output its row publishes — the handle a later stop row reads
    /// back.
    ///
    /// The scan row carries exactly the resource it writes and the setting that says how: `anchor`,
    /// `participants` and `quorum` were declared but never read — the required number of players in a scan
    /// belongs to the puzzle's own data block and no native entry takes it per request — and a parameter or port
    /// the game ignores is a choice an author would keep making, so the rulings had them deleted.</summary>
    public static readonly HandlerShape ScanStateShape = new HandlerShape()
        .Inputs("scan").Outputs("result")
        .Parameters("mode");
    public static readonly HandlerSlot Scan = ScanStateShape.InputSlot("scan");
    public static readonly HandlerSlot ScanMode = ScanStateShape.ParameterSlot("mode");
    /// <summary>The wave start row carries the wave resource and nothing else: the native entry's only arguments are
    /// the course node and the author's settings / population pair, so size, pace and randomness live in those data
    /// blocks and the row declares no port for them.</summary>
    public static readonly HandlerShape WaveStartShape = new HandlerShape()
        .Inputs("wave").Outputs("result", "wave_handle");
    public static readonly HandlerSlot Wave = WaveStartShape.InputSlot("wave");
    public static readonly HandlerShape WaveStopShape = new HandlerShape()
        .Inputs("waves").Outputs("result");
    public static readonly HandlerSlot Waves = WaveStopShape.InputSlot("waves");
    /// <summary>The alarm switch's own ports: the objects it acts on, against the result row and the encounter-scoped handle the row publishes.
    /// Both structural members travel as parameters, exactly as the catalog declares them.</summary>
    public static readonly HandlerShape AlarmShape = new HandlerShape()
        .Inputs("targets").Outputs("result", "alarm_handle").Parameters("mode", "scope");
    public static readonly HandlerSlot AlarmTargets = AlarmShape.InputSlot("targets");
    public static readonly HandlerSlot AlarmMode = AlarmShape.ParameterSlot("mode");
    public static readonly HandlerSlot AlarmScope = AlarmShape.ParameterSlot("scope");

    /// <summary>The binding id of one capability: the capability's own suffix under the Map provider, so either
    /// side of a row names the counterpart of the other without a second table.</summary>
    public static string Binding(string capabilityId)
        => ProviderId + ".binding." + capabilityId["forge.action.map.".Length..];

    /// <summary>The chained-puzzle resource kind's own name in the shared resource-kind table. A chained puzzle
    /// is both the alarm and the scan: the game has one instance kind, its data block's `TriggerAlarmOnActivate`
    /// is what makes an activation an alarm, and the one scan row is therefore how a level's alarm is started.</summary>
    public const string ChainedPuzzleKind = "chained-puzzle";
    /// <summary>The wave resource kind's own name. A wave resource is the author's pair of data blocks — the
    /// settings block and the population block — not a running wave; the running wave is the Mastermind event the
    /// native start entry answers with.</summary>
    public const string WaveKind = "wave";

    /// <summary>The provider that owns the two resource kinds above, for the registration the integration batch
    /// wires into `MapPluginSession.Definition()`. Enumeration and resolution are the native world's own table:
    /// the provider answers what the level currently holds and never invents an instance for an id it does not
    /// have, which is what makes a plan naming a chained puzzle that this level never built fail as
    /// `stale-resource` instead of silently starting nothing.
    ///
    /// Both providers are handed in by the native half, because only it can read the level: this assembly is the
    /// declaration side and holds no game type.</summary>
    public static IReadOnlyDictionary<string, RuntimeResourceProvider> ResourceProviders(
        Func<IReadOnlyList<ResourceRef>> chainedPuzzles, Func<string, ResourceRef?> resolveChainedPuzzle,
        Func<IReadOnlyList<ResourceRef>> waves, Func<string, ResourceRef?> resolveWave)
        => new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
        {
            [ChainedPuzzleKind] = RuntimeResourceProvider.Of(chainedPuzzles, resolveChainedPuzzle),
            [WaveKind] = RuntimeResourceProvider.Of(waves, resolveWave)
        };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.scan_state", Blocks.ActionMapScanState.Id, ScanStateHandler, "execute", new[] { ScanControlPermission }, ScanStateShape),
        new ProviderBinding("forge.module.gtfo.map.binding.wave_start", Blocks.ActionMapWaveStart.Id, WaveStartHandler, "execute", new[] { WaveControlPermission }, WaveStartShape),
        new ProviderBinding("forge.module.gtfo.map.binding.wave_stop", Blocks.ActionMapWaveStop.Id, WaveStopHandler, "execute", new[] { WaveControlPermission }, WaveStopShape),
        new ProviderBinding("forge.module.gtfo.map.binding.alarm", Blocks.ActionMapAlarm.Id, AlarmHandler, "execute", new[] { AlarmControlPermission }, AlarmShape)
    };
}
