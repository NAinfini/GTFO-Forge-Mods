using System;
using System.Collections.Generic;
using System.Linq;
using ChainedPuzzles;
using ForgeCore;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>Puts the replay level's objects into recorded states through ForgeCore's <see cref="WorldStates"/>. The replay level's objects have
/// other instance ids than the recorded ones, so an object is found by position: the nearest of its kind within <see cref="Reach"/> metres of
/// the place the recording noted. A pickup is found that way the first time its recorded key comes and is kept for that key afterwards, so an
/// item put down somewhere else (a carried cell dropped) moves there instead of being looked for at the new place. Items the recording
/// spawned during the level (ItemSpawn) are spawned here, at the recorded place, and are then found like the level's own. Objects are looked
/// up once per level (the lists are dropped by <see cref="Reset"/>); every kind found by place is kept for its recorded key like the pickups. Kinds that are not level objects are refused and counted by the caller.
/// Main thread only.</summary>
internal sealed class WorldStateSetter
{
    private const float Reach = 3f;
    /// <summary>How far a pickup may lie from its recorded resting place before a jump puts it back there (Reach only finds it).</summary>
    private const float PlaceTolerance = 0.3f;
    // The recording stores the game's ItemMode number; this stops the build if the game's number for Pickup is not the one the format names.
    private const int PickupModeIsTheRecordedOne = 1 / ((int)ItemMode.Pickup == (int)ItemSpawned.PickupMode ? 1 : 0);
    private LG_SecurityDoor[]? _security;
    private LG_WeakDoor[]? _weak;
    private LG_PowerGenerator_Core[]? _generators;
    private List<LG_PickupItem_Sync>? _items;
    private ChainedPuzzleInstance[]? _puzzles;
    private CP_Bioscan_Core[]? _scans;
    private LG_ResourceContainer_Sync[]? _containers;
    private LG_WardenObjective_Reactor[]? _reactors;
    private LG_WeakLock[]? _locks;
    private CP_Cluster_Core[]? _clusters;
    private LG_DisinfectionStation[]? _stations;
    // The status this class last gave each station: the game's handler takes the old one too.
    private readonly Dictionary<IntPtr, eDisinfectionStationStatus> _stationStatus = new();
    // Recorded pickup key -> the replay level's pickup it was matched to; the matched ones are not matched to another key.
    private readonly Dictionary<ulong, LG_PickupItem_Sync> _itemByKey = new();
    private readonly HashSet<IntPtr> _matched = new();
    // The items spawned for the recording's ItemSpawn events, by the spawn's number (null: not spawned, see _tried).
    private readonly List<GameObject?> _spawned = new();
    private readonly List<bool> _tried = new();
    // Whether this class last showed (true) or hid each spawned item: only a change of that is applied, so a pickup the game hid itself
    // (picked up) is left as the game has it.
    private readonly List<bool> _visible = new();

    internal int Done { get; private set; }
    internal int Refused { get; private set; }

    /// <summary>The level's pickup a recorded item state was already set on, else null.</summary>
    // Where the level had each recorded pickup (the last word of its state), the place the level's own object is searched for and put back to.
    private readonly Dictionary<ulong, Vector3> _home = new();

    internal void Home(ulong key, Vector3 at) => _home[key] = at;

    internal Vector3? Home(ulong key) => _home.TryGetValue(key, out var at) ? at : null;

    internal LG_PickupItem_Sync? Known(ulong key) => _itemByKey.TryGetValue(key, out var kept) && kept != null && !kept.WasCollected ? kept : null;

    // A door whose object is switched off cannot play its state: it gets the last one the moment it is on (WakeDoors). Its object is kept with it, read
    // when it went to sleep: a component stays on the object it was made on for as long as it lives, and the door's life is checked first.
    private readonly Dictionary<IntPtr, (Component Door, GameObject Body, eDoorStatus Status)> _asleep = new();
    private static readonly List<IntPtr> Woken = new();

    private void PutDoor(Component door, eDoorStatus status, bool instant)
    {
        var body = door.gameObject;
        if (!body.activeInHierarchy) { _asleep[door.Pointer] = (door, body, status); return; }
        _asleep.Remove(door.Pointer);
        if (door is LG_SecurityDoor security) WorldStates.Door(security, status, instant);
        else if (door is LG_WeakDoor weak) WorldStates.Door(weak, status, instant);
    }

