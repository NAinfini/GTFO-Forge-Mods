using System;
using System.Collections.Generic;
using ForgeCore;
using Gear;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>What players put down in the recording, as the game's own objects: a sentry is the owner's tool line spawned as the game's sentry, a mine
/// the recorded item spawned as the game's mine, foam the game's glue projectile. Their logic is off (a sentry neither aims nor fires by itself, a mine
/// senses nothing, neither can be picked up), so they settle nothing. The presenter notes what the recording says, a jump says it all again
/// (<see cref="Restore"/>), and <see cref="Frame"/> makes the level match. Main thread only.</summary>
internal static class Deployables
{
    private const float RetrySeconds = 0.25f, GiveUpSeconds = 30f, TriggeredSeconds = 1.5f, AimSeconds = 0.6f, ExplosionReach = 1.5f, ShotReach = 1f;
    private const uint SentryBaseItem = 97, ExplosiveMineItem = 125;

    private sealed class Piece
    {
        internal ushort Kind;
        internal ulong Key;
        internal bool Wanted, Asked, Failed;
        internal int Owner = -1;
        internal uint ItemId;
        // A sentry's own gear as the recording wrote it with its state (newer recordings), else null.
        internal string? Gear;
        internal Vector3 At;
        internal Quaternion Rotation = Vec.Identity;
        internal float AskAt, FirstAsk = -1f, RemoveAt = -1f, AimUntil;
        internal Vector3 Aim;
        internal SentryGunInstance? Sentry;
        internal MineDeployerInstance? Mine;
        internal bool LaserSet;
        // The bullet count last put on its screen, -1 none yet.
        internal long Bullets = -1;
    }

    private sealed class Blob
    {
        internal uint Id;
        internal ulong Phase;
        internal bool Live, Flown;
        internal Vector3 At, Velocity;
        internal float Volume, Expand, Scale, Strength, ShownVolume, ShownExpand;
        internal bool Splat;
        internal int Owner = -1;
    }

    private static readonly Dictionary<(ushort Kind, ulong Key), Piece> Pieces = new();
    private static readonly Dictionary<uint, Blob> Blobs = new();
    private static readonly List<(ushort, ulong)> PieceGone = new();
    private static readonly List<uint> BlobGone = new();

    /// <summary>The recording says this about a sentry, a mine or a foam blob; <paramref name="read"/> stands after the place. <paramref name="live"/>: while
    /// playing, as opposed to the state at the moment a jump lands on.</summary>
    internal static void Note(ushort kind, ulong key, ulong value, ulong extra, Vector3 at, PayloadReader read, bool live)
    {
        if (kind == EventKind.Foam) { NoteFoam(key, value, extra, at, read, live); return; }
        if (!Pieces.TryGetValue((kind, key), out var piece)) Pieces[(kind, key)] = piece = new Piece { Kind = kind, Key = key, ItemId = ExplosiveMineItem };
        piece.At = at;
        piece.Owner = extra == 0 ? -1 : (int)(extra - 1) & 3;
        var (ex, ey, ez) = read.Pos();
        piece.Rotation = Quaternion.Euler(ex, ey, ez);
        if (read.U() is var item && item != 0) piece.ItemId = (uint)item;
        if (kind == EventKind.Sentry && read.Text() is { Length: > 0 } gear) piece.Gear = gear;
        piece.Wanted = value == 0;
        // A triggered mine stands until it blows up (a moment later, its explosion takes it away); a jump past that has no mine.
        if (kind == EventKind.Mine && value == 1 && live) { piece.Wanted = true; piece.RemoveAt = ForgeCore.Frame.UnscaledTime + TriggeredSeconds; }
    }

