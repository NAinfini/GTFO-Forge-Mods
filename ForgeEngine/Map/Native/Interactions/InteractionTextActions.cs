using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using HarmonyLib;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The native half of the interaction-prompt row (`forge.action.presentation.interaction_text`): the one action
/// that writes one object's prompt rule, and the readback the interaction prompt goes through.
///
/// The rule is held per map object and applied where the game asks for the prompt, not by rewriting any text the
/// game owns: `Interact_ComputerTerminal.InteractionMessage` is the terminal's own override of the getter the
/// interaction layer draws, and it remains the only member patched here.
///
/// Authored hold points use the same rules through their existing native prompt setter. Doors are refused.
/// A door button answers with `Interact_Timed.get_InteractionMessage`, the first declaration of
/// the property, and its compiled body is a plain field read that identical-code folding shares with 31 unrelated
/// getters (`get_CourseNode`, `get_sprite`, ...) at 0x3A5F30. A native patch sits on that address and would run for
/// every one of them, so until a door prompt entry with an address of its own is evidenced, a door recipient is
/// refused with the recipient code instead of being told its prompt changed.
///
/// The rule table is keyed by the native component: a terminal's `m_terminal`, or an authored hold point's
/// `Interact_Timed`. The latter refreshes through its existing session and native prompt setter. An interactable
/// no rule owns keeps the game's own prompt; text resources are resolved using this client's Player provider.
///
/// The glitch is read off the game clock rather than animated by an update loop of this package's own:
/// the prompt's own redraws sample the rule's refresh interval, so no per-frame work is added here. If the prompt
/// turns out to be drawn once per selection instead of per frame, the styles would be static rather than moving —
/// that is the one part of this row a run in the game has to settle.
///
/// Nothing here writes object state: a prompt cannot unlock, open or alarm anything. The row is a presentation
/// row, so the host decides when the step runs and every addressed client writes its own copy of the rule; a
/// handler that is not on the host still writes, which is why this half never reads `IsHost`.</summary>
internal sealed class InteractionTextActions
{
    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;
    private readonly Func<InteractionPointSession?> _points;

