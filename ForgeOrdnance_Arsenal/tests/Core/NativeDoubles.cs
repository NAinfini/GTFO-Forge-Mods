// Offline doubles for exercising the actual Arsenal loan and visual lifecycle code. No game assemblies are loaded.
using System;
using System.Collections.Generic;
using System.Linq;
using ForgeOrdnanceArsenal;

namespace UnityEngine
{
    public class Object
    {
        private static long _next;
        public IntPtr Pointer { get; } = new(++_next);
        public bool WasCollected;
        public int InstanceId;
        public int GetInstanceID() => InstanceId != 0 ? InstanceId : (int)Pointer;
        public T? TryCast<T>() where T : class => this as T;
        public static void Destroy(Object obj, float delay = 0) { if (delay == 0) obj.WasCollected = true; }
        public static void DestroyImmediate(Object obj) => obj.WasCollected = true;
    }
    public class MonoBehaviour : Component { public MonoBehaviour(IntPtr p){} }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
    }
    public class Transform : Object
    {
        public GameObject gameObject=null!;
        public Vector3 position, localScale;
        public Quaternion rotation;
        public Vector3 forward => new(0, 0, 1);
        private readonly List<Transform> Children = new();
        public int childCount => Children.Count(c=>!c.gameObject.WasCollected);
        public Transform GetChild(int i) => Children.Where(c=>!c.gameObject.WasCollected).ElementAt(i);
        public Transform? Parent;
        public bool IsChildOf(Transform parent) => this == parent || Parent?.IsChildOf(parent) == true;
        public void SetParent(Transform parent, bool worldPositionStays) { Parent?.Children.Remove(this); Parent=parent; parent.Children.Add(this); }
    }
    public class GameObject : Object
    {
        public static readonly List<GameObject> Created = new();
        public string name;
        public int layer;
        public readonly List<Component> Components = new();
        public Transform transform = new();
        public GameObject(string name = "") { this.name = name; transform.gameObject=this; Created.Add(this); }
        public T AddComponent<T>() where T : Component, new() { var c = new T(); c.gameObject = this; Components.Add(c); return c; }
        public T[] GetComponentsInChildren<T>(bool inactive) => Components.OfType<T>().ToArray();
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static bool operator ==(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
        public override bool Equals(object? o) => o is Vector2 v && this == v;
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode();
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 down => new(0, -1, 0);
        public static Vector3 operator -(Vector3 a) => new(-a.x,-a.y,-a.z);
        public static Vector3 up => new(0, 1, 0);
        public static Vector3 one => new(1, 1, 1);
        public float sqrMagnitude => x*x + y*y + z*z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude == 0 ? default : this * (1/magnitude);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x+b.x, a.y+b.y, a.z+b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x-b.x, a.y-b.y, a.z-b.z);
        public static Vector3 operator *(Vector3 a, float b) => new(a.x*b, a.y*b, a.z*b);
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b-a)*t;
        public static float Dot(Vector3 a, Vector3 b) => a.x*b.x + a.y*b.y + a.z*b.z;
        public static float Distance(Vector3 a, Vector3 b) => (a-b).magnitude;
    }
    public struct Quaternion
    {
        public Vector3 eulerAngles;
        public static Quaternion LookRotation(Vector3 v) => default;
        public static Vector3 operator *(Quaternion q,Vector3 v) => v;
        public static Quaternion identity => default;
        public static Quaternion Euler(float x, float y, float z) => new() { eulerAngles = new(x,y,z) };
        public static Quaternion operator *(Quaternion a, Quaternion b) => a;
    }
    public struct Bounds { }
    public struct Color
    {
        public float r,g,b,a;
        public Color(float r, float g, float b, float a = 1) { this.r=r; this.g=g; this.b=b; this.a=a; }
    }
    public class Shader : Object { public static Shader Find(string path) => new(); }
    public class Material : Object { public string name = ""; public Material(Shader s) {} }
    public enum LightType { Point }
    public class Light : Component { public bool enabled=true; public LightType type; public Color color; public float range, intensity; }
    public class Renderer : Component { public bool enabled; }
    public class MeshRenderer : Renderer { }
    public class SkinnedMeshRenderer : Renderer { }
    public class LineRenderer : Renderer
    {
        public Material sharedMaterial = null!;
        public int positionCount;
        public bool useWorldSpace, loop;
        public float widthMultiplier;
        public Color startColor, endColor;
        public void SetPosition(int i, Vector3 v) { }
    }
    public static class Time { public static float time; public static float deltaTime = .02f; }
    public static class Mathf { public const float PI = MathF.PI; public static float Sin(float f)=>MathF.Sin(f); public static float Cos(float f)=>MathF.Cos(f); }
    public class Collider : Component
    {
        public Enemies.EnemyAgent? Owner;
        public int Lookups;
        public T? GetComponentInParent<T>() where T : class { Lookups++; return Owner as T; }
    }
    public enum QueryTriggerInteraction { Ignore, Collide }
    public struct RaycastHit { public Vector3 point, normal; }
    public static class Physics
    {
        public static bool Hit;
        public static Collider[] Hits = Array.Empty<Collider>();
        public static int OverlapSphereNonAlloc(Vector3 p,float r,Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> result,int mask,QueryTriggerInteraction trigger)
        {
            var n=Math.Min(Hits.Length,result.Length);
            for(var i=0;i<n;i++) result[i]=Hits[i];
            return n;
        }
        public static bool Raycast(Vector3 p,Vector3 d,out RaycastHit hit,float distance,int mask,QueryTriggerInteraction trigger=QueryTriggerInteraction.Ignore)
        { hit=new() {point=p,normal=Vector3.up}; return Hit; }
    }
    public static class Input { public static bool Drop; }
}

