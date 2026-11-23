using ForgeEngine.GameBindings;
using ForgeUI;
using System;
using System.Collections.Generic;
using CellMenu;
using ForgeEngine.Framework;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>
/// ForgePlayer's three device writers — the flashlight, the player interface and the map — registered on the
/// <see cref="ForgePlayer.DeviceSwitchboard"/> this package owns. Every one of them writes only the player this machine
/// draws: the device row is a `presentation` row, so every addressed machine runs it, and a player whose screen is on
/// another machine is refused here with `device-player-remote` and written there.
/// Each writer remembers exactly what its own disable changed and puts exactly that back.
/// </summary>
internal static class PlayerDeviceWriters
{
    /// <summary>The three writers over one player directory, in the category table's own order.</summary>
    internal static IDeviceWriter[] Create(IPlayerDirectory players)
        => new IDeviceWriter[] { new Flashlight(players), new PlayerHud(players), new MapPage(players) };

    /// <summary>The player behind a target when this machine drives it, or the refusal a writer answers with. The
    /// screen writers take only the machine's own player (<paramref name="screen"/>): a bot this machine drives has
    /// a flashlight but no interface or map of its own, and its target must not blank the local player's.</summary>
    private static string? Local(IPlayerDirectory players, EntityReference target, bool screen, out PlayerAgent? agent)
    {
        agent = null;
        if (!EntityKinds.Is(target, EntityKinds.Player))
            return DeviceCategories.TargetKindCode;
        agent = players.AgentOf(target);
        if (agent == null) return ResultCode.DeviceUnavailable;
        if (!screen) return agent.IsLocallyOwned ? null : ResultCode.DevicePlayerRemote;
        var local = players.LocalAgent;
        if (local != null && local.Pointer == agent.Pointer) return null;
        // Only a player another machine simulates is that machine's to write; a bot this machine drives has no screen.
        return ForgeCore.Players.SimulatedHere(agent.Owner) ? ResultCode.DeviceUnavailable : ResultCode.DevicePlayerRemote;
    }

    private static PlayerAgent Require(IPlayerDirectory players, EntityReference target, bool screen)
        => Local(players, target, screen, out var agent) == null ? agent!
            : throw new InvalidOperationException("The player is no longer drawn on this machine.");

    /// <summary>
    /// Uses the game's flashlight request/sync path for every call. While held, only this sync's enable argument
    /// is constrained to false; the body still runs exactly once, preserving broadcastOnly and native packet work.
    /// The hold records each later native request and restores that latest intent,
    /// not an old snapshot. No public native ownership token provides this temporary constraint, so the narrow
    /// request hook remains. Direct inventory/packet writes bypassing this API are not covered by this hook.
    /// </summary>
    internal sealed class Flashlight : IDeviceWriter
    {
        private readonly IPlayerDirectory _players;
        private readonly Dictionary<string, FlashlightHoldGate.Hold> _held = new(StringComparer.Ordinal);
        internal Flashlight(IPlayerDirectory players) => _players = players ?? throw new ArgumentNullException(nameof(players));

        public string Category => DeviceCategories.Flashlight;

        public string? Refusal(EntityReference target)
        {
            var refusal = Local(_players, target, screen: false, out var agent);
            if (refusal != null) return refusal;
            return agent!.Sync == null || agent.Inventory == null ? ResultCode.DeviceUnavailable : null;
        }

