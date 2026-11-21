using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeDevelopment.Native;

internal static partial class ExperimentTargeting
{
    // FindObjectsOfType(Type) returns Object wrappers, not CLR instances of the native component class.
    // Cast through IL2CPP to the requested generated type, preserving its getters and proven nulls.
    // Query/cast errors must propagate as failures, never become "no live instance".
    internal static IEnumerable<Component> ComponentsOf(Type type)
        => (IEnumerable<Component>)typeof(ExperimentTargeting)
            .GetMethod(nameof(LoadedComponents), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).Invoke(null, null)!;

    private static IEnumerable<Component> LoadedComponents<T>() where T : Component
    {
        foreach (var item in Object.FindObjectsOfType(Il2CppType.Of<T>()))
            if (item != null)
            {
                var component = item.Cast<T>();
                if (component != null) yield return component;
            }
    }
}
