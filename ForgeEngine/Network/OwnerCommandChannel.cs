using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.Network;

/// <summary>One owner message: the host's "apply" to the machine that holds what the step changes, and that machine's "result"
/// or "ended" back.</summary>
internal sealed record OwnerChannelMessage(string Kind, OwnerOutput Request, CommandResult? Result, string Reason);

/// <summary>
/// Carries owner steps between the host and the one machine that holds what each changes, over the engine's reliable owner
/// messages (<see cref="EngineNet"/>). The kernel's own <c>OwnerCommandLedger</c> decides execution and lifetime; this class only
/// remembers which request it sent to whom, so a holder's answer is matched to the exact request it was sent.
/// </summary>
internal static class OwnerCommandChannel
{
    private static EngineNet? net;
    private static Action<OwnerChannelMessage>? deliver;
    // Transport correlation retains the exact admitted request; it never decides native execution or lifetime.
    private static readonly OwnerCommandLedger sent = new(resultsKnown: false);
    private static long sentWorld = -1;

    internal static void RestoreMigration(IReadOnlyList<OwnerOutput> requests, long worldEpoch)
    {
        var restored = new OwnerCommandLedger();
        foreach (var request in requests)
        {
            var refusal = request.WorldEpoch == worldEpoch
                ? restored.Admit(request, null, out _, out _)
                : CommandResult.Rejected(NetworkCodes.StaleWorld);
            if (refusal != null) throw new NetworkContractException(refusal.Code, refusal.Detail);
        }
        sent.Clear();
        foreach (var entry in restored.Entries) sent.Admit(entry.Request, null, out _, out _);
        sentWorld = worldEpoch;
    }

    /// <summary><paramref name="handler"/> runs a message addressed to this machine in the kernel.</summary>
    internal static void Attach(EngineNet current, Action<OwnerChannelMessage> handler)
    {
        net = current;
        deliver = handler;
        current.OwnerReceived = Receive;
        sent.Clear();
    }

    internal static void Detach()
    {
        if (net != null) net.OwnerReceived = null;
        net = null;
        deliver = null;
        sent.Clear();
    }

    internal static void Send(OwnerOutput request)
    {
        if (net is not { IsHost: true, Kernel: { } kernel } current) return;
        if (request.WorldEpoch != kernel.WorldEpoch)
        { Deliver(new("result", request, CommandResult.Rejected(NetworkCodes.StaleWorld), "")); return; }
        if (sentWorld != request.WorldEpoch) { sent.Clear(); sentWorld = request.WorldEpoch; }
        var refusedAdmission = sent.Admit(request, null, out _, out _);
        if (refusedAdmission != null)
        {
            if (refusedAdmission.Code == "owner-ledger-budget") refusedAdmission = CommandResult.Rejected("owner-channel-budget");
            Deliver(new("result", request, refusedAdmission, "")); return;
        }
        if (request.HolderIdentity == 0)
        {
            Deliver(new("result", request, CommandResult.Rejected(RuntimeKernel.OwnerHolderCode,
            "Owner channel refused an unresolved holder identity before sending."), "")); return;
        }
        var message = new OwnerChannelMessage("apply", request, null, "");
        if (request.HolderIdentity == current.Local()) { Deliver(message); return; }
        if (current.Refusal(request.HolderIdentity) is { } refused)
        { Deliver(new("result", request, CommandResult.Rejected(RuntimeKernel.OwnerUnsupportedCode, refused), "")); return; }
        var bytes = EngineMessages.Encode(message);
        if (bytes.Length > NetworkProtocol.MaximumMessageBytes)
        { Deliver(new("result", request, CommandResult.Rejected(NetworkCodes.PayloadTooLarge), "")); return; }
        current.SendOwner(request.HolderIdentity, bytes);
    }

    internal static void Reply(OwnerOutput request, CommandResult result, string reason = "") => TryReply(request, result, reason);

    /// <summary>The holder's answer to an owner step (no <paramref name="reason"/>) or its notice that the step's effect ended
    /// (with one). False when there is no session, or the ending belongs to a world that is gone.</summary>
    internal static bool TryReply(OwnerOutput request, CommandResult result, string reason)
    {
        if (net is not { Kernel: { } kernel } current) return false;
        if (reason.Length != 0 && request.WorldEpoch != kernel.WorldEpoch) return false;
        var message = new OwnerChannelMessage(reason.Length == 0 ? "result" : "ended", request, result, reason);
        if (current.IsHost) { Deliver(message); return true; }
        var bytes = EngineMessages.Encode(message);
        if (bytes.Length > NetworkProtocol.MaximumMessageBytes)
        {
            // A valid SDK result can exceed one message. Do not block every later cleanup or throw back through the native
            // provider before it releases its handle. Report explicit uncertainty about the result that cannot travel;
            // never silently truncate it or invent a confirmed outcome.
            bytes = EngineMessages.Encode(message with
            {
                Result = CommandResult.FailedUnknown("owner-end-budget", "The holder result exceeded the owner-channel message limit.")
            });
        }
        return current.SendOwnerToHost(bytes);
    }

    private static void Receive(ulong sender, byte[] bytes)
    {
        if (net is not { Kernel: { } kernel } current) return;
        // A message this machine does not take is counted under the code that says why (ForgeCore's drop counts), never lost unseen.
        if (!EngineMessages.TryDecode(bytes, out OwnerChannelMessage message)) { ForgeCore.Net.Drop(NetworkCodes.FormatInvalid); return; }
        if (message.Kind == "apply")
        {
            if (!current.FromHost(sender)) { ForgeCore.Net.Drop(NetworkCodes.SenderNotHost); return; }
            if (message.Request.Holder != current.LocalAddress() || message.Request.HolderIdentity != current.Local())
            { ForgeCore.Net.Drop(NetworkCodes.NoConsumer); return; }
            if (message.Request.WorldEpoch != kernel.WorldEpoch)
            {
                TryReply(message.Request, CommandResult.Rejected(NetworkCodes.StaleWorld), "");
                return;
            }
        }
        else
        {
            if (!current.IsHost) { ForgeCore.Net.Drop(NetworkCodes.NoConsumer); return; }
            if (current.Refusal(sender) is { } refusal) { ForgeCore.Net.Drop(refusal); return; }
            if (message.Request.WorldEpoch != kernel.WorldEpoch) { ForgeCore.Net.Drop(NetworkCodes.StaleWorld); return; }
            // An answer to a step this host did not send to that player, in that world, answers nothing.
            if (!sent.TryGet(message.Request.CommandId, out var recorded)
                || recorded.Request.HolderIdentity != sender || recorded.Request.WorldEpoch != message.Request.WorldEpoch)
            { ForgeCore.Net.Drop(NetworkCodes.NoConsumer); return; }
            // Preserve only a validated ending identity. Every original application field still comes from the
            // admitted correlation, so a holder cannot replace inputs or borrow another cleanup obligation.
            var request = recorded.Request;
            if (message.Kind == "ended" && OwnerCommandLedger.IsEnding(message.Request))
            {
                try
                {
                    var ending = message.Request.Data;
                    request = OwnerCommandLedger.WithEnding(request, RuntimeJson.Text(ending, "endedSlot"), ending.GetProperty("endedHandle"));
                    OwnerCommandLedger.ReadEnding(request, out _, out _);
                }
                catch (RuntimeContractException) { ForgeCore.Net.Drop(NetworkCodes.FormatInvalid); return; }
            }
            message = message with { Request = request };
        }
        Deliver(message);
    }

    private static void Deliver(OwnerChannelMessage message) => deliver?.Invoke(message);
}
