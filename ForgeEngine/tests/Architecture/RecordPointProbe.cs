using System.Reflection;
using System.Reflection.Emit;
using ForgeEngine.Framework;

/// <summary>I-DIAG call-site rules for the kernel, checked on compiled IL: the record type carries no composed
/// message and no inputs payload, the event-code constants are exactly the codes the kernel writes, and no record-point
/// helper composes text. HostIntegration's Cecil probe additionally follows every record text field back to its producer
/// and covers the compiled host; this probe is the part that needs no BepInEx reference and so runs beside the domain
/// assembly boundary checks.</summary>
internal static class RecordPointProbe
{
    private static readonly string[] ExpectedCodes = { "log.dropped", "log.level", "plan.loaded", "plan.rejected",
        "registration.rejected", "binding.registered", "world.began", "trigger.fired", "dispatch.started", "dispatch.finished",
        "event.rejected", "budget.exceeded", "event.deferred", "event.cancelled", "entry.started", "entry.finished",
        "step.started", "step.finished", "entry.stopped", "gate.refused", "observer.failed", "runtime.suspended", "map.layout-generated",
        "runtime.tick_rate", "runtime.tick-rate-mismatch" };

    internal static void Run(Action<bool, string> check)
    {
        var properties = typeof(RuntimeLogRecord).GetProperties().Select(p => p.Name).ToArray();
        check(!properties.Contains("Message") && !properties.Contains("Inputs")
            && !properties.Any(name => name.Contains("Player", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Steam", StringComparison.OrdinalIgnoreCase)),
            "the I-DIAG record grew a composed message, an inputs payload or a player identity field");
        var declared = typeof(RuntimeLogCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToArray();
        check(declared.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(ExpectedCodes.OrderBy(c => c, StringComparer.Ordinal)),
            "the event code constants and the code table drifted: " + string.Join(",", declared.Except(ExpectedCodes)));
        check(declared.All(c => !c.StartsWith("adapter.", StringComparison.Ordinal)),
            "adapter.* codes were declared before I-ADAPTER-SCHEMA rules them");
        check(RuntimeLogReasonCodes.InvalidHandlerResult == "invalid-handler-result"
            && RuntimeLogReasonCodes.LifecycleObserverFailed == "lifecycle-observer-failed", "kernel reason code value");
        check(Enum.GetValues<RuntimeLogLevel>().Select(l => (int)l).SequenceEqual(new[] { 0, 1, 2, 3 }),
            "the level vocabulary order no longer supports a single integer comparison");
        var recordPoints = KernelTypes.SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(m => m.GetMethodBody() is { } body
                && body.LocalVariables.Any(v => v.LocalType == typeof(RuntimeLogRecord)))
            .ToArray();
        check(recordPoints.Length >= 12, "the record-point walk found too few helpers: " + recordPoints.Length);
        var composed = recordPoints.SelectMany(m => BuildsText(m).Select(text => m.Name + "->" + text)).ToArray();
        check(composed.Length == 0, "a kernel record point composes text: " + string.Join(", ", composed));
        // Only step.finished reports a world commit outcome. Counted domain refusals explicitly carry none.
        var commitSites = recordPoints.Where(m => CommitStates(m) && m.Name != "LogStepFinished").Select(m => m.Name).ToArray();
        check(commitSites.Length == 0, "a record point other than step.finished set a world commit outcome: " + string.Join(", ", commitSites));

        // Behavior-editor trace coverage is architectural, not a convention at call sites. The main dispatch walk
        // owns trigger/entry boundaries and the common step boundary used by action/control/presentation/owner;
        // EvaluateStep owns the lazily evaluated pure/query nodes. If either route loses a paired record point,
        // ForgeDevelopment would silently lose a class of nodes.
        var advance = typeof(RuntimeKernel).GetMethod("AdvanceCore", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RuntimeKernel).FullName, "AdvanceCore");
        var advanceCalls = ReachableKernelCalls(advance);
        foreach (var name in new[] { "LogDispatchStarted", "LogDispatchFinished", "LogEntryStarted", "LogEntryFinished", "LogStepStarted", "LogStepFinished" })
            check(advanceCalls.Contains(name), "Advance no longer reaches behavior trace boundary " + name);
        var evaluate = typeof(RuntimeKernel).GetMethod("EvaluateStep", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RuntimeKernel).FullName, "EvaluateStep");
        var evaluateCalls = ReachableKernelCalls(evaluate);
        check(evaluateCalls.Contains("LogStepStarted") && evaluateCalls.Contains("LogStepFinished"),
            "pure/query evaluation no longer emits paired behavior-node trace boundaries");
    }

    // The kernel is the facade and the component classes it holds (KernelLog, KernelRegistry, ...): every top-level
    // Kernel* class beside it, so a component added later is walked without being listed here.
    private static readonly HashSet<Type> KernelTypes = typeof(RuntimeKernel).Assembly.GetTypes()
        .Where(type => type == typeof(RuntimeKernel) || (!type.IsNested && type.Namespace == typeof(RuntimeKernel).Namespace
            && type.Name.StartsWith("Kernel", StringComparison.Ordinal))).ToHashSet();

    // The dispatch walk can span several private stages. Follow only calls declared on the kernel's classes, and visit
    // each method once so recursive control/evaluation paths cannot turn this architecture check into a loop.
    private static HashSet<string> ReachableKernelCalls(MethodInfo root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<MethodInfo>();
        var pending = new Stack<MethodInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var source = pending.Pop();
            if (!visited.Add(source)) continue;
            var text = source.GetMethodBody()?.GetILAsByteArray();
            if (text == null) continue;
            for (var i = 0; i < text.Length;)
            {
                var opcode = OpCode(text, i);
                var operand = i + opcode.Size;
                var size = OperandBytes(opcode, text, operand);
                if (opcode.OperandType == OperandType.InlineMethod && operand + 4 <= text.Length
                    && Resolve(source, BitConverter.ToInt32(text, operand)) is MethodInfo target
                    && target.DeclaringType is { } owner && KernelTypes.Contains(owner))
                {
                    names.Add(target.Name);
                    if (!visited.Contains(target)) pending.Push(target);
                }
                i = operand + size;
            }
        }
        return names;
    }