namespace GameData
{
    public class GameDataBlockBase<T>
    {
        public uint persistentID;
        public static readonly Dictionary<uint, T> Blocks = new();
        public static T? GetBlock(uint id) => Blocks.TryGetValue(id, out var b) ? b : default;
    }
    public class PlayerOfflineGearDataBlock : GameDataBlockBase<PlayerOfflineGearDataBlock> { public string GearJSON = "1"; }
    public class WeaponMuzzleFlashDataBlock : GameDataBlockBase<WeaponMuzzleFlashDataBlock> { public string muzzleFlashFPS="fps", muzzleFlash3RD="third"; }
}
namespace Gear
{
    public enum eGearComponent { MuzzleFlash, Category, FireMode }
    public class GearIDRange
    {
        private readonly string _json;
        public GearIDRange(string json) { _json=json; }
        public string PublicGearName { get; set; } = "";
        public uint GetCompID(eGearComponent c) => c == eGearComponent.FireMode ? 0 : GetChecksum();
        public uint GetChecksum() => uint.Parse(_json);
        public string ToJSON() => _json;
    }
    public class Item : UnityEngine.Component { }
    public class ItemEquippable : Item
    {
        public Player.PlayerAgent? Owner;
        public GameData.ItemDataBlock? ItemDataBlock = new();
        public bool IsReloading;
        public int Clip = 5;
        public void TryTriggerReloadAnimationSequence() { }
        public void SetCurrentClip(int clip) => Clip=clip;
        public int GetCurrentClip()=>Clip;
        public int GetMaxClip()=>10;
        public GearPartHolder? GearPartHolder = new();
        public UnityEngine.Transform SightLookAlign = new();
        public UnityEngine.Transform? m_rightHandGripAlign, m_leftHandGripAlign, MuzzleAlign;
    }
    public class GearPartHolder
    {
        public UnityEngine.Transform m_aSightLook = new();
        public UnityEngine.Transform? m_aRightHand = new(), m_aLeftHand = new(), m_aMuzzle = new(), m_aMag = new();
    }
    public class BulletWeaponArchetype { public ArchetypeDataBlock m_archetypeData = null!; }
    public class BulletWeapon : ItemEquippable
    {
        public static object? s_tracerPool;
        public ArchetypeDataBlock? ArchetypeData = new();
        public static ArchetypeDataBlock Stock = new() { SpecialChargetupTime = 1.5f, ShotDelay = .1f, Damage = 9f, DefaultReloadTime = 3.3f, PrecisionDamageMulti = 2f, StaggerDamageMulti = 1f };
        public BulletWeaponArchetype? m_archeType = new() { m_archetypeData = Stock };
        public void Fire() { }
        public bool BulletHit() => true;
    }
}
namespace SNetwork
{
    public enum SNet_ChannelType { GameOrderCritical }
    public sealed class SNet_Player { public ulong Lookup; public bool IsBot; }
    public static class SNet
    {
        public static bool IsMaster = true; public static SNet_Player LocalPlayer=null!; public static SNet_Player? Master;
        public static bool TryGetPlayer(ulong lookup,out SNet_Player? player)
        { player=Player.PlayerManager.PlayerAgentsInLevel.FirstOrDefault(a=>a.Owner.Lookup==lookup)?.Owner; return player!=null; }
    }
}
public enum InventorySlot { GearSpecial, GearStandard }
public enum InputAction { Use, SelectSpecial }
public enum eFocusState { FPS, Other }
public static class FocusStateManager { public static eFocusState CurrentState = eFocusState.FPS; }
public static class InputMapper { public static Func<InputAction,eFocusState,bool> GetButtonDown = (_,_)=>false; }
public static class GuiManager
{
    public static InteractionLayer? InteractionLayer = new();
}
public class InteractionLayer
{
    public bool InteractPromptVisible;
    public readonly List<string> Prompts = new();
}
namespace Player
{
    public class PlayerAgent : UnityEngine.Component
    {
        public SNetwork.SNet_Player Owner = new();
        public bool Alive = true, IsLocallyOwned;
        public int DimensionIndex;
        public UnityEngine.Vector3 Position, EyePosition;
        public Inventory Inventory = new();
        public Camera FPSCamera = new();
        public Sync Sync = new();
        public Enemies.EnemyDamage Damage = new();
    }
    public class Inventory { public Gear.ItemEquippable? WieldedItem; }
    public class Camera : UnityEngine.Component { public Camera(){gameObject=new();} public void Shake(float a,float b,float c){} }
    public class Sync { public void WantsToWieldSlot(InventorySlot slot){} }
    public static class PlayerManager
    {
        public static readonly List<PlayerAgent> PlayerAgentsInLevel = new();
        public static PlayerAgent? Local;
        public static PlayerAgent? GetLocalPlayerAgent()=>Local;
    }
    public class BackpackItem
    {
        public Gear.GearIDRange? GearIDRange;
        public Gear.ItemEquippable? Instance;
        public bool IsLoaded = true;
    }
    public class AmmoPool { public float AmmoInPack=30, AmmoMaxCap=100, CostOfBullet=1; public int BulletsInPack => (int)(AmmoInPack/CostOfBullet); }
    public enum AmmoType { Standard, Special }
    public class PlayerAmmoStorage
    {
        private static long _next;
        public IntPtr Pointer = new(++_next);
        public AmmoPool SpecialAmmo = new();
        /// <summary>The game adds to the pool and stops at its capacity.</summary>
        public void PickupAmmo(AmmoType type, float amount) { if (type == AmmoType.Special) SpecialAmmo.AmmoInPack = Math.Min(SpecialAmmo.AmmoInPack + amount, SpecialAmmo.AmmoMaxCap); }
        public float GetClipAmmoFromSlot(InventorySlot slot)=>PlayerBackpackManager.LocalBackpack.Item?.Instance?.GetCurrentClip()??0;
        public void UpdateAllAmmoUI(){}
    }
    public class PlayerBackpack
    {
        public BackpackItem? Item;
        public PlayerAmmoStorage AmmoStorage = new();
        public bool TryGetBackpackItem(InventorySlot slot,out BackpackItem? item) { item=Item; return item!=null; }
    }
    public static class PlayerBackpackManager
    {
        public static PlayerBackpack LocalBackpack = new();
        public static readonly Dictionary<ulong, PlayerBackpack> Backpacks = new();
        public static Action<Gear.GearIDRange>? Equip;
        public static int EquipCalls;
        public static void EquipLocalGear(Gear.GearIDRange range) { EquipCalls++; Equip?.Invoke(range); }
        public static bool TryGetBackpack(SNetwork.SNet_Player owner,out PlayerBackpack pack)=>Backpacks.TryGetValue(owner.Lookup,out pack!);
        public static void ForceLocalAmmoStorageUpdate(){}
    }
}
namespace ForgeOrdnance
{
    public sealed class OrdnancePayloadData
    {
        public ulong SummonerLookup; public uint ThrowId; public byte Tag; public byte[] Data=Array.Empty<byte>(); public bool Replayed;
    }
    internal static class TestDefinition
    {
        internal static OrdnanceDefinition Make() => new("test.call","test","Weapon",0u,"","",new[]{Direction.Up},1,1,1,"arsenal.HeavyWeapon",default,new object(),"");
    }
    public sealed class OrdnancePresentation
    {
        public ulong SummonerLookup; public uint ThrowId; public object Parameters=null!; public OrdnanceDefinition Definition=TestDefinition.Make();
        public UnityEngine.Vector3 Position; public UnityEngine.Quaternion Rotation; public OrdnanceArrivalMode Arrival;
    }
    public sealed class OrdnanceExecution
    {
        public ulong SummonerLookup; public uint ThrowId; public object Parameters=null!; public OrdnanceDefinition Definition=TestDefinition.Make();
        public Player.PlayerAgent? Summoner;
        public UnityEngine.Vector3 Position; public UnityEngine.Quaternion Rotation; public OrdnanceArrivalMode Arrival;
    }
    public readonly struct OrdnancePodBay
    {
        public OrdnancePodBay(UnityEngine.Vector3 p, UnityEngine.Quaternion q, UnityEngine.Vector3 s, UnityEngine.Transform? anchor = null) { Position=p; Rotation=q; Size=s; Anchor=anchor; }
        public UnityEngine.Vector3 Position { get; } public UnityEngine.Quaternion Rotation { get; } public UnityEngine.Vector3 Size { get; }
        public UnityEngine.Transform? Anchor { get; }
        /// <summary>How many bays a look was stood in (the real fit needs Unity's mesh bounds).</summary>
        public static int Fits;
        public UnityEngine.GameObject Fit(UnityEngine.GameObject look, UnityEngine.Quaternion frontToForward, out float scale, out float lean)
        {
            Fits++; scale=1; lean=0;
            var holder=new UnityEngine.GameObject("ForgeOrdnance.BayHolder");
            if(Anchor!=null) holder.transform.SetParent(Anchor,false);
            look.transform.SetParent(holder.transform,false);
            return holder;
        }
    }
    public sealed class OrdnancePod
    {
        public ulong SummonerLookup; public uint ThrowId;
        public bool IsOpen;
        public int WeaponBayCount = 1;
        public UnityEngine.Transform[]? WeaponAnchors;
        public OrdnancePodBay[]? WeaponBays;
        public int LastAnchorIndex = -1, LastBayIndex = -1;
        public UnityEngine.Transform? WeaponAnchorAt(int index) { LastAnchorIndex = index; return WeaponAnchors == null ? WeaponAnchor : WeaponAnchors[index]; }
        public OrdnancePodBay? WeaponBayAt(int index)
        {
            LastBayIndex = index;
            var bay = WeaponBays == null ? WeaponBay : WeaponBays[index];
            return bay is { } b ? new OrdnancePodBay(b.Position, b.Rotation, b.Size, WeaponAnchorAt(index)) : null;
        }
        public OrdnanceClaimable AddClaimable(int bay, ClaimableSpec spec) => OrdnanceClaims.Add(SummonerLookup, ThrowId, spec, this, bay, default, 0f);
        public UnityEngine.Transform? WeaponAnchor;
        public OrdnancePodBay? WeaponBay;
        public event Action? Opened, Left;
        public void Open() { IsOpen=true; Opened?.Invoke(); }
        public void Leave()=>Left?.Invoke();
    }
    public static class OrdnanceDelivery
    {
        public static float PodContentsDelay(OrdnanceArrivalMode arrival)=>arrival==OrdnanceArrivalMode.Direct?0:1;
        public static OrdnancePod? Pod;
        public static int Emptied;
        public static OrdnancePod? StartPod(OrdnancePresentation p)=>Pod;
        public static OrdnancePod? FindPod(ulong summoner,uint id)=>Pod;
        public static void HostAfter(float seconds,Action work)=>work();
        public static void HostReportPodEmpty(ulong s,uint t)=>Emptied++;
        public static OrdnanceClaimable AddClaimable(ulong s,uint t,UnityEngine.Vector3 position,float openAfter,ClaimableSpec spec)=>OrdnanceClaims.Add(s,t,spec,null,0,position,openAfter);
        public static void HostOpenClaim(ulong s,uint t)=>OrdnanceClaims.HostOpen(s,t);
        public static bool TryPodWeaponBay(UnityEngine.Vector3 p,UnityEngine.Quaternion q,out OrdnancePodBay bay){bay=default;return false;}
        public static UnityEngine.Quaternion PodRotation(UnityEngine.Quaternion q)=>q;
    }
    public interface IOrdnancePayload { }
    internal static class Plugin
    {
        internal const string Id="NAinfini.ForgeOrdnance";
        internal static readonly List<string> Errors=new();
        internal static void Error(string message)=>Errors.Add(message);
    }
    /// <summary>Records what the claim code sends; the host applies its own state locally, so a single peer plays both roles.</summary>
    internal static class OrdnanceNetwork
    {
        internal static readonly List<ClaimStateMessage> States=new();
        internal static readonly List<ClaimMessage> Requests=new();
        internal static void Reset(){ States.Clear(); Requests.Clear(); }
        internal static void Broadcast<T>(string name,T message) where T:unmanaged,ForgeCore.INetEpoch { if(message is ClaimStateMessage state) States.Add(state); }
        internal static void ToHost<T>(string name,T message) where T:unmanaged,ForgeCore.INetEpoch { if(message is ClaimMessage request) Requests.Add(request); }
    }

