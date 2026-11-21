using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Enemies;
using ForgeCore;
using Gear;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>The console's player and world actions that no existing drive entry covers, each answering one <c>name=value; ...</c> line.
/// Called by name from <c>drive_cli.py</c> (<c>call static:ForgeDevelopment.Native.PlayerActions Reload</c>). The direct forms
/// (<see cref="Reload"/>, <see cref="Swing"/>, <see cref="Throw"/>, <see cref="Posture"/>) are the game's own methods the level scenario also
/// uses; the default console path presses the matching input through <see cref="GameInputDrive"/> instead. Enemies spawned with a name stay in
/// the level after the command ends (a script spawns in one line and fires in the next); <see cref="Clear"/> despawns them.</summary>
internal static class PlayerActions
{
    private static readonly Dictionary<string, List<EnemyAgent>> Named = new(StringComparer.Ordinal);
    private static long _world = -1;

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Point(Vector3 value) => Number(value.x) + "," + Number(value.y) + "," + Number(value.z);

    internal static PlayerAgent Local => Players.Local ?? throw new InvalidOperationException("no local player: enter a level first");

    // ---- the direct forms of the player's own actions ---------------------------------------------------------------

    /// <summary>The reload as the game plays it: the wielded weapon's own animation, and the interact message the others receive.</summary>
    internal static string Reload()
    {
        var player = Local;
        var item = player.Inventory.WieldedItem ?? throw new InvalidOperationException("nothing in the hands (wielded: nothing)");
        if (item.TryCast<BulletWeapon>() == null) throw new InvalidOperationException("no gun in the hands (wielded: " + item.name + ")");
        item.TryTriggerReloadAnimationSequence();
        player.Sync.SendGenericInteract(pGenericInteractAnimation.TypeEnum.Reload, false);
        return "reload requested";
    }

    /// <summary>One swing of the wielded melee weapon (<c>MeleeWeaponFirstPerson.DoTriggerAttack</c>).</summary>
    internal static string Swing()
    {
        var melee = Local.Inventory.WieldedItem?.TryCast<MeleeWeaponFirstPerson>() ?? throw new InvalidOperationException("wielded item is not a melee weapon");
        melee.DoTriggerAttack(false);
        return "melee swing requested";
    }

    /// <summary>The throw's two native status messages: charging, then the release. The status alone throws nothing (it is what the
    /// others see); with a throwable in the hands the release is the call its own <c>Update</c> makes at the end of the release,
    /// <c>ThrowingWeapon.Throw(m_throwStrength)</c>, which leaves the item and spends a use.</summary>
    internal static string Throw(bool release)
    {
        var player = Local;
        var thrower = release ? player.Inventory.WieldedItem?.TryCast<ThrowingWeapon>() : null;
        if (thrower == null)
        {
            player.Sync.SendThrowStatus(release ? pPlayerThrowStatus.StatusEnum.Throw : pPlayerThrowStatus.StatusEnum.Charging, false);
            return "throw " + (release ? "released" : "charging");
        }
        thrower.Throw(thrower.m_throwStrength);
        return "thrown " + thrower.PublicName;
    }

    /// <summary>Crouch, Stand, Jump or Run through the game's own locomotion state change.</summary>
    internal static string Posture(string name)
    {
        var state = Enum.TryParse<PlayerLocomotion.PLOC_State>(name, false, out var parsed) ? parsed : throw new ArgumentException("unknown locomotion state " + name);
        var player = Local;
        player.Locomotion.ChangeState(state);
        return "locomotion " + player.Locomotion.m_currentStateEnum;
    }

    /// <summary>The team-down of the level scenario: lethal bullet damage to the local player.</summary>
    internal static string Down()
    {
        var player = Local;
        Combat.BulletDamage(player.Damage, 99999f, null, player.Position, Vector3.forward, Vector3.back);
        return "down requested; alive " + player.Alive;
    }

    internal static string Revive()
    {
        var player = Local;
        var data = new pSetHealthData();
        data.health.Set(20f, 20f);
        player.Locomotion.Downed.OnPlayerRevived(data);
        return "revive requested; alive " + player.Alive;
    }

