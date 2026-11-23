using System;
using System.Collections.Generic;
using Enemies;
using ForgeEnemy.Profile;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// The native half of the block field application: the values <see cref="EnemyFieldResolver"/> read from a
/// `forge/enemies/*.forge` document, written to the members the research named.
///
/// Two application points. The spawn-time values go through <see cref="EnemyModule.ApplyProfiles"/> from the
/// `EnemySync.OnSpawn` postfix, which runs once per native life on every peer after the receiver's own setup; the
/// skeleton entries go through <see cref="EnemyModule.AssembleAppearance"/> before native appearance initialization,
/// which runs once per enemy type. Both are per-type constants: every peer reads the same document and writes the
/// same member, so nothing goes over the network.
///
/// This file is compiled only by the projects that mirror the whole native set. The partial declarations in
/// `EnemyModuleProfiles.cs` and `EnemyModuleAppearance.cs` make a build without it a no-op instead of a compile
/// error, which is what the slice suites (a shim `EnemyModule` and its own doubles) expect.
///
/// What is written here and by the sections in `EnemyModuleProfiles.cs` is exactly
/// <see cref="EnemyFieldSchema.EnemyDefinitionFields"/>. Everything else the field table accepts is reported once
/// per type as `enemy.field-unapplied` by <see cref="EnemyModule"/>; nothing is silently dropped.
/// </summary>
internal sealed partial class EnemyModule
{
    /// <summary>
    /// Writes the block fields whose carrier is not one of the three profile sections. Each value is the one the
    /// resolver typed; a member with no value in the document is left exactly as the prefab authored it.
    /// </summary>
    partial void ApplyBlockFields(EnemyAgent enemy, EnemyFieldResolution fields)
    {
        if (fields.RequireTagForDetection is bool requireTag) enemy.RequireTagForDetection = requireTag;
        // `m_enemyCost` is not written here: it is the life's on-screen cap share, which the game allocates when the
        // life turns aggressive and frees on death, so a spawn-time write would free what was never counted. The
        // cap share is corrected at that allocation instead (EnemyModule.CapCost.cs).
        var locomotion = enemy.Locomotion;
        if (locomotion != null)
        {
            // `ES_Hibernate` owns the two hibernate-state colours; the state machine reads the vectors when it
            // enters detection and when it beats, so writing them once at spawn is what the reader documents.
            var hibernate = locomotion.Hibernate;
            if (hibernate != null)
            {
                if (fields.GlowDetection is { } detection) hibernate.m_detectingColorVec = Vector(detection);
                if (fields.GlowHeartbeat is { } heartbeat) hibernate.m_heartbeatColorVec = Vector(heartbeat);
            }
            // `ES_HibernateWakeUp.m_selfWakeupColor` is the colour a self-awakened enemy fades towards. Its
            // sibling, the propagated-wake-up colour, is a static on that type, so it is not written here: the look
            // swaps it around this type's own propagated wake-up (EnemyModuleLook.cs, `WakeupStarting`).
            var wakeUp = locomotion.HibernateWakeup;
            if (wakeUp != null && fields.GlowSelfWakeup is { } selfWakeup) wakeUp.m_selfWakeupColor = Vector(selfWakeup);
            // `ES_ShooterAttack.m_attackGlowColor_End` is the colour the shooter state animates its glow to.
            var shooter = locomotion.ShooterAttack;
            if (shooter != null && fields.GlowShooterFire is { } fire) shooter.m_attackGlowColor_End = Vector(fire);
            if (fields.AnimHandle is { } handle) ApplyAnimHandle(locomotion, handle);
        }
        // The tentacle-attack glow belongs to the ability component that owns the tentacle attack, not to the
        // locomotion: the type's own ability table is scanned for it, exactly as the ability row finds its index.
        if (fields.GlowTentacleAttack is { } tentacle) ApplyTentacleGlow(enemy, tentacle);
    }

