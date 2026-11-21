using BepInEx.Configuration;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The recorder's own configuration. It has a section of its own because every key here is about one session's cost
/// and size, not about a diagnosis: an author who runs out of disk turns gzip or the budget down without touching the
/// performance or inspection settings.
/// </summary>
internal static partial class Settings
{
    internal static ConfigEntry<bool> RecorderEnabled = null!;
    internal static ConfigEntry<bool> WorldCaptureEnabled = null!;
    internal static ConfigEntry<int> RecorderBudgetGiB = null!;
    internal static ConfigEntry<int> RecorderKeepSessions = null!;
    internal static ConfigEntry<int> RecorderReportsMaxGiB = null!;
    internal static ConfigEntry<int> RecorderSegmentMiB = null!;
    internal static ConfigEntry<int> RecorderRecordKiB = null!;
    internal static ConfigEntry<int> RecorderQueue = null!;
    internal static ConfigEntry<bool> RecorderTrace = null!;
    internal static ConfigEntry<bool> RecorderTraceAllProfiles = null!;
    internal static ConfigEntry<bool> RecorderPopulationBudget = null!;
    internal static ConfigEntry<KeyCode> RecorderBookmarkKey = null!;
    internal static ConfigEntry<string> RecorderBookmarkLabel = null!;
    internal static ConfigEntry<KeyCode> RecorderSnapshotKey = null!;
    internal static ConfigEntry<KeyCode> RecorderScreenshotKey = null!;
    internal static ConfigEntry<KeyCode> RecorderPanelKey = null!;

    internal static void BindRecorder()
    {
        RecorderEnabled = Plugin.Configuration.Bind("Recorder", "Enabled", false,
            "Opt in to recording the authoring session, native tracing and state capture into BepInEx/ForgeReports/rec-*. Restart required. Pack tests acquire and release their own recorder without changing this setting.");
        WorldCaptureEnabled = Plugin.Configuration.Bind("Recorder", "WorldCaptureEnabled", true,
            "Scan the live level for world snapshots and per-frame change records while recording. Off keeps the recording, commands and traces but runs no world capture, which removes its per-frame and periodic scene-scan cost for drive and performance sessions. Restart required.");
        RecorderBudgetGiB = Plugin.Configuration.Bind("Recorder", "BudgetGiB", 2,
            new ConfigDescription("Total bytes one session may write. The session stops writing at this size, counts every record it dropped, and says so on the session channel and in the screen corner.", new AcceptableValueRange<int>(1, 64)));
        RecorderKeepSessions = Plugin.Configuration.Bind("Recorder", "KeepSessions", 5,
            new ConfigDescription("Recording runs kept in BepInEx/ForgeReports (rec-* folders and the per-process diagnostic .forge files, each counted separately). Older ones are deleted when a recording starts; the current run is never deleted. A session renewed at its byte budget counts with the run it continues.", new AcceptableValueRange<int>(1, 1000)));
        RecorderReportsMaxGiB = Plugin.Configuration.Bind("Recorder", "ReportsMaxGiB", 10,
            new ConfigDescription("Total size of the recognized recording output in BepInEx/ForgeReports. Over this size the oldest runs are deleted when a recording starts, whatever KeepSessions says; the current run and runs written in the last hour are kept. Capture, probe and pack-test folders and unknown files are never touched or counted.", new AcceptableValueRange<int>(1, 4096)));
        RecorderSegmentMiB = Plugin.Configuration.Bind("Recorder", "SegmentMiB", 8,
            new ConfigDescription("Bytes per JSONL segment before the writer opens the next one. A closed segment is packed to .jsonl.gz; the open one stays plain for live reading.", new AcceptableValueRange<int>(1, 256)));
        RecorderRecordKiB = Plugin.Configuration.Bind("Recorder", "RecordKiB", 64,
            new ConfigDescription("Largest single record. One value over this budget is dropped and counted rather than growing the writer's memory.", new AcceptableValueRange<int>(4, 1024)));
        RecorderQueue = Plugin.Configuration.Bind("Recorder", "QueueRecords", 16384,
            new ConfigDescription("Maximum queued records and documents combined. A separate 16 MiB retained-data budget includes the item currently being written. Full queues refuse the newest item and mark the session incomplete.", new AcceptableValueRange<int>(256, 262144)));
        RecorderTrace = Plugin.Configuration.Bind("Recorder", "TraceEnabled", false,
            "Install native method tracing from the package's probes/trace definitions when Enabled is on. Pack tests never trace. Select individual profiles in [Trace Profiles]. Restart required.");
        RecorderTraceAllProfiles = Plugin.Configuration.Bind("Recorder", "TraceAllProfiles", false,
            "Enable every valid packaged trace profile, overriding [Trace Profiles], for full tracing regression runs. Restart required.");
        RecorderPopulationBudget = Plugin.Configuration.Bind("Recorder", "PopulationBudgetEnabled", false,
            "Observe exact native population-budget calls and live-cap snapshots. Read-only, bounded and authoring-only; enable before level generation. Native hook refusals and missing observations remain inconclusive.");
        RecorderBookmarkKey = Plugin.Configuration.Bind("Recorder", "BookmarkKey", KeyCode.F6, "Mark \"it just happened here\" in the session. The bookmark carries the label below and where the camera was looking.");
        RecorderBookmarkLabel = Plugin.Configuration.Bind("Recorder", "BookmarkLabel", "", "Label written with the next bookmark; empty writes the default label.");
        RecorderSnapshotKey = Plugin.Configuration.Bind("Recorder", "SnapshotKey", KeyCode.F7, "Ask every capture provider for a full state snapshot.");
        RecorderScreenshotKey = Plugin.Configuration.Bind("Recorder", "ScreenshotKey", KeyCode.F8, "Capture the screen into the session directory and record its path.");
        RecorderPanelKey = Plugin.Configuration.Bind("Recorder", "ExperimentPanelKey", KeyCode.F5, "Toggle the experiment panel.");
    }
}
