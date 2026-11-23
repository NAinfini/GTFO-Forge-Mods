using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeEngine.Logging;
using LevelGeneration;
using SNetwork;

namespace ForgeEngine.GameBindings;

/// <summary>
/// The host's half of the content generations (contract I-RELOAD): startup commits the empty generation into generation 1
/// inside the one startup attempt, and the author's reload commits generation N into N+1 in a clean lobby, both through
/// <see cref="ContentGenerations.Commit"/>. The host owns two participants: the content entries (weapons, enemies, behaviour,
/// registered first) and the plan index (registered last, after the domains registered theirs at their Start).
/// </summary>
internal static partial class GameRuntimeBridge
{
    /// <summary>The process's content generations; participants register while the domains start.</summary>
    internal static ContentGenerations Generations { get; private set; } = new();
    private static readonly List<(string Owner, Func<IReadOnlyList<string>> EnsureAll)> OptionalHookGroups = new();

    /// <summary>A domain's participant, registered at its Start (before the first generation); commit order is registration order.</summary>
    internal static void RegisterContentParticipant(IContentParticipant participant) => Generations.Register(participant);

    /// <summary>A domain's optional hook groups (hooks only some content needs). In Authoring every group is put on when the first
    /// generation is committed, so a reload never installs a hook; in Play a group goes on when content first needs it.
    /// <paramref name="ensureAll"/> returns the refusal of every group that could not go on.</summary>
    internal static void RegisterOptionalHooks(string owner, Func<IReadOnlyList<string>> ensureAll) => OptionalHookGroups.Add((owner, ensureAll));

    private static void ResetGenerations()
    {
        Generations = new ContentGenerations();
        OptionalHookGroups.Clear();
        Generations.Register(new EntriesParticipant());
    }

    /// <summary>Startup: discovery of the install as ForgeEngine's Load found it, then the empty generation committed into
    /// generation 1. A refusal or failure here fails the startup attempt as every startup failure does.</summary>
    private static void CommitFirstGeneration(RuntimeKernel kernel)
    {
        if (!Generations.Participants.Any(participant => participant.Id == PlansParticipant.Name))
            Generations.Register(new PlansParticipant());
        var generation = Generations.Commit(Generations.Discover(Paths.BepInExRootPath, startup: true));
        ContentPackStartup.LogStartup(kernel);
        if (RuntimeSettings.Mode == RuntimeMode.Authoring)
            foreach (var (owner, ensureAll) in OptionalHookGroups)
                foreach (var refusal in ensureAll())
                    LogGeneration(kernel, RuntimeLogLevel.Error, RuntimeHostLogCodes.ContentGenerationRefused, generation.Number,
                        "optional-hooks-refused", owner, refusal);
        LogGeneration(kernel, RuntimeLogLevel.Info, RuntimeHostLogCodes.ContentGenerationCommitted, generation.Number, "startup",
            string.Join(",", generation.Parts.Keys), "leases=" + generation.LeaseCount + " digest=" + generation.Digest);
    }

    /// <summary>Why the game is not clean for a new generation, or null when it is: either this machine hosts a lobby with no other
    /// person in it, or the game is in the menu with no lobby and no session at all (the game's state NoLobby); in both no level is
    /// built and nothing is being dispatched or played back.</summary>
    internal static string? CleanLobbyRefusal()
    {
        bool menu = GameStateManager.CurrentStateName == eGameStateName.NoLobby && !SNet.IsInLobby;
        if (!menu)
        {
            if (!SNet.IsMaster) return "not-host";
            if (Players.OtherHumanInLobby()) return "other-player-in-lobby";
            if (GameStateManager.CurrentStateName != eGameStateName.Lobby) return "not-in-lobby";
        }
        if (_inLevel || Builder.CurrentFloor != null) return "floor-not-empty";
        if (_dispatching || Puppets.Playback) return "runtime-busy";
        return null;
    }

    /// <summary>The author's reload: a new discovery committed as the next generation. Authoring only, in a clean lobby; Play never
    /// changes its generation. A refusal leaves the current generation as it was.</summary>
    internal static CommandResult ReloadContent(RuntimeMode mode)
    {
        if (mode != RuntimeMode.Authoring)
            return CommandResult.Rejected(ContentGenerationRefusal.AuthoringRequired, "Content reload requires Authoring mode.");
        var kernel = Kernel;
        if (kernel == null || Suspension != null || kernel.StartupState != RuntimeStartupState.Ready || Generations.Current.Number == 0)
            return CommandResult.Rejected("runtime-not-ready", "Content reload requires a started host.");
        if (NativeContentRefusal.Stopped != null)
            return CommandResult.Rejected(ContentGenerationRefusal.Stopped, "Forge content is stopped until the game restarts.");
        if (CleanLobbyRefusal() is { } unclean)
        {
            LogGeneration(kernel, RuntimeLogLevel.Error, RuntimeHostLogCodes.ContentGenerationRefused, Generations.Current.Number + 1,
                ContentGenerationRefusal.NotClean, "host", unclean);
            return CommandResult.Rejected(ContentGenerationRefusal.NotClean, unclean);
        }
        return CommitNext(kernel, redo: false);
    }

