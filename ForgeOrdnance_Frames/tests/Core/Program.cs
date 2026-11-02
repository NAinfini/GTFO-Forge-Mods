using GameData;
using System.Runtime.InteropServices;
using System.Text.Json;
using ForgeOrdnanceFrames;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    checks++;
}

JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

const string Minimal = "{\"Type\":\"frames.Exosuit\",\"Model\":\"Frames:frame-m15\",\"Armor\":100,"
    + "\"Weapon\":{\"Gear\":38,\"Magazine\":30,\"Reserve\":60}}";

// ---- definitions ---------------------------------------------------------------------------------------------

{
    var spec = FrameSpecParser.Parse(Json(Minimal));
    Check(spec.Model == "Frames:frame-m15" && spec.Armor == 100 && spec.Weapon.Gear == 38 && spec.Weapon.Magazine == 30 && spec.Weapon.DamageMultiplier == 1f, "minimal payload parses with defaults");
    Check(spec.Boost == null && spec.Siege == null && spec.Scan == null && spec.Shield == null && !spec.CarryHeavy, "specials are off unless present");
    Check(spec.DamageReduction == 0f && spec.Movement.Walk > 1f && spec.Movement.Sprint > 1f && spec.Movement.Jump > 1f && spec.Light.RangeMultiplier >= 2f,
        "no damage reduction, faster walking, a sprint bonus, a higher jump and the wider lamp by default");
    Check(spec.Opening == null && spec.Shell.Length == 0, "no opening parts and no first-person shell unless listed");
    Check(Math.Abs(FrameSequence.EnterSeconds - 2.85f) < 1e-4f && FrameSequence.ExitSeconds < 2f, "boarding lasts 2.85 s to the last power-on block and leaving ends on the landing, for every model (no definition sets them)");
}

void Rejected(string name, string payload, string field)
{
    try { FrameSpecParser.Parse(Json(payload)); }
    catch (ForgeCore.StrictJsonException e) { Check(e.Field == field, name + ": names " + field + ", got " + e.Field); return; }
    throw new Exception("FAIL: " + name + " was accepted");
}

Rejected("unknown top-level field", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Health\":1"), "Health");
Rejected("unreachable second active special", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Boost\":{\"Seconds\":2,\"SpeedMultiplier\":2,\"Cooldown\":10},\"Siege\":{}"), "Siege");
Rejected("unknown weapon field", Minimal.Replace("\"Gear\":38", "\"Gear\":38,\"Damage\":1"), "Weapon.Damage");
Rejected("weapon without gear", Minimal.Replace("\"Gear\":38,", ""), "Weapon.Gear");
Rejected("damage reduction above 90 percent", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"DamageReduction\":0.95"), "DamageReduction");
Rejected("walk below the player's own", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Movement\":{\"Walk\":0.9}"), "Movement.Walk");
Rejected("jump below the player's own", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Movement\":{\"Jump\":0.8}"), "Movement.Jump");
Rejected("opening without parts", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{}"), "Opening.Parts");
Rejected("opening part twice", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{\"Parts\":[{\"Node\":\"p\"},{\"Node\":\"p\"}]}"), "Opening.Parts[1].Node");
Rejected("opening part with a runtime hinge", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{\"Parts\":[{\"Node\":\"p\",\"Axis\":[0,1,0]}]}"), "Opening.Parts[0].Axis");
Rejected("opening with a runtime curve", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{\"Ease\":4,\"Parts\":[{\"Node\":\"p\"}]}"), "Opening.Ease");
Rejected("shell on an unknown body bone", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shell\":[{\"Bone\":\"hand.L\",\"Body\":\"LeftPaw\"}]"), "Shell[0].Body");
Rejected("shell body bone by number", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shell\":[{\"Bone\":\"hand.L\",\"Body\":\"3\"}]"), "Shell[0].Body");
Rejected("shell body bone twice", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shell\":[{\"Bone\":\"hand.L\",\"Body\":\"LeftHand\"},{\"Bone\":\"hand.R\",\"Body\":\"LeftHand\"}]"), "Shell[1].Body");
{
    var opened = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Opening\":{\"Parts\":[{\"Node\":\"plate\"},{\"Node\":\"plate.back.L\",\"Seam\":true}]},\"Shell\":[{\"Bone\":\"forearm.R\",\"Body\":\"RightLowerArm\"}]")));
    Check(opened.Opening!.Parts.Length == 2 && opened.Opening.Parts[0].Node == "plate" && opened.Opening.Parts[1].Node == "plate.back.L"
        && !opened.Opening.Parts[0].Seam && opened.Opening.Parts[1].Seam, "opening parts parse as the bones the model's clips move, the seam's marked");
    Check(FrameSpecParser.Parse(Json(Minimal)).Weapon.RecoilMultiplier == 1f, "the suit gun keeps the gun's own recoil unless the definition lowers it");
    Check(FrameSpecParser.Parse(Json(Minimal)).Weapon.ReloadMultiplier == 1f, "every gun keeps its own reload time unless the definition shortens it");
    Rejected("reload multiplier above one", Minimal.Replace("\"Gear\":38", "\"Gear\":38,\"ReloadMultiplier\":1.5"), "Weapon.ReloadMultiplier");
    Rejected("recoil multiplier of zero", Minimal.Replace("\"Gear\":38", "\"Gear\":38,\"RecoilMultiplier\":0"), "Weapon.RecoilMultiplier");
    Check(opened.Shell.Length == 1 && opened.Shell[0].Bone == "forearm.R" && opened.Shell[0].Body == FrameBodyBone.RightLowerArm, "shell bones map onto body bones");
    Check(FrameSpecKey.Of(opened) != FrameSpecKey.Of(opened with { Shell = Array.Empty<ShellBone>() }), "the shell is part of the content key");
}
Rejected("bad lamp colour", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Light\":{\"Colour\":\"warm\"}"), "Light.Colour");
Rejected("bad strip colour", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Light\":{\"Glow\":\"#12\"}"), "Light.Glow");
Rejected("missing model", Minimal.Replace("\"Model\":\"Frames:frame-m15\",", ""), "Model");
Rejected("missing weapon", "{\"Type\":\"frames.Exosuit\",\"Model\":\"Frames:m\",\"Armor\":1}", "Weapon");
Rejected("armour out of range", Minimal.Replace("\"Armor\":100", "\"Armor\":0"), "Armor");
Rejected("fractional magazine", Minimal.Replace("\"Magazine\":30", "\"Magazine\":30.5"), "Weapon.Magazine");
Rejected("string number", Minimal.Replace("\"Armor\":100", "\"Armor\":\"100\""), "Armor");
Rejected("boost without speed", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Boost\":{\"Seconds\":2,\"Cooldown\":5}"), "Boost.SpeedMultiplier");
Rejected("scan radius missing", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Scan\":{}"), "Scan.Radius");