    /// <summary>
    /// Writes the multi-tentacle attack glow on the ability component that carries it. A type whose ability table
    /// has no such component has nowhere to put the value, so the field is reported unapplied by the caller's own
    /// pass rather than silently dropped; the scan is ordinal and takes the first component of that kind.
    /// </summary>
    private static void ApplyTentacleGlow(EnemyAgent enemy, float[] color)
    {
        var abilities = enemy.Abilities;
        var comps = abilities?.AllComps;
        if (comps == null) return;
        for (int index = 0; index < comps.Length; index++)
        {
            if (comps[index] == null) continue;
            if (comps[index].TryCast<EAB_MovingEnemyTentacleMultiple>() is not { } multiple) continue;
            multiple.m_attackGlowColor = Vector(color);
            return;
        }
    }

    /// <summary>
    /// Writes one `forge.eec.Model.Bone.Bones` entry onto the named node of the live instance's main model.
    /// A name no node answers, or one two nodes answer, is reported and the
    /// node keeps its authored transform.
    /// </summary>
    partial void ApplyBlockBones(GameObject prefab, IReadOnlyList<EnemyFieldBone> bones, List<string> unresolved, List<Action> undo)
    {
        var model = prefab.GetComponent<EnemyAgent>()?.MainModelTransform;
        if (model == null) return;
        foreach (var bone in bones)
        {
            var node = FindBone(model, bone.Name);
            if (node == null) { unresolved.Add(bone.Name); continue; }
            var position = node.localPosition;
            var rotation = node.localEulerAngles;
            var originalScale = node.localScale;
            var writtenPosition = bone.Offset is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) : position;
            var writtenRotation = bone.RotationOffset is { Length: 3 } r ? new Vector3(r[0], r[1], r[2]) : rotation;
            var writtenScale = bone.Scale is { Length: 3 } s ? (bone.UsingRelativeScale == true
                ? new Vector3(originalScale.x * s[0], originalScale.y * s[1], originalScale.z * s[2]) : new Vector3(s[0], s[1], s[2])) : originalScale;
            EnemyAppearanceAssembler.UndoOn(undo, node, () =>
            {
                if (bone.Offset != null && node.localPosition.Equals(writtenPosition)) node.localPosition = position;
                if (bone.RotationOffset != null && node.localEulerAngles.Equals(writtenRotation)) node.localEulerAngles = rotation;
                if (bone.Scale != null && node.localScale.Equals(writtenScale)) node.localScale = originalScale;
            });
            if (bone.Offset is { Length: 3 } offset) node.localPosition = new Vector3(offset[0], offset[1], offset[2]);
            if (bone.RotationOffset is { Length: 3 } angles)
            {
                node.localEulerAngles = new Vector3(angles[0], angles[1], angles[2]);
                writtenRotation = node.localEulerAngles; // Unity normalizes Euler angles.
            }
            if (bone.Scale is { Length: 3 } scale)
            {
                // `UsingRelativeScale` is what decides whether the authored scale replaces the node's own or is
                // multiplied into it; a document that says nothing means no scaling of the node at all.
                var current = node.localScale;
                node.localScale = bone.UsingRelativeScale == true
                    ? new Vector3(current.x * scale[0], current.y * scale[1], current.z * scale[2])
                    : new Vector3(scale[0], scale[1], scale[2]);
            }
        }
    }

    /// <summary>The one node under the model whose name matches, or null when none or more than one does. The
    /// lookup is per-ancestor and matches the whole name, so `Head` cannot answer with `HeadRoot`.</summary>
    private static Transform? FindBone(Transform model, string name)
    {
        Transform? found = null;
        var nodes = model.gameObject.GetComponentsInChildren<Transform>(true);
        for (int index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            if (node == null || !string.Equals(node.name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found != null && !ForgeCore.NativeObjects.Same(found, node)) return null;
            found = node;
        }
        return found;
    }

    /// <summary>
    /// Writes the animator-controller handle values onto the instance's own `AnimHandle` property. The handle is a
    /// struct the game copies into the instance from the static controller table keyed by
    /// `AnimatorControllerHandleName`, so writing the property is the only way a type-specific value does not leak
    /// to every other type sharing that controller.
    /// </summary>
    internal void ControllerInitialized(EnemyLocomotion locomotion)
    {
        if (_profiles == null || _enemyType == null || locomotion.m_agent is not { } enemy) return;
        uint? typeId = _enemyType(enemy);
        if (typeId is not uint id) return;
        var profile = _profiles.Resolve(id);
        if (profile.Fields?.AnimHandle is { } overrides) ApplyAnimHandle(locomotion, overrides);
        if (profile.Sounds.Length != 0)
            _sounds.ApplyMelee(locomotion, profile.Sounds, (slot, sound, reason) => SoundRefusal(id, profile.Source, slot, sound, reason));
    }

    private static void ApplyAnimHandle(EnemyLocomotion locomotion, EnemyAnimHandleOverrides overrides)
    {
        var handle = overrides.ForController((int)locomotion.AnimHandleName);
        if (handle == null) return;
        var animator = locomotion.AnimHandle;
        if (animator == null) return;
        if (handle.WalkSpeed is float walk) animator.WalkSpeed = walk;
        if (handle.RunSpeed is float run) animator.RunSpeed = run;
        if (handle.StrafeWalkSpeed is float strafeWalk) animator.StrafeWalkSpeed = strafeWalk;
        if (handle.StrafeRunSpeed is float strafeRun) animator.StrafeRunSpeed = strafeRun;
        if (handle.ClimbSpeed is float climb) animator.ClimbSpeed = climb;
        if (handle.InPlayerRoomSpeed is float room) animator.InPlayerRoomSpeed = room;
        if (handle.JumpMoveSpeed is float jumpMove) animator.JumpMoveSpeed = jumpMove;
        if (handle.TentacleAttackWindUpLen is float windUp) animator.TentacleAttackWindUpLen = windUp;
        if (handle.AllowMelee180 is bool allow180) animator.AllowMelee180 = allow180;
        if (handle.NeedsArmsToMove is bool arms) animator.NeedsArmsToMove = arms;
        // The two melee timing entries are structs: the game's own property returns a copy, so the whole value is
        // read, changed and written back rather than a field of a temporary being set.
        var forward = animator.MeleeAttackFwd;
        bool forwardChanged = false;
        if (handle.MeleeAttackFwdDuration is float duration) { forward.Duration = duration; forwardChanged = true; }
        if (handle.MeleeAttackFwdDamageStart is float damageStart) { forward.DamageStart = damageStart; forwardChanged = true; }
        if (handle.MeleeAttackFwdDamageEnd is float damageEnd) { forward.DamageEnd = damageEnd; forwardChanged = true; }
        if (forwardChanged) animator.MeleeAttackFwd = forward;

        var backward = animator.MeleeAttackBwd;
        bool backwardChanged = false;
        if (handle.MeleeAttackBwdDuration is float bwdDuration) { backward.Duration = bwdDuration; backwardChanged = true; }
        if (handle.MeleeAttackBwdDamageStart is float bwdStart) { backward.DamageStart = bwdStart; backwardChanged = true; }
        if (handle.MeleeAttackBwdDamageEnd is float bwdEnd) { backward.DamageEnd = bwdEnd; backwardChanged = true; }
        if (handle.MeleeAttackBwdSfxId is int bwdSfx) { backward.SFXId = (uint)bwdSfx; backwardChanged = true; }
        if (backwardChanged) animator.MeleeAttackBwd = backward;
        // The handle is a boxed value type the game copies out of the shared controller table: writing the
        // instance's own property back is what keeps the change from leaking to every other type on that
        // controller, and what makes a mutation of the returned handle stick at all.
        locomotion.AnimHandle = animator;
    }

    /// <summary>A document colour (three or four components in 0..1) as the native vector the glow members
    /// carry; a missing alpha is opaque.</summary>
    private static Vector4 Vector(float[] color)
        => new(color[0], color[1], color[2], color.Length > 3 ? color[3] : 1f);
}
