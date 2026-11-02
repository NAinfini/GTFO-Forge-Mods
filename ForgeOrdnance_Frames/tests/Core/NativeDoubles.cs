// Offline boundary doubles. FrameWorld and FrameNetwork themselves are compiled from production source.
using ForgeOrdnanceFrames;
using UnityEngine;

namespace UnityEngine
{
    public class Object
    {
        public static void Destroy(Object value) { }
        public T? TryCast<T>() where T : class => this as T;
    }
    public sealed class SkinnedMeshRenderer : Object { }
    public sealed class MeshRenderer : Object { }
    public sealed class Animation : Object { }
    public sealed class Sprite : Object { }
    public sealed class Transform : Object
    {
        public string name = "";
        public Transform[] Children = Array.Empty<Transform>();
        public int childCount => Children.Length;
        public Transform GetChild(int index) => Children[index];
    }
    public sealed class GameObject : Object
    {
        public bool Skinned = true;
        public int Meshes;
        public bool Animated;
        public Transform transform = new();
        public T? GetComponentInChildren<T>(bool inactive) where T : class => typeof(T) == typeof(Animation) && Animated ? (T)(object)new Animation() : null;
        public T[] GetComponentsInChildren<T>(bool inactive)
            => typeof(T) == typeof(MeshRenderer) ? Enumerable.Range(0, Meshes).Select(_ => (T)(object)new MeshRenderer()).ToArray()
                : Skinned ? new[] { (T)(object)new SkinnedMeshRenderer() } : Array.Empty<T>();
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 forward => new(0, 0, 1);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
    }
    public struct Quaternion
    {
        public Vector3 eulerAngles;
        public static Vector3 operator *(Quaternion q, Vector3 v) => v;
        public static Quaternion LookRotation(Vector3 v) => default;
    }
    public static class Random { public static int Range(int min, int max) => min; }
    public static class Time { public static float time, deltaTime; public static int frameCount; }
}
namespace SNetwork
{
    public sealed class SNet_Player { public ulong Lookup; }
    public static class SNet
    {
        public static SNet_Player LocalPlayer = new() { Lookup = 1 };
        public static SNet_Player? Master = LocalPlayer;
        public static bool IsMaster => Master?.Lookup == LocalPlayer.Lookup;
        public static bool TryGetPlayer(ulong id, out SNet_Player player) { player = new() { Lookup = id }; return id != 0; }
    }
    public enum SNet_ChannelType { GameOrderCritical, GameNonCritical }
}
namespace Agents
{
    public class Agent
    {
        private static int _nextPointer;
        public Vector3 Position;
        public bool WasCollected;
        public ushort GlobalID = (ushort)(++_nextGlobalId);
        private static int _nextGlobalId;
        public IntPtr Pointer = (IntPtr)(++_nextPointer);
        public T? TryCast<T>() where T : class => this as T;
    }
}
namespace Enemies { public sealed class EnemyAgent : Agents.Agent { } }
namespace Player
{
    public sealed class PlayerAgent : Agents.Agent
    {
        public SNetwork.SNet_Player? Owner;
        public bool Alive = true;
        public Quaternion Rotation;
        public PlayerLocomotion? Locomotion;
        public CharacterController? PlayerCharacterController;
        public AIGraph.AIG_CourseNode? CourseNode;
        public eDimensionIndex DimensionIndex;
    }
    public sealed class PlayerLocomotion
    {
        public enum PLOC_State { Normal, ClimbLadder, Downed, Stunned, GrabbedByTrap, GrabbedByTank, GrabbedByPouncer, StandStill }
        public PLOC_State m_currentStateEnum;
    }
    public sealed class CharacterController { public bool IsGrounded = true; }
    public static class PlayerManager { public static List<PlayerAgent>? PlayerAgentsInLevel = new(); }
}
public enum eDimensionIndex { Reality, Dimension1, MAX_COUNT }
public enum NM_NoiseType { InstaDetect }
namespace GameData { public sealed class EnemyDetectionDataBlock { public float movementDetectionDistance; public static EnemyDetectionDataBlock? Default; public static EnemyDetectionDataBlock? GetBlock(string name) => name == "DefaultDetection" ? Default : null; } }
public struct NM_NoiseData
{
    public object? noiseMaker;
    public Vector3 position;
    public float radiusMin, radiusMax, yScale;
    public AIGraph.AIG_CourseNode? node;
    public NM_NoiseType type;
    public bool includeToNeightbourAreas, raycastFirstNode;
}
public static class NoiseManager
{
    public static readonly List<NM_NoiseData> Emitted = new();
    public static void MakeNoise(NM_NoiseData noise) => Emitted.Add(noise);
}
namespace AIGraph
{
    public sealed class AIG_CourseNode
    {
        public bool WasCollected;
        public static AIG_CourseNode? Available = new();
        public static bool TryGetCourseNode(eDimensionIndex dimension, Vector3 at, float sample, out AIG_CourseNode? node)
        { node = dimension == eDimensionIndex.Dimension1 ? Available : null; return node != null; }
    }
}
namespace ForgeVisual
{
    public sealed class BundleHandle
    {
        public static readonly Dictionary<string, GameObject> Prefabs = new()
        {
            ["frame-m15"] = Suit(), ["unskinned"] = new GameObject { Skinned = false }, ["no-chest"] = Suit("chest"),
            ["pod"] = new GameObject { Skinned = false, Meshes = 1, transform = new Transform { name = "pod", Children = new[] { new Transform { name = "model", Children = new[] { new Transform { name = "muzzle_1" } } } } } },
            ["bare-pod"] = new GameObject { Skinned = false, Meshes = 1 },
            ["round"] = new GameObject { Skinned = false, Meshes = 1 },
        };
        /// <summary>A suit prefab with the 16 rig bones (and the opening clips holder), less the bones in <paramref name="without"/>.</summary>
        private static GameObject Suit(params string[] without) => new()
        {
            Animated = true,
            transform = new Transform { name = "model", Children = FrameBodyRig.PoseLinks.Where(l => !without.Contains(l.Model)).Select(l => new Transform { name = l.Model }).ToArray() },
        };
        public IReadOnlyCollection<string> ModelNames => Prefabs.Keys;
        public bool TryModel(string name, out ModelHandle model)
        {
            model = Prefabs.TryGetValue(name, out var prefab) ? new ModelHandle { Prefab = prefab } : null!;
            return model != null;
        }
    }
    public sealed class ModelHandle
    {
        public GameObject Prefab { get; init; } = new();
        public GameObject? Spawn(Vector3 at, Quaternion rotation, Transform? parent = null) => new();
    }
    public static class Bundles
    {
        public static BundleHandle? Register(string modId, string pack, string path) => new();
        /// <summary>Two packs, both holding the same prefabs: this plugin's and another mod's.</summary>
        public static bool TryGet(string pack, out BundleHandle bundle) { bundle = new(); return pack is "Frames" or "Other"; }
    }
    public static class Models { public static ModelHandle? Get(string name) => new(); }
}
namespace ForgeOrdnance
{
    public interface IOrdnancePayload
    {
        object Parse(System.Text.Json.JsonElement payload);
        void Execute(OrdnanceExecution execution);
        void OnArrived(OrdnancePresentation presentation);
        void OnData(OrdnancePayloadData data);
    }

