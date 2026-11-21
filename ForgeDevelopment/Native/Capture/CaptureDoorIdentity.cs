using System;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Native door readings the world capture shares.</summary>
internal static class CaptureDoorIdentity
{
    internal static double? Health(LG_WeakDoor door)
    {
        try
        {
            // m_destruction is the iLG_WeakDoor_Destruction wrapper; m_health is on the concrete component.
            var number = door.m_destruction?.TryCast<LG_WeakDoor_Destruction>()?.m_health;
            return number is { } health && double.IsFinite(health) ? health : null;
        }
        catch (Exception error) { CaptureRegistry.Log("door-health", error); return null; }
    }
}
