using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ForgeDevelopment.Native;

/// <summary>Local Authoring transport only. Execution and cleanup remain entirely in ExperimentRunner.</summary>
internal static class ExperimentRequests
{
    private const int MaximumRequest = 16 * 1024;
    private const int MaximumCommand = 4 * 1024 * 1024;
    // Archived requests and sealed results are evidence only until the driver has read them; the transport keeps the
    // newest ones and a week, never the last hour.
    private const int KeptRequestFiles = 32;
    private const int KeptResultFiles = 32;
    private static readonly string Instance = Guid.NewGuid().ToString("D");
    // A request is looked for every tenth of a second so a command starts promptly. The status the driver judges for freshness
    // (3 s) is written when its content changes and otherwise once a second as a heartbeat; the transport directories are
    // re-verified when a request is taken and every 30 seconds otherwise.
    private static long _nextPoll, _nextStatus, _nextPrepare;
    private static bool _stopped, _faulted;
    private readonly record struct StatusKey(long World, string Session, string State, string Active, bool Stopped);
    private readonly record struct StatusSnapshot(StatusKey Key, string Utc, double MonotonicSeconds);
    private readonly record struct PollResult(bool RequestPending, StatusKey? WrittenStatus, long NextStatus,
        long NextPrepare, long PollMicroseconds, long PrepareMicroseconds, long StatusMicroseconds);
    private static Task<PollResult>? _poll;
    private static bool _statusDirty;
    private static StatusKey? _statusKey;
    private static Request? _active;
    private static int _activeExecution;
    private static long _runWorld;
    private static string _runSession = "";
    private static string Control => Path.Combine(DevelopmentPaths.Root, "experiments", "control");
    private static string Results => Path.Combine(Control, "results");
    private sealed record Request(string Id, int ProcessId, string Instance, long WorldEpoch, string RecorderSession,
        string CommandId, string CommandSha256, double ExpiresMonotonicSeconds);

    // Same-machine transport: Stopwatch and the Python transport explicitly read Windows QPC.
    // UTC remains an evidence timestamp; wall-clock corrections cannot expire a live request or heartbeat.
    private static double MonotonicSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal static void Start()
    {
        JoinBackground();
        _stopped = _faulted = false;
        _nextPoll = _nextStatus = _nextPrepare = 0;
        _statusKey = null;
        _statusDirty = true;
    }

    internal static void Tick()
    {
        Harvest();
        // The transport's heartbeat is required by the driver's freshness check, but
        // there is no driver context to publish while no recorder or experiment exists.
        if (!RecRuntime.Active && !ExperimentRunner.Running && _active == null) return;
        if (_stopped || _faulted || _poll != null) return;
        var now = Stopwatch.GetTimestamp();
        if (!_statusDirty && now < _nextPoll) return;
        try { Schedule(now); }
        catch (Exception error)
        {
            _faulted = true;
            Plugin.PluginLog.LogError("experiment request transport stopped: " + error.Message);
        }
    }

    // Kept outside Tick so the disabled hot path does not allocate the closure used by Task.Run.
    private static void Schedule(long now)
    {
        _nextPoll = now + Stopwatch.Frequency / 10;
        var snapshot = CurrentStatus();
        var previous = _statusKey;
        var nextStatus = _nextStatus;
        var nextPrepare = _nextPrepare;
        var canRequest = _active == null;
        _statusDirty = false;
        _poll = Task.Run(() => Poll(snapshot, previous, nextStatus, nextPrepare, canRequest));
    }

    private static PollResult Poll(StatusSnapshot snapshot, StatusKey? previous, long nextStatus, long nextPrepare, bool canRequest)
    {
        var started = Stopwatch.GetTimestamp();
        var requested = canRequest && File.Exists(Path.Combine(Control, "request.forge"));
        var pollMicros = Microseconds(started);
        long prepareMicros = 0, statusMicros = 0;
        var now = Stopwatch.GetTimestamp();
        if (requested || now >= nextPrepare)
        {
            started = Stopwatch.GetTimestamp();
            PrepareFiles();
            prepareMicros = Microseconds(started);
            nextPrepare = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
        }
        StatusKey? written = null;
        if (requested || now >= nextStatus || snapshot.Key != previous)
        {
            started = Stopwatch.GetTimestamp();
            Status(snapshot);
            statusMicros = Microseconds(started);
            nextStatus = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            written = snapshot.Key;
        }
        return new(requested, written, nextStatus, nextPrepare, pollMicros, prepareMicros, statusMicros);
    }

