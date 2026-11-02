using System;
using Agents;
using Enemies;
using Player;
using SNetwork;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeCore;

/// <summary>Takes the game's own agents out of the game's hands: <see cref="Enemy"/> and <see cref="Player"/> hold an enemy or a player
/// agent as a puppet whose position, facing and walk animation come only from the caller, and <see cref="EnemyPuppet.Dispose"/> /
/// <see cref="PlayerPuppet.Dispose"/> put back everything that was changed. Nothing here knows why a puppet is wanted (replays, cut scenes,
/// demonstrations). Main thread only.</summary>
public static class Puppets
{
    /// <summary>Holds <paramref name="enemy"/> as a puppet: its AI is switched off, its nav mesh agent and root motion are disabled, it cannot be
    /// killed by damage (its damage limbs stay, so it can still be hit and aimed at), and the game may not cull its animator. Null for an enemy
    /// that is dead or collected. <paramref name="owner"/> names the caller in error lines.</summary>
    public static EnemyPuppet? Enemy(EnemyAgent enemy, string owner)
    {
        if (enemy == null || enemy.WasCollected || !enemy.Alive || enemy.AI == null || enemy.Anim == null) return null;
        return new EnemyPuppet(enemy, owner);
    }

    /// <summary>Opens up to <paramref name="count"/> empty player slots to bots (<c>SlotPermission.Bot</c>); the game fills them with its own bots
    /// and spawns their player agents. Returns how many slots were opened. Host only.</summary>
    public static int OpenBotSlots(int count)
    {
        var opened = 0;
        for (var i = 0; i < SNet.Slots.PlayerSlots.Length && opened < count; i++)
            if (SNet.Slots.GetPlayerInSlot(i) == null && SNet.Slots.SetSlotPermission(i, SNet_PlayerSlotManager.SlotPermission.Bot)) opened++;
        return opened;
    }

    /// <summary>Holds <paramref name="agent"/> (a player agent that is not the local player: a bot or a remote player) as a puppet. The game's
    /// bot AI is not part of this: <c>PlayerManager.DisableBotAI</c> is the game's own switch and is thrown by <see cref="Player"/> once per
    /// level, and stays off until the level ends.</summary>
    public static PlayerPuppet? Player(PlayerAgent agent, string owner)
    {
        if (agent == null || agent.WasCollected || agent.Locomotion == null || agent.IsLocallyOwned) return null;
        return new PlayerPuppet(agent, owner);
    }
}

/// <summary>An enemy held by <see cref="Puppets.Enemy"/>.</summary>
public sealed class EnemyPuppet : IDisposable
{
    private readonly string _owner;
    private readonly AgentMode _mode;
    private readonly bool _rootMotion, _immortal, _noCulling;
    private readonly float _speed;
    private readonly NavMeshAgent? _nav;
    private readonly bool _navEnabled;
    private bool _disposed;

    internal EnemyPuppet(EnemyAgent enemy, string owner)
    {
        Agent = enemy; _owner = owner;
        _mode = enemy.AI.Mode; _rootMotion = enemy.Anim.applyRootMotion; _immortal = enemy.Damage.IsImortal;
        _noCulling = enemy.ForceNoAnimationCulling; _speed = enemy.Anim.speed;
        _nav = enemy.GetComponent<NavMeshAgent>(); _navEnabled = _nav != null && _nav.enabled;
        // The locomotion state decides which animator state the enemy is in: PathMove is the walk blend (not every enemy has StandStill).
        // It is changed while the AI is still as the game left it, then the game's hands come off.
        try { enemy.Locomotion.ChangeState(ES_StateEnum.PathMove); }
        catch (Exception error) { Log.For(owner).ErrorOnce("A puppet enemy could not leave its hibernation state: " + error); }
        Hold();
    }

    public EnemyAgent Agent { get; }

    /// <summary>The playback rate of its animation (1 normal, 0 frozen); the enemy's own speed returns on release.</summary>
    public float Rate { set { if (Held) Agent.Anim.speed = value; } }

    /// <summary>How many times the game had taken the enemy back (AI mode, nav mesh, root motion or immortality changed) and it was
    /// put under control again; each one also wrote an error line. Zero in a clean run.</summary>
    public int Retaken { get; private set; }

    /// <summary>True while the enemy exists, lives and has not been released.</summary>
    public bool Held => !_disposed && Agent != null && !Agent.WasCollected && Agent.Alive;

    private void Hold()
    {
        var enemy = Agent;
        enemy.AI.Mode = AgentMode.Off;
        enemy.AI.SetNavMeshAgent(false);
        if (_nav != null) _nav.enabled = false;
        enemy.Anim.applyRootMotion = false;
        enemy.Damage.IsImortal = true;
        enemy.ForceNoAnimationCulling = true;
    }

    // The game's attack states turn root motion on themselves, so while an action plays that is not the game taking the enemy back;
    // the position is written every frame anyway, and root motion is switched off again when the action ends.
    private bool Free() => Agent.AI.Mode != AgentMode.Off || (!_acting && Agent.Anim.applyRootMotion) || !Agent.Damage.IsImortal || (_nav != null && _nav.enabled);

