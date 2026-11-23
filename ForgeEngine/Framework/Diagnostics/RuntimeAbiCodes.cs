using System;

namespace ForgeEngine.Framework;

/// <summary>
/// The plan-ABI kernel codes the contract's I-DIAG section registers, in its own grouping and spelling. Every
/// rejection this runtime raises about the plan ABI is written from here, so one fact has one code and one
/// spelling: a caller reads the code, never a message. The result-row codes belong to the shared table as well —
/// the row writer is the only caller of those three, and `result-schema` is already raised here at registration.
/// </summary>
public static class RuntimeAbiCodes
{
    // 结果行
    public const string ResultRowBudget = "result-row-budget";
    public const string ResultRowStatus = "result-row-status";
    public const string ResultRowIncomplete = "result-row-incomplete";
    public const string ResultSchema = "result-schema";
    /// <summary>The explicitly selected result row does not exist in this action's written row set.</summary>
    public const string ResultIndexOutOfRange = "result-index-out-of-range";

    // 句柄与资源
    public const string HandleLiteral = "handle-literal";
    public const string HandleKind = "handle-kind";
    public const string HandleLifetime = "handle-lifetime";
    public const string StaleHandle = "stale-handle";
    public const string HandleBudget = "handle-budget";
    /// <summary>Retired: a resource is now a reference a plan compiles and resolves through its kind's owner
    /// (`stale-resource`) or a value an event payload carries, so no input is refused for being one. The code stays
    /// registered until the contract's own I-DIAG list drops it with the rest of this batch.</summary>
    public const string ResourceRuntimeValue = "resource-runtime-value";
    /// <summary>A plan's data input names an event: an event row is a dispatch identity carried by its own port,
    /// never a literal a plan compiles or a value one step hands to another.</summary>
    public const string EventLiteral = "event-literal";
    public const string StaleResource = "stale-resource";
    public const string ResourceKind = "resource-kind";
    /// <summary>A provider cannot answer for a resource reference: the id names nothing it currently owns. Raised
    /// where the reference is resolved, never where it is carried — a frame moves a resource like any other value.</summary>
    public const string ResourceUnavailable = "resource-unavailable";
    /// <summary>An event value does not match the port's envelope schema, or its row index is outside the
    /// dispatch's own event table.</summary>
    public const string EventPortKind = "event-port-kind";

    // 控制
    public const string ControlShape = "control-shape";
    public const string ControlUnsupported = "control-unsupported";
    public const string ControlBound = "control-bound";
    public const string IterationBudget = "iteration-budget";
    public const string DispatchStepBudget = "dispatch-step-budget";

    // 状态：内核自己回答的两行（见 StatusContract）
    /// <summary>A provider handler claimed the kernel-dispatched dispel action: the instances that action ends are
    /// the kernel's own records, so a provider body would be a second implementation of the same ending.</summary>
    public const string KernelActionBound = "kernel-action-bound";
    /// <summary>The dispel step does not carry the one status-kind input the kernel addresses an instance by.</summary>
    public const string StatusDispelShape = "status-dispel-shape";

    // 成本：动作步骤的 cost_policy/cost 参数组（见 RuntimeKernel.Cost）
    /// <summary>One item of a step's declared cost could not be charged: the whole step is refused and everything
    /// already reserved for it is released, so a step never runs partly paid.</summary>
    public const string InsufficientCost = "insufficient-cost";
    /// <summary>A step declares a cost of a kind whose owner keeps no balance to charge it against.</summary>
    public const string CostUnsupported = "cost-unsupported";
    /// <summary>A cost owner threw during commit; whether that item was charged is unknown.</summary>
    public const string CostCommitFailed = "cost-commit-failed";

    // 查询
    public const string QueryBudget = "query-budget";
    public const string QueryObserverUnavailable = "query-observer-unavailable";
    public const string QueryAuthority = "query-authority";
    public const string PureWorldPort = "pure-world-port";
    /// <summary>A `pure` capability declares a read: a declaration is a read even when no port carries one, and
    /// nothing about a pure evaluation may touch the world.</summary>
    public const string PureWorldRead = "pure-world-read";

    // 挂载
    public const string AttachmentEmpty = "attachment-empty";
    public const string AttachmentOrder = "attachment-order";
    public const string AttachmentDuplicate = "attachment-duplicate";
    public const string AttachmentKind = "attachment-kind";
    public const string AttachmentBudget = "attachment-budget";
    /// <summary>A player mount without a host output, a host output on any other mount, or an output no entry trigger of
    /// the plan reports an entity through: a mount whose subject could never arrive is refused at load, not left idle.</summary>
    public const string AttachmentPort = "attachment-port";

    // presentation
    /// <summary>A presentation step addresses no player: the recipient set is the step's own routing, and an
    /// empty one is a step that would present to nobody.</summary>
    public const string PresentationRecipient = "presentation-recipient";
    /// <summary>A presentation step's recipients are real players none of whom has a screen (every one a bot): the
    /// step is sent to nobody and finishes succeeded under this code, so the walk goes on and the log says why
    /// nothing was shown. It is not `no-targets`, because the set was not empty.</summary>
    public const string PresentationNoViewer = "presentation-no-viewer";
    /// <summary>A presentation step's resolved inputs do not fit the payload slot one command request carries.</summary>
    public const string PresentationInputBudget = "presentation-input-budget";
    /// <summary>A presentation handler reported a result that commits state. The tier writes presentation only, so
    /// its result is `none` or it is refused.</summary>
    public const string PresentationCommit = "presentation-commit";

    /// <summary>Every code this table registers, in declaration order. A row's `code` may name a domain reason or one
    /// of these: a kernel refusal that writes a row writes it with the code it refused by, and the row's own declared
    /// code list can never carry a code the kernel raises.</summary>
    private static readonly string[] Registered =
    {
        ResultRowBudget, ResultRowStatus, ResultRowIncomplete, ResultSchema, ResultIndexOutOfRange,
        HandleLiteral, HandleKind, HandleLifetime, StaleHandle, HandleBudget, ResourceRuntimeValue, EventLiteral,
        StaleResource, ResourceKind, ResourceUnavailable, EventPortKind,
        ControlShape, ControlUnsupported, ControlBound, IterationBudget, DispatchStepBudget,
        KernelActionBound, StatusDispelShape,
        InsufficientCost, CostUnsupported, CostCommitFailed,
        QueryBudget, QueryObserverUnavailable, QueryAuthority, PureWorldPort, PureWorldRead,
        AttachmentEmpty, AttachmentOrder, AttachmentDuplicate, AttachmentKind, AttachmentBudget, AttachmentPort,
        PresentationRecipient, PresentationNoViewer, PresentationInputBudget, PresentationCommit,
    };

    /// <summary>Whether one text is a code this table registers. The row writer asks it of a `code` its port did not
    /// declare, so a kernel refusal's own code is never mistaken for a domain code the port forgot to list.</summary>
    public static bool IsRegistered(string code) => Array.IndexOf(Registered, code) >= 0;
}