    private static long Microseconds(long started)
        => (long)((Stopwatch.GetTimestamp() - started) * (1_000_000d / Stopwatch.Frequency));

    private static void Harvest()
    {
        var poll = _poll;
        if (poll == null || !poll.IsCompleted) return;
        _poll = null;
        try
        {
            var result = poll.GetAwaiter().GetResult();
            _nextStatus = result.NextStatus;
            _nextPrepare = result.NextPrepare;
            if (result.WrittenStatus != null) _statusKey = result.WrittenStatus;
            Slow("poll", result.PollMicroseconds, result.RequestPending, "background");
            Slow("prepare", result.PrepareMicroseconds, result.RequestPending, "background");
            Slow("status", result.StatusMicroseconds, result.RequestPending, "background");
            if (_stopped || _faulted || !result.RequestPending) return;
            var started = Stopwatch.GetTimestamp();
            try { Consume(null, null); }
            finally { Slow("request", Microseconds(started), true, "main"); }
            _statusDirty = true;
        }
        catch (Exception error)
        {
            // A broken transport is retired instead of throwing the same error every frame.
            _faulted = true;
            Plugin.PluginLog.LogError("experiment request transport stopped: " + error.Message);
        }
    }

    private static void Slow(string operation, long elapsedMicros, bool requestPending, string phase)
    {
        if (elapsedMicros < 20_000) return;
        RecDiag.Record(false, "development.transport-slow",
            "development transport " + operation + " took " + elapsedMicros.ToString(CultureInfo.InvariantCulture) + " us",
            new("operation", operation), new("elapsedMicros", elapsedMicros),
            new("requestPending", requestPending), new("phase", phase));
    }

    // Runner calls this only after its own terminal recording and cleanup, with its frozen run context.
    internal static void Completed(string commandId, ExperimentRecord record, long worldEpoch, string recorderSession)
    {
        if (_active == null || _active.CommandId != commandId || ExperimentRunner.SessionExecutions != _activeExecution) return;
        try
        {
            _runWorld = worldEpoch;
            _runSession = recorderSession;
            Complete(_active, record.Status == ExperimentRunStatus.NotRun ? "Failed" : record.Status.ToString(), record.Message);
            _active = null;
            // The driver waits for an idle status before the next command; do not make it wait for the next heartbeat.
            _statusDirty = true;
        }
        catch (Exception error)
        {
            _faulted = true;
            Plugin.PluginLog.LogError("experiment request completion failed: " + error.Message);
        }
    }

    internal static void Stop(string status, string reason)
    {
        if (_stopped) return;
        _stopped = true;
        JoinBackground();
        if (status is not ("Aborted" or "Failed" or "Rejected")) status = "Aborted";
        try
        {
            PrepareFiles();
            if (_active != null) { Complete(_active, status, reason); _active = null; }
            if (File.Exists(Path.Combine(Control, "request.forge"))) Consume(status, reason);
            var snapshot = CurrentStatus();
            Status(snapshot);
            _statusKey = snapshot.Key;
        }
        catch (Exception error) { Plugin.PluginLog.LogError("experiment request shutdown failed: " + error.Message); }
    }

    private static void JoinBackground()
    {
        var poll = _poll;
        _poll = null;
        if (poll == null) return;
        try
        {
            var result = poll.GetAwaiter().GetResult();
            Slow("poll", result.PollMicroseconds, result.RequestPending, "background");
            Slow("prepare", result.PrepareMicroseconds, result.RequestPending, "background");
            Slow("status", result.StatusMicroseconds, result.RequestPending, "background");
        }
        catch (Exception error) { Plugin.PluginLog.LogError("experiment request background shutdown failed: " + error.Message); }
    }

