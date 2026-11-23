using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>
/// The `forge.action.combat.projectile_launch` body: one of the game's own projectiles, from a point and a
/// direction, as many copies as the request asked for.
///
/// The launch is the manager's own complete request, <c>ProjectileManager.WantToFireTargeting</c>, called once per
/// heading and reached through <see cref="ProjectileSpawner"/>. A launch is confirmed only when the manager's
/// own projectile list shows the new projectile; this action reports that, not flight, damage or replication. The requested profile must name a native
/// <c>ProjectileType</c> member. Only the authoritative host may submit these calls. The optional `homing_target` is the
/// agent the projectile homes on: the host resolves it and hands it to the same request, whose broadcast packet
/// carries it to every machine.
/// </summary>
internal sealed class WeaponProjectileActions
{
    private static readonly Vector3 Up = new(0f, 1f, 0f);
    private static readonly Vector3 Forward = new(0f, 0f, 1f);

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;

    internal WeaponProjectileActions(IForgeEngine kernel, Func<bool> canExecute, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>`forge.action.combat.projectile_launch`. Refusals, in the order they are checked: a command this
    /// machine may not run, a frame whose vectors do not read, a profile this build has no prefab for, a count or
    /// spread outside what a launch can honour, and finally the game's own spawn path. The result's own field is
    /// how many copies really left the origin, which is what the plan can check its request against.</summary>
    internal CommandResult Launch(CommandContext context)
    {
        // The `source` port is the row's own recipient, so a request that names none cannot carry a result row:
        // refused before the first spawn rather than reported as a launch with no recipient.
        if (!context.TryEntity(CombatPrimitiveContract.ProjectileLaunchSourceSlot, out var source))
            return Outcome.Rejected(ResultCode.SourceMissing).WithDetail("source");
        if (!_canExecute() || !_kernel.Lifecycle.IsReadyHost) return Refused(context, ResultCode.AuthorityOrPhase);
        if (!ShotVectors.TryRead(context, CombatPrimitiveContract.ProjectileLaunchOriginSlot, out var origin)
            || !ShotVectors.TryRead(context, CombatPrimitiveContract.ProjectileLaunchDirectionSlot, out var direction))
            return Refused(context, ResultCode.VectorInvalid);
        if (Length(direction) < 1e-4d) return Refused(context, ResultCode.VectorInvalid);
        var profile = context.Text(CombatPrimitiveContract.ProjectileLaunchProfileSlot);
        if (profile == null || !((IList<string>)CombatPrimitiveContract.ProjectileProfiles).Contains(profile)
            || ((IList<string>)BulletProjectileContract.Profiles).IndexOf(profile) is not (>= 0 and var memberIndex))
            return Refused(context, ResultCode.ProjectileProfileUnknown);
        var countValue = context.Integer(CombatPrimitiveContract.ProjectileLaunchCountSlot) ?? 1L;
        if (countValue < 1 || countValue > int.MaxValue) return Refused(context, ResultCode.ProjectileCountOutOfRange);
        var count = (int)countValue;
        var spread = context.Number(CombatPrimitiveContract.ProjectileLaunchSpreadSlot) ?? 0d;
        // The spread is the cone's half angle in degrees: negative has no meaning, and any width rotates cleanly.
        if (!double.IsFinite(spread) || spread < 0d)
            return Refused(context, ResultCode.ProjectileSpreadOutOfRange);
        if (!ProjectileSpawner.TryType(BulletProjectileContract.NativeMembers[memberIndex], out var kind)) return Refused(context, ResultCode.ProjectileSpawnUnavailable);
        // The homing target is resolved once, here on the host, and travels in the game's own fire packet. A target
        // that was wired and no longer names a live agent of this world refuses the launch: flying straight instead
        // would be another request.
        Agents.Agent? target = null;
        if (context.Present(CombatPrimitiveContract.ProjectileLaunchTargetSlot))
        {
            if (!context.TryEntity(CombatPrimitiveContract.ProjectileLaunchTargetSlot, out var named)
                || named.WorldEpoch != context.WorldEpoch || !_kernel.IsEntityCurrent(named)
                || context.EntityInstance(named) is not Agents.Agent agent || agent.Pointer == IntPtr.Zero)
                return Refused(context, ResultCode.StaleEntity);
            target = agent;
        }
        var launched = 0;
        for (var index = 0; index < count; index++)
        {
            var heading = count == 1 || spread <= 0d ? Normalize(direction) : Scatter(direction, spread, index, count);
            try
            {
                if (ProjectileSpawner.Launch(kind, target, origin, heading))
                {
                    launched++;
                    continue;
                }
                _report("weapon.projectile-spawn-unconfirmed: the manager's projectile list did not grow");
            }
            catch (Exception error)
            {
                _report("weapon.projectile-spawn-unconfirmed: " + error.GetType().Name + ": " + error.Message);
            }
            // The wrapper cannot prove that a missing projectile or a throw happened before native side effects.
            // Stop immediately: retrying or completing the remainder could duplicate projectiles.
            context.Rows.Begin(0).Null(Blocks.ActionCombatProjectileLaunch.RowTargetCount)
                .End(Status.Failed, Commit.Unknown, ResultCode.ProjectileSpawnUnconfirmed);
            return Outcome.FailedUnknown(ResultCode.ProjectileSpawnUnconfirmed);
        }
        context.Rows.Begin(0).Integer(Blocks.ActionCombatProjectileLaunch.RowTargetCount, launched)
            .End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        return Outcome.Succeeded();
    }

    /// <summary>A launch refused before the first spawn. The one result row's `target` is the source the request
    /// carried, and its own column counts the copies that really left: none.</summary>
    private static CommandResult Refused(CommandContext context, string code)
    {
        context.Rows.Begin(0).Integer(Blocks.ActionCombatProjectileLaunch.RowTargetCount, 0).End(Status.Rejected, Commit.None, code);
        return Outcome.Rejected(code);
    }

    /// <summary>
    /// A deterministic fan whose end headings lie on the requested cone. No random seed is declared by this action.
    /// </summary>
    private static Vector3 Scatter(Vector3 direction, double spread, int index, int count)
    {
        var forward = Normalize(direction);
        double fx = forward.x, fy = forward.y, fz = forward.z;
        var radians = spread * (2d * index / (count - 1) - 1d) * Math.PI / 180d;
        // Use a perpendicular axis; a heading parallel to up uses the world's forward instead.
        var axis = Vector3.Cross(Up, forward);
        double ax = axis.x, ay = axis.y, az = axis.z;
        if (ax * ax + ay * ay + az * az < 1e-12d)
        {
            axis = Vector3.Cross(Forward, forward);
            ax = axis.x; ay = axis.y; az = axis.z;
        }
        var axisLength = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (!double.IsFinite(axisLength) || axisLength < 1e-6d) return forward;
        ax /= axisLength; ay /= axisLength; az /= axisLength;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dot = ax * fx + ay * fy + az * fz;
        var cx = ay * fz - az * fy;
        var cy = az * fx - ax * fz;
        var cz = ax * fy - ay * fx;
        return Normalize(new Vector3(
            (float)(fx * cos + cx * sin + ax * dot * (1d - cos)),
            (float)(fy * cos + cy * sin + ay * dot * (1d - cos)),
            (float)(fz * cos + cz * sin + az * dot * (1d - cos))));
    }

    /// <summary>One vector's length, from its components.</summary>
    private static double Length(Vector3 value)
    {
        double x = value.x, y = value.y, z = value.z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>The unit vector of one heading, or the same reading when it has no length to divide by — the
    /// caller has already refused a zero direction, so this is the belt to that refusal's braces.</summary>
    private static Vector3 Normalize(Vector3 value)
    {
        var length = Length(value);
        if (length < 1e-9d) return value;
        return new Vector3((float)(value.x / length), (float)(value.y / length), (float)(value.z / length));
    }
}