    /// <summary>Per frame: the doors that have come on since their state was set take it now, at once.</summary>
    internal void WakeDoors()
    {
        if (_asleep.Count == 0) return;
        Woken.Clear();
        foreach (var (pointer, asleep) in _asleep) if (asleep.Door == null || asleep.Door.WasCollected || asleep.Body == null || asleep.Body.activeInHierarchy) Woken.Add(pointer);
        foreach (var pointer in Woken)
        {
            var (door, _, status) = _asleep[pointer];
            _asleep.Remove(pointer);
            if (door != null && !door.WasCollected) PutDoor(door, status, true);
        }
    }

    internal void Reset()
    {
        _asleep.Clear();
        ForgetLists(); _stationStatus.Clear(); _requeryAt = 0f;
        _itemByKey.Clear(); _home.Clear(); _kept.Clear(); _matched.Clear(); _spawned.Clear(); _tried.Clear(); _visible.Clear();
        Done = Refused = 0;
        if (_musicHeld && DramaManager.Current != null) DramaManager.Current.enabled = true;
        if (_musicHeld && MusicManager.Current != null) MusicManager.Current.enabled = true;
        _musicHeld = false;
    }

    // The scene's object lists are read once and kept; a miss asks again (at most every few seconds): objects the level made or woke since, such as a puzzle made
    // when its trigger fired or a door that was shut off, are not in a list read at the start.
    private const float RequerySeconds = 5f;
    private float _requeryAt;

    private void ForgetLists()
    {
        _bulkheads = null; _hsus = null; _portals = null; _generatorClusters = null; _turbines = null; _movables = null;
        _security = null; _weak = null; _generators = null; _items = null; _puzzles = null; _scans = null; _containers = null; _reactors = null; _locks = null; _clusters = null; _stations = null;
    }

    public bool Set(ushort kind, ulong key, Vector3 near, ulong state, ulong extra, bool instant)
    {
        var ok = Apply(kind, key, near, state, extra, instant);
        if (!ok && Time.unscaledTime >= _requeryAt)
        {
            _requeryAt = Time.unscaledTime + RequerySeconds;
            ForgetLists();
            ok = Apply(kind, key, near, state, extra, instant);
        }
        if (ok) Done++; else Refused++;
        return ok;
    }

