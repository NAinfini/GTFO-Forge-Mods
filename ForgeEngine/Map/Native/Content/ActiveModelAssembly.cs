using System;
using System.Collections.Generic;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>What every package-owned model assembly of this module shares, whatever it is the look of: its owner, its
/// content, and the commit and rollback that follow the native Rundown publication. Committing makes the assembly the
/// active one of its owner and keeps the one it replaced; rolling back puts that one back. <typeparamref name="TSelf"/>
/// is the kind, so each kind keeps its own active table.</summary>
internal abstract class ActiveModelAssembly<TSelf> where TSelf : ActiveModelAssembly<TSelf>
{
    private protected static readonly Dictionary<string, TSelf> Active = new(StringComparer.Ordinal);
    private readonly NativeModelRuntime.NativeModelRegistration registration;
    private TSelf? previous;

    private protected ActiveModelAssembly(string owner, NativeModelContent content, NativeModelRuntime.NativeModelRegistration registration)
    { Owner = owner; Content = content; this.registration = registration; }

    internal string Owner { get; }
    internal NativeModelContent Content { get; }
    internal NativeModelAssembly Assembly => Content.Assemblies[0];
    /// <summary>Whether this assembly is committed now.</summary>
    private protected bool Committed { get; private set; }

    internal void Commit()
    {
        registration.Commit();
        Active.TryGetValue(Owner, out previous);
        Active[Owner] = (TSelf)this;
        Committed = true;
    }

    internal void Rollback()
    {
        registration.Rollback();
        if (Active.TryGetValue(Owner, out var current) && ReferenceEquals(current, this))
        {
            if (previous == null) Active.Remove(Owner);
            else Active[Owner] = previous;
        }
        previous = null;
        Committed = false;
    }
}
