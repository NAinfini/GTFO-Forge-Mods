using System;

namespace ForgeDevelopment.Native;

// Cleanup may retry, installation may not spin on an unchanged failed generation.
// Explicit disable followed by enable or a different plan grants one new attempt;
// the native backend may still refuse a process-terminal failure.
internal sealed class CapturePlanAttemptGate
{
    private string? _failed;
    internal bool CanInstall(string text) => !string.Equals(_failed, text, StringComparison.Ordinal);
    internal void Failed(string text) => _failed = text;
    internal void Disabled() => _failed = null;
}