    private bool Apply(ushort kind, ulong key, Vector3 near, ulong state, ulong extra, bool instant)
    {
        switch (kind)
        {
            case EventKind.Door:
                {
                    var kept = Kept(kind, key);
                    if (kept is LG_SecurityDoor or LG_WeakDoor) { PutDoor((Component)kept, (eDoorStatus)state, instant); return true; }
                    _security ??= UnityEngine.Object.FindObjectsOfType<LG_SecurityDoor>();
                    _weak ??= UnityEngine.Object.FindObjectsOfType<LG_WeakDoor>();
                    var security = Nearest(_security, near, out var sd);
                    var weak = Nearest(_weak, near, out var wd);
                    if (security != null && (weak == null || sd <= wd)) { _kept[(kind, key)] = security; PutDoor(security, (eDoorStatus)state, instant); return true; }
                    if (weak != null) { _kept[(kind, key)] = weak; PutDoor(weak, (eDoorStatus)state, instant); return true; }
                    return false;
                }
            case EventKind.Generator:
                {
                    _generators ??= UnityEngine.Object.FindObjectsOfType<LG_PowerGenerator_Core>();
                    var generator = Found(kind, key, _generators, near);
                    if (generator == null) return false;
                    WorldStates.Generator(generator, (ePowerGeneratorStatus)state, instant);
                    return true;
                }
            case EventKind.ItemState:
                {
                    var item = Item(key, near);
                    if (item == null) return false;
                    var status = (ePickupItemStatus)state;
                    // Put down away from where the replay's copy lies: placed at the recorded place.
                    var moved = status == ePickupItemStatus.PlacedInLevel && Vector3.Distance(item.transform.position, near) > PlaceTolerance;
                    WorldStates.Item(item, status, instant, moved ? near : null);
                    return true;
                }
            case EventKind.Puzzle:
                {
                    _puzzles ??= UnityEngine.Object.FindObjectsOfType<ChainedPuzzleInstance>();
                    var puzzle = Found(kind, key, _puzzles, near);
                    if (puzzle == null) return false;
                    WorldStates.Puzzle(puzzle, (eChainedPuzzleStatus)state, instant);
                    return true;
                }
            case EventKind.Scan:
                {
                    _scans ??= UnityEngine.Object.FindObjectsOfType<CP_Bioscan_Core>();
                    var scan = Found(kind, key, _scans, near);
                    if (scan == null) return false;
                    // The extra as the recorder packs it: percent, then the slots inside, then how many are needed. A slot with no agent of its own counts through the observer.
                    var inside = new Il2CppSystem.Collections.Generic.List<PlayerAgent>();
                    for (var slot = 0; slot < 4; slot++)
                    {
                        if ((extra >> (8 + slot) & 1) == 0) continue;
                        var agent = Puppeteer.PlayerOf(slot)?.Agent ?? Players.Local;
                        if (agent != null) inside.Add(agent);
                    }
                    WorldStates.Scan(scan, (eBioscanStatus)state, (extra & 0xFF) / 100f, inside, (int)(extra >> 12 & 0xF), instant);
                    return true;
                }
            case EventKind.ResourceBox:
                {
                    _containers ??= UnityEngine.Object.FindObjectsOfType<LG_ResourceContainer_Sync>();
                    var container = Found(kind, key, _containers, near);
                    if (container == null) return false;
                    WorldStates.Container(container, (eResourceContainerStatus)state, instant);
                    return true;
                }
            case EventKind.Reactor:
                {
                    _reactors ??= UnityEngine.Object.FindObjectsOfType<LG_WardenObjective_Reactor>();
                    var reactor = Found(kind, key, _reactors, near);
                    if (reactor == null) return false;
                    WorldStates.Reactor(reactor, (eReactorStatus)state, extra / 100f, instant);
                    return true;
                }
            case EventKind.Music:
                SetMusic((MUS_State)(int)state);
                return true;
            case EventKind.WeakLock:
                {
                    var weakLock = Found(kind, key, _locks ??= UnityEngine.Object.FindObjectsOfType<LG_WeakLock>(), near);
                    if (weakLock == null) return false;
                    WorldStates.WeakLock(weakLock, (eWeakLockStatus)state, (extra & 1) == 1, instant);
                    return true;
                }
            case EventKind.Cluster:
                {
                    var cluster = Found(kind, key, _clusters ??= UnityEngine.Object.FindObjectsOfType<CP_Cluster_Core>(), near);
                    if (cluster == null) return false;
                    return WorldStates.Cluster(cluster, (eClusterStatus)state, extra / 100f, instant);
                }
            case EventKind.Disinfection:
                {
                    var station = Found(kind, key, _stations ??= UnityEngine.Object.FindObjectsOfType<LG_DisinfectionStation>(), near);
                    if (station == null) return false;
                    var status = (eDisinfectionStationStatus)state;
                    _stationStatus.TryGetValue(station.Pointer, out var was);
                    WorldStates.Disinfection(station, was, status);
                    _stationStatus[station.Pointer] = status;
                    return true;
                }
            default: return Mechanism(kind, key, near, state, extra, instant);
        }
    }

    // The level machines of events 77 to 85. A machine the game refuses (an object it has not set up) is skipped with a note, never invented.
    private LG_BulkheadDoorController_Core[]? _bulkheads;
    private LG_HSUActivator_Core[]? _hsus;
    private LG_DimensionPortal[]? _portals;
    private LG_PowerGeneratorCluster[]? _generatorClusters;
    private HeavyFogRepellerGlobalState[]? _turbines;

