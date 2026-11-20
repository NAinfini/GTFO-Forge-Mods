using UnityEngine;

namespace ForgeCore;

/// <summary>A drawing layer for objects only Forge's own drawing should see.</summary>
internal static class SpareLayers
{
    /// <summary>The highest unnamed layer from 30 down to 8 that no camera present now draws, or -1 when there is none. Spare layers are
    /// unnamed; 31 is never answered, the game's lobby characters are drawn on it.</summary>
    internal static int Unseen()
    {
        var drawn = 0;
        foreach (var camera in Camera.allCameras) drawn |= camera.cullingMask;
        for (var layer = 30; layer >= 8; layer--)
            if ((drawn & (1 << layer)) == 0 && string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
        return -1;
    }
}
