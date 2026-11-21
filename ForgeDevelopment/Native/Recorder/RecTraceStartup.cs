namespace ForgeDevelopment.Native;

// This is a one-shot readiness gate on the existing authoring Update, not a
// second scheduler. No type discovery or detour work occurs before GameData.
internal sealed class RecTraceStartup
{
    private bool _pending;
    internal bool Pending => _pending;
    internal void Schedule(bool enabled) => _pending = enabled;
    internal bool Take(bool gameDataLoaded)
    {
        if (!_pending || !gameDataLoaded) return false;
        _pending = false; // A failed attempt is reported; it is never retried each frame.
        return true;
    }
    internal void Cancel() => _pending = false;
}
