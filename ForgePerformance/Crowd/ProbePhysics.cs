using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Enemies;
using Player;
using UnityEngine;

namespace ForgePerformance.Crowd;

/// <summary>Probe readback of what the physics engine has to carry: its global settings, the rigid bodies and colliders in the
/// scene by owner, and one enemy's limb colliders. Called from the console; walks every collider once, so it is not for a frame path.</summary>
internal static class ProbePhysics
{
    internal static string Read()
    {
        var text = new StringBuilder();
        text.Append("auto_sync=").Append(Physics.autoSyncTransforms ? 1 : 0)
            .Append(";auto_simulation=").Append(Physics.autoSimulation ? 1 : 0)
            .Append(";reuse_callbacks=").Append(Physics.reuseCollisionCallbacks ? 1 : 0)
            .Append(";solver_iterations=").Append(Physics.defaultSolverIterations)
            .Append(";fixed_dt=").Append(Time.fixedDeltaTime.ToString("0.####", CultureInfo.InvariantCulture))
            .Append(";max_dt=").Append(Time.maximumDeltaTime.ToString("0.###", CultureInfo.InvariantCulture));

        int bodies = 0, awake = 0, kinematic = 0, kinematicAwake = 0;
        foreach (var body in Object.FindObjectsOfType<Rigidbody>())
        {
            bodies++;
            var sleeping = body.IsSleeping();
            if (!sleeping) awake++;
            if (body.isKinematic) { kinematic++; if (!sleeping) kinematicAwake++; }
        }
        text.Append(";bodies=").Append(bodies).Append(";bodies_awake=").Append(awake)
            .Append(";kinematic=").Append(kinematic).Append(";kinematic_awake=").Append(kinematicAwake);

        // owner / layer / shape / body kind -> count
        var groups = new Dictionary<string, int>();
        var colliders = Object.FindObjectsOfType<Collider>();
        foreach (var collider in colliders)
        {
            var owner = collider.GetComponentInParent<EnemyAgent>() != null ? "enemy"
                : collider.GetComponentInParent<PlayerAgent>() != null ? "player" : "other";
            var body = collider.attachedRigidbody;
            var key = owner + "/" + LayerMask.LayerToName(collider.gameObject.layer) + "/" + collider.GetIl2CppType().Name
                + (collider.isTrigger ? "/trigger" : "") + (body == null ? "/static" : body.isKinematic ? "/kinematic" : "/dynamic");
            groups[key] = groups.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        text.Append(";colliders=").Append(colliders.Length).Append(";groups=");
        var keys = new List<string>(groups.Keys);
        keys.Sort((a, b) => groups[b].CompareTo(groups[a]));
        for (var i = 0; i < keys.Count && i < 24; i++) text.Append(keys[i]).Append(':').Append(groups[keys[i]]).Append(',');

        int enemies = 0, shown = 0, animatorsOn = 0, cullCompletely = 0;
        EnemyAgent? sample = null;
        foreach (var enemy in Object.FindObjectsOfType<EnemyAgent>())
        {
            enemies++;
            sample ??= enemy;
            if (enemy.MovingCuller is { } culler && culler.IsShown) shown++;
            var animator = enemy.Locomotion?.m_animator;
            if (animator == null) continue;
            if (animator.enabled) animatorsOn++;
            if (animator.cullingMode == AnimatorCullingMode.CullCompletely) cullCompletely++;
        }
        text.Append(";enemies=").Append(enemies).Append(";enemies_shown=").Append(shown)
            .Append(";animators_on=").Append(animatorsOn).Append(";cull_completely=").Append(cullCompletely);
        if (sample != null)
        {
            int limbs = 0, withBody = 0, limbKinematic = 0, triggers = 0;
            foreach (var collider in sample.GetComponentsInChildren<Collider>(true))
            {
                limbs++;
                if (collider.isTrigger) triggers++;
                var body = collider.attachedRigidbody;
                if (body == null) continue;
                withBody++;
                if (body.isKinematic) limbKinematic++;
            }
            text.Append(";sample=").Append(sample.name).Append(";sample_colliders=").Append(limbs).Append(";sample_with_body=").Append(withBody)
                .Append(";sample_kinematic=").Append(limbKinematic).Append(";sample_triggers=").Append(triggers)
                .Append(";sample_bodies=").Append(sample.GetComponentsInChildren<Rigidbody>(true).Length);
        }
        return text.ToString();
    }
}
