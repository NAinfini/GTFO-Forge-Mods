using System;
using System.Collections.Generic;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>What the claim prompt shows while the local player aims at a claimable.</summary>
public readonly struct ClaimPrompt
{
    public ClaimPrompt(string text, string key, bool claimable) { Text = text; Key = key; Claimable = claimable; }

    /// <summary>The prompt line (for example what the player would pick up, or why they cannot).</summary>
    public string Text { get; }
    /// <summary>The key shown with it (such as <c>[E]</c>), empty when there is none.</summary>
    public string Key { get; }
    /// <summary>True when pressing the use key asks the host for it.</summary>
    public bool Claimable { get; }
}

/// <summary>The host's answer while a claimant installs what they reserved (<see cref="ClaimableSpec.HostVerify"/>).</summary>
public readonly struct ClaimVerdict
{
    private ClaimVerdict(byte kind, string reason, Guid token) { Kind = kind; Reason = reason; Token = token; }

    internal byte Kind { get; }
    internal string Reason { get; }
    internal Guid Token { get; }

    /// <summary>Not installed yet; ask again next frame (until the reservation times out).</summary>
    public static ClaimVerdict Wait => default;
    /// <summary>Installed wrongly or impossible: the reservation is cancelled and it is open again. <paramref name="reason"/> goes to the log.</summary>
    public static ClaimVerdict Refuse(string reason) => new(1, reason, default);
    /// <summary>Installed and verified: it is taken. <paramref name="token"/> reaches every peer's <see cref="ClaimableSpec.Committed"/>.</summary>
    public static ClaimVerdict Accept(Guid token) => new(2, "", token);
}

/// <summary>One claim as the claimant sees it: the thing, and this player's attempt at it. Report the installation with
/// <see cref="Installed"/> or <see cref="Failed"/>.</summary>
public sealed class ClaimInstall
{
    internal ClaimInstall(ulong summoner, uint throwId, uint attempt, int bay = 0) { Summoner = summoner; ThrowId = throwId; Attempt = attempt; Bay = bay; }

    public ulong Summoner { get; }
    public uint ThrowId { get; }
    /// <summary>The pod's weapon bay of the claimed thing.</summary>
    public int Bay { get; }
    /// <summary>This player's sequence number of the claim.</summary>
    public uint Attempt { get; }

    /// <summary>Tells the host the gear is fully installed; it then verifies it (<see cref="ClaimableSpec.HostVerify"/>).</summary>
    public void Installed() => OrdnanceClaims.Report(this, ClaimMessage.Installed);

    /// <summary>Tells the host installing failed; the thing is open again.</summary>
    public void Failed() => OrdnanceClaims.Report(this, ClaimMessage.Failed);
}

/// <summary>What <see cref="ClaimableSpec.Committed"/> receives on every peer when a claim is taken.</summary>
public readonly struct ClaimResult
{
    internal ClaimResult(ulong player, uint attempt, Guid token, bool ours) { Player = player; Attempt = attempt; Token = token; Ours = ours; }

    /// <summary>Who took it.</summary>
    public ulong Player { get; }
    public uint Attempt { get; }
    /// <summary>What the host's <see cref="ClaimVerdict.Accept"/> gave.</summary>
    public Guid Token { get; }
    /// <summary>True on the claimant's own peer.</summary>
    public bool Ours { get; }
}

/// <summary>
/// A claimable thing lying in a drop pod (a heavy weapon on its rack): the platform does the aiming, the prompt, the request to the
/// host, the host's reach, reservation and timeout rules, the reservation round trip and the messages. The claiming package decides
/// what the prompt says, whether this player may take it, how it is installed and verified, and what taking it means.
/// Callbacks run on the main thread; an exception is logged.
/// </summary>
public sealed class ClaimableSpec
{
    /// <summary>Local, every frame the player aims at it (return a cached value while nothing changed).</summary>
    public Func<ClaimPrompt> Prompt { get; set; } = null!;

    /// <summary>Host: a refusal reason for this player's request (shown in the log), or null to reserve it for them. Called after the
    /// platform's own checks (alive, in reach, open, not reserved).</summary>
    public Func<PlayerAgent, string?>? HostCheck { get; set; }