    public class OrdnancePresentation
    {
        public OrdnanceDefinition Definition = new() { Key = 1 };
        public object Parameters = null!;
        public ulong SummonerLookup;
        public uint ThrowId;
        public Vector3 Position;
        public Quaternion Rotation;
        public OrdnanceArrivalMode Arrival;
    }
    public sealed class OrdnanceDefinition { public uint Key; public string Name = ""; }
    public enum OrdnanceArrivalMode : byte { Drop = 0, Direct = 1 }
    public sealed class OrdnanceExecution : OrdnancePresentation { public AIGraph.AIG_CourseNode? CourseNode; }
    public sealed class OrdnancePayloadData
    {
        public byte Tag = FrameMessages.StateTag;
        public byte[] Data = Array.Empty<byte>();
        public ulong SummonerLookup;
        public uint ThrowId;
        public bool Replayed;
    }
    public static class OrdnanceApi
    {
        public static readonly Dictionary<(ulong, uint), byte[]> Retained = new();
        public static int Sent;
        public static int Capacity = 256;
        public static bool SendState(string type, ulong thrower, uint id, byte tag, byte[] bytes)
        {
            Sent++;
            if (!Retained.ContainsKey((thrower, id)) && Retained.Count >= Capacity) return false;
            Retained[(thrower, id)] = bytes;
            return true;
        }
        public static void ClearState(string type, ulong thrower, uint id) => Retained.Remove((thrower, id));
        /// <summary>The loaded definitions by key (Ordnance's own lookup is tested in Mods/ForgeOrdnance/tests).</summary>
        public static readonly Dictionary<uint, OrdnanceDefinition> Loaded = new();
        public static bool TryGetDefinition(uint key, out OrdnanceDefinition definition) => Loaded.TryGetValue(key, out definition!);
        public static string DisplayName(OrdnanceDefinition definition) => definition.Name;
    }
}
// ForgeCore as Frames calls it. ForgeCore's own behaviour (the channel's queue, overflow and version refusal; the noise and
// explosion requests) is tested in Mods/ForgeCore/tests; these doubles pass Frames' calls to the recording game doubles here.
namespace ForgeCore
{
    public enum NetDelivery : byte { Reliable, Lossy }