    /// <summary>The game's data tables were initialized again (GTFO-API's data-initialized event after Forge's first generation):
    /// when the ledger still matches the tables nothing happened to Forge's rows; otherwise the current generation is committed
    /// again from the same input. The event is what is detected, not who raised it.</summary>
    internal static void DataTablesInitialized()
    {
        var kernel = Kernel;
        if (kernel == null || Suspension != null || Generations.Current.Number == 0 || Generations.Committing
            || NativeContentRefusal.Stopped != null) return;
        var problems = NativeDataBlockTables.AuditOwnership();
        LogGeneration(kernel, RuntimeLogLevel.Info, RuntimeHostLogCodes.ContentTablesReinitialized,
            Generations.Current.Number, problems.Count == 0 ? "intact" : "rows-missing", "host",
            problems.Count == 0 ? "" : string.Join("; ", problems.Take(8)));
        if (problems.Count == 0) return;
        CommitNext(kernel, redo: true);
    }

    /// <summary>The discovery the latest reload or redo admitted, committed or not: the reload hand-off reads each package's
    /// refusals from it (<see cref="ContentReloadHandoff"/>).</summary>
    internal static ContentDiskSnapshot? LastReloadSnapshot { get; private set; }

    private static CommandResult CommitNext(RuntimeKernel kernel, bool redo)
    {
        var current = Generations.Current;
        using var boundary = BeginDispatchBoundary();
        int hooksBefore = HarmonyLib.Harmony.GetAllPatchedMethods().Count();
        try
        {
            var snapshot = redo ? current.Snapshot!.Again() : Discover();
            LastReloadSnapshot = snapshot;
            ContentChanged? change = null;
            void Note(ContentChanged value) => change = value;
            ContentGeneration generation;
            Generations.Changed += Note;
            try { generation = Generations.Commit(snapshot, redo); }
            finally { Generations.Changed -= Note; }
            int hooksAfter = HarmonyLib.Harmony.GetAllPatchedMethods().Count();
            // A reload names only the participants whose content changed; carried ones keep their leases.
            LogGeneration(kernel, RuntimeLogLevel.Info, RuntimeHostLogCodes.ContentGenerationCommitted, generation.Number, redo ? "redo" : "reload",
                string.Join(",", change?.Changed ?? Array.Empty<string>()), "leases=" + generation.LeaseCount + " digest=" + generation.Digest
                + " hooksBefore=" + hooksBefore + " hooksAfter=" + hooksAfter);
            boundary.Complete();
            return CommandResult.Create(CommandStatuses.Succeeded, CommitStates.Confirmed, "content-reloaded", "",
                RuntimeJson.From(new { generation = generation.Number, previous = current.Number, digest = generation.Digest, hooksBefore, hooksAfter }));
        }
        catch (ContentGenerationRefusal refusal)
        {
            LogGeneration(kernel, RuntimeLogLevel.Error, RuntimeHostLogCodes.ContentGenerationRefused, current.Number + 1, refusal.Code,
                refusal.Participant, refusal.Message);
            boundary.Complete();
            // The content the game lost cannot be put back: Forge content stops until the game restarts.
            if (redo) NativeContentRefusal.Stop(refusal);
            return CommandResult.Rejected(refusal.Code, refusal.Message);
        }
        catch (Exception error)
        {
            // A rollback that failed: the tables are in a state no generation describes.
            LogGeneration(kernel, RuntimeLogLevel.Error, RuntimeHostLogCodes.ContentGenerationRefused, current.Number + 1,
                "content-native-state-unknown", "host", error.ToString());
            NativeContentRefusal.Stop(error);
            boundary.Complete();
            return CommandResult.FailedUnknown("content-native-state-unknown", "Content reload failed and could not be rolled back; Forge content is stopped until the game restarts.");
        }

        static ContentDiskSnapshot Discover()
        {
            ContentPackages.Rediscover(Paths.BepInExRootPath);
            return Generations.Discover(Paths.BepInExRootPath, startup: false);
        }
    }

