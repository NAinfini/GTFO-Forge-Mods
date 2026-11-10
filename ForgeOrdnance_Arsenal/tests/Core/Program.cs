using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ForgeOrdnance;
using ForgeOrdnanceArsenal;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    checks++;
}

JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

T Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T error) { checks++; return error; }
    throw new Exception("FAIL: " + name + " did not throw " + typeof(T).Name);
}

// ---- the shipped definitions --------------------------------------------------------------------------------

// The package folder, from this source file's own location (the build output lives under artifacts/).
var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile.Path())!, "..", ".."));
var registry = new PayloadRegistry();
var categories = new CategoryRegistry(id => () => id);
registry.Register(StrikeSpec.TypeName, new StrikeParser());
registry.Register(HeavyWeaponSpec.TypeName, new HeavyParser());
registry.Register(DeployableSpec.TypeName, new DeployableParser());
registry.Register("Item", new ItemParser());
registry.Register("Gear", new GearParser());
var file = Path.Combine(root, "ForgeOrdnance", "arsenal.forge");
var parsed = DefinitionParser.ParseFile("arsenal.forge", File.ReadAllText(file), registry, categories);
Check(parsed.Errors.Count == 0, "shipped definitions load without errors: " + string.Join(" | ", parsed.Errors));

var kinds = parsed.Definitions.Where(d => d.Parameters is StrikeSpec).Select(d => ((StrikeSpec)d.Parameters).Kind).ToHashSet();
Check(kinds.Count == 10 && Enum.GetValues<StrikeKind>().All(kinds.Contains), "every strike kind has a definition");

// Codes: none equal or a prefix of another, here, in ForgeOrdnance's test definitions (test profile
// ForgeOrdnance/test-callins.json), in Frames' test definitions (test-frames.forge) and in Frames' shipped definitions.
var codes = parsed.Definitions.Select(d => string.Join(",", d.Code)).ToList();
codes.AddRange(new[]
{
    "Down,Down,Up,Right", "Down,Down,Up,Left", "Down,Down,Left,Left", "Down,Down,Right,Up", "Down,Right,Down,Up", "Down,Right,Down,Down", "Down,Right,Down,Left",
    "Left,Left,Down,Up,Right", "Left,Left,Down,Up,Left", "Left,Left,Down,Up,Down", "Left,Left,Down,Up,Up",
});
var frames = Path.Combine(root, "..", "ForgeOrdnance_Frames", "Definitions", "frames.forge");
if (File.Exists(frames))
    foreach (var entry in JsonDocument.Parse(File.ReadAllText(frames)).RootElement.EnumerateArray())
        codes.Add(string.Join(",", entry.GetProperty("Code").EnumerateArray().Select(c => c.GetString())));
else Console.WriteLine("Frames definitions not found beside this package; their codes were not checked.");
for (var i = 0; i < codes.Count; i++)
    for (var j = 0; j < codes.Count; j++)
        if (i != j) Check(!(codes[j] + ",").StartsWith(codes[i] + ",", StringComparison.Ordinal), "code " + codes[i] + " is not a prefix of " + codes[j]);

// ---- wire ---------------------------------------------------------------------------------------------------

