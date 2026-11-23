using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The `forge.trigger.interaction.terminal_log_read` row: a player reading one of a terminal's local log
/// files. The row's shape is the catalog's own — the three coordinates a `map-object` address carries are not
/// part of it, but the terminal it happened at, the file that was read and the player who read it are.
///
/// The native side of this row is the terminal command interpreter's own pair of entries: `ReadLog(string,
/// string)` is the one the machine that received the `READLOG` command runs, and `OnReceiveLogRead(pLogRead)` is
/// the one every other machine runs when that command's packet arrives. Neither is a state readback: the fact is
/// an instantaneous event with no state behind it, so this row is a trigger the provider publishes and not a
/// value anything resolves — which is also why reading one file twice is two facts and needs no state key.
///
/// The packet carries one number and nothing else: `pLogRead.ID` is the read file's `FileContent.Id`. The file's
/// *name* is read back from the terminal's own local log table, whose keys are `FileName.ToUpper()` and whose
/// records carry both the authored `FileName` and that same id (evidence/door-terminal-hooks.json holds the
/// reading of all three entries). A read whose id names no record, or names more than one, publishes nothing and
/// reports why, because a name this provider guessed would be a file the player did not read. A log this mod adds
/// at run time has no Text block, so every machine gives it the id derived from its authored name
/// (`TerminalLogReading.IdFor`) whether it arrives by the command, a restore or a migration; the packet's id is
/// therefore the same number on host and client and never depends on the language.
///
/// Publication happens on the host alone, which the two entries already decide between them: the machine that
/// typed the command runs `ReadLog` and the other machines run `OnReceiveLogRead`, so a client's read is the
/// host's received packet and the host's own read is its own `ReadLog`. The two never both fire for one read on
/// one machine, which is what keeps exactly one fact per read without this provider keeping a table of them.</summary>
internal static class TerminalLogContract
{
    /// <summary>The fact kind this row carries. The binding names it, the native callbacks spell it, and the
    /// capability is looked up from it, so the three cannot drift apart.</summary>
    public const string Fact = "terminal_log_read";

    /// <summary>The binding id, under this provider and beside its other interaction rows.</summary>
    public static string BindingId => ModuleDefinition.ProviderId + ".binding.interaction." + Fact;

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.terminal_log_read", Blocks.TriggerInteractionTerminalLogRead.Id,"gtfo.map_object.terminal_log_read", "observe", new[] { "gtfo.map_object.read" })
    };
}