    public sealed class NetChannel
    {
        private readonly Queue<Action> _inbox = new();
        /// <summary>How each message was registered (ForgeCore's own routing is tested in Mods/ForgeCore/tests).</summary>
        public static readonly Dictionary<string, NetDelivery> Registered = new();
        public void On<T>(string name, Action<ulong, T> handler, NetDelivery delivery = NetDelivery.Reliable) where T : unmanaged
        {
            Registered[name] = delivery;
            GTFO.API.NetworkAPI.RegisterEvent<T>(name, (sender, message) => _inbox.Enqueue(() => handler(sender, message)));
        }
        public void ToHost<T>(string name, T message) where T : unmanaged
            => GTFO.API.NetworkAPI.Sent.Add((name, message, (SNetwork.SNet.Master ?? throw new InvalidOperationException("No host.")).Lookup));
        public bool TryToHost<T>(string name, T message) where T : unmanaged { if (SNetwork.SNet.Master == null) return false; ToHost(name, message); return true; }
        public void To<T>(ulong player, string name, T message) where T : unmanaged => GTFO.API.NetworkAPI.Sent.Add((name, message, player));
        public void Broadcast<T>(string name, T message) where T : unmanaged => GTFO.API.NetworkAPI.Sent.Add((name, message, 0));
        public void Pump() { while (_inbox.Count != 0) _inbox.Dequeue()(); }
        public void Clear() => _inbox.Clear();
        public event Action<ulong>? PeerSynced;
        public static NetChannel? Last;
        public void Sync(ulong player) => PeerSynced?.Invoke(player);
    }

    public static class Net
    {
        public static NetChannel Open(string modId, int capacity = 512) => NetChannel.Last = new();
    }

    public static class Level
    {
        public static bool IsPlayer(Player.PlayerAgent? agent, ulong lookup)
            => lookup != 0 && agent != null && !agent.WasCollected && agent.Owner != null && agent.Owner.Lookup == lookup;
        public static Player.PlayerAgent? PlayerByLookup(ulong lookup) => PlayerByLookup(lookup, out _);
        public static Player.PlayerAgent? PlayerByLookup(ulong lookup, out int index)
        {
            var agents = Player.PlayerManager.PlayerAgentsInLevel;
            for (var i = 0; agents != null && i < agents.Count; i++)
                if (IsPlayer(agents[i], lookup)) { index = i; return agents[i]; }
            index = -1;
            return null;
        }
        // ForgeCore's own search order is tested in Mods/ForgeCore/tests; here only the first dimension has a node.
        public static bool CourseNodeAt(Vector3 position, float sample, out AIGraph.AIG_CourseNode? node, out int dimension, int preferredDimension = -1)
        {
            var found = AIGraph.AIG_CourseNode.TryGetCourseNode(eDimensionIndex.Dimension1, position, sample, out node);
            dimension = found ? (int)eDimensionIndex.Dimension1 : -1;
            return found;
        }
    }

    public sealed class PlayerNoiseLease : IDisposable
    {
        public Player.PlayerAgent Player { get; }
        public float Multiplier { get; }
        public string Owner { get; }
        public bool Active { get; private set; } = true;
        internal PlayerNoiseLease(Player.PlayerAgent player, float multiplier, string owner) { Player = player; Multiplier = multiplier; Owner = owner; }
        public void Dispose() { Active = false; PlayerNoise.Held.Remove(this); }
    }

