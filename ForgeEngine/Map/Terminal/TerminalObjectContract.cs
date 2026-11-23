using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The two terminal action rows this provider implements for the one `gtfo.map_object` namespace: a
/// whitelisted command run through the terminal's own interpreter, and one localisable line printed onto a
/// terminal. This file holds their bindings and handler shapes; the capability rows are the generated block
/// table's.
///
/// Both rows are `execute` bindings inside this package rather than a second provider: the terminal is a map
/// object this package already addresses, its native entry points live in the same assembly as the door and
/// terminal observation, and a plan that pins either binding resolves against the Map provider's own registry.
///
/// A port the native side cannot honour is refused by name at execution time rather than declared as a promise
/// this package cannot keep.</summary>
internal static class TerminalObjectContract
{
    /// <summary>The permission the catalog's own recipient contract names for the command row. It is the whole
    /// permission a plan pinning this binding declares, because this binding requires no other row.</summary>
    public const string CommandPermission = "terminal.command";
    /// <summary>The permission the catalog's recipient contract names for the printed-line row.</summary>
    public const string OutputPermission = "terminal.output";

    public const string CommandBindingId = ModuleDefinition.ProviderId + ".binding.action.map.terminal_command";
    public const string OutputBindingId = ModuleDefinition.ProviderId + ".binding.action.map.terminal_output";
    public const string CommandHandlerName = "gtfo.map_object.terminal_command";
    public const string OutputHandlerName = "gtfo.map_object.terminal_output";

    /// <summary>The command row's own ports, in the catalog's order: the recipient collection, the command text
    /// and its argument string. `arguments` is a string because the row composes one input line for the terminal's own
    /// interpreter, which is what parses a typed line. The row runs the command and nothing else: whether a
    /// command appears on the terminal at all is `forge.action.map.terminal_content`'s show and hide.</summary>
    public static readonly HandlerShape CommandShape = new HandlerShape()
        .Inputs("terminals", "command", "arguments").Outputs("result");
    public static readonly HandlerSlot CommandTerminals = CommandShape.InputSlot("terminals");
    public static readonly HandlerSlot CommandText = CommandShape.InputSlot("command");
    public static readonly HandlerSlot CommandArguments = CommandShape.InputSlot("arguments");

    /// <summary>The printed-line row's ports: the same recipient collection, the two entries its text comes from —
    /// the literal a plan computed and the stored Text block its id entry names, which the compiler pairs so a
    /// node fills exactly one. The one structural parameter is the line's severity.</summary>
    public static readonly HandlerShape OutputShape = new HandlerShape()
        .Inputs("terminals", "text", "text_id").Outputs("result").Parameters("severity");
    public static readonly HandlerSlot OutputTerminals = OutputShape.InputSlot("terminals");
    public static readonly HandlerSlot OutputText = OutputShape.InputSlot("text");
    public static readonly HandlerSlot OutputTextId = OutputShape.InputSlot("text_id");
    public static readonly HandlerSlot OutputSeverity = OutputShape.ParameterSlot("severity");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(CommandBindingId, Blocks.ActionMapTerminalCommand.Id, CommandHandlerName, "execute", new[] { CommandPermission }, CommandShape),
        new ProviderBinding(OutputBindingId, Blocks.ActionMapTerminalOutput.Id, OutputHandlerName, "execute", new[] { OutputPermission }, OutputShape)
    };
}
