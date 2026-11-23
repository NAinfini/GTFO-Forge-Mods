using System;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The `forge.action.map.terminal_content` row: the running terminal's own content, which the EOS
/// security-door-terminal feature's stopped row folds into (`terminal_override_command`, rulings R1/R2). One
/// capability adds or removes a command on a terminal the level already built, and adds, removes or hides one of
/// its local log files.
///
/// The row writes the terminal's own command interpreter and log table rather than a table of this package's, so
/// a command it adds is typed, completed and answered exactly like a vanilla one and travels to the other
/// machines as far as the terminal replicates it: the game replicates a command's visibility, but not its name,
/// help line or a local log's text. Those travel in the package's own terminal-content record, which the host writes
/// and every machine applies to its own terminal (a late joiner, a reconnect and a checkpoint restore included).
/// That is also why the row is a `host` row: the host is the machine that writes the record.
///
/// The command's own help line is the row's `text` input, and its code is `number`. The code is the interpreter's
/// own `TERM_Command` member rather than a free string, because that enum is what the interpreter looks a command
/// up by; a request naming a member the enum does not carry is refused before anything is written.
///
/// The EOS feature's four access settings (`accessible_when_locked`, `accessible_when_unlocked`, `accessibility`
/// and `on_puzzle_solved`) are deliberately **not** parameters here. Whether a terminal may be used at all is the
/// interaction-state row's business, and when a door's own puzzle grants the command is the door's own field: a
/// request that wants "the override becomes available once the door is unlocked" writes those two rows, and the
/// command row only decides what the command is.</summary>
internal static class TerminalContentContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.map.terminal_content";
    public const string HandlerName = "gtfo.map.terminal_content";

    // ---- the content read (forge.query.map.terminal_content) -----------------------------------------

    /// <summary>The read of the two tables the row above writes: whether a terminal already holds a command or a
    /// log, and whether that content sits on the list a player reads. It is declared beside the write because one
    /// native member family is spelled once: both halves reach the interpreter's own command table and the
    /// terminal's own local log table, and splitting them would let two files disagree about which table a
    /// content read is about.</summary>
    public const string QueryHandlerName = "gtfo.map.terminal_content_query";

    public static readonly string QueryBindingId = ModuleDefinition.ProviderId + ".binding.query.map.terminal_content";

    /// <summary>The read's ports: the terminal and the name or body text to look up, with the structural `kind`
    /// that says which of the terminal's two tables the question is about.</summary>
    public static readonly HandlerShape QueryShape = new HandlerShape()
        .Inputs("terminal", "text").Outputs("present", "visible").Parameters("kind");

    /// <summary>The refusal an input that is not a terminal gets.</summary>
    public const string QueryInputKindCode = "terminal-content-query-input-kind";
    /// <summary>The refusal a map-object reference whose address is not a terminal address gets.</summary>
    public const string QueryInputAddressCode = "terminal-content-query-input-address";
    /// <summary>The refusal a terminal this world no longer holds gets.</summary>
    public const string QueryInputStaleCode = "terminal-content-query-input-stale";
    /// <summary>The refusal a request whose structural `kind` is absent or names neither of the row's two
    /// members gets. Which table the question is about is the request's own decision, never a guess.</summary>
    public const string QueryKindCode = "terminal-content-query-kind";

    /// <summary>One content reading, as the terminal's own two tables answer it: whether the name is held, and
    /// whether it sits on the list a player reads. For a command the second port is "not hidden"; for a log it is
    /// the terminal's own visibility flag.</summary>
    public readonly record struct TerminalContentReading(bool Present, bool Visible);

    /// <summary>The one native read this row needs: the terminal a reference names, the `kind` member that chose
    /// the table, the text to look up, and the two answers. Null means this world does not answer for the
    /// reference; a terminal that is not there is not a terminal that holds nothing.</summary>
    public sealed record TerminalContentReader(Func<EntityReference, string, string, TerminalContentReading?> Read);

    /// <summary>The evaluator, keyed by handler name: the reference is refused by name unless it names a terminal
    /// of this provider's namespace, the `kind` parameter must be one of the row's two members, and the text is
    /// required — an absent name answered as "not present" is exactly the answer a terminal that really does not
    /// hold it gives.</summary>
    public static EvaluatorHandler QueryEvaluator(TerminalContentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (context, outputs) =>
        {
            var terminal = Input(context, "terminal");
            if (!EntityKinds.Is(terminal, EntityKinds.MapObject))
                throw new RuntimeContractException(QueryInputKindCode,
                    "This provider reads the content of a map object, not of " + terminal.Id);
            if (MapObjectTerminalAddress.TryParse(terminal.Id[(EntityKinds.MapObject.Length + 1)..]) == null)
                throw new RuntimeContractException(QueryInputAddressCode, "The input is not a terminal address: " + terminal.Id);
            string? kind = Text(context.Parameters, "kind");
            if (kind == null || Array.IndexOf(Kinds, kind) < 0)
                throw new RuntimeContractException(QueryKindCode, "The read names no content kind this row carries.");
            string? text = Text(context.Inputs, "text");
            if (text == null)
                throw new RuntimeContractException("missing-field", "text");
            var reading = reader.Read(terminal, kind, text)
                ?? throw new RuntimeContractException(QueryInputStaleCode, "The terminal is not current: " + terminal.Id);
            outputs.Boolean(0, reading.Present); outputs.Boolean(1, reading.Visible);
        };
    }

    /// <summary>The read's one required input, refused by name when the plan left it out.</summary>
    private static EntityReference Input(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
            ? RuntimeJson.Entity(value)
            : throw new RuntimeContractException("missing-field", port);

    /// <summary>One member of a JSON bag, or null when it is absent or not a string.</summary>
    private static string? Text(System.Text.Json.JsonElement bag, string id)
        => bag.ValueKind == System.Text.Json.JsonValueKind.Object && bag.TryGetProperty(id, out var value)
           && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

    /// <summary>The permission the row's own write needs: it changes what a terminal offers, so a plan declares
    /// that it may write a terminal's content before it can.</summary>
    public const string Permission = "terminal.content";

    /// <summary>What kind of content one request edits. A command and a log file are different natives behind
    /// different members, so the request has to say which one it means rather than being guessed at from the
    /// members it happens to carry.</summary>
    public static readonly string[] Kinds = { "command", "log" };

    /// <summary>The four edits: add and remove a command, add, remove, show and hide a log file. `remove` and
    /// `hide` are different natives — one takes the entry out of the terminal's log table, the other only takes it
    /// off the list a player reads — so both are offered.</summary>
    public static readonly string[] Operations = { "add", "remove", "show", "hide" };

    /// <summary>The interpreter's own three rules for what happens to a command once it has been used, in the
    /// generic spelling of what they do.</summary>
    public static readonly string[] Rules = { "normal", "only_once", "only_once_delete" };

    public static HandlerShape Shape { get; } = new HandlerShape()
        .Inputs("terminals", "text").Outputs("result")
        .Parameters("kind", "operation", "slot", "name", "rule", "sound");
    public static readonly HandlerSlot Terminals = Shape.InputSlot("terminals");
    public static readonly HandlerSlot ContentText = Shape.InputSlot("text");
    public static readonly HandlerSlot ContentKind = Shape.ParameterSlot("kind");
    public static readonly HandlerSlot Operation = Shape.ParameterSlot("operation");
    public static readonly HandlerSlot Slot = Shape.ParameterSlot("slot");
    public static readonly HandlerSlot Name = Shape.ParameterSlot("name");
    public static readonly HandlerSlot Rule = Shape.ParameterSlot("rule");
    public static readonly HandlerSlot Sound = Shape.ParameterSlot("sound");

    /// <summary>The permission the content read needs: the map-object namespace's own read permission, spelled
    /// on its own here so this contract compiles without the map-object family's file beside it, exactly as the
    /// terminal command read beside it spells the same value.</summary>
    public const string QueryReadPermission = "gtfo.map_object.read";

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionMapTerminalContent.Id, HandlerName, "execute", new[] { Permission }, Shape),
        new ProviderBinding("forge.module.gtfo.map.binding.query.map.terminal_content", Blocks.QueryMapTerminalContent.Id, QueryHandlerName, "evaluate", new[] { QueryReadPermission }, QueryShape)
    };
}
