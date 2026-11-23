using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeMap.Generation;

namespace ForgeMap.Native;

/// <summary>
/// The Map domain's two content generation participants (contract I-RELOAD tiers A, B and C):
/// <list type="bullet">
/// <item><see cref="Content"/> (<c>map</c>): the Rundowns and their native blocks, the menu and every Map sidecar (tiers B and C).
/// Startup commits them through the host callback Map registered, a reload through <see cref="MapPluginSession.CommitContent"/>
/// directly; both are the same commit. Unchanged files keep the current generation's content; a redo writes the same input again.</item>
/// <item><see cref="Generation"/> (<c>map-generation</c>): the compiled generation plans every build reads (tier A). Every
/// generation builds from its own read: the plans of the Rundowns the Map participant published, put in as one set.</item>
/// </list>
/// </summary>
internal static class MapContentParticipants
{
    internal const string ContentName = "map", GenerationName = "map-generation";
    private const string EntriesName = "entries";

    /// <summary><paramref name="refusePlans"/> refuses a refused package's plans with it (<see cref="RundownPlanAdmission"/>).</summary>
    internal static void Register(MapPluginSession session, Action<MapPackageRejection> refusePlans)
    {
        GameRuntimeBridge.RegisterContentParticipant(new Content(session, refusePlans));
        GameRuntimeBridge.RegisterContentParticipant(new Generation());
    }

    /// <summary>The captured packages of one discovery: the session's own capture at Map Load for startup, a new capture for a
    /// reload. The digest leaves the generation plans out: a plan change alone is the generation participant's. Map validates its
    /// Rundowns against the accepted content entries, so it keeps its content only when neither its files nor the entries changed.</summary>
    private sealed class Content : IContentParticipant
    {
        private readonly MapPluginSession _session;
        private readonly Action<MapPackageRejection> _refusePlans;
        // The entries part the standing Map content was validated against.
        private string? _entries;
        internal Content(MapPluginSession session, Action<MapPackageRejection> refusePlans) { _session = session; _refusePlans = refusePlans; }
        public string Id => ContentName;
        public void Read(ContentDiskSnapshot snapshot) => snapshot.Put(ContentName, snapshot.Startup
            ? _session.CapturedPackages ?? throw new InvalidOperationException("Map captured no packages at Load.")
            : MapPackageAdmission.Capture(snapshot.Root, MapReport.Write, _refusePlans));

        public ContentPart Admit(ContentDiskSnapshot snapshot, ContentGeneration current)
        {
            var packages = snapshot.Get<MapPackageAdmission>(ContentName);
            var rows = new List<string>();
            foreach (var stage in packages.Stages)
            {
                foreach (var document in stage.Documents)
                    if (!document.Resource.Relative.EndsWith("/forge/map-generation.forge", StringComparison.Ordinal))
                        rows.Add(document.Resource.Relative + "=" + Hash.Sha256Hex(document.Text));
                foreach (var rejection in stage.Rejections) rows.Add("rejected " + rejection.Path + " " + rejection.Reason);
            }
            foreach (var pack in packages.CapturedPacks)
            {
                rows.Add(pack.Path + " " + pack.Version + " slot=" + pack.MenuSlot);
                foreach (var block in pack.Blocks) rows.Add(pack.Path + "#" + block.Type + "/" + block.PersistentId + "=" + Hash.Sha256Hex(block.Data.ToJsonString()));
            }
            rows.Sort(StringComparer.Ordinal);
            return new(ContentName, Hash.Sha256Hex(string.Join("\n", rows)), packages);
        }

        public PreparedContent Prepare(ContentChange change)
        {
            var entries = change.Next.Part(EntriesName)?.Digest;
            if (change.Unchanged && entries == _entries) return PreparedContent.Carry(change.CurrentLeases!);
            return new Swap(this, (MapPackageAdmission)change.Part.Value!, entries, startup: change.Current == null, change.Redo);
        }

        /// <summary>One generation's Map content in place of the previous generation's. The commit is validated against the accepted
        /// content entries, which exist only once the entries participant committed, so validation and the native writes share the
        /// commit; a refusal there undoes itself and fails the generation like any failed commit.</summary>
        private sealed class Swap : PreparedContent
        {
            private readonly Content _owner;
            private readonly MapPluginSession _session;
            private readonly MapPackageAdmission _packages;
            private readonly string? _entries;
            private readonly bool _startup, _redo;
            private MapContentGeneration? _committed, _previous;
            internal Swap(Content owner, MapPackageAdmission packages, string? entries, bool startup, bool redo)
            { _owner = owner; _session = owner._session; _packages = packages; _entries = entries; _startup = startup; _redo = redo; }

            internal override void Commit(ContentGeneration next)
            {
                _previous = _session.Content;
                var content = next.Result<AdmittedContentSnapshot>(EntriesName);
                if (_startup)
                {
                    // The host callback Map registered at Load (MapHost): the same commit, after the package identity preflight.
                    ContentPackHostStartup.Dispatch(GameRuntimeBridge.Kernel!, content);
                    _committed = _session.Content ?? throw new InvalidOperationException("The Map startup callback committed no content.");
                }
                else
                {
                    // A later generation's package refusals (its capture's and the Rundown checks') refuse the plans of those packages
                    // from the moment it stands, and are reported like startup's, without stopping Forge content.
                    RundownPlanAdmission.BeginGeneration(GameRuntimeBridge.Kernel!, _redo);
                    _committed = _session.CommitContent(_packages, content, _redo);
                }
                var generation = _committed;
                // What this generation put in, each revoked only while it is still the committed generation (a later generation
                // replaces the same rows, menu values and halves in place and lets these go through Release).
                bool Committed() => ReferenceEquals(_session.Content, generation);
                Leases.Add(ContentLeaseKind.TableRows, ContentName, () => { if (Committed()) { generation.Rows.RevokeAll(); _session.ForgetContent(); } });
                Leases.Add(ContentLeaseKind.ModelRegistration, ContentName, () => { if (Committed()) MapContentHost.Dispose(generation.Prepared); });
                Leases.Add(ContentLeaseKind.UnityObject, ContentName, () => { if (Committed()) RoomRecipeHost.Reset(clearRecipes: true); });
                Leases.Add(ContentLeaseKind.Menu, ContentName, generation.Menu.Uninstall);
                Leases.Add(ContentLeaseKind.SidecarRows, ContentName, () => { if (Committed()) _session.WithdrawSidecars(); });
                next.Publish(ContentName, generation);
            }

