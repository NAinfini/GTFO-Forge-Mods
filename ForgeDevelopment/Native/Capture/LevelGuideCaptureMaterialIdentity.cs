using System.Reflection;

namespace ForgeDevelopment.Native;

internal static class LevelGuideCaptureMaterialIdentity
{
    // ReflectedType changes when GetMethods returns an inherited method. Harmony supplies the
    // declaring method. Its module/token/declaring type, unlike MethodInfo equality, stay equal.
    internal static string Method(MethodBase method)
        => method.Module.ModuleVersionId + "/" + method.MetadataToken + "/" + method.DeclaringType?.FullName;
}