{
    // The definitions this package ships must load.
    var file = Path.Combine(AppContext.BaseDirectory, "frames.json");
    using var doc = JsonDocument.Parse(File.ReadAllText(file));
    var models = new Dictionary<string, FrameSpec>();
    var codes = new HashSet<string>();
    foreach (var entry in doc.RootElement.EnumerateArray())
    {
        var spec = FrameSpecParser.Parse(entry.GetProperty("Payload"));
        Check(spec.Model.StartsWith("Frames:frame-m", StringComparison.Ordinal), "shipped definition names a prefab of the Frames pack");
        Check(entry.GetProperty("Id").GetString()!.StartsWith("nainfini.frames.", StringComparison.Ordinal), "shipped Id carries the author namespace");
        Check(codes.Add(string.Join(",", entry.GetProperty("Code").EnumerateArray().Select(c => c.GetString()))), "shipped codes are distinct");
        models[spec.Model.Substring("Frames:".Length)] = spec;
    }
    Check(models.Count == 4, "four shipped models");
    var keys = new HashSet<ulong>();
    foreach (var m in models.Values) keys.Add(FrameSpecKey.Of(m));
    Check(keys.Count == 4, "each shipped model has its own content key");
    LangChecks.Run(Check, models.Values);
    // Each model's specialty is data: tank barrier and carrying, firepower siege, recon scan, assault charge.
    Check(models["frame-m15"].Boost != null && models["frame-m15"].Shield == null, "assault: charge only");
    Check(models["frame-m16"].Shield != null && models["frame-m16"].CarryHeavy && models["frame-m16"].Boost == null, "tank: barrier and heavy carrying");
    Check(FrameRules.CarryBlockReleased(true, true, true) && !FrameRules.CarryBlockReleased(false, true, true)
        && !FrameRules.CarryBlockReleased(true, false, true) && !FrameRules.CarryBlockReleased(true, true, false),
        "the carry slot's wield block is lifted only for the local pilot of a heavy-carrying suit");
    Check(models["frame-m17"].Siege != null && models["frame-m17"].Rockets != null && models["frame-m17"].Scan == null, "firepower: siege and rockets");
    Check(models["frame-m15"].Rockets == null && models["frame-m16"].Rockets == null && models["frame-m18"].Rockets == null, "only the firepower model has rockets");
    var fp = models["frame-m17"];
    Check(fp.Siege!.FireRateMultiplier > 1f && fp.Siege.ReloadMultiplier < 1f && fp.Shield != null && fp.Shield.WhileSieged && !models["frame-m16"].Shield!.WhileSieged,
        "firepower: planting fires faster, reloads faster and raises its own barrier; the tank's barrier always stands");
    GunChecks.Run(Check, models);
    GunLoanChecks.Run(Check, models);
    var planted = new FrameRecord(3, 3, fp) { State = FrameState.Piloted, Pilot = 5 };
    var walking = !FrameRules.BarrierUp(planted) && !FrameRules.Repels(planted, planted.X, planted.Y + 1.2f, planted.Z) && !FrameRules.ShieldHit(planted, 10f, 1f) && planted.Shield == fp.Shield!.Capacity;
    planted.Sieged = true;
    Check(walking && FrameRules.BarrierUp(planted) && FrameRules.Repels(planted, planted.X, planted.Y + 1.2f, planted.Z) && !FrameRules.ShieldHit(planted, 10f, 2f) && planted.Shield < fp.Shield!.Capacity,
        "a barrier that stands while planted is down (and takes nothing) while walking and up once planted");
    Rejected("barrier while planted without a siege", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shield\":{\"Capacity\":50,\"RegenRate\":10,\"WhileSieged\":true}"), "Shield.WhileSieged");
    Check(models["frame-m18"].Scan != null && models["frame-m18"].Siege == null && models["frame-m18"].Shield == null, "recon: scan only");
    Check(FrameRules.ScanTags(ForgeCore.EnemyAlert.Awake) && FrameRules.ScanTags(ForgeCore.EnemyAlert.Hunting) && !FrameRules.ScanTags(ForgeCore.EnemyAlert.Sleeping) && !FrameRules.ScanTags(ForgeCore.EnemyAlert.Off), "recon: the scan tags awake enemies, not sleeping ones");
    // Every suit is faster and jumps higher than on foot; recon most, then assault, firepower, tank (still above the player).
    var order = new[] { "frame-m18", "frame-m15", "frame-m17", "frame-m16" };
    for (var i = 0; i < order.Length; i++)
    {
        var mv = models[order[i]].Movement;
        Check(mv.Walk > 1f && mv.Sprint > 1f && mv.Jump > 1f, order[i] + " is faster and jumps higher than the player on foot");
        if (i == 0) continue;
        var up = models[order[i - 1]].Movement;
        Check(up.Walk > mv.Walk && up.Sprint >= mv.Sprint && up.Jump > mv.Jump, order[i - 1] + " outruns and outjumps " + order[i]);
    }
    foreach (var m in models.Values)
    {
        Check(m.Opening != null && m.Opening.Parts.Length >= 2 && m.Opening.Parts.All(part => !part.Node.Contains("helmet", StringComparison.Ordinal)), m.Model + " lists its opening parts; the helmet stays shut");
        Check(m.Shell.Length >= 6 && m.Shell.Any(b => b.Body == FrameBodyBone.LeftUpperArm) && m.Shell.Any(b => b.Body == FrameBodyBone.RightUpperArm) && m.Shell.Any(b => b.Body == FrameBodyBone.Hips)
        && !m.Shell.Any(b => b.Body is FrameBodyBone.LeftLowerArm or FrameBodyBone.RightLowerArm or FrameBodyBone.LeftHand or FrameBodyBone.RightHand), m.Model + " lines its skeleton up on the upper arms and hips and keeps the game's own forearms and hands");
        var worn = FrameBodyRig.ShellBones(m.Shell);
        Check(worn.Length == m.Shell.Length && worn.All(b => b.Worn != b.Body is ForgeVisual.BodyBone.LeftUpperArm or ForgeVisual.BodyBone.RightUpperArm)
            && worn.Where(b => b.Body is ForgeVisual.BodyBone.LeftUpperLeg or ForgeVisual.BodyBone.LeftLowerLeg or ForgeVisual.BodyBone.LeftFoot).All(b => b.Scale == ForgeVisual.BodyMeasure.LeftLeg)
            && worn.Where(b => b.Body is ForgeVisual.BodyBone.RightUpperLeg or ForgeVisual.BodyBone.RightLowerLeg or ForgeVisual.BodyBone.RightFoot).All(b => b.Scale == ForgeVisual.BodyMeasure.RightLeg)
            && worn.Where(b => b.Body is ForgeVisual.BodyBone.Hips or ForgeVisual.BodyBone.Spine).All(b => b.Scale == ForgeVisual.BodyMeasure.ShoulderSpan),
            m.Model + " wears its shell: the upper arms only line up, each leg sized by its own length, the body by the shoulder span");
        Check(FrameBodyRig.HingedParts(m.Opening).SequenceEqual(m.Opening!.Parts.Select(p => p.Node)), m.Model + " keeps its opening parts shut on the shell");
    }
}