    private static void NoteFoam(ulong key, ulong value, ulong extra, Vector3 at, PayloadReader read, bool live)
    {
        var id = (uint)key;
        if (!Blobs.TryGetValue(id, out var blob)) Blobs[id] = blob = new Blob { Id = id };
        var (vx, vy, vz) = read.Pos();
        var expand = read.U() / 1000f;
        var scale = read.U() / 1000f;
        var strength = read.U() / 1000f;
        var splat = read.U() == 1;
        var owner = read.U();
        // A blob in the air at the moment of a jump comes back when it lands.
        blob.Phase = !live && value == 0 ? 2 : value;
        if (value == 2) return;
        blob.At = at;
        blob.Volume = extra / 1000f;
        blob.Expand = expand;
        blob.Scale = scale;
        if (value != 0) return;
        blob.Velocity = new Vector3(vx, vy, vz);
        blob.Strength = strength;
        blob.Splat = splat;
        blob.Owner = owner == 0 ? -1 : (int)(owner - 1) & 3;
    }

    /// <summary>After a jump: everything as the recording has it at that moment, nothing of what the old moment had.</summary>
    internal static void Restore(WorldState state)
    {
        _lightsInstant = true;
        foreach (var piece in Pieces.Values) { piece.Wanted = false; piece.RemoveAt = -1f; }
        foreach (var blob in Blobs.Values) blob.Phase = 2;
        foreach (var ((kind, _), payload) in state.Objects)
        {
            if (kind != EventKind.Sentry && kind != EventKind.Mine && kind != EventKind.Foam) continue;
            var read = new PayloadReader(payload);
            var key = read.U();
            var value = read.U();
            var extra = read.U();
            var (x, y, z) = read.Pos();
            var at = new Vector3(x, y, z);
            Note(kind, key, value, extra, at, read, false);
        }
    }

    /// <summary>The replay's own sentry standing within <see cref="ShotReach"/> of <paramref name="at"/> (where the recording's sentry stood), or null while there is none.</summary>
    internal static SentryGunInstance? SentryNear(Vector3 at)
    {
        SentryGunInstance? found = null; var best = ShotReach;
        foreach (var piece in Pieces.Values)
        {
            if (piece.Kind != EventKind.Sentry || piece.Sentry == null || piece.Sentry.WasCollected) continue;
            var distance = Vector3.Distance(piece.Sentry.transform.position, at);
            if (distance <= best) { best = distance; found = piece.Sentry; }
        }
        return found;
    }

    /// <summary>A recorded mine explosion takes the nearest mine away with it.</summary>
    internal static void Exploded(Vector3 at)
    {
        Piece? nearest = null; var best = ExplosionReach;
        foreach (var piece in Pieces.Values)
        {
            if (piece.Kind != EventKind.Mine || !piece.Wanted) continue;
            var distance = Vector3.Distance(piece.At, at);
            if (distance <= best) { best = distance; nearest = piece; }
        }
        if (nearest != null) nearest.Wanted = false;
    }

