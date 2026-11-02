// Only the native boundary is doubled; BeaconItem, ThrowWatch, OrdnanceController, OrdnanceApi and ItemPayload are shipped sources.
#pragma warning disable CS0067, CS0649
using ForgeOrdnance;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 up => new(0, 1, 0);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(Vector3 value, float scale) => new(value.x * scale, value.y * scale, value.z * scale);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized => this * (1f / MathF.Sqrt(sqrMagnitude));
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity => new() { w = 1 };
        public Vector3 eulerAngles;
        public static Quaternion Euler(float x, float y, float z) => new() { eulerAngles = new Vector3(x, y, z) };
        public static Vector3 operator *(Quaternion _, Vector3 value) => value;
    }
    public sealed class Transform
    {
        public readonly Dictionary<string, Transform> Children = new();
        public Transform? Find(string path) => Children.TryGetValue(path, out var child) ? child : null;
    }
    public sealed class GameObject { public readonly Transform transform = new(); }
    internal static class Time { public static float time; public static int frameCount; }
    internal enum KeyCode { Mouse2, Mouse3, Mouse4, Mouse5, Mouse6, W, S, A, D, UpArrow, DownArrow, LeftArrow, RightArrow }
    internal static class Input
    {
        internal static readonly HashSet<KeyCode> Down = new();
        public static bool GetKeyDown(KeyCode key) => Down.Contains(key);
        public static bool GetKey(KeyCode key) => Down.Contains(key);
        public static bool GetMouseButtonDown(int button) => false;
    }
}
namespace AIGraph
{
    public sealed class AIG_CourseNode { internal bool WasCollected; internal int LayerType = 1; }
    internal struct pCourseNode { internal void Set(AIG_CourseNode _) { } }
}
namespace LevelGeneration
{
    internal enum ePickupItemInteractionType { Pickup, Place, UpdateCustomData }
    internal enum ePickupItemStatus { PlacedInLevel, PickedUp }
    internal struct pPickupPlacement { public Vector3 position; }
    internal struct pPickupItemState { public ePickupItemStatus status; public pPickupPlacement placement; public pItemData_Custom custom; }
    /// <summary>The pickup's replicated state: a spawned pickup starts empty, as the game's does at run time.</summary>
    internal sealed class LG_PickupItem_Sync
    {
        public void OnStateChange(pPickupItemState oldState, pPickupItemState newState, bool isRecall) { }
        public bool WasCollected;
        internal pPickupItemState State;
        internal bool IgnorePlace;
        internal readonly List<(ePickupItemInteractionType Type, float Ammo, Vector3 Position, bool Force)> Interactions = new();
        public void AttemptPickupInteraction(ePickupItemInteractionType type, SNetwork.SNet_Player player, pItemData_Custom custom, Vector3 position,
            Quaternion rotation, AIGraph.AIG_CourseNode node, bool droppedOnFloor, bool forceUpdate)
        {
            Interactions.Add((type, custom.ammo, position, forceUpdate));
            if (type == ePickupItemInteractionType.Place && !IgnorePlace) State = new pPickupItemState { status = ePickupItemStatus.PlacedInLevel, placement = new pPickupPlacement { position = position }, custom = custom };
        }
        public pPickupItemState GetCurrentState() => State;
        public T? TryCast<T>() where T : class => this as T;
    }
}
internal enum InventorySlot { None, Standard, Special, Tool, Melee, ResourcePack, Consumable, HackingTool, InLevelCarry, ConsumableHeavy, Pickup }
internal enum ItemMode { FirstPerson, ThirdPerson, Pickup, Instance }
internal enum AmmoType { CurrentConsumable }
internal enum InputAction { SelectStandard, SelectSpecial, SelectTool, SelectMelee, SelectResourcePack, SelectConsumable, SelectHackingTool, MoveHorizontal, MoveVertical, Fire, Aim, ScrollItems }
internal enum eFocusState { FPS, Menu }
internal static class CheckpointManager { public static bool IsReloadingCheckpoint; }
internal static class FocusStateManager { internal static eFocusState CurrentState = eFocusState.FPS; }
internal static class InputMapper
{
    public static Func<InputAction, eFocusState, bool> GetButtonDown = (action, _) => ForgeCore.InputLease.Pressed.Contains(action);
}
internal interface ISyncedItem { T? TryCast<T>() where T : class; }
internal class Item : ISyncedItem
{
    private static long _next;
    public IntPtr Pointer = new(++_next);
    public bool WasCollected;
    public GameData.ItemDataBlock? ItemDataBlock;
    public T? TryCast<T>() where T : class => this as T;
}
internal sealed class ItemInLevel : Item { public AIGraph.AIG_CourseNode? CourseNode; public LevelGeneration.LG_PickupItem_Sync Sync = new(); public LevelGeneration.LG_PickupItem_Sync? GetSyncComponent() => Sync; }
internal sealed class ItemReplicator : SNetwork.IReplicator
{
    public Item? Item;
    public ushort Key = 1;
    public bool WasCollected;
    public T? TryCast<T>() where T : class => this as T;
}
internal struct pItemData_Custom { public float ammo; }
internal struct pItemData
{
    public uint itemID_gearCRC;
    public InventorySlot slot;
    public int originLayer;
    public AIGraph.pCourseNode originCourseNode;
    public pItemData_Custom custom;
}
internal struct pItemSpawnData { public pItemData itemData; public SpawnOwner owner; public Vector3 Position; }
internal struct SpawnOwner
{
    public Player.PlayerAgent? Player;
    public bool TryGetPlayer(out Player.PlayerAgent? player) { player = Player; return player != null; }
}
internal sealed class ItemReplicationManager
{
    internal static ItemReplicationManager? Current { get; set; } = new();
    internal ItemReplicationManager? m_SNetReplicationManager_Item => this;
    internal ItemReplicator? m_internalSpawnCallbackReturnReplicator;
    internal bool WasCollected;
    internal static readonly Queue<ItemReplicator?> SpawnResults = new();
    internal static int SpawnCalls, ThrowOnCall;