// The suits' rig onto ForgeVisual's body bones: every definition bone has its humanoid namesake, the pose table is well formed.
foreach (var bone in Enum.GetValues<FrameBodyBone>())
    Check(FrameBodyRig.Body(bone).ToString() == bone.ToString(), "body bone " + bone + " maps onto ForgeVisual's namesake");
Check(ForgeVisual.PoseSolver.Validate(FrameBodyRig.PoseLinks) == null, "the suit pose table is well formed");
Check(FrameBodyRig.PoseLinks.Length == 16 && FrameBodyRig.PoseLinks[0].Body == ForgeVisual.BodyBone.Hips
    && FrameBodyRig.PoseLinks[FrameBodyRig.RightHandLink].Model == "hand.R" && FrameBodyRig.PoseLinks[FrameBodyRig.LeftUpperArmLink].Model == "upperarm.L"
    && FrameBodyRig.PoseLinks[FrameBodyRig.RightUpperArmLink].Model == "upperarm.R", "the pose table starts at the pelvis and names the hand and arms the suit uses");
Check(FrameBodyRig.PoseLinks[2].Body == ForgeVisual.BodyBone.UpperChest && FrameBodyRig.PoseLinks[2].BodyAlt == ForgeVisual.BodyBone.Chest
    && FrameBodyRig.PoseLinks[2].TowardBody == ForgeVisual.BodyBone.Neck, "the game's upper chest and neck fold into the suit's chest");
