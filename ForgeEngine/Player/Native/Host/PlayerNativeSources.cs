using System;
using ForgeEngine.Framework;
using Player;
using SNetwork;

namespace ForgePlayer.Native;

/// <summary>The native half of the player-state and player-event observations: what a life's vitals are right now,
/// and which life a native instance belongs to. Every read is paired with a re-read of the instance identity, so a
/// life replaced mid-read answers null instead of reporting another life's state; the identity module owns which
/// agent is which recorded life and this half asks it rather than keeping a table of its own.</summary>
internal sealed class NativePlayerVitals
{
    private readonly PlayerIdentityModule _module;

    internal NativePlayerVitals(PlayerIdentityModule module) => _module = module ?? throw new ArgumentNullException(nameof(module));

    /// <summary>Whether this process may publish: the identity half's own gate, which already includes the
    /// runtime being ready, the registration being live and this peer being the authority.</summary>
    public bool Authoritative => _module.Authoritative;

    public void Reconcile() => _module.Reconcile();

    /// <summary>The recorded life behind a native instance. A damage receiver resolves through its own owner, so
    /// the hooks that are handed the receiver and the hooks that are handed the agent reach the same life.</summary>
    public EntityReference? ReferenceOf(object? instance) => instance switch
    {
        PlayerAgent agent => _module.ReferenceOf((object?)agent),
        Dam_PlayerDamageBase damage => _module.ReferenceOf((object?)damage.Owner),
        SNet_Player player => _module.ReferenceOf(player),
        _ => null
    };

    /// <summary>One read of a recorded life's vitals. The receiver must exist, be set up and belong to the agent
    /// the reference names — the same conditions the health receiver applies to a write — and the agent's pointer
    /// is re-read afterwards so a teardown during the read answers null.</summary>
    public PlayerVitals? Read(EntityReference reference)
    {
        var agent = _module.CurrentAgent(reference);
        if (agent == null) return null;
        var pointer = agent.Pointer;
        var damage = agent.Damage;
        if (damage == null || damage.Pointer == IntPtr.Zero) return null;
        if (damage.Owner == null || damage.Owner.Pointer != pointer || !damage.IsSetup) return null;
        float health = damage.Health, maximum = damage.HealthMax, infection = damage.Infection;
        bool alive = agent.Alive;
        if (agent.Pointer != pointer) return null;
        if (!float.IsFinite(health) || !float.IsFinite(maximum) || maximum <= 0 || health < 0 || health > maximum) return null;
        return new PlayerVitals(reference, health, maximum, float.IsFinite(infection) ? infection : float.NaN, alive);
    }
}

/// <summary>The native half of the value rows: the detail the shared entity snapshot does not carry. The
/// equipment a player holds and the expedition item in the backpack are resolved through the kernel's one
/// object-to-entity entry, so whichever package owns that object answers — this half never guesses a kind, and an
/// object no registered package recognizes is reported as unresolved rather than as "holding nothing".</summary>
internal sealed class NativePlayerValues
{
    internal const string EquipmentUnresolvedCode = "equipment-unresolved";
    internal const string NoWieldedGearCode = "no-wielded-gear";
    internal const string BackpackCode = "backpack-unavailable";

    private readonly IForgeEngine _kernel;

    private readonly PlayerIdentityModule _module;