        public void Disable(EntityReference target)
        {
            var agent = Require(_players, target, screen: false);
            var sync = agent.Sync ?? throw new InvalidOperationException("The player has no sync component.");
            var held = FlashlightHoldGate.Begin(sync, agent.Inventory!.FlashlightEnabled);
            bool registered = false;
            try { _held.Add(target.Id, held); registered = true; held.Write(false); }
            catch (Exception error)
            {
                if (registered) _held.Remove(target.Id);
                FlashlightHoldGate.Release(held);
                // A request can mutate then throw. Compensate through the same native API using the latest
                // native request intent; propagate any failure rather than reporting a successful device hold.
                if (registered)
                    try { sync.WantsToSetFlashlightEnabled(held.Desired, false); }
                    catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }

        public void Restore(EntityReference target)
        {
            if (!_held.Remove(target.Id, out var held)) return;
            FlashlightHoldGate.Release(held);
            // Never resolve target.Id to a replacement life and restore an old hold onto its new sync.
            var agent = _players.AgentOf(target);
            if (agent?.Sync?.Pointer != held.Sync.Pointer) return;
            if (agent.Inventory?.FlashlightEnabled != held.Desired)
                held.Sync.WantsToSetFlashlightEnabled(held.Desired, false);
        }
    }

    /// <summary>The HUD hide is ForgeUI's game-HUD claim: the status, weapon list, compass and objective readouts go and
    /// everything else the game draws stays. ForgeUI also releases the claim when the level ends or the local player
    /// dies; the writer forgets the hold then, and a later Restore has nothing left to put back.</summary>
    internal sealed class PlayerHud : IDeviceWriter
    {
        private const GameHudPart Hidden = GameHudPart.Vitals | GameHudPart.WeaponList | GameHudPart.Compass | GameHudPart.Objectives;
        private readonly IPlayerDirectory _players;
        private readonly Dictionary<string, GameHudHide> _held = new(StringComparer.Ordinal);
        internal PlayerHud(IPlayerDirectory players) => _players = players ?? throw new ArgumentNullException(nameof(players));
        public string Category => DeviceCategories.PlayerHud;
        public string? Refusal(EntityReference target)
            => Local(_players, target, screen: true, out _) ?? (GuiManager.Current?.m_playerLayer == null ? ResultCode.DeviceUnavailable : null);

        public void Disable(EntityReference target)
        {
            Require(_players, target, screen: true);
            var hide = UIKit.Mod(ForgeEngine.Plugin.PluginGuid).HideGameHud("PlayerHud", GameHudPart.All & ~Hidden)
                ?? throw new InvalidOperationException("ForgeUI refused the player HUD hide.");
            hide.Released += _ => _held.Remove(target.Id);
            _held.Add(target.Id, hide);
            hide.Active = true;
            if (!hide.Active)
            {
                _held.Remove(target.Id);
                hide.Remove();
                throw new InvalidOperationException("ForgeUI did not take the player HUD hide.");
            }
        }

        public void Restore(EntityReference target)
        {
            if (_held.Remove(target.Id, out var hide)) hide.Remove();
        }
    }