    /// <summary>Claimant: the host reserved it; install it, then call <see cref="ClaimInstall.Installed"/> (or <see cref="ClaimInstall.Failed"/>).
    /// The host cancels the reservation if this is not reported within a few seconds.</summary>
    public Action<ClaimInstall> Install { get; set; } = null!;

    /// <summary>Claimant: the host cancelled the reservation (a failure it saw, a timeout, or the pod leaving).</summary>
    public Action<ClaimInstall>? Cancelled { get; set; }

    /// <summary>Host, every frame while the claimant has reported <see cref="ClaimInstall.Installed"/>: read the claimant's gear and
    /// answer.</summary>
    public Func<PlayerAgent, ClaimVerdict> HostVerify { get; set; } = null!;

    /// <summary>Every peer, the host included: it was taken. On the host this is where its own bookkeeping starts.</summary>
    public Action<ClaimResult>? Committed { get; set; }

    /// <summary>Every peer: the pod opened (or was already open when this was added); show the thing in its bay.</summary>
    public Action? Opened { get; set; }

    /// <summary>Every peer: it can no longer be claimed because its pod left, or (without a pod) it was taken. Remove what you showed.</summary>
    public Action? Removed { get; set; }
}

/// <summary>One registered claimable (<see cref="OrdnancePod.AddClaimable"/>).</summary>
public sealed class OrdnanceClaimable
{
    internal OrdnanceClaimable(ulong summoner, uint throwId, ClaimableSpec spec, OrdnancePod? pod, int bay, Vector3 fallback)
    {
        Summoner = summoner; ThrowId = throwId; Spec = spec; Pod = pod; Bay = bay; Fallback = fallback;
    }

    public ulong Summoner { get; }
    public uint ThrowId { get; }
    /// <summary>True once the pod has opened.</summary>
    public bool IsOpen => Open;
    /// <summary>True once someone took it.</summary>
    public bool IsTaken => Taken;
    /// <summary>True while players can aim at it and the host takes requests for it: its pod has been open for <see cref="DeliveryTiming.ItemsAfterOpen"/>
    /// (the moment <see cref="OrdnancePod.AddClaimable"/> documents as the host's opening), it is not taken and not withdrawn. A host that takes over after a host
    /// change calls <see cref="OrdnanceDelivery.HostOpenClaim"/> for the claimables that are ready.</summary>
    public bool IsReady => Ready(Time.time);

    /// <summary>Takes it out of play now on this peer (it can no longer be aimed at or claimed, and <see cref="ClaimableSpec.Removed"/> runs), for
    /// example when its owner finds it cannot be kept. A pod's departure and a taking do this by themselves.</summary>
    public void Withdraw() => OrdnanceClaims.Remove(this);

    internal ClaimableSpec Spec { get; }
    internal OrdnancePod? Pod { get; }
    internal int Bay { get; }
    internal Vector3 Fallback { get; }
    /// <summary>When it can be aimed at and requested: a pod's claimable <see cref="DeliveryTiming.ItemsAfterOpen"/> after its doors began to open (when the host
    /// opens it), one without a pod at its own open time.</summary>
    internal float OpenAt;
    internal bool Open, Taken, Gone;
    /// <summary>True while it can be taken now: open (its pod has opened), not taken, not withdrawn.</summary>
    internal bool Ready(float now) => !Gone && !Taken && (Pod == null || Open) && now >= OpenAt;
    internal ulong PendingPlayer;
    internal uint Attempt;
    internal float Expires;
    internal bool Installed;

    /// <summary>Where the thing lies: the pod's weapon anchor of its bay, or the landing point (raised) without a pod.</summary>
    internal Vector3 Position
        => Pod?.WeaponAnchorAt(Bay) is { } anchor && !anchor.WasCollected ? anchor.position : Fallback;
}

/// <summary>Claims of the things lying in drop pods, on every peer; the host's reservation book is <see cref="ClaimBook"/>.</summary>
internal static class OrdnanceClaims
{
    private const float ReachMetres = 2.4f;
    /// <summary>How much farther the host lets a claimant stand than the claimant's own reach: the host sees the claimant's eye a network delay late.</summary>
    private const float ReachLag = 0.6f;
    private const float AimDot = 0.92f;
    /// <summary>How long the claimant has to install and report; the reservation lapses after this and a little more.</summary>
    private const float InstallSeconds = 8f;
    private const float RequestCooldown = 1f;

