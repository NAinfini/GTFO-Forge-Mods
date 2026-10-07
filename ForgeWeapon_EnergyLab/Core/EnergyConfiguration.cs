using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ForgeEnergyLab.Api;

namespace ForgeWeaponEnergyLabExperimental;

internal static class EnergyConfiguration
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        ["magazine"] = "Whole rounds in the magazine (1..10000).",
        ["ammoPerRound"] = "Native reserve units per round (>0).",
        ["range"] = "Chain Arc first-hit distance or flame length in metres (>0); the other weapons have no range limit.",
        ["interval"] = "Seconds between discharges (0.01..60).",
        ["damage"] = "Base direct, explosion or field-pulse damage; minimum-charge damage for orbs.",
        ["ammoCost"] = "Whole magazine rounds per discharge; must be 1 for every weapon, at every charge power.",
        ["stagger"] = "Native stagger multiplier for direct/selected hits (0..100).",
        ["precision"] = "Native precision multiplier for direct/selected hits (0..100).",
        ["reloadTime"] = "Native reload duration in seconds (0.01..120).",
        ["equipTime"] = "Native equip transition duration in seconds (0.01..120).",
        ["aimTime"] = "Native aim transition duration in seconds (0.01..120).",
        ["targets"] = "Chain recipients including the first hit (1..4 packet budget).",
        ["jumpRange"] = "Maximum chain hop distance in metres (>0).",
        ["damageRetention"] = "Damage fraction retained after each chain hop (0..1).",
        ["arcLifetime"] = "Arc hit-feedback duration in seconds (>0).",
        ["hopDelay"] = "Visual hop delay in seconds (0..10); damage is immediate.",
        ["minimumCharge"] = "Minimum hold seconds to fire (>=0.1).",
        ["fullCharge"] = "Hold seconds for full power (>minimumCharge); holding longer keeps full power until release.",
        ["botChargePower"] = "Power of a native bot shot (0..1).",
        ["speed"] = "Projectile speed in metres/second (>0).",
        ["fullSpeed"] = "Full-charge projectile speed in metres/second (>0).",
        ["bodyRadius"] = "Projectile collision radius in metres (>0).",
        ["fullBodyRadius"] = "Full-charge collision radius in metres (>0).",
        ["travelDistance"] = "Minimum-charge flight distance in metres (>0).",
        ["fullTravelDistance"] = "Full-charge flight distance in metres (>0).",
        ["fullDamage"] = "Full-charge damage (>=damage).",
        ["activeProjectiles"] = "Per-definition active limit within shared slots (discs 1..3, others 1..4).",
        ["blastRadius"] = "Explosion radius in metres with native linear falloff (>0).",
        ["fullBlastRadius"] = "Full-charge explosion radius in metres (>0).",
        ["explosionLifetime"] = "Explosion slot/feedback duration in seconds (>0).",
        ["noiseRadius"] = "Native explosion noise radius in metres (0..1000).",
        ["explosionForce"] = "Native explosion receiver force (0..1000).",
        ["arcRadius"] = "Minimum-charge pulse radius in metres (>0).",
        ["fullArcRadius"] = "Full-charge pulse radius in metres (>0).",
        ["pulseInterval"] = "Seconds between damage pulses (0.01..60).",
        ["pulseDamageFraction"] = "Fraction of orb direct damage applied by a pulse (0..1).",
        ["pulseStagger"] = "Native pulse stagger multiplier (0..100).",
        ["pulseTargets"] = "Recipients per flight pulse (1..3 effect budget).",
        ["fadeLifetime"] = "Fade/slot duration after flight in seconds (>0).",
        ["bounces"] = "Allowed wall bounces (0..128).",
        ["lifetime"] = "Disc flight or gravity field duration in seconds (>0).",
        ["startRadius"] = "Flame cone radius at the muzzle in metres (>0).",
        ["radiusPerMetre"] = "Flame cone widening per metre (0..10).",
        ["flightLifetime"] = "Gravity flight seconds before field activation (>0).",
        ["gravity"] = "Downward projectile acceleration in metres/second squared (0..1000).",
        ["radius"] = "Gravity outer damage/pull radius in metres (>coreRadius).",
        ["coreRadius"] = "Full-damage inner radius in metres (>0, <radius).",
        ["collapseDuration"] = "Final field seconds without damage/pull (>0, <lifetime).",
        ["growthDuration"] = "Visible field growth duration in seconds (>0).",
        ["edgeDamageFraction"] = "Damage fraction at the outer field edge (0..1).",
        ["pullSpeed"] = "Attraction speed in metres/second (0..1000).",
        ["queryInterval"] = "Maximum target re-query interval in seconds (0.01..60)."
    };

    internal static JsonNode TuningJson(EnergyTuning tuning)
        => JsonSerializer.SerializeToNode(tuning, tuning.GetType(), JsonOptions)!;

    internal const string OverridesFile = "tuning-overrides.json", DefaultsFile = "tuning-defaults.json";

    // The defaults file is a reference rewritten on every launch, so it always shows this build's values.
    // The overrides file holds only the values a player changed; every other value follows the current defaults.
    internal static WeaponDefinition[] Load(string directory, WeaponDefinition[] defaults)
    {
        Directory.CreateDirectory(directory);
        var reference = new JsonObject();
        foreach (var definition in defaults) reference.Add(definition.Id, TuningJson(definition.Tuning));
        File.WriteAllText(Path.Combine(directory, DefaultsFile), new JsonObject
        {
            ["_note"] = "Reference only, rewritten on every launch. Copy the values you want to change into " + OverridesFile + ".",
            ["_descriptions"] = JsonSerializer.SerializeToNode(Descriptions, JsonOptions), ["weapons"] = reference
        }.ToJsonString(JsonOptions), new System.Text.UTF8Encoding(false));
        var path = Path.Combine(directory, OverridesFile);
        if (!File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(new JsonObject { ["weapons"] = new JsonObject() }.ToJsonString(JsonOptions));
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = Object(doc.RootElement, "$", new[] { "weapons" }, required: new[] { "weapons" });
            var weapons = Object(root.GetProperty("weapons"), "$.weapons", defaults.Select(d => d.Id).ToArray(), required: Array.Empty<string>());
            var result = new WeaponDefinition[defaults.Length];
            for (var index = 0; index < defaults.Length; index++)
            {
                var definition = defaults[index];
                result[index] = definition;
                if (!weapons.TryGetProperty(definition.Id, out var item)) continue;
                var field = "$.weapons." + definition.Id;
                var type = definition.Tuning.GetType();
                var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                Object(item, field, properties.Select(p => JsonOptions.PropertyNamingPolicy!.ConvertName(p.Name)).ToArray(), required: Array.Empty<string>());
                var merged = TuningJson(definition.Tuning).AsObject();
                foreach (var property in properties)
                {
                    var key = JsonOptions.PropertyNamingPolicy!.ConvertName(property.Name);
                    if (!item.TryGetProperty(key, out var value)) continue;
                    if (value.ValueKind != JsonValueKind.Number ||
                        (property.PropertyType == typeof(int) ? !value.TryGetInt32(out _) :
                         property.PropertyType == typeof(float) ? !value.TryGetSingle(out var f) || !float.IsFinite(f) :
                         !value.TryGetDouble(out var d) || !double.IsFinite(d)))
                        Fail(field + "." + key, property.PropertyType == typeof(int) ? "expected a 32-bit whole number" : "expected a finite number");
                    merged[key] = JsonNode.Parse(value.GetRawText());
                }
                var tuning = (EnergyTuning)merged.Deserialize(type, JsonOptions)!;
                ValidateTuning(definition.Mode, tuning, field);
                result[index] = definition with { Tuning = tuning };
            }
            return result;
        }
        catch (JsonException error) { throw new InvalidDataException($"{error.Path ?? "$"}: invalid JSON or numeric type at line {error.LineNumber + 1}, byte {error.BytePositionInLine + 1}", error); }
    }

    private static JsonElement Object(JsonElement value, string path, string[] allowed, string[]? required = null)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail(path, "expected an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) Fail(path + "." + property.Name, "unknown field");
            if (!seen.Add(property.Name)) Fail(path + "." + property.Name, "duplicate field");
        }
        foreach (var field in required ?? allowed) if (!seen.Contains(field)) Fail(path + "." + field, "missing field");
        return value;
    }

    internal static void ValidateDefinition(WeaponDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (definition.Id == null || definition.Id.Length > 128 || !Regex.IsMatch(definition.Id, "^[a-z0-9][a-z0-9_-]*(\\.[a-z0-9][a-z0-9_-]*){2,}$")) Fail("id", "expected a lowercase namespaced id such as author.mod.weapon (at most 128 characters)");
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128) Fail(definition.Id + ".name", "expected a nonempty name of at most 128 characters");
        if (definition.Description is { } text && (string.IsNullOrWhiteSpace(text) || text.Length > 512)) Fail(definition.Id + ".description", "expected null or a nonempty text of at most 512 characters");
        if (definition.Slot is not (WeaponSlot.Main or WeaponSlot.Special)) Fail(definition.Id + ".slot", "expected Main or Special");
        if (definition.Model == null || string.IsNullOrWhiteSpace(definition.Model.Key) || definition.Model.Key.Length > 128) Fail(definition.Id + ".model.key", "expected a prefab key");
        if (definition.Model!.BundlePath is { } path)
        {
            if (!Path.IsPathFullyQualified(path)) Fail(definition.Id + ".model.bundlePath", "expected an absolute bundle path");
            if (definition.Model.IconPath == null) Fail(definition.Id + ".model.iconPath", "a provider bundle needs its own PNG icon");
        }
        else EnergyGears.BuiltInModel(definition.Model.Key);
        if (definition.Model.IconPath is { } icon && !Path.IsPathFullyQualified(icon)) Fail(definition.Id + ".model.iconPath", "expected an absolute PNG path");
        ValidateTuning(definition.Mode, definition.Tuning, definition.Id + ".tuning");
    }

    internal static void ValidateTuning(EnergyMode mode, EnergyTuning t, string path)
    {
        var valid = mode switch
        {
            EnergyMode.Beam => t is BeamTuning, EnergyMode.ArcChain => t is ArcChainTuning,
            EnergyMode.PlasmaBlast => t is PlasmaBlastTuning, EnergyMode.PlasmaArc => t is PlasmaArcTuning,
            EnergyMode.Blast => t is BlastTuning, EnergyMode.Disc => t is DiscTuning,
            EnergyMode.Flame => t is FlameTuning, EnergyMode.BlackHole => t is BlackHoleTuning, _ => false
        };
        if (!valid) Fail(path, "tuning record type must match a firing behaviour");
        void Check(string name, double number, double min, double max) { if (!double.IsFinite(number) || number + 1e-7 < min || number - 1e-7 > max) Fail(path + "." + JsonOptions.PropertyNamingPolicy!.ConvertName(name), FormattableString.Invariant($"expected {min}..{max}")); }
        Check(nameof(t.Magazine), t.Magazine, 1, 10000); Check(nameof(t.AmmoCost), t.AmmoCost, 1, t.Magazine);
        if (t.AmmoCost != 1) Fail(path + ".ammoCost", "native fire spends exactly one magazine round at every charge power");
        Check(nameof(t.AmmoPerRound), t.AmmoPerRound, 0.001, 1000000);
        Check(nameof(t.Interval), t.Interval, 0.01, 60); Check(nameof(t.Damage), t.Damage, 0, 1000000);
        Check(nameof(t.Stagger), t.Stagger, 0, 100); Check(nameof(t.Precision), t.Precision, 0, 100);
        Check(nameof(t.ReloadTime), t.ReloadTime, 0.01, 120); Check(nameof(t.EquipTime), t.EquipTime, 0.01, 120); Check(nameof(t.AimTime), t.AimTime, 0.01, 120);
        switch (t)
        {
            case ArcChainTuning arc:
                Check(nameof(arc.Range), arc.Range, 0.01, EnergyLimits.Reach);
                Check(nameof(arc.Targets), arc.Targets, 1, 4); Check(nameof(arc.JumpRange), arc.JumpRange, 0.01, 1000);
                Check(nameof(arc.DamageRetention), arc.DamageRetention, 0, 1); Check(nameof(arc.ArcLifetime), arc.ArcLifetime, 0.01, 120); Check(nameof(arc.HopDelay), arc.HopDelay, 0, 10);
                break;
            case PlasmaTuning orb:
                Check(nameof(orb.MinimumCharge), orb.MinimumCharge, 0.1, 120); Check(nameof(orb.FullCharge), orb.FullCharge, orb.MinimumCharge + 0.01, 120);
                Check(nameof(orb.BotChargePower), orb.BotChargePower, 0, 1);
                Check(nameof(orb.Speed), orb.Speed, 0.01, 1000); Check(nameof(orb.FullSpeed), orb.FullSpeed, 0.01, 1000);
                Check(nameof(orb.BodyRadius), orb.BodyRadius, 0.01, 100); Check(nameof(orb.FullBodyRadius), orb.FullBodyRadius, 0.01, 100);
                Check(nameof(orb.TravelDistance), orb.TravelDistance, 0.01, 1000); Check(nameof(orb.FullTravelDistance), orb.FullTravelDistance, 0.01, 1000); Check(nameof(orb.FullDamage), orb.FullDamage, orb.Damage, 1000000);
                Check(nameof(orb.ActiveProjectiles), orb.ActiveProjectiles, 1, 4);
                if (orb is PlasmaBlastTuning blastOrb)
                {
                    Check(nameof(blastOrb.BlastRadius), blastOrb.BlastRadius, 0.01, 100); Check(nameof(blastOrb.FullBlastRadius), blastOrb.FullBlastRadius, 0.01, 100);
                    Check(nameof(blastOrb.ExplosionLifetime), blastOrb.ExplosionLifetime, 0.01, 120); Check(nameof(blastOrb.NoiseRadius), blastOrb.NoiseRadius, 0, 1000); Check(nameof(blastOrb.ExplosionForce), blastOrb.ExplosionForce, 0, 1000);
                }
                if (orb is PlasmaArcTuning arcOrb)
                {
                    Check(nameof(arcOrb.ArcRadius), arcOrb.ArcRadius, 0.01, 100); Check(nameof(arcOrb.FullArcRadius), arcOrb.FullArcRadius, 0.01, 100);
                    Check(nameof(arcOrb.PulseInterval), arcOrb.PulseInterval, 0.01, 60); Check(nameof(arcOrb.PulseDamageFraction), arcOrb.PulseDamageFraction, 0, 1); Check(nameof(arcOrb.PulseStagger), arcOrb.PulseStagger, 0, 100);
                    Check(nameof(arcOrb.PulseTargets), arcOrb.PulseTargets, 1, 3); Check(nameof(arcOrb.FadeLifetime), arcOrb.FadeLifetime, 0.01, 120);
                }
                break;
            case BlastTuning blast:
                Check(nameof(blast.BlastRadius), blast.BlastRadius, 0.01, 100); Check(nameof(blast.ExplosionLifetime), blast.ExplosionLifetime, 0.01, 120);
                Check(nameof(blast.ActiveProjectiles), blast.ActiveProjectiles, 1, 4); Check(nameof(blast.NoiseRadius), blast.NoiseRadius, 0, 1000); Check(nameof(blast.ExplosionForce), blast.ExplosionForce, 0, 1000);
                break;
            case DiscTuning disc:
                Check(nameof(disc.Speed), disc.Speed, 0.01, 1000); Check(nameof(disc.BodyRadius), disc.BodyRadius, 0.01, 100); Check(nameof(disc.Bounces), disc.Bounces, 0, 128);
                Check(nameof(disc.Lifetime), disc.Lifetime, 0.01, 120); Check(nameof(disc.ActiveProjectiles), disc.ActiveProjectiles, 1, 3);
                break;
            case FlameTuning flame:
                Check(nameof(flame.Range), flame.Range, 0.01, EnergyLimits.Reach);
                Check(nameof(flame.StartRadius), flame.StartRadius, 0.01, 100); Check(nameof(flame.RadiusPerMetre), flame.RadiusPerMetre, 0, 10);
                break;
            case BlackHoleTuning hole:
                Check(nameof(hole.Speed), hole.Speed, 0.01, 1000); Check(nameof(hole.BodyRadius), hole.BodyRadius, 0.01, 100); Check(nameof(hole.FlightLifetime), hole.FlightLifetime, 0.01, 120); Check(nameof(hole.Gravity), hole.Gravity, 0, 1000);
                Check(nameof(hole.Radius), hole.Radius, 0.02, 100); Check(nameof(hole.CoreRadius), hole.CoreRadius, 0.01, hole.Radius - 0.01); Check(nameof(hole.Lifetime), hole.Lifetime, 0.02, 120);
                Check(nameof(hole.CollapseDuration), hole.CollapseDuration, 0.01, hole.Lifetime - 0.01); Check(nameof(hole.GrowthDuration), hole.GrowthDuration, 0.01, 120);
                Check(nameof(hole.PulseInterval), hole.PulseInterval, 0.01, 60); Check(nameof(hole.EdgeDamageFraction), hole.EdgeDamageFraction, 0, 1); Check(nameof(hole.PullSpeed), hole.PullSpeed, 0, 1000); Check(nameof(hole.QueryInterval), hole.QueryInterval, 0.01, 60);
                break;
        }
    }

    private static void Fail(string field, string reason) => throw new InvalidDataException(field + ": " + reason);
}
