using System;
using Enemies;
using Gear;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

// Resolve the whole chain before submitting damage.
internal sealed class ChainLightning
{
    private readonly VisibleEnemies _enemies = new();
    private readonly long[] _visited = new long[EnergyLimits.ArcTargets];

    internal int Continue(PlayerAgent owner, in WeaponNumbers numbers, in ShotTuning shot, NativeShot[] shots)
    {
        var first = shots[0].Receiver?.GetBaseAgent()?.TryCast<EnemyAgent>();
        if (first == null || !first.Alive) return 1;
        _visited[0] = first.Pointer.ToInt64();
        var segments = 1;
        while (segments < numbers.Targets)
        {
            var origin = shots[segments - 1].End;
            if (!_enemies.Nearest(owner, TargetRelationFilter.Enemies, hitInterveningReceiver: true,
                origin, numbers.JumpRange, _visited.AsSpan(0, segments),
                _visited[segments - 1], out var hit, out var identity)) break;
            var tuning = shot with { Range = numbers.JumpRange,
                Damage = shot.Damage * MathF.Pow(numbers.DamageRetention, segments) };
            shots[segments].ResolveHit(owner, tuning, origin, (hit.point - origin).normalized, hit);
            var recipient = shots[segments].Receiver?.GetBaseAgent()?.TryCast<EnemyAgent>();
            _visited[segments++] = identity;
            if (recipient == null || !recipient.Alive) break;
        }
        return segments;
    }
}
