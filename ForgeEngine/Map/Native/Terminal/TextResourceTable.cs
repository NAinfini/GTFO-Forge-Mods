using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using GameData;

namespace ForgeMap.Native;

/// <summary>Native Text block identities; display reads use the local player's one text-language source.</summary>
internal static class TextResourceTable
{
    /// <summary>Every Text block this install loaded, in the blocks' own loaded order. A kind's whole answer is
    /// what its owner holds; a full enumeration of this kind is larger than one read may carry
    /// (`RuntimeKernel.MaximumResourcesPerRead`), which the kernel refuses by name rather than truncating, and
    /// which is why no query card enumerates this kind.</summary>
    internal static IReadOnlyList<ResourceRef> Enumerate()
    {
        var references = new List<ResourceRef>();
        var blocks = GameDataBlockBase<TextDataBlock>.GetAllBlocks();
        if (blocks == null) return references.AsReadOnly();
        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block == null || block.WasCollected) continue;
            references.Add(new ResourceRef(TextResourceContract.ResourceKind,
                block.persistentID.ToString(CultureInfo.InvariantCulture)));
        }
        return references.AsReadOnly();
    }

    /// <summary>One block by its own id, or null when this install has no Text block with it. A plan that names a
    /// block this install does not have is what the kernel reports as `stale-resource` at load, so the resolve
    /// answers nothing rather than a reference nothing stands behind.</summary>
    internal static ResourceRef? Resolve(string resourceId)
        => Block(resourceId) is null ? null : new ResourceRef(TextResourceContract.ResourceKind, resourceId);

    private static TextDataBlock? Block(string resourceId)
        => uint.TryParse(resourceId, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
            ? GameDataBlockBase<TextDataBlock>.GetBlock(id) : null;
}
