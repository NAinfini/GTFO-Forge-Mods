using System;
using System.Diagnostics.CodeAnalysis;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeModuleHandle
{
    /// <summary>Publishes one typed service other packages look up through <see cref="RuntimeKernel.TryService{T}"/>.
    /// A type has exactly one publisher; the service leaves with this registration. Registration window only.</summary>
    public void PublishService<T>(T service) where T : class => kernel.PublishService(this, typeof(T), service);
}

public sealed partial class RuntimeKernel
{
    internal void PublishService(RuntimeModuleHandle handle, Type type, object service)
    {
        ArgumentNullException.ThrowIfNull(handle); ArgumentNullException.ThrowIfNull(service);
        Mutable();
        RuntimeJson.Require(handle.IsRegistered, "module-unregistered", "The module handle is no longer registered.");
        RuntimeJson.Require(IsRegistrationOpen, "registration-closed", "Services are published during registration.");
        registry.PublishService(type, handle.ProviderId, service);
    }

    /// <summary>The service one registered provider published for <typeparamref name="T"/>, or false when none is
    /// published. Read on the host thread, typically once while a dependent package registers.</summary>
    public bool TryService<T>([MaybeNullWhen(false)] out T service) where T : class
    {
        ReadThread();
        return registry.TryService(out service);
    }
}
