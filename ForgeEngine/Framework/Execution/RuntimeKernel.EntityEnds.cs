using System;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeModuleHandle
{
    /// <summary>
    /// Reports that one life of an entity this module owns has ended. Only a kind this module declared in
    /// <see cref="RuntimeModule.EntityEndReports"/> may be reported (`entity-end-owner` otherwise), and for such a kind
    /// this report is the only way the kernel learns of the end: everything that holds the life — a schedule, a numeric
    /// lease, an owned or native handle, an effect, a deferred result — ends at the kernel's next look with the same
    /// code a resolver answering false produced (`stale-entity`), and every variable scope keyed by the entity
    /// (player, weapon, object, enemy) is released.
    ///
    /// Call it from every place a life of the kind ends — death, despawn, destruction, collection by the game, leaving
    /// the session, the end of the world — and make the kind's resolver answer false for the life from then on. It may
    /// be called at any moment on the simulation thread, inside a dispatch or a resolver included: the end is recorded
    /// at once, the entries that hold the life end at the next safe point that looks at them (a pulse about to fire, or
    /// the start of the next host tick), and a scope release that arrives inside a dispatch is applied when the tick
    /// ends. The answer is true when this call recorded the end, false when the reference names another world or the
    /// same life was already reported.
    /// </summary>
    public bool EntityEnded(EntityReference entity) => kernel.EntityEnded(this, entity);
}

public sealed partial class RuntimeKernel
{

    private static readonly string[] EntityScopeKinds =
        { VariableScopeKinds.Player, VariableScopeKinds.Weapon, VariableScopeKinds.Object, VariableScopeKinds.Enemy };

    internal bool EntityEnded(RuntimeModuleHandle owner, EntityReference entity)
    {
        ReadThread(); ArgumentNullException.ThrowIfNull(owner);
        RuntimeEntityReferences.Validate(entity);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var kind = RuntimeJson.KindOf(entity.Id);
        RuntimeJson.Require(registry.Tables.EntityEndReports.TryGetValue(kind, out var reporter) && reporter == owner.ProviderId,
            "entity-end-owner", entity.Id);
        if (!world.worldStarted || entity.WorldEpoch != WorldEpoch || !entityLives.endedLives.Add(entity)) return false;
        // The scope release is a write to the variable table, so it waits for the safe point when the report arrives
        // inside a dispatch or a read-only notification; the end itself is only recorded, which is safe anywhere.
        if (advancing || notifyingLifecycle || notifyingBehaviorTrace || observingEntities)
        {
            foreach (var scope in EntityScopeKinds)
                if (kernelVariables.queuedScopeReleaseKeys.Add((scope, entity.Id))) kernelVariables.queuedScopeReleases.Add((scope, entity.Id));
        }
        else
            foreach (var scope in EntityScopeKinds) kernelVariables.variables.ClearScope(scope, entity.Id);
        return true;
    }
}
