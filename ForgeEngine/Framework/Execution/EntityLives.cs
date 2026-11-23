using System.Collections.Generic;
using System.Text.Json;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>Which entity lives are current, as data the layers above ask: the lives reported ended in this world, whether a held life must be re-asked of its owner, the liveness verdict for a reference, and the per-event memo of the entities an event was already read for. It reads the registry's resolvers and the clock; the query budget and the observers stay with the facade.</summary>
internal sealed class EntityLives
{
    private readonly KernelWorld world;
    private readonly KernelRegistry registry;
    private readonly KernelLog log;

    internal EntityLives(KernelWorld world, KernelRegistry registry, KernelLog log)
    {
        this.world = world;
        this.registry = registry;
        this.log = log;
    }

    /// <summary>One entity reference of a captured event, of a frame or of a publish payload, checked against the
    /// provider that answers for its kind. <paramref name="live"/> collects the reference together with the resolver
    /// that answered for it, so a later read of the same value asks that resolver again without parsing the frame a
    /// second time.</summary>
    internal void CheckEntity(EntityReference entity, List<EntityCheck>? live = null)
    {
        RuntimeJson.Text(entity.Id); RuntimeJson.Integer(entity.WorldEpoch); RuntimeJson.Integer(entity.LifeEpoch);
        RuntimeJson.Require(entity.WorldEpoch == world.WorldEpoch, "stale-world", entity.Id);
        RuntimeJson.Require(registry.TryEntityKind(entity.Id, out var kind), "entity-resolver", entity.Id);
        bool valid;
        try { valid = kind.Resolve(entity); }
        catch (Exception error)
        {
            log.LogCallbackFailedTypeOnly("entity-resolver-failed entity=" + entity.Id, error);
            throw new RuntimeContractException("entity-resolver-failed", entity.Id);
        }
        RuntimeJson.Require(valid, "stale-entity", entity.Id);
        live?.Add(new EntityCheck(entity, kind));
    }

    /// <summary>Re-asks the resolvers one value's entities were validated with. This is the only part of a frame's
    /// validation that a handler of the same activation can still invalidate, so it is the only part a repeated read
    /// repeats.</summary>
    internal void RecheckEntities(EntityCheck[] checks)
    {
        var code = EntityLivenessCode(checks, out var subject);
        if (code != null) throw new RuntimeContractException(code, subject);
    }

    /// <summary>The lives of reported kinds that ended in this world. A held reference of a reported kind is current
    /// exactly while it is not in this set, so the set is never pruned inside a world; it is emptied with the world.</summary>
    internal HashSet<EntityReference> endedLives = new();

    /// <summary>Set for one host sweep after a migration commit: the image was decoded from the old host's tables and
    /// an end reported while the migration was paused named the old world, so every held life of a reported kind is
    /// asked through its resolver once before the reports alone are trusted again.</summary>
    internal bool verifyReportedLives;

    /// <summary>Whether an entity one value names has lost its life: the whole of what a repeated look at a
    /// validated value still asks. A kind whose owner reports its ends is answered from those reports and its
    /// resolver is not called; every other kind is asked through the resolver captured with it.</summary>
    internal string? EntityLivenessCode(EntityCheck[] checks, out string subject, bool admission = false)
    {
        for (var i = 0; i < checks.Length; i++)
        {
            subject = checks[i].Entity.Id;
            if (EntityLifeCode(in checks[i], admission) is { } code) return code;
        }
        subject = "";
        return null;
    }

    /// <summary>The same answer for one held entity.</summary>
    internal string? EntityLifeCode(in EntityCheck check, bool admission = false)
    {
        if (check.Entity.WorldEpoch != world.WorldEpoch) return "stale-world";
        if (check.Kind.Reported)
        {
            if (endedLives.Count != 0 && endedLives.Contains(check.Entity)) return "stale-entity";
            // Admission is the one look that always asks: a reference handed in now has to be current now, and the
            // end reports only say which lives ended after they were held.
            if (!admission && !verifyReportedLives) return null;
        }
        bool valid;
        try { valid = check.Resolve(check.Entity); }
        catch (Exception error)
        {
            log.LogCallbackFailed("entity-resolver-failed entity=" + check.Entity.Id, error);
            return "entity-resolver-failed";
        }
        return valid ? null : "stale-entity";
    }

