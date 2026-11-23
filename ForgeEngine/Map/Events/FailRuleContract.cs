using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The session-scope row about this expedition's failure rule: the outcome `forge.action.session.fail_rule` that
/// records which failure policy is in force.
///
/// <b>Where the rule lives.</b> The game evaluates its own failure verdict every frame and has no writable rule
/// state to set: `WardenObjectiveManager.CheckExpeditionFailed()` is a static, immediate answer, and the
/// third-party failure switches belong to that mod's own classes, which are never a landing (§4.6). The rule is
/// therefore held by this package — the mirrored state §4.6 allows — and enforced at the game's own decision
/// point: the hook this half installs gives the native verdict to the ledger and takes it back, so the failure
/// itself still runs through the game's own path (the countdown and the session command behind
/// `GS_InLevel.Update`) and never through a manufactured end state. The ledger is keyed by world epoch and is
/// cleared when the world changes, so a rule of a level that is gone can never answer for the level standing now.
///
/// <b>Which subject each policy reads.</b> The reviewed row's recipient port is `subject`, an `entity many` port,
/// and each policy reads it the way its own semantics require rather than as one shared filter:
/// <list type="bullet">
/// <item>`team-wipe` — whether the whole team being down still fails the expedition. The reviewed verdict is the
/// game's own team check, so the named subjects are the team the rule is declared for and not a filter over it,
/// exactly as the weapon switch rows treat their own `subject`; `enabled: false` suppresses the native verdict
/// and `enabled: true` restores it.</item>
/// <item>`any-player-down` — the named subjects are the watched players, and the rule forces a failure while any
/// one of them is down.</item>
/// <item>`bound-group-wiped` — the named subjects are the bound group, and the rule forces a failure while every
/// one of them is down.</item>
/// </list>
/// A player counts as down when the identity's own read says the locomotion machine is in the downed state or
/// the life is no longer alive: a life that ended cannot be revived, so a wipe that leaves it out would clear a
/// level the team has already lost. A subject the world can no longer read forces nothing — the rule waits rather
/// than inventing a failure from a life that is not there.
///
/// <b>What is refused.</b> A request names a subject that is not a player (`unsupported-recipient`), a subject of
/// another world (`stale-entity`), or a subject this world does not hold (`fail-rule-subject-missing`), and each
/// is refused by name before anything is written. An empty subject set never reaches the handler: the kernel
/// answers it as no targets.
/// </summary>
internal static class FailRuleContract
{
    // ---- forge.action.session.fail_rule ----------------------------------------------------------------

    public const string BindingId = ModuleDefinition.ProviderId + ".binding.session.fail_rule";
    public const string HandlerName = "gtfo.map.session.fail_rule";

    /// <summary>The policy members, spelled once: the reviewed parameter's own list and every reader of the ledger
    /// name these constants, so a literal that drifts from the declaration is impossible.</summary>
    public const string TeamWipePolicy = "team-wipe";
    public const string AnyPlayerDownPolicy = "any-player-down";
    public const string BoundGroupWipedPolicy = "bound-group-wiped";

    /// <summary>The three policies the reviewed parameter declares, in its own order. The handler reads the
    /// member the kernel resolved before it ran and refuses a name outside this list rather than mapping it to
    /// the nearest policy.</summary>
    public static readonly string[] Policies = { TeamWipePolicy, AnyPlayerDownPolicy, BoundGroupWipedPolicy };

    /// <summary>The permission the row's own recipient contract names (`recipients.requires`): the failure rule
    /// is session state, so the plan declares the session's failure surface and not a map object's.</summary>
    public const string Permission = "session.fail";

    /// <summary>The ports the handler reads: the subject set, the switch, and the row set it writes. The
    /// reviewed row declares the policy as a structural parameter, so the handler reads it from the parameters
    /// rather than from an input.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("subject", "enabled").Outputs("result").Parameters("policy");
    public static readonly HandlerSlot Subject = Shape.InputSlot("subject");
    public static readonly HandlerSlot Enabled = Shape.InputSlot("enabled");
    public static readonly HandlerSlot Policy = Shape.ParameterSlot("policy");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionSessionFailRule.Id, HandlerName, "execute", new[] { Permission }, Shape)
    };
}
