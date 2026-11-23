using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

internal sealed partial class MapPluginSession
{
    private RuntimeWorkLease? _generationBoundary;
    private void ObserveGenerationWorld(long epoch, bool stopped)
    {
        ReleaseGenerationBoundary();
        if (stopped || epoch < 0) return;
        _generationBoundary = RuntimeUpstreamEvents.Subscribe(RuntimeUpstreamKind.BuildStarted,
            "ForgeMap.Generation.Start", epoch, _ => CompiledMapGenerator.BeginGeneration(),
            replayCurrent: false);
    }
    private void ReleaseGenerationBoundary()
    {
        _generationBoundary?.Dispose(); _generationBoundary = null;
    }
}
