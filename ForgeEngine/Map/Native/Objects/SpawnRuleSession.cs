using System;
using System.Collections.Generic;
using System.Linq;
using AIGraph;
using BepInEx;
using Enemies;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the random spawn rules (contract M16). It reads this install's `spawn-rules.forge`
/// documents, installs the current level's rules into the instance ledger (<see cref="SpawnRuleRegistry"/>), and
/// fills the ledger from the vanilla spawn points:
///
/// - an item rule is told apart at the shared big-pickup builder by the zone and the item id the zone's distribution
///   drew (<see cref="BigPickupSetupHooks"/>); the spawned pickup is remembered at once, in generation order, and
///   its network identity is captured later — the state replicator the identity comes from is registered by then —
///   always from the front of the list, so the order of the ledger is the order the level generated;
/// - a rule whose spawn point is not hooked yet, or whose instance has no readable identity, records nothing and says
///   so by name (`map.spawn-rule-refused`); an instance is never attached to another rule or another entity;
/// - an enemy rule is told apart at the allocator that spawns each member: the group the member belongs to carries the
///   data block the zone's random enemy list drew, and the block's group type and difficulty, with the zone the member
///   spawned in, pick the rule (<see cref="SpawnRuleEnemySpawned"/>). Survival waves and every group that has no zone
///   of a rule are not this ledger's business. The member is remembered at once, in spawn order (the host's order is
///   the authority; clients never spawn members), and its identity is captured later like an item's.
///
/// The selection of the instances that carry behaviours is the ledger's: a level seed and the rule id decide the
/// positions, never a Unity instance id (see <see cref="SpawnRuleRegistry.Choose"/>). Ledger rows are per world.
/// </summary>
internal sealed class SpawnRuleSession : IDisposable
{
    [ThreadStatic] private static bool _inDistribution;

    /// <summary>Whether the big-pickup builder now running was reached through a zone distribution block.</summary>
    internal static bool InDistribution { get => _inDistribution; set => _inDistribution = value; }

    private abstract record Pending(SpawnRule Rule, long Epoch)
    {
        /// <summary>Whether the native instance is still there to read; one that is gone is refused by name.</summary>
        internal abstract bool Alive { get; }
    }

    private sealed record PendingItem(SpawnRule Rule, ItemInLevel Item, long Epoch) : Pending(Rule, Epoch)
    {
        internal override bool Alive => Item != null && !Item.WasCollected;
    }

    private sealed record PendingEnemy(SpawnRule Rule, EnemyAgent Agent, long Epoch) : Pending(Rule, Epoch)
    {
        internal override bool Alive => Agent != null && !Agent.WasCollected;
    }

    private IReadOnlyList<SpawnRule> _rules = Array.Empty<SpawnRule>();
    private readonly Func<long> _worldEpoch;
    private readonly Func<MapLevelReference?> _level;
    private readonly Func<LevelObjectModule?> _levels;
    private readonly Action<string> _report;
    private readonly Func<EnemyAgent, EntityReference?> _enemyReference;
    private readonly List<PendingItem> _pending = new();
    private readonly List<PendingEnemy> _pendingEnemies = new();
    private readonly ForgeCore.OnceReporter _warned = new();
    private long _loadedEpoch = -1;
    private bool _disposed;

    /// <summary>A host migration keeps the level and its spawned content: the world stamps move to the new number.</summary>
    internal void FollowWorld(long from, long to)
    {
        if (_loadedEpoch == from) _loadedEpoch = to;
        if (_sitesEpoch == from) _sitesEpoch = to;
    }

    internal SpawnRuleRegistry Registry { get; } = new();