    /// <summary>Records the leases Frames takes; ForgeCore's own multiplier arithmetic and detection hook are tested in Mods/ForgeCore/tests.</summary>
    public static class PlayerNoise
    {
        public static readonly List<PlayerNoiseLease> Held = new();
        public static PlayerNoiseLease Scale(Player.PlayerAgent player, float multiplier, string owner)
        {
            var lease = new PlayerNoiseLease(player, multiplier, owner);
            Held.Add(lease);
            return lease;
        }
    }
    public static class Combat
    {
        public static bool Noise(Vector3 position, float radius, AIGraph.AIG_CourseNode? node)
        {
            if (radius <= 0f || node == null) return false;
            NoiseManager.MakeNoise(new NM_NoiseData { position = position, radiusMax = radius, yScale = 1f, node = node, type = NM_NoiseType.InstaDetect, includeToNeightbourAreas = true });
            return true;
        }
    }
}
namespace GTFO.API
{
    public static class NetworkAPI
    {
        public static readonly List<(string Event, object Message, ulong To)> Sent = new();
        public static readonly Dictionary<string, Delegate> Handlers = new();
        public static void RegisterEvent<T>(string name, Action<ulong, T> handler) => Handlers[name] = handler;
    }
}
namespace ForgeOrdnanceFrames
{
    internal static class FrameAudio { internal static void Clear() { } }
    internal static class FrameHudArt
    {
        /// <summary>The model packs whose cockpit mark is registered with ForgeUI.</summary>
        internal static readonly HashSet<string> Marked = new() { "Frames:frame-m15" };
        internal static Sprite? Mark(string model) => Marked.Contains(model) ? new Sprite() : null;
        /// <summary>The lines ForgeUI would write (its icon book reports a missing icon once per message); Frames writes none itself.</summary>
        internal static readonly List<string> UiLog = new();
        private static readonly HashSet<string> Expected = new();
        private static bool _settled;
        internal static void Expect(string model) { if (Expected.Add(model) && _settled) Look(model); }
        internal static void Settle() { _settled = true; foreach (var model in Expected) Look(model); }
        internal static void Unsettle() { _settled = false; Expected.Clear(); UiLog.Clear(); }
        private static void Look(string model)
        {
            var line = "Icon '" + FrameModelName.MarkIcon(model) + "' is not registered";
            if (Mark(model) == null && !UiLog.Contains(line)) UiLog.Add(line);
        }
    }
    internal static class FramePrewarm
    {
        }

    internal static class Plugin
    {
        internal const string Id = "NAinfini.ForgeOrdnanceFrames";
        internal static readonly ForgeCore.ModLog Logger = ForgeCore.Log.For(Id);
        internal static readonly List<string> Errors = new();
        internal static bool ThrowOnError = true;
        internal static void Error(string m)
        {
            Errors.Add(m);
            if (ThrowOnError) throw new Exception(m);
        }
        internal static string Num(float f) => f.ToString();
    }
    internal sealed class FrameVisual
    {
        internal const float FallSeconds = 2.0f;
        internal const float ArriveSeconds = FallSeconds + FrameSequence.LandSettleSeconds;
        internal bool Replayed, Gone;
        internal static FrameVisual Create(FrameUnit unit, bool replayed = false) => new() { Replayed = replayed };
        internal void Tick(float now) { }
        internal void LateTick(float now) { }
        internal void HandsSolved(System.IntPtr syncIK) { }
        internal void Destroy() => Gone = true;
        internal void Shots(int shots, Vector3 from, Vector3 to)
        {
        }
    }
    internal static class FrameFx
    {
    }
    internal static class MechGun { internal static void AfterReload(float now) { } }
    internal static class LocalPilot
    {
        internal static int Refusals;
        internal static int Ejections;
        internal static FrameRejection LastRefusal;
        internal static FrameEjection LastEjection;
        internal static FrameUnit? Unit;
        internal static void Refused(FrameRequestKind kind, FrameRejection reason) { Refusals++; LastRefusal = reason; }
        internal static void Ejected(FrameEjection reason) { Ejections++; LastEjection = reason; }
        internal static void Release(string reason) { }
        internal static void LevelCleanup() { }
        internal static void Tick(float now) { }
        internal static void LateTick(float now) { }
        internal static void RocketsLaunched(FrameUnit unit, float now) { }
        internal static int Hits;
        internal static void Hit(FrameUnit unit, FrameHitMessage message) => Hits++;
        internal static void Called(FrameSpec spec) { }
    }
    internal static class FrameRockets
    {
        internal static int Flying, Launches;
        internal static void Launch(FrameUnit unit, FrameRocketMessage message, float now)
        {
            Flying++;
            Launches++;
        }
        internal static void Tick(float now) { }
        internal static void Clear() => Flying = 0;
    }
    internal static class FrameRepel
    {
        internal const float Interval = .25f;
        internal static void Host(FrameUnit unit, Player.PlayerAgent agent, float now) { }
        internal static void Clear() { }
    }
}
namespace Il2CppInterop.Runtime { public static class Il2CppType { public static Type Of<T>() => typeof(T); } }