    /// <summary>A recorded sentry shot: the game's own sentry turns that way and shoots, the way a peer draws it (no damage). False when the replay has no such sentry.</summary>
    /// <paramref name="audioId"/> and <paramref name="fireMode"/>: the recording's; when it has none (0), the audio setting and fire mode of the sentry's recorded
    /// gear (the two gear parts the game sets the sentry's firing up with), and none for a sentry with no recorded gear.
    internal static bool Shot(ulong key, Vector3 at, Vector3 direction, ref ulong audioId, ref int fireMode, out Vector3 muzzle)
    {
        muzzle = at;
        if (!Pieces.TryGetValue((EventKind.Sentry, key), out var piece))
            foreach (var other in Pieces.Values)
                if (other.Kind == EventKind.Sentry && other.Sentry != null && Vector3.Distance(other.At, at) <= ShotReach) { piece = other; break; }
        if (piece?.Sentry == null || piece.Sentry.WasCollected) return false;
        piece.Aim = direction;
        piece.AimUntil = ForgeCore.Frame.UnscaledTime + AimSeconds;
        try
        {
            if (audioId == 0 && piece.Gear != null)
            {
                var gear = new GearIDRange(piece.Gear);
                audioId = gear.GetCompID(eGearComponent.AudioSetting);
                fireMode = (int)gear.GetCompID(eGearComponent.FireMode);
            }
            var firing = piece.Sentry.m_firingComp?.TryCast<SentryGunInstance_Firing_Bullets>();
            if (firing == null) return false;
            firing.FireBullet(false, false);
            if (firing.MuzzleAlign != null) muzzle = firing.MuzzleAlign.position;
            return true;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A replayed sentry could not shoot: " + error.Message); return false; }
    }

    private static readonly Dictionary<ulong, uint> LastBurstSound = new();

    /// <summary>The sound events of one recorded sentry shot, from the weapon audio block the recording names, the way a peer's machine draws it
    /// (BulletWeaponSynced.Fire posts every entry of the list, the body, mechanics and tail layers): the per-shot list of an automatic or a burst
    /// gun, the 3D fire list of a semi gun. A burst gun whose block does not ask for a sound per shot sounds once per burst. Null when the block or
    /// its list has none.</summary>
    internal static Il2CppSystem.Collections.Generic.List<string>? FireSound(ulong sentry, uint audioId, int fireMode, uint timeMs)
    {
        var block = GameData.WeaponAudioDataBlock.GetBlock(audioId);
        if (block == null) return null;
        var list = PerShot(block, fireMode);
        if (list == null || list.Count == 0) return null;
        if (fireMode is 1 or 3 or 12 && !block.TriggerBurstAudioForEachShot)
        {
            if (LastBurstSound.TryGetValue(sentry, out var last) && timeMs - last < BurstGapMs) { LastBurstSound[sentry] = timeMs; return null; }
            LastBurstSound[sentry] = timeMs;
        }
        return list;
    }
    private const uint BurstGapMs = 400;

    // eWeaponFireMode: Auto 2 and sentry Auto 11 use the synced automatic per-shot list, Burst 1, SemiBurst 3 and sentry Burst 12 the burst one, the rest the semi list.
    private static Il2CppSystem.Collections.Generic.List<string>? PerShot(GameData.WeaponAudioDataBlock block, int fireMode) => fireMode switch
    {
        2 or 11 => block.eventOnSyncedAutoFirePerShot3D,
        1 or 3 or 12 => block.eventOnSyncedBurstFirePerShot3D,
        _ => block.eventOnSemiFire3D,
    };

    private static readonly Dictionary<string, Il2CppSystem.Collections.Generic.List<string>?> ShotLists = new();

    /// <summary>A player's shot: the recording names one event of the gun's per-shot list (the first, the body layer); the whole list is the one of the
    /// weapon audio block that holds that name, so the shot sounds with all its layers. Null when no block holds it (the name alone then plays).</summary>
    internal static Il2CppSystem.Collections.Generic.List<string>? ShotList(string name)
    {
        if (ShotLists.TryGetValue(name, out var found)) return found;
        found = null;
        foreach (var block in GameData.WeaponAudioDataBlock.GetAllBlocks())
        {
            foreach (var list in new[] { block.eventOnSemiFire3D, block.eventOnSyncedAutoFirePerShot3D, block.eventOnSyncedBurstFirePerShot3D })
                if (list != null && list.Contains(name)) { found = list; break; }
            if (found != null) break;
        }
        ShotLists[name] = found;
        return found;
    }

    /// <summary>Per frame: makes the level match what was noted.</summary>
    internal static void Frame(uint nowMs)
    {
        WorldStateSetter.KeepMusic();
        var now = ForgeCore.Frame.UnscaledTime;
        PieceGone.Clear();
        foreach (var (id, piece) in Pieces)
        {
            try
            {
                if (piece.RemoveAt >= 0f && now >= piece.RemoveAt) { piece.Wanted = false; piece.RemoveAt = -1f; }
                if (!piece.Wanted)
                {
                    if (Remove(piece)) PieceGone.Add(id);
                    continue;
                }
                if (piece.Kind == EventKind.Sentry) KeepSentry(piece, nowMs, now); else KeepMine(piece, now);
            }
            catch (Exception error) { Skipped.Note("a replayed " + (piece.Kind == EventKind.Sentry ? "sentry" : "mine") + " failed", error.Message); piece.Failed = true; }
        }
        foreach (var id in PieceGone) Pieces.Remove(id);
        KeepFoam();
    }

    // True when nothing is left of it, or a spawn is on its way (then the callback removes it).
    private static bool Remove(Piece piece)
    {
        if (piece.Asked && piece.Kind == EventKind.Sentry && piece.Sentry == null) return false;
        var replicator = piece.Sentry != null && !piece.Sentry.WasCollected ? piece.Sentry.Replicator : piece.Mine != null && !piece.Mine.WasCollected ? piece.Mine.Replicator : null;
        if (replicator != null) Items.Despawn(replicator);
        return true;
    }

    private static bool Waited(Piece piece, float now, string what)
    {
        piece.AskAt = now + RetrySeconds;
        if (piece.FirstAsk < 0f) piece.FirstAsk = now;
        if (now - piece.FirstAsk < GiveUpSeconds) return false;
        Skipped.Note("a replayed sentry could not be stood in " + GiveUpSeconds + " s", what);
        piece.Failed = true;
        return true;
    }

    private static void KeepSentry(Piece piece, uint nowMs, float now)
    {
        if (piece.Sentry != null && !piece.Sentry.WasCollected)
        {
            // The game's spawn adds the sentry's ground offset to the place it is given (SentryGunInstance_Sync.OnSpawn), and the recorded place, read after a spawn, has it already.
            var seat = piece.Sentry.transform;
            if (Vec.SqrLength(seat.position - piece.At) > 0.0001f) seat.position = piece.At;
            // Nothing else turns it: its own update is off. The game's turning towards a target, smoothed, only while the recording shoots.
            if (now < piece.AimUntil) piece.Sentry.UpdateRotation(true, false, piece.Aim);
            return;
        }
        if (piece.Asked || piece.Failed || now < piece.AskAt) return;
        // Its own recorded gear, else the owner's (or anyone's) tool line, and where the file holds no
        // sentry in anyone's gear (a header written before that player's gear arrived), the game's own first sentry stands in its place.
        var line = piece.Gear ?? SentryLine(piece.Owner, nowMs) ?? StockSentry();
        if (line == null)
        {
            Skipped.Note("a replayed sentry could not be stood", "the recording names no sentry gear and the game lists none");
            piece.Failed = true;
            return;
        }
        var gear = new GearIDRange(line);
        var reading = ForgeCore.Gear.Prepare(gear);
        if (reading.Status != GearAssetStatus.Ready || ForgeCore.Gear.PrepareSentry().Status != GearAssetStatus.Ready) { Waited(piece, now, reading.Detail); return; }
        var owner = OwnerAgent(piece.Owner);
        if (owner == null) { Waited(piece, now, "no player's class ammunition is readable"); return; }
        var node = NodeNear(piece.At, piece.Rotation, piece.Owner);
        if (node == null) { Waited(piece, now, "no course node near " + piece.At); return; }
        piece.Asked = true;
        ForgeCore.Gear.SpawnSentry(gear, SentryMade(piece), piece.At, piece.Rotation, node, owner, 1f, 1f);
    }

    private static ItemReplicationManager.delItemCallback SentryMade(Piece piece)
        => new Action<ISyncedItem, PlayerAgent>((item, _) =>
        {
            try
            {
                var sentry = item.TryCast<SentryGunInstance>();
                if (sentry == null) { Plugin.Logger.ErrorOnce("The replay's sentry gear spawned something that is not a sentry."); return; }
                sentry.enabled = false;
                if (sentry.m_interactPickup != null) HoldPoint.Disable(sentry.m_interactPickup);
                var warpable = sentry.TryCast<IWarpableObject>();
                if (warpable != null) Dimension.RemoveWarpable(warpable);
                piece.Sentry = sentry;
                piece.Bullets = -1;
                if (!Pieces.ContainsKey((piece.Kind, piece.Key))) Items.Despawn(sentry.Replicator);
            }
            catch (Exception error) { Plugin.Logger.ErrorOnce("The replay's sentry could not be set up: " + error.Message); }
        });

    // The tool line of the owner at this moment, else any sentry anyone ever carried (a sentry stays after its owner changed tools).
    private static string? SentryLine(int owner, uint nowMs)
    {
        if (owner >= 0 && IsSentry(Puppeteer.LoadoutOf(owner), out var line)) return line;
        var header = Replayer.Pending?.Header;
        var presence = Replayer.Report?.Presence;
        if (presence != null)
            for (var i = presence.Count - 1; i >= 0; i--)
                if ((owner < 0 || presence[i].Slot == owner) && presence[i].Loadout != null && IsSentry(presence[i].Loadout!, out line)) return line;
        if (header != null)
            foreach (var player in header.Players)
                if (IsSentry(player.Loadout, out line)) return line;
        return owner >= 0 ? SentryLine(-1, nowMs) : null;
    }

    private static string? StockSentry()
    {
        foreach (var gear in GearManager.GetAllGearForSlot(InventorySlot.GearClass))
            if (gear != null && gear.GetCompID(eGearComponent.BaseItem) == SentryBaseItem) return gear.ToJSON().Replace("\n", " ");
        return null;
    }

    private static bool IsSentry(string loadout, out string line)
    {
        line = "";
        var lines = loadout.Split('\n');
        if (lines.Length <= Puppeteer.ToolLine) return false;
        line = lines[Puppeteer.ToolLine].Trim();
        return line.Length > 0 && new GearIDRange(line).GetCompID(eGearComponent.BaseItem) == SentryBaseItem;
    }

    private static PlayerAgent? OwnerAgent(int slot)
    {
        if (slot >= 0 && Puppeteer.PlayerOf(slot)?.Agent is { } puppet && ForgeCore.Gear.SentryAmmoReady(puppet)) return puppet;
        foreach (var agent in PlayerManager.PlayerAgentsInLevel)
            if (agent != null && ForgeCore.Gear.SentryAmmoReady(agent)) return agent;
        return null;
    }

    // The course node a placed thing is spawned in. A mine sits on a wall and a sentry at a room's edge, where the point itself is in no node: the node beside it, else its owner's, else the observer's.
    private static readonly Vector3 Right = Vec.Of(1f, 0f, 0f);

    private static AIGraph.AIG_CourseNode? NodeNear(Vector3 at, Quaternion rotation, int owner)
    {
        if (Dimension.TryGetCourseNodeFromPos(at, out var node) && node != null) return node;
        var directions = new[] { rotation * Vec.Forward, rotation * Vec.Up, rotation * Right, Vec.Up };
        foreach (var distance in new[] { 0.25f, 0.5f, 1f })
            foreach (var direction in directions)
            {
                if (Dimension.TryGetCourseNodeFromPos(at + direction * distance, out node) && node != null) return node;
                if (Dimension.TryGetCourseNodeFromPos(at - direction * distance, out node) && node != null) return node;
            }
        return (owner >= 0 ? Puppeteer.PlayerOf(owner)?.Agent?.CourseNode : null) ?? Players.Local?.CourseNode;
    }

    private static void KeepMine(Piece piece, float now)
    {
        if (piece.Mine != null && !piece.Mine.WasCollected)
        {
            if (!piece.LaserSet) { piece.LaserSet = true; LayLaser(piece.Mine); }
            return;
        }
        if (piece.Failed || now < piece.AskAt) return;
        var block = GameData.ItemDataBlock.GetBlock(piece.ItemId);
        if (block == null)
        {
            Skipped.Note("a replayed mine has no item block", piece.ItemId.ToString());
            piece.Failed = true;
            return;
        }
        var node = NodeNear(piece.At, piece.Rotation, piece.Owner);
        if (node == null) { piece.AskAt = now + RetrySeconds; return; }
        var replicator = Items.Spawn(piece.ItemId, block.inventorySlot, null, ItemMode.Instance, piece.At, piece.Rotation, node, out var refusal);
        var mine = replicator?.Item?.TryCast<MineDeployerInstance>();
        if (mine == null)
        {
            if (refusal == ItemSpawnRefusal.NotReady) { piece.AskAt = now + RetrySeconds; return; }
            Skipped.Note("a replayed mine could not be spawned", "item " + piece.ItemId + ": " + refusal);
            piece.Failed = true;
            return;
        }
        mine.m_detectionEnabled = false;
        mine.enabled = false;
        if (mine.m_interactPickup is { } pickup && pickup != null) HoldPoint.Disable(pickup);
        piece.Mine = mine;
        piece.LaserSet = false;
    }

    // The trip line as the game draws it: from the mine along its aim to the first wall its scan sees. The sensing that would do this is off.
    private static void LayLaser(MineDeployerInstance mine)
    {
        var laser = mine.m_detectionComp?.TryCast<MineDeployerInstance_Detect_Laser>();
        var align = laser?.m_lineRendererAlign;
        if (laser == null || align == null) return;
        var length = laser.m_maxLineDistance;
        if (Physics.Raycast(align.position, align.forward, out var hit, length, laser.m_scanMask)) length = hit.distance;
        laser.UpdateDetectionRange(length);
    }

    private static int _foamPasses;

    private static void KeepFoam()
    {
        var manager = ProjectileManager.Current;
        if (manager == null || Blobs.Count == 0) return;
        BlobGone.Clear();
        // One foam gun makes hundreds of blobs: a blob with nothing to do asks the game nothing, only now and then whether a landed one is still there.
        var all = manager.m_glueGunProjectiles;
        // Every 30th pass (this runs once a frame while there is foam).
        var recheck = ++_foamPasses % 30 == 0;
        foreach (var (id, blob) in Blobs)
        {
            try
            {
                if (blob.Phase == 0 && blob.Flown) continue;
                if (blob.Phase == 1 && blob.Live && blob.Volume == blob.ShownVolume && !recheck) continue;
                var live = all != null && all.ContainsKey(id);
                switch (blob.Phase)
                {
                    case 0:
                        if (live || blob.Flown) break;
                        blob.Flown = true;
                        var fire = new ProjectileManager.pFireGlue { syncID = id, position = blob.At, velocity = blob.Velocity, expandVolume = blob.Expand, glueStrengthMultiplier = blob.Strength, allowSplat = blob.Splat };
                        var owner = OwnerAgentOrAny(blob.Owner);
                        if (owner == null) break;
                        fire.owner.Set(owner);
                        manager.DoFireGlue(fire);
                        break;
                    case 1:
                        if (!live || !blob.Live)
                        {
                            manager.DoSpawnGlueStatic(new ProjectileManager.pSpawnGlueOnStatic { syncID = id, position = blob.At, volumeDesc = new GlueVolumeDesc { volume = blob.Volume, expandVolume = blob.Expand, currentScale = blob.Scale } });
                            blob.Live = true; blob.ShownVolume = blob.Volume; blob.ShownExpand = blob.Expand;
                        }
                        else if (blob.Volume < blob.ShownVolume)
                        {
                            // A jump back to a smaller foam: it cannot shrink, so it goes and is made again the next frame at the recorded size.
                            manager.DoDestroyGlue(new ProjectileManager.pDestroyGlue { syncID = id });
                            blob.Live = false;
                        }
                        else if (blob.Volume > blob.ShownVolume)
                        {
                            // Foam that merged into it: the game grows it from here.
                            all![id]?.SyncIncomingMerge(blob.Volume - blob.ShownVolume, Mathf.Max(0f, blob.Expand - blob.ShownExpand));
                            blob.ShownVolume = blob.Volume; blob.ShownExpand = blob.Expand;
                        }
                        break;
                    default:
                        if (live) manager.DoDestroyGlue(new ProjectileManager.pDestroyGlue { syncID = id });
                        BlobGone.Add(id);
                        break;
                }
            }
            catch (Exception error) { Skipped.Note("a replayed foam blob failed", error.Message); BlobGone.Add(id); }
        }
        foreach (var id in BlobGone) Blobs.Remove(id);
    }

    private sealed class Flying
    {
        internal bool Seen, Made, Failed;
        internal float AskAt, FirstAsk = -1f;
        internal SNetwork.IReplicator? Replicator;
        internal GameObject? Body;
        // Body's transform, read once when it is made: an object keeps its transform for its whole life, and Body is checked for that life first.
        internal Transform? Place;
        internal FogRepeller_Sphere? Repeller;
        internal GlowstickInstance? Glow;
    }

    private const float ThrownSnap = 3f, ThrownGlide = 0.06f;
    private static readonly Dictionary<ulong, Flying> Thrown = new();
    private static readonly List<ulong> ThrownGone = new();
    private static byte[]? _lights;
    private static bool _lightsInstant = true;

    /// <summary>Per frame, from the state alone, so a jump needs nothing else: what is thrown at this moment, and the zones' lights.</summary>
    internal static void World(WorldState state)
    {
        try { KeepThrown(state); }
        catch (Exception error) { Skipped.Note("a replayed thrown item failed", error.Message); }
        try { KeepLights(state); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The recorded zone lights could not be shown: " + error.Message); }
        try { KeepAmmo(state); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A replayed sentry's ammunition could not be shown: " + error.Message); }
    }

    // A sentry's own update is off and the spawn does not write its screen (it read 0), so its screen shows what the recording says it had: the
    // game's ammo setter writes the screen too. A recording without the number shows the ammunition the sentry was spawned with.
    private static void KeepAmmo(WorldState state)
    {
        foreach (var piece in Pieces.Values)
        {
            if (piece.Kind != EventKind.Sentry || piece.Sentry == null || piece.Sentry.WasCollected) continue;
            if (!state.Extras.TryGetValue((EventKind.SentryAmmo, piece.Key), out var payload))
            {
                if (piece.Bullets == -1) { piece.Bullets = -2; piece.Sentry.Ammo = piece.Sentry.Ammo; }
                continue;
            }
            var read = PayloadReader.Over(payload);
            read.U();
            var bullets = (long)read.U();
            if (bullets == piece.Bullets) continue;
            piece.Bullets = bullets;
            piece.Sentry.Ammo = bullets * piece.Sentry.CostOfBullet;
        }
    }

    private static void KeepLights(WorldState state)
    {
        if (!state.Extras.TryGetValue((EventKind.ZoneLights, 0UL), out var payload) || ReferenceEquals(payload, _lights)) return;
        // A keyframe block writes the same state again.
        var same = _lights != null && payload.AsSpan().SequenceEqual(_lights);
        _lights = payload;
        if (same) return;
        var map = ReplayExtras.Lights(state);
        if (map != null) WorldStates.ZoneLights(map.Modes, _lightsInstant);
        _lightsInstant = false;
    }

    // The game's own items, spawned as a thrower's machine draws them, placed by the recording: no physics and no contact, so they neither bounce, nor go off, nor settle anything.
    private static void KeepThrown(WorldState state)
    {
        foreach (var flying in Thrown.Values) flying.Seen = false;
        var now = ForgeCore.Frame.UnscaledTime;
        foreach (var thrown in ReplayExtras.ThrownNow(state))
        {
            if (!Thrown.TryGetValue(thrown.Key, out var flying)) Thrown[thrown.Key] = flying = new Flying();
            flying.Seen = true;
            if (!flying.Made && !MakeThrown(flying, thrown, now)) continue;
            if (flying.Body == null || flying.Body.WasCollected) continue;
            var target = new Vector3(thrown.X, thrown.Y, thrown.Z);
            var place = flying.Place!;
            var from = place.position;
            place.position = Vec.SqrLength(from - target) > ThrownSnap * ThrownSnap ? target : Vector3.Lerp(from, target, 1f - Mathf.Exp(-ForgeCore.Frame.UnscaledDeltaTime / ThrownGlide));
            // A glowstick's life runs on real time and it despawns itself after: held for as long as the recording has it, however long the bar stands still.
            if (flying.Glow != null && !flying.Glow.WasCollected && flying.Glow.m_state == eFadeState.One) flying.Glow.m_progression = 1f;
            if (flying.Repeller == null || flying.Repeller.WasCollected) continue;
            // A fog sphere's extra: its state in the low four bits, its range x 10 above. The item starts its sphere and resets its fields on its own first frame, so this is compared and written every frame.
            var active = (thrown.Extra & 15) is 2 or 3;
            var showing = flying.Repeller.CurrentState is eFogRepellerSphereState.Grow or eFogRepellerSphereState.Life;
            if (active)
            {
                var range = (thrown.Extra >> 4) / 10f;
                if (range > 0f) flying.Repeller.Range = range;
                flying.Repeller.InfiniteDuration = true;
                if (!showing) flying.Repeller.StartRepelling();
            }
            else if (showing) flying.Repeller.StopRepelling();
        }
        ThrownGone.Clear();
        foreach (var (key, flying) in Thrown)
        {
            if (flying.Seen) continue;
            if (flying.Replicator != null && !flying.Replicator.WasCollected && flying.Body != null && !flying.Body.WasCollected) Items.Despawn(flying.Replicator);
            ThrownGone.Add(key);
        }
        foreach (var key in ThrownGone) Thrown.Remove(key);
    }

    private static bool MakeThrown(Flying flying, ThrownState thrown, float now)
    {
        if (flying.Failed || now < flying.AskAt) return false;
        var block = GameData.ItemDataBlock.GetBlock(thrown.ItemId);
        var at = new Vector3(thrown.X, thrown.Y, thrown.Z);
        if (block == null || !Dimension.TryGetCourseNodeFromPos(at, out var node) || node == null)
        {
            // In the air between rooms a place has no node yet: asked again, and given up on only when it has no item block.
            if (block == null) { flying.Failed = true; Skipped.Note("a replayed thrown item has no item block", thrown.ItemId.ToString()); }
            else flying.AskAt = now + RetrySeconds;
            return false;
        }
        var replicator = Items.Spawn(thrown.ItemId, block.inventorySlot, null, ItemMode.Instance, at, Vec.Identity, node, out var refusal);
        var item = replicator?.Item?.TryCast<Item>();
        if (item == null)
        {
            if (refusal == ItemSpawnRefusal.NotReady)
            {
                if (flying.FirstAsk < 0f) flying.FirstAsk = now;
                if (now - flying.FirstAsk < GiveUpSeconds) { flying.AskAt = now + RetrySeconds; return false; }
            }
            flying.Failed = true;
            Skipped.Note("a replayed thrown item could not be spawned", "item " + thrown.ItemId + ": " + refusal);
            return false;
        }
        flying.Made = true;
        flying.Replicator = replicator!.TryCast<SNetwork.IReplicator>();
        flying.Body = item.gameObject;
        flying.Place = item.transform;
        foreach (var body in item.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;
        foreach (var contact in item.GetComponentsInChildren<Collider>(true)) contact.enabled = false;
        // A grenade's fuse and blast are in its own update.
        var grenade = item.TryCast<GrenadeBase>();
        if (grenade != null) grenade.enabled = false;
        flying.Repeller = item.TryCast<FogRepellerInstance>()?.m_repellerSphere;
        flying.Glow = item.TryCast<GlowstickInstance>();
        return true;
    }

    private static PlayerAgent? OwnerAgentOrAny(int slot)
        => (slot >= 0 ? Puppeteer.PlayerOf(slot)?.Agent : null) ?? Players.Local;

    /// <summary>Forgets everything; the level cleanup that follows a replay destroys the objects.</summary>
    internal static void Clear()
    {
        Pieces.Clear(); Blobs.Clear(); Thrown.Clear(); _lights = null; _lightsInstant = true;
    }
}