    public static class OrdnanceApi
    {
        public static readonly List<(byte Tag,byte[] Data)> Sent=new();
        public static Func<string,bool> Registration = _ => true;
        public static event Action? DefinitionsReady { add { } remove { } }
        public static bool RegisterPayload(string type,IOrdnancePayload payload)=>Registration(type);
        public static int Cleared;
        public static void SendData(string type,ulong s,uint t,byte tag,byte[] data)
        {
            Sent.Add((tag,data));
            var message = new OrdnancePayloadData {SummonerLookup=s,ThrowId=t,Tag=tag,Data=data};
            if (type == HeavyWeaponSpec.TypeName) HeavyWeapons.OnData(message);
            else StrikeVisuals.Receive(message);
        }
        public static bool RetainState = true;
        public static bool SendState(string type,ulong s,uint t,byte tag,byte[] data)
        { SendData(type,s,t,tag,data); return RetainState; }
        public static void ClearState(string type,ulong s,uint t)=>Cleared++;
        public static string DisplayName(OrdnanceDefinition definition)=>definition.Name;
    }
}
namespace ForgeOrdnanceArsenal
{
    internal sealed record HeavyParameters(HeavyWeaponSpec Spec,uint GearId);
    internal static class ArsenalArt
    {
        internal static object? Bundle = null;
        internal static void Register(){}
        internal static void Load(){}
        internal static string Failure=>"";
        internal static UnityEngine.Quaternion FrontToForward=>default;
        internal static UnityEngine.Quaternion NoseDown=>default;
        internal static ForgeVisual.ModelHandle? Bomblet => new();
        internal static ForgeVisual.ModelHandle? Weapon(string id)=>new();
        internal static ForgeVisual.ModelHandle? Look(ShellLook look)=>new();
    }
    internal static class ArsenalAudio
    {
        internal enum Cue { Incoming,GasRelease,StasisPulse,BigBoom,SmallBoom,Debris,ClusterSplit,FireIgnite,GasLoop,StasisLoop,FireLoop,BeamStart,BeamLoop,BeamCutting,BeamEnd,Handover,GrenadeFire,GrenadeReload,GrenadeBurst,WeaponReturn }
        internal static readonly List<Cue> PlayedAt = new();
        internal static readonly List<UnityEngine.Vector3> ExplosionAt = new();
        internal static void Register() { }
        internal static void Dispose() { }
        internal static void PlayAt(Cue cue,UnityEngine.Vector3 at)
        {
            PlayedAt.Add(cue);
            if (cue is Cue.BigBoom or Cue.SmallBoom or Cue.ClusterSplit or Cue.GrenadeBurst) ExplosionAt.Add(at);
        }
        internal static void PlayAttached(Cue cue,UnityEngine.GameObject owner,int dimension,bool isSelf=false) => PlayedAt.Add(cue);
        internal static void PlayLoop(Cue cue,UnityEngine.GameObject owner) => PlayedAt.Add(cue);
        internal static void PlayReturn(UnityEngine.GameObject owner,int dimension) => PlayedAt.Add(Cue.WeaponReturn);
    }
    internal sealed class HeavyWeaponPayload : ForgeOrdnance.IOrdnancePayload { }
    // The drawing (StrikeEffects) and slowing (StasisSlow) need Unity's renderer, animator and mesh APIs, and ForgeVisual's pools;
    // the lifecycle code that drives them is real. These record what it asked for.
    internal static class StrikeEffects
    {
        internal static readonly UnityEngine.Color BeamColour = new(1f, .22f, .12f);
        internal static int Clouds, BeamsMade, BeamsReleased;
        internal sealed class Beam { internal int Shown; internal void Show(UnityEngine.Vector3 from, UnityEngine.Vector3 to, float now) => Shown++; internal void Release() => BeamsReleased++; }
        internal sealed class Cloud { internal int Ticks, Releases; internal void Tick(float now) => Ticks++; internal void Release() => Releases++; }
        internal static Beam? CreateBeam(UnityEngine.Color colour, float now) { BeamsMade++; return new(); }
        internal static Cloud? CreateCloud(StrikeKind kind, UnityEngine.Vector3 centre, float radius, UnityEngine.Color colour, float start, float ends, uint seed) { Clouds++; return new(); }
    }
    // The blast and trail effects (StrikeFx) draw sprites in ForgeVisual's pool with Unity's ray APIs; the lifecycle code that calls them is
    // real. This records what it asked for, in order, as short names.
    internal static class StrikeBursts
    {
        internal static readonly List<string> Calls = new();
        internal static readonly List<float> BurstFlights = new();
        internal static readonly List<UnityEngine.Vector3> BurstPoints = new();
        internal static int Trails, EndedTrails, Moves;
        internal static readonly List<UnityEngine.Vector3> Centres = new();
        internal static void ClusterCentre(UnityEngine.Vector3 at, float blast, float now) { Calls.Add("cluster-centre"); Centres.Add(at); }
        internal static void Reset() { Calls.Clear(); BurstFlights.Clear(); BurstPoints.Clear(); Centres.Clear(); Trails = EndedTrails = Moves = 0; }
        internal static void Tick(float now) { }
        internal static void Clear() => Calls.Add("clear");
        internal static void EnterLevel() { }
        internal static void LeaveLevel() { }
        internal static int BeginTrail(ShellLook look, StrikeKind kind, UnityEngine.Vector3 from, float now)
        {
            if (look is ShellLook.Canister or ShellLook.Emitter) return -1;
            Calls.Add("trail-" + kind);
            return Trails++;
        }
        internal static void MoveTrail(int slot, UnityEngine.Vector3 position, float progress, float now) => Moves++;
        internal static void EndTrail(int slot) { if (slot >= 0) EndedTrails++; }
        internal static void Blast(StrikeKind kind, UnityEngine.Vector3 at, float radius, float now) => Calls.Add("blast-" + kind);
        internal static void ClusterBurst(ulong summoner, uint throwId, UnityEngine.Vector3 burst, UnityEngine.Vector3 impact, float blast, float flight, float now)
        { Calls.Add("burst"); BurstFlights.Add(flight); BurstPoints.Add(burst); }
        internal static void GasBurst(UnityEngine.Vector3 at, float now) => Calls.Add("gas-burst");
        internal static void StasisPulse(UnityEngine.Vector3 centre, float radius, float now) => Calls.Add("stasis-pulse");
        internal static void BeamCharge(UnityEngine.Vector3 at, UnityEngine.Color colour, float now) => Calls.Add("beam-charge");
        internal static void BeamMark(UnityEngine.Vector3 at, UnityEngine.Color colour, float now) => Calls.Add("beam-mark");
        internal static void ReturnGlow(Player.PlayerAgent agent, float now) => Calls.Add("return-glow");
    }
    internal static class StasisSlow
    {
        internal sealed record Field(ulong Summoner, uint ThrowId, float Radius, float Factor);
        internal static readonly List<Field> Live = new();
        internal static int Forgotten, Cleared;
        internal static void Add(ulong s, uint t, UnityEngine.Vector3 centre, float radius, float factor, float seconds) { Live.RemoveAll(f => f.Summoner == s && f.ThrowId == t); Live.Add(new(s, t, radius, factor)); }
        internal static void Remove(ulong s, uint t) => Live.RemoveAll(f => f.Summoner == s && f.ThrowId == t);
        internal static void Tick(float now) { }
        internal static void Forget(Enemies.EnemyAgent enemy) => Forgotten++;
        internal static void Clear() { Live.Clear(); Cleared++; }
    }
    internal static class ArsenalTurrets { internal static void Clear() { } }

}
namespace ForgeAudio { public static class AudioKit { public const string PluginId="NAinfini.ForgeAudio"; } }
namespace ForgeOrdnanceArsenal
{
    /// <summary>The shipped English texts (<c>lang/en.json</c>), so prompts read as the player sees them.</summary>
    internal static class ArsenalText
    {
        private static Dictionary<string,string>? _english;
        private static Dictionary<string,string> English([System.Runtime.CompilerServices.CallerFilePath] string here="") => _english ??=
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(System.IO.File.ReadAllText(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!,"../../lang/en.json")))!;
        internal static int Version => 0;
        internal static string T(string key) => English().TryGetValue(key,out var text) ? text : key;
        internal static string T(string key,params object?[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture,T(key),args);
        internal static string CallIn(string id) => id;
        internal static int Named;
        internal static void NameHeavyWeapons() => Named++;
    }
}
namespace ForgeUI
{
    public static class UIKit
    {
        public const string PluginId="NAinfini.ForgeUI";
        public static UIKitMod Mod(string modId) => new();
    }
    public sealed class UIKitMod
    {
        public InteractPrompt? AddInteractPrompt(string name) => new();
        public KeyAction? AddKeyAction(string name,string nameKey,string groupKey,string defaultKey,KeyMode mode=KeyMode.Press,string context="") => new();
    }
    public enum KeyMode { Press, Hold, Toggle }
    /// <summary>ForgeUI's key action: pressed once after a test sets <see cref="UnityEngine.Input.Drop"/>.</summary>
    public sealed class KeyAction
    {
        public bool Pressed { get { var b=UnityEngine.Input.Drop; UnityEngine.Input.Drop=false; return b; } }
    }
    /// <summary>Records what ForgeUI's prompt would put on the game's interaction layer.</summary>
    public sealed class InteractPrompt
    {
        public bool IsShown { get; private set; }
        public void Show(string text,string key="") { GuiManager.InteractionLayer?.Prompts.Add(text); IsShown=true; }
        public void Hide() { if (!IsShown) return; IsShown=false; if (GuiManager.InteractionLayer!=null) GuiManager.InteractionLayer.InteractPromptVisible=false; }
    }
}

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)] public class BepInPluginAttribute : Attribute { public BepInPluginAttribute(string id,string name,string version){} }
    [AttributeUsage(AttributeTargets.Class,AllowMultiple=true)] public class BepInDependencyAttribute : Attribute { public BepInDependencyAttribute(string id){} public BepInDependencyAttribute(string id,string versions){} }
}
namespace BepInEx.Configuration
{
    public class ConfigEntry<T> { public T Value; public ConfigEntry(T value)=>Value=value; }
    public class ConfigFile { public ConfigEntry<T> Bind<T>(string section,string key,T value,string description)=>new(value); }
}
namespace BepInEx.Logging
{
    public class ManualLogSource { public readonly List<string> Lines=new(); public void LogError(string s)=>Lines.Add(s); public void LogInfo(string s)=>Lines.Add(s); }
}
namespace BepInEx.Unity.IL2CPP
{
    public class BasePlugin
    {
        public BepInEx.Logging.ManualLogSource Log=new();
        public BepInEx.Configuration.ConfigFile Config=new();