    private static void PrepareFiles()
    {
        DirectoryPhysical(Path.Combine(DevelopmentPaths.Root, "experiments"));
        DirectoryPhysical(Control);
        DirectoryPhysical(Results);
        DirectoryPhysical(Path.Combine(Control, "requests"));
        TransportRetention.PruneFiles(Results, "*.forge", KeptResultFiles);
        TransportRetention.PruneFiles(Path.Combine(Control, "requests"), "*.forge", KeptRequestFiles);
    }

    private static StatusSnapshot CurrentStatus() => new(new(RecTime.WorldEpoch, RecSession.SessionId ?? "",
        GameStateManager.CurrentStateName.ToString(), ExperimentRunner.Running ? ExperimentRunner.CurrentCommand : "", _stopped),
        DateTimeOffset.UtcNow.ToString("O"), MonotonicSeconds);

    private static void Status(StatusSnapshot snapshot)
    {
        // The status is a heartbeat, not evidence: it must be atomic, not durable across a power loss.
        Atomic(Path.Combine(Control, "status.forge"), new
        {
            format = "forge.development-experiment-status",
            schemaVersion = "1.0.0",
            processId = Environment.ProcessId,
            instance = Instance,
            utc = snapshot.Utc,
            worldEpoch = snapshot.Key.World,
            monotonicSeconds = snapshot.MonotonicSeconds,
            recorderSession = snapshot.Key.Session,
            gameState = snapshot.Key.State,
            activeCommand = snapshot.Key.Active,
            stopped = snapshot.Key.Stopped
        }, true, durable: false);
    }

