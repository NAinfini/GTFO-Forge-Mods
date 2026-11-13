using System.Runtime.InteropServices;
using System.Text.Json;
using ForgeOrdnance.Frames;

namespace ForgeOrdnance.Tests;

/// <summary>The suits' Core: the payload parser, the shipped definitions, the host rules, the messages, the checkpoint copy and the rig.</summary>
internal static class FramesChecks
{
    internal static void Run(Action<bool, string> check)
    {
        JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

        const string Minimal = "{\"Type\":\"frames.Exosuit\",\"Model\":\"Frames:frame-m15\",\"Armor\":100,"
            + "\"Weapon\":{\"Gear\":38,\"Magazine\":30,\"Reserve\":60}}";

        // ---- definitions ---------------------------------------------------------------------------------------------

        {
            var spec = FrameSpecParser.Parse(Json(Minimal));
            check(spec.Model == "Frames:frame-m15" && spec.Armor == 100 && spec.Weapon.Gear == 38 && spec.Weapon.Magazine == 30 && spec.Weapon.DamageMultiplier == 1f, "minimal payload parses with defaults");
            check(spec.DamageReduction == 0f && spec.Movement.Walk > 1f && spec.Movement.Sprint > 1f && spec.Movement.Jump > 1f && spec.Light.RangeMultiplier >= 2f,
                "no damage reduction, faster walking, a sprint bonus, a higher jump and the wider lamp by default");
            check(Math.Abs(FrameSequence.EnterSeconds - 2.85f) < 1e-4f && FrameSequence.ExitSeconds < 2.1f, "boarding lasts 2.85 s to the last power-on block and leaving ends after the landing and the black has cleared, for every model (no definition sets them)");
        }

        void Rejected(string name, string payload, string field)
        {
            try { FrameSpecParser.Parse(Json(payload)); }
            catch (ForgeCore.StrictJsonException e) { check(e.Field == field, name + ": names " + field + ", got " + e.Field); return; }
            throw new Exception("FAIL: " + name + " was accepted");
        }

        Rejected("unreachable second active special", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Boost\":{\"Seconds\":2,\"SpeedMultiplier\":2,\"Cooldown\":10},\"Siege\":{}"), "Siege");
        Rejected("unknown weapon field", Minimal.Replace("\"Gear\":38", "\"Gear\":38,\"Damage\":1"), "Weapon.Damage");
        Rejected("weapon without gear", Minimal.Replace("\"Gear\":38,", ""), "Weapon.Gear");
        Rejected("damage reduction above 90 percent", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"DamageReduction\":0.95"), "DamageReduction");
        Rejected("walk below the player's own", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Movement\":{\"Walk\":0.9}"), "Movement.Walk");
        Rejected("opening without parts", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{}"), "Opening.Parts");
        Rejected("opening part twice", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{\"Parts\":[{\"Node\":\"p\"},{\"Node\":\"p\"}]}"), "Opening.Parts[1].Node");
        Rejected("opening part with a runtime hinge", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{\"Parts\":[{\"Node\":\"p\",\"Axis\":[0,1,0]}]}"), "Opening.Parts[0].Axis");
        Rejected("opening with a runtime curve", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{\"Ease\":4,\"Parts\":[{\"Node\":\"p\"}]}"), "Opening.Ease");
        Rejected("shell on an unknown body bone", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shell\":[{\"Bone\":\"hand.L\",\"Body\":\"LeftPaw\"}]"), "Shell[0].Body");
        Rejected("shell body bone twice", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shell\":[{\"Bone\":\"hand.L\",\"Body\":\"LeftHand\"},{\"Bone\":\"hand.R\",\"Body\":\"LeftHand\"}]"), "Shell[1].Body");
        {
            var opened = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{\"Parts\":[{\"Node\":\"plate\"},{\"Node\":\"plate.back.L\",\"Seam\":true}]},\"Shell\":[{\"Bone\":\"forearm.R\",\"Body\":\"RightLowerArm\"}]")));
            check(opened.Opening!.Parts.Length == 2 && opened.Opening.Parts[0].Node == "plate" && opened.Opening.Parts[1].Node == "plate.back.L"
                && !opened.Opening.Parts[0].Seam && opened.Opening.Parts[1].Seam, "opening parts parse as the bones the model's clips move, the seam's marked");
            check(FrameSpecParser.Parse(Json(Minimal)).Weapon.ReloadMultiplier == 1f, "every gun keeps its own reload time unless the definition shortens it");
            Rejected("reload multiplier above one", Minimal.Replace("\"Gear\":38", "\"Gear\":38,\"ReloadMultiplier\":1.5"), "Weapon.ReloadMultiplier");
            check(opened.Shell.Length == 1 && opened.Shell[0].Bone == "forearm.R" && opened.Shell[0].Body == FrameBodyBone.RightLowerArm, "shell bones map onto body bones");
            check(FrameSpecKey.Of(opened) != FrameSpecKey.Of(opened with { Shell = Array.Empty<ShellBone>() }), "the shell is part of the content key");
        }
        Rejected("bad lamp colour", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Light\":{\"Colour\":\"warm\"}"), "Light.Colour");
        Rejected("missing model", Minimal.Replace("\"Model\":\"Frames:frame-m15\",", ""), "Model");
        Rejected("fractional magazine", Minimal.Replace("\"Magazine\":30", "\"Magazine\":30.5"), "Weapon.Magazine");
        Rejected("string number", Minimal.Replace("\"Armor\":100", "\"Armor\":\"100\""), "Armor");
        Rejected("boost without speed", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Boost\":{\"Seconds\":2,\"Cooldown\":5}"), "Boost.SpeedMultiplier");

        {
            // The suit definitions the mod ships must load.
            var file = Path.Combine(ModFolder.Path, "Frames", "Definitions", "frames.forge");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var entries = doc.RootElement.EnumerateArray().ToList();
            var models = entries.Select(e => FrameSpecParser.Parse(e.GetProperty("Payload"))).ToDictionary(s => s.Model.Substring("Frames:".Length));
            check(models.Count == 4 && models.Values.All(m => m.Model.StartsWith("Frames:frame-m", StringComparison.Ordinal))
                && entries.All(e => e.GetProperty("Id").GetString()!.StartsWith("nainfini.frames.", StringComparison.Ordinal))
                && models.Values.Select(FrameSpecKey.Of).Distinct().Count() == 4,
                "four shipped models, each a prefab of the Frames pack with its own content key, under the author namespace");
            // Each model's specialty is data: tank barrier and carrying, firepower siege, recon scan, assault charge.
            check(models["frame-m15"].Boost != null && models["frame-m15"].Shield == null, "assault: charge only");
            check(models["frame-m16"].Shield != null && models["frame-m16"].CarryHeavy && models["frame-m16"].Boost == null, "tank: barrier and heavy carrying");
            check(FrameRules.CarryBlockReleased(true, true, true) && !FrameRules.CarryBlockReleased(false, true, true)
                && !FrameRules.CarryBlockReleased(true, false, true) && !FrameRules.CarryBlockReleased(true, true, false),
                "the carry slot's wield block is lifted only for the local pilot of a heavy-carrying suit");
            check(models["frame-m17"].Siege != null && models["frame-m17"].Scan == null && models.Values.Count(m => m.Rockets != null) == 1 && models["frame-m17"].Rockets != null, "firepower: siege and the only rockets");
            var fp = models["frame-m17"];
            check(fp.Siege!.FireRateMultiplier > 1f && fp.Siege.ReloadMultiplier < 1f && fp.Shield != null && fp.Shield.WhileSieged && !models["frame-m16"].Shield!.WhileSieged,
                "firepower: planting fires faster, reloads faster and raises its own barrier; the tank's barrier always stands");
            var planted = new FrameRecord(3, 3, fp) { State = FrameState.Piloted, Pilot = 5 };
            var walking = !FrameRules.BarrierUp(planted) && !FrameRules.Repels(planted, planted.X, planted.Y + 1.2f, planted.Z) && !FrameRules.ShieldHit(planted, 10f, 1f) && planted.Shield == fp.Shield!.Capacity;
            planted.Sieged = true;
            check(walking && FrameRules.BarrierUp(planted) && FrameRules.Repels(planted, planted.X, planted.Y + 1.2f, planted.Z) && !FrameRules.ShieldHit(planted, 10f, 2f) && planted.Shield < fp.Shield!.Capacity,
                "a barrier that stands while planted is down (and takes nothing) while walking and up once planted");
            Rejected("barrier while planted without a siege", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shield\":{\"Capacity\":50,\"RegenRate\":10,\"WhileSieged\":true}"), "Shield.WhileSieged");
            check(models["frame-m18"].Scan != null && models["frame-m18"].Siege == null && models["frame-m18"].Shield == null, "recon: scan only");
            check(FrameRules.ScanTags(ForgeCore.EnemyAlert.Awake) && FrameRules.ScanTags(ForgeCore.EnemyAlert.Hunting) && !FrameRules.ScanTags(ForgeCore.EnemyAlert.Sleeping) && !FrameRules.ScanTags(ForgeCore.EnemyAlert.Off), "recon: the scan tags awake enemies, not sleeping ones");
            // The same rules over every shipped model.
            check(models.Values.All(m => m.Movement.Walk > 1f && m.Movement.Sprint > 1f && m.Movement.Jump > 1f), "every model is faster and jumps higher than the player on foot");
            check(models.Values.All(m => m.Opening != null && m.Opening.Parts.Length >= 2 && m.Opening.Parts.All(part => !part.Node.Contains("helmet", StringComparison.Ordinal))
                && FrameBodyRig.HingedParts(m.Opening).SequenceEqual(m.Opening.Parts.Select(p => p.Node))), "every model lists its opening parts, keeps them shut on the shell, and the helmet stays shut");
            check(models.Values.All(m => m.Shell.Length >= 6 && m.Shell.Any(b => b.Body == FrameBodyBone.LeftUpperArm) && m.Shell.Any(b => b.Body == FrameBodyBone.RightUpperArm) && m.Shell.Any(b => b.Body == FrameBodyBone.Hips)
                && !m.Shell.Any(b => b.Body is FrameBodyBone.LeftLowerArm or FrameBodyBone.RightLowerArm or FrameBodyBone.LeftHand or FrameBodyBone.RightHand)),
                "every model lines its skeleton up on the upper arms and hips and keeps the game's own forearms and hands");
            check(models.Values.All(m =>
                {
                    var worn = FrameBodyRig.ShellBones(m.Shell);
                    return worn.Length == m.Shell.Length && worn.All(b => b.Worn != b.Body is ForgeVisual.BodyBone.LeftUpperArm or ForgeVisual.BodyBone.RightUpperArm)
                        && worn.Where(b => b.Body is ForgeVisual.BodyBone.LeftUpperLeg or ForgeVisual.BodyBone.LeftLowerLeg or ForgeVisual.BodyBone.LeftFoot).All(b => b.Scale == ForgeVisual.BodyMeasure.LeftLeg)
                        && worn.Where(b => b.Body is ForgeVisual.BodyBone.RightUpperLeg or ForgeVisual.BodyBone.RightLowerLeg or ForgeVisual.BodyBone.RightFoot).All(b => b.Scale == ForgeVisual.BodyMeasure.RightLeg)
                        && worn.Where(b => b.Body is ForgeVisual.BodyBone.Hips or ForgeVisual.BodyBone.Spine).All(b => b.Scale == ForgeVisual.BodyMeasure.ShoulderSpan);
                }), "every model wears its shell: the upper arms only line up, each leg sized by its own length, the body by the shoulder span");
        }

        // The suits' rig onto ForgeVisual's body bones: every definition bone has its humanoid namesake, the pose table is well formed.
        check(Enum.GetValues<FrameBodyBone>().All(bone => FrameBodyRig.Body(bone).ToString() == bone.ToString()), "every body bone maps onto ForgeVisual's namesake");
        check(ForgeVisual.PoseSolver.Validate(FrameBodyRig.PoseLinks) == null, "the suit pose table is well formed");
        check(ForgeVisual.PoseSolver.ValidateReach(FrameBodyRig.ArmReach, FrameBodyRig.PoseLinks.Length) == null
            && FrameBodyRig.ArmReach.Length == 2 && Array.TrueForAll(FrameBodyRig.ArmReach, r =>
                FrameBodyRig.PoseLinks[r.Upper].Model.StartsWith("upperarm.") && FrameBodyRig.PoseLinks[r.Lower].Model.StartsWith("forearm.")
                && FrameBodyRig.PoseLinks[r.End].Model.StartsWith("hand.") && FrameBodyRig.PoseLinks[r.Upper].Model[^1] == FrameBodyRig.PoseLinks[r.End].Model[^1]
                && FrameBodyRig.PoseLinks[r.Anchor].Model == "chest"), "both arms reach the body's wrists from the chest, each its own side's upper arm, forearm and hand");

        // ---- host rules ----------------------------------------------------------------------------------------------

        var assault = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Boost\":{\"Seconds\":3,\"SpeedMultiplier\":2,\"Cooldown\":10}")));
        {
            var f = new FrameRecord(7, 1, assault) { State = FrameState.Arriving, StateEndsAt = 1f };
            check(FrameRules.CanEnter(f, 1f, false, false) == FrameRejection.NotIdle, "cannot board while it falls");
            check(FrameRules.Tick(f, 1f, out var gone) && f.State == FrameState.Idle && !gone, "lands and stands empty");
            check(FrameRules.CanEnter(f, FrameRules.BoardDistance + 0.1f, false, false) == FrameRejection.TooFar, "too far to board");
            check(FrameRules.CanEnter(f, float.NaN, false, false) == FrameRejection.TooFar, "unknown distance is too far");
            check(FrameRules.CanEnter(f, 1f, true, false) == FrameRejection.AlreadyPiloting, "one suit per pilot");
            check(FrameRules.CanEnter(f, 1f, false, true) == FrameRejection.PilotDown, "a downed player cannot board");
            check(FrameRules.CanEnter(f, FrameRules.BoardSpot, false, false) == FrameRejection.None, "board in reach");
            FrameRules.Enter(f, 42, 10f);
            check(f.State == FrameState.Entering && f.Pilot == 42 && Math.Abs(f.StateEndsAt - (10f + FrameSequence.EnterSeconds)) < 1e-4f, "boarding takes the boarding sequence's 2.85 s");
            check(FrameRules.CanEnter(f, 1f, false, false) == FrameRejection.NotIdle, "a second player cannot board an occupied suit");
            check(FrameRules.CanExit(f, 42) == FrameRejection.NotIdle, "cannot leave mid-boarding");
            FrameRules.Tick(f, 10f + FrameSequence.EnterSeconds - 0.01f, out _);
            var stillBoarding = f.State == FrameState.Entering;
            FrameRules.Tick(f, 10f + FrameSequence.EnterSeconds, out _);
            check(stillBoarding && f.State == FrameState.Piloted, "boarding completes, and the pilot moves, exactly when the boarding sequence ends");
            check(FrameRules.CanExit(f, 43) == FrameRejection.NotPilot, "only the pilot can leave");

            check(FrameRules.CanBoost(f, 42, 12f) == FrameRejection.None, "boost ready");
            FrameRules.Boost(f, 12f);
            var walk = assault.Movement.Walk;
            check(FrameRules.SpeedMultiplier(f, 13f) == walk * 2f && FrameRules.SpeedMultiplier(f, 15.5f) == walk, "boost multiplies speed for its time only");
            check(FrameRules.CanBoost(f, 42, 13f) == FrameRejection.Charging && FrameRules.CanBoost(f, 42, 20f) == FrameRejection.Cooling && FrameRules.CanBoost(f, 42, 25f) == FrameRejection.None,
                "a charge refuses while it runs (charging), then cools down (not ready)");
            var planting = new FrameRecord(7, 8, assault) { State = FrameState.Piloted, Pilot = 42, SiegeDeployEndsAt = 5f };
            check(FrameRules.CanBoost(planting, 42, 1f) == FrameRejection.Planted, "a planted or planting suit refuses a charge as planted");
            check(FrameRules.CanSiege(f, 42) == FrameRejection.NoSpecial, "no siege on a suit without it");

            check(FrameRules.ReportAmmo(f, 42, 20, 60) && f.Magazine == 20, "pilot reports spent ammunition");
            check(!FrameRules.ReportAmmo(f, 42, 30, 60), "a report cannot add ammunition");
            check(!FrameRules.ReportAmmo(f, 43, 10, 10), "only the pilot reports");

            check(!FrameRules.Damage(f, 20f, 30f) && Math.Abs(f.Armor - 80f) < 1e-3f, "hits land on the suit's armour");
            var plated = new FrameRecord(7, 9, assault with { DamageReduction = 0.4f, Siege = new SiegeSpec(1f, 1.4f, 0.5f, 1f, 1f, 1f) }) { Sieged = true };
            check(Math.Abs(FrameRules.Absorbed(plated, 100f) - 30f) < 1e-3f, "damage reduction and the planted suit's reduction both apply");
            FrameRules.Exit(f, 31f, 1, 2, 3, 90);
            check(f.State == FrameState.Exiting && f.X == 1 && f.Yaw == 90 && f.Pilot == 42, "leaving keeps the pilot until the sequence ends");
            FrameRules.Tick(f, 31f + FrameSequence.ExitSeconds - 0.01f, out _);
            check(f.State == FrameState.Exiting, "leaving holds until the black has cleared after the landing");
            FrameRules.Tick(f, 31f + FrameSequence.ExitSeconds, out _);
            check(f.State == FrameState.Idle && f.Pilot == 0 && Math.Abs(f.Armor - 80f) < 1e-3f && f.Magazine == 20, "left standing with its armour and ammunition");
            check(!FrameRules.Damage(f, 50f, 35f) && Math.Abs(f.Armor - 80f) < 1e-3f, "an empty suit takes no damage");

            FrameRules.Enter(f, 9, 40f);
            FrameRules.Tick(f, 45f, out _);
            var wrecked = FrameRules.Damage(f, 1000f, 50f);
            check(wrecked && f.State == FrameState.Destroyed && f.Pilot == 0 && f.Armor == 0f, "a wrecking hit throws the pilot out");
            check(!FrameRules.Tick(f, 55f, out var early) && !early, "the wreck lingers");
            FrameRules.Tick(f, 50f + FrameRules.WreckSeconds, out var removed);
            check(removed, "the wreck is removed");

            var g = new FrameRecord(7, 2, assault) { State = FrameState.Piloted, Pilot = 5 };
            FrameRules.Eject(g, 4, 5, 6, 0);
            check(g.State == FrameState.Idle && g.Pilot == 0 && g.X == 4, "ejection leaves the suit standing at once");

            var worn = new FrameRecord(7, 3, assault) { State = FrameState.Piloted, Pilot = 5 };
            var boarding = new FrameRecord(7, 4, assault) { State = FrameState.Entering, Pilot = 5 };
            check(!FrameRules.Interrupted(worn, true, 900f, 0f, 0f), "a worn suit is never interrupted: it moves with its pilot");
            check(!FrameRules.Interrupted(boarding, false, FrameRules.BoardDistance, 0f, 0f) && FrameRules.Interrupted(boarding, false, 0f, 0f, FrameRules.TetherReach + 0.1f)
                && FrameRules.Interrupted(boarding, true, 0f, 0f, 0f), "boarding ends when the game holds the pilot or puts it beyond the tether");
        }
        {
            var sieger = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Siege\":{\"DamageMultiplier\":2}")));
            var f = new FrameRecord(1, 1, sieger) { State = FrameState.Piloted, Pilot = 3 };
            check(FrameRules.CanSiege(f, 3) == FrameRejection.None, "siege available");
            FrameRules.ToggleSiege(f, 0f);
            check(!f.Sieged && f.SiegeDeployEndsAt == sieger.Siege!.DeploySeconds && FrameRules.SpeedMultiplier(f, 0f) == 0f, "deployment locks movement before granting siege bonuses");
            FrameRules.Tick(f, sieger.Siege!.DeploySeconds, out _);
            check(f.Sieged && FrameRules.SpeedMultiplier(f, 0f) == 0f, "a planted suit cannot move");
            check(!FrameRules.SetSiege(f, 2f, true) && f.Sieged, "a repeated plant request changes nothing (it names its target, it does not toggle)");
            check(FrameRules.SetSiege(f, 2f, false) && !f.Sieged && FrameRules.SpeedMultiplier(f, 0f) == sieger.Movement.Walk, "packing up restores movement");
            check(FrameRules.Rockets(f, 3, 0f) == FrameRejection.NoSpecial, "no rockets on a suit without them");
            FrameRules.Enter(f, 3, 3f);
            check(f.Drive == 1 && ForgeCore.NetBody.TryRead(ForgeCore.NetBody.Of(FrameStateMessage.From(f, 3f, FrameEjection.None)), out FrameStateMessage drove) && drove.Drive == 1, "boarding counts and the state carries it");
        }
        {
            const string launcher = "\"Launcher\":{\"Pod\":\"Frames:pod\",\"Round\":\"Frames:round\",\"Bone\":\"plate\",\"Position\":[0.3,1.9,-0.1],\"Rotation\":[0,15,0]}";
            var rocketeer = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":40," + launcher + "}")));
            var k = rocketeer.Rockets!;
            check(k.Launcher.Pod == "Frames:pod" && k.Launcher.Round == "Frames:round" && k.Launcher.Bone == "plate" && k.Launcher.X == 0.3f && k.Launcher.Z == -0.1f && k.Launcher.Yaw == 15f,
                "the launcher names its pod and round and its place on a bone");
            Rejected("rockets without a launcher", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":40}"), "Rockets.Launcher");
            Rejected("launcher scaled", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":40,\"Launcher\":{\"Pod\":\"Frames:p\",\"Round\":\"Frames:r\",\"Bone\":\"plate\",\"Position\":[0,2,0],\"Rotation\":[0,0,0],\"Scale\":[-1,1,1]}}"), "Rockets.Launcher.Scale");
            Rejected("launcher position not three numbers", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":40,\"Launcher\":{\"Pod\":\"Frames:p\",\"Round\":\"Frames:r\",\"Bone\":\"plate\",\"Position\":[0,2],\"Rotation\":[0,0,0]}}"), "Rockets.Launcher.Position");
            var f = new FrameRecord(1, 1, rocketeer) { State = FrameState.Piloted, Pilot = 3 };
            check(FrameRules.Rockets(f, 4, 0f) == FrameRejection.NotPilot, "only the pilot launches rockets");
            check(FrameRules.Rockets(f, 3, 5f) == FrameRejection.None && f.RocketsReadyAt == 17f, "a salvo starts the cooldown");
            check(FrameRules.Rockets(f, 3, 16.9f) == FrameRejection.Cooling && FrameRules.Rockets(f, 3, 17f) == FrameRejection.None, "the next salvo waits for the cooldown");
            var inside = true; var differ = false;
            FrameRules.RocketShot(77, 0, 1.5f, out var x0, out var z0, out var d0);
            for (var i = 0; i < 8; i++)
            {
                FrameRules.RocketShot(77, i, 1.5f, out var x, out var z, out var d);
                FrameRules.RocketShot(77, i, 1.5f, out var x2, out var z2, out var d2);
                inside &= x * x + z * z <= 1.5f * 1.5f + 1e-4f && x == x2 && z == z2 && d == i * FrameRules.RocketInterval;
                differ |= i > 0 && (x != x0 || z != z0);
            }
            check(inside && differ && d0 == 0f, "rocket scatter is the same on every peer, inside the spread, and differs per rocket");
        }

        {
            var tank = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shield\":{\"Capacity\":50,\"RegenDelay\":6,\"RegenRate\":10}")));
            var f = new FrameRecord(1, 1, tank) { State = FrameState.Piloted, Pilot = 3 };
            check(f.ShieldUp && f.Shield == 50, "barrier starts full and standing");
            check(!FrameRules.ShieldHit(f, 30, 10) && f.Shield == 20 && f.Armor == 100, "the barrier takes enemy attacks, the armour does not");
            check(FrameRules.ShieldHit(f, 30, 11) && !f.ShieldUp && f.Shield == 0, "emptied, the barrier breaks");
            check(!FrameRules.ShieldHit(f, 30, 12) && f.ShieldLastHit == 11, "a broken barrier stops nothing");
            var g = new FrameRecord(1, 2, tank);
            FrameRules.Place(g, 0, 0, 0, 0);
            check(!FrameRules.Shelters(g, 0, 0, 0, 1, 1, 0, 6, 1, 0), "an empty suit raises no barrier");
            g.State = FrameState.Piloted; g.Pilot = 7;
            check(FrameRules.Shelters(g, 0, 0, 0, 1, 1, 0, 6, 1, 0), "a player inside is sheltered from an attacker outside");
            check(!FrameRules.Shelters(g, 0, 0, 0, 1, 1, 0, 2, 1, 0), "an attacker inside the dome is not stopped");
            check(!FrameRules.Shelters(g, 0, 0, 0, 4, 1, 0, 9, 1, 0), "a player outside the dome is not sheltered");
            check(FrameRules.Repels(g, 2, 0, 0) && !FrameRules.Repels(g, 4, 0, 0), "an enemy inside the dome is pushed, one outside is not");
            check(!FrameRules.Repels(new FrameRecord(1, 3, tank), 0, 0, 0), "an empty suit pushes nothing");
            check(!FrameRules.RegenShield(f, 16.9f, 0.1f), "no refill before the quiet time");
            for (var i = 0; i < 24; i++) FrameRules.RegenShield(f, 17f + i * 0.1f, 0.1f);
            check(!f.ShieldUp && f.Shield > 23f && f.Shield < 25f, "refilling but not yet standing");
            for (var i = 0; i < 3; i++) FrameRules.RegenShield(f, 20f + i * 0.1f, 0.1f);
            check(f.ShieldUp, "stands again at the raise fraction");
            var plain = new FrameRecord(1, 2, assault);
            check(!plain.ShieldUp && !FrameRules.ShieldHit(plain, 5, 1) && !FrameRules.RegenShield(plain, 100, 1), "suits without a barrier have none");
        }

        // ---- messages ------------------------------------------------------------------------------------------------

        // The state fits the carrier's per-call data (192 bytes).
        check(Marshal.SizeOf<FrameStateMessage>() == 95 && Marshal.OffsetOf<FrameStateMessage>(nameof(FrameStateMessage.Pilot)).ToInt32() == 6
            && Marshal.OffsetOf<FrameStateMessage>(nameof(FrameStateMessage.Ejection)).ToInt32() == 54 && Marshal.OffsetOf<FrameStateMessage>(nameof(FrameStateMessage.Spec)).ToInt32() == 55
            && Marshal.OffsetOf<FrameStateMessage>(nameof(FrameStateMessage.Definition)).ToInt32() == 63
            && Marshal.SizeOf<FrameRequestMessage>() == 26 && Marshal.SizeOf<FrameFireMessage>() == 37 && Marshal.SizeOf<FrameRocketMessage>() == 30
            && Marshal.SizeOf<FrameHitMessage>() == 25 && Marshal.OffsetOf<FrameHitMessage>(nameof(FrameHitMessage.Damage)).ToInt32() == 20
            && Marshal.SizeOf<FrameCheckpointEntry>() == 107 && Marshal.OffsetOf<FrameCheckpointEntry>(nameof(FrameCheckpointEntry.State)).ToInt32() == 12, "message and checkpoint entry layouts");
        {
            var host = new FrameRecord(9, 4, assault) { State = FrameState.Piloted, Pilot = 77, Armor = 55, Shield = 7, ShieldUp = true, Magazine = 12, Reserve = 40, X = 1, Y = 2, Z = 3, Yaw = 45, Sequence = 5, BoostEndsAt = 13f, Sieged = true, DefinitionKey = 0xC0FFEEu };
            var bytes = ForgeCore.NetBody.Of(FrameStateMessage.From(host, 10f, FrameEjection.None));
            check(ForgeCore.NetBody.TryRead(bytes, out FrameStateMessage read) && read.Spec == FrameSpecKey.Of(assault) && read.Spec == host.SpecKey, "state round trip names the model by its content key");
            var mirror = new FrameRecord(9, 4, assault);
            check(read.ApplyTo(mirror, 100f), "mirror takes the state");
            check(mirror.State == FrameState.Piloted && mirror.Pilot == 77 && mirror.Armor == 55 && mirror.Shield == 7 && mirror.ShieldUp && mirror.Sieged && Math.Abs(mirror.BoostEndsAt - 103f) < 1e-3f, "fields survive, timers move to the local clock");
            check(mirror.DefinitionKey == 0xC0FFEEu, "the state carries the definition key, so every peer finds the definition that names the suit");
            check(!read.ApplyTo(mirror, 101f), "a repeated sequence is ignored");
            check(!ForgeCore.NetBody.TryRead(new byte[3], out FrameStateMessage _), "short data is refused");
        }

        // ---- checkpoint persistence and replication -----------------------------------------------------------------
        {
            var tank = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shield\":{\"Capacity\":50,\"RegenDelay\":6,\"RegenRate\":10}")));
            var specs = new Dictionary<ulong, FrameSpec> { [FrameSpecKey.Of(tank)] = tank, [FrameSpecKey.Of(assault)] = assault };
            var saved = new FrameRecord(7, 4, tank) { State = FrameState.Piloted, Pilot = 9, Sieged = true, BoostEndsAt = 15, Sequence = 5, X = 8, Armor = 60, Magazine = 12 };
            var records = new List<FrameRecord> { saved, new(7, 5, assault) { State = FrameState.Destroyed } };
            var snapshot = FrameCheckpoint.Capture(records, 10);
            check(snapshot.Length == Marshal.SizeOf<FrameCheckpointEntry>() && saved.HasPilot && saved.Sieged, "checkpoint excludes wrecks without mutating the live suit");
            records.Clear();
            records.Add(new(7, 6, assault));
            var rebuilt = FrameCheckpoint.Restore(snapshot, specs, 100, 20);
            check(rebuilt.Count == 1 && rebuilt[0].ThrowId == 4 && !ReferenceEquals(rebuilt[0], saved), "checkpoint rebuilds a saved suit even if destroyed/removed and excludes later suits");
            check(rebuilt[0].State == FrameState.Idle && rebuilt[0].Pilot == 0 && !rebuilt[0].Sieged && rebuilt[0].BoostEndsAt == 0, "recovery removes pilot and active modes");
            check(rebuilt[0].ShieldUp && rebuilt[0].Shield == 50 && rebuilt[0].Armor == 60 && rebuilt[0].Magazine == 12 && rebuilt[0].X == 8, "recovery retains shield, health, ammunition and placement");
            check(rebuilt[0].Sequence == 21, "restored state is newer than the supplied live population");
            check(FrameCheckpoint.Restore((byte[])snapshot.Clone(), specs, 200, 30).Single().ShieldUp, "a peer that holds the copy restores it as the host does");
            check(FrameCheckpoint.Restore(FrameCheckpoint.Capture(Array.Empty<FrameRecord>(), 0), specs, 0, 0).Count == 0, "an empty checkpoint restores no suit");
            bool Refused(byte[]? copy, Dictionary<ulong, FrameSpec> known)
            {
                try { FrameCheckpoint.Restore(copy, known, 0, 0); }
                catch (InvalidOperationException) { return true; }
                return false;
            }
            check(Refused(null, specs) && Refused(snapshot[..^1], specs), "a missing or cut checkpoint copy is an explicit failure");
            check(Refused(snapshot, new Dictionary<ulong, FrameSpec>()), "missing checkpoint model is an explicit failure");
        }

        {
            // A heavy object hangs behind the shell at chest height, clear of it by the gap, whatever its depth.
            var thin = CarrySocket.Centre(-0.4f, 1.5f, 0.1f);
            var deep = CarrySocket.Centre(-0.4f, 1.5f, 0.5f);
            check(thin.X == 0f && thin.Y == 1.5f && Math.Abs(thin.Z - (-0.4f - CarrySocket.Gap - 0.05f)) < 1e-6f, "a carried object is centred between the shoulders at chest height");
            check(Math.Abs((deep.Z + 0.25f) - (-0.4f - CarrySocket.Gap)) < 1e-6f && deep.Z < thin.Z, "a deeper object's near face still sits the gap behind the shell");
        }

        // ---- model names and nodes -----------------------------------------------------------------------------------

        {
            check(ForgeCore.PackItem.TrySplit("Pack:item", out var pack, out var item) && pack == "Pack" && item == "item", "Pack:item splits");
            check(!ForgeCore.PackItem.TrySplit("item", out _, out _) && !ForgeCore.PackItem.TrySplit(":item", out _, out _) && !ForgeCore.PackItem.TrySplit("Pack:", out _, out _) && !ForgeCore.PackItem.TrySplit("a:b:c", out _, out _) && !ForgeCore.PackItem.TrySplit(null, out _, out _),
                "a name without exactly one pack and one item is refused");
            check(FrameModelName.MarkIcon("Other:walker") == "Other:mark-walker", "the cockpit mark is mark-<item> in the icon pack of the model\u0027s pack");
            var spec = FrameSpecParser.Parse(Json(Minimal));
            var all = FrameBodyRig.PoseLinks.Select(l => l.Model).ToHashSet();
            check(FrameBodyRig.PoseLinks.Count() == 16 && FrameBodyRig.MissingNodes(spec, all.Contains).Count() == 0, "a model with the 16 rig bones lacks nothing");
            all.Remove("chest");
            var missing = FrameBodyRig.MissingNodes(spec, all.Contains).ToList();
            check(missing.Count == 1 && missing[0] == ("Model", "chest"), "a missing rig bone is named with the definition field");
        }
    }
}