    internal NativePlayerValues(IForgeEngine kernel, PlayerIdentityModule module)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public bool TryInfection(EntityReference reference, out double value, out string code)
    {
        value = 0;
        if (Agent(reference, out var agent, out code) is false) return false;
        var damage = agent!.Damage;
        if (damage == null || damage.Pointer == IntPtr.Zero) { code = ResultCode.MissingHealthReceiver; return false; }
        if (damage.Owner == null || damage.Owner.Pointer != agent.Pointer || !damage.IsSetup) { code = ResultCode.MissingHealthReceiver; return false; }
        float infection;
        try { infection = damage.Infection; }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error); code = ResultCode.ReadbackException; return false; }
        if (!float.IsFinite(infection)) { code = ResultCode.ReadbackException; return false; }
        value = infection;
        code = "";
        return true;
    }

    public bool TryWieldedGear(EntityReference reference, out EntityReference? equipment, out int slot, out string code)
    {
        equipment = null;
        if (!TryWieldedItem(reference, out var item, out slot, out code)) return false;
        if (item == null) return true;
        // An item that exists but that no package can name as an entity is unresolved, which is a different
        // answer from a player holding nothing.
        equipment = _kernel.ResolveEntity(item);
        if (equipment == null) { code = EquipmentUnresolvedCode; return false; }
        code = "";
        return true;
    }

    public bool TryCarriedItem(EntityReference reference, out EntityReference? item, out string code)
    {
        item = null;
        if (Agent(reference, out var agent, out code) is false) return false;
        if (agent!.Owner == null) { code = ResultCode.StaleEntity; return false; }
        if (!PlayerBackpackManager.TryGetBackpack(agent.Owner, out var backpack) || backpack == null)
        { code = BackpackCode; return false; }
        code = "";
        // A big item is the one in the game's own carry slot, in the hands or on the back; a crate or a cell
        // carries no expedition component, so that component is not the read.
        if (!backpack.TryGetBackpackItem(InventorySlot.InLevelCarry, out var carried) || carried?.Instance == null) return true;
        item = _kernel.ResolveEntity(carried.Instance);
        if (item == null) { code = EquipmentUnresolvedCode; return false; }
        return true;
    }

    /// <summary>The tool row's native read: the held item's class-ammunition pool — the pool the game spends as a
    /// tool's energy and as a weapon's own reserve — and the backpack's consumable stacks. Both are the game's own
    /// members: `GetClassAmmoInPackAbs` / `GetClassAmmoMaxCap` on the wielded item, and `PocketItemsGroups`, which
    /// is the stack table the pickup and removal paths maintain one list per item id.
    ///
    /// A pool the game answers outside its own bounds is a read that did not happen, not a value: a negative or
    /// cap-less pool would make `ammo` look like an observed number. The item entity is resolved through the
    /// kernel's own object-to-entity entry exactly like `wielded_gear`, so an item no package can name refuses
    /// instead of reporting another package's entity.</summary>
    public bool TryTool(EntityReference reference, out PlayerTool tool, out string code)
    {
        tool = default;
        if (!TryWieldedItem(reference, out var item, out _, out code)) return false;
        if (item == null) { code = NoWieldedGearCode; return false; }
        if (Agent(reference, out var agent, out code) is false) return false;
        if (agent!.Owner == null) { code = ResultCode.StaleEntity; return false; }
        if (!PlayerBackpackManager.TryGetBackpack(agent.Owner, out var backpack) || backpack == null)
        { code = BackpackCode; return false; }
        int ammo, maximum, count = 0, stacks = 0;
        try
        {
            ammo = item.GetClassAmmoInPackAbs(agent.Owner);
            maximum = item.GetClassAmmoMaxCap(agent.Owner);
            var groups = backpack.PocketItemsGroups;
            if (groups != null)
            {
                for (int index = 0; index < groups.Count; index++)
                {
                    var group = groups[index];
                    if (group == null) continue;
                    stacks++;
                    count += group.Count;
                }
            }
            if (ammo < 0 || maximum <= 0 || ammo > maximum || count < 0) { code = ResultCode.ReadbackException; return false; }
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error); code = ResultCode.ReadbackException; return false; }
        EntityReference? equipment = _kernel.ResolveEntity(item);
        if (equipment == null) { code = EquipmentUnresolvedCode; return false; }
        tool = new PlayerTool(equipment, ammo, maximum, count, stacks);
        code = "";
        return true;
    }

    /// <summary>The item a player is holding right now, or null when the player holds none. The chain is the
    /// game's own: the reference to its current agent, the agent to its inventory, the inventory to its wielded
    /// item.</summary>
    private bool TryWieldedItem(EntityReference reference, out ItemEquippable? item, out int slot, out string code)
    {
        item = null;
        slot = 0;
        if (Agent(reference, out var agent, out code) is false) return false;
        try
        {
            var inventory = agent!.Inventory;
            if (inventory == null || inventory.Pointer == IntPtr.Zero) { code = ResultCode.StaleEntity; return false; }
            slot = (int)inventory.WieldedSlot;
            if (!Enum.IsDefined(typeof(InventorySlot), inventory.WieldedSlot)) { code = ResultCode.ReadbackException; return false; }
            var wielded = inventory.WieldedItem;
            item = wielded == null || wielded.Pointer == IntPtr.Zero ? null : wielded;
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error); code = ResultCode.ReadbackException; return false; }
        code = "";
        return true;
    }

    private bool Agent(EntityReference reference, out PlayerAgent? agent, out string code)
    {
        agent = _module.CurrentAgent(reference);
        if (agent == null)
        {
            code = EntityKinds.Is(reference, EntityKinds.Player) ? ResultCode.StaleEntity : ResultCode.UnsupportedRecipient;
            return false;
        }
        code = "";
        return true;
    }

    public bool TryBackpackSlot(EntityReference reference, int slot, out PlayerBackpackSlot value, out string code)
    {
        value = default;
        if (!Agent(reference, out var agent, out code)) return false;
        if (!PlayerBackpackReads.TryRead(agent!, slot, out var sample, out code)) return false;
        if (_module.CurrentAgent(reference)?.Pointer != agent!.Pointer) { code = ResultCode.StaleEntity; return false; }
        value = sample.Value;
        return true;
    }
}