    /// <summary>The original map drawing/disconnected UI, through its two native setters. The held page's
    /// calls retain their native body once; only that call's bool is constrained. Each new native request replaces
    /// the release intent, so release never blindly restores the initial pair. No replacement notice or map UI.</summary>
    internal sealed class MapPage : IDeviceWriter
    {
        private readonly IPlayerDirectory _players;
        private readonly Dictionary<string, Hold> _held = new(StringComparer.Ordinal);
        private static readonly Dictionary<IntPtr, Hold> Active = new();
        private sealed class Hold
        {
            internal readonly CM_PageMap Page;
            internal readonly IntPtr PagePointer;
            private readonly GameObject? _visualObject, _noticeObject;
            internal bool Visuals, Notice;
            private bool OwnsVisuals => Page != null && _visualObject != null && Page.m_mapHolder?.Pointer == _visualObject.Pointer;
            private bool OwnsNotice => Page != null && _noticeObject != null && Page.m_mapDisconnected?.Pointer == _noticeObject.Pointer;
            private bool _ownVisuals, _ownNotice;
            internal Hold(CM_PageMap page)
            {
                Page = page; PagePointer = page.Pointer; _visualObject = page.m_mapHolder; _noticeObject = page.m_mapDisconnected;
                Visuals = _visualObject != null && _visualObject.activeSelf;
                Notice = _noticeObject != null && _noticeObject.activeSelf;
            }
            internal void Disable()
            {
                _ownVisuals = true;
                try { Page.SetMapVisualsIsActive(false); } finally { _ownVisuals = false; }
                RequireOriginalTargets();
                _ownNotice = true;
                try { Page.SetMapDisconnetedTextIsActive(true); } finally { _ownNotice = false; }
                RequireOriginalTargets();
            }
            private void RequireOriginalTargets()
            {
                if (Page.m_mapHolder?.Pointer != _visualObject?.Pointer
                    || Page.m_mapDisconnected?.Pointer != _noticeObject?.Pointer)
                    throw new InvalidOperationException("The native map rebuilt its UI during the hold request.");
            }
            internal bool Constrain(bool visuals, bool requested)
            {
                if (visuals)
                {
                    if (!OwnsVisuals) return requested;
                    if (_ownVisuals) { _ownVisuals = false; return requested; }
                    Visuals = requested; return false;
                }
                if (!OwnsNotice) return requested;
                if (_ownNotice) { _ownNotice = false; return requested; }
                Notice = requested; return true;
            }
            /// <summary>The page's own refresh wrote both objects without the setters: its write is the native intent, and the
            /// hold is put back through the setters.</summary>
            internal void Refreshed()
            {
                if (OwnsVisuals) Visuals = _visualObject!.activeSelf;
                if (OwnsNotice) Notice = _noticeObject!.activeSelf;
                Disable();
            }
            internal void Restore()
            {
                // Both native entries get their release even if the first mutates then throws. There is no
                // switchboard retry after Restore, and no retained constraint is allowed to strand the page.
                List<Exception>? errors = null;
                if (OwnsVisuals)
                    try { Page.SetMapVisualsIsActive(Visuals); } catch (Exception e) { (errors ??= new()).Add(e); }
                if (OwnsNotice)
                    try { Page.SetMapDisconnetedTextIsActive(Notice); } catch (Exception e) { (errors ??= new()).Add(e); }
                if (errors != null) throw new AggregateException(errors);
            }
        }
        /// <summary>Forgets every page hold at session teardown, like <see cref="FlashlightHoldGate.Clear"/>.</summary>
        internal static void Clear() => Active.Clear();
        internal static void Refreshed(CM_PageMap page)
        {
            if (Active.Count != 0 && page != null && Active.TryGetValue(page.Pointer, out var held)) held.Refreshed();
        }
        internal static bool Constrain(CM_PageMap page, bool visuals, bool requested)
            => Active.Count != 0 && page != null && Active.TryGetValue(page.Pointer, out var held) ? held.Constrain(visuals, requested) : requested;
        private static void End(Hold hold)
        {
            if (Active.TryGetValue(hold.PagePointer, out var current) && ReferenceEquals(hold, current))
                Active.Remove(hold.PagePointer);
        }
        internal MapPage(IPlayerDirectory players) => _players = players ?? throw new ArgumentNullException(nameof(players));
        public string Category => DeviceCategories.Map;
        public string? Refusal(EntityReference target)
        {
            var refusal = Local(_players, target, screen: true, out _);
            if (refusal != null) return refusal;
            var page = CM_PageMap.Current;
            return page == null || page.m_mapHolder == null ? ResultCode.DeviceUnavailable : null;
        }
        public void Disable(EntityReference target)
        {
            Require(_players, target, screen: true);
            var page = CM_PageMap.Current ?? throw new InvalidOperationException("The map page is gone.");
            var held = new Hold(page);
            Active.Add(page.Pointer, held);
            bool registered = false;
            try { _held.Add(target.Id, held); registered = true; held.Disable(); }
            catch (Exception error)
            {
                if (registered) _held.Remove(target.Id);
                End(held);
                if (registered)
                    try { held.Restore(); } catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }
        public void Restore(EntityReference target)
        {
            if (!_held.Remove(target.Id, out var held)) return;
            End(held);
            // Never write an old page's intent onto the replacement Current page.
            if (held.Page != null) held.Restore();
        }
    }
}

/// <summary>Only active flashlight holds, not a second device state service. Entries live for the existing
/// switchboard's hold and observe the current request's intent; no timer, global player history or polling.</summary>
internal static class FlashlightHoldGate
{
    internal sealed class Hold
    {
        internal readonly PlayerSync Sync;
        internal bool Desired;
        private bool _ownEntry;
        internal Hold(PlayerSync sync, bool desired) { Sync = sync; Desired = desired; }