Check(ForgeVisual.PoseSolver.ValidateReach(FrameBodyRig.ArmReach, FrameBodyRig.PoseLinks.Length) == null
    && FrameBodyRig.ArmReach.Length == 2 && Array.TrueForAll(FrameBodyRig.ArmReach, r =>
        FrameBodyRig.PoseLinks[r.Upper].Model.StartsWith("upperarm.") && FrameBodyRig.PoseLinks[r.Lower].Model.StartsWith("forearm.")
        && FrameBodyRig.PoseLinks[r.End].Model.StartsWith("hand.") && FrameBodyRig.PoseLinks[r.Upper].Model[^1] == FrameBodyRig.PoseLinks[r.End].Model[^1]
        && FrameBodyRig.PoseLinks[r.Anchor].Model == "chest"), "both arms reach the body's wrists from the chest, each its own side's upper arm, forearm and hand");

// ---- host rules ----------------------------------------------------------------------------------------------

var assault = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Boost\":{\"Seconds\":3,\"SpeedMultiplier\":2,\"Cooldown\":10}")));
{
    var f = new FrameRecord(7, 1, assault) { State = FrameState.Arriving, StateEndsAt = 1f };
    Check(FrameRules.CanEnter(f, 1f, false, false) == FrameRejection.NotIdle, "cannot board while it falls");
    Check(FrameRules.Tick(f, 1f, out var gone) && f.State == FrameState.Idle && !gone, "lands and stands empty");
    Check(FrameRules.CanEnter(f, FrameRules.BoardDistance + 0.1f, false, false) == FrameRejection.TooFar, "too far to board");
    Check(FrameRules.CanEnter(f, float.NaN, false, false) == FrameRejection.TooFar, "unknown distance is too far");
    Check(FrameRules.CanEnter(f, 1f, true, false) == FrameRejection.AlreadyPiloting, "one suit per pilot");
    Check(FrameRules.CanEnter(f, 1f, false, true) == FrameRejection.PilotDown, "a downed player cannot board");
    Check(FrameRules.CanEnter(f, FrameRules.BoardSpot, false, false) == FrameRejection.None, "board in reach");
    FrameRules.Enter(f, 42, 10f);
    Check(f.State == FrameState.Entering && f.Pilot == 42 && Math.Abs(f.StateEndsAt - (10f + FrameSequence.EnterSeconds)) < 1e-4f, "boarding takes the boarding sequence's 2.85 s");
    Check(FrameRules.CanEnter(f, 1f, false, false) == FrameRejection.NotIdle, "a second player cannot board an occupied suit");
    Check(FrameRules.CanExit(f, 42) == FrameRejection.NotIdle, "cannot leave mid-boarding");
    FrameRules.Tick(f, 10f + FrameSequence.EnterSeconds - 0.01f, out _);
    var stillBoarding = f.State == FrameState.Entering;
    FrameRules.Tick(f, 10f + FrameSequence.EnterSeconds, out _);
    Check(stillBoarding && f.State == FrameState.Piloted, "boarding completes, and the pilot moves, exactly when the power-on's picture is whole");
    Check(FrameRules.CanExit(f, 43) == FrameRejection.NotPilot, "only the pilot can leave");

    Check(FrameRules.CanBoost(f, 42, 12f) == FrameRejection.None, "boost ready");
    FrameRules.Boost(f, 12f);
    var walk = assault.Movement.Walk;
    Check(FrameRules.SpeedMultiplier(f, 13f) == walk * 2f && FrameRules.SpeedMultiplier(f, 15.5f) == walk, "boost multiplies speed for its time only");
    Check(FrameRules.CanBoost(f, 42, 13f) == FrameRejection.Charging && FrameRules.CanBoost(f, 42, 20f) == FrameRejection.Cooling && FrameRules.CanBoost(f, 42, 25f) == FrameRejection.None,
        "a charge refuses while it runs (charging), then cools down (not ready)");
    var planting = new FrameRecord(7, 8, assault) { State = FrameState.Piloted, Pilot = 42, SiegeDeployEndsAt = 5f };
    Check(FrameRules.CanBoost(planting, 42, 1f) == FrameRejection.Planted, "a planted or planting suit refuses a charge as planted");
    Check(FrameRules.CanSiege(f, 42) == FrameRejection.NoSpecial, "no siege on a suit without it");

    Check(FrameRules.ReportAmmo(f, 42, 20, 60) && f.Magazine == 20, "pilot reports spent ammunition");
    Check(!FrameRules.ReportAmmo(f, 42, 30, 60), "a report cannot add ammunition");
    Check(!FrameRules.ReportAmmo(f, 43, 10, 10), "only the pilot reports");

    Check(!FrameRules.Damage(f, 20f, 30f) && Math.Abs(f.Armor - 80f) < 1e-3f, "hits land on the suit's armour");
    var plated = new FrameRecord(7, 9, assault with { DamageReduction = 0.4f, Siege = new SiegeSpec(1f, 1.4f, 0.5f, 1f, 1f, 1f) }) { Sieged = true };
    Check(Math.Abs(FrameRules.Absorbed(plated, 100f) - 30f) < 1e-3f, "damage reduction and the planted suit's reduction both apply");
    FrameRules.Exit(f, 31f, 1, 2, 3, 90);
    Check(f.State == FrameState.Exiting && f.X == 1 && f.Yaw == 90 && f.Pilot == 42, "leaving keeps the pilot until the sequence ends");
    FrameRules.Tick(f, 31f + FrameSequence.ExitSeconds - 0.01f, out _);
    Check(f.State == FrameState.Exiting, "leaving holds until the landing");
    FrameRules.Tick(f, 31f + FrameSequence.ExitSeconds, out _);
    Check(f.State == FrameState.Idle && f.Pilot == 0 && Math.Abs(f.Armor - 80f) < 1e-3f && f.Magazine == 20, "left standing with its armour and ammunition");
    Check(!FrameRules.Damage(f, 50f, 35f) && Math.Abs(f.Armor - 80f) < 1e-3f, "an empty suit takes no damage");

    FrameRules.Enter(f, 9, 40f);
    FrameRules.Tick(f, 45f, out _);
    var wrecked = FrameRules.Damage(f, 1000f, 50f);
    Check(wrecked && f.State == FrameState.Destroyed && f.Pilot == 0 && f.Armor == 0f, "a wrecking hit throws the pilot out");
    Check(!FrameRules.Tick(f, 55f, out var early) && !early, "the wreck lingers");
    FrameRules.Tick(f, 50f + FrameRules.WreckSeconds, out var removed);
    Check(removed, "the wreck is removed");

    var g = new FrameRecord(7, 2, assault) { State = FrameState.Piloted, Pilot = 5 };
    FrameRules.Eject(g, 4, 5, 6, 0);
    Check(g.State == FrameState.Idle && g.Pilot == 0 && g.X == 4, "ejection leaves the suit standing at once");

    var worn = new FrameRecord(7, 3, assault) { State = FrameState.Piloted, Pilot = 5 };
    var boarding = new FrameRecord(7, 4, assault) { State = FrameState.Entering, Pilot = 5 };
    Check(!FrameRules.Interrupted(worn, true, 900f, 0f, 0f), "a worn suit is never interrupted: it moves with its pilot");
    Check(!FrameRules.Interrupted(boarding, false, FrameRules.BoardDistance, 0f, 0f) && FrameRules.Interrupted(boarding, false, 0f, 0f, FrameRules.TetherReach + 0.1f)
        && FrameRules.Interrupted(boarding, true, 0f, 0f, 0f), "boarding ends when the game holds the pilot or puts it beyond the tether");
}
{
    var sieger = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Siege\":{\"DamageMultiplier\":2}")));
    var f = new FrameRecord(1, 1, sieger) { State = FrameState.Piloted, Pilot = 3 };
    Check(FrameRules.CanSiege(f, 3) == FrameRejection.None, "siege available");
    FrameRules.ToggleSiege(f, 0f);
    Check(!f.Sieged && f.SiegeDeployEndsAt == sieger.Siege!.DeploySeconds && FrameRules.SpeedMultiplier(f, 0f) == 0f, "deployment locks movement before granting siege bonuses");
    FrameRules.Tick(f, sieger.Siege!.DeploySeconds, out _);
    Check(f.Sieged && FrameRules.SpeedMultiplier(f, 0f) == 0f, "a planted suit cannot move");
    FrameRules.ToggleSiege(f, 2f);
    Check(!f.Sieged && FrameRules.SpeedMultiplier(f, 0f) == sieger.Movement.Walk, "packing up restores movement");
    Check(FrameRules.Rockets(f, 3, 0f) == FrameRejection.NoSpecial, "no rockets on a suit without them");
}
{
    const string launcher = "\"Launcher\":{\"Pod\":\"Frames:pod\",\"Round\":\"Frames:round\",\"Bone\":\"plate\",\"Position\":[0.3,1.9,-0.1],\"Rotation\":[0,15,0]}";
    var rocketeer = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":40," + launcher + "}")));
    var k = rocketeer.Rockets!;
    Check(k.Salvo == 4 && k.Radius == 4f && k.Spread == 1.5f && k.Cooldown == 12f && k.Range == 120f && k.Speed == 45f, "rocket defaults");
    Check(k.Launcher.Pod == "Frames:pod" && k.Launcher.Round == "Frames:round" && k.Launcher.Bone == "plate" && k.Launcher.X == 0.3f && k.Launcher.Z == -0.1f && k.Launcher.Yaw == 15f,
        "the launcher names its pod and round and its place on a bone");
    Rejected("rockets without a launcher", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":40}"), "Rockets.Launcher");
    Rejected("launcher scaled", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":40,\"Launcher\":{\"Pod\":\"Frames:p\",\"Round\":\"Frames:r\",\"Bone\":\"plate\",\"Position\":[0,2,0],\"Rotation\":[0,0,0],\"Scale\":[-1,1,1]}}"), "Rockets.Launcher.Scale");
    Rejected("launcher position not three numbers", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":40,\"Launcher\":{\"Pod\":\"Frames:p\",\"Round\":\"Frames:r\",\"Bone\":\"plate\",\"Position\":[0,2],\"Rotation\":[0,0,0]}}"), "Rockets.Launcher.Position");
    var f = new FrameRecord(1, 1, rocketeer) { State = FrameState.Piloted, Pilot = 3 };
    Check(FrameRules.Rockets(f, 4, 0f) == FrameRejection.NotPilot, "only the pilot launches rockets");
    Check(FrameRules.Rockets(f, 3, 5f) == FrameRejection.None && f.RocketsReadyAt == 17f, "a salvo starts the cooldown");
    Check(FrameRules.Rockets(f, 3, 16.9f) == FrameRejection.Cooling && FrameRules.Rockets(f, 3, 17f) == FrameRejection.None, "the next salvo waits for the cooldown");
    var inside = true; var differ = false;
    FrameRules.RocketShot(77, 0, 1.5f, out var x0, out var z0, out var d0);
    for (var i = 0; i < 8; i++)
    {
        FrameRules.RocketShot(77, i, 1.5f, out var x, out var z, out var d);
        FrameRules.RocketShot(77, i, 1.5f, out var x2, out var z2, out var d2);
        inside &= x * x + z * z <= 1.5f * 1.5f + 1e-4f && x == x2 && z == z2 && d == i * FrameRules.RocketInterval;
        differ |= i > 0 && (x != x0 || z != z0);
    }
    Check(inside && differ && d0 == 0f, "rocket scatter is the same on every peer, inside the spread, and differs per rocket");
    Rejected("salvo too large", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":1,\"Salvo\":9}"), "Rockets.Salvo");
    Rejected("rockets without damage", Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{}"), "Rockets.Damage");
}

