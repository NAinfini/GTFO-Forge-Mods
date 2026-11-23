using System;
using System.Runtime.InteropServices;
using CullingSystem;
using Il2CppInterop.Runtime;

namespace ForgePerformance.LoadShedding;

// ShadowProxyHold. An enemy whose shadow is drawn (C_MovingCuller_Cullbucket.hasShadowsEnabled) gets shadowEnabledTime = Clock.Time + 1
// only when the shadow is switched on; C_MovingCuller.Update switches it off again once that time has passed, and the next injection
// switches it on again (SetActive and enabled off then on, animator culling on then off) once per second for as long as a light
// keeps shadowing the enemy. The postfix of ShadowUtility.InjectDrawCommands(cmd, material, cullables, additional, includeExtra)
// walks this call's cullables by raw pointer and renews the time of every such bucket that was just drawn, so the enemy stays
// on while it is drawn and goes off one second after it stops being drawn, as in the game. Nothing is sent and nothing visible changes.
internal static class ShadowProxyHold
{
    internal static bool Enabled;
    private static readonly IntPtr Bucket = Il2CppClassPointerStore<C_MovingCuller_Cullbucket>.NativeClassPtr;
    private static readonly int HasShadows = NativeReads.Offset(Bucket, "hasShadowsEnabled"), EnabledTime = NativeReads.Offset(Bucket, "shadowEnabledTime");
    private static long _renewed, _flipsAvoided, _calls;

    internal static void Renew(Il2CppSystem.Collections.Generic.List<C_Cullable>? list, float now)
    {
        if (list == null) return;
        var pointer = list.Pointer;
        for (int i = 0, count = NativeList<C_Cullable>.Count(pointer); i < count; i++)
        {
            var item = NativeList<C_Cullable>.Item(pointer, i);
            if (item == IntPtr.Zero || Marshal.ReadIntPtr(item) != Bucket || Marshal.ReadByte(item, HasShadows) == 0) continue;
            _renewed++;
            // A time that has run out is a switch-off the game would make in the coming Update.
            if (BitConverter.Int32BitsToSingle(Marshal.ReadInt32(item, EnabledTime)) <= now) _flipsAvoided++;
            Marshal.WriteInt32(item, EnabledTime, BitConverter.SingleToInt32Bits(now + 1f));
        }
    }

    internal static void Call() => _calls++;
    internal static (long Calls, long Renewed, long FlipsAvoided) Take()
    {
        var result = (_calls, _renewed, _flipsAvoided);
        _calls = _renewed = _flipsAvoided = 0;
        return result;
    }
    internal static void Reset() { Enabled = false; _calls = _renewed = _flipsAvoided = 0; }
}