    private static void LogGeneration(RuntimeKernel kernel, RuntimeLogLevel level, string code, long generation, string outcome, string participant, string detail)
    {
        if (!kernel.RuntimeLogs(level)) return;
        kernel.LogRuntime(level, code, new RuntimeLogFacts
        {
            Detail = detail,
            Values = new[]
            {
                RuntimeLogValue.Of("generation", generation), RuntimeLogValue.Of("outcome", outcome),
                RuntimeLogValue.Quote("participant", participant)
            }
        });
    }

    /// <summary>The rows the ledger gained since <paramref name="before"/>, as one lease: revoking it takes each owner's rows out of
    /// the tables again in one table transaction per owner (rows another step already removed are skipped).</summary>
    internal static ContentLease TableRowsLease(string owner, IReadOnlyList<(string Owner, string Type, uint Id)> before)
    {
        var known = new HashSet<(string, string, uint)>(before);
        var added = NativeDataBlockTables.OwnedRows().Where(row => !known.Contains(row)).ToArray();
        return new ContentLease(ContentLeaseKind.TableRows, owner, () =>
        {
            var present = new HashSet<(string, string, uint)>(NativeDataBlockTables.OwnedRows());
            foreach (var group in added.Where(present.Contains).GroupBy(row => row.Owner, StringComparer.Ordinal))
            {
                var transaction = new NativeDataBlockTables.Transaction(group.Key);
                foreach (var row in group) transaction.Remove(row.Type, row.Id);
                transaction.Commit();
                transaction.Release();
            }
        });
    }

    /// <summary>A Unity object the generation created; revoking the lease destroys it.</summary>
    internal static ContentLease UnityObjectLease(string owner, UnityEngine.Object value)
        => new(ContentLeaseKind.UnityObject, owner, () => { if (value != null) UnityEngine.Object.Destroy(value); });

    /// <summary>The behaviour plan index: every generation's plan set is that generation's read (contract §3.2 plan loading). A
    /// changed index is adopted in the lobby and announced to the session; the chosen level's plans are read again from it.</summary>
    private sealed class PlansParticipant : IContentParticipant
    {
        internal const string Name = "plans";
        public string Id => Name;
        public void Read(ContentDiskSnapshot snapshot) => snapshot.Put(Name, PlanDiscovery.Index(snapshot.Root, Kernel));
        public ContentPart Admit(ContentDiskSnapshot snapshot, ContentGeneration current)
        {
            var index = snapshot.Get<PlanIndex>(Name);
            return new(Name, Hash.Sha256Hex(string.Join("\n", index.Digests.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => row.Key + "=" + row.Value)
                .Concat(index.Refused.Select(file => "refused " + file.Path).OrderBy(row => row, StringComparer.Ordinal)))), index);
        }
        public PreparedContent Prepare(ContentChange change)
        {
            if (change.Unchanged) return PreparedContent.Carry(change.CurrentLeases!);
            return new Adoption((PlanIndex)change.Part.Value!, change.Current == null);
        }

        private sealed class Adoption : PreparedContent
        {
            private readonly PlanIndex _index;
            private readonly bool _startup;
            private PlanIndex? _previous;
            internal Adoption(PlanIndex index, bool startup) { _index = index; _startup = startup; }
            internal override void Commit(ContentGeneration next)
            {
                var kernel = Kernel!;
                _previous = _planIndex;
                Adopt(kernel, _index);
                if (!_startup || !kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
                kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.PlanAdopted, new RuntimeLogFacts
                {
                    Values = new[] { RuntimeLogValue.Of("level", "index"), RuntimeLogValue.Of("plans", (long)_index.Entries.Count), RuntimeLogValue.Of("elapsedMs", 0L) }
                });
            }
            internal override void Revert()
            {
                if (_previous != null) Adopt(Kernel!, _previous);
            }

            private static void Adopt(RuntimeKernel kernel, PlanIndex index)
            {
                _planIndex = index;
                // A file discovery could not read is in no level: its refusal is recorded now, once per index.
                if (index.Refused.Count != 0) kernel.LoadPlans(index.Refused);
                _admittedPlans ??= Array.Empty<PlanCandidate>();
                // The chosen level's plans come from this index: a read or adoption of the previous one is dropped.
                _levelRead = null; _levelAdopted = false; _adoptedLevel = null;
                // Adopting the set announces this side's own hello: the host's readiness signal a refused client waits for.
                NetworkBinding.AdoptPlans(index);
                if (_selectedLevel != null) ReadLevelPlans(_selectedLevel);
            }
        }
    }
}