    /// <summary>Built before the map-object module, which holds the ledger; the rule documents are read afterwards
    /// by <see cref="Admit"/>, when content admission has decided which packages and levels are allowed.</summary>
    internal SpawnRuleSession(Func<long> worldEpoch, Func<MapLevelReference?> level, Func<LevelObjectModule?> levels,
        Func<EnemyAgent, EntityReference?> enemyReference, Action<string> report)
    {
        _worldEpoch = worldEpoch; _level = level; _levels = levels; _enemyReference = enemyReference; _report = report;
        Registry.BeforeMatch = Flush;
    }

    private static readonly PublishedTextCodes ReadCodes = new("spawn-rule-path", "spawn-rule-unreadable", "spawn-rule-file-size",
        "spawn-rule-json", "spawn-rule-utf8");

    /// <summary>Reads the install's rule documents. A document or row that does not read is reported and left out;
    /// a rule id claimed twice keeps the first.</summary>
    internal void Admit(RundownPlacementAdmission admission)
    {
        var report = _report;
        var loaded = new List<SpawnRule>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in ContentPackages.Read(Paths.BepInExRootPath, ContentPackages.EngineReader, "forge", SpawnRuleManifest.FileName, ReadCodes, report))
        {
            if (!admission.AllowsDocument(document, "spawn-rule", report)) continue;
            if (!PublishedResources.TryRead(document, PublishedResources.MaximumFileBytes, ReadCodes, report, out var text)) continue;
            if (!SpawnRuleManifest.TryParse(text!, out var parsed, out var code, out var reason))
            {
                report(PublishedResources.Diagnostic(document.Relative, code!, reason!));
                continue;
            }
            foreach (var refusal in parsed!.Refusals) report(PublishedResources.Diagnostic(document.Relative, refusal.Code, refusal.Reason));
            foreach (var rule in parsed.Rules)
            {
                if (!admission.AllowsLevel(document, rule.Level, "spawn-rule", report)) continue;
                if (!claimed.Add(rule.Id))
                {
                    report(PublishedResources.Diagnostic(document.Relative, "spawn-rule-duplicate",
                        "Rule `" + rule.Id + "` is claimed by more than one document; the later row is left out."));
                    continue;
                }
                loaded.Add(rule);
            }
        }
        Install(loaded);
    }

    /// <summary>Takes the admitted rules as the install's whole rule set and hooks the factory batch that flushes the
    /// ledger when any rule exists.</summary>
    internal void Install(IReadOnlyList<SpawnRule> loaded)
    {
        _rules = Array.AsReadOnly(loaded.ToArray());
        _hasModelRules = _rules.Any(rule => rule.Kind == SpawnRuleKind.Model);
        if (_rules.Count != 0 && _batchDone == null)
        {
            _batchDone = ForgeCore.FactoryEvents.OnBatchDone(_ => { try { Flush(); } catch (Exception error) { MapLog.Contained("spawn-rule.batch-flush", error); } });
        }
    }

    private IDisposable? _batchDone;

    /// <summary>Whether any rule is authored: with none, the spawn hook does no work.</summary>
    internal bool Any => !_disposed && _rules.Count != 0;

    /// <summary>Whether any rule of this kind is authored: the hook of a kind that has no rule does no work.</summary>
    internal bool AnyOf(SpawnRuleKind kind) => !_disposed && _rules.Any(rule => rule.Kind == kind);

    private bool _hasModelRules;

    /// <summary>Whether any random model rule is authored; read per generation call and per frame, so it is a field.</summary>
    internal bool HasModelRules => !_disposed && _hasModelRules;

    private void EnsureLoaded()
    {
        long epoch = _worldEpoch();
        if (_loadedEpoch == epoch) return;
        _loadedEpoch = epoch;
        _pending.Clear(); _pendingEnemies.Clear();
        var level = _level();
        Registry.Load(level == null ? Array.Empty<SpawnRule>() : _rules.Where(rule => rule.Level == level).ToArray(), Builder.BuildSeed);
    }

    /// <summary>One pickup the shared big-pickup builder spawned through a zone distribution. The zone and the item id
    /// pick the rule; a pickup of no rule is not this ledger's business.</summary>
    internal void CollectItem(LG_PickupItem pickup, uint itemId)
    {
        if (!Any || pickup == null || pickup.WasCollected) return;
        EnsureLoaded();
        if (!Registry.HasRules) return;
        var item = pickup.gameObject.GetComponentInChildren<ItemInLevel>(true);
        var zone = pickup.SpawnNode?.m_zone;
        if (item == null || zone == null || ZoneIndex.Coordinates(zone) is not { } at) return;
        if (Registry.ItemRule(at.Dimension, at.Layer, at.Zone, itemId) is not { } rule) return;
        _pending.Add(new PendingItem(rule, item, _loadedEpoch));
    }

    /// <summary>One member the allocator spawned for a group of the factory (not a survival wave). The zone it spawned in
    /// and its group's data block pick the rule; a member of no rule is not this ledger's business.</summary>
    internal void CollectEnemy(EnemyAgent? agent, EnemyGroup? group, AIG_CourseNode? node)
    {
        if (!AnyOf(SpawnRuleKind.Enemy) || agent == null || agent.WasCollected || group == null || group.WasCollected) return;
        if (group.SurvivalWave != null || group.m_isSurvivalWave) return;
        EnsureLoaded();
        if (!Registry.HasRules) return;
        var block = group.m_groupDataBlock;
        var zone = (node ?? group.CurrentCourseNode)?.m_zone;
        if (block == null || zone == null || ZoneIndex.Coordinates(zone) is not { } at) return;
        if (Registry.EnemyRule(at.Dimension, at.Layer, at.Zone, (int)block.Type, (int)block.Difficulty) is not { } rule) return;
        _pendingEnemies.Add(new PendingEnemy(rule, agent, _loadedEpoch));
    }

    /// <summary>A big pickup spawned by the direct branch (an objective item or an authored item id) reaches the shared
    /// builder without a distribution block, so it belongs to no distribution rule. It says so by name instead of being
    /// skipped; it is never matched to a rule by its item id.</summary>
    internal void ReportOutside(LG_PickupItem pickup, uint itemId)
    {
        if (!Any || pickup == null) return;
        EnsureLoaded();
        if (!Registry.HasRules) return;
        var zone = pickup.SpawnNode?.m_zone;
        var at = zone == null ? null : ZoneIndex.Coordinates(zone);
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.SpawnRuleOutside, "Big item outside every random rule.",
            RuntimeLogValue.Of("item", itemId),
            RuntimeLogValue.Quote("zone", at is { } c ? c.Dimension + "/" + c.Layer + "/" + c.Zone : "unknown"));
    }

    // ExpeditionFunction.BigPickupItem (12) in native build 20403457: the marker group a random model rule places onto.
    private const ExpeditionFunction BigItemFunction = (ExpeditionFunction)12;

    /// <summary>Where one instance of a random model rule stands: the area of the free marker the spot picked, and its
    /// world pose as position (x, y, z) and rotation (x, y, z, w).</summary>
    internal sealed record ModelSite(LG_Area Area, double[] Position, double[] Rotation);

    private static readonly IReadOnlyDictionary<string, ModelSite> NoSites = new Dictionary<string, ModelSite>();
    private readonly MarkerOccupancy _occupancy = new();
    private IReadOnlyDictionary<string, ModelSite> _sites = NoSites;
    private long _sitesEpoch = long.MinValue;
    private readonly HashSet<string> _registeredModels = new(StringComparer.Ordinal);

    /// <summary>One marker that received a function prefab in vanilla's own generation. Rows belong to one world.</summary>
    internal void MarkOccupied(LG_MarkerSpawner? marker)
    {
        if (!HasModelRules || marker == null || marker.WasCollected) return;
        _occupancy.Mark(_worldEpoch(), marker.Pointer.ToInt64());
    }

    /// <summary>Whether a composed placement stands in for a random model rule's instance: its place is chosen by the
    /// rule, never read from the placement row.</summary>
    internal bool IsModelSlot(string id)
    {
        foreach (var rule in _rules)
            if (rule.Models != null && rule.Models.Contains(id)) return true;
        return false;
    }

    /// <summary>The slot placement id to its chosen spot, for the current world. Null while the level has not finished
    /// generating (no spot is final before that); empty when no model rule applies. Each rule takes `Expected` of its
    /// zones' free big-item markers by the shared deterministic pick, in slot order, so every peer reaches the same
    /// spots. A rule with fewer free markers than instances places the ones it has and says so by name.</summary>
    internal IReadOnlyDictionary<string, ModelSite>? ModelSites()
    {
        if (!HasModelRules) return NoSites;
        long epoch = _worldEpoch();
        if (_sitesEpoch == epoch) return _sites;
        if (!LG_Factory.BuildDone || _level() is not { } level) return null;
        EnsureLoaded();
        _sitesEpoch = epoch;
        _registeredModels.Clear();
        var sites = new Dictionary<string, ModelSite>(StringComparer.Ordinal);
        var taken = new HashSet<long>();
        foreach (var rule in _rules)
        {
            if (rule.Kind != SpawnRuleKind.Model || rule.Level != level) continue;
            var spots = FreeSpots(epoch, rule, taken);
            var picked = ModelRulePlacement.Pick(Registry.Seed, rule.Id, spots.Count, rule.Expected);
            for (int i = 0; i < picked.Length; i++)
            {
                var spot = spots[picked[i]];
                taken.Add(spot.Marker);
                sites[rule.Models![i]] = spot.Site;
            }
            if (picked.Length < rule.Expected) Refuse(rule, "points-short", picked.Length, rule.Expected);
        }
        _sites = sites;
        return _sites;
    }

    private readonly record struct Spot(long Marker, ModelSite Site);

    private List<Spot> FreeSpots(long epoch, SpawnRule rule, HashSet<long> taken)
    {
        var spots = new List<Spot>();
        var index = ZoneIndex.Current(epoch);
        foreach (var coordinates in rule.Zones!)
            foreach (var zone in index.Zones(new ZoneCoordinates(coordinates.Dimension, coordinates.Layer, coordinates.Zone)))
            {
                if (zone == null || zone.WasCollected || zone.m_areas == null) continue;
                for (int a = 0; a < zone.m_areas.Count; a++)
                {
                    var area = zone.m_areas[a];
                    var groups = area?.m_markerSpawnerGroups;
                    var group = groups != null && groups.Length > (int)BigItemFunction ? groups[(int)BigItemFunction] : null;
                    var markers = group?.GetAllSpawners();
                    if (area == null || markers == null) continue;
                    for (int m = 0; m < markers.Count; m++)
                    {
                        var marker = markers[m];
                        if (marker == null || marker.WasCollected || marker.m_parent == null) continue;
                        long key = marker.Pointer.ToInt64();
                        if (_occupancy.IsUsed(epoch, key) || taken.Contains(key) || spots.Exists(spot => spot.Marker == key)) continue;
                        var parent = marker.m_parent.transform;
                        var position = parent.TransformPoint(marker.m_localPosition);
                        var rotation = parent.rotation * marker.m_localRotation;
                        spots.Add(new Spot(key, new ModelSite(area,
                            new double[] { position.x, position.y, position.z },
                            new double[] { rotation.x, rotation.y, rotation.z, rotation.w })));
                    }
                }
            }
        return spots;
    }

    /// <summary>Records the placed instances of the model rules in slot order, once each per world: the placement that
    /// really stands is the ledger row, one that was refused is not. Called after the placed-model table is published.</summary>
    internal void RegisterModels(Func<string, EntityReference?> reference)
    {
        if (!HasModelRules || _sitesEpoch != _worldEpoch()) return;
        var level = _level();
        foreach (var rule in _rules)
        {
            if (rule.Kind != SpawnRuleKind.Model || rule.Level != level) continue;
            foreach (var id in rule.Models!)
            {
                if (!_sites.ContainsKey(id) || _registeredModels.Contains(id) || reference(id) is not { } entity) continue;
                _registeredModels.Add(id);
                int before = Registry.Counts(rule.Id).Registered;
                bool attached = Registry.Register(rule, entity);
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.SpawnRuleInstance, "Random rule instance recorded.",
                    RuntimeLogValue.Quote("rule", rule.Id), RuntimeLogValue.Quote("kind", "model"),
                    RuntimeLogValue.Quote("entity", entity.Id), RuntimeLogValue.Of("position", before),
                    RuntimeLogValue.Quote("attached", attached ? "yes" : "no"));
            }
        }
    }

    /// <summary>Captures the identity of the remembered pickups and records them, from the front. The first one that
    /// has no identity yet stops the pass, so the ledger never records a later instance before an earlier one. Called
    /// before any fact that needs the ledger is read.</summary>
    internal void Flush()
    {
        if (!Any || _pending.Count == 0 && _pendingEnemies.Count == 0 || _levels() is not { } module) return;
        long epoch = _worldEpoch();
        Drain(_pending, epoch, "item", pending => LevelInstanceObservation.Item(pending.Item, module));
        Drain(_pendingEnemies, epoch, "enemy", pending => _enemyReference(pending.Agent));
    }

    private void Drain<T>(List<T> list, long epoch, string kind, Func<T, EntityReference?> identity) where T : Pending
    {
        int done = 0;
        while (done < list.Count)
        {
            var next = list[done];
            if (next.Epoch != epoch) { done++; continue; } // another world: its rows were dropped with the ledger
            if (!next.Alive)
            {
                Refuse(next.Rule, "instance-unreadable");
                done++;
                continue;
            }
            var reference = identity(next);
            if (reference == null) break;
            int before = Registry.Counts(next.Rule.Id).Registered;
            bool attached = Registry.Register(next.Rule, reference);
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.SpawnRuleInstance, "Random rule instance recorded.",
                RuntimeLogValue.Quote("rule", next.Rule.Id), RuntimeLogValue.Quote("kind", kind),
                RuntimeLogValue.Quote("entity", reference.Id), RuntimeLogValue.Of("position", before),
                RuntimeLogValue.Quote("attached", attached ? "yes" : "no"));
            done++;
        }
        list.RemoveRange(0, done);
    }
    /// <summary>The count a rule produced against the count its author expected, logged once per rule and world after
    /// the level finished generating.</summary>
    internal void ReportCounts()
    {
        if (!Any) return;
        Flush();
        foreach (var rule in _rules)
        {
            if (rule.Level != _level() || rule.Kind != SpawnRuleKind.Item) continue;
            var (registered, _, _) = Registry.Counts(rule.Id);
            if (registered == rule.Expected || !_warned.ShouldReport(_loadedEpoch + "/" + rule.Id)) continue;
            Refuse(rule, registered < rule.Expected ? "count-short" : "count-over", registered, rule.Expected);
        }
    }

    private static void Refuse(SpawnRule rule, string reason, int? registered = null, int? expected = null)
    {
        var values = new List<RuntimeLogValue> { RuntimeLogValue.Quote("rule", rule.Id), RuntimeLogValue.Quote("reason", reason) };
        if (registered != null) values.Add(RuntimeLogValue.Of("registered", registered.Value));
        if (expected != null) values.Add(RuntimeLogValue.Of("expected", expected.Value));
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.SpawnRuleRefused, "Random rule refused.", values.ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _batchDone?.Dispose(); _batchDone = null;
        _pending.Clear(); _pendingEnemies.Clear();
        _occupancy.Clear(); _sites = NoSites; _sitesEpoch = long.MinValue; _registeredModels.Clear();
        Registry.Clear();
    }
}
