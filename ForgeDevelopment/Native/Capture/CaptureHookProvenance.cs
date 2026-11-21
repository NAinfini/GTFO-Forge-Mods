using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;

namespace ForgeDevelopment.Native;

// A composite owner name alone is never evidence. Resolve the current physical set,
// exact admitted callback manifest, readonly bindings, and logical writer state.
internal static class CaptureHookProvenance
{
    internal const string SharedOwner = "ForgeCore.Observe/ForgeDevelopment.Native.SharedHooks";
    internal static bool ReadOnly(DevelopmentNativeHooks.Manifest? manifest)
    {
        if (manifest == null || manifest.DamageWriterActive || manifest.Consumers.Length == 0) return false;
        try
        {
            foreach (var consumer in manifest.Consumers)
            {
                if (consumer.Callbacks.Length == 0 || consumer.Callbacks.Any(callback =>
                    callback.Method.DeclaringType?.Assembly != typeof(CaptureHookProvenance).Assembly)) return false;
                DevelopmentHookCallbacks.Plan.Create(manifest.Target, consumer.Callbacks);
            }
            return true;
        }
        catch { return false; }
    }
    internal static bool Physical(Patch patch, DevelopmentNativeHooks.Manifest? manifest, string phase)
    {
        if (!ReadOnly(manifest) || patch.owner != SharedOwner) return false;
        var method = phase == "prefix" ? manifest!.Patches.Prefix : phase == "postfix" ? manifest!.Patches.Postfix
            : phase == "finalizer" ? manifest!.Patches.Finalizer : null;
        return method != null && patch.PatchMethod == method;
    }
    internal static bool ExactConsumer(DevelopmentNativeHooks.Manifest manifest, string name, DevelopmentNativeHooks.Lane lane,
        params DevelopmentHookCallbacks.Callback[] expected)
    {
        var matches = manifest.Consumers.Where(consumer => consumer.Name == name).ToArray();
        return matches.Length == 1 && matches[0].Lane == lane && matches[0].Callbacks.SequenceEqual(expected);
    }
    internal static object? Describe(DevelopmentNativeHooks.Manifest? manifest)
    {
        if (manifest == null) return null;
        static object Method(MethodInfo method) => new
        {
            method = method.DeclaringType?.FullName + "." + method.Name,
            assembly = method.Module.Assembly.GetName().Name,
            moduleVersionId = method.Module.ModuleVersionId,
            token = method.MetadataToken
        };
        return new
        {
            manifest.EvidenceScope,
            manifest.DamageWriterActive,
            prefix = Method(manifest.Patches.Prefix),
            postfix = Method(manifest.Patches.Postfix),
            finalizer = Method(manifest.Patches.Finalizer),
            consumers = manifest.Consumers.OrderBy(consumer => consumer.Name, StringComparer.Ordinal).Select(consumer => new
            {
                name = consumer.Name,
                lane = consumer.Lane.ToString(),
                callbacks = consumer.Callbacks.Select(callback => new
                {
                    phase = callback.Phase.ToString(),
                    callback = Method(callback.Method)
                }).ToArray()
            }).ToArray()
        };
    }
    internal static string Fingerprint(DevelopmentNativeHooks.Manifest manifest) => JsonSerializer.Serialize(Describe(manifest));
}