    /// <summary>Puts the enemy at <paramref name="position"/> and <paramref name="rotation"/> and animates it as walking with
    /// <paramref name="velocity"/> (world metres per second). Call it after the game's animation each frame (<see cref="Frame.OnLateUpdate"/>),
    /// so the animator's own movement and the game's own writes of the inputs do not stay; the inputs are written as they are, not damped.</summary>
    public void Drive(Vector3 position, Quaternion rotation, Vector3 velocity)
    {
        if (!Held) return;
        if (Free())
        {
            Retaken++;
            Log.For(_owner).ErrorOnce("The game took a puppet enemy back (AI mode " + Agent.AI.Mode + "); it was put under control again.");
            Hold();
        }
        EndAction();
        if (!_acting) Agent.Anim.applyRootMotion = false;
        Agent.transform.SetPositionAndRotation(position, rotation);
        var facing = rotation * Vector3.forward;
        PuppetMotion.Local(velocity.x, velocity.z, facing.x, facing.z, out var forward, out var right);
        var anim = Agent.Anim;
        anim.SetBool(EnemyLocomotion.s_hashMoveOnGround, true);
        anim.SetFloat(EnemyLocomotion.s_hashForward, forward);
        anim.SetFloat(EnemyLocomotion.s_hashRight, right);
    }

    // An action's state is the game's own (the state a client puts a remote enemy in when told); it runs for a while and the enemy is then
    // sent back to walking, the way the host's next state change would.
    private float _actionEnds;
    private bool _acting;

    /// <summary>The enemy is playing an action started by <see cref="Attack"/>, <see cref="Hitreact"/> or <see cref="Scream"/>.</summary>
    public bool Acting => _acting;

    private bool Begin(ES_StateEnum state, float seconds)
    {
        try
        {
            Agent.Locomotion.ChangeState(state);
            _acting = true;
            _actionEnds = Time.unscaledTime + Mathf.Clamp(seconds, 0.3f, 6f);
            return true;
        }
        catch (Exception error)
        {
            Log.For(_owner).ErrorOnce("A puppet enemy could not enter " + state + ": " + error);
            return false;
        }
    }

    /// <summary>Plays an attack the way a client sees one: the attack state is entered and told the attack start (the animation index it
    /// plays and where it aims). <paramref name="target"/> is the player puppet the attack is aimed at (null: none).</summary>
    public void Attack(ES_StateEnum state, Vector3 aim, int animIndex, float seconds, PlayerAgent? target)
    {
        if (!Held || !Begin(state, seconds)) return;
        try
        {
            var locomotion = Agent.Locomotion;
            ES_EnemyAttackBase? attack = state switch
            {
                ES_StateEnum.StrikerAttack => locomotion.StrikerAttack,
                ES_StateEnum.TankAttack => locomotion.TankAttack,
                ES_StateEnum.TankMultiTargetAttack => locomotion.TankMultiTargetAttack,
                ES_StateEnum.ShooterAttack => locomotion.ShooterAttack,
                _ => null,
            };
            if (attack == null) return;
            var data = new pES_EnemyAttackData { Position = Agent.Position, TargetPosition = aim, AnimIndex = (byte)animIndex, Duration = seconds };
            if (target != null) data.TargetAgent.Set(target);
            attack.RecieveAttackStart(data);
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("A puppet enemy could not start its " + state + " attack: " + error); }
    }

    /// <summary>Plays a hit reaction (the game's reaction type, side and animation index).</summary>
    public void Hitreact(ES_HitreactType type, ImpactDirection direction, int animIndex)
    {
        if (!Held || !Begin(ES_StateEnum.Hitreact, 1f)) return;
        try
        {
            var react = Agent.Locomotion.Hitreact?.TryCast<ES_Hitreact>();
            if (react == null) return;
            var forward = Agent.transform.forward;
            react.RecieveStateData(new pES_HitreactData
            {
                StartPosition = Agent.Position, RotationDir = forward, ReactionType = type, ReactionDirection = direction,
                AnimationIndex = (byte)animIndex, DeathDelay = 0, Propagated = false, DamagePos = Agent.Position + forward,
            });
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("A puppet enemy could not play a hit reaction: " + error); }
    }

    /// <summary>Plays a scream animation (<paramref name="animIndex"/> picks which).</summary>
    public void Scream(int animIndex, float seconds)
    {
        if (!Held || !Begin(ES_StateEnum.Scream, seconds)) return;
        try { Agent.Locomotion.Scream?.TryCast<ES_Scream>()?.OnStateShouldBeActivated(new pES_EnemyScreamData { AnimIndex = (byte)animIndex }); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("A puppet enemy could not scream: " + error); }
    }

    private void EndAction()
    {
        if (!_acting || Time.unscaledTime < _actionEnds) return;
        _acting = false;
        try { Agent.Locomotion.ChangeState(ES_StateEnum.PathMove); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("A puppet enemy could not go back to walking: " + error); }
    }

