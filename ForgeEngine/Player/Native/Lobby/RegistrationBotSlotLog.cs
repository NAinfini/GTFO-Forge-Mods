using ForgeEngine.Framework;

namespace ForgePlayer.Native;

/// <summary>The bot-slot lifecycle's records, written through the Player registration it runs on.</summary>
internal sealed class RegistrationBotSlotLog
{
    private readonly RuntimeModuleHandle _handle;

    internal RegistrationBotSlotLog(RuntimeModuleHandle handle) => _handle = handle;

    public bool Logs(RuntimeLogLevel level) => _handle.IsRegistered && _handle.Logs(level);

    public void Record(RuntimeLogLevel level, string code, string detail, params RuntimeLogValue[] values)
    {
        if (!Logs(level)) return;
        _handle.Log(level, code, new RuntimeLogFacts { Detail = detail, Values = values.Length == 0 ? null : values });
    }
}
