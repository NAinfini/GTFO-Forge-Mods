using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// Where the party stands when the start elevator releases it (contract §4.5 M13). The native seat exit
/// (PLOC_InElevator.Exit, build 20403457 RVA 0x14CBBA0) puts the local player's feet at the seat camera align less the
/// camera's position offset (FPSCamera.m_posOffset); a standing player's offset is PlayerDataBlock.camPosDefault, which
/// PLOC_Stand.Enter hands to FPSCamera.SetNewPosOffset. At release the offset still reads zero, so the feet are put at
/// the seat's eye height and the player falls from there to the floor (2.5–2.7 m in the vanilla shafts, more in an
/// authored start room whose floor is lower than the donor shaft's, and about 5 m under an authored landing point).
///
/// With an authored landing the cage's end reference (the pose the native End animation descends to and the pose the
/// cage is committed to before players are released) is set so the centre of the seats as they stand at the seat
/// exit (<see cref="SeatExitCentre"/>), at the cage floor (the native shaft floor height above the cage origin), is on
/// the authored room-local point and facing, so the machine stands on the authored ground as in a vanilla shaft and
/// the party is released around the authored point. The seats are not where they will be released while the cage
/// descends: read at the start of End they sit within a metre of the cage origin, and the native landing animation
/// then carries them about 5.7 m forward, so a centre read then put the cage origin, not the seats, on the point. The
/// landing, seat-exit and cargo flows stay native: the seats and the cargo align are children of the cage, so they
/// follow the cage pose. The point may be in the air or in a shaft; it is never snapped, projected or moved.
///
/// At the local seat exit the camera offset native Exit subtracts is set so the feet land on the standing height: the
/// authored point's height, straight below the player's own seat, or else, in a self-built start room
/// only, the solid floor the player collides with below the native release point. A vanilla start room with neither an
/// authored landing nor a player's own point keeps the vanilla release untouched. The eye stays where the seat put it and the native Stand transition
/// then brings the camera down to standing eye height, so nobody falls. Only levels a Forge plan generates are touched.
///
/// The editor writes either a party row or per-player rows, never both; a player without a row of their own lands on the native landing when the level has no party row. A player with an own landing row (its slot is the native PlayerSlotIndex, which every machine reads alike) is put
/// on that point instead, by the same camera offset, on the machine that exits that player: each machine its local
/// player, the host its bots (which have no camera, so the host uses the game's replicated warp once the bot's
/// locomotion accepts one). Nothing new is sent. The native Stand transition then carries the camera from the seat
/// to the point; the settle record and <see cref="LandingReadback"/> read how far it glided and the largest step.
/// A room that is missing or ambiguous, an end reference that cannot be read or a pose that does not read back refuses
/// by name and leaves the native landing alone.
/// </summary>
internal static class EntryLandingApplier
{
    /// <summary>Metres the end reference may differ from the authored point after the write.</summary>
    internal const double PositionTolerance = .01;

    private static IReadOnlyList<EntryLanding> _rows = Array.Empty<EntryLanding>();
    private static Func<long> _epoch = () => 0;
    private static Pose? _authored;
    private static bool _active;

    /// <summary>The end reference pose (<c>Position</c>, <c>Rotation</c>) and the authored party landing point.
    /// <c>Epoch</c> is the world generation the pose was made for: the ride object (and its native address) outlives
    /// the level, so a pose is only ever in force for the generation that set it.</summary>
    internal readonly record struct Pose(IntPtr Ride, long Epoch, Vector3 Position, Quaternion Rotation, Vector3 Landing);

    internal static void Activate(IReadOnlyList<EntryLanding> rows, Func<long> epoch)
    {
        _rows = rows; _epoch = epoch; _authored = null; _active = true;
    }

    internal static void Forget()
    {
        _rows = Array.Empty<EntryLanding>(); _authored = null; _active = false;
        EndSettle(); _bots.Clear(); _readback = "none";
    }

