using System;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>I-MANIFEST: the Forge block fields this package's own profile appliers write.
///
/// The declaration is a public entry point of its own rather than a member of `ModuleFactory.cs`, because the
/// slice suites compile that file against a shim: they own one family of the package and deliberately leave out
/// the profile reader and its field table, so a declaration naming them would be a member those projects cannot
/// compile. The game plugin calls this from its registration window, and the release export calls it with the
/// handle it has just registered, so the manifest the release set publishes is the running registry's own
/// statement and not a second list kept beside it.</summary>
internal static partial class ModuleDefinition
{
    public static void DeclareDefinitionFields(RuntimeModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.DeclareDefinitionFields(EnemyFieldSchema.EnemyDefinitionFields);
    }
}
