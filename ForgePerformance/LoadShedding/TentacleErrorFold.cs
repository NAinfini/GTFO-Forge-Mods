using System;
using Il2CppInterop.Runtime;

namespace ForgePerformance.LoadShedding;

// TentacleLimitErrorFold. When the game's global striker-tentacle pool is full, MovingEnemyTentacleBase.BuildAndUpdateAttackSpline
// logs this literal with Debug.LogError for every tentacle it cannot draw, on every try. Each call walks Unity's error path (stack
// trace, every log listener, the log file). The message is held back during a level: the first one is written at once, so a
// session that is killed or crashes before the level ends still records it, and the count is written when the level ends (a
// normal quit ends the level first). The literal is an interned IL2CPP string, so after the first match a held message is
// recognised by its pointer alone; any other error passes through unchanged.
internal static class TentacleErrorFold
{
    internal const string Code = "perf.tentacle-limit-reached";
    internal const string Message = "MovingEnemyTentacleBase, Could not allocate GPUCurvy-tentacle, Global limit is met, unable to spawn curvy thing";
    internal static bool Enabled;
    private static IntPtr _literal;
    private static long _count;

    internal static bool Hold(Il2CppSystem.Object? message)
    {
        if (!Enabled || message is null || LoadSheddingController.Current is not { Active: true } current) return false;
        var pointer = message.Pointer;
        if (pointer != _literal)
        {
            if (_literal != IntPtr.Zero || !IsMessage(pointer)) return false;
            _literal = pointer;
        }
        if (_count++ == 0) current.LogFoldedError(Code, "first", 1, Message);
        return true;
    }

    private static bool IsMessage(IntPtr pointer)
        => IL2CPP.il2cpp_object_get_class(pointer) == Il2CppClassPointerStore<string>.NativeClassPtr
            && IL2CPP.Il2CppStringToManaged(pointer) == Message;

    // The count held back since the last call.
    internal static long Take()
    {
        var count = _count;
        _count = 0;
        return count;
    }

    internal static void Reset() { Enabled = false; _count = 0; }
}
