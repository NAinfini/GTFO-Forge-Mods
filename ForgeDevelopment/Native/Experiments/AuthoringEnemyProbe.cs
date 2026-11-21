using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Enemies;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Driver operations on one live native enemy, named by its GlobalID so they reach a batch enemy, a level-placed enemy
// or a Forge-authored enemy alike. A hit goes through the game's own damage entry with the local player as source, so
// settlement, death and replication are the game's; the answer is only what the game then holds. Nothing is written except
// that one request; no event is fabricated. Two routes, named in the request and never substituted for each other:
// "limb" is the limb's own request (Dam_EnemyDamageLimb.BulletDamage: weak spot, armour and limb multipliers apply, as for a
// shot that hit the limb), "body" is the enemy's own request (Dam_EnemyDamageBase.BulletDamage: no limb multiplier).
internal static class AuthoringEnemyProbe
{
    private const string Hit = "drive.enemy.hit", Limbs = "drive.enemy.limbs", Follow = "drive.enemy.follow";
    private sealed record Attachment(IntPtr Pointer, string Path, Vector3 Position, Vector3 LocalPosition, string Bone, Vector3 BonePosition);
    private sealed record FollowBaseline(int GlobalId, IntPtr Enemy, long World, Dictionary<string, Attachment> Rows);
    private static FollowBaseline? _follow;

    internal static bool Handles(string name) => name is Hit or Limbs or Follow;

