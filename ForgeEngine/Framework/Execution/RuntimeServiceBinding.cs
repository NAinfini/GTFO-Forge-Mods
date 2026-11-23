using System;
using System.Diagnostics.CodeAnalysis;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeModuleHandle
{
    /// <summary>Resolve a service once per world or registry change, including an absent provider.</summary>
    public RuntimeServiceBinding<T> BindService<T>() where T : class => new(kernel);
}

public sealed partial class RuntimeKernel
{
    // Module registration/removal changes the version; publishing within a registration changes the count.
    internal (long World, long Modules, int Services) ServiceBindingStamp
        => (WorldEpoch, registry.ModuleTableVersion, registry.Services.Count);
}

/// <summary>A host-thread service reference that also caches absence. No registry lookup on stable frames.</summary>
public sealed class RuntimeServiceBinding<T> where T : class
{
    private readonly RuntimeKernel kernel;
    private (long World, long Modules, int Services) stamp;
    private bool known;
    private T? value;

    /// <summary>Create a host binding before a consumer module registers its resolvers.</summary>
    public RuntimeServiceBinding(IForgeEngine runtime)
        => kernel = runtime as RuntimeKernel ?? throw new ArgumentException("Service bindings require the Forge Engine host.", nameof(runtime));

    public bool TryGet([NotNullWhen(true)] out T? service)
    {
        var current = kernel.ServiceBindingStamp;
        if (!known || stamp != current)
        {
            ((IForgeEngine)kernel).TryService<T>(out value);
            stamp = current;
            known = true;
        }
        service = value;
        return service != null;
    }
}
