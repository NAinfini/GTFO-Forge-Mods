using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Read native release/recovery boundaries. Never changes a landing, collider or player position.</summary>
internal static class ElevatorLandingObservation
{
    internal readonly record struct ReleaseInput(bool Captured, Vector3? SeatLanding, bool SeatKnown, Vector3? SeatCamera);
    internal readonly record struct RecoveryInput(bool Captured, Vector3 Position, LG_Area? Area, string Locomotion,
        RuntimeLogValue[]? NativeInputs = null, RuntimeLogValue[]? Surface = null);

    /// <summary>False once a refused landing hook withdraws the observation: the hooks that were admitted stay
    /// installed and read nothing.</summary>
    internal static bool Enabled { get; set; } = true;

    internal static ReleaseInput BeforeRelease(PLOC_InElevator state)
    {
        if (!Enabled || !MapLog.Logs(RuntimeLogLevel.Error)) return default;
        try
        {
            // CommonExit also runs while abandoning a drop, and Exit can run during world teardown.
            // A landing is the native locomotion release into the playing level, never either cleanup.
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return default;
            if (!MapLog.Logs(RuntimeLogLevel.Info)) return new(true, null, false, null);
            var seat = state.m_seat;
            return new(true, seat?.m_landingPoint?.position, seat != null, seat?.m_cameraAlign?.position);
        }
        catch (Exception error) { Failed("release-input", error); return default; }
    }

