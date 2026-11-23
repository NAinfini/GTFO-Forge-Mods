using HarmonyLib;
using LevelGeneration;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The two Harmony patches behind `forge.trigger.interaction.terminal_log_read`. They are the terminal command
/// interpreter's own pair of log-read entries, and between them they cover a read on every peer exactly once:
/// `ReadLog` is the entry the machine that received the `READLOG` command runs before it sends the file's
/// content id out as a `pLogRead` packet, and `OnReceiveLogRead` is the entry every other machine runs when that
/// packet arrives. Neither the sender nor the receivers run both, so one read is one callback per machine and the
/// host is the peer whose callback publishes.
///
/// Both postfixes only choose when to read: the interpreter's own terminal, the file its table names and the
/// player its synced interaction source names are read by `TerminalLogFacts`, so a patch holds no rule of its
/// own and the same body is driven by the focused test. The two entries, their RVAs and the table they are read
/// through are frozen in `evidence/door-terminal-hooks.json`.</summary>
[HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter),
    nameof(LG_ComputerTerminalCommandInterpreter.ReadLog))]
internal static class TerminalLogReadReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_ComputerTerminalCommandInterpreter __instance, string param1)
    {
        if (!Session.IsHost) return;
        TerminalLogFacts.Current?.Guard(facts => facts.Read(__instance, param1));
    }
}

[HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter),
    nameof(LG_ComputerTerminalCommandInterpreter.OnReceiveLogRead))]
internal static class TerminalLogReceivedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_ComputerTerminalCommandInterpreter __instance, pLogRead data)
    {
        if (!Session.IsHost) return;
        TerminalLogFacts.Current?.Guard(facts => facts.Received(__instance, data.ID));
    }
}
