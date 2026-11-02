// Offline doubles for running ForgeCore's real nearby-enemy query (Native/Enemies.cs). No game assembly is loaded.
namespace UnityEngine
{
    public class Object
    {
        private static long _next;
        public IntPtr Pointer { get; } = new(++_next);
        public bool WasCollected;
        public int InstanceId;
        public static readonly List<Object> Scene = new();
        public static T[] FindObjectsOfType<T>() where T : Object => Scene.OfType<T>().ToArray();
        public int GetInstanceID() => InstanceId != 0 ? InstanceId : (int)Pointer;
        public T? TryCast<T>() where T : class => this as T;
    }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
    }
    public class Transform : Object
    {
        public Transform? Parent;
        public bool IsChildOf(Transform parent) => this == parent || Parent?.IsChildOf(parent) == true;
        public void SetParent(Transform parent, bool worldPositionStays) => Parent = parent;
    }
    public class GameObject : Object
    {
        public Transform transform = new();
    }
    public struct Quaternion { public static Quaternion identity => default; }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 down => new(0, -1, 0);
        public static Vector3 up => new(0, 1, 0);
        public static Vector3 zero => default;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(Vector3 a, float f) => new(a.x * f, a.y * f, a.z * f);
        public override string ToString() => "(" + x + "," + y + "," + z + ")";
    }
    public class Collider : Component
    {
        public bool isTrigger;
        public global::Enemies.EnemyAgent? Owner;
        public int Lookups;
        public T? GetComponentInParent<T>() where T : class { Lookups++; return Owner as T; }
    }
    public class Animator : Object { public float speed = 1f; }
    public static class Time { public static float time; }
    public static class Mathf { public static float Max(float a, float b) => MathF.Max(a, b); }
    public struct Color32 { public byte r, g, b, a; }
    public class Texture2D : Object
    {
        public byte[] Raw = Array.Empty<byte>();
        public unsafe void LoadRawTextureData(IntPtr data, int size) { Raw = new byte[size]; new ReadOnlySpan<byte>((void*)data, size).CopyTo(Raw); }
    }
    public enum QueryTriggerInteraction { UseGlobal, Ignore, Collide }
    public struct RaycastHit { public Collider? collider; public Vector3 point; public float distance; }
    public static class Physics
    {
        public static Collider? FloorCollider;
        public static (float Distance, int Mask) LastRay;
        /// <summary>When set, answers every ray with a hit point instead of the floor collider; a null result is a miss.</summary>
        public static Func<Vector3, Vector3, Vector3?>? RayAnswer;
        /// <summary>When set, says which line a linecast meets something on.</summary>
        public static Func<Vector3, Vector3, bool> LineBlocked = (_, _) => false;
        public static (Vector3 From, Vector3 To, int Mask, QueryTriggerInteraction Trigger) LastLine;
        public static bool Linecast(Vector3 from, Vector3 to, int mask, QueryTriggerInteraction trigger)
        { LastLine = (from, to, mask, trigger); return LineBlocked(from, to); }
        /// <summary>When set, answers the next rays in order with these hits (the distance is from that ray's own origin); an empty queue is a miss.</summary>
        public static Queue<(Collider Collider, float Distance)>? Scripted;
        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float distance, int mask, QueryTriggerInteraction trigger)
        {
            LastRay = (distance, mask);
            if (Scripted != null)
            {
                if (!Scripted.TryDequeue(out var next)) { hit = default; return false; }
                hit = new RaycastHit { collider = next.Collider, distance = next.Distance };
                return true;
            }
            if (RayAnswer != null)
            {
                var point = RayAnswer(origin, direction);
                hit = new RaycastHit { collider = FloorCollider, point = point ?? default };
                return point.HasValue;
            }
            hit = new RaycastHit { collider = FloorCollider }; return FloorCollider != null;
        }
        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float distance, int mask)
        { LastRay = (distance, mask); hit = new RaycastHit { collider = FloorCollider }; return FloorCollider != null; }
        public static Collider[] Hits = Array.Empty<Collider>();
        public static (int Mask, QueryTriggerInteraction Trigger) Last;
        public static int OverlapSphereNonAlloc(Vector3 p, float r, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> result, int mask, QueryTriggerInteraction trigger)
        {
            Last = (mask, trigger);
            var n = Math.Min(Hits.Length, result.Length);
            for (var i = 0; i < n; i++) result[i] = Hits[i];
            return n;
        }
    }
}
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    /// <summary>Native memory laid out like the game's arrays: a header of four pointers, then the elements.</summary>
    public unsafe class Il2CppStructArray<T> where T : unmanaged
    {
        public IntPtr Pointer { get; }
        public int Length { get; }
        public Il2CppStructArray(int n)
        {
            Length = n;
            Pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4 * IntPtr.Size + n * sizeof(T));
        }
        public Span<T> Items => new((void*)IntPtr.Add(Pointer, 4 * IntPtr.Size), Length);
        public T this[int i] { get => Items[i]; set => Items[i] = value; }
    }
    public class Il2CppReferenceArray<T>
    {
        private readonly T[] _items;
        public Il2CppReferenceArray(int n) => _items = new T[n];
        public int Length => _items.Length;
        public T this[int i] { get => _items[i]; set => _items[i] = value; }
    }
}
namespace UnityEngine.AI
{
    public struct NavMeshHit { public UnityEngine.Vector3 position; }
    public enum NavMeshPathStatus { PathComplete, PathPartial, PathInvalid }
    public class NavMeshPath
    {
        public NavMeshPathStatus status;
        public UnityEngine.Vector3[] Corners = Array.Empty<UnityEngine.Vector3>();
        public int GetCornersNonAlloc(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.Vector3> into)
        {
            var n = Math.Min(Corners.Length, into.Length);
            for (var i = 0; i < n; i++) into[i] = Corners[i];
            return n;
        }
    }
    public static class NavMesh
    {
        /// <summary>The mesh: a point is on it when this says so; the snapped position is the point itself.</summary>
        public static Func<UnityEngine.Vector3, bool> OnMesh = _ => true;
        public static NavMeshPathStatus PathStatus = NavMeshPathStatus.PathComplete;
        public static UnityEngine.Vector3[] PathCorners = Array.Empty<UnityEngine.Vector3>();
        public static bool CalculateOk = true;
        public static (float Radius, int Area) LastSample;
        public static bool SamplePosition(UnityEngine.Vector3 p, out NavMeshHit hit, float radius, int areaMask)
        {
            LastSample = (radius, areaMask);
            hit = new NavMeshHit { position = p };
            return OnMesh(p);
        }
        public static bool CalculatePath(UnityEngine.Vector3 from, UnityEngine.Vector3 to, int areaMask, NavMeshPath path)
        {
            path.status = PathStatus; path.Corners = PathCorners;
            return CalculateOk;
        }
    }
}public enum eDimensionIndex { Reality, Dimension1, Dimension2, Dimension3, MAX_COUNT }
namespace AIGraph
{
    public sealed class AIG_CourseNode
    {
        public bool WasCollected;
        public static readonly Dictionary<eDimensionIndex, AIG_CourseNode?> Nodes = new();
        public static readonly List<eDimensionIndex> Asked = new();
        public static bool TryGetCourseNode(eDimensionIndex dimension, UnityEngine.Vector3 position, float sample, out AIG_CourseNode? node)
        {
            Asked.Add(dimension);
            return Nodes.TryGetValue(dimension, out node) && node != null;
        }
    }
}
namespace Agents
{
    public enum AgentMode { Off, Agressive, Patrolling, Scout, Hibernate }
    public class Agent : UnityEngine.Component
    {
        public bool Alive = true;
        public int DimensionIndex;
        public UnityEngine.Vector3 Position;
    }
}
namespace Enemies
{
    public class EnemyGroup { }
    /// <summary>The game's allocator as ForgeCore's spawn request sees it: records each request and answers with the enemy the case sets.</summary>
    public class EnemyAllocator
    {
        public static EnemyAllocator? Current;
        public bool WasCollected;
        public readonly List<string> Requests = new();
        public EnemyAgent? Result;
        public EnemyAgent? SpawnEnemy(uint id, AIGraph.AIG_CourseNode node, Agents.AgentMode mode, UnityEngine.Vector3 position, UnityEngine.Quaternion rotation,
            EnemyGroup? group, int placementIndex = -1)
        { Requests.Add($"{id} {mode} {position} group={group != null} index={placementIndex}"); return Result; }
    }
    public class EnemyAI { public Agents.AgentMode Mode = Agents.AgentMode.Hibernate; }
    public class EnemyLocomotion { public ES_StateEnum CurrentStateEnum; }
    public class EnemyAgent : Agents.Agent, IDamageable
    {
        public EnemyAI AI = new();
        public UnityEngine.Animator? Anim = new();
        public EnemyLocomotion? Locomotion;
        public IDamageable? GetBaseDamagable() => this;
        public Agents.Agent GetBaseAgent() => this;
        public void Setup() { }
        public void OnDead() { }
        public void OnDeSpawn() { }
        public void OnDestroy() { }
    }
}
public enum ES_StateEnum { Hibernate, StuckInGlue }
public interface IDamageable { IntPtr Pointer { get; } Agents.Agent? GetBaseAgent(); IDamageable? GetBaseDamagable(); }
public static class LayerManager { public const int MASK_ENEMY_DAMAGABLE = 2, MASK_WORLD = 4, MASK_EXPLOSION_TARGETS = 8, MASK_EXPLOSION_BLOCKERS = 16, MASK_BULLETWEAPON_RAY = 32; }
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)] public class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    [AttributeUsage(AttributeTargets.Method)] public class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public class HarmonyPostfix : Attribute { }
}
namespace ForgeCore
{
    /// <summary>ForgeCore's host pieces that Native/Enemies.cs calls: patching is recorded, the log is ForgeCore's own.</summary>
    internal static class Patches
    {
        internal static readonly List<string> Ensured = new();
        internal static void Ensure(string module, Type patches) => Ensured.Add(module + ":" + patches.Name);
    }
    /// <summary>The enemy life brackets Native/Enemies.cs subscribes to; Fire drives them as the game's four patches do.</summary>
    internal static class GameHooks
    {
        internal static readonly Bracket<global::Enemies.EnemyAgent> EnemySetup = new("GameHooks.EnemySetup", typeof(GameHooks));
        internal static readonly Bracket<global::Enemies.EnemyAgent> EnemyDead = new("GameHooks.EnemyDead", typeof(GameHooks));
        internal static readonly Bracket<global::Enemies.EnemyAgent> EnemyDespawned = new("GameHooks.EnemyDespawned", typeof(GameHooks));
        internal static readonly Bracket<global::Enemies.EnemyAgent> EnemyDestroyed = new("GameHooks.EnemyDestroyed", typeof(GameHooks));
        internal static void Fire(string name, global::Enemies.EnemyAgent e)
        {
            if (name == "Setup" || name == "Spawned") { EnemySetup.RunBefore(e); EnemySetup.RunAfter(e, true); }
            else if (name == "Dead") EnemyDead.RunBefore(e);
            else if (name == "Despawned") EnemyDespawned.RunBefore(e);
            else if (name == "Destroyed") EnemyDestroyed.RunBefore(e);
            else throw new ArgumentException(name);
        }
    }
    internal static class Plugin
    {
        internal static ModLog Logger => Log.For("NAinfini.ForgeCore");
    }
}

public enum eRundownTier { TierA = 1, TierB, TierC, TierD, TierE }
public struct pActiveExpedition { public eRundownTier tier; public int expeditionIndex; }
public static class RundownManager
{
    public static pActiveExpedition Active;
    public static bool ThrowOnRead;
    public static pActiveExpedition GetActiveExpeditionData() => ThrowOnRead ? throw new InvalidOperationException("no expedition") : Active;
}
namespace Globals { public static class Global { public static uint RundownIdToLoad; } }