{
    var shell = new ShellRecord(ShellLook.Bomblet, StrikeKind.Cluster, 1.5f, -2f, 3.25f, 12f, 0.9f, 3.5f);
    var bytes = Wire.Shell(shell);
    Check(bytes.Length == Wire.ShellBytes && bytes.Length <= 192, "shell record size");
    Check(Wire.TryShell(bytes, out var back) && back.Look == shell.Look && back.Kind == shell.Kind && back.X == 1.5f && back.Top == 12f && back.Blast == 3.5f, "shell round trip");
    Check(Convert.ToHexString(bytes) == "02040000C03F000000C000005040000040416666663F00006040", "shell wire bytes unchanged");
    Check(Convert.ToHexString(Wire.Zone(new ZoneRecord(StrikeKind.Gas, 1, 2, 3, 7, 12))) ==
        "060000803F00000040000040400000E040000040410000803F", "zone wire bytes carry the slow share");
    Check(Convert.ToHexString(Wire.Beam(new BeamRecord(new Guid("00112233-4455-6677-8899-aabbccddeeff"), 1, 2, 3, 4, 5, 6, true))) ==
        "010000803F0000004000004040000080400000A0400000C04033221100554477668899AABBCCDDEEFF", "beam wire bytes unchanged");
    bytes[0] = 99;
    Check(!Wire.TryShell(bytes, out _), "unknown look rejected");
    Check(!Wire.TryShell(new byte[3], out _), "short record rejected");
    var nan = Wire.Shell(new ShellRecord(ShellLook.Shell, StrikeKind.Area, float.NaN, 0, 0, 0, 1, 1));
    Check(!Wire.TryShell(nan, out _), "non-finite rejected");

    // The ground drawn is the ground hurt, a malformed record is refused, and a strip covers only its strip.
    var gasSpec = StrikeSpec.Parse(Json("{\"Kind\":\"Gas\"}"));
    Check(StrikeFootprint.Of(gasSpec, 0f, 2f)!.Value.Radius == gasSpec.ZoneRadius, "a gas call's footprint is its cloud's radius");
    var creepSpec = StrikeSpec.Parse(Json("{\"Kind\":\"Creeping\"}"));
    var strip = new DangerRecord(StrikeKind.Creeping, 0, 0, 0, 0, 1, StrikeFootprint.Of(creepSpec, 0f, 2f)!.Value);
    Check(Wire.TryDanger(Wire.Danger(strip), out var stripBack) && stripBack.Footprint == strip.Footprint && stripBack.Kind == StrikeKind.Creeping, "danger record round trip");
    Check(stripBack.Covers(0, 0, creepSpec.Length) && !stripBack.Covers(0, 0, -creepSpec.BlastRadius - 1f) && !stripBack.Covers(creepSpec.Width, 0, 5f) && !stripBack.Covers(0, 9f, 5f), "the strip covers its own ground only");
    Check(!Wire.TryDanger(Wire.Danger(new DangerRecord(StrikeKind.Gas, 0, 0, 0, 0, 1, new StrikeFootprint(0f, 0f, 0f, 0f, 2f))), out _), "a footprint of nothing is refused");
    Check(StrikeFootprint.Of(StrikeSpec.Parse(Json("{\"Kind\":\"Hunter\"}")), 0f, 1f) == null, "the hunter has no fixed ground");
    Check(Wire.TryZone(Wire.Zone(new ZoneRecord(StrikeKind.Gas, 1, 2, 3, 7, 12)), out var zone) && zone.Kind == StrikeKind.Gas && zone.Radius == 7 && zone.Seconds == 12, "zone round trip");
    Check(!Wire.TryZone(Wire.Zone(new ZoneRecord(StrikeKind.Gas, 1, 2, 3, 0, 12)), out _), "zone without radius rejected");
    Check(Wire.TryZone(Wire.Zone(new ZoneRecord(StrikeKind.Stasis, 1, 2, 3, 8, 8, 0.35f)), out var slowZone) && slowZone.Slow == 0.35f, "stasis zone round trip keeps its slow share");
    Check(!Wire.TryZone(Wire.Zone(new ZoneRecord(StrikeKind.Stasis, 1, 2, 3, 8, 8, 0f)), out _), "a zone that stops enemies outright is rejected");
    var generation = Guid.NewGuid();
    Check(Wire.TryBeam(Wire.Beam(new BeamRecord(generation, 1, 2, 3, 4, 5, 6, true)), out var beam) && beam.HasTarget && beam.TZ == 6 && beam.Generation == generation, "beam round trip");
    Check(Wire.TryBeam(Wire.Beam(new BeamRecord(generation, 1, 2, 3, 0, 0, 0, false)), out var idle) && !idle.HasTarget, "beam without target");
    Check(!Wire.TryBeam(new byte[25], out _), "old beam layout rejected");
    Check(!Wire.TryBeam(Wire.Beam(new BeamRecord(Guid.Empty, 0, 1, 0, 0, 0, 0, false)), out _), "beam needs a reset generation");


    // Rack state, replayed to late joiners: the weapon rebuilt without the definition file, the name cut on a character boundary.
    HeavyModels.Register("Arsenal:grenade-launcher");
    HeavyModels.Register("Arsenal:heavy-mg");
    var launcher = HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:grenade-launcher\",\"Gear\":15,\"AmmoRatio\":0.5,\"Grenade\":{\"Damage\":200}}"));
    var rack = Wire.Rack(new RackRecord(launcher, 15, 1, 2, 3, 90, "Grenade Launcher"));
    // The record is the earlier one-byte-model layout with the model's 4-byte hash in front of the gear id.
    Check(Convert.ToHexString(rack) == Convert.ToHexString(BitConverter.GetBytes(launcher.ModelHash)) + "0F000000" + "0000003F00004843000080400000004200" + "00803F00000040000040400000B4420000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF000080BF00000000000000000000803F0000803F4772656E616465204C61756E63686572"
        && Wire.RackBytes == 108, "rack wire bytes");
    Check(Wire.TryRack(rack, out var racked) && racked.Spec.Model == "Arsenal:grenade-launcher" && racked.GearId == 15 && racked.Spec.GearId == 15
        && racked.Spec.AmmoRatio == 0.5f && racked.Spec.Grenade == new GrenadeSpec(200, 4, 32) && racked.Yaw == 90f && racked.Id == "Grenade Launcher", "rack round trip");
    var longName = Wire.Rack(new RackRecord(launcher, 15, 1, 2, 3, 0, new string('重', 100)));
    Check(longName.Length <= Wire.MaxDataBytes && Wire.TryRack(longName, out var cut) && cut.Id.Length == (Wire.MaxDataBytes - Wire.RackBytes) / 3, "long rack name cut to whole characters");
    var gun = HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38}"));
    Check(Wire.TryRack(Wire.Rack(new RackRecord(gun, 38, 0, 0, 0, 0, "")), out var bare) && bare.Spec.Model == "Arsenal:heavy-mg" && bare.Spec.Grenade == null && bare.Id == "", "rack without name or grenade");
    Check(!Wire.TryRack(Wire.Rack(new RackRecord(gun, 0, 0, 0, 0, 0, "x")), out _), "rack without gear rejected");
    Check(gun.Muzzle == HeavyMuzzle.Mg && HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"Muzzle\":\"amr\"}")).Muzzle == HeavyMuzzle.Amr
        && Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"Muzzle\":\"flame\"}")), "unknown muzzle").Field == "Muzzle", "muzzle flash from the payload: default mg, unknown refused");
    var unknown = (byte[])rack.Clone();
    unknown[0] ^= 0xFF;
    Check(!Wire.TryRack(unknown, out _) && Wire.UnknownRackModel(unknown, out var unknownHash) && unknownHash != launcher.ModelHash && !Wire.UnknownRackModel(rack, out _), "rack with an unknown model hash is dropped whole and says why");
    HeavyModels.Clear();
    Check(!Wire.TryRack(rack, out _) && Wire.UnknownRackModel(rack, out _), "a model this install never accepted is not shown");
    HeavyModels.Register("Arsenal:heavy-mg");
    Check(HeavyWeaponSpec.Restore("Arsenal:heavy-mg", 38, 1f, new GrenadeSpec(140, 0.1f, 32), ArchetypeTuning.None) == null, "grenade values outside their range rejected");
    Check(HeavyWeaponSpec.Restore("MachineGun", 38, 1f, null, ArchetypeTuning.None) == null, "a model that is not Pack:item rejected");

    // Factory settings: only the written fields are set, and they survive the rack record.
    var tuned = HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"Damage\":5.6,\"Magazine\":80,\"Pierce\":2,\"AmmoCost\":1.9}"));
    Check(tuned.Tuning.Damage == 5.6f && tuned.Tuning.Magazine == 80 && tuned.Tuning.Pierce == 2 && tuned.Tuning.AmmoCost == 1.9f
        && float.IsNaN(tuned.Tuning.ShotDelay) && float.IsNaN(tuned.Tuning.Chargeup) && !tuned.Tuning.IsEmpty && HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38}")).Tuning.IsEmpty, "factory settings parse, unset stay unset");
    Check(Wire.TryRack(Wire.Rack(new RackRecord(tuned, 38, 0, 0, 0, 0, "mg")), out var tunedBack) && tunedBack.Spec.Tuning.SameAs(tuned.Tuning), "factory settings round trip");
    var vanillaNumbers = new ArchetypeTuning(new[] { 4f, .08f, 0f, 0f, 50f, 2.5f, 1f, 1f, 0f, 3f, 40f, 80f });
    var inForce = tuned.Tuning.Over(vanillaNumbers);
    Check(inForce.Damage == 5.6f && inForce.ShotDelay == .08f && inForce.Magazine == 80 && inForce.AmmoCost == 1.9f && inForce.Valid()
        && inForce.Rounds(1f, 230f) == Math.Floor(230d / 1.9f) + 80, "factory settings over the game's numbers; the budget counts reserve plus magazine");
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"Magazine\":2.5}")), "fractional magazine").Field == "Magazine", "magazine is whole");
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"AmmoCost\":0}")), "free ammunition").Field == "AmmoCost", "round cost is positive");
    var placed = tuned.WithAmmo(0.5f, 20, 4f).Tuning;
    Check(placed.Magazine == 20 && placed.AmmoCost == 4f && placed.Damage == 5.6f && tuned.WithAmmo(null, null, null).Tuning.SameAs(tuned.Tuning) && placed.Over(vanillaNumbers).Rounds(0.5f, 230f) == Math.Floor(115d / 4f) + 20, "a placement sets magazine and round cost over the definition; the budget follows");
    Check(ArchetypeTuning.FromWire(new[] { float.NaN, 0f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN }) == null, "tuning outside its range rejected on the wire");
    // Damage falloff in metres: both or neither, the end beyond the start; unset takes the game block's own.
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"FalloffStart\":80}")), "falloff start alone").Field == "FalloffStart"
        && Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"FalloffEnd\":200}")), "falloff end alone").Field == "FalloffStart"
        && Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"FalloffStart\":80,\"FalloffEnd\":80}")), "falloff end not beyond start").Field == "FalloffEnd", "falloff needs both fields, end beyond start");

    Check(Marshal.SizeOf<GrenadeShot>() == 64 && (int)Marshal.OffsetOf<GrenadeShot>("VX") == 40 && (int)Marshal.OffsetOf<GrenadeShot>("Radius") == 56, "grenade shot layout");
    Check(Marshal.SizeOf<GrenadeBurst>() == 41 && (int)Marshal.OffsetOf<GrenadeBurst>("Sequence") == 24
        && (int)Marshal.OffsetOf<GrenadeBurst>("X") == 28 && (int)Marshal.OffsetOf<GrenadeBurst>("Rocket") == 40, "grenade settlement layout");
}

