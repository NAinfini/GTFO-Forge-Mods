using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The generator half of the map-object provider. A power generator is one more category of `gtfo.map_object`
/// (ruling 148.4): it has an address, an identity in the one namespace, an observation and a position, its two
/// facts are published by <see cref="MapObjectModule"/> under the same registration, authority gate and
/// `map-object` attachment matcher as a door and a terminal, and the same module owns the generator resource
/// table the value row reads. There is no `gtfo.level_object` generator half, no second entity namespace and no
/// second publisher for these rows.
///
/// This half is a partial declaration rather than a second module for the same reason the zone half is: exactly
/// one map-object provider exists and one publication path serves it. The declared half composes the definition;
/// this half adds the generator category's reader, publishes the two facts a native callback produces and keeps
/// the table the value row and the resource kind answer from.
/// </summary>
internal sealed partial class MapObjectModule
{
    /// <summary>The generators this module addresses. A module that was composed without a native reader answers
    /// for none of them rather than refusing to exist: the generator category is optional to this provider
    /// exactly as the packages that place generators are optional to the install.</summary>
    private IMapObjectSource _generatorSource = EmptyGeneratorSource.Instance;

    /// <summary>The generator addresses registered by placement or a real cell fact, by address text: the resource
    /// table `forge.condition.predicate.power` enumerates and resolves, and the mark that says which addresses
    /// are generators' rather than groups'. A group address is a fact subject, never a resource, so the two key
    /// forms are told apart by <see cref="MapObjectGeneratorAddress.IsGroupKey"/>.</summary>
    private readonly Dictionary<string, string> _generators = new(StringComparer.Ordinal);

    /// <summary>The same registration, composed with the generators' own reader and category. The generator
    /// source is handed over before <see cref="Register"/> because every category is declared on the one
    /// definition at registration time; a module that is never given one registers the empty table and publishes
    /// no generator fact.</summary>
    public MapObjectModule(IForgeEngine kernel, RuntimeLogLevel logLevel, IMapObjectSource doors,
        IMapObjectSource terminals, Func<string, string, object?> resolve, Func<object, EntityReference?>? players,
        Func<bool> authority, Action<string> report, Action<string> log, Func<MapLevelReference?>? currentLevel = null,
        Func<object, object?>? hit = null, TriggerZoneSource? zones = null, IMapObjectSource? generators = null)
        : this(kernel, logLevel, doors, terminals, resolve, players, authority, report, log, currentLevel, hit, zones)
        => _generatorSource = generators ?? EmptyGeneratorSource.Instance;

    /// <summary>The value row's reader, handed in by the session that owns the level: it answers one generator's
    /// powered flag and its group's counts from the native instance the key names. A registration with no reader
    /// answers the row with a refusal instead of an empty value, which is what keeps a row that cannot be read
    /// from looking like a row that read nothing.</summary>
    public Func<string, JsonElement?>? GeneratorStateReader { get; set; }

    /// <summary>Index an exactly resolved placed generator without publishing a cell-change event. Object
    /// selection must work at build completion, before the first real cell interaction.</summary>
    public void RegisterPlacedGenerator(object generator)
    {
        CheckThread();
        if (!TrySubject(_generatorSource, generator, out var subject)) return;
        _generators[subject.Address.ToString()] = subject.Address.ToString();
    }

    /// <summary>
    /// One generator's cell changing, from the generator's own replicated state change. The instance is re-read
    /// after the native body returned, so the direction the fact reports is the state the generator is really in
    /// — the game has exactly two, powered and unpowered — and the running state (`state`, spelled the way the
    /// native status member spells it, and `powered`) and the group counts are the same read. The cell the
    /// callback named and the player it named are the game's own record of what changed, which is the one thing
    /// the instance does not carry.
    /// </summary>
    public void GeneratorCellChanged(object generator, string? cell, object? actor, EntityReference? item = null)
    {
        CheckThread();
        if (!TrySubject(_generatorSource, generator, out var subject) || !Read(subject, out var observation)) return;
        var snapshot = observation.Generator!;
        bool inserted = snapshot.Powered;
        // The address is remembered before the fact is published: a repeated reading of one state publishes no
        // second event, and the resource table still has to answer for the generator it named.
        _generators[subject.Address.ToString()] = subject.Address.ToString();
        if (!Admit(subject, "generator.cell", _generatorCellGate, snapshot.StateKey,
            out var raised, out var transition)) return;
        var values = raised.Values;
        values.Entity(Blocks.TriggerMapGeneratorCell.OutGenerator, subject.Reference);
        // A cell the callback could not name leaves the port out rather than publishing a null: the key is
        // declared optional, and an absent port is the framework's own way to say "not observable".
        if (!string.IsNullOrEmpty(cell)) values.Text(Blocks.TriggerMapGeneratorCell.OutCell, cell);
        // The cell's level-item instance: the same captured reference `item_pickup`, `item_placed` and
        // `item_removed` publish (contract M16), so a rule host recognises one item across all four facts.
        if (item is not null) values.Entity(Blocks.TriggerMapGeneratorCell.OutItem, item);
        values.Boolean(Blocks.TriggerMapGeneratorCell.OutInserted, inserted);
        if (Actor(actor) is { } player) values.Entity(Blocks.TriggerMapGeneratorCell.OutActor, player);
        values.Integer(Blocks.TriggerMapGeneratorCell.OutConnected, snapshot.Connected);
        values.Integer(Blocks.TriggerMapGeneratorCell.OutTotal, snapshot.Total);
        values.Boolean(Blocks.TriggerMapGeneratorCell.OutPowered, snapshot.Powered);
        Raise(raised, subject, "generator.cell", transition);
    }