    internal static void ResetSpawns()
    {
        Current = new ItemReplicationManager();
        SpawnResults.Clear();
        SpawnCalls = ThrowOnCall = 0;
    }

    public static void SpawnItem(pItemData data, object? callback, ItemMode mode, Vector3 position, Quaternion rotation,
        AIGraph.AIG_CourseNode node, Player.PlayerAgent? owner)
    {
        SpawnCalls++;
        if (ThrowOnCall == SpawnCalls) throw new InvalidOperationException("simulated spawn failure");
        Current!.m_internalSpawnCallbackReturnReplicator = SpawnResults.Count == 0 ? null : SpawnResults.Dequeue();
    }

    public sealed class delItemCallback
    {
        private readonly Action<ISyncedItem, Player.PlayerAgent> _action;
        private delItemCallback(Action<ISyncedItem, Player.PlayerAgent> action) => _action = action;
        public static implicit operator delItemCallback(Action<ISyncedItem, Player.PlayerAgent> action) => new(action);
        public void Invoke(ISyncedItem item, Player.PlayerAgent owner) => _action(item, owner);
    }
    public void OnItemSpawn(pItemSpawnData spawnData, ItemReplicator replicator) { }
    public void ThrowItem() { }
}
internal static class ItemSpawnManager
{
    public static Dictionary<uint, object>?[]? m_loadedPrefabsPerItemMode;
    internal static bool ReadyToSpawnItems = true;
    internal static readonly HashSet<uint> PickupPrefabs = new();
    internal static bool ItemHasMode(uint id, ItemMode mode) => mode == ItemMode.Pickup && PickupPrefabs.Contains(id);
    public static void Setup() { }
}
namespace SNetwork
{
    internal interface IReplicator { }
    internal sealed class SNet_Player { }
    internal static class SNet
    {
        internal static bool IsMaster = true;
        internal static SNet_Player? LocalPlayer = new();
        internal sealed class CaptureState { public bool IsRecalling; }
        internal static readonly CaptureState Capture = new();
    }
    internal static class SNetStructs
    {
        internal struct pReplicator
        {
            public ushort keyPlusOne;
            public void SetID(IReplicator replicator) => keyPlusOne = ((ItemReplicator)replicator).Key;
        }
    }
}
namespace GameData
{
    internal sealed class ItemDataBlock
    {
        private static long _next;
        internal static readonly Dictionary<uint, ItemDataBlock> Blocks = new();
        public IntPtr Pointer = new(++_next);
        public string name = "";
        public uint persistentID;
        public bool internalEnabled, WasCollected;
        public InventorySlot inventorySlot;
        public int ConsumableAmmoMin, ConsumableAmmoMax;
        public static ItemDataBlock? GetBlock(uint id) => Blocks.GetValueOrDefault(id);
        public new ItemDataBlock MemberwiseClone() => new() { name = name, persistentID = persistentID };
        public T Cast<T>() => (T)(object)this;
    }
    internal static class GameDataBlockBase<T>
    {
        public static void AddBlock(ItemDataBlock block) => ItemDataBlock.Blocks[block.persistentID] = block;
        public static ItemDataBlock? GetBlock(uint id) => ItemDataBlock.GetBlock(id);
        public static ItemDataBlock? GetBlock(string name) => ItemDataBlock.Blocks.Values.FirstOrDefault(block => block.name == name);
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] internal sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    [AttributeUsage(AttributeTargets.Method)] internal sealed class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] internal sealed class HarmonyPostfix : Attribute { }
}
namespace GTFO.API
{
    internal static class EventAPI
    {
        public static event Action? OnCheckpointReached, OnCheckpointReloaded;
        internal static void ReachCheckpoint() => OnCheckpointReached?.Invoke();
        internal static void ReloadCheckpoint() => OnCheckpointReloaded?.Invoke();
    }
    internal static class LevelAPI { public static event Action? OnLevelCleanup; }
}
namespace Player
{
    public sealed class PlayerAgent
    {
        internal bool Alive = true, WasCollected;
        internal ulong Lookup = 1;
        internal readonly Inventory Inventory = new();
        internal readonly Sync Sync = new();
        internal readonly Holder FPItemHolder = new();
    }
    internal sealed class Holder { public bool ItemIsBusy; public string m_currentState = "Up"; }
    internal sealed class Inventory { public InventorySlot WieldedSlot = InventorySlot.Special; public Item? WieldedItem; }
    internal sealed class Sync
    {
        internal int Wields;
        public void WantsToWieldSlot(InventorySlot slot) { Wields++; PlayerManager.Agent!.Inventory.WieldedSlot = slot; }
    }
    internal static class PlayerManager { internal static PlayerAgent? Agent = new(); public static PlayerAgent? GetLocalPlayerAgent() => Agent; }
    internal sealed class BackpackItem { public uint ItemID; }
    internal sealed class AmmoPool { public float AmmoInPack; public int BulletsInPack => (int)AmmoInPack; public float CostOfBullet => 1; }
    internal sealed class AmmoStorage
    {
        public readonly AmmoPool ConsumableAmmo = new();
        public void SetAmmo(AmmoType type, float ammo) => ConsumableAmmo.AmmoInPack = ammo;
        public void UpdateSlotAmmoUI(InventorySlot slot) { }
    }
    internal sealed class Backpack
    {
        public BackpackItem? Item;
        public readonly AmmoStorage AmmoStorage = new();
        public bool TryGetBackpackItem(InventorySlot slot, out BackpackItem? item) { item = Item; return item != null; }
    }
    internal static class PlayerBackpackManager
    {
        public static Backpack? LocalBackpack = new();
        internal static int Restores, Clears;
        public static void AddDebugItem(uint id, float ammo)
        {
            if (id != BeaconItem.BlockId) Restores++;
            LocalBackpack!.Item = new() { ItemID = id };
            LocalBackpack.AmmoStorage.ConsumableAmmo.AmmoInPack = ammo;
            PlayerManager.Agent!.Inventory.WieldedSlot = InventorySlot.Consumable;
            PlayerManager.Agent.Inventory.WieldedItem = new Gear.ThrowingWeapon { ItemDataBlock = GameData.ItemDataBlock.GetBlock(id), Owner = PlayerManager.Agent };
        }
        public static void ClearLocalSlot(InventorySlot slot, bool a, bool b) { Clears++; LocalBackpack!.Item = null; }
    }
}
namespace Gear
{
    internal sealed class ThrowingWeapon : Item
    {
        public Player.PlayerAgent? Owner;
        public string m_throwSoundEvent = "PLAY_THROW";
        public bool m_inThrowCharge, m_hasThrown, m_waitingForReady, IsReloading;
        public bool IsEnabled = true, FireButtonPressed = true, m_ready = true, CanWield = true;
    }
}
namespace ForgeUI
{
    /// <summary>ForgeUI's key action: down while its key is in <see cref="UnityEngine.Input.Down"/> (a test frame presses and lets go).</summary>
    public sealed class KeyAction
    {
        private readonly KeyCode _key;
        internal KeyAction(KeyCode key) => _key = key;
        public bool Pressed => Input.Down.Contains(_key);
        public bool Held => Input.Down.Contains(_key);
        public string Chord => _key.ToString();
    }
    internal static class UIKitKeys
    {
        internal static bool GameKeyDown(InputAction action, bool negative = false) => false;
    }
}
namespace ForgeOrdnance
{
    internal static class OrdnanceKeys
    {
        internal static readonly ForgeUI.KeyAction Summon = new(KeyCode.Mouse3), Up = new(KeyCode.UpArrow), Down = new(KeyCode.DownArrow),
            Left = new(KeyCode.LeftArrow), Right = new(KeyCode.RightArrow);
    }
}
namespace ForgeVisual
{
    public sealed class ModelHandle { public UnityEngine.GameObject Prefab { get; } = new(); }
    public readonly struct ShakeSpec
    {
        public readonly float Amplitude, Range;
        public ShakeSpec(float amplitude, float range, float seconds = 0.7f, float frequency = 18f, float inner = 0f) { Amplitude = amplitude; Range = range; }
    }
    /// <summary>The library's camera shake; the tests record what was asked.</summary>
    public static class CameraShake
    {
        internal static readonly System.Collections.Generic.List<(UnityEngine.Vector3 At, ShakeSpec Spec)> Shakes = new();
        public static float Shake(UnityEngine.Vector3 at, in ShakeSpec spec) { Shakes.Add((at, spec)); return spec.Amplitude; }
    }
}
namespace ForgeOrdnance
{
    internal static class Plugin
    {
        internal const string Id = "NAinfini.ForgeOrdnance";
        internal static readonly List<string> Errors = new();
        internal static OrdnanceSession Session { get; } = new();
        internal static void Error(string text) => Errors.Add(text);
        internal static void Report(string text) => Errors.Add(text);
        internal static string Pos(Vector3 p) => "position";
    }
    /// <summary>The shipped English texts (<c>lang/en.json</c>), so notices read as the player sees them.</summary>
    internal static class OrdnanceText
    {
        private static Dictionary<string, string>? _english;
        private static Dictionary<string, string> English([System.Runtime.CompilerServices.CallerFilePath] string here = "") => _english ??=
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "../../lang/en.json")))!;
        internal static string T(string key) => English().TryGetValue(key, out var text) ? text : key;
        internal static string T(string key, params object?[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, T(key), args);
        internal static string Reason(Rejection reason) => T("reject." + reason);
    }
    internal static class OrdnanceContent { internal static DefinitionSet Set { get; set; } = DefinitionSet.Empty; internal static string Name(OrdnanceDefinition definition) => definition.Name; }

    internal sealed class OrdnanceSession
    {
        internal event Action<string>? Notice;
        internal event Action<RejectMessage>? ThrowRejected;
        internal event Action<AcceptedMessage, OrdnanceDefinition>? BeaconStarted;
        internal event Action<ArrivedMessage, OrdnanceDefinition>? BeaconArrived;
        internal event Action<ulong, uint>? BeaconFailed;
        internal event Action? PresentationReset;
        internal static ulong Local => 1;
        internal bool InLevel = true, Enabled = true;
        internal uint ThrowFloor;
        internal bool Place(string ordnanceId, Vector3 position, float yaw, OrdnanceArrivalMode arrival, int dimension, out string reason) { reason = ""; return true; }
        internal string? DisabledReason => null;
        internal bool CallMenuOff;
        internal readonly SlotBook Book = new();
        internal IReadOnlyList<OrdnanceDefinition> Loadout = Array.Empty<OrdnanceDefinition>();
        internal IReadOnlyList<OrdnanceDefinition> LocalLoadout() => Loadout;
        internal void Poll() { }
        internal void Tick() { }
        internal void ReportLanding(LandingMessage message) { }
        internal bool IsPeerCompatible(ulong player) => InLevel && Enabled && player == Local;
        internal void DeliveryFailed(ulong thrower, uint throwId) => BeaconFailed?.Invoke(thrower, throwId);
    }
    internal static class OrdnanceNetwork
    {
        internal static readonly List<(string Name, object Message)> Broadcasts = new();
        internal static void Pump() { }
        internal static void Broadcast<T>(string name, T message) where T : unmanaged => Broadcasts.Add((name, message));
    }
    internal static class GearPayload { internal static void Tick(float now) { } }
    internal static class DeliveryTiming
    {
        internal static Quaternion PodRotation(float yaw) => Quaternion.Euler(0f, yaw + 90f, 0f);
    }
    internal static class OrdnanceAssets { internal static object? PodAppearance; }
    /// <summary>The bay pose comes from the art bundle and the pickup's prefab bounds; offline, pickup i stands i metres along x.</summary>
    internal static class PodPacking
    {
        internal static (Vector3 Position, Quaternion Rotation) Pose(uint itemId, Vector3 landing, Quaternion podRotation, int index, bool inPod) => (landing + new Vector3(index, 0, 0), podRotation);
    }
    internal sealed class ThrownBeacon
    {
        internal static int Created;
        internal ThrownBeacon(uint id, uint key, Vec3 origin, Item item, ushort replicator = 0) { Created++; ThrowId = id; Key = key; Origin = origin; Replicator = replicator; }
        internal uint ThrowId, Key;
        internal ushort Replicator;
        internal Vec3 Origin;
        internal Vector3 Position => default;
        internal bool Reported, Gone, Fell;
        internal float Age => 0;
        internal bool Settled() => false;
    }
    internal sealed class OrdnanceBeacon
    {
        internal OrdnanceBeacon(ulong thrower, uint id, OrdnanceDefinition definition, Vector3 landing, float delay) { Thrower = thrower; ThrowId = id; }
        internal ulong Thrower;
        internal uint ThrowId;
        internal float ArriveAt;
        internal void Destroy() { }
        internal void Tick() { }
    }
    internal static class HandPulse
    {
        internal static int Started;
        internal static void Start(uint beamRgb) => Started++;
    }
    internal static class Deliveries
    {
        internal static bool SupplyAtPosition;
        internal static bool IsSupplyPosition(Vector3 position) => SupplyAtPosition;
        internal static readonly List<Action> Scheduled = new();
        internal static readonly List<(ulong Thrower, uint ThrowId, ItemInLevel?[] Items)> Watched = new();
        internal static readonly List<(ulong Thrower, uint ThrowId)> Failures = new();
        internal static object? StartPod(OrdnancePresentation presentation) => null;
        internal static readonly List<float> ScheduledDelays = new();
        internal static readonly List<(ItemInLevel Item, ulong Thrower, uint ThrowId)> Held = new();
        internal static void HostAfter(float seconds, Action work) { Scheduled.Add(work); ScheduledDelays.Add(seconds); }
        internal static void Hold(ItemInLevel item, ulong thrower, uint throwId) => Held.Add((item, thrower, throwId));
        internal static void Watch(ulong thrower, uint throwId, ItemInLevel?[] items) => Watched.Add((thrower, throwId, items));
        internal static void ReportFailure(ulong thrower, uint throwId) => Failures.Add((thrower, throwId));
        internal static void Clear() { Scheduled.Clear(); ScheduledDelays.Clear(); Held.Clear(); Watched.Clear(); Failures.Clear(); }
    }
    internal static class OrdnanceClaims { internal static void Clear() { } }
    /// <summary>The lobby's picks as the API reads them.</summary>
    internal static class LobbySync
    {
        internal static void PickedParameters(string type, List<object> into) => into.Clear();
    }
    internal static class OrdnanceCues
    {
        internal static readonly List<(string Cue, Vector3 Position)> Played = new();
        internal static readonly List<OrdnanceSound> Sounds = new();
        internal static void Local(OrdnanceSound sound) => Sounds.Add(sound);
        internal static void At(string cue, Vector3 position) => Played.Add((cue, position));
    }
}
namespace ForgeCore
{
    internal enum DataBlockResult { Free, Added, AlreadyAdded, Conflict, ReadbackFailed }
    /// <summary>ForgeCore's DataBlocks as the ball block calls it: a clone under a new name and ID, and an add that refuses any ID already held.</summary>
    internal static class DataBlocks
    {
        internal static GameData.ItemDataBlock Clone(GameData.ItemDataBlock donor, string name, uint id)
        {
            var copy = donor.MemberwiseClone();
            copy.name = name; copy.persistentID = id; copy.internalEnabled = true;
            return copy;
        }
        internal static DataBlockResult Add(string modId, GameData.ItemDataBlock block)
        {
            if (GameData.ItemDataBlock.GetBlock(block.persistentID) != null) return DataBlockResult.Conflict;
            GameData.GameDataBlockBase<GameData.ItemDataBlock>.AddBlock(block);
            return DataBlockResult.Added;
        }
    }
    internal enum ItemSpawnRefusal { None, NotReady, ModeMissing, ResultUnavailable }
    /// <summary>ForgeCore's item spawning as the pickup payload calls it: the same readiness and mode refusals, and the replicator the
    /// game's replication manager leaves behind (the doubles queue those results).</summary>
    internal static class Items
    {
        internal static ItemReplicator? Spawn(uint itemId, InventorySlot slot, pItemData_Custom? custom, ItemMode mode, Vector3 position,
            Quaternion rotation, AIGraph.AIG_CourseNode node, out ItemSpawnRefusal refusal)
        {
            var replication = ItemReplicationManager.Current?.m_SNetReplicationManager_Item;
            if (!ItemSpawnManager.ReadyToSpawnItems || replication == null || replication.WasCollected) { refusal = ItemSpawnRefusal.NotReady; return null; }
            if (!ItemSpawnManager.ItemHasMode(itemId, mode)) { refusal = ItemSpawnRefusal.ModeMissing; return null; }
            replication.m_internalSpawnCallbackReturnReplicator = null;
            ItemReplicationManager.SpawnItem(new pItemData(), null, mode, position, rotation, node, null);
            var spawned = replication.m_internalSpawnCallbackReturnReplicator;
            refusal = spawned == null || spawned.WasCollected ? ItemSpawnRefusal.ResultUnavailable : ItemSpawnRefusal.None;
            return refusal == ItemSpawnRefusal.None ? spawned : null;
        }
    }
    /// <summary>ForgeCore's input lease as the controller calls it: whether one is held, and the game's own "pressed" answer.</summary>
    internal sealed class InputLease : System.IDisposable
    {
        internal static readonly System.Collections.Generic.HashSet<InputAction> Pressed = new();
        private bool _held = true;
        public static InputLease Take(string modId) => new();
        public bool IsHeld => _held;
        public void Block(params InputAction[] actions) { }
        public void Dispose() => _held = false;
        public static bool Down(InputAction action) => Pressed.Contains(action);
        public static float Axis(InputAction action) => 0;
    }
    /// <summary>ForgeCore's checkpoint events as the controller subscribes to them; the tests raise them through the game's
    /// event API, which is where ForgeCore raises them from.</summary>
    internal static class GameEvents
    {
        static GameEvents() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Checkpoint).TypeHandle);
        public static event System.Action? CheckpointReached { add => GTFO.API.EventAPI.OnCheckpointReached += value; remove => GTFO.API.EventAPI.OnCheckpointReached -= value; }
        public static event System.Action? CheckpointReloaded { add => GTFO.API.EventAPI.OnCheckpointReloaded += value; remove => GTFO.API.EventAPI.OnCheckpointReloaded -= value; }
    }
    /// <summary>ForgeCore's checkpoint settling with its own rule: a frame after the reload event, and the game no longer
    /// reloading or recalling.</summary>
    internal static class Checkpoint
    {
        private static int _reloadedFrame = -1;
        static Checkpoint() => GTFO.API.EventAPI.OnCheckpointReloaded += () => _reloadedFrame = UnityEngine.Time.frameCount;
        internal static bool Settled => UnityEngine.Time.frameCount > _reloadedFrame && !(CheckpointManager.IsReloadingCheckpoint || SNetwork.SNet.Capture?.IsRecalling == true);
    }
    /// <summary>ForgeCore's level queries as touchdown noise asks them; the tests choose the course node it answers with.</summary>
    internal static class Level
    {
        internal static AIGraph.AIG_CourseNode? Node;
        internal static bool CourseNodeAt(UnityEngine.Vector3 position, float sample, out AIGraph.AIG_CourseNode? node, out int dimension, int preferredDimension = -1)
        {
            node = Node; dimension = 0; return Node != null;
        }
    }
    /// <summary>ForgeCore's world noise: the tests record what the game would have been asked.</summary>
    internal static class Combat
    {
        internal static readonly System.Collections.Generic.List<(UnityEngine.Vector3 At, float Radius, AIGraph.AIG_CourseNode Node)> Noises = new();
        internal static bool Noise(UnityEngine.Vector3 position, float radius, AIGraph.AIG_CourseNode? node)
        {
            if (radius <= 0f || node == null) return false;
            Noises.Add((position, radius, node));
            return true;
        }
    }
}