Console.WriteLine("ForgeOrdnance_Arsenal Core: " + checks + " checks passed.");

internal sealed class StrikeParser : IOrdnancePayloadParser
{
    public object Parse(JsonElement payload) => StrikeSpec.Parse(payload);
}

internal sealed class DeployableParser : IOrdnancePayloadParser
{
    public object Parse(JsonElement payload) => DeployableSpec.Parse(payload);
}

internal sealed class HeavyParser : IOrdnancePayloadParser
{
    public object Parse(JsonElement payload) => HeavyWeaponSpec.Parse(payload);
}

/// <summary>The framework's <c>Item</c> parser (its Core list rules) with the game's item persistentIDs this package uses.</summary>
internal sealed class ItemParser : IOrdnancePayloadParser
{
    // ItemDataBlock: AmmoPackWeapon 101, MediPack 102, AmmoPackTool 127, DisinfectionPack 132, CONSUMABLE_GlueGrenade 115,
    // CONSUMABLE_Tripmine_Explosive 139, CONSUMABLE_FogRepeller 117, CONSUMABLE_GlowStick 114, CONSUMABLE_Syringe_Health 140,
    // CONSUMABLE_Syringe_MeleeBuff 142, CONSUMABLE_Tripmine_Glue 144, CONSUMABLE_LockMelter 116.
    private static readonly HashSet<uint> Known = new() { 101, 102, 127, 132, 115, 139, 117, 114, 140, 142, 144, 116 };