    /// <summary>
    /// One generator group's every member reading powered, from the group's own state callback. The native
    /// cluster state carries a fog step and no count, so the instance is re-read here and the counts are the
    /// group's own members: the fact is published only when the group holds at least one member and every one of
    /// them reads powered, which the module re-checks rather than trusting a caller.
    /// </summary>
    public void GeneratorGroupConnected(object group)
    {
        CheckThread();
        if (!TrySubject(_generatorSource, group, out var subject) || !Read(subject, out var observation)) return;
        var snapshot = observation.Group!;
        if (snapshot.Total == 0 || snapshot.Connected != snapshot.Total) return;
        if (!Admit(subject, "generator.group_connected", _generatorClusterGate, snapshot.StateKey, out var raised, out var transition)) return;
        var values = raised.Values;
        values.Entity(Blocks.TriggerMapGeneratorCluster.OutCluster, subject.Reference);
        values.Integer(Blocks.TriggerMapGeneratorCluster.OutConnected, snapshot.Connected);
        values.Integer(Blocks.TriggerMapGeneratorCluster.OutTotal, snapshot.Total);
        Raise(raised, subject, "generator.group_connected", transition);
    }

    /// <summary>Every generator this provider currently holds, one reference each, addressed by the same key
    /// the cell fact publishes. The set is the generator keys this world published a fact for; the order is
    /// ordinal, so the answer never depends on a dictionary's layout. Placement registration emits no fact.</summary>
    public IReadOnlyList<ResourceRef> EnumerateGenerators()
    {
        CheckThread();
        var references = new List<ResourceRef>(_generators.Count);
        foreach (var key in _generators.Keys)
            references.Add(new ResourceRef(GeneratorContract.GeneratorResourceKind, key));
        references.Sort(static (left, right) => string.CompareOrdinal(left.ResourceId, right.ResourceId));
        return references.AsReadOnly();
    }

    /// <summary>One generator by the address text the facts publish, or null when this world holds none. A key
    /// that survived a level change resolves nothing rather than a same-serial generator of the new level,
    /// because the table is dropped with the world it was read from. A text that is not a generator address, or
    /// one that names a group, resolves nothing whether or not the table happens to hold it.</summary>
    public ResourceRef? ResolveGenerator(string resourceId)
        => MapObjectGeneratorAddress.TryParse(resourceId) is { } address
            && !MapObjectGeneratorAddress.IsGroupKey(address.Key) && _generators.ContainsKey(resourceId)
            ? new ResourceRef(GeneratorContract.GeneratorResourceKind, resourceId) : null;

    /// <summary>One generator's own state and its group's counts, for `forge.condition.predicate.power`. The
    /// game-bound half answers the powered flag and the counts; this only answers the shape, and a read of a
    /// generator this world does not hold is a refusal by name rather than a key nothing can answer.</summary>
    public void ReadGeneratorState(EvaluationContext context, EvaluationOutputs outputs)
    {
        CheckThread();
        if (ValueRowReading.ResourceId(context, "generator") is not { } id)
            throw new RuntimeContractException("generator-resource-missing", "The generator input names no resource.");
        if (!_generators.ContainsKey(id))
            throw new RuntimeContractException("generator-not-found", id);
        var reader = GeneratorStateReader
            ?? throw new RuntimeContractException("generator-unavailable", id);
        var reading = reader(id) ?? throw new RuntimeContractException("generator-unavailable", id);
        outputs.Json(0, ValueRowReading.Require(reading, "value", id));
        outputs.Json(1, ValueRowReading.Require(reading, "connected", id));
        outputs.Json(2, ValueRowReading.Require(reading, "total", id));
    }
}

/// <summary>
/// The reader of a module that was composed without a native generator half: it addresses no instance, reads
/// none and reports nothing. The category stays declared, so a plan that mounts a generator in a build without
/// the native half is refused by the address it names rather than by a category the provider never had.
/// </summary>
internal sealed class EmptyGeneratorSource : IMapObjectSource
{
    internal static readonly EmptyGeneratorSource Instance = new();

    public string Category => MapObjectGeneratorAddress.Category;

    public MapObjectReference? TryAddress(object instance) => null;

    public MapObjectReference? Parse(string text) => MapObjectGeneratorAddress.TryParse(text);

    public MapObjectObservation? Read(object instance) => null;

    public double[]? Position(object instance) => null;

    public bool IsCurrentAddress(object instance, MapObjectReference address) => false;

    public void Report(string message) { }
}