    private bool Mechanism(ushort kind, ulong key, Vector3 near, ulong state, ulong extra, bool instant)
    {
        try
        {
            switch (kind)
            {
                case EventKind.Bulkhead:
                    return Found(kind, key, _bulkheads ??= UnityEngine.Object.FindObjectsOfType<LG_BulkheadDoorController_Core>(), near) is { } bulkhead && WorldStates.Bulkhead(bulkhead, (byte)state, instant);
                case EventKind.HsuActivator:
                    return Found(kind, key, _hsus ??= UnityEngine.Object.FindObjectsOfType<LG_HSUActivator_Core>(), near) is { } hsu && WorldStates.Hsu(hsu, (byte)state, (extra & 1) == 1, instant);
                case EventKind.Portal:
                    return Found(kind, key, _portals ??= UnityEngine.Object.FindObjectsOfType<LG_DimensionPortal>(), near) is { } portal && WorldStates.Portal(portal, (byte)state, (extra & 1) == 1, (extra & 2) == 2, instant);
                case EventKind.GeneratorCluster:
                    return Found(kind, key, _generatorClusters ??= UnityEngine.Object.FindObjectsOfType<LG_PowerGeneratorCluster>(), near) is { } cluster && WorldStates.GeneratorCluster(cluster, (int)state, instant);
                case EventKind.FogTurbine:
                    {
                        // A turbine a player picked up is no active scene object any more: the floor's own children are searched too, or a
                        // jump that lands while it is carried reports that the level's turbine could not be found (the recording's only kind 81).
                        if (Found(kind, key, _turbines ??= LevelFind.All<HeavyFogRepellerGlobalState>(), near) is not { } turbine) return false;
                        // A turbine a player carries cannot follow the replay's puppet here: it is shown standing where the recording last saw it (extra: owner byte, then who + 1).
                        var owner = (byte)(extra & 0xFF) == (byte)eCarryItemWithGlobalStateOwner.Player ? (byte)eCarryItemWithGlobalStateOwner.StaticPosition : (byte)(extra & 0xFF);
                        return WorldStates.FogTurbine(turbine, (byte)state, owner, near, instant);
                    }
                case EventKind.ScanMove:
                    {
                        // The scan's own events keep the scan found for this key; a moving scan has left the place its first move was recorded at.
                        var movable = (Kept(EventKind.Scan, key) as CP_Bioscan_Core)?.GetComponent<CP_BasicMovable>() ?? Found(kind, key, MovingScans(), near);
                        return movable != null && WorldStates.ScanMove(movable, state / 1000f, extra == 1, instant);
                    }
                case EventKind.Spitter:
                    {
                        var spitter = SpitterAt(near);
                        if (spitter == null) return false;
                        if (state != 0) WorldStates.Spitter(spitter, state == 2);
                        return true;
                    }
                default: return false;
            }
        }
        catch (Exception e)
        {
            Skipped.Note("a level machine refused its recorded state", "kind " + kind + ": " + e.Message);
            return false;
        }
    }

    private CP_BasicMovable[]? _movables;

    private CP_BasicMovable[] MovingScans() => _movables ??= UnityEngine.Object.FindObjectsOfType<CP_BasicMovable>();

    // The spitter whose own position is nearest to the recorded place (they are polled by index, which the replay's own level numbers alike but this does not rely on).
    private static InfectionSpitter? SpitterAt(Vector3 near)
    {
        var all = InfectionSpitter.s_allSpitters;
        if (all == null) return null;
        InfectionSpitter? best = null; var distance = Reach;
        for (var i = 0; i < all.Count; i++)
        {
            var spitter = all[i];
            if (spitter == null || spitter.WasCollected) continue;
            var d = Vector3.Distance(spitter.m_position, near);
            if (d <= distance) { best = spitter; distance = d; }
        }
        return best;
    }

    public bool Original(ushort kind, Vector3 near, out ulong state, out ulong extra)
    {
        state = extra = 0;
        switch (kind)
        {
            case EventKind.Door:
                {
                    _security ??= UnityEngine.Object.FindObjectsOfType<LG_SecurityDoor>();
                    _weak ??= UnityEngine.Object.FindObjectsOfType<LG_WeakDoor>();
                    var security = Nearest(_security, near, out var sd);
                    var weak = Nearest(_weak, near, out var wd);
                    if (security != null && (weak == null || sd <= wd)) { state = (ulong)(int)security.LastStatus; return true; }
                    if (weak == null) return false;
                    state = (ulong)(int)weak.LastStatus;
                    return true;
                }
            case EventKind.WeakLock:
                {
                    var weakLock = Nearest(_locks ??= UnityEngine.Object.FindObjectsOfType<LG_WeakLock>(), near, out _);
                    if (weakLock == null) return false;
                    state = (ulong)(int)weakLock.Status;
                    return true;
                }
            default: return MechanismOriginal(kind, near, out state, out extra);
        }
    }

