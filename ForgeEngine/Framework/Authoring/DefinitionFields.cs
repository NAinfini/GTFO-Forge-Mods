using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeModuleHandle
{
    /// <summary>I-MANIFEST. The Forge block fields this provider's own profile appliers write, declared once
    /// inside the registration window. One field id has exactly one owner, and only a `forge.` block field is
    /// a field a mod carries: the runtime has no copy of `catalog/authoring/object-fields.json`, so it checks the id's
    /// shape here and leaves existence to the release-set check. The manifest reports the merged declarations,
    /// which is the one fact the gameplay ledger judges a field row's mod status by.</summary>
    public void DeclareDefinitionFields(IReadOnlyCollection<string> fieldIds)
        => kernel.DeclareDefinitionFields(this, fieldIds);
}

public sealed partial class RuntimeKernel
{
    internal void DeclareDefinitionFields(RuntimeModuleHandle handle, IReadOnlyCollection<string> fieldIds)
    {
        ArgumentNullException.ThrowIfNull(fieldIds);
        Mutable();
        RuntimeJson.Require(handle.IsRegistered, "module-unregistered", "A disposed module cannot declare definition fields.");
        RuntimeJson.Require(IsRegistrationOpen, "registration-closed",
            "Definition fields are declared before the host freezes registration.");
        registry.DeclareDefinitionFields(handle.ProviderId, fieldIds);
    }
}
