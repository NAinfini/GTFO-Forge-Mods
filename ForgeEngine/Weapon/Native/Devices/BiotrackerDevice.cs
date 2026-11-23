using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>
/// The bio tracker (`Gear.EnemyScanner`, dump.cs 616152). The scanner has no off switch of its own: its whole
/// behaviour is its `Update` body, and the game switches that body with the component's own `enabled` flag —
/// `OnWield` (RVA 0x1467A20) ends in `Behaviour.set_enabled(true)` and `OnUnWield` (RVA 0x1467970) in
/// `set_enabled(false)`. A disable therefore does what an unwield does to the scanning — `enabled = false`, so no
/// scan, no blip and no tag runs — and hides the hologram the player reads (`m_scannerObjRoot`, which no scanner
/// body toggles). Because the game's own wield switches the component back on, a postfix on `OnWield` puts a held
/// scanner off again (<see cref="BiotrackerWieldHold"/>). The restore shows the hologram as it was and gives the
/// component back the state the game would have: on while the scanner is the item in the player's hands.
/// Only the local player's own scanner is written: the tracker is the player's own screen, drawn on their machine.
/// </summary>
internal sealed class BiotrackerDevice : IDeviceWriter, IDisposable
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[] { typeof(BiotrackerWieldHold) });

    private static readonly Dictionary<IntPtr, BiotrackerDevice> Held = new();
    private readonly IPlayerDirectory _players;
    private readonly Dictionary<EntityReference, Taken> _taken = new();
    private bool _disposed;

    private sealed record Taken(EnemyScanner Scanner, IntPtr Pointer, Transform? Root, bool HidRoot, IntPtr Player)
    {
        internal bool RootPending = HidRoot;
        internal bool ScannerPending = true;
    }

    internal BiotrackerDevice(IPlayerDirectory players) => _players = players ?? throw new ArgumentNullException(nameof(players));

    public string Category => DeviceCategories.Biotracker;

    /// <summary>Whether a held scanner is being wielded again, asked by the wield postfix.</summary>
    internal static bool Holds(IntPtr scanner) => Held.Count != 0 && Held.ContainsKey(scanner);

    // A session releases only its own entries; another registration is never cleared globally.
    public void Dispose()
    {
        if (_disposed && _taken.Count == 0) return;
        _disposed = true;
        ReleaseAll(Restore);
    }

    internal void ReleaseAll(Action<EntityReference> release)
    {
        var targets = new List<EntityReference>(_taken.Keys);
        List<Exception>? errors = null;
        foreach (var target in targets)
            try { release(target); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        if (errors != null) throw new AggregateException("Bio tracker release failed.", errors);
    }

    public string? Refusal(EntityReference target)
    {
        if (_disposed) return ResultCode.DeviceUnavailable;
        if (target == null || !EntityKinds.Is(target, EntityKinds.Player))
            return DeviceCategories.TargetKindCode;
        var agent = _players.AgentOf(target);
        if (agent == null) return ResultCode.DeviceUnavailable;
        var local = _players.LocalAgent;
        // Only a player another machine simulates is that machine's to write; a bot this machine drives has no tracker screen.
        if (local == null || local.Pointer != agent.Pointer)
            return ForgeCore.Players.SimulatedHere(agent.Owner) ? ResultCode.DeviceUnavailable : ResultCode.DevicePlayerRemote;
        var scanner = LocalScanner();
        if (scanner == null) return ResultCode.DeviceUnavailable;
        foreach (var taken in _taken.Values)
            if (taken.Pointer == scanner.Pointer && !Held.ContainsKey(taken.Pointer))
                return ResultCode.DeviceUnavailable; // A failed release must finish before this object is acquired again.
        return Held.TryGetValue(scanner.Pointer, out var owner) && !ReferenceEquals(owner, this)
            ? ResultCode.DeviceUnavailable : null;
    }

    /// <summary>The scanner in this machine's own backpack, or null when the local player carries none.</summary>
    private static EnemyScanner? LocalScanner()
    {
        var slots = PlayerBackpackManager.LocalBackpack?.Slots;
        if (slots == null) return null;
        for (int index = 0; index < slots.Length; index++)
            if (slots[index]?.Instance?.TryCast<EnemyScanner>() is { } scanner) return scanner;
        return null;
    }

    public void Disable(EntityReference target)
    {
        if (Refusal(target) != null) throw new InvalidOperationException("The bio tracker is no longer drawn on this machine.");
        if (_taken.ContainsKey(target)) return;
        var scanner = LocalScanner()!;
        // A pointer already held for another life cannot be acquired twice or released by a stale target.
        if (Held.ContainsKey(scanner.Pointer)) throw new InvalidOperationException("The bio tracker is already held.");
        var root = scanner.m_scannerObjRoot;
        bool hideRoot = root != null && root.gameObject.activeSelf;
        var taken = new Taken(scanner, scanner.Pointer, root, hideRoot, _players.LocalAgent!.Pointer);
        _taken.Add(target, taken);
        Held.Add(scanner.Pointer, this);
        try
        {
            scanner.enabled = false;
            if (hideRoot) root!.gameObject.SetActive(false);
        }
        catch
        {
            Restore(target);
            throw;
        }
    }

    public void Restore(EntityReference target)
    {
        if (!_taken.TryGetValue(target, out var taken)) return;
        var scanner = taken.Scanner;
        if (Held.TryGetValue(taken.Pointer, out var owner) && ReferenceEquals(owner, this))
            Held.Remove(taken.Pointer);
        List<Exception>? errors = null;
        // Each exact object is released independently. A failing visual setter cannot strand the scanner off.
        if (taken.RootPending)
            try
            {
                if (taken.Root != null && !taken.Root.gameObject.activeSelf) taken.Root.gameObject.SetActive(true);
                taken.RootPending = false;
            }
            catch (Exception error) { (errors ??= new()).Add(error); }
        if (taken.ScannerPending)
            try
            {
                // Resolve current native wield state, not the enabled state from when the hold began.
                var local = _players.LocalAgent;
                if (scanner != null && local != null && local.Pointer == taken.Player)
                {
                    var inventory = local.Inventory;
                    var wielded = inventory == null ? null : inventory.WieldedItem;
                    scanner.enabled = wielded != null && wielded.Pointer == taken.Pointer;
                }
                taken.ScannerPending = false;
            }
            catch (Exception error) { (errors ??= new()).Add(error); }
        if (!taken.RootPending && !taken.ScannerPending) _taken.Remove(target);
        if (errors != null) throw new AggregateException("Bio tracker restoration failed.", errors);
    }

    /// <summary>Puts a held scanner off again after the game's own wield switched it on.</summary>
    internal static void Reapply(EnemyScanner scanner)
    {
        if (!Holds(scanner.Pointer)) return;
        scanner.enabled = false;
    }
}

/// <summary>Keeps a held bio tracker off across a wield: `EnemyScanner.OnWield` ends by switching the component on,
/// and this postfix switches a held one back off. A scanner no hold covers is untouched.</summary>
[HarmonyPatch(typeof(EnemyScanner), nameof(EnemyScanner.OnWield))]
internal static class BiotrackerWieldHold
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(EnemyScanner __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Equipment)?.Guard(static (_, scanner) => BiotrackerDevice.Reapply(scanner), __instance);
}
