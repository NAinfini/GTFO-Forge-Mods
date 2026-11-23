using System;
using System.Collections.Generic;
using System.IO;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>One package-owned, probe-admitted model assembly that is the look of a custom mission item on one level. It follows
/// the placed-model lifecycle (prepare while the package is admitted, commit with the native Rundown publication, roll back
/// or dispose with the content host), but is found by Item id at spawn time instead of being placed by a room.</summary>
internal sealed class CarryItemAssembly : ActiveModelAssembly<CarryItemAssembly>, IDisposable
{
    internal CarryItem Item { get; }

    private CarryItemAssembly(CarryItem item, string owner, NativeModelContent content, NativeModelRuntime.NativeModelRegistration registration)
        : base(owner, content, registration) => Item = item;

    internal static IReadOnlyList<CarryItemAssembly> Prepare(string package, IReadOnlyList<CarryItem> items, Func<MapLevelReference?> readLevel)
    {
        var prepared = new List<CarryItemAssembly>();
        string path = package + "/forge/" + CarryItemManifest.FileNameOnly;
        foreach (var item in items)
        {
            try
            {
                if (!DocumentReading.Known(item.Content, "modelAssemblies", "nativeBindings", "resourceDefinitions", "resourceScope", "resourcePackages"))
                    throw new InvalidDataException("model-content-shape");
                var content = NativeModelContent.Read(item.Content, "map");
                if (content.Assemblies.Count != 1 || content.Assemblies[0].Id != item.Id)
                    throw new InvalidDataException("carry-item-assembly-mismatch");
                string owner = "carry:" + package + ":" + item.Level + ":" + item.Id;
                var registration = NativeModelRuntime.Prepare(owner, content, () => readLevel() == item.Level);
                prepared.Add(new(item, owner, content, registration));
            }
            catch (RuntimeContractException error)
            { throw new MapPackageDocumentException(error.Code, "id=" + item.Id + "; " + error.Message, path); }
            catch (Exception error) when (error is not MapPackageDocumentException)
            { throw new MapPackageDocumentException("carry-item-content-invalid", "id=" + item.Id + "; " + error.GetBaseException().Message, path); }
        }
        return Array.AsReadOnly(prepared.ToArray());
    }

    /// <summary>The committed assembly that gives Item <paramref name="itemId"/> its look on <paramref name="level"/>.</summary>
    internal static CarryItemAssembly? Find(uint itemId, MapLevelReference? level)
    {
        if (level == null) return null;
        foreach (var value in Active.Values)
            if (value.Item.ItemId == itemId && value.Item.Level == level.Value) return value;
        return null;
    }

    internal static bool AnyActive => Active.Count != 0;

    internal string? Place(Transform host, string instance)
        => Committed ? NativeModelRuntime.Place(Owner, Assembly, host, instance) : "carry-item-assembly-unregistered";

    public void Dispose()
    {
        try { if (Committed) NativeModelRuntime.RemoveCopies(Owner, ""); }
        finally { Rollback(); }
    }
}
