using System;
using System.IO;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

// BuildWeaponBundle creates mesh parts and authored sockets as direct children. A part may
// share a socket's name (notably Muzzle), so Transform.Find can bind the mesh pivot instead.
internal static class ModelSockets
{
    internal static Transform Require(Transform model, string name) =>
        Find(model, name) ?? throw new InvalidDataException("Energy model socket missing: " + name);

    internal static Transform? Find(Transform model, string name)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Socket name is required.", nameof(name));
        Transform? found = null;
        for (var i = 0; i < model.childCount; i++)
        {
            var child = model.GetChild(i);
            // Test the candidate itself, not its descendants: effects may be attached below a socket.
            if (child.name != name || child.GetComponent<MeshFilter>() != null || child.GetComponent<Renderer>() != null)
                continue;
            if (found != null) throw new InvalidDataException("Energy model socket is ambiguous: " + name);
            found = child;
        }
        return found;
    }
}