    private static (RoomHit? Room, string? Refusal) Place(RoomLocator locator, long epoch)
        => RoomResolver.TryPlace(epoch, locator, out var room, out var refusal) ? (room, null) : (null, refusal);

    /// <summary>The pose in force for this ride, or null when the level has no authored landing or it was refused.</summary>
    internal static Pose? Authored => _authored;

    /// <summary>Sets the ride's end reference to the authored pose once per ride. Safe to call repeatedly.</summary>
    internal static void Prepare(ElevatorRide? ride)
    {
        try
        {
            // The session spans several levels and the ride object is shared by all of them, so the pose of an earlier
            // level must not survive into one that has no authored landing (the V1 E1 log applied A3's landing there).
            if (_rows.Count == 0 || LevelIdentity.Read() is not { } level) { _authored = null; return; }
            EntryLanding? row = null;
            foreach (var candidate in _rows) if (candidate.Level == level && candidate.Slot == null) { row = candidate; break; }
            if (row == null) { _authored = null; return; }
            if (ride == null || ride.WasCollected) { Refuse(level, "ride-unavailable"); return; }
            var end = ride.m_elevatorCageEndRef;
            if (end == null || end.WasCollected) { Refuse(level, "end-reference-unavailable"); return; }
            var epoch = _epoch();
            if (epoch == 0) { Refuse(level, "epoch-unavailable"); return; }
            if (_authored is { } done && done.Ride == ride.Pointer && done.Epoch == epoch) return;
            _authored = null;
            var (room, refusal) = Place(row.Locator, epoch);
            if (room == null) { Refuse(level, refusal ?? "room-unresolved"); return; }
            var world = room.Pose.Point(row.Position);
            var q = room.Pose.Compose(Rotations.Yaw(row.YawDegrees));
            var landing = new Vector3((float)world[0], (float)world[1], (float)world[2]);
            var rotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
            // The authored point is where the party stands when released, not where the cage body stops: the centre of
            // the seats at the seat exit, at the cage floor. The end reference is therefore the authored pose moved back
            // by that cage-local offset, which puts the cage floor level with the authored ground.
            var offset = SeatExitCentre;
            var position = landing - rotation * offset;
            end.SetPositionAndRotation(position, rotation);
            double error = Vector3.Distance(end.position, position);
            if (!(error <= PositionTolerance))
            {
                Refuse(level, "readback-mismatch", error);
                return;
            }
            _authored = new Pose(ride.Pointer, epoch, position, rotation, landing);
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.EntryLandingPlaced,
                "The start elevator's end reference was set to the authored landing point.",
                RuntimeLogValue.Of("level", level.ToString()),
                RuntimeLogValue.Of("x", (double)landing.x), RuntimeLogValue.Of("y", (double)landing.y), RuntimeLogValue.Of("z", (double)landing.z),
                RuntimeLogValue.Of("endX", (double)position.x), RuntimeLogValue.Of("endY", (double)position.y), RuntimeLogValue.Of("endZ", (double)position.z),
                RuntimeLogValue.Of("offsetX", (double)offset.x), RuntimeLogValue.Of("offsetY", (double)offset.y), RuntimeLogValue.Of("offsetZ", (double)offset.z),
                RuntimeLogValue.Of("yaw", row.YawDegrees), RuntimeLogValue.Of("error", error));
        }
        catch (Exception error) { RefuseQuietly("exception:" + error.GetType().Name); }
    }

    /// <summary>At End -> PlayerExit, after the native endpoint commit: the cage keeps the authored facing. A native
    /// commit that missed the end reference, or a seat-exit centre that does not read back on the authored point, is
    /// logged as misaligned.</summary>
    internal static void CommitFacing(ElevatorRide ride)
    {
        try
        {
            if (_authored is not { } authored || authored.Ride != ride.Pointer) return;
            var body = ride.m_elevatorCage!.transform;
            body.rotation = authored.Rotation;
            double error = Vector3.Distance(body.position, authored.Position);
            var party = body.TransformPoint(SeatExitCentre);
            double partyError = Vector3.Distance(party, authored.Landing);
            var values = new List<RuntimeLogValue>
            {
                RuntimeLogValue.Of("phase", "committed"), RuntimeLogValue.Of("error", error), RuntimeLogValue.Of("partyError", partyError),
                RuntimeLogValue.Of("partyX", (double)party.x), RuntimeLogValue.Of("partyY", (double)party.y), RuntimeLogValue.Of("partyZ", (double)party.z)
            };
            // Evidence that the point is standable: the solid world below the party and the room it belongs to.
            ElevatorLandingObservation.FloorBelow(values, "party", party);
            if (!(error <= PositionTolerance) || !(partyError <= PositionTolerance))
                MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.EntryLandingMisaligned,
                    "The start elevator party landing centre is not at the authored landing point after the native end commit.", values.ToArray());
            else
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.EntryLandingPlaced,
                    "The start elevator party landing centre reads back at the authored landing point after the native end commit.", values.ToArray());
        }
        catch (Exception error) { RefuseQuietly("commit-exception:" + error.GetType().Name); }
    }

    /// <summary>Metres the native shaft floor is above the cage origin when the cage rests at its end reference
    /// (<c>floorHeight</c> in catalog/forge-release/elevator-cage.json, embedded here and read by the map editor's landing
    /// preview). The seats hang about 3.2 m above the origin and the machine's arm and pod hang below them, so the cage
    /// must stay at this height above the authored ground; placing the seats' standing centre on the ground instead sank
    /// the whole machine about 1.5 m into it.</summary>
    internal static readonly float CageFloorHeight;

    /// <summary>The cage-local point the party stands around when released: the horizontal centre of the four seats'
    /// camera aligns at the seat exit (<c>seatExit</c> in the same file), at the cage floor. The seats are released in one
    /// row across the cage, 0.94 and 2.69 m either side of its axis and 5.97 and 5.35 m in front of its origin. It cannot
    /// be read from the seats when the end reference is set, because the landing animation has not carried them there
    /// yet. The height the feet land at is set separately by <see cref="Release"/>.</summary>
    private static readonly Vector3 SeatExitCentre;

    static EntryLandingApplier()
    {
        using var stream = typeof(EntryLandingApplier).Assembly.GetManifestResourceStream("ForgeMap.ElevatorCage.json")
            ?? throw new InvalidOperationException("No elevator cage geometry is embedded.");
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        var root = document.RootElement;
        CageFloorHeight = root.GetProperty("floorHeight").GetSingle();
        double x = 0, z = 0;
        int seats = 0;
        foreach (var seat in root.GetProperty("seatExit").EnumerateArray()) { x += seat[0].GetDouble(); z += seat[1].GetDouble(); seats++; }
        SeatExitCentre = new Vector3((float)(x / seats), CageFloorHeight, (float)(z / seats));
    }

    /// <summary>Metres searched below the native release point for the floor; a floor further down is an opening the
    /// native release keeps (vanilla shafts release 2.5–2.7 m above their floor, authored start rooms up to 3.2 m).</summary>
    private const float FloorReach = 4f;

    /// <summary>Metres above the found floor the feet are put, so the body starts on it rather than in it.</summary>
    private const float FloorClearance = .02f;

    /// <summary>Before the native local seat exit: sets the camera offset native Exit subtracts from the seat camera
    /// align so the feet land on the standing height instead of at eye height. Never raises a player.</summary>
    internal static void Release(PLOC_InElevator state)
    {
        try
        {
            if (!_active || GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
            if (LevelIdentity.Read() is not { } level || !CompiledMapGenerator.Generates(level.ToString())) return;
            var player = state.m_owner;
            if (player == null || player.WasCollected || !player.IsLocallyOwned) return;
            var align = state.m_seat?.m_cameraAlign;
            var camera = player.FPSCamera;
            if (align == null || align.WasCollected || camera == null || camera.WasCollected) return;
            var eye = align.position;
            var native = eye - camera.m_posOffset;
            Vector3 stand;
            string source;
            string collider = "none";
            var slot = player.PlayerSlotIndex;
            if (PlayerPoint(level, slot) is { } own)
            {
                // The player's own point, wherever it is: the feet land on it, the eye stays at the seat and the native
                // Stand transition carries the camera from there over the point (the settle record reads that glide).
                // A point above the release height raises the player too: it is where the author put them.
                // The native Stand transition (FPSCamera.SetNewPosOffset) starts from the current offset, but Stand.Enter
                // runs after Exit and FPSCamera.Update meanwhile resets an idle offset to its target every frame, so the
                // offset alone is gone by then and the camera snapped from the seat to the feet in one tick (in game:
                // maxStep 6.633 = the whole eye-to-point distance). Start, current and target all hold the seat offset,
                // and the native transition time is stretched to the distance until the settle ends.
                var offset = eye - own.Point;
                camera.m_posOffset = offset; camera.m_posOffsetStart = offset; camera.m_posOffsetTarget = offset;
                camera.m_posOffsetTransTimer = 0f;
                float nativeSeconds = camera.m_posOffsetTransDuration;
                camera.m_posOffsetTransDuration = Mathf.Clamp(offset.magnitude / GlideSpeed, nativeSeconds, GlideSecondsMax);
                _settle = new Settle(player, level, slot, own.Point, own.Look, eye, new Vector2(eye.x - own.Point.x, eye.z - own.Point.z).magnitude, nativeSeconds);
                Standing(level, slot, "player-point", eye, native, own.Point, collider);
                return;
            }
            var ride = ElevatorRide.Current;
            if (_authored is { } authored && ride != null && !ride.WasCollected && authored.Ride == ride.Pointer)
            {
                // Straight below the player's own seat, as the native release; the height is the authored point's.
                stand = new Vector3(eye.x, authored.Landing.y, eye.z);
                source = "authored";
            }
            else if (!SelfBuiltStart(level))
            {
                // A vanilla start room keeps the vanilla release untouched (owner ruling): only an authored landing or a
                // player's own point moves anyone there.
                Standing(level, slot, "native", eye, native, native, collider);
                return;
            }
            else if (ForgeCore.Level.FloorBelow(native + new Vector3(0, .1f, 0), FloorReach, out var floor))
            {
                stand = new Vector3(native.x, floor.point.y + FloorClearance, native.z);
                source = "floor";
                collider = floor.collider!.name;
            }
            else
            {
                Standing(level, slot, "none", eye, native, native, collider);
                return;
            }
            if (native.y - stand.y > .05f) camera.m_posOffset = eye - stand;
            else stand = native;
            Standing(level, slot, source, eye, native, stand, collider);
        }
        catch (Exception error) { RefuseQuietly("release-exception:" + error.GetType().Name); }
    }

    /// <summary>Whether this level starts in a self-built room (a recipe room standing in for the elevator block, so
    /// there is no vanilla shaft floor under the seats).</summary>
    private static bool SelfBuiltStart(MapLevelReference level) => RoomRecipeHost.FunctionalRecipe(level, "start") != null;

    /// <summary>The world point and facing of one player's own landing in this level, or null when the level has none
    /// for that slot. A room that does not resolve refuses by name; the player then keeps the party landing.</summary>
    private static (Vector3 Point, Vector3 Look)? PlayerPoint(MapLevelReference level, int slot)
    {
        EntryLanding? row = null;
        foreach (var candidate in _rows) if (candidate.Level == level && candidate.Slot == slot) { row = candidate; break; }
        if (row == null) return null;
        var epoch = _epoch();
        if (epoch == 0) { Refuse(level, "epoch-unavailable", slot: slot); return null; }
        var (room, refusal) = Place(row.Locator, epoch);
        if (room == null) { Refuse(level, refusal ?? "room-unresolved", slot: slot); return null; }
        var world = room.Pose.Point(row.Position);
        var q = room.Pose.Compose(Rotations.Yaw(row.YawDegrees));
        var rotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
        return (new Vector3((float)world[0], (float)world[1], (float)world[2]), rotation * Vector3.forward);
    }

    /// <summary>Seconds the camera is followed after a seat exit onto a player's own point.</summary>
    private const float SettleSeconds = 3f;

    /// <summary>Metres the settled feet may stand off the point horizontally and still read as on it.</summary>
    private const float OnPointTolerance = .25f;

    /// <summary>Metres per second the camera glides from the seat to the point (the native 0.3 s is the floor).</summary>
    private const float GlideSpeed = 4f;

    /// <summary>Longest seat-to-point glide, seconds; well inside <see cref="SettleSeconds"/>.</summary>
    private const float GlideSecondsMax = 2f;

    private sealed class Settle
    {
        internal Settle(PlayerAgent player, MapLevelReference level, int slot, Vector3 point, Vector3 look, Vector3 eye, float glide, float nativeSeconds)
        { Player = player; Level = level; Slot = slot; Point = point; Look = look; Last = eye; Glide = glide; NativeSeconds = nativeSeconds; }
        internal readonly PlayerAgent Player;
        internal readonly MapLevelReference Level;
        internal readonly int Slot;
        internal readonly Vector3 Point, Look;
        internal readonly float Glide, NativeSeconds;
        internal Vector3 Last;
        internal float Seconds, MaxStep, MaxStepAt, PointError, HeightError;
        internal int Samples;
    }

    private sealed class BotLanding
    {
        internal BotLanding(PlayerAgent agent, MapLevelReference level, int slot, Vector3 point, Vector3 look)
        { Agent = agent; Level = level; Slot = slot; Point = point; Look = look; }
        internal readonly PlayerAgent Agent;
        internal readonly MapLevelReference Level;
        internal readonly int Slot;
        internal readonly Vector3 Point, Look;
        internal float Waited;
    }

    private static Settle? _settle;
    private static readonly List<BotLanding> _bots = new();
    private static string _readback = "none";

    /// <summary>Whether a seat exit onto a player's own point is still being followed or a bot still waits to be placed;
    /// the Map clock asks this every tick and calls <see cref="Tick"/> while it holds.</summary>
    internal static bool Pending => _settle != null || _bots.Count != 0;

    /// <summary>The last settle readback, for a console script (<c>call static:ForgeMap.Native.EntryLandingApplier
    /// LandingReadback</c>): <c>slot;source;pointError;heightError;glide;maxStep;maxStepAt;samples;cameraFlat;walked;onPoint</c>
    /// (glide: metres the camera starts horizontally off the point; maxStep: the largest camera move between two ticks;
    /// cameraFlat: the camera's horizontal distance from the feet once settled; walked: how far the player stands from the point when
    /// the settle ends; pointError and heightError are the feet at the first tick, before the player can walk), or <c>none</c>.</summary>
    internal static string LandingReadback() => _readback;

    /// <summary>After a bot's seat exit on the host (either native exit body): a bot has no camera to carry, so the host
    /// warps it to its own point through the game's replicated warp once its locomotion accepts one.</summary>
    internal static void BotReleased(PlayerAgent? player)
    {
        try
        {
            // A player this machine simulates but has no camera for is a bot on the host (the local player has the camera
            // and is placed by Release).
            if (!_active || player == null || player.WasCollected || !ForgeCore.Players.SimulatedHere(player.Owner) || player.FPSCamera != null) return;
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
            if (LevelIdentity.Read() is not { } level || !CompiledMapGenerator.Generates(level.ToString())) return;
            foreach (var queued in _bots) if (queued.Agent.Pointer == player.Pointer) return;
            if (PlayerPoint(level, player.PlayerSlotIndex) is { } own)
                _bots.Add(new BotLanding(player, level, player.PlayerSlotIndex, own.Point, own.Look));
        }
        catch (Exception error) { RefuseQuietly("bot-exception:" + error.GetType().Name); }
    }

    /// <summary>One Map clock tick while <see cref="Pending"/>.</summary>
    internal static void Tick(float seconds)
    {
        try
        {
            if (_settle != null) Follow(_settle, seconds);
            for (int i = _bots.Count - 1; i >= 0; i--) PlaceBot(i, seconds);
        }
        catch (Exception error) { EndSettle(); _bots.Clear(); RefuseQuietly("settle-exception:" + error.GetType().Name); }
    }

    /// <summary>Stops following the seat exit; the glide is over (it is at most <see cref="GlideSecondsMax"/>), so later
    /// crouch and stand transitions take the native time again.</summary>
    private static void EndSettle()
    {
        var settle = _settle;
        _settle = null;
        var camera = settle?.Player == null || settle.Player.WasCollected ? null : settle.Player.FPSCamera;
        if (camera != null && !camera.WasCollected) camera.m_posOffsetTransDuration = settle!.NativeSeconds;
    }

    /// <summary>Turns the player to the point's facing on the first tick (the native Stand transition has started by
    /// then) and follows the camera for <see cref="SettleSeconds"/>: the largest move between two ticks says whether it
    /// glided or jumped. Readback only, except the facing.</summary>
    private static void Follow(Settle settle, float seconds)
    {
        var player = settle.Player;
        var camera = player == null || player.WasCollected ? null : player.FPSCamera;
        if (camera == null || camera.WasCollected) { _settle = null; Refuse(settle.Level, "settle-player-unavailable", slot: settle.Slot); return; }
        if (settle.Samples == 0)
        {
            // Where the placement put the feet, read before the player can walk: a player who moves during the settle
            // window would otherwise be judged as misplaced.
            var placed = player!.transform.position;
            settle.PointError = new Vector2(placed.x - settle.Point.x, placed.z - settle.Point.z).magnitude;
            settle.HeightError = placed.y - settle.Point.y;
            ForgeCore.Players.Face(player, player.Position + settle.Look);
        }
        var at = camera.transform.position;
        float step = Vector3.Distance(at, settle.Last);
        settle.Last = at; settle.Seconds += seconds; settle.Samples++;
        if (step > settle.MaxStep) { settle.MaxStep = step; settle.MaxStepAt = settle.Seconds; }
        if (settle.Seconds < SettleSeconds) return;
        EndSettle();
        var feet = player!.transform.position;
        float pointError = settle.PointError, heightError = settle.HeightError,
            walked = new Vector2(feet.x - settle.Point.x, feet.z - settle.Point.z).magnitude,
            cameraFlat = new Vector2(at.x - feet.x, at.z - feet.z).magnitude;
        bool onPoint = pointError <= OnPointTolerance;
        _readback = string.Join(";",
            "slot=" + settle.Slot, "source=player-point", "pointError=" + Text(pointError), "heightError=" + Text(heightError),
            "glide=" + Text(settle.Glide), "maxStep=" + Text(settle.MaxStep), "maxStepAt=" + Text(settle.MaxStepAt), "samples=" + settle.Samples,
            "cameraFlat=" + Text(cameraFlat), "walked=" + Text(walked), "onPoint=" + (onPoint ? "true" : "false"));
        MapLog.Record(onPoint ? RuntimeLogLevel.Info : RuntimeLogLevel.Error, onPoint ? MapLogCodes.ElevatorLanding : MapLogCodes.ElevatorLandingInvalid,
            "Start elevator seat exit onto the player's own point: where the feet settled and how the camera moved from the seat.",
            RuntimeLogValue.Of("phase", "camera-settle"), RuntimeLogValue.Of("level", settle.Level.ToString()), RuntimeLogValue.Of("playerSlot", settle.Slot),
            RuntimeLogValue.Of("pointError", (double)pointError), RuntimeLogValue.Of("heightError", (double)heightError),
            RuntimeLogValue.Of("glide", (double)settle.Glide), RuntimeLogValue.Of("maxStep", (double)settle.MaxStep),
            RuntimeLogValue.Of("maxStepAt", (double)settle.MaxStepAt), RuntimeLogValue.Of("samples", settle.Samples),
            RuntimeLogValue.Of("cameraFlat", (double)cameraFlat), RuntimeLogValue.Of("walked", (double)walked));
    }

    private static string Text(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static void PlaceBot(int index, float seconds)
    {
        var bot = _bots[index];
        var result = ForgeCore.Players.Warp(bot.Agent, eDimensionIndex.Reality, bot.Point, bot.Look, ForgeCore.WarpMode.Synced);
        bot.Waited += seconds;
        // The seat exit hands the bot over before its locomotion accepts a warp; it is asked again on the next ticks.
        if (result.Refusal == ForgeCore.WarpRefusal.State && bot.Waited < SettleSeconds) return;
        _bots.RemoveAt(index);
        if (!result.Accepted) { Refuse(bot.Level, "bot-warp-refused", slot: bot.Slot, warp: result.Refusal.ToString()); return; }
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ElevatorLanding, "Start elevator seat exit: the host warped a bot to its own point.",
            RuntimeLogValue.Of("phase", "bot-point"), RuntimeLogValue.Of("level", bot.Level.ToString()), RuntimeLogValue.Of("playerSlot", bot.Slot),
            RuntimeLogValue.Of("standX", (double)bot.Point.x), RuntimeLogValue.Of("standY", (double)bot.Point.y), RuntimeLogValue.Of("standZ", (double)bot.Point.z),
            RuntimeLogValue.Of("landed", result.Landed), RuntimeLogValue.Of("distance", (double)result.Distance));
    }

    private static void Standing(MapLevelReference level, int slot, string source, Vector3 eye, Vector3 native, Vector3 stand, string collider) =>
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ElevatorLanding,
            "Start elevator seat exit: the standing height the local player is released on.",
            RuntimeLogValue.Of("phase", "standing-floor"), RuntimeLogValue.Of("level", level.ToString()), RuntimeLogValue.Of("playerSlot", slot),
            RuntimeLogValue.Of("source", source), RuntimeLogValue.Of("eyeY", (double)eye.y), RuntimeLogValue.Of("nativeFeetY", (double)native.y),
            RuntimeLogValue.Of("standX", (double)stand.x), RuntimeLogValue.Of("standY", (double)stand.y), RuntimeLogValue.Of("standZ", (double)stand.z),
            RuntimeLogValue.Of("drop", (double)(native.y - stand.y)), RuntimeLogValue.Quote("collider", collider));

    private static void Refuse(MapLevelReference level, string reason, double? error = null, int? slot = null, string? warp = null)
    {
        var values = new List<RuntimeLogValue> { RuntimeLogValue.Of("level", level.ToString()), RuntimeLogValue.Of("reason", reason) };
        if (error is { } e) values.Add(RuntimeLogValue.Of("error", e));
        if (slot is { } s) values.Add(RuntimeLogValue.Of("playerSlot", s));
        if (warp != null) values.Add(RuntimeLogValue.Quote("warp", warp));
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.EntryLandingRefused,
            "The authored entry landing was not applied; the native landing is kept.", values.ToArray());
    }

    private static void RefuseQuietly(string reason) =>
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.EntryLandingRefused,
            "The authored entry landing was not applied; the native landing is kept.", RuntimeLogValue.Of("reason", reason));
}
