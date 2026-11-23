using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeMap;

/// <summary>The live identity of one authored model in one world. Native placement owns the body; this object is
/// the stable instance the map-object provider addresses and the weapon impact resolver returns.</summary>
internal sealed class PlacedModelInstance
{
    private readonly double[] _position;
    private readonly IReadOnlyList<double> _positionView;
    public PlacedModelInstance(PlacedModel model, IReadOnlyList<double> position)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _position = position.ToArray(); _positionView = Array.AsReadOnly(_position);
        Address = PlacedModelAddress.Of(model.Id);
    }
    public PlacedModel Model { get; }
    public long LifeEpoch { get; internal set; }
    public IReadOnlyList<double> Position => _positionView;
    internal MapObjectReference? Address { get; }
    internal void SetPosition(double x, double y, double z)
    { _position[0] = x; _position[1] = y; _position[2] = z; }
}

internal sealed class PlacedModelSource : IMapObjectSource
{
    internal sealed class LifeSlot
    {
        internal PlacedModelInstance? Instance;
        internal long Generation;
    }
    /// <summary>What restoring a saved transform state throws when it names an older life of a model than the one placed.</summary>
    internal const string StateLifeStale = "placed-model-transform-state-life";
    private readonly Dictionary<string, LifeSlot> _lives = new(StringComparer.Ordinal);
    private long _world = long.MinValue;
    public string Category => PlacedModelAddress.Category;
    internal void BeginWorld(long world)
    {
        if (_world == world) return;
        _world = world;
        foreach (var pair in _lives) pair.Value.Instance = null;
        _lives.Clear();
    }
    public void Load(IEnumerable<PlacedModelInstance> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var next = models.ToDictionary(value => value.Model.Id, StringComparer.Ordinal);
        foreach (var value in next.Values)
        {
            var life = BindLife(value.Model.Id);
            if (ReferenceEquals(life.Instance, value)) continue;
            value.LifeEpoch = checked(life.Generation + 1);
            life.Generation = value.LifeEpoch;
        }
        foreach (var value in next.Values) BindLife(value.Model.Id).Instance = value;
        foreach (var pair in _lives)
            if (!next.ContainsKey(pair.Key)) pair.Value.Instance = null;
    }
    internal void RestoreGeneration(PlacedModelInstance instance, long generation)
    {
        if (!Mine(instance) || generation < instance.LifeEpoch || generation <= 0)
            throw new InvalidOperationException(StateLifeStale);
        instance.LifeEpoch = generation;
        BindLife(instance.Model.Id).Generation = generation;
    }
    public PlacedModelInstance? ById(string? id) => id != null && _lives.TryGetValue(id, out var life) ? life.Instance : null;
    private bool Mine(object instance) => instance is PlacedModelInstance value && ReferenceEquals(ById(value.Model.Id), value);
    public MapObjectReference? TryAddress(object instance) => Mine(instance) ? ((PlacedModelInstance)instance).Address : null;
    public MapObjectReference? Parse(string text) => PlacedModelAddress.TryParse(text);
    public MapObjectObservation? Read(object instance) => Mine(instance) ? new MapObjectObservation(true, null, null) : null;
    public double[]? Position(object instance) => Mine(instance) ? ((PlacedModelInstance)instance).Position.ToArray() : null;
    public bool IsCurrentAddress(object instance, MapObjectReference address) => Mine(instance) && TryAddress(instance) == address;
    // The same slot owns both membership and generation. A bound read survives replacement/removal without
    // looking the model up again; a world boundary retires the old slots before their index is cleared.
    internal LifeSlot BindLife(string id)
    {
        if (!_lives.TryGetValue(id, out var life)) _lives.Add(id, life = new LifeSlot());
        return life;
    }
    public void Report(string message) { }
}

internal sealed partial class MapObjectModule
{
    private readonly PlacedModelSource _placedModels = new();
    public PlacedModelSource PlacedModels { get { CheckThread(); return _placedModels; } }
}