    /// <summary>A record point sets commit when its IL stores a commit into the result it builds. The record types are
    /// readonly structs, so their object initializers call the setter directly rather than virtually; a <c>ldnull</c>
    /// reaching that setter stores no commit at all, which is why the walk reads the instruction before the call.</summary>
    private static bool CommitStates(MethodInfo method)
    {
        var text = method.GetMethodBody()?.GetILAsByteArray();
        if (text == null) return false;
        var previous = OpCodes.Ldnull;
        string? previousText = null;
        for (var i = 0; i < text.Length;)
        {
            var opcode = OpCode(text, i);
            var operand = i + opcode.Size;
            if (opcode.OperandType == OperandType.InlineMethod)
            {
                var target = Resolve(method, BitConverter.ToInt32(text, operand));
                if (target is { Name: "set_Commit", DeclaringType: { } owner } && owner == typeof(RuntimeLogResult)
                    && previous != OpCodes.Ldnull
                    && !(method.Name == "WriteDomainLog" && previous == OpCodes.Ldstr
                        && previousText == ForgeEngine.Framework.CommitStates.None)) return true;
            }
            previous = opcode;
            previousText = opcode == OpCodes.Ldstr ? method.Module.ResolveString(BitConverter.ToInt32(text, operand)) : null;
            i = operand + OperandBytes(opcode, text, operand);
        }
        return false;
    }