    internal InteractionTextActions(IForgeEngine kernel, Func<bool> ready, Action<string> report,
        Func<InteractionPointSession?> points)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _points = points ?? throw new ArgumentNullException(nameof(points));
    }

    /// <summary>The rules in force, one per map object. Each rule names the session and complete recipient
    /// identity that admitted it, so an old world's rule cannot rewrite a later native prompt. The prompt patch
    /// reads this table and this action is its only writer; access is serialized with prompt readback.</summary>
    private static readonly object RulesGate = new();
    // The native pointer is captured while the component is live. Cleanup can then retire a rule after Unity has
    // collected its wrapper without invoking Unity equality or hashing on the dead object.
    private static readonly Dictionary<IntPtr, RuleEntry> Rules = new();
    // Lock-free mirrors of the table, rewritten under the gate at every mutation: the prompt patch fires per frame and
    // must be able to leave before it touches a native field, and the prompt refresh must know if any rule animates.
    private static volatile int _ruleCount;
    private static volatile bool _anyAnimated;

    /// <summary>Whether any rule in force is the animated (glitch) mode; false means no prompt changes by itself.</summary>
    internal static bool AnyAnimated => _anyAnimated;

    private static void SyncMirrors()
    {
        bool animated = false;
        foreach (var entry in Rules.Values)
            if (entry.Rule.Mode == InteractionTextMode.Glitch) { animated = true; break; }
        _ruleCount = Rules.Count;
        _anyAnimated = animated;
    }

    // The registered command handler owns the action's lifetime; a static prompt entry must not keep a released
    // session alive. A collected owner is treated exactly like any other expired overlay at readback.
    private sealed record RuleEntry(WeakReference<InteractionTextActions> Actions, EntityReference Target,
        InteractionTextRule Rule)
    {
        internal bool ReadFailureReported;
    }

    internal CommandResult Handle(CommandContext context) => Apply(context);

    /// <summary>The row's whole decision: the rule is read once for the command — a request this layer cannot
    /// carry out as asked must not half-apply — and then written per recipient, with a refusal code of its own for
    /// every recipient that could not be reached.</summary>
    internal CommandResult Apply(CommandContext context)
    {
        if (!Ready()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var text = context.Text(InteractionTextContract.Text);
        if (context.Present(InteractionTextContract.Text) && text == null)
            return Outcome.Rejected(ResultCode.InteractionTextTooLong);
        var refresh = context.Number(InteractionTextContract.RefreshInterval);
        if (context.Present(InteractionTextContract.RefreshInterval) && refresh == null)
            return Outcome.Rejected(ResultCode.InteractionTextRefreshInvalid);
        if (!InteractionText.TryRead(context.Text(InteractionTextContract.Mode),
                context.Text(InteractionTextContract.Style), refresh, text, _kernel.TicksPerSecond,
                out var rule, out var code))
            return Outcome.Rejected(code);
        try
        {
            if (!TryRender(rule, _kernel, "", out _, out code)) return Outcome.Rejected(code);
        }
        catch (RuntimeContractException error) { return Outcome.Rejected(error.Code); }
        int count = context.Count(InteractionTextContract.Targets);
        if (count < 0) return Outcome.Rejected(ResultCode.MissingField);
        if (count > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);
        for (int index = 0; index < count; index++)
            if (!context.TryEntity(InteractionTextContract.Targets, index, out _))
                return Outcome.Rejected(ResultCode.UnsupportedRecipient);

        // A presentation row: it commits no world state. Every row carries the text; the skeleton writes the count.
        var request = new TextRequest { Self = this, Rule = rule };
        return context.EachTargetPresented(ref request, TextStep, report: WriteFailed,
            countColumn: Blocks.ActionPresentationInteractionText.RowTargetCount);
    }

    private static readonly Action<EntityReference, Exception> WriteFailed =
        static (_, error) => MapLog.Contained("interaction-text.write", error);

    private struct TextRequest
    {
        internal InteractionTextActions Self;
        internal InteractionTextRule Rule;
    }

    private static readonly TargetStep<TextRequest> TextStep = TextOne;

    private static void TextOne(ref TextRequest request, EntityReference target, int index, RowWriter rows)
    {
        var self = request.Self;
        var rule = request.Rule;
        if (!self.Resolve(target, out var owner, out var refusal))
        {
            rows.Text(Blocks.ActionPresentationInteractionText.RowText, "").End(Status.Rejected, Commit.None, refusal);
            return;
        }
        self.Write(owner, target, rule);
        if (owner is Interact_Timed) self._points()?.RefreshPrompt(target);
        // A row whose prompt was written is `partial + none`: the local display ran and nothing was committed.
        rows.Text(Blocks.ActionPresentationInteractionText.RowText, rule.IsClear ? "" : rule.Text)
            .End(Status.Partial, Commit.None, rule.IsClear ? ResultCode.InteractionTextCleared : ResultCode.InteractionTextApplied);
    }
    /// <summary>Whether this machine may write a prompt at all. A prompt is drawn by the machine the player sits
    /// at, so every addressed client writes its own copy and the host fact is deliberately not part of this gate;
    /// what is left is the session's readiness and the runtime's own started world.</summary>
    private bool Ready()
        => _ready() && _kernel.Lifecycle.StartupState == RuntimeStartupState.Ready;

    /// <summary>Only current terminals and placed authored hold points have a safe native prompt entry here.
    /// A door or another unsupported recipient is refused rather than read through the wrong reader.</summary>
    private bool Resolve(EntityReference? reference, out Component owner, out string refusal)
    {
        owner = null!;
        if (reference == null) { refusal = ResultCode.UnsupportedRecipient; return false; }
        if (!_kernel.IsEntityCurrent(reference)) { refusal = ResultCode.StaleEntity; return false; }
        if (reference.Id.StartsWith(EntityKinds.MapObject + ":interaction_point/", StringComparison.Ordinal))
        {
            var address = reference.Id[(EntityKinds.MapObject.Length + 1)..];
            if (_points()?.InspectInteraction(address, reference.WorldEpoch) is not { } point)
            { refusal = ResultCode.StaleEntity; return false; }
            owner = point; refusal = ""; return true;
        }
        if (TerminalObjectActions.Address(reference) is { } terminalAddress)
        {
            if (TerminalObservation.ByAddress(terminalAddress) is not { } terminal)
            {
                refusal = ResultCode.StaleEntity;
                return false;
            }
            owner = terminal;
            refusal = "";
            return true;
        }
        refusal = ResultCode.UnsupportedRecipient;
        return false;
    }

    private void Write(Component owner, EntityReference target, InteractionTextRule rule)
    {
        var key = owner.Pointer;
        lock (RulesGate)
        {
            if (rule.IsClear) Rules.Remove(key);
            else Rules[key] = new RuleEntry(new WeakReference<InteractionTextActions>(this), target, rule);
            SyncMirrors();
        }
    }

    /// <summary>The prompt patch's one call: the rule in force for this terminal, if any, rewrites the text the
    /// game answered with.</summary>
    internal static void Rewrite(Interact_ComputerTerminal instance, ref string result)
    {
        if (_ruleCount == 0) return;
        try { Rewrite(instance.m_terminal, ref result); }
        catch (Exception error) { ReportUnknownReadFailure(error); }
    }

    internal static bool Animated(Component owner)
    {
        if (ReferenceEquals(owner, null) || owner.WasCollected) return false;
        lock (RulesGate) return Rules.TryGetValue(owner.Pointer, out var entry)
            && entry.Rule.Mode == InteractionTextMode.Glitch;
    }

    internal static void Remove(Component owner)
    {
        if (ReferenceEquals(owner, null) || owner.WasCollected) return;
        Remove(owner.Pointer);
    }

    internal static void Remove(IntPtr owner)
    {
        lock (RulesGate) { Rules.Remove(owner); SyncMirrors(); }
    }

    internal static bool TryRender(InteractionTextRule rule, IForgeEngine kernel, string original,
        out string rendered, out string code)
    {
        rendered = original; code = ResultCode.TextResourceMissing;
        if (!StationTextValue.TryRender(rule.Text, kernel, out var text)) return false;
        code = ResultCode.InteractionTextTooLong;
        if (text.Length > InteractionText.MaximumTextLength) return false;
        rendered = rule.Mode switch
        {
            InteractionTextMode.Replace => text,
            InteractionTextMode.Prefix => text + original,
            InteractionTextMode.Suffix => original + text,
            _ => rule.Render(original, Time.time)
        };
        code = ""; return true;
    }

    internal static void Rewrite(Component? owner, ref string result)
    {
        if (result == null || _ruleCount == 0) return;
        RuleEntry? entry = null;
        InteractionTextActions? actions = null;
        string replacement;
        try
        {
            // Owner acquisition and pointer lookup touch native interop. Keep both inside the same boundary as the
            // runtime reads; the managed table itself is keyed by the captured pointer.
            if (owner == null) return;
            var key = owner.Pointer;
            lock (RulesGate)
            {
                if (!Rules.TryGetValue(key, out entry)) return;
                if (!entry.Actions.TryGetTarget(out actions))
                {
                    Rules.Remove(key); SyncMirrors();
                    return;
                }
                if (!actions.Ready() || entry.Target.WorldEpoch != actions._kernel.WorldEpoch
                    || !actions._kernel.IsEntityCurrent(entry.Target))
                {
                    // Retire only this stale overlay. Never restore a captured string: the getter has already
                    // supplied the game's current text, including any native state or localization change.
                    Rules.Remove(key); SyncMirrors();
                    return;
                }
                if (!TryRender(entry.Rule, actions._kernel, result, out replacement, out var code))
                    throw new RuntimeContractException(code, "Interaction prompt text could not be resolved.");
            }
        }
        catch (Exception error)
        {
            if (entry is not null && actions is not null) actions.ReportReadFailure(entry, error);
            else ReportUnknownReadFailure(error);
            return;
        }
        // Commit the presentation result only after the entire observation succeeded.
        result = replacement;
    }

    private void ReportReadFailure(RuleEntry entry, Exception error)
    {
        try
        {
            lock (RulesGate)
            {
                if (entry.ReadFailureReported) return;
                entry.ReadFailureReported = true;
            }
            string reason = error is RuntimeContractException refusal ? refusal.Code : error.GetType().Name;
            _report("Interaction text for " + entry.Target.Id
                + " kept the native prompt because rule readback failed (" + reason + ").");
        }
        // A reporter must not turn a successful native getter into an exception; its failure is recorded instead.
        catch (Exception reporterError) { MapLog.Contained("interaction-text.read-failure-report", reporterError); }
    }

    private static void ReportUnknownReadFailure(Exception error) =>
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.InstanceRefused,
            "Native interaction prompt owner could not be observed; the original prompt was kept.",
            RuntimeLogValue.Quote("exception", error.GetType().Name));
}

/// <summary>The terminal's own override of the prompt getter the interaction layer draws.</summary>
[HarmonyPatch(typeof(Interact_ComputerTerminal), "get_InteractionMessage")]
internal static class TerminalInteractionTextPromptReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Interact_ComputerTerminal __instance, ref string __result)
        => InteractionTextActions.Rewrite(__instance, ref __result);
}
