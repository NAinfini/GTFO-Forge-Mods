using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The two reads of an encounter that is already running: a raised alarm and a started wave. Both address their
/// subject by the effect handle the action that started it published — `AlarmWaveContract`'s own `encounter`
/// handles — which is why they are declared beside that family rather than inside it: they consume what it
/// produces and produce nothing of their own.
///
/// <list type="bullet">
/// <item><description>`forge.query.map.alarm_state` answers whether the chained puzzle a handle names is still
/// active (`ChainedPuzzleInstance.IsActive`).</description></item>
/// <item><description>`forge.query.map.wave_state` answers whether the Mastermind still holds the event a handle
/// names (`Mastermind.TryGetEvent`), which is the one register a running survival wave lives in.</description></item>
/// </list>
/// </summary>
internal static class EncounterStateContract
{
    public const string AlarmStateBindingId = ModuleDefinition.ProviderId + ".binding.alarm_state";
    public const string WaveStateBindingId = ModuleDefinition.ProviderId + ".binding.wave_state";

    public const string AlarmStateHandler = "gtfo.map.alarm_state";
    public const string WaveStateHandler = "gtfo.map.wave_state";

    /// <summary>The two read permissions the query rows carry. A read is not a control: a plan that may ask
    /// whether the alarm is still up is not thereby allowed to raise or clear one.</summary>
    public const string AlarmReadPermission = "alarm.read";
    public const string WaveReadPermission = "wave.read";

    public static readonly HandlerShape AlarmStateShape = new HandlerShape().Inputs("alarm").Outputs("active");
    public static readonly HandlerShape WaveStateShape = new HandlerShape().Inputs("wave").Outputs("active");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(AlarmStateBindingId, Blocks.QueryMapAlarmState.Id, AlarmStateHandler, "evaluate", new[] { AlarmReadPermission }, AlarmStateShape),
        new ProviderBinding(WaveStateBindingId, Blocks.QueryMapWaveState.Id, WaveStateHandler, "evaluate", new[] { WaveReadPermission }, WaveStateShape)
    };
}