    // What the replay's own machine of that kind holds before the recording sets it (the state a jump back puts it in).
    private bool MechanismOriginal(ushort kind, Vector3 near, out ulong state, out ulong extra)
    {
        state = extra = 0;
        switch (kind)
        {
            case EventKind.Bulkhead:
                {
                    if (Nearest(_bulkheads ??= UnityEngine.Object.FindObjectsOfType<LG_BulkheadDoorController_Core>(), near, out _) is not { } bulkhead || !WorldStates.BulkheadStatus(bulkhead, out var status)) return false;
                    state = status;
                    return true;
                }
            case EventKind.HsuActivator:
                {
                    if (Nearest(_hsus ??= UnityEngine.Object.FindObjectsOfType<LG_HSUActivator_Core>(), near, out _) is not { } hsu || !WorldStates.HsuStatus(hsu, out var status, out var incomplete)) return false;
                    state = status; extra = incomplete ? 1UL : 0UL;
                    return true;
                }
            case EventKind.Portal:
                {
                    if (Nearest(_portals ??= UnityEngine.Object.FindObjectsOfType<LG_DimensionPortal>(), near, out _) is not { } portal || !WorldStates.PortalStatus(portal, out var status, out var closed, out var incomplete)) return false;
                    state = status; extra = (closed ? 1UL : 0UL) | (incomplete ? 2UL : 0UL);
                    return true;
                }
            case EventKind.GeneratorCluster:
                {
                    if (Nearest(_generatorClusters ??= UnityEngine.Object.FindObjectsOfType<LG_PowerGeneratorCluster>(), near, out _) is not { } cluster || !WorldStates.GeneratorClusterStep(cluster, out var step)) return false;
                    state = (ulong)Math.Max(0, step);
                    return true;
                }
            case EventKind.FogTurbine:
                {
                    if (Nearest(_turbines ??= LevelFind.All<HeavyFogRepellerGlobalState>(), near, out _) is not { } turbine || !WorldStates.FogTurbineState(turbine, out var status, out var owner, out _)) return false;
                    state = status; extra = owner;
                    return true;
                }
            case EventKind.ScanMove:
                {
                    if (Nearest(MovingScans(), near, out _) is not { } movable || !WorldStates.ScanMoveState(movable, out var lerp, out var paused)) return false;
                    state = (ulong)Math.Round(Mathf.Clamp01(lerp) * 1000f); extra = paused ? 1UL : 0UL;
                    return true;
                }
            // A spitter's burst or glue ends by itself: there is nothing to put back.
            default: return false;
        }
    }

    // The recorded music. The drama machine, which would choose the music from enemies and tension nothing here has, stops once the recording says what plays.
    private static bool _musicHeld;
    private static MUS_State _music;
    private static float _musicCheckAt;
    private const float MusicCheckSeconds = 1f;

    private static void SetMusic(MUS_State music)
    {
        _music = music;
        // Both machines stop: the drama machine would choose the music from the enemies and tension, and the music manager's own update
        // (MusicManager.Update, every frame) moves the music machine on from the drama state it still reads; left running, the two
        // changed the state back and forth (the console's "Setting music state to" lines, SurvivalHidden and IntentionalCombatHidden).
        if (!_musicHeld && DramaManager.Current != null)
        {
            DramaManager.Current.enabled = false;
            if (MusicManager.Current != null) MusicManager.Current.enabled = false;
            _musicHeld = true;
        }
        var machine = MusicManager.Machine;
        if (machine != null && machine.CurrentState != music) machine.ChangeState(music);
    }

