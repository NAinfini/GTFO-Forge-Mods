using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>Propagates the owning package's authoritative refusal before resource resolution.
/// It grants no admission and keeps the first cause, rather than replacing it with a missing sidecar. Startup refuses into the
/// standing set; every later Map commit (contract I-RELOAD) collects its own set between <see cref="BeginGeneration"/> and
/// <see cref="EndGeneration"/>, which replaces the standing set only when that generation stands.</summary>
internal static class RundownPlanAdmission
{
    private sealed class State
    {
        internal Dictionary<string, string> Standing = new(StringComparer.Ordinal);
        internal Dictionary<string, string>? Collecting;
    }
    private static readonly ConditionalWeakTable<RuntimeKernel, State> States = new();

    public static void Refuse(IForgeEngine runtime, string packageRoot, string code, string detail)
    {
        if (runtime is not RuntimeKernel kernel) throw new InvalidOperationException("Rundown plans require the live Runtime host.");
        var state = States.GetValue(kernel, _ => new State());
        // Outside a later generation's Map commit only startup may refuse.
        if (state.Collecting == null) kernel.RequireContentHostAccess(registering: kernel.IsRegistrationOpen);
        RuntimeJson.Text(packageRoot); RuntimeJson.Text(code); ArgumentNullException.ThrowIfNull(detail);
        var message = "Owning Rundown rejected (" + code + "): " + detail;
        // The existing plan diagnostic contract permits 256 trimmed characters, without controls. Bound native
        // errors visibly so a long path or multiline exception cannot suppress the owning package's refusal.
        if (message.Length > 256) message = message[..255] + "…";
        message = string.Create(message.Length, message, static (span, source) =>
        {
            for (int index = 0; index < source.Length; index++)
                span[index] = source[index] <= 31 || source[index] == 127 ? ' ' : source[index];
        }).Trim();
        (state.Collecting ?? state.Standing).TryAdd(packageRoot, message);
    }

    /// <summary>A later generation's Map commit begins: its refusals collect apart from the standing ones. A redo writes the same
    /// input again and starts from the standing refusals (a package refused once is not reported a second time).</summary>
    internal static void BeginGeneration(RuntimeKernel kernel, bool redo)
    {
        var state = States.GetValue(kernel, _ => new State());
        state.Collecting = redo ? new Dictionary<string, string>(state.Standing, StringComparer.Ordinal) : new(StringComparer.Ordinal);
    }

    /// <summary>The Map commit's generation stood (its refusals become the standing set) or did not (they are dropped).</summary>
    internal static void EndGeneration(RuntimeKernel kernel, bool stood)
    {
        if (!States.TryGetValue(kernel, out var state) || state.Collecting == null) return;
        if (stood) state.Standing = state.Collecting;
        state.Collecting = null;
    }

    public static IReadOnlyList<PlanCandidate> Check(RuntimeKernel kernel, IReadOnlyList<PlanCandidate> candidates)
    {
        // Reads the standing set. Plan adoption waits for the generation's dispatch boundary, so it never sees a set being collected.
        if (!States.TryGetValue(kernel, out var state) || state.Standing.Count == 0) return candidates;
        var refusals = state.Standing;
        var checkedPlans = new List<PlanCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            int separator = candidate.Path.IndexOf("/forge/plans/", StringComparison.Ordinal);
            // Discovery's own IO/path/size refusal still identifies a file that could not be read.
            if (!candidate.IsHostRejected && separator > 0
                && refusals.TryGetValue(candidate.Path[..separator], out var detail))
                checkedPlans.Add(PlanCandidate.Rejected(candidate.Path, "content-invalid", detail));
            else checkedPlans.Add(candidate);
        }
        return checkedPlans;
    }
}
