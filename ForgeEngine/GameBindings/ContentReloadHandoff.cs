using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeEngine.Logging;
using ForgeMap.Native;

namespace ForgeEngine.GameBindings;

/// <summary>
/// The workshop's hand-off to a running game and the author's reload with its rejoin (contract I-RELOAD 「工坊与游戏的交接」「一键重进」).
/// The workshop installs a package (staging folder, folder renamed whole into the profile, <c>.forge-installed.json</c> last) and then
/// writes <c>BepInEx/forge-reload/request.json</c> naming each package with the SHA-256 of that record; the game reads only this request.
/// A request is answered once with <c>BepInEx/forge-reload/result.json</c>: the generation committed or the refusal code, and per package
/// the record digest the game read and every admission refusal of that package (layer, document, path, code, detail: the rows of the
/// offline package admission). The reload itself is <see cref="Plugin.ReloadContent"/>: Authoring only, in a clean lobby or in the menu
/// with no lobby, never queued. A request that comes while this machine hosts an expedition alone first takes the game back to its lobby
/// (<see cref="SoloDrop.Leave"/>; the result says status leaving), then reloads and rejoins the level it left. A rejoin after a committed
/// generation drops into the level the request left, through ForgeCore's <see cref="SoloDrop.Rejoin"/>, to its spawn; the result is
/// written again when the drop is in the level or failed. A request that comes in the lobby or in the menu with no lobby (the Rundown
/// selection screen) only swaps the generation and enters nothing (rejoin state skipped-not-in-level). Main thread.
/// </summary>
internal static class ContentReloadHandoff
{
    internal const string DirectoryName = "forge-reload", RequestName = "request.json", ResultName = "result.json", RunningName = "game.lock";
    internal const string RequestFormat = "forge.reload-request", ResultFormat = "forge.reload-result";
    /// <summary>The request is not a readable request document.</summary>
    internal const string RequestUnreadable = "reload-request-unreadable";
    /// <summary>A package the request names is not installed as the request says (its record is missing or has other bytes).</summary>
    internal const string PackageMismatch = "reload-package-mismatch";
    /// <summary>The request came in an expedition and the way back to the lobby failed: the game refused to end it, did not arrive in
    /// <see cref="SoloDrop.LeaveLimit"/> seconds, or the lobby was not clean <see cref="CleanLimit"/> seconds after arriving.</summary>
    internal const string LeaveFailed = "reload-leave-failed";
    /// <summary>How long after the game is back in the lobby the runtime may take to see it clean (its own level-left bookkeeping).</summary>
    private const float CleanLimit = 10f;
    private const string RecordName = ".forge-installed.json";
    private const float PollSeconds = 1f;
    private const int MaximumRejections = 256;
    private static readonly Regex PackageName = new("^[A-Za-z0-9_]+-[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static float _nextPoll;
    private static DateTime _handledWrite;
    private static DateTime? _processStart;
    // The answered request whose rejoin is still dropping: its result is written again when the drop ends.
    private static Answer? _rejoining;
    // A request that came in an expedition: the game is on its way back to the lobby, then the reload and the rejoin follow.
    private static Leaving? _leaving;

    private sealed class Leaving
    {
        internal Answer Answer = null!;
        internal IReadOnlyList<string> Packages = Array.Empty<string>();
        internal LevelId Level;
        internal float LeftAt = -1f;
    }
    // Held open without sharing for the life of the process: the workshop's test that a game answers requests in this profile (Windows
    // releases it when the process ends, however it ends). Null until the kernel has started; a failed open is logged once.
    private static FileStream? _running;
    private static bool _runningFailed;

    private static string Folder => Path.Combine(Paths.BepInExRootPath, DirectoryName);

    /// <summary>One answer, as the result file and the author command report it.</summary>
    internal sealed class Answer
    {
        internal string Request = "";
        internal string Status = "refused";
        internal string Code = "";
        internal string Detail = "";
        internal string? Participant;
        internal long Generation, Previous;
        internal string Digest = "";
        internal readonly List<PackageAnswer> Packages = new();
        internal string Rejoin = "not-requested";
        internal string RejoinLevel = "";
        internal string RejoinDetail = "";
    }

    internal sealed record PackageAnswer(string Package, string Record, IReadOnlyList<Rejection> Rejections);
    internal sealed record Rejection(string Layer, string Document, string Path, string Code, string Detail, string AuthorId = "");

    /// <summary>Every frame from the host's Update: once a second, a request not answered yet is answered; a running rejoin is followed.</summary>
    internal static void Poll()
    {
        if (_rejoining != null) FollowRejoin();
        if (_leaving != null) FollowLeave();
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;
        var kernel = GameRuntimeBridge.Kernel;
        // Before the first generation the game has nothing to swap; the request waits for startup to finish.
        if (kernel == null || kernel.StartupState == RuntimeStartupState.Registering) return;
        if (_running == null && !_runningFailed) HoldRunning(kernel);
        var request = Path.Combine(Folder, RequestName);
        DateTime written;
        try
        {
            if (!File.Exists(request)) return;
            written = File.GetLastWriteTimeUtc(request);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        if (written == _handledWrite) return;
        _handledWrite = written;
        _processStart ??= Process.GetCurrentProcess().StartTime.ToUniversalTime();
        if (written < _processStart)
        {
            // Installed while the game was closed: this start's first generation already holds it.
            Log(kernel, "before-start", "", request);
            Delete(kernel, request);
            return;
        }
        string text;
        try { text = File.ReadAllText(request); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The workshop renames the request into place whole; a read that fails now is tried again at the next write.
            Log(kernel, "unreadable", "", error.Message);
            return;
        }
        var answer = Handle(text);
        Delete(kernel, request);
        Write(answer);
        Log(kernel, answer.Status, answer.Request, answer.Code + (answer.Detail.Length == 0 ? "" : ": " + answer.Detail));
    }

    private static void HoldRunning(RuntimeKernel kernel)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            _running = new FileStream(Path.Combine(Folder, RunningName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Another game holds it (two games on one profile) or the folder is not writable: the workshop then sees this game as the
            // other one or as not running, and its requests go unanswered here.
            _runningFailed = true;
            Log(kernel, "running-mark-unavailable", "", error.Message);
        }
    }

    /// <summary>The hand-off's one path: reload, and when the request came in a level (<paramref name="left"/> given, the level it left)
    /// drop into that level again once the new generation is committed. Without it nothing is entered.</summary>
    internal static Answer Reload(string request = "", IReadOnlyList<string>? packages = null, LevelId? left = null)
    {
        var answer = new Answer { Request = request, Previous = GameRuntimeBridge.Generations.Current.Number };
        var level = left ?? default;
        var before = GameRuntimeBridge.LastReloadSnapshot;
        var result = Plugin.ReloadContent();
        answer.Code = result.Code ?? "";
        answer.Detail = result.Detail ?? "";
        if (result.Status == CommandStatuses.Succeeded && result.CommitState == CommitStates.Confirmed)
        {
            answer.Status = "committed";
            answer.Generation = GameRuntimeBridge.Generations.Current.Number;
            answer.Digest = GameRuntimeBridge.Generations.Current.Digest;
        }
        else
        {
            answer.Status = result.Status == CommandStatuses.Rejected ? "refused" : "failed";
            // A generation refusal reads "<code> (<participant>): <detail>" (ContentGenerationRefusal).
            var prefix = answer.Code + " (";
            int close = answer.Detail.StartsWith(prefix, StringComparison.Ordinal) ? answer.Detail.IndexOf("): ", prefix.Length, StringComparison.Ordinal) : -1;
            if (close > 0)
            {
                answer.Participant = answer.Detail[prefix.Length..close];
                answer.Detail = answer.Detail[(close + 3)..];
            }
        }
        var snapshot = ReferenceEquals(before, GameRuntimeBridge.LastReloadSnapshot) ? null : GameRuntimeBridge.LastReloadSnapshot;
        foreach (var package in packages ?? Array.Empty<string>())
            answer.Packages.Add(new PackageAnswer(package, RecordDigest(package) ?? "", snapshot == null ? Array.Empty<Rejection>() : Rejections(snapshot, package)));
        if (answer.Status != "committed") { answer.Rejoin = "not-started"; answer.RejoinDetail = "The generation was not committed."; return answer; }
        if (left == null)
        {
            answer.Rejoin = "skipped-not-in-level";
            answer.RejoinDetail = "The request did not come in a level; the game does not enter a level by itself from the lobby or the Rundown selection screen.";
            if (GameRuntimeBridge.Kernel is { } kernel) Log(kernel, "rejoin-skipped-not-in-level", request, answer.RejoinDetail);
            return answer;
        }
        if (!level.IsValid) { answer.Rejoin = "no-level"; answer.RejoinDetail = "The level the request left is not known."; return answer; }
        answer.RejoinLevel = level.Coordinate;
        var refusal = SoloDrop.Check(level);
        if (refusal != SoloDropRefusal.None) { answer.Rejoin = "failed"; answer.RejoinDetail = "The level cannot start: " + refusal + "."; return answer; }
        try
        {
            SoloDrop.Rejoin(level);
            answer.Rejoin = "started";
            _rejoining = answer;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            answer.Rejoin = "failed";
            answer.RejoinDetail = error.Message;
        }
        return answer;
    }

    private static Answer Handle(string text)
    {
        string id = "";
        var packages = new List<string>();
        var records = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String
                || format.GetString() != RequestFormat)
                return Refused("", RequestUnreadable, "The request is not a " + RequestFormat + " document.");
            if (!root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String || (id = idValue.GetString()!).Length is 0 or > 128)
                return Refused("", RequestUnreadable, "The request has no id.");
            if (!root.TryGetProperty("packages", out var list) || list.ValueKind != JsonValueKind.Array)
                return Refused(id, RequestUnreadable, "The request lists no packages.");
            foreach (var row in list.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("package", out var name) || name.ValueKind != JsonValueKind.String
                    || !PackageName.IsMatch(name.GetString()!) || !row.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.String)
                    return Refused(id, RequestUnreadable, "A package row needs a package name (Team-Name) and its record digest.");
                packages.Add(name.GetString()!);
                records.Add(record.GetString()!.ToLowerInvariant());
            }
        }
        catch (JsonException error) { return Refused(id, RequestUnreadable, error.Message); }
        // The request answers for these installs: a package the workshop did not finish installing, or one that changed since, is not it.
        for (int i = 0; i < packages.Count; i++)
        {
            var read = RecordDigest(packages[i]);
            if (read == records[i]) continue;
            var refused = Refused(id, PackageMismatch, read == null
                ? packages[i] + " has no " + RecordName + "; it is not installed by the workshop."
                : packages[i] + "'s " + RecordName + " is not the one the request names; it was installed again or changed.");
            for (int j = 0; j < packages.Count; j++) refused.Packages.Add(new PackageAnswer(packages[j], RecordDigest(packages[j]) ?? "", Array.Empty<Rejection>()));
            return refused;
        }
        return Begin(id, packages);
    }

    private static LevelId? ActiveLevel()
        => Level.TryActiveExpedition(out var rundown, out var tier, out var index) && new LevelId(rundown, tier, index) is { IsValid: true } level ? level : null;

    /// <summary>A request that comes while this machine hosts an expedition alone in Authoring (the only refusal of the reload would be
    /// that the game is not in the lobby): the game goes back to its lobby through its own way out (<see cref="SoloDrop.Leave"/>), the
    /// answer says so (status leaving), and once the lobby is clean the reload runs with a rejoin of the level that was left. Any other
    /// request is reloaded at once.</summary>
    private static Answer Begin(string request, IReadOnlyList<string> packages)
    {
        if (Plugin.ConfiguredMode != RuntimeMode.Authoring || NativeContentRefusal.Stopped != null
            || GameRuntimeBridge.CleanLobbyRefusal() != "not-in-lobby")
            return Reload(request, packages);
        var level = ActiveLevel() ?? default;
        var answer = new Answer
        {
            Request = request,
            Status = "leaving",
            Previous = GameRuntimeBridge.Generations.Current.Number,
            Rejoin = "not-started",
            RejoinLevel = level.IsValid ? level.Coordinate : ""
        };
        foreach (var package in packages) answer.Packages.Add(new PackageAnswer(package, RecordDigest(package) ?? "", Array.Empty<Rejection>()));
        try { SoloDrop.Leave(); }
        catch (InvalidOperationException error) { return LeaveFailure(answer, error.Message); }
        if (SoloDrop.State == SoloDropState.Failed) return LeaveFailure(answer, SoloDrop.Failure);
        _leaving = new Leaving { Answer = answer, Packages = packages, Level = level };
        return answer;
    }

    private static Answer LeaveFailure(Answer answer, string detail)
    {
        answer.Status = "failed";
        answer.Code = LeaveFailed;
        answer.Detail = detail;
        answer.Rejoin = "not-started";
        answer.RejoinDetail = "The game did not get back to the lobby.";
        return answer;
    }

    /// <summary>Every frame while a request waits for the lobby: once the game is back and the lobby clean, the reload and its rejoin
    /// (a request that came in an expedition always rejoins); a failed or stalled way back is answered as <see cref="LeaveFailed"/>,
    /// never retried.</summary>
    private static void FollowLeave()
    {
        var leaving = _leaving!;
        Answer answer;
        switch (SoloDrop.State)
        {
            case SoloDropState.Leaving: return;
            case SoloDropState.Left:
                if (leaving.LeftAt < 0f) leaving.LeftAt = UnityEngine.Time.realtimeSinceStartup;
                var unclean = GameRuntimeBridge.CleanLobbyRefusal();
                if (unclean == null)
                {
                    if (GameRuntimeBridge.Kernel is { } kernel) Log(kernel, "left", leaving.Answer.Request, GameStateManager.CurrentStateName.ToString());
                    answer = Reload(leaving.Answer.Request, leaving.Packages, leaving.Level);
                }
                else if (UnityEngine.Time.realtimeSinceStartup - leaving.LeftAt > CleanLimit)
                    answer = LeaveFailure(leaving.Answer, "Back in the lobby, but it was not clean within " + CleanLimit + " s: " + unclean + ".");
                else return;
                break;
            case SoloDropState.Failed:
                answer = LeaveFailure(leaving.Answer, SoloDrop.Failure);
                break;
            default:
                answer = LeaveFailure(leaving.Answer, "The way back to the lobby was cancelled (" + SoloDrop.State + ").");
                break;
        }
        _leaving = null;
        Write(answer);
        if (GameRuntimeBridge.Kernel is { } log) Log(log, answer.Status, answer.Request, answer.Code + (answer.Detail.Length == 0 ? "" : ": " + answer.Detail));
    }

    private static Answer Refused(string id, string code, string detail) => new() { Request = id, Code = code, Detail = detail };

    private static string? RecordDigest(string package)
    {
        try
        {
            var path = Path.Combine(Paths.PluginPath, package, RecordName);
            return File.Exists(path) ? Hash.Sha256Hex(File.ReadAllBytes(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The admission refusals of one package in a discovery: the content entries', the plan index's and Map's (with the Rundown
    /// refusals of Map's commit of this discovery), as rows of the offline package admission (layer content / plan / map).</summary>
    private static IReadOnlyList<Rejection> Rejections(ContentDiskSnapshot snapshot, string package)
    {
        var prefix = "plugins/" + package + "/";
        bool Own(string path) => PosixPath.ToPosix(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var rows = new List<Rejection>();
        if (snapshot.Has("entries"))
            rows.AddRange(snapshot.Get<ContentCatalog>("entries").Rejections.Where(row => Own(row.Path))
                .Select(row => new Rejection("content", row.Entry, PosixPath.ToPosix(row.Path), row.Code, row.Detail + (row.Other is { Length: > 0 } ? " other=" + row.Other : ""))));
        if (snapshot.Has("plans"))
            rows.AddRange(snapshot.Get<PlanIndex>("plans").Refused.Where(file => Own(file.Path))
                .Select(file => new Rejection("plan", Path.GetFileName(file.Path), PosixPath.ToPosix(file.Path), file.RejectedCode ?? "", file.RejectedDetail ?? "")));
        if (snapshot.Has(MapContentParticipants.ContentName))
        {
            var map = snapshot.Get<MapPackageAdmission>(MapContentParticipants.ContentName);
            // The Rundown refusals of the commit (validation, menu slot, native publication) come before the package refusals they cause.
            string rundown = map.CapturedPacks.FirstOrDefault(pack => string.Equals(pack.Package, package, StringComparison.OrdinalIgnoreCase))?.Path
                ?? prefix + "forge/rundown.forge";
            rows.AddRange(map.RundownRefusals.Where(row => string.Equals(row.Package, package, StringComparison.OrdinalIgnoreCase))
                .Select(row => new Rejection("map", "rundown.forge", PosixPath.ToPosix(rundown), row.Code,
                    row.Detail + (row.Entry.Length == 0 ? "" : " entry=" + row.Entry) + (row.Other.Length == 0 ? "" : " other=" + row.Other))));
            rows.AddRange(map.Stages.SelectMany(stage => stage.Rejections)
                .Where(row => Own(row.Path) || string.Equals(row.Package, package, StringComparison.OrdinalIgnoreCase))
                .Select(row => new Rejection("map", row.Document, PosixPath.ToPosix(row.Path), row.Reason, row.Detail, row.AuthorId)));
        }
        return rows.Count <= MaximumRejections ? rows : rows.Take(MaximumRejections).ToArray();
    }

    private static void FollowRejoin()
    {
        var answer = _rejoining!;
        if (SoloDrop.State == SoloDropState.InLevel) answer.Rejoin = "in-level";
        else if (SoloDrop.State == SoloDropState.Failed) { answer.Rejoin = "failed"; answer.RejoinDetail = SoloDrop.Failure; }
        else if (SoloDrop.State == SoloDropState.Idle) { answer.Rejoin = "failed"; answer.RejoinDetail = "The drop was cancelled."; }
        else return;
        _rejoining = null;
        if (answer.Request.Length != 0) Write(answer);
        if (GameRuntimeBridge.Kernel is { } kernel) Log(kernel, "rejoin-" + answer.Rejoin, answer.Request, answer.RejoinLevel + " " + answer.RejoinDetail);
    }

    private static void Write(Answer answer)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, ResultName);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, Json(answer));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (GameRuntimeBridge.Kernel is { } kernel) Log(kernel, "result-unwritten", answer.Request, error.Message);
        }
    }

    /// <summary>The result document (contract I-RELOAD 「工坊与游戏的交接」).</summary>
    internal static byte[] Json(Answer answer)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("format", ResultFormat);
            json.WriteNumber("formatVersion", 1);
            json.WriteString("request", answer.Request);
            json.WriteString("status", answer.Status);
            json.WriteString("code", answer.Code);
            json.WriteString("detail", answer.Detail);
            if (answer.Participant != null) json.WriteString("participant", answer.Participant);
            json.WriteNumber("previous", answer.Previous);
            if (answer.Status == "committed")
            {
                json.WriteNumber("generation", answer.Generation);
                json.WriteString("digest", answer.Digest);
            }
            json.WriteStartArray("packages");
            foreach (var package in answer.Packages)
            {
                json.WriteStartObject();
                json.WriteString("package", package.Package);
                json.WriteString("record", package.Record);
                json.WriteStartArray("rejections");
                foreach (var row in package.Rejections)
                {
                    json.WriteStartObject();
                    json.WriteString("layer", row.Layer);
                    json.WriteString("document", row.Document);
                    json.WriteString("path", row.Path);
                    json.WriteString("code", row.Code);
                    json.WriteString("detail", row.Detail);
                    if (row.AuthorId.Length != 0) json.WriteString("authorId", row.AuthorId);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartObject("rejoin");
            json.WriteString("state", answer.Rejoin);
            json.WriteString("level", answer.RejoinLevel);
            json.WriteString("detail", answer.RejoinDetail);
            json.WriteEndObject();
            json.WriteString("written", DateTime.UtcNow.ToString("O"));
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void Delete(RuntimeKernel kernel, string path)
    {
        // A request that cannot be deleted is still answered once: its write time is remembered.
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log(kernel, "request-undeleted", "", error.Message); }
    }

    private static void Log(RuntimeKernel kernel, string outcome, string request, string detail)
    {
        if (!kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.ContentReloadHandoff, new RuntimeLogFacts
        {
            Detail = detail,
            Values = new[] { RuntimeLogValue.Of("outcome", outcome), RuntimeLogValue.Quote("request", request) }
        });
    }
}
