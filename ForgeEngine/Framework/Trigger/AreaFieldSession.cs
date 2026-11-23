using System;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger;

/// <summary>
/// The provider's current field table and nothing else: the registration reads through it, so a new world replaces
/// one table rather than re-registering the module — the kernel's registration window is closed for the life of
/// the process (`Mods/ForgeEngine/Native/Trigger/TriggerHost.cs`), and a provider that answered a second table would be a
/// second source of truth about which fields exist.
///
/// The table itself is the shared reader's (`ForgeEngine.Framework.AreaFieldTable`); this type is only the
/// indirection the native pump replaces. The field definitions are level-attached content, so the pump re-reads
/// them when the runtime reports a new world epoch: a field of a world whose documents are no longer installed is
/// then honestly absent, and a row that names it answers its own `unloaded` policy instead of a silently empty
/// volume.
/// </summary>
internal sealed class AreaFieldSession
{
    private AreaFieldTable _table;
    private Targeting.ObservedFamily? _family;

    public AreaFieldSession(AreaFieldTable table) => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>The table every read of the kind goes through.</summary>
    public AreaFieldTable Current => _table;

    /// <summary>This session's one declaration family, built the first time it is asked for. A session owns
    /// exactly one family, so a row's evaluator is one identity: the registry resolves a handler per binding, and
    /// a caller that compares the module's own tables against the declaration tables compares the same delegates
    /// instead of two closures built from the same session.</summary>
    public Targeting.ObservedFamily Family => _family ??= Targeting.AreaFieldNodes.Family(this);

    /// <summary>One world's snapshot, in place of the previous one. Called by the host's own pump when the
    /// runtime reports a new world epoch, on the same thread the kernel dispatches on.</summary>
    public void Replace(AreaFieldTable table) => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>The session of a caller that has no install in front of it — a direct call to `Create` in a test or
    /// in the release export — which owns the kind and answers the honest empty table.</summary>
    public static AreaFieldSession Empty { get; } = new(AreaFieldTable.Empty);
}