        internal void Write(bool enable)
        {
            _ownEntry = true;
            try { Sync.WantsToSetFlashlightEnabled(enable, false); }
            finally { _ownEntry = false; }
        }

        internal bool Constrain(bool enable)
        {
            // Consume only the immediate Forge call. Reentrant requests from the native body
            // are native request intent, even when they address the same sync before our call returns.
            if (_ownEntry) { _ownEntry = false; return enable; }
            Desired = enable;
            return false;
        }
    }

    private static readonly Dictionary<IntPtr, Hold> Held = new();
    internal static Hold Begin(PlayerSync sync, bool desired)
    {
        var hold = new Hold(sync, desired);
        Held.Add(sync.Pointer, hold);
        return hold;
    }
    internal static void Release(Hold hold)
    {
        if (Held.TryGetValue(hold.Sync.Pointer, out var current) && ReferenceEquals(current, hold))
            Held.Remove(hold.Sync.Pointer);
    }
    internal static void Clear() => Held.Clear();
    internal static bool Idle => Held.Count == 0;
    internal static bool Constrain(IntPtr sync, bool enable)
        => Held.TryGetValue(sync, out var hold) ? hold.Constrain(enable) : enable;
}

/// <summary>A call-scoped constraint on the held sync only. Returning void preserves the native body and
/// broadcast path for on and off requests; unheld syncs and broadcastOnly are untouched.</summary>
[HarmonyPatch(typeof(PlayerSync), nameof(PlayerSync.WantsToSetFlashlightEnabled))]
internal static class FlashlightHoldPrefix
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(PlayerSync __instance, ref bool enable)
    {
        if (!FlashlightHoldGate.Idle) enable = FlashlightHoldGate.Constrain(__instance.Pointer, enable);
    }
}

/// <summary>Preserves the original drawing setter body; scopes its bool constraint to the actively held page.</summary>
[HarmonyPatch(typeof(CM_PageMap), nameof(CM_PageMap.SetMapVisualsIsActive))]
internal static class MapVisualHoldPrefix
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(CM_PageMap __instance, ref bool __0)
        => __0 = PlayerDeviceWriters.MapPage.Constrain(__instance, visuals: true, __0);
}

/// <summary>The page's refresh (<c>UpdatePlayerData</c>: opening the map, a player's health, infection, death or pickup) inlines both
/// setters, so the two prefixes never see its write: after it the held page takes the written pair as the native intent and is held
/// again.</summary>
[HarmonyPatch(typeof(CM_PageMap), nameof(CM_PageMap.UpdatePlayerData))]
internal static class MapRefreshHoldPostfix
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(CM_PageMap __instance)
    {
        try { PlayerDeviceWriters.MapPage.Refreshed(__instance); }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); }
    }
}

/// <summary>Preserves the original localized disconnected-notice UI and setter body.</summary>
[HarmonyPatch(typeof(CM_PageMap), nameof(CM_PageMap.SetMapDisconnetedTextIsActive))]
internal static class MapNoticeHoldPrefix
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(CM_PageMap __instance, ref bool __0)
        => __0 = PlayerDeviceWriters.MapPage.Constrain(__instance, visuals: false, __0);
}
