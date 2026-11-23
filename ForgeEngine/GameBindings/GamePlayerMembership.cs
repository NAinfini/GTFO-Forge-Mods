using System;
using System.Reflection;
using Player;

namespace ForgeEngine.GameBindings;

/// <summary>The three native entries every player's arrival and removal passes through. Several Forge packages
/// read them, so ForgeCore patches each once (<see cref="ForgeCore.GameHooks"/>), Runtime binds them to its world and every package is a
/// read-only subscriber: none can occupy the entry first, and which packages are installed or load first changes nothing.</summary>
public enum GamePlayerMembershipEntry
{
    /// <summary>`PlayerReplicationManager.OnSpawn(pPlayerSpawnData, PlayerReplicator)`.</summary>
    ReplicationSpawn,
    /// <summary>`PlayerReplicationManager.OnDeSpawn(PlayerReplicator)`.</summary>
    ReplicationDespawn,
    /// <summary>`PlayerManager.OnPlayerSpawned(pPlayerSpawnData, PlayerReplicator)`, called inside ReplicationSpawn.</summary>
    PlayerSpawned
}

/// <summary>Before the native body (Entered) or after it returned (Returned).</summary>
public enum GameEntryPhase { Entered, Returned }

/// <summary>One call of a membership entry with its native arguments and the host epoch it started in.</summary>
public readonly struct GamePlayerMembershipCall
{
    public long WorldEpoch { get; }
    public GamePlayerMembershipEntry Entry { get; }
    public GameEntryPhase Phase { get; }
    /// <summary>The spawn record the entry received. Only the spawn entries carry one; see <see cref="HasSpawnData"/>.</summary>
    public pPlayerSpawnData SpawnData { get; }
    public bool HasSpawnData { get; }
    internal GamePlayerMembershipCall(long epoch, GamePlayerMembershipEntry entry, GameEntryPhase phase,
        pPlayerSpawnData spawnData, bool hasSpawnData)
    { WorldEpoch = epoch; Entry = entry; Phase = phase; SpawnData = spawnData; HasSpawnData = hasSpawnData; }
}

/// <summary>One subscription per entry to ForgeCore's bracket, with independently leased Entered and Returned observations.
/// The bracket's prefix and postfix run first among Harmony patches, so subscribers see the player list before any other prefix
/// and exactly as the native body left it.</summary>
public static class GamePlayerMembership
{
    internal const string Writer = "ForgeEngine.PlayerMembership";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    // Each target is the type's own declared member with the reviewed shape (the replicator argument is not read).
    private static readonly MethodInfo SpawnTarget = Method(typeof(PlayerReplicationManager), "OnSpawn", 2, spawnData: true);
    private static readonly MethodInfo DespawnTarget = Method(typeof(PlayerReplicationManager), "OnDeSpawn", 1, spawnData: false);
    private static readonly MethodInfo SpawnedTarget = Method(typeof(PlayerManager), "OnPlayerSpawned", 2, spawnData: true);

    private static readonly SharedBracket SpawnCore = new(Writer, SpawnTarget,
        static () => Spawn(ForgeCore.GameHooks.PlayerReplicationSpawned, GamePlayerMembershipEntry.ReplicationSpawn));
    private static readonly SharedBracket DespawnCore = new(Writer, DespawnTarget, static () => ForgeCore.GameHooks.PlayerReplicationDespawned.Around(
        static _ => Entered(GamePlayerMembershipEntry.ReplicationDespawn, default!, false),
        static (_, epoch, _) => Publish(GamePlayerMembershipEntry.ReplicationDespawn, GameEntryPhase.Returned, epoch, default!, false)));
    private static readonly SharedBracket SpawnedCore = new(Writer, SpawnedTarget,
        static () => Spawn(ForgeCore.GameHooks.PlayerManagerSpawned, GamePlayerMembershipEntry.PlayerSpawned));
    private static readonly NativeHookChannel<GamePlayerMembershipCall>[] Channels =
    {
        new(SpawnCore, "entered"), new(SpawnCore, "returned"), new(DespawnCore, "entered"), new(DespawnCore, "returned"),
        new(SpawnedCore, "entered"), new(SpawnedCore, "returned")
    };

    public static bool IsTarget(MethodBase target)
        => SpawnTarget.Equals(target) || DespawnTarget.Equals(target) || SpawnedTarget.Equals(target);

    public static NativeHookLease Subscribe(string consumer, long worldEpoch, GamePlayerMembershipEntry entry,
        GameEntryPhase phase, Action<GamePlayerMembershipCall> callback)
        => Channel(entry, phase).Subscribe(consumer, worldEpoch, callback);

    private static NativeHookChannel<GamePlayerMembershipCall> Channel(GamePlayerMembershipEntry entry, GameEntryPhase phase)
    {
        if (!Enum.IsDefined(entry)) throw new ArgumentOutOfRangeException(nameof(entry));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        return Channels[(int)entry * 2 + (int)phase];
    }

    private static MethodInfo Method(Type type, string name, int arguments, bool spawnData)
    {
        var found = Array.FindAll(type.GetMethods(Instance), method => method.Name == name && method.ReturnType == typeof(void)
            && method.GetParameters() is var parameters && parameters.Length == arguments
            && (!spawnData || parameters[0].ParameterType == typeof(pPlayerSpawnData)));
        return found.Length == 1 ? found[0] : throw new MissingMethodException(type.FullName, name);
    }

    private static void Publish(GamePlayerMembershipEntry entry, GameEntryPhase phase, long epoch,
        pPlayerSpawnData spawnData, bool hasSpawnData)
        => Channel(entry, phase).Publish(new(epoch, entry, phase, spawnData, hasSpawnData), epoch);

    // The call's state is the epoch it started in: Returned belongs to that world even if the body moved the host on.
    private static long Entered(GamePlayerMembershipEntry entry, pPlayerSpawnData spawnData, bool hasSpawnData)
    {
        var epoch = NativeHookRegistration.WorldEpoch;
        Publish(entry, GameEntryPhase.Entered, epoch, spawnData, hasSpawnData);
        return epoch;
    }
    private static IDisposable Spawn(ForgeCore.Bracket<pPlayerSpawnData> bracket, GamePlayerMembershipEntry entry) => bracket.Around(
        data => Entered(entry, data, true),
        (data, epoch, _) => Publish(entry, GameEntryPhase.Returned, epoch, data, true));
}
