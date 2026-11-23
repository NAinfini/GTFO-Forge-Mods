using System.Collections.Generic;
using ForgeCore;

namespace ForgeMap.Native;

internal sealed partial class MapPluginSession
{
    internal PlacedModelSession? PlacedModels { get; private set; }

    private void StartPlacedModels(IReadOnlyList<PlacedModel> rows, PlacedImageTable images, IReadOnlyList<MapModelAssembly> assemblies)
        => PlacedModels = PlacedModelSession.Start(_kernel, MapObjects!, LevelIdentity.Read, _report, rows, images, assemblies,
            () => !Down && Session.IsHost, SpawnRules);

    private void CleanupPlacedModels(List<System.Exception> errors)
    {
        var models = PlacedModels;
        PlacedModels = null;
        if (models != null) ForgeEngine.Framework.DomainSession.Cleanup(models.Dispose, errors);
    }
}