    internal static void Released(PlayerAgent player, ReleaseInput input)
    {
        if (!input.Captured) return;
        try
        {
            string level = LevelIdentity.Read()?.ToString() ?? "unresolved";
            // Only a player this machine simulates (its own, and on the host every bot) leaves a seat of this machine's cage.
            // A remote player's exit is its owner's, mirrored here whenever it arrives, with this machine's cage wherever
            // it then is (parked at the top once this machine's own ride is over).
            bool placedHere = ForgeCore.Players.SimulatedHere(player.Owner);
            if (!MapLog.Logs(RuntimeLogLevel.Info))
            {
                EndAlignment(ElevatorRide.Current, "native-end-align", level, player.PlayerSlotIndex, checkRelease: placedHere);
                return;
            }
            // The cached Agent.Position can remain at an earlier ride sample while Unity moves the seat parent.
            var position = (player.transform ?? throw new InvalidOperationException("player-transform-unavailable")).position;
            bool floor = ForgeCore.Level.FloorBelow(position + new Vector3(0, .1f, 0), 4f, out var hit);
            var values = Fields("player-release", level, player.PlayerSlotIndex);
            values.Add(RuntimeLogValue.Of("seatLandingKnown", input.SeatLanding != null));
            Point(values, "landing", position);
            if (input.SeatLanding is { } seat) Point(values, "seatLanding", seat);
            Point(values, "agent", player.Position);
            if (Builder.CurrentFloor?.Root is { } root) Point(values, "floorRoot", root.position);
            // A ray sample is observation, not a Forge failure or proof of a stable landing.
            // Native recovery and a proven invalid landing are separate observations below.
            const RuntimeLogLevel severity = RuntimeLogLevel.Info;
            const string code = MapLogCodes.ElevatorLanding;
            // The SDK permits 16 scalar fields per record. Pair both phases by world, level and player slot.
            MapLog.Record(severity, code, "Native player release position; observation only.", values.ToArray());
            values = Fields("floor-support", level, player.PlayerSlotIndex);
            values.Add(RuntimeLogValue.Of("floorHit", floor));
            values.Add(RuntimeLogValue.Quote("collider", floor ? hit.collider!.name : "none"));
            values.Add(RuntimeLogValue.Of("colliderLayer", floor ? hit.collider!.gameObject.layer : -1));
            Area(values, "player", player.CourseNode?.m_area);
            if (floor)
            {
                Point(values, "floor", hit.point);
                values.Add(RuntimeLogValue.Of("floorDistance", (double)hit.distance));
                Area(values, "floor", ColliderArea(hit.collider!));
            }
            else
            {
                values.Add(RuntimeLogValue.Of("reason", "no-solid-floor-below-native-release"));
                // A floor above the released player distinguishes an embedded/below-floor landing from a hole.
                bool above = ForgeCore.Level.FloorBelow(position + new Vector3(0, 2, 0), 2f, out var overhead);
                values.Add(RuntimeLogValue.Of("floorAbove", above));
                if (above) { Point(values, "floorAbove", overhead.point); values.Add(RuntimeLogValue.Quote("colliderAbove", overhead.collider!.name)); }
            }
            MapLog.Record(severity, code, "Solid world floor below native release; observation only.", values.ToArray());
            values = Fields("native-seat", level, player.PlayerSlotIndex);
            values.Add(RuntimeLogValue.Of("seatKnown", input.SeatKnown));
            values.Add(RuntimeLogValue.Of("locallyOwned", player.IsLocallyOwned));
            values.Add(RuntimeLogValue.Of("seatCameraKnown", input.SeatCamera != null));
            if (input.SeatCamera is { } cameraAlign) Point(values, "seatCamera", cameraAlign);
            var camera = player.FPSCamera;
            values.Add(RuntimeLogValue.Of("cameraOffsetKnown", camera != null));
            if (camera != null) Point(values, "cameraOffset", camera.m_posOffset);
            MapLog.Record(severity, code, "Native Exit uses seat camera alignment and camera position offset; readback only.", values.ToArray());
            values = Fields("native-ride", level, player.PlayerSlotIndex);
            var ride = ElevatorRide.Current;
            values.Add(RuntimeLogValue.Of("rideKnown", ride != null));
            if (ride != null)
            {
                values.Add(RuntimeLogValue.Of("fastElevator", ride.m_fastElevator));
                values.Add(RuntimeLogValue.Of("prerelease", ElevatorRide.m_isInPrereleaseSequence));
                var cage = ride.m_elevatorCage;
                values.Add(RuntimeLogValue.Of("cageKnown", cage != null));
                if (cage != null)
                {
                    Point(values, "cage", cage.transform.position);
                    var animator = cage.m_animator;
                    values.Add(RuntimeLogValue.Of("animatorKnown", animator != null));
                    if (animator != null)
                    {
                        var current = animator.GetCurrentAnimatorStateInfo(0);
                        values.Add(RuntimeLogValue.Of("animationTime", (double)current.normalizedTime));
                        values.Add(RuntimeLogValue.Of("animationLength", (double)current.length));
                        values.Add(RuntimeLogValue.Of("animationSpeed", (double)animator.speed));
                    }
                }
            }
            MapLog.Record(severity, code, "Native release animation state; observation only.", values.ToArray());
            LandingAlignment(level, player.PlayerSlotIndex);
            EndAlignment(ElevatorRide.Current, "native-end-align", level, player.PlayerSlotIndex, checkRelease: placedHere);
        }
        catch (Exception error) { Failed("player-release", error); }
    }

    internal static void RideBoundary(ElevatorRide? ride, string phase)
    {
        if (!Enabled || !MapLog.Logs(RuntimeLogLevel.Info)) return;
        try { EndAlignment(ride, phase, LevelIdentity.Read()?.ToString() ?? "unresolved", -1, checkRelease: false); }
        catch (Exception error) { Failed(phase, error); }
    }