        public virtual void Load(){} public virtual bool Unload()=>false;
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public class HarmonyPatch : Attribute { public HarmonyPatch(Type type,string method){} }
    [AttributeUsage(AttributeTargets.Method)] public class HarmonyPostfix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public class HarmonyPrefix : Attribute { }
    public class Harmony { public Harmony(string id){} public void PatchAll(System.Reflection.Assembly a){} public void UnpatchSelf(){} }
}
namespace GTFO.API
{
    public static class NetworkAPI
    {
        public static readonly Dictionary<string,Delegate> Handlers = new();
        public static readonly List<ForgeOrdnanceArsenal.GrenadeShot> BroadcastShots = new(), RequestedShots = new();
        public static readonly List<ForgeOrdnanceArsenal.GrenadeBurst> BroadcastBursts = new();
        public static void RegisterEvent<T>(string name,Action<ulong,T> handler) where T:unmanaged => Handlers[name]=handler;
        public static void Deliver<T>(string name,ulong sender,T message) where T:unmanaged => ((Action<ulong,T>)Handlers[name])(sender,message);
        public static void InvokeEvent<T>(string name,T message,SNetwork.SNet_ChannelType channel) where T:unmanaged
        {
            if(message is ForgeOrdnanceArsenal.GrenadeShot shot) BroadcastShots.Add(shot);
            if(message is ForgeOrdnanceArsenal.GrenadeBurst burst) BroadcastBursts.Add(burst);
        }
        public static void InvokeEvent<T>(string name,T message,SNetwork.SNet_Player player,SNetwork.SNet_ChannelType channel) where T:unmanaged
        {
            if(message is ForgeOrdnanceArsenal.GrenadeShot shot) RequestedShots.Add(shot);
        }
    }
    public static class LevelAPI
    {
        public static event Action? OnBuildDone,OnEnterLevel;
        public static void BuildDone()=>OnBuildDone?.Invoke(); public static void Enter()=>OnEnterLevel?.Invoke();
        public static void Reset() { OnBuildDone=null; OnEnterLevel=null; ForgeCore.GameEvents.Reset(); }
    }
    public static class EventAPI
    {
        public static event Action? OnCheckpointReloaded,OnCheckpointReached;
        public static void Reload()=>OnCheckpointReloaded?.Invoke(); public static void Capture()=>OnCheckpointReached?.Invoke();
        public static void Reset() { OnCheckpointReloaded=null; OnCheckpointReached=null; }
    }
}

public enum eWeaponFireMode { Semi, Burst, Auto, SemiBurst }
public class ArchetypeDataBlock : GameData.GameDataBlockBase<ArchetypeDataBlock>
{
    public float ShotDelay { get; set; } = .5f;
    public float CostOfBullet { get; set; } = 1;
    public UnityEngine.Vector2 DamageFalloff { get; set; } = new(40f, 80f);
    public float SpecialChargetupTime { get; set; }
    public float Damage { get; set; } = 1;
    public float SpecialCooldownTime { get; set; }
    public float DefaultReloadTime { get; set; } = 2;
    public float PrecisionDamageMulti { get; set; } = 1;
    public float StaggerDamageMulti { get; set; } = 1;
    public bool PiercingBullets { get; set; }
    public int PiercingDamageCountLimit { get; set; }
    public int DefaultClipSize { get; set; } = 10;
}
namespace GameData
{
    public class ItemDataBlock { public InventorySlot inventorySlot = InventorySlot.GearSpecial; }
    public class GearCategoryDataBlock : GameDataBlockBase<GearCategoryDataBlock>
    {
        public uint SemiArchetype=2, BurstArchetype=2, AutoArchetype=2, SemiBurstArchetype=2;
    }
}
public static class LayerManager { public const int MASK_WORLD=1, MASK_ENEMY_DAMAGABLE=2, MASK_EXPLOSION_TARGETS=4, MASK_EXPLOSION_BLOCKERS=8; }
namespace Enemies
{
    public class EnemyAgent : UnityEngine.Component, IDamageable
    {
        public bool Alive=true;
        public UnityEngine.Vector3 Position;
        public UnityEngine.Transform? AimTarget;
        public EnemyDamage Damage = new();
        public EnemyData EnemyData = new();
        public EnemyAgent GetBaseAgent()=>this;
        public void Setup(){} public void OnDead(){}
    }
    public class EnemyData { public string name="enemy"; }
    public class EnemyDamage
    {
        public float HealthMax=100, Health=100;
        public int Burns, Bullets;
        public void FireDamage(float damage,Player.PlayerAgent? source) { Burns++; }
        public void ExplosionDamage(float damage,UnityEngine.Vector3 p,UnityEngine.Vector3 force){}
        public void BulletDamage(float damage,Player.PlayerAgent? source,UnityEngine.Vector3 p,UnityEngine.Vector3 direction,UnityEngine.Vector3 normal){ Bullets++; }
    }
}

public interface IDamageable { Enemies.EnemyAgent? GetBaseAgent(); }
public static class DamageUtil
{
    public static int Explosions;
    public static float Damage,Radius;
    public static void DoExplosionDamage(UnityEngine.Vector3 at,float radius,float damage,int targets,int blockers,bool force,float strength)
    { Explosions++; Damage=damage; Radius=radius; }
}
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public class Il2CppReferenceArray<T>
    {
        private readonly T[] Items;
        public Il2CppReferenceArray(int n) { Items=new T[n]; }
        public int Length=>Items.Length;
        public T this[int i] { get=>Items[i]; set=>Items[i]=value; }
    }
}
// ForgeVisual's models, dressing and effects by name; the real ones draw with the game's renderers. These record what Arsenal asked for.
namespace ForgeVisual
{
    public sealed class ModelHandle
    {
        public UnityEngine.GameObject Spawn(UnityEngine.Vector3 p, UnityEngine.Quaternion q, UnityEngine.Transform? parent = null)
        {
            var root = new UnityEngine.GameObject(); root.transform.position = p; if (parent != null) root.transform.SetParent(parent, false);
            return root;
        }
    }
    public sealed class DressOptions
    {
        public bool HideNative { get; set; } = true;
        public bool FitLength { get; set; }
        public float MaxScale { get; set; } = 2f;
        public bool Layer { get; set; } = true;
        public UnityEngine.Quaternion Rotation { get; set; }
        public string? Name { get; set; }
        public GunNodes? Gun { get; set; }
    }
    public sealed class GunNodes
    {
        public UnityEngine.Transform? RightHand { get; set; }
        public UnityEngine.Transform? LeftHand { get; set; }
        public UnityEngine.Transform? Muzzle { get; set; }
        public UnityEngine.Transform? Magazine { get; set; }
    }
    public sealed class Dressed
    {
        public UnityEngine.GameObject Native = null!, Look = null!;
        /// <summary>Game meshes the next <see cref="HideLate"/> finds (parts the game added after dressing).</summary>
        public int Late;
        public bool Undone;
        public int Hidden => 0;
        public float Scale => 1f;
        public bool Alive => !Native.WasCollected && !Look.WasCollected;
        public int HideLate() { var late = Late; Late = 0; return late; }
        public void Undo() { Undone = true; UnityEngine.Object.Destroy(Look); }
    }
    public static class Models
    {
        public static readonly List<(UnityEngine.GameObject Native, DressOptions Options)> Dresses = new();
        public static readonly List<Dressed> Looks = new();
        public static bool Measurable = true;
        public static UnityEngine.Transform? Find(UnityEngine.Transform root, string name)
        {
            if (root.gameObject.name == name) return root;
            for (var i = 0; i < root.childCount; i++) if (Find(root.GetChild(i), name) is { } found) return found;
            return null;
        }
        public static bool NativeBounds(UnityEngine.Transform root, UnityEngine.Transform? skip, out UnityEngine.Bounds bounds) { bounds = default; return Measurable; }
        public static Dressed? Dress(UnityEngine.GameObject native, ModelHandle look, DressOptions options)
        {
            Dresses.Add((native, options));
            var instance = look.Spawn(native.transform.position, native.transform.rotation, native.transform);
            instance.name = options.Name ?? "";
            // Every heavy weapon model has its optic's aim socket.
            new UnityEngine.GameObject("SightLook").transform.SetParent(instance.transform, false);
            var dressed = new Dressed { Native = native, Look = instance };
            Looks.Add(dressed);
            return dressed;
        }
    }
    public readonly struct ShakeSpec
    {
        public readonly float Amplitude;
        public ShakeSpec(float amplitude, float range, float seconds = 0.7f, float frequency = 18f, float inner = 0f) => Amplitude = amplitude;
    }
    public static class CameraShake
    {
        public static readonly List<(UnityEngine.Vector3 At, float Amplitude)> Shakes = new();
        public static float Shake(UnityEngine.Vector3 at, in ShakeSpec spec) { Shakes.Add((at, spec.Amplitude)); return spec.Amplitude; }
    }
    public readonly struct PointLightSpec
    {
        public readonly UnityEngine.Color Color;
        public readonly float Range, Intensity;
        public PointLightSpec(UnityEngine.Color color, float range, float intensity, float life = 0f, float flicker = 0.1f) { Color = color; Range = range; Intensity = intensity; }
    }
    /// <summary>The shared effect lights: which handles are held now, where and how strong.</summary>
    public static class PointLights
    {
        public static readonly Dictionary<int, (UnityEngine.Vector3 At, float Strength)> Held = new();
        private static int _next;
        public static void Hold(ref int handle, UnityEngine.Vector3 position, in PointLightSpec spec, float now, float strength = 1f)
        {
            if (handle == 0) handle = ++_next;
            Held[handle] = (position, strength);
        }
        public static void Release(ref int handle) { Held.Remove(handle); handle = 0; }
    }
    public enum FxResult { Played, Unavailable, Unsupported }
    public sealed class FxHandle { }
    public static class Fx
    {
        public static readonly List<(string Name, UnityEngine.Vector3 At)> Played = new();
        public static FxHandle? Find(string name) => new();
        public static FxResult Play(FxHandle fx, UnityEngine.Vector3 position, UnityEngine.Vector3 direction) { Played.Add(("", position)); return FxResult.Played; }
    }
}
namespace ForgeCore { public static partial class LoanedGun { public static void FinishParts(global::Player.BackpackItem item) { } private static void BeginSwap() { } public static void EndSwap() { } } }
