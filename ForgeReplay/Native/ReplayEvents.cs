using System;

namespace ForgeReplay;

/// <summary>One recording on disk, as another mod sees it (<see cref="ReplayEvents.RecordingFinished"/>, <see cref="ReplayControl.Recordings"/>). <see cref="Outcome"/> is how the level ended (Unknown for a file
/// with no footer), <see cref="Complete"/> is false for a file cut off by a crash or quit, <see cref="Starred"/> for one the list keeps for good; <see cref="RundownName"/> and <see cref="LevelCode"/> are
/// what the list shows ("" when the header cannot be read).</summary>
public sealed record RecordingInfo(string Path, string RundownName, string LevelCode, Outcome Outcome, uint DurationMs, bool Complete, bool Starred);

/// <summary>A note a mod wrote into the recording (ForgeCore <c>Recording.Note</c>) as the replay plays past it, with the recording time it was written at.</summary>
public sealed record ReplayNote(uint TimeMs, string ModId, string Kind, string Payload, bool Mark);

/// <summary>What ForgeReplay tells other mods. Handlers run on the main thread; one that throws is logged and does not stop the others.</summary>
public static class ReplayEvents
{
    /// <summary>A recording was closed and is now an ordinary file in the replay folder (the level ended or was left). Not raised for a recording cut off by a crash.</summary>
    public static event Action<RecordingInfo>? RecordingFinished;

    /// <summary>The playing replay passed a note a mod wrote while recording (<c>Recording.Note</c>); not raised for a jump, which passes nothing.</summary>
    public static event Action<ReplayNote>? OnNote;

    internal static void Finished(RecordingInfo info) => Raise(RecordingFinished, info);

    internal static RecordingInfo Info(ReplayEntry e) => new(e.Path, RundownNames.Name(e) ?? "", RundownNames.Code(e) ?? "", e.Outcome, e.DurationMs, e.Complete, e.Starred);

    internal static void Noted(ReplayNote note) => Raise(OnNote, note);

    private static void Raise<T>(Action<T>? handlers, T value)
    {
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<T>)handler)(value); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("ForgeReplay event handler failed: " + error); }
        }
    }
}
