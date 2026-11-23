namespace ForgeEngine.Framework;

using System;

/// <summary>
/// The row the kernel itself answers for, and the status vocabulary it shares with the effect table.
///
/// A declared status is a write the kernel already owns: the action card's `effect` block names the status kind it
/// writes, the kernel casts the handle, keeps the clock and the layers, and calls the module back when the instance
/// ends. The ending is therefore the kernel's own to answer, and it is not a second effect system:
///
///   * `forge.trigger.status.ended` states that one declared status instance of one receiver ended, with the reason
///     it ended — `expired`, `cancelled`, `dispelled` or `reclaimed`. The kernel publishes it from the one place an
///     instance ends, so every binding of that row in the registry hears about every ending, whichever module
///     registered the binding. Nobody registers it yet: the row is a proposed Primitive, and a module may only
///     register a row the contract has reviewed.
///   * <see cref="DispelCapability"/> names no registered row: the catalog carries no dispel card, so no plan can
///     place one. The kernel's own dispatch branch still reads the id and is left to the kernel's owner to remove
///     together with <see cref="IsDispel"/> and <see cref="NotDispellable"/>.
///
/// The removal policy is not read from the catalog here: the compiler resolves the status profile's own declaration
/// into the step's `effect` block, exactly as it writes the other card defaults (plan §3.4 统一选项: 编译器按卡种写
/// 默认值，运行时不猜), and the kernel applies what the step declares.
/// </summary>
internal static class StatusContract
{
    /// <summary>The receiver-side fact that a declared status ended.</summary>
    public const string EndedCapability = Blocks.TriggerStatusEnded.Id;

    /// <summary>The action that ends dispellable instances of one declared status.</summary>
    public const string DispelCapability = "forge.action.status.dispel";

    /// <summary>The two members of the shared `status_removal` set, as the catalog declares them.</summary>
    public const string RemovalDispellable = "dispellable";
    public const string RemovalUntilExpiry = "until-expiry";

    /// <summary>The `status_kind` set's own name, which is what the step's status input is declared against.</summary>
    public const string StatusKindSchema = "status_kind";

    /// <summary>The `status_end_reason` set the ending's `reason` port carries: the four <see cref="EffectEndReasons"/>.</summary>
    public const string EndReasonSchema = "status_end_reason";

    /// <summary>The row's own refusal, exactly as its result port declares it: the recipients' status was not
    /// dispelled — nothing of that kind is on the receiver, or what is there may only end at its own expiry. One
    /// code, because the row answers one question and a code the row does not declare is one no reader can show.</summary>
    public const string NotDispellable = "status-not-dispellable";

    /// <summary>Whether one capability id is the kernel-dispatched dispel row.</summary>
    internal static bool IsDispel(string capabilityId) => capabilityId == DispelCapability;

    /// <summary>
    /// The compiled member index one member of <paramref name="schema"/> (a status kind or an end reason) is written
    /// as. A payload carries the index of its set and never the member name (Q3), so the fact this kernel publishes
    /// is the representation a compiler writes and a step's own enum port expects; a name the shared set does not
    /// carry answers -1, which is a writer's silence rather than a fact no reader could decode.
    /// </summary>
    internal static int IndexOf(string schema, string member)
    {
        var members = RuntimeEnumSets.Members(schema);
        for (var index = 0; index < members.Count; index++)
            if (string.Equals(members[index], member, StringComparison.Ordinal)) return index;
        return -1;
    }
}