{
    var tank = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shield\":{\"Capacity\":50,\"RegenDelay\":6,\"RegenRate\":10}")));
    Check(tank.Shield!.Radius == 2.75f && tank.Shield.RaiseFraction == 0.5f, "barrier defaults");
    var f = new FrameRecord(1, 1, tank) { State = FrameState.Piloted, Pilot = 3 };
    Check(f.ShieldUp && f.Shield == 50, "barrier starts full and standing");
    Check(!FrameRules.ShieldHit(f, 30, 10) && f.Shield == 20 && f.Armor == 100, "the barrier takes enemy attacks, the armour does not");
    Check(FrameRules.ShieldHit(f, 30, 11) && !f.ShieldUp && f.Shield == 0, "emptied, the barrier breaks");
    Check(!FrameRules.ShieldHit(f, 30, 12) && f.ShieldLastHit == 11, "a broken barrier stops nothing");
    var g = new FrameRecord(1, 2, tank);
    FrameRules.Place(g, 0, 0, 0, 0);
    Check(!FrameRules.Shelters(g, 0, 0, 0, 1, 1, 0, 6, 1, 0), "an empty suit raises no barrier");
    g.State = FrameState.Piloted; g.Pilot = 7;
    Check(FrameRules.Shelters(g, 0, 0, 0, 1, 1, 0, 6, 1, 0), "a player inside is sheltered from an attacker outside");
    Check(!FrameRules.Shelters(g, 0, 0, 0, 1, 1, 0, 2, 1, 0), "an attacker inside the dome is not stopped");
    Check(!FrameRules.Shelters(g, 0, 0, 0, 4, 1, 0, 9, 1, 0), "a player outside the dome is not sheltered");
    Check(FrameRules.Repels(g, 2, 0, 0) && !FrameRules.Repels(g, 4, 0, 0), "an enemy inside the dome is pushed, one outside is not");
    Check(!FrameRules.Repels(new FrameRecord(1, 3, tank), 0, 0, 0), "an empty suit pushes nothing");
    Check(!FrameRules.RegenShield(f, 16.9f, 0.1f), "no refill before the quiet time");
    for (var i = 0; i < 24; i++) FrameRules.RegenShield(f, 17f + i * 0.1f, 0.1f);
    Check(!f.ShieldUp && f.Shield > 23f && f.Shield < 25f, "refilling but not yet standing");
    for (var i = 0; i < 3; i++) FrameRules.RegenShield(f, 20f + i * 0.1f, 0.1f);
    Check(f.ShieldUp, "stands again at the raise fraction");
    var plain = new FrameRecord(1, 2, assault);
    Check(!plain.ShieldUp && !FrameRules.ShieldHit(plain, 5, 1) && !FrameRules.RegenShield(plain, 100, 1), "suits without a barrier have none");
}