    internal static string Call(string name, string json)
    {
        RecObservation.RequireNativeRead();
        if (!ExperimentRunner.Running || !RecSession.Active || !ForgeCore.Session.IsHost
            || GameStateManager.CurrentStateName != eGameStateName.InLevel)
            throw new InvalidOperationException("enemy-probe.authoring-host-level-recorder-required");
        if (json.Length > 2048) throw new ArgumentException("enemy-probe.input-budget");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("enemy-probe.object-required");
        var required = name switch
        {
            Hit => new[] { "schemaVersion", "globalId", "route", "mode" },
            Limbs => new[] { "schemaVersion", "globalId" },
            _ => new[] { "schemaVersion", "globalId", "phase" }
        };
        var optional = name == Hit ? new[] { "amount", "limb", "limbName", "limbType" } : Array.Empty<string>();
        var fields = root.EnumerateObject().Select(field => field.Name).ToArray();
        if (fields.Distinct().Count() != fields.Length || fields.Any(field => !required.Contains(field) && !optional.Contains(field))
            || required.Any(field => !fields.Contains(field)))
            throw new ArgumentException("enemy-probe.unknown-duplicate-or-missing-field");
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.String || root.GetProperty("schemaVersion").GetString() != "1.0.0")
            throw new ArgumentException("enemy-probe.schema-version");
        var enemy = Find(root.GetProperty("globalId").GetInt32());
        return name switch
        {
            Hit => IssueHit(enemy, root),
            Limbs => ReadLimbs(enemy),
            _ => ReadFollow(enemy, root.GetProperty("phase").GetString() ?? "")
        };
    }

    internal static void Cleanup() => _follow = null;

    private static EnemyAgent Find(int globalId)
    {
        var matches = UnityEngine.Object.FindObjectsOfType<EnemyAgent>()
            .Where(enemy => enemy != null && !enemy.WasCollected && enemy.GlobalID == globalId).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("enemy-probe.native-enemy-not-unique:" + matches.Length);
        var found = matches[0];
        if (!found.IsSetup || found.Damage == null || found.Damage.WasCollected || !found.Damage.IsSetup)
            throw new InvalidOperationException("enemy-probe.native-enemy-not-setup");
        return found;
    }

    private static Dam_EnemyDamageLimb? LimbOf(EnemyAgent enemy, int id)
    {
        var limbs = enemy.Damage.DamageLimbs;
        for (var index = 0; limbs != null && index < limbs.Length; index++)
            if (limbs[index] is { WasCollected: false } limb && limb.m_limbID == id) return limb;
        return null;
    }

    private static float KillAmount(Dam_EnemyDamageBase damage) => damage.Health + damage.HealthMax + 1f;

    /// <summary>A kill is one lethal request through the game's own damage entry, source = the local player.</summary>
    internal static void Kill(EnemyAgent enemy, PlayerAgent player)
    {
        var damage = enemy.Damage;
        ForgeCore.Combat.BulletDamage(damage, KillAmount(damage), player, enemy.Position, Vector3.zero, Vector3.zero, false, 0, 0f, 1f);
    }

    /// <summary>The limb a route-"limb" hit names: by id (<c>limb</c>), by object name (<c>limbName</c>) or by native type
    /// (<c>limbType</c>: Normal, Weakspot or Armor; the first present limb of that type). Exactly one selector.</summary>
    private static Dam_EnemyDamageLimb SelectLimb(EnemyAgent enemy, JsonElement root)
    {
        var named = new[] { "limb", "limbName", "limbType" }.Where(field => root.TryGetProperty(field, out _)).ToArray();
        if (named.Length != 1) throw new ArgumentException("enemy-probe.limb-selector-exactly-one");
        var limbs = enemy.Damage.DamageLimbs;
        for (var index = 0; limbs != null && index < limbs.Length; index++)
        {
            if (limbs[index] is not { WasCollected: false } limb) continue;
            var match = named[0] switch
            {
                "limb" => limb.m_limbID == root.GetProperty("limb").GetInt32(),
                "limbName" => limb.gameObject.name == root.GetProperty("limbName").GetString(),
                _ => string.Equals(limb.m_type.ToString(), root.GetProperty("limbType").GetString(), StringComparison.OrdinalIgnoreCase)
            };
            if (match) return limb;
        }
        throw new InvalidOperationException("enemy-probe.limb-not-present:" + named[0] + "=" + root.GetProperty(named[0]));
    }

    private static string IssueHit(EnemyAgent enemy, JsonElement root)
    {
        var mode = root.GetProperty("mode").GetString();
        if (mode is not ("damage" or "kill")) throw new ArgumentException("enemy-probe.mode");
        var route = root.GetProperty("route").GetString();
        if (route is not ("limb" or "body")) throw new ArgumentException("enemy-probe.route");
        var viaLimb = route == "limb";
        if (!viaLimb && (root.TryGetProperty("limbName", out _) || root.TryGetProperty("limbType", out _)))
            throw new ArgumentException("enemy-probe.body-route-takes-limb-id-only");
        var limbId = !viaLimb && root.TryGetProperty("limb", out var rawLimb) ? rawLimb.GetInt32() : 0;
        if (limbId < 0) throw new ArgumentException("enemy-probe.limb");
        var player = ForgeCore.Players.Local;
        if (player == null || player.WasCollected || !player.Alive) throw new InvalidOperationException("enemy-probe.local-player-unavailable");
        var damage = enemy.Damage;
        var before = damage.Health;
        if (!enemy.Alive || !float.IsFinite(before) || before <= 0) throw new InvalidOperationException("enemy-probe.enemy-not-alive");
        float amount;
        if (mode == "kill")
        {
            if (root.TryGetProperty("amount", out _)) throw new ArgumentException("enemy-probe.kill-takes-no-amount");
            amount = KillAmount(damage);
        }
        else
        {
            if (!root.TryGetProperty("amount", out var raw) || !raw.TryGetSingle(out amount) || !(amount > 0f) || !float.IsFinite(amount))
                throw new ArgumentException("enemy-probe.amount");
        }
        // Limb 0 is also the game's "no particular limb": it is only read back when the enemy really has a limb 0.
        var limb = viaLimb ? SelectLimb(enemy, root) : LimbOf(enemy, limbId);
        if (viaLimb) limbId = limb!.m_limbID;
        if (!viaLimb && limbId != 0 && limb == null) throw new InvalidOperationException("enemy-probe.limb-not-present:" + limbId);
        var limbBefore = limb == null ? (float?)null : limb.m_health;
        var limbType = limb?.m_type.ToString();
        // The multiplier the game holds for this limb's type; the observed one is what the enemy then really lost per point.
        var declared = limb == null ? (float?)null : limb.m_type.ToString() switch
        {
            "Weakspot" => limb.m_weakspotDamageMulti,
            "Armor" => limb.m_armorDamageMulti,
            _ => 1f
        };
        var pointer = enemy.Pointer;
        // Route "limb": the limb's own request (Dam_EnemyDamageLimb.BulletDamage), which applies weak spot, armour and the limb's
        // multipliers before forwarding to its enemy. Route "body": the enemy's own request, which skips those multipliers.
        if (viaLimb) ForgeCore.Combat.BulletDamage(limb!, amount, player, limb!.transform.position, limb.transform.forward, -limb.transform.forward, false, 0f, 1f);
        else ForgeCore.Combat.BulletDamage(damage, amount, player, enemy.Position, Vector3.zero, Vector3.zero, false, limbId, 0f, 1f);
        if (enemy.WasCollected || enemy.Pointer != pointer) throw new InvalidOperationException("enemy-probe.enemy-changed-during-hit");
        var after = damage.Health;
        if (!float.IsFinite(after)) throw new InvalidOperationException("enemy-probe.health-readback-missing");
        return JsonSerializer.Serialize(new
        {
            globalId = enemy.GlobalID,
            enemyType = enemy.EnemyDataID,
            route,
            mode,
            requestedLimb = limbId,
            amount,
            limbType,
            limbName = limb?.gameObject.name,
            declaredMultiplier = declared,
            observedMultiplier = mode == "damage" && limb != null ? (before - after) / amount : (float?)null,
            enemyHealthBefore = before,
            enemyHealthAfter = after,
            enemyLoss = before - after,
            alive = enemy.Alive,
            limbHealthBefore = limbBefore,
            limbHealthAfter = limb == null ? (float?)null : limb.m_health,
            limbLoss = limb == null ? (float?)null : limbBefore - limb.m_health,
            limbColliderEnabled = limb == null ? (bool?)null : ColliderStates(limb).Any(state => state.Enabled)
        });
    }

    private static (bool Enabled, bool Trigger, int Layer)[] ColliderStates(Dam_EnemyDamageLimb limb)
    {
        var rows = new List<(bool, bool, int)>();
        var colliders = limb.gameObject.GetComponents<Collider>();
        for (var index = 0; colliders != null && index < colliders.Length; index++)
            if (colliders[index] is { WasCollected: false } collider)
                rows.Add((collider.enabled, collider.isTrigger, collider.gameObject.layer));
        return rows.ToArray();
    }

    private static string ReadLimbs(EnemyAgent enemy)
    {
        var rows = new List<object>();
        var limbs = enemy.Damage.DamageLimbs;
        for (var index = 0; limbs != null && index < limbs.Length; index++)
        {
            if (limbs[index] is not { WasCollected: false } limb) { rows.Add(new { slot = index, present = false }); continue; }
            var colliders = ColliderStates(limb);
            var renderers = limb.gameObject.GetComponentsInChildren<Renderer>(true);
            var visible = 0;
            var total = 0;
            var shadowModes = new List<string>();
            for (var at = 0; renderers != null && at < renderers.Length; at++)
            {
                if (renderers[at] is not { WasCollected: false } renderer) continue;
                total++;
                if (renderer.enabled && renderer.gameObject.activeInHierarchy) visible++;
                shadowModes.Add(renderer.name + ":" + renderer.shadowCastingMode);
            }
            rows.Add(new
            {
                slot = index,
                present = true,
                limb = limb.m_limbID,
                name = limb.gameObject.name,
                // Where the limb hangs (a sac under its Weakspot_N anchor) and how each of its renderers casts: ShadowsOnly is a shadow-only look.
                path = PathOf(limb.transform, enemy.transform),
                shadowModes,
                type = limb.m_type.ToString(),
                health = limb.m_health,
                healthMax = limb.m_healthMax,
                activeSelf = limb.gameObject.activeSelf,
                activeInHierarchy = limb.gameObject.activeInHierarchy,
                colliders = colliders.Select(state => new { enabled = state.Enabled, trigger = state.Trigger, layer = state.Layer }).ToArray(),
                anyColliderEnabled = colliders.Any(state => state.Enabled),
                renderers = total,
                renderersVisible = visible
            });
        }
        return JsonSerializer.Serialize(new { globalId = enemy.GlobalID, enemyType = enemy.EnemyDataID, limbCount = rows.Count, limbs = rows });
    }

    private static string PathOf(Transform node, Transform stop)
    {
        var names = new List<string>();
        for (var current = node; current != null && current.Pointer != stop.Pointer; current = current.parent) names.Add(current.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private static Dictionary<string, Attachment> Attachments(EnemyAgent enemy)
    {
        var rows = new Dictionary<string, Attachment>(StringComparer.Ordinal);
        var root = enemy.transform;
        var nodes = enemy.GetComponentsInChildren<Transform>(true);
        for (var index = 0; nodes != null && index < nodes.Length; index++)
        {
            var node = nodes[index];
            if (node == null || node.WasCollected || !node.name.StartsWith("ForgeModel.", StringComparison.Ordinal)) continue;
            var parent = node.parent;
            if (parent == null || parent.name.StartsWith("ForgeModel.", StringComparison.Ordinal)) continue;
            rows[PathOf(node, root)] = new Attachment(node.Pointer, PathOf(node, root), node.position, node.localPosition,
                PathOf(parent, root), parent.position);
        }
        return rows;
    }

    private static string Vec(Vector3 value) => string.Format(CultureInfo.InvariantCulture, "[{0:R},{1:R},{2:R}]", value.x, value.y, value.z);

    private static string ReadFollow(EnemyAgent enemy, string phase)
    {
        var world = RecTime.WorldEpoch;
        if (phase == "begin")
        {
            var rows = Attachments(enemy);
            if (rows.Count == 0) throw new InvalidOperationException("enemy-probe.no-attachments");
            _follow = new FollowBaseline(enemy.GlobalID, enemy.Pointer, world, rows);
            return JsonSerializer.Serialize(new
            {
                phase,
                globalId = enemy.GlobalID,
                attachments = rows.Count,
                paths = rows.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray()
            });
        }
        if (phase != "end") throw new ArgumentException("enemy-probe.phase");
        var begin = _follow ?? throw new InvalidOperationException("enemy-probe.follow-begin-missing");
        if (begin.GlobalId != enemy.GlobalID || begin.Enemy != enemy.Pointer || begin.World != world)
            throw new InvalidOperationException("enemy-probe.follow-enemy-or-world-changed");
        var now = Attachments(enemy);
        var result = new List<object>();
        foreach (var (path, first) in begin.Rows.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!now.TryGetValue(path, out var last) || last.Pointer != first.Pointer)
                throw new InvalidOperationException("enemy-probe.attachment-changed:" + path);
            var attachmentDelta = last.Position - first.Position;
            var boneDelta = last.BonePosition - first.BonePosition;
            result.Add(new
            {
                attachment = path,
                bone = first.Bone,
                attachmentDelta = Vec(attachmentDelta),
                boneDelta = Vec(boneDelta),
                attachmentMoved = attachmentDelta.magnitude,
                boneMoved = boneDelta.magnitude,
                deltaDifference = (attachmentDelta - boneDelta).magnitude,
                // A rigid follow keeps the pose the attachment holds in its bone's space; a world delta alone cannot tell a
                // bone that only turned from an attachment that stayed behind.
                localPositionChange = (last.LocalPosition - first.LocalPosition).magnitude
            });
        }
        _follow = null;
        return JsonSerializer.Serialize(new { phase, globalId = enemy.GlobalID, attachments = result });
    }
}
