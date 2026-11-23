using System;
using System.Linq;

namespace ForgeEngine.Framework;

/// <summary>The control vocabulary, owned by the kernel: `forge.control.flow.*` is native and versioned
/// the same way as <see cref="CombatContracts"/>. The kernel dispatches `control`-kind steps internally — it reads
/// their declared inputs and routes to the successor table in output order (see RuntimeKernel.AdvanceCore) — so it
/// never looks a control binding up in <c>RuntimeRegistry.Handlers</c> and no handler function is supplied here.
/// <see cref="RuntimeRegistry.WithModule"/> only requires a supplied handler for `action`-kind capabilities bound
/// with role `execute`; a `control`-kind capability bound the same way is exempt, but every implemented binding —
/// `control` included — still needs exactly one <see cref="BindingSupport"/> row.
/// The declared port shapes are the contract the plan loader re-derives every control step against: `next` is the
/// exit, `pulse`/`body` is the region the next activation starts from, and the value outputs are what a later step
/// reads through `fromStepSlot`. The rows are the generated block table's (<see cref="Blocks"/>): branch, sequence,
/// delay, repeat (counted, or periodic when it has an interval), for-each (over candidates or over positions), the
/// enum fan-out (`enum_switch`, whose exits are `case_1`..`case_N` then `otherwise`), `restart` (re-arms a live
/// timer from now and keeps its handle), `cancel`, which the end-of-flow node is the landing point for, and
/// `present`, the when-present guard of rule 142.3 whose one value port is the author's own class. `ramp` is the one
/// row whose single execution output `next` is entered by its own schedule rather than by the step's entry, which is
/// what "walks a value over a duration" means; see <see cref="RampEnvelope"/>. `group` is the action group (contract
/// §3.2 I-PLAN): it joins the lines its `body` starts, bounds them by a time limit, re-runs them on failure, caps how
/// many groups of one card run at once, and its handle is the one a `cancel` ends the whole group with — see
/// RuntimeKernel.Groups. A control outside the table is refused by id with `control-unsupported`.</summary>
internal static class ControlContracts
{
    private const string ProviderId = "forge.contract.control";

    /// <summary>Every row of the provider, each bound to the kernel step its id ends with: `forge.control.flow.branch`
    /// is bound as `forge.contract.control.binding.branch` and walked as `runtime.control.branch`.</summary>
    public static RuntimeModule Module()
    {
        var declaration = PrimitiveContracts.Declaration(ProviderId);
        return declaration.Bind(declaration.Capabilities.Select(row =>
        {
            var id = RuntimeJson.Text(row, "id");
            var kind = id[(id.LastIndexOf('.') + 1)..];
            return new ProviderBinding(ProviderId + ".binding." + kind, id, "runtime.control." + kind, "execute", Array.Empty<string>());
        }).ToArray()).Module(RuntimeApi.Fingerprint);
    }
}
