using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeEngine.Framework;

/// <summary>A load-bound equipment relation. The event player is never returned as the equipment subject.</summary>
public delegate EquipmentHolderMatch? EquipmentHolderMatcher(EquipmentHolderSubject player);

/// <summary>The canonical player identity decoded at the event boundary, before mount matching.</summary>
public readonly struct EquipmentHolderSubject
{
    public EquipmentHolderSubject(EntityReference player)
    {
        Player = player ?? throw new ArgumentNullException(nameof(player));
        const string prefix = EntityKinds.Player + ":";
        PlayerNumber = player.Id.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(player.Id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0 ? number : 0;
    }
    public EntityReference Player { get; }
    public long PlayerNumber { get; }
}

/// <summary>One recorded equipment life and its real holder, with probes bound to those native lives.
/// Created when identities are observed, never while matching an event.</summary>
public sealed class EquipmentHolderMatch
{
    public EquipmentHolderMatch(EntityReference equipment, EntityReference holder,
        Func<bool> equipmentCurrent, Func<bool> holderCurrent)
    {
        Equipment = equipment; Holder = holder;
        Subject = new EquipmentHolderSubject(holder);
        EquipmentCurrent = equipmentCurrent; HolderCurrent = holderCurrent;
        Subjects = new[] { equipment };
    }
    public EntityReference Equipment { get; }
    public EntityReference Holder { get; }
    public Func<bool> EquipmentCurrent { get; }
    public Func<bool> HolderCurrent { get; }
    internal IReadOnlyList<EntityReference> Subjects { get; }
    internal EquipmentHolderSubject Subject { get; }
}
