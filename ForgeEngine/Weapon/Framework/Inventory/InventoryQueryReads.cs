using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The magazine numbers one equipment life carries right now: the rounds loaded, the magazine's own
/// capacity, and the rounds left in the pool its slot draws from.</summary>
internal sealed record EquipmentAmmo(int Clip, int ClipMaximum, int Reserve);

/// <summary>The two world reads behind <see cref="InventoryQueryContract.EquipmentAmmoCapability"/> and
/// <see cref="InventoryQueryContract.PocketItemCountCapability"/>. Implemented by this package's native half,
/// which is the only place a game object is reached: the row bodies below hold this interface and never a game
/// type, so every row is exercised against a double and none needs a dispatch to be answered. A read that cannot
/// be answered returns false with the code it refused by rather than a substituted number, so a plan can never
/// read a zero the game never reported.</summary>
internal interface IInventoryQuerySource
{
    /// <summary>The magazine of one equipment life, or false with the code the read refused by: a life this
    /// machine does not answer for right now, a slot whose item has no magazine, or a pool that cannot be read.
    /// </summary>
    bool TryEquipmentAmmo(EntityReference equipment, out EquipmentAmmo ammo, out string code);

    /// <summary>How many of one item resource a holder's backpack holds right now, or false with the code the
    /// read refused by: a holder that is not a player this machine can name, or a resource that is not an item.
    /// </summary>
    bool TryHeldCount(EntityReference holder, string resourceId, out int count, out string code);

}

/// <summary>The two read rows this package evaluates on demand. Each body is a pure function of the evaluated
/// node and the source the host attached, which is what an `observe` row over a `state` capability is: the ports are read from the node's own inputs, the entity the row is about is charged to the plan's query
/// budget and checked current by the kernel, and the answer comes from the attached source or the row refuses by
/// name. Nothing is cached between evaluations, so a re-evaluation reads the game again.
///
/// <see cref="Installed"/> is the attached source and its own thread: the native half installs it from the thread
/// that owns the game objects it reads, and a read from any other thread is refused rather than answered through
/// interop off the game's thread.</summary>
internal sealed class InventoryQueryReads
{
    /// <summary>No source is attached. The export process and any host that declared this provider without its
    /// game half answer every read with this code: a registration can only promise the rows it can answer.
    /// </summary>
    public const string SourceUnavailableCode = "inventory-query-source-unavailable";
    /// <summary>A required port of the node is absent or not the kind the row declares.</summary>
    public const string MissingFieldCode = "missing-field";

    private readonly IInventoryQuerySource _source;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private InventoryQueryReads(IInventoryQuerySource source) => _source = source;

    /// <summary>The source of the host this package is loaded into, or null while none is attached.</summary>
    public static InventoryQueryReads? Installed { get; private set; }

    /// <summary>Attaches one host's source and answers the reads a session detaches with.</summary>
    public static InventoryQueryReads Attach(IInventoryQuerySource source)
        => Installed = new InventoryQueryReads(source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>Detaches exactly the reads one attach returned: a session that is not the installed one leaves
    /// the installed one alone, so a disposal cannot detach a machine that replaced it.</summary>
    public static void Detach(InventoryQueryReads reads)
    {
        if (ReferenceEquals(Installed, reads)) Installed = null;
    }

    /// <summary>The evaluator table the registration carries. Each name is a handler the rows above bind, and
    /// the shape beside it is the one the contract declares, so a body and its ports travel together.
    /// </summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [InventoryQueryContract.EquipmentAmmoHandler] = EquipmentAmmoRow,
        [InventoryQueryContract.PocketItemCountHandler] = PocketItemCountRow
    };

    /// <summary>`forge.query.equipment.ammo`: the equipment life the node names, answered as the clip it has
    /// loaded, the magazine it can hold and its slot's remaining reserve.</summary>
    private static void EquipmentAmmoRow(EvaluationContext context, EvaluationOutputs outputs)
    {
        var reads = Require();
        var equipment = Reference(context, "equipment");
        Snapshot(context, equipment);
        Ammo(reads._source, equipment, outputs);
    }

    /// <summary>The equipment read's answer as a function of the source and the life it is about: the row above
    /// supplies the port, the budgeted currency check and the attached source, and a source that refuses keeps its
    /// own code rather than being answered with a number.</summary>
    internal static void Ammo(IInventoryQuerySource source, EntityReference equipment, EvaluationOutputs outputs)
    {
        if (!source.TryEquipmentAmmo(equipment, out var ammo, out var code))
            throw new RuntimeContractException(code, "Equipment ammunition read refused: " + code);
        InventoryQueryContract.AmmoAnswer(ammo, outputs);
    }

    /// <summary>`forge.query.player.item_count`: the native pocket read, answered as the count itself. The resource is read off the node's own `item` input;
    /// the read answers the number of that item the backpack holds.</summary>
    private static void PocketItemCountRow(EvaluationContext context, EvaluationOutputs outputs)
    {
        var reads = Require();
        var holder = Reference(context, "holder");
        Snapshot(context, holder);
        Count(reads._source, holder, Item(context.Inputs), outputs);
    }

    /// <summary>The count row's answer as a function of the source, the holder and the item resource: the pocket
    /// count itself, and a source that refuses keeps its own code.</summary>
    internal static void Count(IInventoryQuerySource source, EntityReference holder, string resourceId, EvaluationOutputs outputs)
    {
        if (!source.TryHeldCount(holder, resourceId, out int count, out var code))
            throw new RuntimeContractException(code, "Pocket item read refused: " + code);
        outputs.Integer(0, count);
    }

    /// <summary>The `item` resource one input frame carries, refused by the port's own name when it is absent or
    /// not the resource shape a compiled resource input has.</summary>
    internal static string Item(JsonElement inputs)
    {
        if (!inputs.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("resourceId", out var resource) || resource.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException(MissingFieldCode, "item");
        return resource.GetString()!;
    }

    /// <summary>The installed source, refused by name while none is attached and only read from its own thread.
    /// </summary>
    private static InventoryQueryReads Require()
    {
        if (Installed is not { } reads)
            throw new RuntimeContractException(SourceUnavailableCode, "No inventory query source is attached.");
        reads.CheckThread();
        return reads;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException(SourceUnavailableCode, "The inventory query source is read off its own thread.");
    }

    /// <summary>The entity one port names. The port is declared by the row, so an input of another shape is a
    /// malformed node and is refused by the port's own name.</summary>
    private static EntityReference Reference(EvaluationContext context, string port)
    {
        if (!context.Inputs.TryGetProperty(port, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException(MissingFieldCode, port);
        return RuntimeJson.Entity(value);
    }

    /// <summary>The budgeted world read every row starts with: the entity the row is about is charged to the
    /// plan's query budget and checked against the kernel's own entity table before any native object is touched,
    /// so a life that is gone refuses here instead of being read from a stale handle.</summary>
    private static void Snapshot(EvaluationContext context, EntityReference reference)
    {
        if (!context.Query.TrySnapshot(reference, out _, out var code))
            throw new RuntimeContractException(code, "Entity read failed: " + code);
    }
}
