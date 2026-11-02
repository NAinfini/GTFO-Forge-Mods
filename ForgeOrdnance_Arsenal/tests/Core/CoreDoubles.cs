// ForgeCore as Arsenal calls it. ForgeCore's own behaviour (channels, the nearby-enemy query, gear preparation) is tested in
// Mods/ForgeCore/tests; these doubles pass Arsenal's calls to the recording game doubles in NativeDoubles.cs.
using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeCore
{
    public sealed class NetChannel
    {
        private readonly Queue<Action> _inbox = new();
        public void On<T>(string name, Action<ulong, T> handler) where T : unmanaged
            => GTFO.API.NetworkAPI.RegisterEvent<T>(name, (sender, message) => _inbox.Enqueue(() => handler(sender, message)));
        public void ToHost<T>(string name, T message) where T : unmanaged
            => GTFO.API.NetworkAPI.InvokeEvent(name, message, SNetwork.SNet.Master ?? throw new InvalidOperationException("No host."), SNetwork.SNet_ChannelType.GameOrderCritical);
        public bool TryToHost<T>(string name, T message) where T : unmanaged { if (SNetwork.SNet.Master == null) return false; ToHost(name, message); return true; }
        public void Broadcast<T>(string name, T message) where T : unmanaged
            => GTFO.API.NetworkAPI.InvokeEvent(name, message, SNetwork.SNet_ChannelType.GameOrderCritical);
        public void Pump() { while (_inbox.Count != 0) _inbox.Dequeue()(); }
        public void Clear() => _inbox.Clear();
    }

    /// <summary>ForgeCore's game events as Arsenal subscribes to them; the tests raise them by hand.</summary>
    public static class GameEvents
    {
        public static event Action? MasterChanged, LevelEnded;
        public static void RaiseMaster() => MasterChanged?.Invoke();
        public static void RaiseLevelEnded() => LevelEnded?.Invoke();
        public static void Reset() { MasterChanged = null; LevelEnded = null; }
    }

    /// <summary>ForgeCore's one patch of the game's firing as Arsenal subscribes to it; the tests call Grenades.BeforeFire/AfterFire directly.</summary>
    public static class GameHooks
    {
        public static class Fire
        {
            public static readonly Bracket<global::Gear.BulletWeapon> BulletWeapon = new();
        }
        public static readonly Bracket<BulletHitCall> BulletHit = new();
        public static readonly Bracket<global::Gear.ItemEquippable> ReloadTriggered = new();
    }

    public sealed class BulletHitCall { public void Skip(bool result = false) { } }

    public sealed class Bracket<TCall>
    {
        public void Before(Action<TCall> before) { }
        public void After(Action<TCall, bool> after) { }
        public void Around<TState>(Func<TCall, TState> before, Action<TCall, TState, bool>? after, Action<TCall, TState, Exception>? faulted = null) { }
    }

    /// <summary>ForgeCore's checkpoint settling as Arsenal reads it; the tests set whether the game is recalling.</summary>
    public static class Checkpoint
    {
        public static bool Recalling;
        public static bool Settled => !Recalling;
    }

    public static class Net
    {
        public static NetChannel Open(string modId, int capacity = 512) => new NetChannel();
    }

    public static class Enemies
    {
        /// <summary>Each living owner of <c>Physics.Hits</c> once.</summary>
        public static ReadOnlySpan<global::Enemies.EnemyAgent?> Near(UnityEngine.Vector3 position, float radius)
            => UnityEngine.Physics.Hits.Select(c => c.Owner).Where(e => e != null && e.Alive).Distinct().ToArray();
    }

    public static class Combat
    {
        public static void Explode(UnityEngine.Vector3 position, float radius, float damage, float force)
        {
            if (damage <= 0f) return;
            DamageUtil.DoExplosionDamage(position, radius, damage, LayerManager.MASK_EXPLOSION_TARGETS, LayerManager.MASK_EXPLOSION_BLOCKERS, force > 0f, force);
        }
        public static void Fire(global::Enemies.EnemyDamage receiver, float damage, Player.PlayerAgent? source) => receiver.FireDamage(damage, source);
        public static DamageSettlement ExplosionDamage(global::Enemies.EnemyDamage receiver, float damage, UnityEngine.Vector3 position, UnityEngine.Vector3 force)
        {
            var before = receiver.Health;
            receiver.ExplosionDamage(damage, position, force);
            return new(before, receiver.Health);
        }
        public static DamageSettlement BulletDamage(global::Enemies.EnemyDamage receiver, float damage, Player.PlayerAgent? source, UnityEngine.Vector3 position, UnityEngine.Vector3 direction, UnityEngine.Vector3 normal)
        {
            var before = receiver.Health;
            receiver.BulletDamage(damage, source, position, direction, normal);
            return new(before, receiver.Health);
        }
    }

    public readonly record struct DamageSettlement(float HealthBefore, float HealthAfter) { public float Dealt => HealthBefore - HealthAfter; }

    /// <summary>The level as the beam sees it (ForgeCore's own queries are tested in Mods/ForgeCore/tests). By default every target is on a short
    /// straight path, in sight, with no wall in the way; tests make a target unwalkable or hidden (by its position), or raise a wall across a move.</summary>
    public static class Level
    {
        public static Func<UnityEngine.Vector3, bool> Walkable = _ => true;
        public static Func<UnityEngine.Vector3, bool> Sight = _ => true;
        public static Func<UnityEngine.Vector3, UnityEngine.Vector3, bool> Wall = (_, _) => false;
        public static int Searches;
        public static Player.PlayerAgent? PlayerByLookup(ulong lookup) => Player.PlayerManager.PlayerAgentsInLevel.FirstOrDefault(a => a != null && !a.WasCollected && a.Owner?.Lookup == lookup);
        public static bool FloorBelow(UnityEngine.Vector3 from, float distance, out UnityEngine.RaycastHit hit)
            => UnityEngine.Physics.Raycast(from, UnityEngine.Vector3.down, out hit, distance, LayerManager.MASK_WORLD, UnityEngine.QueryTriggerInteraction.Ignore);
        public static void Reset() { Walkable = _ => true; Sight = _ => true; Wall = (_, _) => false; Searches = 0; }
        public static bool TryRoute(UnityEngine.Vector3 from, UnityEngine.Vector3 to, float maxLength, Span<UnityEngine.Vector3> corners, out int count)
        {
            Searches++;
            count = 0;
            if (!Walkable(to)) return false;
            var dx = to.x - from.x; var dz = to.z - from.z;
            if (MathF.Sqrt(dx * dx + dz * dz) > maxLength || corners.Length < 1) return false;
            corners[0] = to;
            count = 1;
            return true;
        }
        public static bool HasSight(UnityEngine.Vector3 floorPoint, UnityEngine.Vector3 aim) => Sight(aim);
        public static bool Blocked(UnityEngine.Vector3 from, UnityEngine.Vector3 to) => Wall(from, to);
    }

    public enum GearAssetStatus { Ready, Loading, Failed }
    public readonly record struct GearAssetReading(GearAssetStatus Status, string Detail = "");

    public static class Gear
    {
        /// <summary>The real slot rules (compiled in from ForgeCore's Core); the game hooks are not part of the test.</summary>
        public static SpecialSlot SpecialSlot => SpecialSlot.Local;
        /// <summary>What preparation answers; a test sets Failed to stand for a gun whose effects the level did not load.</summary>
        public static GearAssetStatus Status = GearAssetStatus.Ready;
        public static GearAssetReading Prepare(global::Gear.GearIDRange gear) => new(Status, "test");
        public static GearAssetReading EquipLocal(global::Gear.GearIDRange gear)
        {
            if (Status == GearAssetStatus.Ready) Player.PlayerBackpackManager.EquipLocalGear(gear);
            return new(Status, "test");
        }
        public static bool WriteLocalAmmo(Player.AmmoType type, global::Gear.BulletWeapon weapon, float reserve, int magazine)
        {
            Player.PlayerBackpackManager.LocalBackpack.AmmoStorage.SpecialAmmo.AmmoInPack = reserve;
            weapon.SetCurrentClip(magazine);
            return true;
        }
    }
}
