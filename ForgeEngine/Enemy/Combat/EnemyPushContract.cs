using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The enemy binding of `forge.action.combat.impulse`: a pure push along the row's shaped force, with no
/// damage of its own. The Player module answers the same row for player recipients; both shape the force with
/// <see cref="ForgePlayer.CombatImpulseContract.TryForce"/> and declare its one handler shape.
///
/// <b>The native entry.</b> `Dam_EnemyDamageBase.SendExplosionForce(Vector3 origin, float forceMagnitude)` - the
/// force channel `catalog/native/native-runtime.json` carries on that type beside `ReceiveExplosionForce` and
/// `m_explosionForcePacket`. It submits a force and nothing else: the force's length is the entry's
/// `forceMagnitude` and its direction is how the origin the force pushes away from is placed. The native half
/// explains the geometry it derives from it.
///
/// <b>Why not the member the ledger names.</b> The ledger row `vanilla-enemy.combat.knockback_push` names
/// `Dam_EnemyDamageLimb.PushDamage(float, Agent, Vector3, Vector3)` as its hook point. That member's first
/// parameter is `dam`, and the frozen damage-window evidence shows the push packet's own receiver
/// (`Dam_EnemyDamageBase.ReceivePushDamage`) reading `get_DamageMax` and handing the packet to
/// `ProcessReceivedDamage` the way every other damage receiver does, so submitting through it would deal damage.</summary>
internal static class EnemyPushContract
{
    /// <summary>The provider every row here belongs to; the same string the other enemy contracts carry.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;
    public const string BindingId = ProviderId + ".binding.combat_impulse";
    public const string HandlerName = "gtfo.enemy.impulse";

    /// <summary>The permission the write carries: a push moves a body, which is the motion write this provider's
    /// stagger and interrupt rows already declare. It is not a health write — the submission carries no damage.
    /// </summary>
    public const string WritePermission = "gtfo.enemy.motion.write";

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionCombatImpulse.Id, HandlerName, "execute", new[] { WritePermission },
            ForgePlayer.CombatImpulseContract.Shape)
    };
}
