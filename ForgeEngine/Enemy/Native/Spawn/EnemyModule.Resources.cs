using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using GameData;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    // Resource resolution is the existing cold plan-loading boundary. Runtime commands receive the resolved reference.
    private static readonly RuntimeResourceProvider EnemyKinds = RuntimeResourceProvider.Of(EnumerateEnemyKinds, ResolveEnemyKind);
    private static readonly RuntimeResourceProvider AbilityKinds = RuntimeResourceProvider.Of(EnumerateAbilityKinds, EnemyAbilityResources.Resolve);
    private static IReadOnlyList<ResourceRef> EnumerateAbilityKinds()
    {
        var resources = new List<ResourceRef>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var block in GameDataBlockBase<EnemyDataBlock>.GetAllBlocks())
        {
            if (block == null || !block.internalEnabled || block.AI_Abilities == null) continue;
            var counts = new int[256];
            foreach (var component in block.AI_Abilities)
            {
                if (component == null || !component.Enabled) continue;
                byte kind = (byte)component.AbilityType;
                int index = counts[kind]++;
                if (EnemyAbilityResources.TryReference(kind, index, out var reference) && seen.Add(reference.ResourceId))
                    resources.Add(reference);
            }
        }
        return resources;
    }
    private static IReadOnlyList<ResourceRef> EnumerateEnemyKinds()
    {
        var resources = new List<ResourceRef>();
        foreach (var block in GameDataBlockBase<EnemyDataBlock>.GetAllBlocks())
            if (block != null && block.internalEnabled)
                resources.Add(new("enemy", block.persistentID.ToString(CultureInfo.InvariantCulture)));
        return resources;
    }
    private static ResourceRef? ResolveEnemyKind(string id)
        => uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var type)
            && type != 0 && type.ToString(CultureInfo.InvariantCulture) == id
            && GameDataBlockBase<EnemyDataBlock>.GetBlock(type) is { internalEnabled: true }
                ? new("enemy", id) : null;
}