    /// <summary>Spawns an item block (by name fragment or persistent id) beside the player as a pickup and takes it through the pickup's own
    /// synced interaction, the call the game's pickup makes (the level scenario's resource pack is one use). A consumable carries its uses
    /// in the pickup's custom <c>ammo</c>, as the level's own consumables do; it is given the block's <c>ConsumableAmmoMax</c>, without which
    /// the player holds it with no uses and cannot throw or place it.</summary>
    internal static string Give(string item, int count = 1)
    {
        var player = Local;
        if (!Dimension.TryGetCourseNodeFromPos(player.Position, out var node) || node == null) throw new InvalidOperationException("no course node at the player");
        var blocks = GameData.GameDataBlockBase<GameData.ItemDataBlock>.GetAllBlocks().Where(b => b.internalEnabled).ToList();
        var block = (uint.TryParse(item, out var id) ? blocks.FirstOrDefault(b => b.persistentID == id) : null)
            ?? blocks.FirstOrDefault(b => string.Equals(b.name, item, StringComparison.OrdinalIgnoreCase))
            ?? blocks.FirstOrDefault(b => b.name.Contains(item, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("no item block " + item);
        pItemData_Custom? custom = block.inventorySlot is InventorySlot.Consumable or InventorySlot.ConsumableHeavy
            ? new pItemData_Custom { ammo = block.ConsumableAmmoMax } : null;
        for (var i = 0; i < count; i++)
        {
            var made = Items.Spawn(block.persistentID, block.inventorySlot, custom, ItemMode.Pickup, player.Position + Flat(player) * 1.5f, Quaternion.identity, node, out var why)
                ?? throw new InvalidOperationException("the item was not spawned: " + why);
            var sync = made.Item?.TryCast<LevelGeneration.ItemInLevel>()?.GetSyncComponent()?.TryCast<LevelGeneration.LG_PickupItem_Sync>()
                ?? throw new InvalidOperationException("the spawned item has no pickup sync");
            // The pickup reads the replicator's current custom state, not the spawn data: commit it there first (as ForgeEngine's give does).
            if (custom is { } state) sync.SetCustomData(state, true);
            sync.AttemptPickupInteraction(LevelGeneration.ePickupItemInteractionType.Pickup, player.Owner);
        }
        return "given " + block.name + " x" + count + " (" + block.persistentID + ")";
    }

    /// <summary>The player's forward direction along the ground.</summary>
    internal static Vector3 Flat(PlayerAgent player)
    {
        var forward = player.TargetLookDir; forward.y = 0f;
        return forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
    }

    // ---- enemies ----------------------------------------------------------------------------------------------------

    /// <summary>The enemy block named exactly as asked, else the first whose name contains it (a "Tank_Boss" is not a tank), else the id.</summary>
    internal static GameData.EnemyDataBlock EnemyBlock(string family)
    {
        var blocks = GameData.GameDataBlockBase<GameData.EnemyDataBlock>.GetAllBlocks().Where(value => value.internalEnabled).ToList();
        return (uint.TryParse(family, out var id) ? blocks.FirstOrDefault(value => value.persistentID == id) : null)
            ?? blocks.FirstOrDefault(value => string.Equals(value.name, family, StringComparison.OrdinalIgnoreCase))
            ?? blocks.FirstOrDefault(value => value.name.Contains(family, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("no enemy " + family);
    }

    private static string Describe(EnemyAgent enemy) => "id=" + enemy.GlobalID + "; type=" + enemy.EnemyData?.name + "; health=" + Number(enemy.Damage.Health)
        + "; alive=" + enemy.Alive + "; pos=" + Point(enemy.Position) + "; mode=" + enemy.AI?.Mode;

    /// <summary>Spawns <paramref name="count"/> enemies through ForgeCore's spawn entry. <paramref name="at"/> is <c>front:metres</c> (along the
    /// player's facing) or <c>x,y,z</c>; <paramref name="mode"/> aggressive, hibernate or scout. Several enemies stand side by side.
    /// <paramref name="name"/> keeps them for <see cref="Find"/>. Answers the first enemy's line plus every id.</summary>
    internal static string Spawn(string enemy, int count = 1, string at = "front:8", string mode = "aggressive", string name = "")
    {
        var player = Local;
        var block = EnemyBlock(enemy);
        var agentMode = mode switch
        {
            "aggressive" => Agents.AgentMode.Agressive,
            "hibernate" => Agents.AgentMode.Hibernate,
            "scout" => Agents.AgentMode.Scout,
            _ => throw new ArgumentException("unknown mode " + mode)
        };
        var flat = Flat(player);
        Vector3 origin;
        if (at.StartsWith("front:", StringComparison.Ordinal)) origin = player.Position + flat * float.Parse(at["front:".Length..], CultureInfo.InvariantCulture);
        else
        {
            var parts = at.Split(',');
            if (parts.Length != 3) throw new ArgumentException("at must be front:metres or x,y,z");
            origin = new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        if (!EnemySpawn.Ready) throw new InvalidOperationException("the enemy allocator is not ready");
        if (_world != RecTime.WorldEpoch) { Named.Clear(); _world = RecTime.WorldEpoch; }
        var side = Vector3.Cross(Vector3.up, flat);
        var made = new List<EnemyAgent>();
        for (var i = 0; i < count; i++)
        {
            var site = origin + side * (i * 1.2f);
            if (!Dimension.TryGetCourseNodeFromPos(site, out var node) || node == null) throw new InvalidOperationException("no course node at " + Point(site));
            var spawned = EnemySpawn.Request(block.persistentID, node, agentMode, site, Quaternion.LookRotation(-flat))
                ?? throw new InvalidOperationException("spawn refused for " + block.name);
            if (agentMode == Agents.AgentMode.Agressive) spawned.AI?.SetTarget(player);
            made.Add(spawned);
        }
        if (name.Length > 0) Named[name] = made;
        return Describe(made[0]) + "; count=" + made.Count + "; ids=" + string.Join(",", made.Select(value => value.GlobalID));
    }

    /// <summary>An enemy by the name a spawn gave (the first of its group) or by GlobalID.</summary>
    private static EnemyAgent Find(string target)
    {
        if (Named.TryGetValue(target, out var group) && group.Count > 0 && group[0] != null && !group[0].WasCollected) return group[0];
        if (int.TryParse(target, out var id))
            foreach (var enemy in UnityEngine.Object.FindObjectsOfType<EnemyAgent>())
                if (enemy != null && !enemy.WasCollected && enemy.GlobalID == id) return enemy;
        throw new ArgumentException("no enemy " + target);
    }

    internal static string Enemy(string target) => Describe(Find(target));

    /// <summary>The nearest enemies within the radius, one line each.</summary>
    internal static string Enemies(float radius = 30f)
    {
        var rows = new List<string>();
        foreach (var value in ForgeCore.Enemies.Nearest(Local.Position, radius, 16))
            if (value != null) rows.Add(Describe(value));
        return rows.Count + " enemies" + string.Concat(rows.Select(row => " | " + row));
    }

    /// <summary>Points the camera at an enemy's aim point.</summary>
    internal static string Aim(string target)
    {
        var point = ForgeCore.Enemies.AimPoint(Find(target));
        return GameInputDrive.Look(point.x, point.y, point.z);
    }

    // ---- interactables, alarms and scans ----------------------------------------------------------------------------

    /// <summary>The interaction's type, object and parent names, then the level piece that owns it. The objects are named after the
    /// prefab (a security door's interaction is <c>Interact_Timed/Interaction_Open_Or_Activate/crossing</c>), so the owner's game type is
    /// what says door or terminal (<c>LG_SecurityDoor</c>, <c>LG_WeakDoor</c>, <c>LG_ComputerTerminal</c>).</summary>
    private static string NameOf(Interact_Base interact)
    {
        var owner = OwnerOf(interact);
        return SelfOf(interact) + (owner.Length == 0 ? "" : "@" + owner);
    }

    private static string SelfOf(Interact_Base interact)
    {
        var parent = interact.transform.parent;
        return interact.GetIl2CppType().Name + "/" + interact.gameObject.name + (parent == null ? "" : "/" + parent.name);
    }

    /// <summary>How well an interaction answers a <c>name=</c> (lower is better; -1 does not match). <c>text@Owner</c> asks for an owner
    /// type containing <c>Owner</c> and, when <c>text</c> is given, own names containing it. A bare <c>text</c> matches either, and an
    /// interaction whose owner type contains it (rank 0) wins over one whose own names only do (rank 1): <c>name=Terminal</c> is the
    /// terminal's own interaction before a prop named after terminals. Every comparison ignores case.</summary>
    private static int Rank(Interact_Base interact, string name)
    {
        if (name.Length == 0) return 0;
        var owner = OwnerOf(interact);
        bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            string self = name[..at], wanted = name[(at + 1)..];
            return Has(owner, wanted) && (self.Length == 0 || Has(SelfOf(interact), self)) ? 0 : -1;
        }
        return Has(owner, name) ? 0 : Has(SelfOf(interact), name) ? 1 : -1;
    }

    /// <summary>The nearest level-generation component at or up to <see cref="OwnerDepth"/> objects above the interaction. The terminal
    /// system's registration (<c>LG_GenericTerminalItem</c>, which every door, controller and item with a terminal name carries) is not
    /// the piece that owns it, so it is passed over: otherwise <c>name=Terminal</c> takes a bulkhead door controller for a terminal.</summary>
    private static string OwnerOf(Interact_Base interact)
    {
        var node = interact.transform;
        for (var depth = 0; node != null && depth <= OwnerDepth; depth++, node = node.parent)
            foreach (var component in node.GetComponents<MonoBehaviour>())
            {
                var type = component == null ? null : component.GetIl2CppType();
                if (type != null && type.Namespace == "LevelGeneration" && type.Name != "LG_GenericTerminalItem") return type.Name;
            }
        return "";
    }

    // Deep enough for a door's or terminal's own root, short of the area and zone objects every interaction sits under.
    private const int OwnerDepth = 4;

    private static Vector3 PointOf(Interact_Base interact)
        => interact.m_colliderToOwn != null && !interact.m_colliderToOwn.WasCollected ? interact.m_colliderToOwn.bounds.center : interact.transform.position;

    /// <summary>The best active interaction (the game's <c>Interact_Base</c>: doors, terminals, pickups, levers, timed holds) for
    /// <paramref name="name"/> by <see cref="Rank"/> (empty: any), nearest first within a rank, within <paramref name="radius"/> metres of
    /// <paramref name="near"/> (<c>x,y,z</c>; empty: the player). The console looks at its point and presses Use, so the game's own
    /// selection and input take it. Answers its name, point, distance from the player's eye and hold time.</summary>
    internal static string Interactable(string name, string near, float radius)
    {
        var player = Local;
        var best = FindInteraction(player, name, near, radius);
        var point = PointOf(best);
        var front = FrontOf(best, point);
        var posture = PostureFor(player, point, front, out var eye, out var height);
        var clear = StandFor(best, player, front, eye, out var stand);
        return "name=" + NameOf(best) + "; point=" + Point(point) + "; distance=" + Number(Vector3.Distance(player.EyePosition, point))
            + "; hold=" + Number(best.TryCast<Interact_Timed>()?.InteractDuration ?? 0f) + "; blocked=" + best.IsBlocked.ToString().ToLowerInvariant()
            + "; front=" + (front is { } facing ? Point(facing) : "none") + "; height=" + Number(height) + "; posture=" + posture + "; eye=" + Number(eye)
            + "; stand=" + Point(stand) + "; standClear=" + clear.ToString().ToLowerInvariant();
    }

    /// <summary>Why the game would not take the interaction <see cref="Interactable"/> finds with the same arguments from where the player
    /// is now, judged as its selection judges: the angle between the camera's look and the line to the interaction's point
    /// (<c>aimAngle</c>, degrees, turn and pitch together) and <c>reason</c>: <c>too-low</c> or <c>too-high</c> when its collider is outside
    /// the selection sphere below or above the eye, <c>too-far</c> when it is outside level with it, <c>blocked</c> when the line of sight
    /// test fails, <c>not-aimed</c> when the look is more than <see cref="AimTolerance"/> degrees off, else <c>none</c>.</summary>
    internal static string Aimed(string name, string near, float radius)
    {
        var player = Local;
        var best = FindInteraction(player, name, near, radius);
        var target = PointOf(best);
        var camera = player.FPSCamera ?? throw new InvalidOperationException("input.camera-unavailable");
        var eye = camera.Position;
        var angle = Vector3.Angle(camera.Forward, target - eye);
        var collider = best.m_colliderToOwn != null && !best.m_colliderToOwn.WasCollected ? best.m_colliderToOwn : null;
        var distance = collider != null ? Vector3.Distance(eye, collider.ClosestPoint(eye)) : Vector3.Distance(eye, target);
        var reason = distance > Reach(player.Interaction.m_searchRadius, eye, target)
            ? target.y < eye.y - LevelBand ? "too-low" : target.y > eye.y + LevelBand ? "too-high" : "too-far"
            : !Sees(eye, best) ? "blocked" : angle > AimTolerance ? "not-aimed" : "none";
        return "aimAngle=" + Number(angle) + "; eye=" + Point(eye) + "; reach=" + Number(distance) + "; reason=" + reason;
    }

    private static Interact_Base FindInteraction(PlayerAgent player, string name, string near, float radius)
    {
        var anchor = player.Position;
        if (near.Length > 0)
        {
            var parts = near.Split(',');
            if (parts.Length != 3) throw new ArgumentException("near must be x,y,z or empty");
            anchor = new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        Interact_Base? best = null;
        var bestDistance = radius;
        var bestRank = int.MaxValue;
        foreach (var interact in UnityEngine.Object.FindObjectsOfType<Interact_Base>())
        {
            if (interact == null || interact.WasCollected || !interact.IsActive || !interact.gameObject.activeInHierarchy) continue;
            var distance = Vector3.Distance(anchor, PointOf(interact));
            if (distance > radius) continue;
            var rank = Rank(interact, name);
            if (rank < 0 || rank > bestRank || rank == bestRank && distance > bestDistance) continue;
            best = interact; bestRank = rank; bestDistance = distance;
        }
        return best ?? throw new InvalidOperationException("interact.not-found: no active interaction" + (name.Length > 0 ? " named " + name : "") + " within " + Number(radius) + " m of " + Point(anchor));
    }

    // The game's selection (PlayerInteraction.FixedUpdate and UpdateWorldInteractions): the colliders overlapping a sphere at the camera
    // of m_searchRadius plus Min(|look direction y|, LookPitchGrowth), on MASK_PLAYER_INTERACT_SPHERE; of their interactions, the one whose
    // transform position is on screen nearest the centre, and one with RequireCollisionCheck only when the line from the camera to that
    // position against MASK_PLAYER_INTERACT_BLOCKERS meets nothing or its own object first.
    private const float LookPitchGrowth = 0.5f;
    private static readonly float[] StandRings = { 0.5f, 0.8f, 1.1f };
    private const int StandDirections = 12;
    // A stand with a front: the ground point within 60 degrees either side of it (cos 60).
    private const float FrontCone = 0.5f;
    // Aimed: a look further off than this is reported not aimed; a point within this height of the eye is level with it.
    private const float AimTolerance = 10f;
    private const float LevelBand = 0.3f;

    /// <summary>Stand or crouch for an interaction at <paramref name="target"/>, with the eye height above the feet that posture has
    /// (<paramref name="eye"/>) and the point's height above the floor under it (<paramref name="height"/>). The eye heights are the
    /// player's own <c>PlayerDataBlock.camPosDefault.y</c> (standing) and <c>camPosCrouch.y</c> (crouched); without the block both are the
    /// eye's height above the feet now. One with a <paramref name="front"/> (a terminal screen) is always used standing. Else the player
    /// crouches only when a standing eye above the nearest stand ring cannot have the point in the game's selection sphere even at its
    /// largest (looking down: m_searchRadius plus LookPitchGrowth): eye minus height above
    /// sqrt((search + LookPitchGrowth)^2 - ring^2).</summary>
    private static string PostureFor(PlayerAgent player, Vector3 target, Vector3? front, out float eye, out float height)
    {
        var now = Mathf.Max(0.5f, player.EyePosition.y - player.Position.y);
        var data = player.PlayerData;
        float standing = data != null && data.camPosDefault.y > 0.5f ? data.camPosDefault.y : now,
            crouching = data != null && data.camPosCrouch.y > 0.3f ? data.camPosCrouch.y : now;
        var floor = ForgeCore.Level.TrySnap(new Vector3(target.x, target.y - standing, target.z), standing, out var ground) ? ground.y : player.Position.y;
        height = target.y - floor;
        var widest = player.Interaction.m_searchRadius + LookPitchGrowth;
        var reachDown = Mathf.Sqrt(Mathf.Max(0f, widest * widest - StandRings[0] * StandRings[0]));
        var low = front == null && standing - height > reachDown;
        eye = low ? crouching : standing;
        return low ? "crouch" : "stand";
    }

    /// <summary>The way a terminal's screen faces, flat on the floor (unit), or none for anything else. While a player uses a terminal the
    /// game puts the camera at the terminal's own <c>LG_ComputerTerminal.m_CameraAlign</c>, so that point is on the side the screen is read
    /// from; only its position against the screen counts, not the transform's axes. None too when it sits straight above the screen.</summary>
    private static Vector3? FrontOf(Interact_Base interact, Vector3 target)
    {
        var align = interact.TryCast<Interact_ComputerTerminal>()?.m_terminal?.m_CameraAlign;
        if (align == null || align.WasCollected) return null;
        var outward = align.position - target;
        outward.y = 0f;
        return outward.sqrMagnitude < 0.0025f ? null : outward.normalized;
    }

    /// <summary>Where the player stands to use <paramref name="interact"/> as a player would: feet on walkable ground (the navigation
    /// mesh), and from the eye there (<paramref name="eye"/> above the feet, <see cref="PostureFor"/>) looking at the collider's centre, the collider inside
    /// the game's selection sphere for that look (<see cref="Reach"/>) and <see cref="Sees"/> true, so nothing stands between. With a
    /// <paramref name="front"/> (<see cref="FrontOf"/>) only ground within <see cref="FrontCone"/> of it counts, so a terminal is used
    /// from in front of its screen, never its side or back. Tries rings of points around the interaction, the nearest ring first and on
    /// a ring the point nearest the player. False, with the nearest walkable point of the rings, when no point sees the interaction.</summary>
    private static bool StandFor(Interact_Base interact, PlayerAgent player, Vector3? front, float eye, out Vector3 stand)
    {
        var target = PointOf(interact);
        var collider = interact.m_colliderToOwn != null && !interact.m_colliderToOwn.WasCollected ? interact.m_colliderToOwn : null;
        var search = player.Interaction.m_searchRadius;
        Vector3 blind = player.Position;
        var blindDistance = float.MaxValue;
        foreach (var ring in StandRings)
        {
            Vector3 seen = default;
            var seenDistance = float.MaxValue;
            for (var i = 0; i < StandDirections; i++)
            {
                var angle = i * Mathf.PI * 2f / StandDirections;
                var wanted = new Vector3(target.x + Mathf.Cos(angle) * ring, target.y - eye, target.z + Mathf.Sin(angle) * ring);
                if (!ForgeCore.Level.TrySnap(wanted, eye, out var ground)) continue;
                // The snapped ground, not the wanted point: the mesh nearest a point inside the housing can lie beside it.
                var off = new Vector3(ground.x - target.x, 0f, ground.z - target.z);
                if (front is { } facing && Vector3.Dot(off.normalized, facing) < FrontCone) continue;
                var head = ground + Vector3.up * eye;
                var toPlayer = Vector3.Distance(ground, player.Position);
                var distance = collider != null ? Vector3.Distance(head, collider.ClosestPoint(head)) : Vector3.Distance(head, target);
                if (distance > Reach(search, head, target)) continue;
                if (Sees(head, interact))
                {
                    if (toPlayer < seenDistance) { seenDistance = toPlayer; seen = ground; }
                }
                else if (toPlayer < blindDistance) { blindDistance = toPlayer; blind = ground; }
            }
            if (seenDistance < float.MaxValue) { stand = seen; return true; }
        }
        stand = blind;
        return false;
    }

    /// <summary>The radius of the game's selection sphere while the camera at <paramref name="eye"/> looks at <paramref name="target"/>.</summary>
    private static float Reach(float search, Vector3 eye, Vector3 target)
        => search + Mathf.Min(Mathf.Abs((target - eye).normalized.y), LookPitchGrowth);

    /// <summary>The game's line-of-sight test for the interaction, as its selection makes it: none unless RequireCollisionCheck, else the
    /// line from the camera to the interaction's transform position against MASK_PLAYER_INTERACT_BLOCKERS meets nothing or its own object.</summary>
    private static bool Sees(Vector3 eye, Interact_Base interact)
    {
        if (!interact.RequireCollisionCheck) return true;
        if (!Physics.Linecast(eye, interact.transform.position, out var hit, LayerManager.MASK_PLAYER_INTERACT_BLOCKERS)) return true;
        return hit.collider != null && hit.collider.gameObject.Pointer == interact.gameObject.Pointer;
    }

    /// <summary>The interaction the game's own player interaction has selected now (what Use would take), or none.</summary>
    internal static string Selected()
    {
        var selected = Local.Interaction?.m_bestSelectedInteract?.TryCast<Interact_Base>();
        return "selected=" + (selected == null || selected.WasCollected ? "none" : NameOf(selected));
    }

    /// <summary>The level's scans and alarms (the game's own <c>ChainedPuzzleManager</c> instances) and the survival waves the
    /// mastermind runs. Counts are of what is running now; <paramref name="all"/> also lists the idle and solved ones. One row per
    /// thing in <c>list</c>: <c>puzzle:&lt;uid&gt;:&lt;state&gt;[,alarm][,solved]:&lt;data id&gt;</c> and <c>wave:&lt;event id&gt;:&lt;state&gt;</c>.</summary>
    internal static string Alarms(bool all = false)
    {
        int active = 0, alarms = 0, waves = 0;
        var rows = new List<string>();
        var instances = ChainedPuzzles.ChainedPuzzleManager.Current?.m_instances;
        if (instances != null)
            foreach (var puzzle in instances)
            {
                if (puzzle == null || puzzle.WasCollected) continue;
                var running = puzzle.IsActive;
                var alarm = puzzle.Data?.TriggerAlarmOnActivate == true;
                if (running) { active++; if (alarm) alarms++; }
                if (running || all)
                    rows.Add("puzzle:" + puzzle.m_puzzleUID + ":" + (running ? "active" : "idle") + (alarm ? ",alarm" : "") + (puzzle.IsSolved ? ",solved" : "")
                        + ":" + (puzzle.Data?.persistentID.ToString(CultureInfo.InvariantCulture) ?? "none"));
            }
        foreach (var wave in CaptureWorld.SurvivalWaves() ?? new List<SurvivalWave>())
        {
            waves++;
            rows.Add("wave:" + wave.EventID.ToString(CultureInfo.InvariantCulture) + ":" + wave.m_state);
        }
        return "active=" + active + "; alarms=" + alarms + "; scans=" + (active - alarms) + "; waves=" + waves + "; list=" + (rows.Count == 0 ? "none" : string.Join("|", rows));
    }

    /// <summary>Despawns every enemy a named spawn made (the console's own, never a level's).</summary>
    internal static string Clear()
    {
        var count = 0;
        foreach (var enemy in Named.Values.SelectMany(group => group))
            if (enemy != null && !enemy.WasCollected && enemy.Sync?.Replicator is { } replicator) { replicator.Despawn(); count++; }
        Named.Clear();
        return "cleared " + count;
    }
}
