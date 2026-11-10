using System;

namespace ForgeReplay;

/// <summary>Where every hook writes its event. <see cref="Begin"/> hands out the one payload writer, <see cref="Emit"/> sends it to the
/// current recording (nothing happens when none is running). State events (<see cref="EmitState"/>) are also remembered by object, so
/// every keyframe block can write the current state of all objects again and a jump can start from it. Main thread only.</summary>
internal static class Rec
{
    private static readonly PayloadWriter Writer = new();
    private static readonly KeyframeStates States = new();

    internal static bool On => PlayerRecorder.Pipeline is { } pipeline && !pipeline.Failed;

    internal static PayloadWriter Begin() => Writer.Reset();

    /// <summary>An object's key in the recording: its instance id zig-zagged, so the negative ids of objects made at run time take up to 5 bytes, not 10.</summary>
    internal static ulong Key(UnityEngine.Object thing) => Key(thing.GetInstanceID());

    internal static ulong Key(int id) => ForgeCore.Compact.ZigZag(id);

    /// <summary>The payload built so far, to compare with the last one before sending it.</summary>
    internal static ReadOnlySpan<byte> Built => Writer.Span;

    internal static void Emit(ushort kind)
    {
        PlayerRecorder.Pipeline?.Event(PlayerRecorder.Now, kind, Writer.Span);
        PlayerRecorder.Count(kind, Writer.Span);
        AnimatorStateProbe.Mark(kind, Writer.Span);
    }

    /// <summary>Sends the built payload and keeps it as the latest state of object <paramref name="key"/> of this kind, until a payload that ends the object retires it.</summary>
    internal static void EmitState(ushort kind, ulong key)
    {
        Emit(kind);
        States.Set(kind, key, Writer.Span);
    }

    /// <summary>Writes the remembered state of every object, at the start of a keyframe block.</summary>
    internal static void WriteStates(BlockPipeline pipeline, uint nowMs)
    {
        States.Write(pipeline, nowMs);
    }

    internal static void Reset() { States.Clear(); Hooks.ResetRecordingState(); HookFailures = 0; }

    /// <summary>How many times a recording hook failed since the recording began, for the completeness block of the footer.</summary>
    internal static int HookFailures { get; private set; }

    internal static void Fail(string hook, Exception error) { HookFailures++; Plugin.Logger.ErrorOnce("The " + hook + " recording hook failed: " + error); }
}