    internal static void EndCommitted(ElevatorRide ride, Vector3 before)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        try
        {
            var values = Fields("native-end-commit", LevelIdentity.Read()?.ToString() ?? "unresolved", -1);
            Point(values, "before", before);
            var after = (ride.m_elevatorCage ?? throw new InvalidOperationException("cage-unavailable-after-end-commit")).transform.position;
            var target = (ride.m_elevatorCageEndRef ?? throw new InvalidOperationException("end-reference-unavailable-after-end-commit")).position;
            Point(values, "after", after);
            Point(values, "endRef", target);
            values.Add(RuntimeLogValue.Of("beforeDistance", (double)Vector3.Distance(before, target)));
            values.Add(RuntimeLogValue.Of("afterDistance", (double)Vector3.Distance(after, target)));
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ElevatorLanding,
                "Cage position after the native End endpoint commit, before player release; readback only.", values.ToArray());
        }
        catch (Exception error) { Failed("native-end-commit", error); }
    }

    private static void EndAlignment(ElevatorRide? ride, string phase, string level, int playerSlot, bool checkRelease)
    {
        if (ride == null || ride.WasCollected) ride = null;
        var cage = ride?.m_elevatorCage;
        if (cage == null || cage.WasCollected) cage = null;
        var end = ride?.m_elevatorCageEndRef;
        if (end == null || end.WasCollected) end = null;
        Vector3? position = cage == null ? null : cage.transform.position;
        Vector3? target = end == null ? null : end.position;
        double? distance = position is { } p && target is { } t ? Vector3.Distance(p, t) : null;
        if (MapLog.Logs(RuntimeLogLevel.Info))
        {
            // 16 scalar fields including phase/level/slot when every reference is available.
            var values = Fields(phase, level, playerSlot);
            values.Add(RuntimeLogValue.Of("rideKnown", ride != null));
            values.Add(RuntimeLogValue.Of("rideState", ride == null ? -1 : (int)ride.CurrentState));
            values.Add(RuntimeLogValue.Of("frameDelta", (double)Clock.SmoothDelta));
            values.Add(RuntimeLogValue.Of("endRefKnown", target != null));
            if (target is { } point) Point(values, "endRef", point);
            values.Add(RuntimeLogValue.Of("cageKnown", position != null));
            if (position is { } body) Point(values, "cage", body);
            if (distance is { } gap)
            {
                values.Add(RuntimeLogValue.Of("distance", gap));
                values.Add(RuntimeLogValue.Of("remainingY", (double)(position!.Value.y - target!.Value.y)));
            }
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ElevatorLanding,
                "Native cage animation endpoint and remaining vertical distance; observation only.", values.ToArray());
        }
        // The cage is host-simulated navigation state: a client's copy still stands where the ride started until the
        // host's End reaches it, so a release read there cannot say the landing was wrong. Only the host, which
        // commits the endpoint before it releases the players, can answer this, and only for the players it releases
        // from its own cage (the caller's checkRelease); the Info record above still carries a remote player's distance.
        if (checkRelease && ForgeCore.Session.IsHost && distance is > .5)
        {
            var values = Fields("cage-alignment", level, playerSlot);
            values.Add(RuntimeLogValue.Of("reason", "native-cage-not-on-end-align"));
            values.Add(RuntimeLogValue.Of("distance", distance.Value));
            Point(values, "cage", position!.Value); Point(values, "endRef", target!.Value);
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ElevatorLandingInvalid,
                "Native player release occurred with the cage more than 0.5 m from its native endpoint.", values.ToArray());
        }
    }

    private static void LandingAlignment(string level, int playerSlot)
    {
        var landing = ElevatorShaftLanding.Current;
        if (landing == null || landing.WasCollected) landing = null;
        var cargo = landing?.m_cargoCageAlign;
        if (cargo == null || cargo.WasCollected) cargo = null;
        var temp = ElevatorRide.m_tempCargoCageAlign;
        if (temp == null || temp.WasCollected) temp = null;
        var values = Fields("native-landing-align", level, playerSlot);
        values.Add(RuntimeLogValue.Of("currentKnown", landing != null));
        values.Add(RuntimeLogValue.Of("cargoAlignKnown", cargo != null));
        if (cargo != null) Point(values, "cargoAlign", cargo.position);
        values.Add(RuntimeLogValue.Of("tempKnown", temp != null));
        if (temp != null) Point(values, "temp", temp.position);
        values.Add(RuntimeLogValue.Of("tempMatchesLanding", cargo != null && temp != null && cargo.Pointer == temp.Pointer));
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ElevatorLanding,
            "Landing Current and native OnBuildDone cargo reference; observation only, separate from the cage endpoint.", values.ToArray());
    }

    internal static RecoveryInput BeforeRecovery(PlayerAgent player)
    {
        if (!Enabled || !MapLog.Logs(RuntimeLogLevel.Info)) return default;
        try
        {
            var position = (player.transform ?? throw new InvalidOperationException("player-transform-unavailable")).position;
            var locomotion = player.Locomotion;
            var fall = locomotion?.CurrentState?.TryCast<PLOC_Fall>();
            var controller = player.PlayerCharacterController;
            var inputs = Fields("native-recovery-input", LevelIdentity.Read()?.ToString() ?? "unresolved", player.PlayerSlotIndex);
            inputs.Add(RuntimeLogValue.Of("clockTime", (double)Clock.Time));
            inputs.Add(RuntimeLogValue.Of("frameDelta", (double)Clock.Delta));
            inputs.Add(RuntimeLogValue.Of("fixedDelta", (double)Clock.FixedDelta));
            inputs.Add(RuntimeLogValue.Of("smoothDelta", (double)Clock.SmoothDelta));
            inputs.Add(RuntimeLogValue.Of("fallStateKnown", fall != null));
            if (fall != null) inputs.Add(RuntimeLogValue.Of("fallElapsed", (double)(Clock.Time - fall.m_enterTime)));
            inputs.Add(RuntimeLogValue.Of("controllerKnown", controller != null));
            if (controller != null)
            {
                // Read stored values. IsGrounded itself updates m_isGrounded (RVA 0x17e7d90).
                inputs.Add(RuntimeLogValue.Of("controllerEnabled", controller.m_updateEnabled));
                inputs.Add(RuntimeLogValue.Of("controllerGrounded", controller.m_controllerGrounded));
                inputs.Add(RuntimeLogValue.Of("isGrounded", controller.m_isGrounded));
                inputs.Add(RuntimeLogValue.Of("groundRayHit", controller.m_groundRayDidHit));
            }
            inputs.Add(RuntimeLogValue.Of("courseNodeKnown", player.CourseNode != null));
            inputs.Add(RuntimeLogValue.Of("goodNodeClusterKnown", player.GoodNodeCluster != null));
            var surface = Fields("native-recovery-surface", LevelIdentity.Read()?.ToString() ?? "unresolved", player.PlayerSlotIndex);
            // Same ground ray as PlayerCharacterController.UpdateGroundRay, not the broader MASK_WORLD.
            bool floor = ForgeCore.Level.GroundRay(position + new Vector3(0, .2f, 0), .4f, LayerManager.MASK_DEFAULT, out var hit);
            surface.Add(RuntimeLogValue.Of("floorHit", floor));
            surface.Add(RuntimeLogValue.Of("floorMask", LayerManager.MASK_DEFAULT));
            if (floor)
            {
                Point(surface, "floor", hit.point);
                surface.Add(RuntimeLogValue.Quote("collider", hit.collider?.name ?? "unresolved"));
            }
            // This build's interop omits the AllAreas constant; all 32 area bits is its native value.
            bool nav = ForgeCore.Level.TrySnap(position, 2f, out var mesh);
            surface.Add(RuntimeLogValue.Of("navHitWithin2m", nav));
            if (nav) Point(surface, "nav", mesh);
            return new(true, position, player.CourseNode?.m_area, locomotion?.m_currentStateEnum.ToString() ?? "unresolved",
                inputs.ToArray(), surface.ToArray());
        }
        catch (Exception error) { Failed("recovery-input", error); return default; }
    }

    internal static void Recovered(PlayerAgent player, bool recovered, RecoveryInput input)
    {
        if (!recovered || !input.Captured) return;
        try
        {
            var values = new List<RuntimeLogValue>
            {
                RuntimeLogValue.Of("reason", "native-try-warp-back"),
                RuntimeLogValue.Of("level", LevelIdentity.Read()?.ToString() ?? "unresolved"),
                RuntimeLogValue.Of("playerSlot", player.PlayerSlotIndex),
                RuntimeLogValue.Of("locomotion", input.Locomotion),
            };
            Point(values, "from", input.Position);
            Point(values, "to", (player.transform ?? throw new InvalidOperationException("player-transform-unavailable")).position);
            Area(values, "from", input.Area); Area(values, "to", player.CourseNode?.m_area);
            // The native Fall watchdog (RVA 0x170bc80) recovers after three seconds. Success is a
            // native policy result, not evidence that Forge caused an invalid landing. Proven cage
            // misalignment and unreadable observations retain their separate Error records.
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.PlayerRecovery,
                "Native TryWarpBack returned success; actual from/to and prior locomotion readback.", values.ToArray());
            if (input.NativeInputs != null) MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.PlayerRecovery,
                "Native fall watchdog and stored controller inputs before recovery; observation only.", input.NativeInputs);
            if (input.Surface != null) MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.PlayerRecovery,
                "Native ground mask and nearby NavMesh before recovery; observation only.", input.Surface);
            // Where the watchdog put the body: whether solid world lies below that point decides if the recovery landed on
            // ground or on a cell with no collision (the body then falls again after the next watchdog interval).
            var landing = new List<RuntimeLogValue>
            {
                RuntimeLogValue.Of("phase", "native-recovery-landing"),
                RuntimeLogValue.Of("level", LevelIdentity.Read()?.ToString() ?? "unresolved"),
                RuntimeLogValue.Of("playerSlot", player.PlayerSlotIndex),
            };
            FloorBelow(landing, "to", (player.transform ?? throw new InvalidOperationException("player-transform-unavailable")).position);
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.PlayerRecovery,
                "Solid world floor below the native recovery landing point; observation only.", landing.ToArray());
        }
        catch (Exception error) { Failed("recovery-result", error); }
    }

    /// <summary>Metres searched below a point for solid world; a landing further above the floor than this reads as no floor.</summary>
    private const float FloorSearchDistance = 40f;

    /// <summary>Observation only: the solid world directly below <paramref name="point"/> (the party's standing point), as
    /// <c>{prefix}FloorHit</c>, the floor height, the point's height above it (negative when the point is under the
    /// surface found) and the room and zone that floor collider belongs to. An unreadable floor is logged as such, never
    /// guessed.</summary>
    internal static void FloorBelow(List<RuntimeLogValue> values, string prefix, Vector3 point)
    {
        try
        {
            bool hit = ForgeCore.Level.FloorBelow(point + new Vector3(0, .5f, 0), FloorSearchDistance, out var floor);
            values.Add(RuntimeLogValue.Of(prefix + "FloorHit", hit));
            if (!hit) return;
            values.Add(RuntimeLogValue.Of(prefix + "FloorY", (double)floor.point.y));
            values.Add(RuntimeLogValue.Of(prefix + "AboveFloor", (double)(point.y - floor.point.y)));
            values.Add(RuntimeLogValue.Quote(prefix + "FloorCollider", floor.collider!.name));
            Area(values, prefix + "Floor", ColliderArea(floor.collider));
        }
        catch (Exception error)
        {
            values.Add(RuntimeLogValue.Of(prefix + "FloorHit", false));
            values.Add(RuntimeLogValue.Quote(prefix + "FloorError", error.GetBaseException().Message));
        }
    }
    private static LG_Area? ColliderArea(Collider collider)
    {
        // Avoid IL2CPP's generic GetComponentInParent out-wrapper construction. Do not infer a room from proximity.
        for (var node = collider.transform; node != null; node = node.parent)
            if (node.GetComponent<LG_Area>() is { } area) return area;
        return null;
    }

    private static void Area(List<RuntimeLogValue> values, string prefix, LG_Area? area)
    {
        var zone = area?.m_zone;
        values.Add(RuntimeLogValue.Of(prefix + "Zone", zone?.m_layer is { } layer
            ? ((int)zone.m_dimensionIndex).ToString(CultureInfo.InvariantCulture) + "/"
              + ((int)layer.m_type).ToString(CultureInfo.InvariantCulture) + "/"
              + ((int)zone.LocalIndex).ToString(CultureInfo.InvariantCulture) : "unresolved"));
        var room = area?.m_geomorph;
        values.Add(RuntimeLogValue.Quote(prefix + "Room", room == null ? "unresolved" : GeneratedRoomRegistry.InstanceIdFor(room) ?? "unresolved"));
        values.Add(RuntimeLogValue.Quote(prefix + "Geomorph", room?.m_geoPrefab?.name ?? "unresolved"));
    }

    private static void Point(List<RuntimeLogValue> values, string prefix, Vector3 point)
    {
        values.Add(RuntimeLogValue.Of(prefix + "X", (double)point.x));
        values.Add(RuntimeLogValue.Of(prefix + "Y", (double)point.y));
        values.Add(RuntimeLogValue.Of(prefix + "Z", (double)point.z));
    }

    private static List<RuntimeLogValue> Fields(string phase, string level, int playerSlot)
        => new() { RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("level", level), RuntimeLogValue.Of("playerSlot", playerSlot) };

    private static void Failed(string phase, Exception error)
        => MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ElevatorLandingInvalid, "Native landing/recovery observation failed.",
            RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("reason", "observation-failed"),
            RuntimeLogValue.Quote("exception", error.GetBaseException().Message));
}