// ---- weapon and steps ----------------------------------------------------------------------------------------

{
    var move = new MovementSpec(1f, 1.5f, 1.3f, 4f, 8f); // sprint 4 s, cool 8 s from full
    var heat = new SprintHeat();
    var bonus = 0;
    while (!heat.Overheated && bonus < 100) if (heat.Step(move, true, 0.1f)) bonus++;
    Check(bonus >= 39 && bonus <= 40, "the sprint bonus lasts SprintSeconds, then the suit overheats, got " + bonus);
    Check(!heat.Step(move, true, 0.1f), "an overheated suit gets no sprint bonus while sprint is held (it cools meanwhile)");
    var cooling = 1;
    while (heat.Overheated && cooling < 100) { heat.Step(move, false, 0.1f); cooling++; }
    Check(cooling >= 51 && cooling <= 53, "it stays overheated until the heat falls to the rearm level, got " + cooling + " steps");
    Check(heat.Step(move, true, 0.1f), "cooled to the rearm level, sprint works again");
}
{
    Check(FrameNoise.PilotMultiplier == 2f, "a piloted suit is heard at twice an ordinary player's distance");
    Check(FrameNoise.LandingRadius(8f) == 16f && FrameNoise.LandingRadius(20f) == 40f, "a pilotless landing is heard as far as the player's run, doubled");
    EnemyDetectionDataBlock.Default = new EnemyDetectionDataBlock { movementDetectionDistance = 8f };
    Check(Noise.LandingRadius() == 16f, "the landing radius is twice the game's default detection distance, read from its block");
    EnemyDetectionDataBlock.Default = null;
    Plugin.ThrowOnError = false;
    Check(Noise.LandingRadius() == 0f && Plugin.Errors.Count == 1, "no default detection block: no landing noise and one error");
    Plugin.ThrowOnError = true; Plugin.Errors.Clear();
}

