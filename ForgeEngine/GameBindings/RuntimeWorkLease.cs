using System;
using System.Threading;
namespace ForgeEngine.GameBindings;

public sealed class RuntimeWorkLease : IDisposable
{
    internal readonly CancellationTokenSource Source = new();
    internal readonly long? Epoch;
    internal Action? Release;
    public string Consumer { get; }
    public string Status { get; private set; } = "active";
    public CancellationToken Cancellation => Source.Token;
    internal RuntimeWorkLease(string consumer, long? epoch) { Consumer = consumer; Epoch = epoch; }
    internal bool Active(long epoch)
    {
        if (Status == "active" && Epoch.HasValue && Epoch.Value != epoch) End("world-epoch-ended");
        return Status == "active";
    }
    internal void End(string status)
    {
        if (Status != "active") return;
        Status = status; var release = Release; Release = null; release?.Invoke(); Source.Cancel();
    }
    public void Dispose() => End("cancelled");
}