    /// <summary>Kills the enemy through the game's own death (its death animation or ragdoll); the puppet ends.</summary>
    public void Die()
    {
        if (!Held) return;
        Agent.Damage.IsImortal = false;
        Agent.Damage.InstantDead(true);
        _disposed = true;
    }

    /// <summary>Gives the enemy back to the game with the settings it had when it was taken.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Agent == null || Agent.WasCollected || !Agent.Alive) return;
        Agent.Damage.IsImortal = _immortal;
        Agent.Anim.applyRootMotion = _rootMotion; Agent.Anim.speed = _speed;
        Agent.ForceNoAnimationCulling = _noCulling;
        if (_nav != null) _nav.enabled = _navEnabled;
        Agent.AI.Mode = _mode;
    }
}

/// <summary>A player agent held by <see cref="Puppets.Player"/>.</summary>
public sealed class PlayerPuppet : IDisposable
{
    private readonly string _owner;
    private bool _disposed;
    private readonly float _speed;

    internal PlayerPuppet(PlayerAgent agent, string owner)
    {
        Agent = agent; _owner = owner; _speed = agent.AnimatorBody != null ? agent.AnimatorBody.speed : 1f;
        // The game's own switch for the bots' decision making; the agent's movement and animation stay the game's synced-player code.
        PlayerManager.Current.DisableBotAI();
    }

    public PlayerAgent Agent { get; }

    public bool Held => !_disposed && Agent != null && !Agent.WasCollected;

    /// <summary>The playback rate of the body's animation (1 normal, 0 frozen); the body's own speed returns on release.</summary>
    public float Rate { set { if (Held && Agent.AnimatorBody != null) Agent.AnimatorBody.speed = value; } }

    /// <summary>Puts the player at <paramref name="position"/>, looking along <paramref name="lookDirection"/>, animated as moving with
    /// <paramref name="velocity"/> (world metres per second) and crouched by <paramref name="crouch"/> (0 standing, 1 crouched).</summary>
    public void Drive(Vector3 position, Vector3 lookDirection, Vector3 velocity, float crouch)
    {
        if (!Held) return;
        var locomotion = Agent.Locomotion;
        locomotion.SyncedSetPosition(position);
        if (lookDirection.sqrMagnitude > 1e-6f) locomotion.SyncedSetRotation(Quaternion.LookRotation(new Vector3(lookDirection.x, 0f, lookDirection.z)));
        PuppetMotion.Local(velocity.x, velocity.z, lookDirection.x, lookDirection.z, out var forward, out var right);
        locomotion.UpdateFullBodyAnimValue(forward, right);
        locomotion.SetCrouch(crouch);
    }

    /// <summary>Puts the inventory slot <paramref name="slot"/> in the player's hands for everyone to see (no gear is changed).</summary>
    public void Wield(InventorySlot slot)
    {
        if (Held) Agent.Sync.WantsToWieldSlot(slot, true);
    }

    /// <summary>Plays the third-person animation of a generic interaction (reload, shove, place a mine, point, pick up, use a resource)
    /// the way the game does for a remote player.</summary>
    public void Interact(pGenericInteractAnimation.TypeEnum type)
    {
        if (!Held) return;
        try { Agent.Inventory.SyncGenericInteract(type); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("A puppet player could not play " + type + ": " + error); }
    }

    /// <summary>Plays the throw animation (charging, thrown).</summary>
    public void Throw(pPlayerThrowStatus.StatusEnum status)
    {
        if (!Held) return;
        try { Agent.Inventory.SyncThrowState(status); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("A puppet player could not play a throw: " + error); }
    }

    /// <summary>Shows the muzzle flash of the weapon in the player's hands (no shot, no sound). False when the hands hold no weapon with one.</summary>
    public bool MuzzleFlash(out Vector3 muzzle)
    {
        muzzle = Agent.Position;
        if (!Held) return false;
        try
        {
            var weapon = Agent.Inventory.WieldedItem?.TryCast<Weapon>();
            if (weapon == null || weapon.m_muzzleFlash == null) return false;
            weapon.m_muzzleFlash.Play();
            if (weapon.MuzzleAlign != null) muzzle = weapon.MuzzleAlign.position;
            return true;
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("A puppet player's muzzle flash could not play: " + error); return false; }
    }

    private PlayerLocomotion.PLOC_State _special = PlayerLocomotion.PLOC_State.Stand;

    /// <summary>Puts the player in an air or floor state of the game's own (jump, fall, land, downed) and takes it out again when
    /// <paramref name="state"/> is <c>Stand</c>; the player's walking and crouching are not states entered here.</summary>
    public void State(PlayerLocomotion.PLOC_State state)
    {
        if (!Held || state == _special) return;
        _special = state;
        try { Agent.Locomotion.ChangeState(state); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("A puppet player could not enter " + state + ": " + error); }
    }

    /// <summary>The bot AI stays off for the rest of the level (the game has no switch to turn it on again), so there is nothing to give back.</summary>
    public void Dispose()
    {
        if (Held && Agent.AnimatorBody != null) Agent.AnimatorBody.speed = _speed;
        _disposed = true;
    }
}