    private static readonly List<OrdnanceClaimable> All = new();
    private static readonly ClaimBook Book = new();
    private static readonly ForgeUI.InteractPrompt? Prompt = ForgeUI.UIKit.Mod(Plugin.Id).AddInteractPrompt("Claim");
    private static ClaimInstall? _ours;
    private static uint _nextAttempt;
    private static float _requestedAt = -10f;

    // ---- registration (every peer) ------------------------------------------------------------------------------

    internal static OrdnanceClaimable Add(ulong summoner, uint throwId, ClaimableSpec spec, OrdnancePod? pod, int bay, Vector3 fallback, float openAfter)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (spec.Prompt == null || spec.Install == null || spec.HostVerify == null) throw new ArgumentException("A claimable needs Prompt, Install and HostVerify.", nameof(spec));
        var claimable = new OrdnanceClaimable(summoner, throwId, spec, pod, bay, fallback);
        All.Add(claimable);
        if (pod == null)
        {
            claimable.OpenAt = Time.time + Math.Max(0f, openAfter);
            return claimable;
        }
        if (pod.IsOpen) Opened(claimable, 0f);
        else pod.Opened += () => Opened(claimable, DeliveryTiming.ItemsAfterOpen);
        pod.Left += () => Remove(claimable);
        return claimable;
    }

    private static void Opened(OrdnanceClaimable claimable, float after)
    {
        if (claimable.Gone || claimable.Open) return;
        claimable.Open = true;
        claimable.OpenAt = Time.time + after;
        Run(claimable.Spec.Opened, "opened");
    }

    internal static void Remove(OrdnanceClaimable claimable)
    {
        if (claimable.Gone) return;
        if (ForgeCore.Session.IsHost && claimable.PendingPlayer != 0) Cancel(claimable, claimable.PendingPlayer, claimable.Attempt);
        claimable.Gone = true;
        Book.Close(claimable.Summoner, claimable.ThrowId, claimable.Bay);
        All.Remove(claimable);
        Run(claimable.Spec.Removed, "removed");
    }

    private static OrdnanceClaimable? Find(ulong summoner, uint throwId, int bay)
    {
        foreach (var c in All) if (c.Summoner == summoner && c.ThrowId == throwId && c.Bay == bay) return c;
        return null;
    }

    /// <summary>Host: the claim can be requested from now on.</summary>
    internal static void HostOpen(ulong summoner, uint throwId, int bay = 0) => Book.Open(summoner, throwId, bay);

    /// <summary>Everything in flight is dropped: level exit and checkpoint reload void every delivery. Each claimable still standing is told
    /// (<see cref="ClaimableSpec.Removed"/>), and an install in progress is told it is cancelled.</summary>
    internal static void Clear()
    {
        var standing = All.ToArray();
        All.Clear();
        Book.Clear();
        Prompt?.Hide();
        var ours = _ours;
        _ours = null;
        _requestedAt = -10f;
        foreach (var c in standing)
        {
            if (c.Gone) continue;
            c.Gone = true;
            if (ours != null && ours.Summoner == c.Summoner && ours.ThrowId == c.ThrowId && ours.Bay == c.Bay) Run(c.Spec.Cancelled, ours, "cancelled");
            Run(c.Spec.Removed, "removed");
        }
    }

    /// <summary>A host change: the claimables still lying there stay, with every reservation void (the new host's book starts empty and
    /// the claiming package opens what is open again, <see cref="OrdnanceClaimable.Ready"/>).</summary>
    internal static void HostChanged()
    {
        Book.Clear();
        Prompt?.Hide();
        _ours = null;
        _requestedAt = -10f;
        foreach (var c in All)
        {
            c.PendingPlayer = 0;
            c.Attempt = 0;
            c.Installed = false;
        }
    }

    /// <summary>True while an untaken claimable of this call lies in a pod.</summary>
    internal static bool HoldsPod(ulong summoner, uint throwId)
    {
        foreach (var c in All)
            if (!c.Gone && !c.Taken && c.Pod != null && c.Summoner == summoner && c.ThrowId == throwId) return true;
        return false;
    }

    // ---- the host -----------------------------------------------------------------------------------------------

    internal static void OnRequest(ulong sender, ClaimMessage m)
    {
        if (!ForgeCore.Session.IsHost) return;
        var c = Find(m.Thrower, m.ThrowId, m.Bay);
        if (c == null || c.Taken) return;
        switch (m.Action)
        {
            case ClaimMessage.Failed:
                Cancel(c, sender, m.Attempt);
                return;
            case ClaimMessage.Installed:
                if (Book.Matches(m.Thrower, m.ThrowId, m.Bay, sender, m.Attempt)) c.Installed = true;
                CheckInstall(c);
                return;
            case ClaimMessage.Request:
                Reserve(c, sender, m.Attempt);
                return;
        }
    }

    private static void Reserve(OrdnanceClaimable c, ulong player, uint attempt)
    {
        var agent = ForgeCore.Level.PlayerByLookup(player);
        if (agent == null || !agent.Alive || !((agent.EyePosition - c.Position).sqrMagnitude <= (ReachMetres + ReachLag) * (ReachMetres + ReachLag))
            || !Book.TryClaim(c.Summoner, c.ThrowId, c.Bay, player, attempt)) return;
        string? refusal = null;
        try { refusal = c.Spec.HostCheck?.Invoke(agent); }
        catch (Exception error) { refusal = "its check failed: " + error; }
        if (refusal != null)
        {
            Plugin.Logger.Error("A claim was refused: " + refusal);
            Cancel(c, player, attempt);
            return;
        }
        c.PendingPlayer = player;
        c.Attempt = attempt;
        c.Expires = Time.time + InstallSeconds;
        c.Installed = false;
        Announce(c, ClaimStateMessage.Reserved, player, attempt, Guid.Empty);
    }

    private static void CheckInstall(OrdnanceClaimable c)
    {
        if (c.PendingPlayer == 0 || !c.Installed) return;
        var player = c.PendingPlayer;
        var attempt = c.Attempt;
        var agent = ForgeCore.Level.PlayerByLookup(player);
        if (agent == null || !agent.Alive) { Cancel(c, player, attempt); return; }
        ClaimVerdict verdict;
        try { verdict = c.Spec.HostVerify(agent); }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Verifying a claim failed: " + error);
            Cancel(c, player, attempt);
            return;
        }
        if (verdict.Kind == 0) return;
        if (verdict.Kind == 1)
        {
            Plugin.Logger.Error("A claim was refused after it was installed: " + verdict.Reason);
            Cancel(c, player, attempt);
            return;
        }
        if (!Book.Confirm(c.Summoner, c.ThrowId, c.Bay, player, attempt)) return;
        c.PendingPlayer = 0;
        Announce(c, ClaimStateMessage.Committed, player, attempt, verdict.Token);
    }

    private static void Cancel(OrdnanceClaimable c, ulong player, uint attempt)
    {
        if (!Book.Cancel(c.Summoner, c.ThrowId, c.Bay, player, attempt)) return;
        c.PendingPlayer = 0;
        c.Installed = false;
        Announce(c, ClaimStateMessage.Cancelled, player, attempt, Guid.Empty);
    }

    /// <summary>Tells every peer, and applies it here (the host is a peer too).</summary>
    private static void Announce(OrdnanceClaimable c, byte state, ulong player, uint attempt, Guid token)
    {
        var message = new ClaimStateMessage { Thrower = c.Summoner, ThrowId = c.ThrowId, Player = player, Attempt = attempt, State = state, Token = token, Bay = (byte)c.Bay };
        OrdnanceNetwork.Broadcast(Messages.ClaimStateEvent, message);
        OnState(message);
    }

    // ---- every peer ---------------------------------------------------------------------------------------------

    internal static void OnState(ClaimStateMessage m)
    {
        var c = Find(m.Thrower, m.ThrowId, m.Bay);
        var ours = _ours != null && SNet.LocalPlayer?.Lookup == m.Player && _ours.Summoner == m.Thrower && _ours.ThrowId == m.ThrowId && _ours.Attempt == m.Attempt;
        switch (m.State)
        {
            case ClaimStateMessage.Reserved:
                if (c == null) return;
                c.PendingPlayer = m.Player;
                c.Attempt = m.Attempt;
                if (!ours) return;
                try { c.Spec.Install(_ours!); }
                catch (Exception error)
                {
                    Plugin.Logger.ErrorOnce("Installing a claim failed: " + error);
                    _ours!.Failed();
                }
                return;
            case ClaimStateMessage.Cancelled:
                if (c != null && c.PendingPlayer == m.Player && c.Attempt == m.Attempt) c.PendingPlayer = 0;
                if (ours && c != null) Run(c.Spec.Cancelled, _ours!, "cancelled");
                return;
            case ClaimStateMessage.Committed:
                if (c == null) return;
                c.Taken = true;
                c.PendingPlayer = 0;
                if (c.Spec.Committed is { } committed)
                {
                    try { committed(new ClaimResult(m.Player, m.Attempt, m.Token, ours)); }
                    catch (Exception error) { Plugin.Logger.ErrorOnce("A claim's committed handler failed: " + error); }
                }
                if (c.Pod == null) Remove(c);
                return;
        }
    }

    // ---- the local player ---------------------------------------------------------------------------------------

    internal static void Report(ClaimInstall claim, byte action)
    {
        if (SNet.LocalPlayer == null) return;
        var message = new ClaimMessage { Thrower = claim.Summoner, ThrowId = claim.ThrowId, Attempt = claim.Attempt, Action = action, Bay = (byte)claim.Bay };
        if (ForgeCore.Session.IsHost) OnRequest(SNet.LocalPlayer.Lookup, message);
        else if (SNet.Master != null) OrdnanceNetwork.ToHost(Messages.ClaimEvent, message);
    }

    internal static void Tick(float now)
    {
        if (ForgeCore.Session.IsHost)
        {
            for (var i = All.Count - 1; i >= 0; i--)
            {
                var c = All[i];
                if (c.PendingPlayer == 0) continue;
                if (now >= c.Expires) Cancel(c, c.PendingPlayer, c.Attempt);
                else CheckInstall(c);
            }
        }
        if (All.Count == 0 && !(Prompt?.IsShown ?? false)) return;
        try { Aim(now); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The claim prompt failed: " + error); }
    }

    private static void Aim(float now)
    {
        var agent = ForgeCore.Players.Local;
        OrdnanceClaimable? aimed = null;
        if (agent != null && !agent.WasCollected && agent.Alive && FocusStateManager.CurrentState == eFocusState.FPS && All.Count != 0)
        {
            var eye = agent.FPSCamera.transform;
            foreach (var c in All)
            {
                if (c.PendingPlayer != 0 || !c.Ready(now)) continue;
                var offset = c.Position - eye.position;
                if (offset.sqrMagnitude > ReachMetres * ReachMetres) continue;
                if (Vector3.Dot(eye.forward, offset.normalized) < AimDot) continue;
                aimed = c;
                break;
            }
        }
        if (aimed == null)
        {
            Prompt?.Hide();
            return;
        }
        var prompt = aimed.Spec.Prompt();
        // Reasserted every frame: native prompts can replace ours between frames. Unchanged text is reused.
        Prompt?.Show(prompt.Text, prompt.Key);
        if (!prompt.Claimable || now - _requestedAt < RequestCooldown || !InputMapper.GetButtonDown.Invoke(InputAction.Use, eFocusState.FPS)) return;
        _requestedAt = now;
        if (++_nextAttempt == 0) ++_nextAttempt;
        _ours = new ClaimInstall(aimed.Summoner, aimed.ThrowId, _nextAttempt, aimed.Bay);
        var message = new ClaimMessage { Thrower = aimed.Summoner, ThrowId = aimed.ThrowId, Attempt = _nextAttempt, Action = ClaimMessage.Request, Bay = (byte)aimed.Bay };
        if (ForgeCore.Session.IsHost) OnRequest(SNet.LocalPlayer.Lookup, message);
        else OrdnanceNetwork.ToHost(Messages.ClaimEvent, message);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static void Run(Action? handler, string what)
    {
        if (handler == null) return;
        try { handler(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A claim's " + what + " handler failed: " + error); }
    }

    private static void Run(Action<ClaimInstall>? handler, ClaimInstall claim, string what)
    {
        if (handler == null) return;
        try { handler(claim); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A claim's " + what + " handler failed: " + error); }
    }
}
