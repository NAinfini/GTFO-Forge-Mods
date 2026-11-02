// Offline doubles for the game's damage entries, so ForgeCore's real Native/Combat.cs and Native/CombatKills.cs run. Every entry
// records what it was called with; health moves only where a test makes the double's receiver "settle".
using AIGraph;
using Agents;

public enum DamageNoiseLevel { Normal, Quiet }
public enum NM_NoiseType { Detectable, InstaDetect }

public class Weapon { public class WeaponHitData { public int Marker; } }

public static class DamageUtil
{
    public enum ExplosionDamageType { Damage, Heal }
    public static readonly List<string> Calls = new();
    public static void DoExplosionDamage(UnityEngine.Vector3 position, float radius, float damage, int targets, int blockers, bool applyForce, float force)
        => Calls.Add($"explosion r={radius} d={damage} t={targets} b={blockers} f={applyForce}/{force}");
    public static void DoExplosionDamage_Capsule(UnityEngine.Vector3 a, UnityEngine.Vector3 b, float ra, float rb, float rc, float rd, float damage, int targets,
        int blockers, bool applyForce, float force, object? ignored, ExplosionDamageType type, uint sourceId)
        => Calls.Add($"capsule r={ra} d={damage} t={targets} b={blockers} f={applyForce}/{force} type={type} source={sourceId}");
}

public class NM_NoiseData
{
    public object? noiseMaker;
    public AIG_CourseNode? node;
    public UnityEngine.Vector3 position;
    public float radiusMin, radiusMax, yScale;
    public NM_NoiseType type;
    public bool includeToNeightbourAreas, raycastFirstNode;
}

public static class NoiseManager
{
    public static readonly List<NM_NoiseData> Made = new();
    public static void MakeNoise(NM_NoiseData data) => Made.Add(data);
}

public class Dam_SyncedDamageBase
{
    public float Health = 100f;
    /// <summary>What the next request takes from the receiver's health: the double's stand-in for the game settling it.</summary>
    public float Settles;
    public readonly List<string> Calls = new();
    protected void Settle() => Health -= Settles;
    public void FireDamage(float damage, Agent? source) { Calls.Add($"fire {damage}"); Settle(); }
    public void PushDamage(float dam, Agent? source, UnityEngine.Vector3 position, UnityEngine.Vector3 direction)
    { Calls.Add($"push {dam} from={source != null} at={position} dir={direction}"); Settle(); }
    public void ExplosionDamage(float damage, UnityEngine.Vector3 position, UnityEngine.Vector3 force)
    { Calls.Add($"explosion {damage} at={position} force={force}"); Settle(); }
    public void BulletDamage(float damage, Agent? source, UnityEngine.Vector3 position, UnityEngine.Vector3 direction, UnityEngine.Vector3 normal)
    { Calls.Add($"bullet {damage} from={source != null} at={position} dir={direction} normal={normal}"); Settle(); }
}

public class Dam_EnemyDamageBase : Dam_SyncedDamageBase
{
    public Enemies.EnemyAgent? Owner;
    public void MeleeDamage(float dam, Agent? source, UnityEngine.Vector3 position, UnityEngine.Vector3 direction, float stagger, float a, float b, float c, float d,
        bool directional, DamageNoiseLevel noise, uint gear)
    { Calls.Add($"melee {dam} stagger={stagger} multi={a}/{b}/{c}/{d} directional={directional} noise={noise} gear={gear}"); Settle(); }
    public void MeleeDamage(float dam, Agent? source, UnityEngine.Vector3 position, UnityEngine.Vector3 direction, int limb, float stagger, float precision)
    { Calls.Add($"melee-limb {dam} limb={limb} stagger={stagger} precision={precision}"); Settle(); }
    public void BulletDamage(float dam, Agent? source, UnityEngine.Vector3 position, UnityEngine.Vector3 direction, UnityEngine.Vector3 normal, bool directional,
        int limb, float stagger, float precision, uint gear)
    { Calls.Add($"bullet-limb {dam} limb={limb} directional={directional} stagger={stagger} precision={precision} gear={gear}"); Settle(); }
    public void ExplosionDamage(float dam, UnityEngine.Vector3 position, UnityEngine.Vector3 force, int limb)
    { Calls.Add($"explosion-limb {dam} limb={limb}"); Settle(); }
    public void SendExplosionForce(UnityEngine.Vector3 origin, float magnitude) => Calls.Add($"force {magnitude} from={origin}");
    public void ReceiveBulletDamage(pBulletDamageData data) { }
}

public class Dam_EnemyDamageLimb
{
    public readonly List<string> Calls = new();
    public void BulletDamage(float dam, Agent? source, UnityEngine.Vector3 position, UnityEngine.Vector3 direction, UnityEngine.Vector3 normal, bool directional,
        float stagger, float precision, uint gear)
        => Calls.Add($"limb-bullet {dam} directional={directional} stagger={stagger} precision={precision} gear={gear}");
}

namespace Player
{
    public class Dam_PlayerDamageBase : Dam_SyncedDamageBase
    {
        public void MeleeDamage(float damage, Agent? source, UnityEngine.Vector3 position, UnityEngine.Vector3 direction) { Calls.Add($"player-melee {damage}"); Settle(); }
        public void TentacleAttackDamage(float damage, Agent? source, UnityEngine.Vector3 position) { Calls.Add($"tentacle {damage}"); Settle(); }
        public void ShooterProjectileDamage(float damage, UnityEngine.Vector3 position) { Calls.Add($"projectile {damage}"); Settle(); }
        public void FallDamage(float damage) { Calls.Add($"fall {damage}"); Settle(); }
    }
}

public struct pAgentRef
{
    public Agent? Value;
    public bool TryGet(out Agent agent) { agent = Value!; return Value != null; }
}
public struct pBulletDamageData { public pAgentRef source; }

namespace SNetwork { public static class SNet { public static bool IsMaster = true; } }

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Method)] public class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
}

namespace Gear
{
    public class BulletWeapon
    {
        public static readonly List<Weapon.WeaponHitData> Hits = new();
        public static bool Answer = true;
        public static bool BulletHit(Weapon.WeaponHitData hit, bool a, float b, uint c, bool d) { Hits.Add(hit); return Answer; }
    }
}
