using System;
using System.Collections.Generic;
using System.Linq;
using ForgeMap.Fields;
using ForgeEngine.Framework;

namespace ForgeMap;

internal static partial class ModuleDefinition
{
    /// <summary>
    /// I-MANIFEST: declares the Forge map block fields this package's own appliers write, on the handle the one
    /// Map registration returned and while the registration window is still open. The list is
    /// <see cref="MapFieldSchema.MapDefinitionFields"/>, which is exactly the fields `Level/` writes (a field the
    /// documents accept but no applier writes is reported as `map.field-unapplied` and is not declared), plus
    /// <see cref="ScanCoverageManifest.DefinitionFields"/>, the scan fields the scan-coverage session applies,
    /// and the outline colour read by TriggerZoneManifest and applied by TriggerZoneOutline.
    /// </summary>
    public static IReadOnlyList<string> DefinitionFields { get; } = Array.AsReadOnly(MapFieldSchema.MapDefinitionFields
        .Concat(ScanCoverageManifest.DefinitionFields)
        .Append("map.forge.triggerZone.display.color").OrderBy(id => id, StringComparer.Ordinal).ToArray());

    public static void DeclareDefinitionFields(RuntimeModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.DeclareDefinitionFields(DefinitionFields);
    }
}