    /// <summary>The members that turn values into one string. A record point reaching any of them composes text, which is
    /// the whole rule: the record carries fields and the writer renders the message. Comparisons, predicates and probes
    /// (<c>op_Equality</c>, <c>IsNullOrEmpty</c>, <c>get_Length</c>, ...) read a string without building one, so they are
    /// not on the list, and an interpolated string builds its text in <c>DefaultInterpolatedStringHandler</c>.</summary>
    private static readonly string[] TextBuilders =
    {
        "System.String::Concat", "System.String::Format", "System.String::Join", "System.String::Copy",
        "System.Text.StringBuilder::.ctor", "System.Text.StringBuilder::Append", "System.Text.StringBuilder::AppendFormat",
        "System.Text.StringBuilder::AppendLine", "System.Text.StringBuilder::Insert", "System.Text.StringBuilder::Replace",
        "System.Text.StringBuilder::ToString", "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::.ctor",
        "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::AppendLiteral",
        "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::AppendFormatted",
        "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::ToStringAndClear",
        "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::ToString",
    };

    /// <summary>Walks the whole method body: a helper that composed text would have to reach one of the composition members,
    /// and the walk reports every such call it finds. The walk reads instruction widths off <see cref="OpCodes"/> rather
    /// than a hand-written table, because one opcode read at the wrong width desyncs every instruction after it and turns
    /// whatever bytes land on a token boundary into a phantom call.</summary>
    internal static string[] BuildsText(MethodInfo method)
    {
        var text = method.GetMethodBody()?.GetILAsByteArray();
        if (text == null) return Array.Empty<string>();
        var violations = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var opcode = OpCode(text, i);
            var operand = i + opcode.Size;
            var size = OperandBytes(opcode, text, operand);
            var call = opcode.OperandType == OperandType.InlineMethod && operand + 4 <= text.Length;
            var token = call ? BitConverter.ToInt32(text, operand) : 0;
            i = operand + size;
            if (!call || token == 0) continue;
            var target = Resolve(method, token);
            if (target != null && TextBuilders.Contains(target.DeclaringType?.FullName + "::" + target.Name, StringComparer.Ordinal))
                violations.Add(target.DeclaringType!.FullName!);
        }
        return violations.ToArray();
    }

    // A call token inside a generic method or type names members through that method's own type parameters (a generic
    // record point invoking its `Func<TSource, …>`), so it resolves only against the method's generic context.
    private static MethodBase? Resolve(MethodInfo method, int token)
        => method.Module.ResolveMethod(token,
            method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null,
            method.IsGenericMethod ? method.GetGenericArguments() : null);

    // The opcode behind the byte at <paramref name="at"/>, or the two-byte opcode it introduces with the next byte. The
    // runtime's own field table is the source of the encoding, so the walk cannot drift from it; every byte of a compiled
    // body is an opcode that table holds, and a byte outside it is a bug in this walk rather than a body to guess at.
    private static OpCode OpCode(byte[] text, int at)
    {
        var full = text[at] == 0xFE ? 0xFE00 | text[at + 1] : text[at];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            // OpCode.Value is signed, so a two-byte opcode reads back negative while the encoding here is unsigned.
            var candidate = (OpCode)field.GetValue(null)!;
            if ((ushort)candidate.Value == full) return candidate;
        }
        throw new InvalidOperationException("IL byte 0x" + full.ToString("x4") + " is not an OpCodes opcode.");
    }

    // ECMA-335 instruction operands are a metadata or call-site token, a four-byte branch target, a byte, an eight-byte
    // integer, a four-byte float, a switch table or nothing at all.
    private static int OperandBytes(OpCode opcode, byte[] text, int operandAt) => opcode.OperandType switch
    {
        OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok
            or OperandType.InlineString or OperandType.InlineSig or OperandType.InlineBrTarget
            or OperandType.InlineI or OperandType.ShortInlineR => 4,
        OperandType.InlineSwitch => operandAt + 4 <= text.Length ? 4 + 4 * BitConverter.ToInt32(text, operandAt) : 4,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 0,
    };
}