    internal string? EntityLivenessCode(EntityCheck[] checks) => EntityLivenessCode(checks, out _);

    /// <summary>One capture of an entity under the kind that answers for it now, or false where nothing resolves it.</summary>
    internal bool TryEntityCheck(EntityReference entity, out EntityCheck check)
    {
        var found = registry.TryEntityKind(entity.Id, out var kind);
        check = found ? new EntityCheck(entity, kind) : default;
        return found;
    }

    /// <summary>The payload of the event being enqueued or walked, and the last entity reference read out of it with
    /// the offset of that reference's text in the payload. A reference is a pure reading of its own text, so the same
    /// text in the same document reads to the same reference and is not parsed again.</summary>
    internal JsonElement entityMemoEvent;

    internal int entityMemoAt = -1;

    internal EntityReference? entityMemo;

    internal (JsonElement Event, int At, EntityReference? Entity) SaveEntityMemo() => (entityMemoEvent, entityMemoAt, entityMemo);

    internal void RestoreEntityMemo((JsonElement Event, int At, EntityReference? Entity) saved)
        => (entityMemoEvent, entityMemoAt, entityMemo) = saved;

    internal void OpenEntityMemo(JsonElement payload, int at, EntityReference? entity)
        => (entityMemoEvent, entityMemoAt, entityMemo) = (payload, entity == null ? -1 : at, entity);

    /// <summary>The last few distinct references read, in the order they were first read. Consecutive events are
    /// mostly about the same few entities — one player's shots, one enemy's hits — and a reference is immutable, so
    /// one that reads to the same id and epochs is the same reference.</summary>
    internal readonly EntityReference?[] recentEntities = new EntityReference?[4];

    internal int recentEntityNext;

    /// <summary>One entity reference (<see cref="RuntimeJson.EntityRow"/>), reused where it is the reference last read
    /// out of the same place in the current event's payload, or one of the references read most recently.</summary>
    internal EntityReference ReadEntity(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return RuntimeJson.EntityRow(value);
        var at = entityMemoEvent.ValueKind == JsonValueKind.Undefined ? -1 : RawJson.OffsetIn(entityMemoEvent, value);
        if (at >= 0 && at == entityMemoAt && entityMemo != null) return entityMemo;
        var entity = RecentEntity(value);
        if (entity == null)
        {
            entity = RuntimeJson.EntityRow(value);
            recentEntities[recentEntityNext] = entity;
            recentEntityNext = (recentEntityNext + 1) % recentEntities.Length;
        }
        if (at >= 0) { entityMemoAt = at; entityMemo = entity; }
        return entity;
    }

    /// <summary>A recently read reference whose id and epochs this value spells, or null. The shape is checked first,
    /// exactly as a full read checks it, so a value a read would refuse is refused by the same rule; a value that
    /// matches a reference that was read is one whose id and epochs a read accepts.</summary>
    internal EntityReference? RecentEntity(JsonElement value)
    {
        RuntimeJson.Shape(value, RuntimeJson.EntityFields);
        var id = value.GetProperty("id");
        var worldValue = value.GetProperty("worldEpoch");
        var lifeValue = value.GetProperty("lifeEpoch");
        if (id.ValueKind != JsonValueKind.String || worldValue.ValueKind != JsonValueKind.Number || lifeValue.ValueKind != JsonValueKind.Number
            || !worldValue.TryGetInt64(out var world) || !lifeValue.TryGetInt64(out var life)) return null;
        foreach (var entity in recentEntities)
            if (entity != null && entity.WorldEpoch == world && entity.LifeEpoch == life && id.ValueEquals(entity.Id)) return entity;
        return null;
    }

    /// <summary>Exchanges the state a host migration swaps with the staged image's. Nothing runs between the
    /// assignments; the image keeps the prior state for rollback.</summary>
    internal void SwapWith(EntityLives other)
    {
        (endedLives, other.endedLives) = (other.endedLives, endedLives);
        (verifyReportedLives, other.verifyReportedLives) = (other.verifyReportedLives, verifyReportedLives);
    }
}
