using System.Globalization;
using ForgeMap.Generation;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    // The native null-DimensionData constructor leaves Reality.ResourceData null (RVA 0x147a715).
    // Reality uses Builder's active set; extra dimensions resolve their own DimensionResourceSetID.
    // This is an owner selection, never a fallback when another dimension's set is missing.
    private static ComplexResourceSetDataBlock? DimensionResources(Dimension dimension)
        => dimension.DimensionIndex == eDimensionIndex.Reality ? Builder.ComplexResourceSetBlock : dimension.ResourceData;

    private static ComplexResourceSetDataBlock RequireDimensionResources(Dimension dimension, uint expected, string path)
        => RequirePinnedResourceSet(DimensionResources(dimension), expected, path,
            dimension.DimensionIndex == eDimensionIndex.Reality ? "Builder.ComplexResourceSetBlock" : "Dimension.ResourceData");

    private static ComplexResourceSetDataBlock RequirePinnedResourceSet(ComplexResourceSetDataBlock? actual, uint expected,
        string path, string owner)
    {
        RequireResourceSetId(actual?.persistentID, expected, path, owner);
        return actual!;
    }

    private static void RequireResourceSetId(uint? actual, uint expected, string path, string owner)
    {
        if (actual != expected)
            throw new GenerationRefusal("gen-pin-mismatch", path, "owner=" + owner
                + " expectedResourceSet=" + expected.ToString(CultureInfo.InvariantCulture)
                + " actualResourceSet=" + (actual?.ToString(CultureInfo.InvariantCulture) ?? "null"));
    }
}
