using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The one deployed-device action this package owns: `forge.action.inventory.detonate`, the row that
/// makes an already-placed mine go off without waiting for its own detection or fuse.
///
/// <b>What it addresses.</b> The `devices` port carries world instances this provider placed — the references the
/// deployment facts publish (`gtfo.equipment:&lt;world&gt;.&lt;life&gt;.&lt;instance&gt;`), resolved back to the
/// native item through the same placement table the life-cycle observation uses. A reference of any other shape is
/// refused per target rather than resolved to something nearby.
///
/// <b>Which payload goes off.</b> The device decides: an explosive mine and a glue mine are two detonation
/// components behind the one `iMineDeployerInstance_Detonation` interface this row calls, and the card declares no
/// payload parameter — the payload is a property of the equipment that was deployed, not of the request.
///
/// <b>What it does not cover.</b> The ledger row this card is shared with also names the thrown-item row
/// (`vanilla-player.item.grenade-throw-detonaate`). This build has no externally callable detonation entry for a
/// thrown grenade: `GrenadeBase` declares only `Awake`, `GrenadeDelay`, `OnCollisionEnter` and `Start`, and the
/// two glue grenade instances declare only their own `ExplosionSequence` coroutine — the explosion is on a timer
/// inside those types and nothing outside them can start it. That half of the row is therefore not served here,
/// and it is recorded rather than papered over with a call into a body the game never exposes.
///
/// The body lives in <see cref="DetonateWrites"/> so this declaration travels into every process that registers
/// this provider, and the native half supplies the source that really calls the trigger. A process with no native
/// half refuses the row by code rather than declaring a detonation nothing performs.
/// </summary>
internal static class DetonateContract
{
    /// <summary>The row's canonical id; the row itself is the generated block table's.</summary>
    public const string CapabilityId = Blocks.ActionInventoryDetonate.Id;

    /// <summary>This provider's one binding of that row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.inventory_detonate";

    /// <summary>The handler name the binding resolves to, which is also the shape key.</summary>
    public const string HandlerName = "gtfo.inventory.detonate";

    /// <summary>The permission this row writes under: it changes a deployed device's own state, which is the same
    /// object class the deployable observations read under their read permission.</summary>
    public const string WritePermission = "gtfo.equipment.deployable.write";

    /// <summary>The ports the body reads: the devices it detonates and the result rows it answers with. The
    /// reviewed graph declares no parameters, so none is declared here either.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("devices").Outputs("result");
    public static readonly HandlerSlot DevicesSlot = Shape.InputSlot("devices");

    /// <summary>The body table, taken from the writes that own it so a shape and its handler can never be
    /// registered apart.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers() => DetonateWrites.Handlers();

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, HandlerName, "execute", new[] { WritePermission }, Shape)
    };
}
