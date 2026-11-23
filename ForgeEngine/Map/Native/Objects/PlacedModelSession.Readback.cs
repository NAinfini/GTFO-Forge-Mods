using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using UnityEngine;

namespace ForgeMap.Native;

internal sealed partial class PlacedModelSession
{
    // Read the owned collider, not scene candidates or authored dimensions. A model with an explicit shot box
    // has exactly that target; inactive bodies remain observable and a missing declared box fails closed.
    internal IReadOnlyList<MapModelInspection> InspectColliders(long world, MapObjectModule owner)
    {
        if (_disposed || world != _kernel.WorldEpoch || !ReferenceEquals(owner, _mapObjects))
            throw new RuntimeContractException("map-inspection-stale-world", "The placed-model owner or world is no longer current.");
        var found = new List<MapModelInspection>();
        if (_placedWorld != world) return found;
        foreach (var entry in _entries.Values)
        {
            var model = entry.Instance;
            if (entry.Root == null || entry.Root.WasCollected || model.Model.Level != _level()
                || !ReferenceEquals(owner.PlacedModels.ById(model.Model.Id), model)
                || owner.PlacedModels.TryAddress(model) != PlacedModelAddress.Of(model.Model.Id))
                throw new RuntimeContractException("map-inspection-model-stale", "The placed-model body no longer matches its registered identity.");
            var shot = model.Model.ShotCollider is { Enabled: true };
            var collider = shot ? entry.ShotCollider : entry.Root.GetComponent<BoxCollider>();
            if (!shot && collider == null) continue; // A visual-only assembly has no shooting target.
            var reference = _kernel.ResolveEntityInstance(EntityKinds.MapObject, model);
            if (collider == null || collider.WasCollected || !ReferenceEquals(PlacedModelHitRegistry.Resolve(collider), model)
                || reference == null || reference.WorldEpoch != world || !_kernel.IsEntityCurrent(reference))
                throw new RuntimeContractException("map-inspection-model-collider-unavailable", "The registered model shooting target is unavailable.");
            found.Add(new(collider, reference, model.Model.Id, model.Model.Room.RoomId));
        }
        return found;
    }
}