[HarmonyPatch(typeof(ElevatorRide), nameof(ElevatorRide.StartElevatorRide))]
internal static class ElevatorRideStartObserved
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix()
    {
        if (MapLog.Logs(RuntimeLogLevel.Info))
            ElevatorLandingObservation.RideBoundary(ElevatorRide.Current, "ride-start-input");
    }
}

[HarmonyPatch(typeof(ElevatorRide._ElevatorEnd_d__115), nameof(ElevatorRide._ElevatorEnd_d__115.MoveNext))]
internal static class ElevatorRideEndObserved
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(ElevatorRide._ElevatorEnd_d__115 __instance)
    {
        if (__instance.__1__state != 0) return;
        // Native End.MoveNext caches m_elevatorCageEndRef.position on its first step
        // (build 20403457, RVA 0x167e764-0x167e789), before interpolating the cage.
        if (__instance.__4__this is { } ride && !ride.WasCollected && ride.CurrentState == ElevatorRideState.End)
            EntryLandingApplier.Prepare(ride);
        if (MapLog.Logs(RuntimeLogLevel.Info))
            ElevatorLandingObservation.RideBoundary(__instance.__4__this, "ride-end-input");
    }
}

// CommonExit is called at the beginning of Exit, before vanilla detaches/moves the player. Both local and
// replicated releases must finish their own native body before reading the floor, so each body has its own
// declaration and both hand their call to the same observation.
[HarmonyPatch(typeof(PLOC_InElevator), nameof(PLOC_InElevator.Exit))]
internal static class ElevatorPlayerExited
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(PLOC_InElevator __instance, out ElevatorLandingObservation.ReleaseInput __state)
    {
        __state = ElevatorLandingObservation.BeforeRelease(__instance);
        // After the observation reads the native inputs, before the native body releases the player.
        EntryLandingApplier.Release(__instance);
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(PLOC_InElevator __instance, ElevatorLandingObservation.ReleaseInput __state)
    {
        ElevatorLandingObservation.Released(__instance.m_owner, __state);
        EntryLandingApplier.BotReleased(__instance.m_owner);
    }
}

[HarmonyPatch(typeof(PLOC_InElevator), nameof(PLOC_InElevator.SyncExit))]
internal static class ElevatorPlayerSyncExited
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(PLOC_InElevator __instance, out ElevatorLandingObservation.ReleaseInput __state)
        => __state = ElevatorLandingObservation.BeforeRelease(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(PLOC_InElevator __instance, ElevatorLandingObservation.ReleaseInput __state)
    {
        ElevatorLandingObservation.Released(__instance.m_owner, __state);
        EntryLandingApplier.BotReleased(__instance.m_owner);
    }
}
[HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.TryWarpBack))]
internal static class PlayerRecoveredFromFall
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(PlayerAgent __instance, out ElevatorLandingObservation.RecoveryInput __state)
        => __state = ElevatorLandingObservation.BeforeRecovery(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(PlayerAgent __instance, bool __result, ElevatorLandingObservation.RecoveryInput __state)
        => ElevatorLandingObservation.Recovered(__instance, __result, __state);
}
