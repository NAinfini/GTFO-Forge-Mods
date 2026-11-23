using System;
using System.Text.Json;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Player;

namespace ForgeWeapon.Native;

/// <summary>
/// The native half of <see cref="WeaponDebugSwitchContract"/>: the game's three process-local debug properties,
/// written and independently read through their native setters/getters. The global `Weapon` type declares
/// `static bool SuperWeapons`, `static bool InfiniteAmmo` and `static bool RecoilOn`
/// (`catalog/native/native-runtime.json`, type `Weapon`; the ledger row `vanilla-player.weapon.weapon-debug-toggles`
/// names the same three and types each of them `boolean`), so there is nothing per weapon to address and nothing
/// to compute: the row's `enabled` input is the value the static is set to, and the readback is that same static.
///
/// The gate is the adapter's own authority, which is what the row's graph declares (`execution: host`): the host
/// writes its own statics and reads them back, and a client that must not act refuses by name. The statics are
/// process-wide and nothing in the game replicates them, so what the host holds on travels to every other machine
/// through the kernel's native-state receipt (the path the other domain state travels, late joiners and a new
/// host included) and each machine writes the same value into its own statics, keeping what they were before so
/// the session's end or the host letting go puts them back.
/// </summary>
internal sealed class WeaponDebugSwitchSource : IWeaponDebugSwitchSource
{
    internal const string RestoreFailedCode = "debug-switch-restore-unconfirmed";
    internal const string OwnershipLostCode = "debug-switch-ownership-lost";

    private const string StateKey = "forge.weapon.debug-switch";

    private readonly EquipmentNativeAdapter _adapter;
    private readonly bool[] _owned = new bool[3], _baseline = new bool[3], _written = new bool[3];
    private readonly DomainStateChannel.NativeStateSource _state;
    private bool _ended;
    // Whether the magazine is kept full on this machine: the infinite-ammo switch as this machine holds it.
    private bool _infiniteAmmo;
    internal static WeaponDebugSwitchSource? Current { get; private set; }