    /// <summary>Per frame: the music machine moves on by itself from some states; it is put back to the recorded one now and then.</summary>
    internal static void KeepMusic()
    {
        if (!_musicHeld || ForgeCore.Frame.UnscaledTime < _musicCheckAt) return;
        _musicCheckAt = ForgeCore.Frame.UnscaledTime + MusicCheckSeconds;
        var machine = MusicManager.Machine;
        if (machine != null && machine.CurrentState != _music) machine.ChangeState(_music);
    }

    /// <summary>The doors and boxes set so far with their animators, for ReplayControl.Inspect: kind, place and what each animator is playing.</summary>
    internal string Inspect(string kind)
    {
        var text = new System.Text.StringBuilder();
        var things = kind == "boxes" ? (_containers ?? Array.Empty<LG_ResourceContainer_Sync>()).Select(c => (Component)c) : _kept.Values.OfType<Component>();
        foreach (var thing in things)
        {
            if (thing == null || thing.WasCollected) continue;
            text.Append(thing.GetIl2CppType().Name).Append(' ').Append(thing.name).Append(" at ").Append(thing.transform.position).Append(" active ").Append(thing.gameObject.activeInHierarchy);
            foreach (var animator in thing.GetComponentsInChildren<Animator>(true)) text.Append(" anim ").Append(animator.name).Append(animator.enabled ? " on " : " off ").Append(animator.cullingMode).Append(' ').Append(animator.isActiveAndEnabled ? animator.GetCurrentAnimatorStateInfo(0).shortNameHash : 0);
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>A pickup that is no recorded item (a copy shown in a player's hands): no key is matched to it.</summary>
    internal void NotRecorded(LG_PickupItem_Sync pickup) => _matched.Add(pickup.Pointer);

    // Switched-off pickups count too: an item in a closed container (or one the game hid after a pickup) is still the object a recorded
    // state belongs to, and leaving it out made a jump back report that the level's item could not be found.
    private List<LG_PickupItem_Sync> Pickups() => _items ??= new List<LG_PickupItem_Sync>(LevelFind.All<LG_PickupItem_Sync>());

    // The pickup kept for the recorded key, else the nearest one no other key has (then kept for this key).
    private LG_PickupItem_Sync? Item(ulong key, Vector3 near)
    {
        if (_itemByKey.TryGetValue(key, out var kept) && kept != null && !kept.WasCollected) return kept;
        // The level's own object lies where the level had it, unless the replay has moved it; the place of this state is the fallback.
        if (_home.TryGetValue(key, out var home)) near = home;
        LG_PickupItem_Sync? best = null; var distance = Reach;
        foreach (var candidate in Pickups())
        {
            if (candidate == null || candidate.WasCollected || _matched.Contains(candidate.Pointer)) continue;
            var d = Vector3.Distance(candidate.transform.position, near);
            if (d <= distance) { best = candidate; distance = d; }
        }
        if (best == null) return null;
        _itemByKey[key] = best;
        _matched.Add(best.Pointer);
        return best;
    }

    /// <summary>The pickup lying nearest <paramref name="at"/> (within <see cref="Reach"/>), for a hidden-renderers list a library recorded
    /// under a world object: the recording has only that object's place, not its item key.</summary>
    internal LG_PickupItem_Sync? ItemNear(Vector3 at)
    {
        LG_PickupItem_Sync? best = null; var distance = Reach;
        foreach (var candidate in Pickups())
        {
            if (candidate == null || candidate.WasCollected) continue;
            var d = Vector3.Distance(candidate.transform.position, at);
            if (d <= distance) { best = candidate; distance = d; }
        }
        return best;
    }

    /// <summary>Makes the replay level hold the items of the first <paramref name="count"/> recorded spawns and none of the later ones: a spawn
    /// not made yet is spawned now (the game's own <see cref="ForgeCore.Items.Spawn"/>, as a pickup at the recorded place), one made for a later moment
    /// (a jump back) is hidden. Only spawns recorded in the pickup mode are made; an item in a hand or a gear spawn (a sentry or a mine put
    /// down) is shown by its own events. A spawn the game is not ready for is tried again on the next call, not given up; returns how many
    /// were finally refused (each also writes one error line).</summary>
    public int Spawns(IReadOnlyList<ItemSpawned> spawns, int count)
    {
        var failed = 0;
        for (var i = 0; i < Math.Max(count, _spawned.Count); i++)
        {
            if (i >= _spawned.Count) { _spawned.Add(null); _tried.Add(false); _visible.Add(true); }
            if (i < count && !_tried[i])
            {
                var outcome = Spawn(spawns[i], out var made);
                _tried[i] = outcome != SpawnOutcome.Retry;
                _spawned[i] = made;
                if (outcome == SpawnOutcome.Failed) failed++;
            }
            var item = _spawned[i];
            if (item == null || item.WasCollected) continue;
            var want = i < count;
            if (_visible[i] == want) continue;
            _visible[i] = want;
            item.SetActive(want);
        }
        return failed;
    }

    // Done (made, or nothing to make), Retry (the game's item spawning was not ready: the next call tries again) or Failed (a real refusal).
    private enum SpawnOutcome : byte { Done, Retry, Failed }

    private SpawnOutcome Spawn(ItemSpawned spawn, out GameObject? made)
    {
        made = null;
        // Only an item lying in the level is a pickup here: one spawned in a hand or as a placed deployable is shown by the player's and the
        // deployable's own events. A gear checksum is not an item data block.
        if (!spawn.IsGroundPickup) return SpawnOutcome.Done;
        var block = GameData.ItemDataBlock.GetBlock(spawn.ItemId);
        if (block == null) return SpawnOutcome.Done;
        var at = new Vector3(spawn.X, spawn.Y, spawn.Z);
        // The recorded place is the item's own position, which can rest a hair under the floor the course nodes are built on, so the lookup searches a metre around it.
        if (!ForgeCore.Level.CourseNodeAt(at, 1f, out var node, out _) || node == null)
        {
            Skipped.Note("a spawned item has no course node at its recorded place", "item " + spawn.ItemId + " at " + at);
            return SpawnOutcome.Failed;
        }
        var replicator = ForgeCore.Items.Spawn(spawn.ItemId, block.inventorySlot, null, ItemMode.Pickup, at, Vec.Identity, node, out var refusal);
        // An item with no pickup form (it was held, not lying) is simply not a ground item.
        if (refusal == ItemSpawnRefusal.ModeMissing) return SpawnOutcome.Done;
        // The spawn was refused before the game made anything (no spawn manager or replication manager yet): tried again, not given up, or
        // the item's recorded state would have no object to be set on for the rest of the replay.
        if (refusal is ItemSpawnRefusal.NotReady or ItemSpawnRefusal.ResultUnavailable) return SpawnOutcome.Retry;
        var item = replicator?.Item;
        if (item == null)
        {
            Skipped.Note("a spawned item could not be spawned", "item " + spawn.ItemId + ": " + refusal);
            return SpawnOutcome.Failed;
        }
        made = item.gameObject;
        var pickup = item.GetComponentInChildren<LG_PickupItem_Sync>();
        if (pickup != null) Pickups().Add(pickup);
        return SpawnOutcome.Done;
    }

    // Recorded (kind, key) -> the object of the replay level it was matched to, for every kind found by place: the search runs once for a key and
    // a later event of it goes straight to the object (the place search reads the transform of every candidate, once per event).
    private readonly Dictionary<(ushort Kind, ulong Key), Component> _kept = new();

    private Component? Kept(ushort kind, ulong key) => _kept.TryGetValue((kind, key), out var kept) && kept != null && !kept.WasCollected ? kept : null;

    private T? Found<T>(ushort kind, ulong key, T[] all, Vector3 near) where T : Component
    {
        if (Kept(kind, key) is T kept) return kept;
        var best = Nearest(all, near, out _);
        if (best != null) _kept[(kind, key)] = best;
        return best;
    }

    private static T? Nearest<T>(T[] all, Vector3 near, out float distance) where T : Component
    {
        T? best = null; distance = Reach;
        foreach (var candidate in all)
        {
            if (candidate == null) continue;
            var d = Vector3.Distance(candidate.transform.position, near);
            if (d <= distance) { best = candidate; distance = d; }
        }
        return best;
    }
}