// ---- messages ------------------------------------------------------------------------------------------------

Check(Marshal.SizeOf<FrameStateMessage>() == 91, "state message size " + Marshal.SizeOf<FrameStateMessage>());
Check(Marshal.OffsetOf<FrameStateMessage>(nameof(FrameStateMessage.Spec)).ToInt32() == 55, "state spec offset");
Check(Marshal.OffsetOf<FrameStateMessage>(nameof(FrameStateMessage.Definition)).ToInt32() == 63, "state definition offset");
Check(Marshal.OffsetOf<FrameStateMessage>(nameof(FrameStateMessage.Pilot)).ToInt32() == 6, "state pilot offset");
Check(Marshal.OffsetOf<FrameStateMessage>(nameof(FrameStateMessage.Ejection)).ToInt32() == 54, "state ejection offset");
Check(Marshal.SizeOf<FrameRequestMessage>() == 21, "request size");
Check(Marshal.SizeOf<FrameRejectMessage>() == 14, "reject size");
Check(Marshal.SizeOf<FrameFireMessage>() == 37, "fire size");
Check(Marshal.SizeOf<FrameRocketMessage>() == 26, "rocket size");
Check(Marshal.SizeOf<FrameStateMessage>() <= 192, "state fits ForgeOrdnance per-call data");
{
    var host = new FrameRecord(9, 4, assault) { State = FrameState.Piloted, Pilot = 77, Armor = 55, Shield = 7, ShieldUp = true, Magazine = 12, Reserve = 40, X = 1, Y = 2, Z = 3, Yaw = 45, Sequence = 5, BoostEndsAt = 13f, Sieged = true, DefinitionKey = 0xC0FFEEu };
    var bytes = FrameMessages.ToBytes(FrameStateMessage.From(host, 10f, FrameEjection.None));
    Check(FrameMessages.TryRead(bytes, out FrameStateMessage keyed) && keyed.Spec == FrameSpecKey.Of(assault) && keyed.Spec == host.SpecKey, "state names the model by its content key");
    Check(FrameMessages.TryRead(bytes, out FrameStateMessage read), "state round trip reads");
    var mirror = new FrameRecord(9, 4, assault);
    Check(read.ApplyTo(mirror, 100f), "mirror takes the state");
    Check(mirror.State == FrameState.Piloted && mirror.Pilot == 77 && mirror.Armor == 55 && mirror.Shield == 7 && mirror.ShieldUp && mirror.Sieged && Math.Abs(mirror.BoostEndsAt - 103f) < 1e-3f, "fields survive, timers move to the local clock");
    Check(mirror.DefinitionKey == 0xC0FFEEu, "the state carries the definition key, so every peer finds the definition that names the suit");
    Check(!read.ApplyTo(mirror, 101f), "a repeated sequence is ignored");
    Check(!FrameMessages.TryRead(new byte[3], out FrameStateMessage _), "short data is refused");
}

