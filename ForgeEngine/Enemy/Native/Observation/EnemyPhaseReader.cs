using System;
using Enemies;

namespace ForgeEnemy.Native.Observation;

/// <summary>
/// The boss phase one enemy's own behaviour machine holds: <c>Enemies.SquidBossBehaviour.Phase</c>, the one member
/// the write row `forge.action.enemy.phase_set` writes through the same instance's setter and the read row
/// `forge.query.enemy.phase` answers from (`catalog/native/native-runtime.json`, type `SquidBossBehaviour`, field
/// `Phase`). One member family is spelled once, so the write path's own reads and the value row's read both come
/// through here instead of each casting the behaviour machine itself.
///
/// The read is explicit-instance and read-only: the behaviour machine is judged first, the phase is read once, and
/// a machine that is not a boss — or one whose getter cannot be read — has no phase here. A value outside the one
/// byte the replicated field carries (`EnemySync.pExtraBehaviourStateData.bossPhase`) is not a phase either; the
/// write row narrows further, to the phases the game's ability choice names. An unreadable phase is never reported
/// as phase zero, and this reader never writes anything.
/// </summary>
internal static class EnemyPhaseReader
{
    /// <summary>The phase domain the replicated field carries. It is the wire's domain and not the setter's — the
    /// setter is an auto-property that validates nothing — so the read compares against it.</summary>
    internal const int Minimum = 0;
    internal const int Maximum = 255;

    /// <summary>
    /// The behaviour machine as the boss it has to be — the interop cast hands back the same managed wrapper only
    /// for an instance whose native class really is `SquidBossBehaviour` — together with the phase it currently
    /// holds. False for a machine that is not a boss, one the cast cannot perform, and one whose getter returns a
    /// value outside the replicated field's domain; the caller decides whether that is a refusal or an absent fact,
    /// and the phase it answers with is 0 in every false case.
    /// </summary>
    internal static bool TryRead(EnemyBehaviour? behaviour, out int phase, out SquidBossBehaviour boss)
    {
        phase = 0;
        boss = null!;
        if (behaviour == null || behaviour.Pointer == IntPtr.Zero) return false;
        try
        {
            if (behaviour is not SquidBossBehaviour squid || squid.Pointer == IntPtr.Zero) return false;
            int current = squid.Phase;
            if (current < Minimum || current > Maximum) return false;
            boss = squid;
            phase = current;
            return true;
        }
        catch (Exception caught)
        {
            EnemyLog.Contained(caught);
            // A phase question is asked from the game's own dispatch, so a getter that throws answers "no readable
            // phase" instead of escaping into the caller.
            return false;
        }
    }
}