    private static void Consume(string? stoppedStatus, string? stoppedReason)
    {
        var path = Path.Combine(Control, "request.forge");
        Physical(path);
        // Claim by rename; archive the exact submitted file, including malformed and oversized bytes.
        var archive = Path.Combine(Control, "requests", Guid.NewGuid().ToString("D") + ".forge");
        RetrySharing(() => File.Move(path, archive));
        Request? request = null;
        var executionsBefore = ExperimentRunner.SessionExecutions;
        _runWorld = RecTime.WorldEpoch;
        _runSession = RecSession.SessionId ?? "";
        try
        {
            var bytes = Read(archive, MaximumRequest);
            request = Parse(bytes);
            var result = Path.Combine(Results, request.Id + ".forge");
            if (File.Exists(result))
            {
                using var existing = JsonDocument.Parse(Read(result, MaximumRequest));
                var value = existing.RootElement;
                if (value.GetProperty("format").GetString() != "forge.development-experiment-result" || value.GetProperty("schemaVersion").GetString() != "1.0.0" ||
                    value.GetProperty("id").GetString() != request.Id || value.GetProperty("processId").GetInt32() != request.ProcessId ||
                    value.GetProperty("instance").GetString() != request.Instance || value.GetProperty("commandId").GetString() != request.CommandId ||
                    value.GetProperty("commandSha256").GetString() != request.CommandSha256 || value.GetProperty("worldEpoch").GetInt64() != request.WorldEpoch ||
                    value.GetProperty("recorderSession").GetString() != request.RecorderSession || value.GetProperty("evidenceScope").GetString() != "command-execution-only")
                    throw new InvalidDataException("existing result identity/hash conflict; preserved result and archived request");
                // Existing immutable result is the replay fence; never execute or overwrite it.
                return;
            }
            if (stoppedStatus != null) { Complete(request, stoppedStatus, stoppedReason ?? "runner stopped"); return; }
            if (request.ProcessId != Environment.ProcessId || request.Instance != Instance || request.WorldEpoch != RecTime.WorldEpoch ||
                request.RecorderSession != (RecSession.SessionId ?? "")) throw new InvalidDataException(StaleIdentity);
            var now = MonotonicSeconds;
            if (request.ExpiresMonotonicSeconds <= now || request.ExpiresMonotonicSeconds > now + 60)
                throw new InvalidDataException("request expired or expiry exceeds 60 seconds");
            if (ExperimentRunner.Running) throw new InvalidDataException("another experiment is running");
            RetrySharing(ExperimentCatalog.Load);
            var matches = ExperimentCatalog.All.Where(command => command.Id == request.CommandId && command.Valid).ToArray();
            if (matches.Length != 1) throw new InvalidDataException(ExperimentCatalog.Refusal(request.CommandId, Directory.GetFiles(ExperimentCatalog.DirectoryPath, "*.forge", SearchOption.TopDirectoryOnly)
                .SelectMany(file => ExperimentParser.Parse(File.ReadAllText(file), Path.GetFileName(file), out _))));
            // Reparse the exact bytes we hash, so a catalog read/hash race cannot start stale command content.
            var found = new List<ExperimentCommand>();
            foreach (var file in Directory.GetFiles(ExperimentCatalog.DirectoryPath, "*.forge", SearchOption.TopDirectoryOnly))
            {
                var source = Read(file, MaximumCommand);
                var parsed = ExperimentParser.Parse(System.Text.Encoding.UTF8.GetString(source), Path.GetFileName(file), out var error);
                if (error.Length != 0) continue;
                foreach (var command in parsed.Where(command => command.Id == request.CommandId))
                {
                    if (!command.Valid || ForgeCore.Hash.Sha256Hex(source) != request.CommandSha256) throw new InvalidDataException("installed command hash or validity changed");
                    found.Add(command);
                }
            }
            if (found.Count != 1) throw new InvalidDataException("command is missing or duplicated");
            // Catalog IO may take time: recheck the precise admission context immediately before Run.
            if (request.WorldEpoch != RecTime.WorldEpoch || request.RecorderSession != (RecSession.SessionId ?? "") ||
                request.ExpiresMonotonicSeconds <= MonotonicSeconds || ExperimentRunner.Running) throw new InvalidDataException(StaleContext);
            var executions = ExperimentRunner.SessionExecutions;
            _active = request;
            _activeExecution = executions + 1;
            var refused = ExperimentRunner.Run(found[0], _ => null, _ => { });
            if (refused != null || ExperimentRunner.SessionExecutions != executions + 1)
                throw new InvalidDataException(refused ?? "runner did not start this request");
            _runWorld = RecTime.WorldEpoch;
            _runSession = RecSession.SessionId ?? "";
        }
        catch (Exception error)
        {
            _active = null;
            if (request != null)
            {
                if (File.Exists(Path.Combine(Results, request.Id + ".forge")))
                {
                    Plugin.PluginLog.LogError("experiment request rejected or already sealed: " + error.Message);
                    return;
                }
                var executed = ExperimentRunner.SessionExecutions > executionsBefore;
                var record = executed ? ExperimentRunner.Record(request.CommandId) : null;
                var status = executed && record!.Status is ExperimentRunStatus.Failed or ExperimentRunStatus.Aborted
                    ? record.Status.ToString() : executed ? "Failed" : "Rejected";
                Complete(request, status, error.Message);
            }
            else
            {
                // Invalid IDs cannot become paths; retain original bytes under archive and give the
                // malformed input its own rejection identity instead of retrying it on every frame.
                var id = Path.GetFileNameWithoutExtension(archive);
                Atomic(Path.Combine(Results, id + ".forge"), new
                {
                    format = "forge.development-experiment-result",
                    schemaVersion = "1.0.0",
                    id,
                    processId = Environment.ProcessId,
                    instance = Instance,
                    status = "Rejected",
                    reason = error.Message,
                    evidenceScope = "command-execution-only",
                    archive = Path.GetFileName(archive),
                    utc = DateTimeOffset.UtcNow.ToString("O")
                }, false);
            }
            // A request the world or recorder session outran never ran; the driver reads the new identity and asks again.
            if (error.Message is StaleIdentity or StaleContext) Plugin.PluginLog.LogWarning("experiment request rejected: " + error.Message);
            else Plugin.PluginLog.LogError("experiment request rejected: " + error.Message);
        }
    }

    private const string StaleIdentity = "request identity changed", StaleContext = "request context changed before admission";