            // The rows, menu and halves were replaced in place: confirming lets the previous generation's objects go, and the
            // Rundown page, laid out from the previous blocks, is placed again.
            internal override void Release(ContentLeases? previous)
            {
                if (_committed == null) return;
                _owner._entries = _entries;
                if (!_startup) RundownPlanAdmission.EndGeneration(GameRuntimeBridge.Kernel!, stood: true);
                _session.ReleaseContent(_committed, _previous);
                if (_previous != null) RundownMenu.PlaceAgain();
            }

            internal override void Revert()
            {
                if (!_startup) RundownPlanAdmission.EndGeneration(GameRuntimeBridge.Kernel!, stood: false);
                var committed = _committed;
                _committed = null;
                if (committed != null) _session.RevertContent(committed, _previous);
            }
        }
    }

    /// <summary>The generation plans of the published Rundowns. Startup activates the plans the Map commit published. A reload takes
    /// the plans of the Rundowns the Map participant published: when Map committed this read, the plans it prepared with them; when
    /// Map kept its content (only plans changed), the plans of this read for the same Rundowns, each checked as a reload's plan
    /// (supported requests, generator hooks already on). The active set is swapped in one step and the previous set is kept until
    /// the generation is confirmed. A redo keeps the active plans (managed objects the game cannot lose).</summary>
    private sealed class Generation : IContentParticipant
    {
        public string Id => GenerationName;
        public void Read(ContentDiskSnapshot snapshot) { }

        public ContentPart Admit(ContentDiskSnapshot snapshot, ContentGeneration current)
        {
            var programs = Candidates(snapshot.Get<MapPackageAdmission>(ContentName));
            return new(GenerationName, Hash.Sha256Hex(string.Join("\n", programs.OrderBy(row => row.Key, StringComparer.Ordinal)
                .Select(row => row.Key + "=" + row.Value.Hash))), programs);
        }

        private static IReadOnlyDictionary<string, GenerationProgram> Candidates(MapPackageAdmission packages) => packages.Stages
            .Where(stage => !stage.IsRejected && stage.Generation != null && stage.Generation.Plan.Levels.GetArrayLength() != 0)
            .ToDictionary(stage => stage.Root, stage => stage.Generation!, StringComparer.Ordinal);

        public PreparedContent Prepare(ContentChange change)
        {
            if (change.Redo && change.CurrentLeases != null) return PreparedContent.Carry(change.CurrentLeases);
            var packages = change.Next.Snapshot!.Get<MapPackageAdmission>(ContentName);
            if (change.Current == null) return new Swap(next => next.Result<MapContentGeneration>(ContentName).Accepted.Generations);
            var candidates = Candidates(packages);
            return new Swap(next =>
            {
                // Map committed this read: its preparation already withdrew each package whose plan cannot be built.
                var map = next.Result<MapContentGeneration>(ContentName);
                if (ReferenceEquals(map.Packages, packages)) return map.Accepted.Generations;
                // Map kept its content: only the plans of the Rundowns it publishes are checked, before anything is written; one
                // that cannot be built refuses the reload. A package that is not published does not count.
                var published = map.Published.Packs.Select(pack => PublishedResources.PackageRoot(pack.Path)).ToHashSet(StringComparer.Ordinal);
                var programs = candidates.Where(row => published.Contains(row.Key)).OrderBy(row => row.Key, StringComparer.Ordinal)
                    .Select(row => row.Value).ToArray();
                foreach (var program in programs)
                {
                    try { CompiledMapGenerator.RequireInstalled(program); }
                    catch (GenerationRefusal refusal) { throw new ContentGenerationRefusal(refusal.Code, GenerationName, refusal.Path, refusal); }
                }
                return programs;
            });
        }

        private sealed class Swap : PreparedContent
        {
            private readonly Func<ContentGeneration, IReadOnlyList<GenerationProgram>> _programs;
            private IReadOnlyList<GenerationProgram>? _previous;
            private long _previousGeneration;
            internal Swap(Func<ContentGeneration, IReadOnlyList<GenerationProgram>> programs) => _programs = programs;
            internal override void Commit(ContentGeneration next)
            {
                var programs = _programs(next);
                _previous = CompiledMapGenerator.ActivePrograms;
                _previousGeneration = CompiledMapGenerator.ContentGeneration;
                CompiledMapGenerator.Activate(programs, next.Number);
                next.Publish(GenerationName, programs);
                Leases.Add(ContentLeaseKind.GenerationPlan, GenerationName, CompiledMapGenerator.Stop);
            }
            // The set is replaced in place: confirming lets the previous plans go without stopping the new ones.
            internal override void Release(ContentLeases? previous) => _previous = null;
            internal override void Revert()
            {
                if (_previous != null) CompiledMapGenerator.Activate(_previous, _previousGeneration);
            }
        }
    }
}