// ---- checkpoint persistence and replication -----------------------------------------------------------------
{
    var tank = FrameSpecParser.Parse(Json(Minimal.Replace("\"Armor\":100", "\"Armor\":100,\"Shield\":{\"Capacity\":50,\"RegenDelay\":6,\"RegenRate\":10}")));
    var specs = new Dictionary<ulong, FrameSpec> { [FrameSpecKey.Of(tank)] = tank, [FrameSpecKey.Of(assault)] = assault };
    var saved = new FrameRecord(7, 4, tank) { State = FrameState.Piloted, Pilot = 9, Sieged = true, BoostEndsAt = 15, Sequence = 5, X = 8, Armor = 60, Magazine = 12 };
    var records = new List<FrameRecord> { saved, new(7, 5, assault) { State = FrameState.Destroyed } };
    var snapshot = new FrameCheckpoint();
    snapshot.Capture(records, 10);
    Check(snapshot.Count == 1 && saved.HasPilot && saved.Sieged, "checkpoint excludes wrecks without mutating the live suit");
    records.Clear();
    records.Add(new(7, 6, assault));
    var rebuilt = snapshot.Restore(specs, 100, 20);
    Check(rebuilt.Count == 1 && rebuilt[0].ThrowId == 4 && !ReferenceEquals(rebuilt[0], saved), "checkpoint rebuilds a saved suit even if destroyed/removed and excludes later suits");
    Check(rebuilt[0].State == FrameState.Idle && rebuilt[0].Pilot == 0 && !rebuilt[0].Sieged && rebuilt[0].BoostEndsAt == 0, "recovery removes pilot and active modes");
    Check(rebuilt[0].ShieldUp && rebuilt[0].Shield == 50 && rebuilt[0].Armor == 60 && rebuilt[0].Magazine == 12 && rebuilt[0].X == 8, "recovery retains shield, health, ammunition and placement");
    Check(rebuilt[0].Sequence == 21, "restored state is newer than the supplied live population");
    var peer = new FrameCheckpoint();
    foreach (var m in snapshot.Messages())
    {
        Check(FrameMessages.TryRead(FrameMessages.ToBytes(m), out FrameCheckpointMessage copy), "checkpoint wire round trip");
        peer.Receive(copy);
    }
    Check(peer.Restore(specs, 200, 30).Single().ShieldUp, "peer takes over a complete checkpoint after migration");
    peer.Receive(new() { Kind = FrameCheckpointMessage.Begin, Revision = 1, Count = 2 });
    peer.Receive(snapshot.Messages().ElementAt(1));
    peer.Receive(new() { Kind = FrameCheckpointMessage.Commit, Revision = 1 });
    Check(peer.Count == 1 && !peer.IsComplete, "partial checkpoint never replaces or authorizes the last complete checkpoint");
    peer.Receive(new() { Kind = FrameCheckpointMessage.Begin, Revision = 1, Count = 2 });
    peer.Receive(snapshot.Messages().ElementAt(1));
    peer.Receive(snapshot.Messages().ElementAt(1));
    peer.Receive(new() { Kind = FrameCheckpointMessage.Commit, Revision = 1 });
    Check(peer.Count == 1 && !peer.IsComplete, "duplicate entries cannot falsely complete a checkpoint");
    peer.Receive(new() { Kind = FrameCheckpointMessage.Begin, Revision = 1, Count = 0 });
    peer.CancelIncoming();
    peer.Receive(new() { Kind = FrameCheckpointMessage.Commit, Revision = 1 });
    Check(peer.Count == 1 && !peer.IsComplete, "authority change cannot commit the cancelled incoming transaction");
    var empty = new FrameCheckpoint();
    empty.Capture(Array.Empty<FrameRecord>(), 0);
    foreach (var m in empty.Messages()) peer.Receive(m);
    Check(peer.Count == 0, "empty checkpoint replaces the previous population");
    var missingRejected = false;
    try { snapshot.Restore(new Dictionary<ulong, FrameSpec>(), 0, 0); }
    catch (InvalidOperationException) { missingRejected = true; }
    Check(missingRejected, "missing checkpoint model is an explicit failure");
    Check(Marshal.SizeOf<FrameCheckpointMessage>() == 112 && Marshal.OffsetOf<FrameCheckpointMessage>(nameof(FrameCheckpointMessage.State)).ToInt32() == 21, "checkpoint message layout");
    Check(Marshal.SizeOf<FrameHitMessage>() == 25 && Marshal.OffsetOf<FrameHitMessage>(nameof(FrameHitMessage.Damage)).ToInt32() == 20, "hit message layout");
    NativeChecks.Run(Check, assault, tank);
    R2Checks.Run(Check, assault, tank);
}

{
    // A heavy object hangs behind the shell at chest height, clear of it by the gap, whatever its depth.
    var thin = CarrySocket.Centre(-0.4f, 1.5f, 0.1f);
    var deep = CarrySocket.Centre(-0.4f, 1.5f, 0.5f);
    Check(thin.X == 0f && thin.Y == 1.5f && Math.Abs(thin.Z - (-0.4f - CarrySocket.Gap - 0.05f)) < 1e-6f, "a carried object is centred between the shoulders at chest height");
    Check(Math.Abs((deep.Z + 0.25f) - (-0.4f - CarrySocket.Gap)) < 1e-6f && deep.Z < thin.Z, "a deeper object's near face still sits the gap behind the shell");
}

HudChecks.Run(Check, assault);
RocketChecks.Run(Check);
SoundChecks.Run(Check);
EffectsChecks.Run(Check);
CockpitChecks.Run(Check);

Console.WriteLine("Frames core: " + checks + " checks passed.");

// ---- model names and nodes -----------------------------------------------------------------------------------

{
    Check(FrameModelName.TrySplit("Pack:item", out var pack, out var item) && pack == "Pack" && item == "item", "Pack:item splits");
    Check(!FrameModelName.TrySplit("item", out _, out _) && !FrameModelName.TrySplit(":item", out _, out _) && !FrameModelName.TrySplit("Pack:", out _, out _) && !FrameModelName.TrySplit("a:b:c", out _, out _) && !FrameModelName.TrySplit(null, out _, out _),
        "a name without exactly one pack and one item is refused");
    Check(FrameModelName.MarkIcon("Other:walker") == "Other:mark-walker", "the cockpit mark is mark-<item> in the icon pack of the model\u0027s pack");
    var spec = FrameSpecParser.Parse(Json(Minimal));
    var all = FrameBodyRig.PoseLinks.Select(l => l.Model).ToHashSet();
    Check(FrameBodyRig.PoseLinks.Count() == 16 && FrameBodyRig.MissingNodes(spec, all.Contains).Count() == 0, "a model with the 16 rig bones lacks nothing");
    all.Remove("chest");
    var missing = FrameBodyRig.MissingNodes(spec, all.Contains).ToList();
    Check(missing.Count == 1 && missing[0] == ("Model", "chest"), "a missing rig bone is named with the definition field");
}