    public object Parse(JsonElement payload) => ItemList.Parse(payload, (id, name) => id.HasValue && Known.Contains(id.Value) ? id : null);
}

internal sealed record TurretSpec(uint Gear, float AmmoRatio, float Lifetime, bool Pickable, string Look);

/// <summary>The framework's <c>Gear</c> fields; the gear must be one of the game's three player sentries.</summary>
internal sealed class GearParser : IOrdnancePayloadParser
{
    // PlayerOfflineGearDataBlock: SentryGun_Semi_Sniper 23, SentryGun_Burst 24, SentryGun_ShotgunSemi 25.
    private static readonly HashSet<uint> Sentries = new() { 23, 24, 25 };

    public object Parse(JsonElement payload)
    {
        PayloadReader.OnlyKnown(payload, "Gear", "AmmoRatio", "Lifetime", "Pickable", "Look", "DamageScale", "FireRateScale", "AmmoScale", "RangeScale", "TurnScale", "AcquireScale");
        var (id, _) = ForgeCore.StrictJson.Reference(payload, "", "Gear");
        if (!id.HasValue || !Sentries.Contains(id.Value)) throw new PayloadParameterException("Gear", "must be a player sentry gear block (23, 24 or 25).");
        var look = PayloadReader.Look(payload, lookId => lookId is "Arsenal:turret-burst" or "Arsenal:turret-sniper" or "Arsenal:turret-shotgun");
        return new TurretSpec(id.Value, (float)ForgeCore.StrictJson.Number(payload, "", "AmmoRatio", 1, 0, 1), (float)ForgeCore.StrictJson.Number(payload, "", "Lifetime", 0, 0, 3600), ForgeCore.StrictJson.Bool(payload, "", "Pickable", true), look);
    }
}

internal static class SourceFile
{
    internal static string Path([CallerFilePath] string path = "") => path;
}