    internal WeaponDebugSwitchSource(EquipmentNativeAdapter adapter, IForgeEngine kernel)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        if (Current != null) throw new InvalidOperationException("Weapon debug switches already have a session writer.");
        _state = DomainStateChannel.RegisterNativeState(kernel, StateKey, Capture, Restore);
        Current = this;
    }

    /// <summary>The three switches in the card's own order, each the value held on or null while nothing holds it.
    /// Every machine keeps the same three, so a machine that becomes the host carries on from what it received.</summary>
    private JsonElement Capture() => JsonSerializer.SerializeToElement(new bool?[]
    {
        _owned[0] ? _written[0] : null, _owned[1] ? _written[1] : null, _owned[2] ? _written[2] : null
    });

    /// <summary>The host's switches arriving on another machine: each held switch is written into this machine's own
    /// static (keeping what it was before), each released one is put back. The whole state is checked before anything
    /// is written. The host's own state is what it wrote, so a receipt changes nothing there.</summary>
    private void Restore(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array || state.GetArrayLength() != _owned.Length)
            throw new RuntimeContractException("native-state-debug-switch", "Weapon debug switch state is malformed.");
        foreach (var value in state.EnumerateArray())
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw new RuntimeContractException("native-state-debug-switch", "Weapon debug switch state is malformed.");
        if (_ended || Session.IsHost) return;
        for (var index = 0; index < _owned.Length; index++)
        {
            var value = state[index];
            if (value.ValueKind == JsonValueKind.Null) Release(index);
            else Hold(index, value.GetBoolean());
        }
    }

    /// <summary>Writes one switch on this machine and keeps the value it replaces. A static somebody else changed since
    /// this session last wrote it is theirs: its current value becomes the one to put back.</summary>
    private void Hold(int index, bool enabled)
    {
        var before = Read(index);
        if (!_owned[index] || before != _written[index]) _baseline[index] = before;
        _owned[index] = true;
        _written[index] = enabled;
        WriteNative(index, enabled);
        if (index == WeaponDebugSwitchContract.InfiniteAmmo) _infiniteAmmo = enabled;
    }

    /// <summary>Puts one switch back to what it was before this session held it, unless somebody else wrote it since.</summary>
    private void Release(int index)
    {
        if (!_owned[index]) return;
        if (Read(index) == _written[index] && _written[index] != _baseline[index]) WriteNative(index, _baseline[index]);
        _owned[index] = false;
        if (index == WeaponDebugSwitchContract.InfiniteAmmo) _infiniteAmmo = false;
    }

    private static bool Read(int index) => index switch
    {
        WeaponDebugSwitchContract.SuperWeapons => Weapon.SuperWeapons,
        WeaponDebugSwitchContract.InfiniteAmmo => Weapon.InfiniteAmmo,
        WeaponDebugSwitchContract.Recoil => Weapon.RecoilOn,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>One switch written. The index is the declared member's own position in the card's value list, so
    /// a member this build has no static for is refused by name rather than mapped onto another switch.</summary>
    public bool TrySet(int switchIndex, bool enabled, out string code)
    {
        if (_ended || !_adapter.Authoritative())
        {
            code = ResultCode.AuthorityOrPhase;
            return false;
        }
        if ((uint)switchIndex >= 3) { code = ResultCode.SwitchUnsupported; return false; }
        if (!TryRead(switchIndex, out var before, out code)) return false;
        if (!_owned[switchIndex] || before != _written[switchIndex]) _baseline[switchIndex] = before;
        // The other machines are owed the switch whenever what the host holds on changes.
        bool changed = !_owned[switchIndex] || _written[switchIndex] != enabled;
        _owned[switchIndex] = true;
        _written[switchIndex] = enabled;
        try
        {
            WriteNative(switchIndex, enabled);
        }
        catch (Exception error)
        {
            // A native setter may throw after changing its static, so this code is the one refusal whose commit
            // is unknown: the caller answers failed/unknown with it.
            NativeCallFailure.Note("WeaponDebugSwitchSource.TrySet", error);
            code = ResultCode.DebugSwitchWriteFailed;
            return false;
        }
        if (switchIndex == WeaponDebugSwitchContract.InfiniteAmmo) _infiniteAmmo = enabled;
        if (changed) _state.Changed();
        code = "";
        return true;
    }

    /// <summary>The game reads <c>Weapon.InfiniteAmmo</c> nowhere in this build: its getter has no caller and no code touches the
    /// static's byte, so writing it changes nothing a player can see. While the switch is on the switch is therefore carried out
    /// here, once per tick on every machine: the local player's wielded gun has its magazine put back to full, so a shot never
    /// costs a round and a reload never takes one from the reserve. Each machine fills its own player's gun.</summary>
    internal void KeepMagazineFull()
    {
        if (_ended || !_infiniteAmmo) return;
        try
        {
            // Unity objects are tested with `==`, which sees a destroyed object; `?.` would read through it.
            var agent = Players.Local;
            var inventory = agent == null ? null : agent.Inventory;
            var wielded = inventory == null ? null : inventory.WieldedItem;
            var weapon = wielded == null ? null : wielded.TryCast<Gear.BulletWeapon>();
            if (weapon == null || weapon.IsReloading) return;
            var full = weapon.GetMaxClip();
            if (weapon.GetCurrentClip() >= full) return;
            weapon.SetCurrentClip(full);
            if (weapon.ItemDataBlock?.inventorySlot is { } slot) PlayerBackpackManager.LocalBackpack?.AmmoStorage?.UpdateSlotAmmoUI(slot);
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponDebugSwitchSource.KeepMagazineFull", failure); }
    }

    private static void WriteNative(int index, bool enabled)
    {
        switch (index)
        {
            case WeaponDebugSwitchContract.SuperWeapons: Weapon.SuperWeapons = enabled; break;
            case WeaponDebugSwitchContract.InfiniteAmmo: Weapon.InfiniteAmmo = enabled; break;
            case WeaponDebugSwitchContract.Recoil: Weapon.RecoilOn = enabled; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    // The original values and contribution are owned here, not in a second Runtime state service. The
    // existing weapon lifecycle calls this before releasing the session or crossing a world boundary, on every
    // machine: each puts its own statics back, and the host's release also travels to the others.
    internal bool RestoreAll(out string code)
    {
        code = "";
        bool released = false;
        for (var index = 0; index < _owned.Length; index++)
        {
            if (!_owned[index]) continue;
            try
            {
                if (!TryRead(index, out var actual, out _)) { code = RestoreFailedCode; continue; }
                if (actual != _baseline[index] && actual != _written[index])
                { code = OwnershipLostCode; continue; }
                if (actual != _baseline[index]) WriteNative(index, _baseline[index]);
                if (!TryRead(index, out actual, out _) || actual != _baseline[index])
                { code = RestoreFailedCode; continue; }
                _owned[index] = false;
                released = true;
                if (index == WeaponDebugSwitchContract.InfiniteAmmo) _infiniteAmmo = false;
            }
            catch (Exception failure) { NativeCallFailure.Note("WeaponDebugSwitchSource.RestoreAll", failure); code = RestoreFailedCode; }
        }
        if (released && Session.IsHost) _state.Changed();
        return code.Length == 0;
    }

    internal void EndSession()
    {
        if (!RestoreAll(out var code)) throw new RuntimeContractException(code, "Native weapon debug restore was not confirmed.");
        _ended = true;
        _state.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>The same three statics read back. The setter answers nothing, so this is the only proof the write
    /// landed; a getter that throws leaves the commit unknown instead of confirming it.</summary>
    public bool TryRead(int switchIndex, out bool value, out string code)
    {
        value = false;
        if ((uint)switchIndex >= 3) { code = ResultCode.SwitchUnsupported; return false; }
        try { value = Read(switchIndex); }
        catch (Exception failure)
        {
            NativeCallFailure.Note("WeaponDebugSwitchSource.TryRead", failure);
            code = ResultCode.DebugSwitchReadFailed;
            return false;
        }
        code = "";
        return true;
    }
}