    private static void Complete(Request request, string status, string reason) => Atomic(Path.Combine(Results, request.Id + ".forge"), new
    {
        format = "forge.development-experiment-result",
        schemaVersion = "1.0.0",
        id = request.Id,
        processId = request.ProcessId,
        instance = request.Instance,
        commandId = request.CommandId,
        commandSha256 = request.CommandSha256,
        worldEpoch = request.WorldEpoch,
        recorderSession = request.RecorderSession,
        runWorldEpoch = _runWorld,
        runRecorderSession = _runSession,
        status,
        reason,
        evidenceScope = "command-execution-only",
        utc = DateTimeOffset.UtcNow.ToString("O")
    }, false);

    private static Request Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("request must be an object");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Name is not ("format" or "schemaVersion" or "id" or "processId" or "instance" or "worldEpoch" or "recorderSession" or "commandId" or "commandSha256" or "expiresMonotonicSeconds") || !fields.TryAdd(property.Name, property.Value))
                throw new InvalidDataException("unknown or duplicate request field");
        string Text(string key) => fields.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("missing string: " + key);
        if (fields.Count != 10 || Text("format") != "forge.development-experiment-request" ||
            fields["schemaVersion"].ValueKind != JsonValueKind.String || fields["schemaVersion"].GetString() != "1.0.0")
            throw new InvalidDataException("unsupported request schema; regenerate with the current Development tools");
        var id = Text("id");
        var instance = Text("instance");
        if (!Guid.TryParseExact(id, "D", out var guid) || guid.ToString("D") != id || !Guid.TryParseExact(instance, "D", out _)) throw new InvalidDataException("invalid request or instance UUID");
        var hash = Text("commandSha256");
        if (hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new InvalidDataException("invalid command SHA-256");
        var commandId = Text("commandId");
        if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 256) throw new InvalidDataException("invalid command ID");
        var expiry = fields["expiresMonotonicSeconds"].GetDouble();
        if (!double.IsFinite(expiry) || expiry <= 0) throw new InvalidDataException("expiry must be a finite monotonic timestamp");
        return new Request(id, fields["processId"].GetInt32(), instance, fields["worldEpoch"].GetInt64(), Text("recorderSession"), commandId, hash, expiry);
    }

    private static byte[] Read(string path, int maximum) => RetrySharing(() => ReadOnce(path, maximum));
    private static byte[] ReadOnce(string path, int maximum)
    {
        Physical(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > maximum) throw new InvalidDataException("transport file exceeds byte budget");
        var bytes = new byte[checked((int)stream.Length)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) throw new IOException("file changed while reading");
            count += read;
        }
        if (stream.ReadByte() != -1) throw new IOException("file grew while reading");
        return bytes;
    }
    private static void Atomic(string path, object value, bool overwrite, bool durable = true)
    {
        Physical(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) Physical(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".transport-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(durable);
            }
            RetrySharing(() => File.Move(temporary, path, overwrite));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool SharingError(Exception error) => error is IOException or UnauthorizedAccessException
        && (error.HResult & 0xffff) is 5 or 32 or 33;

    // Windows readers can hold a non-delete-sharing handle during an atomic publish. Retry that
    // bounded race, never schema/budget/path errors and never an execution or a submitted request.
    private static T RetrySharing<T>(Func<T> operation)
    {
        for (var attempt = 0; ; attempt++)
            try { return operation(); }
            catch (Exception error) when (SharingError(error) && attempt < 5) { Thread.Sleep(10 << attempt); }
    }
    private static void RetrySharing(Action operation) => RetrySharing(() => { operation(); return true; });
    private static void DirectoryPhysical(string path)
    {
        Physical(Path.GetDirectoryName(path)!);
        AuthoringFileSwap.RejectLinkedPath(path, "linked transport directory");
        Directory.CreateDirectory(path);
        Physical(path);
    }
    private static void Physical(string path)
    {
        var full = Path.GetFullPath(path);
        var walk = Path.GetPathRoot(full)!;
        foreach (var segment in full.Substring(walk.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            walk = Path.Combine(walk, segment);
            // A segment that does not exist yet (first start, ForgeDevelopment not created) cannot be a link, and neither can anything below it.
            if (!File.Exists(walk) && !Directory.Exists(walk)) return;
            if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) throw new IOException("linked transport path");
        }
    }
}
