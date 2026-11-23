using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeCore;
using ForgeEngine.Framework;
using Gear;
using Player;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>Writes the authored Forge block fields that have a real native member to write, at the game's own
/// generation points.
///
/// The values come from the one document family (`plugins/&lt;package&gt;/forge/weapons/*.forge`,
/// <see cref="WeaponFieldSet"/>); the block ids and paths are <see cref="WeaponDefinitionFields"/>. Every write
/// here is `set` of an absolute value, so applying the same document twice lands on the same numbers.
///
/// Which machine writes: the fields below are the ones the research marked `identical` — local presentation or
/// local state — so every peer writes the same document value and no replication channel is involved. The
/// fields the research marked `host-replicated` are not written here: a value with no replication channel would
/// diverge between the host and the clients, and none of them is in this applier's vocabulary.
///
/// What is deliberately absent: `FPSInertiaMultiplier` and `SentryGunInstance.InteractionName` are read-only in
/// the shipped interop (getter only), so the document cannot reach them; `tracerPool` and `vfxBulletHit` are
/// native resource references with no id-to-resource path in this build; and the fields with no writable member
/// at all are recipes, not block data.</summary>
internal sealed class WeaponFieldApplier
{
    private readonly WeaponFieldSet _set;
    private readonly WeaponLog _log;
    private readonly OnceReporter _once = new();
    private readonly Dictionary<WeaponFieldEntry, WeaponScannerReport> _scannerReports = new();
    private readonly List<WeaponScannerBinding> _scannerBindings = new();
    private WeaponScannerBinding? _lastScanner;
    private int _configuredScanners;
    private sealed record HackInteraction(Interact_Hack Instance, int InstanceId, float Before, float Written);
    private readonly Dictionary<IntPtr, HackInteraction> _hackInteractions = new();

    internal WeaponFieldApplier(WeaponFieldSet set, WeaponLog log)
    {
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // Build 20403457 reads the static scalars in Update (including inlined node refresh),
    // RefreshNodeEnemyList and the bot's UpdateStateScan. They are inputs to those calls,
    // not process-wide authored configuration. Capture before writing so a failed prefix
    // can still restore both fields in its Harmony finalizer.
    internal bool ScannerCallIdle => _configuredScanners == 0 && !WeaponScannerCall.InCall;

    internal WeaponScannerCall PrepareScannerCall(ItemEquippable? scanner)
    {
        if (ScannerCallIdle) return default;
        if (scanner == null) return new(null, null);
        var pointer = scanner.Pointer;
        var binding = _lastScanner;
        if (binding == null || binding.Pointer != pointer)
        {
            binding = null;
            for (int i = 0; i < _scannerBindings.Count; i++)
                if (_scannerBindings[i].Pointer == pointer) { binding = _scannerBindings[i]; break; }
            _lastScanner = binding;
        }
        return binding == null ? new(null, null)
            : new(binding.Duration, binding.Distance, binding.Report == null ? null : _log, binding);
    }

    /// <summary>One equipment instance finished its own `Setup`, which is the point its own members exist. A
    /// document that says nothing about this item leaves it exactly as the game built it.</summary>
    internal void ApplyItem(ItemEquippable? item, uint itemId)
    {
        if (item == null) return;
        WeaponFieldEntry entry = null!;
        bool found = itemId != 0 && TryEntry(item, itemId, out entry);
        WeaponFieldBlock? scannerBlock = null;
        if (item.TryCast<EnemyScanner>() is { } scanner)
        {
            if (found && entry.TryBlock(WeaponFieldSet.ScannerBlockId, out var resolved)) scannerBlock = resolved;
            BindScanner(scanner, itemId, scannerBlock == null ? null : entry, scannerBlock);
        }
        if (!found) return;
        bool wrote = false;
        if (item.TryCast<GlueGun>() is { } glue && entry.TryBlock("forge.vanilla.tool.glue-gun", out var glueValues))
            wrote |= ApplyGlue(glue, glueValues.Values);
        if (item.TryCast<EnemyScanner>() is { } scannerItem && scannerBlock != null)
            wrote |= ApplyScanner(scannerItem, scannerBlock, itemId);
        if (item.TryCast<MineDeployerFirstPerson>() is { } mineFirst && entry.TryBlock("forge.etc.MineDeployer", out var mineFirstValues))
            wrote |= ApplyMineInteraction(mineFirst, mineFirstValues, itemId);
        if (item.TryCast<MineDeployerInstance>() is { } mine)
            wrote |= ApplyMineInstance(mine, entry, itemId);
        if (item.TryCast<SentryGunFirstPerson>() is { } sentryFirst && entry.TryBlock("forge.etc.Sentry", out var sentryFirstValues))
            wrote |= ApplySentryInteraction(sentryFirst, sentryFirstValues, itemId);
        if (item.TryCast<SentryGunInstance>() is { } sentry && entry.TryBlock("forge.etc.Sentry", out var sentryValues))
            wrote |= ApplySentry(sentry, sentryValues, itemId);
        if (item.TryCast<LockMelterFirstPerson>() is { } melter && entry.TryBlock("forge.vanilla.tool.lock-melter", out var melterValues))
            wrote |= Set(melterValues.Values, "unlockDuration", value => melter.m_interactionDuration = value);
        if (wrote) Applied("item", itemId, entry.Source);
    }

    /// <summary>Resolves one scanner only at its setup/wield boundary. The hot native calls below compare the
    /// exact native instance pointer and read these numbers; they never parse the gear id or reopen document maps.</summary>
    private void BindScanner(EnemyScanner scanner, uint itemId, WeaponFieldEntry? entry, WeaponFieldBlock? block)
    {
        var duration = block?.Values.Single("s_tagDuration");
        var integer = block?.Values.Integer("s_maxCourseNodeDistance");
        int? distance = integer == null ? null : checked((int)integer.Value);
        WeaponScannerReport? report = null;
        if (entry != null && (duration != null || distance != null)
            && !_scannerReports.TryGetValue(entry, out report))
        {
            report = new(entry);
            _scannerReports.Add(entry, report);
        }
        var binding = new WeaponScannerBinding(scanner, itemId, duration, distance, report);
        for (int i = 0; i < _scannerBindings.Count; i++)
        {
            if (_scannerBindings[i].Pointer != binding.Pointer) continue;
            if (_scannerBindings[i].Configured) _configuredScanners--;
            _scannerBindings[i] = binding;
            if (binding.Configured) _configuredScanners++;
            _lastScanner = binding;
            return;
        }
        _scannerBindings.Add(binding);
        if (binding.Configured) _configuredScanners++;
        _lastScanner = binding;
    }

    /// <summary>Prepares a content-publication candidate from the exact scanner lives still held by the published
    /// applier. Resolution and allocation happen before the native commit; the candidate is discarded on failure.</summary>
    internal void PrepareScannerBindings(WeaponFieldApplier? published)
    {
        if (published == null || ReferenceEquals(published, this)) return;
        foreach (var previous in published._scannerBindings)
        {
            var scanner = previous.Scanner;
            if (scanner.Pointer != previous.Pointer) continue;
            WeaponFieldEntry entry = null!;
            bool found = previous.ItemId != 0 && TryEntry(scanner, previous.ItemId, out entry);
            WeaponFieldBlock? block = null;
            if (found && entry.TryBlock(WeaponFieldSet.ScannerBlockId, out var resolved)) block = resolved;
            BindScanner(scanner, previous.ItemId, block == null ? null : entry, block);
        }
    }

    /// <summary>Ends the binding with the exact native equipment instance. A later life reusing the same native
    /// address must pass setup/wield and create a fresh binding before scanner calls can see authored values.</summary>
    internal void ForgetScanner(ItemEquippable? item)
    {
        if (item?.TryCast<EnemyScanner>() is not { } scanner) return;
        var pointer = scanner.Pointer;
        if (_lastScanner?.Pointer == pointer) _lastScanner = null;
        for (int i = 0; i < _scannerBindings.Count; i++)
        {
            if (_scannerBindings[i].Pointer != pointer) continue;
            if (_scannerBindings[i].Configured) _configuredScanners--;
            _scannerBindings.RemoveAt(i);
            return;
        }
    }

    internal bool TryEntry(ItemEquippable item, uint itemId, out WeaponFieldEntry entry)
    {
        // An authored gear never falls back to the base tool shared by unrelated works.
        if (item.ItemDataBlock?.inventorySlot == InventorySlot.HackingTool)
            return _set.TryGetItem(itemId, out entry);
        return _set.TryGetEquipment(EquipmentNativeAdapter.GearBlockId(item.GearIDRange),
            item.GearIDRange != null || item.ItemDataBlock?.inventorySlot is not
                (InventorySlot.Consumable or InventorySlot.ConsumableHeavy or InventorySlot.InPocket), itemId, out entry);
    }

    /// <summary>A timed interaction was selected. Resolve the hacking tool actually used for this lock before
    /// reading its authored value; another tool of the same kind must not supply the duration.</summary>
    internal void ApplyInteraction(Interact_Timed? interaction, bool selected)
    {
        if (interaction == null) return;
        var hack = interaction.TryCast<Interact_Hack>();
        if (hack == null) return;
        if (_hackInteractions.Remove(hack.Pointer, out var previous)) RestoreInteraction(previous);
        if (!selected) return;
        var tool = hack.GetEquippedHackingTool();
        if (tool == null || !TryEntry(tool, tool.ItemDataBlock?.persistentID ?? 0, out var entry)
            || !entry.TryBlock("forge.vanilla.tool.hacking-tool", out var block)) return;
        var duration = block.Values.Single("hackDuration");
        if (duration == null) return;
        _hackInteractions.Add(hack.Pointer, new(hack, hack.GetInstanceID(), hack.InteractDuration, duration.Value));
        hack.InteractDuration = duration.Value;
        float actual = hack.InteractDuration;
        if (actual != duration.Value) throw new InvalidOperationException("weapon-hacking-duration-unconfirmed: " + entry.Source);
        if (_log.Logs(RuntimeLogLevel.Info))
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.FieldApplied, new RuntimeLogFacts
            {
                Values = new[] { RuntimeLogValue.Of("target", "hack-interaction"), RuntimeLogValue.Of("item", (long)entry.Id),
                    RuntimeLogValue.Quote("source", entry.Source), RuntimeLogValue.Of("duration", (double)actual) }
            });
    }

    internal void RestoreInteractions()
    {
        var pending = new List<HackInteraction>(_hackInteractions.Values);
        _hackInteractions.Clear();
        List<Exception>? errors = null;
        foreach (var state in pending)
            try { RestoreInteraction(state); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        if (errors != null) throw new AggregateException("Hacking interaction restoration failed.", errors);
    }

    private static void RestoreInteraction(HackInteraction state)
    {
        var interaction = state.Instance;
        if (interaction != null && interaction.GetInstanceID() == state.InstanceId && interaction.InteractDuration == state.Written)
            interaction.InteractDuration = state.Before;
    }

    /// <summary>A thrown fog repeller started. Its sphere is the instance's own, so the radius is written on this
    /// repeller only, before the sphere grows toward it.</summary>
    internal void ApplyFogRepeller(FogRepellerInstance? repeller)
    {
        if (repeller == null) return;
        var data = repeller.ItemDataBlock;
        var itemId = data == null ? 0u : data.persistentID;
        if (itemId == 0 || !_set.TryGetItem(itemId, out var entry)) return;
        if (!entry.TryBlock("forge.vanilla.tool.fog-repeller", out var block)) return;
        var radius = block.Values.Single("sphere");
        if (radius == null) return;
        var sphere = repeller.m_repellerSphere;
        if (sphere == null)
        {
            // This fog repeller carries no repeller sphere.
            Refused("no-sphere:" + Number(itemId), "unavailable", "weapon.forge.vanilla.tool.fog-repeller.sphere", itemId);
            return;
        }
        sphere.Range = radius.Value;
        Applied("fog-repeller", itemId, entry.Source);
    }

    private bool ApplyGlue(GlueGun gun, WeaponFieldBag values)
    {
        bool wrote = false;
        wrote |= Set(values, "m_timeToMaxpressure", value => gun.m_timeToMaxpressure = value);
        wrote |= Set(values, "m_timeToDepleatePressure", value => gun.m_timeToDepleatePressure = value);
        wrote |= Set(values, "m_pressureProgressToFire", value => gun.m_pressureProgressToFire = value);
        wrote |= Set(values, "m_fireDelayMin", value => gun.m_fireDelayMin = value);
        wrote |= Set(values, "m_fireDelayMax", value => gun.m_fireDelayMax = value);
        wrote |= Set(values, "m_forceSizeMin", value => gun.m_forceSizeMin = value);
        wrote |= Set(values, "m_forceSizeMax", value => gun.m_forceSizeMax = value);
        wrote |= Set(values, "m_spreadMin", value => gun.m_spreadMin = value);
        wrote |= Set(values, "m_spreadMax", value => gun.m_spreadMax = value);
        wrote |= SetInteger(values, "m_burstShotsMin", value => gun.m_burstShotsMin = value);
        wrote |= SetInteger(values, "m_burstShotsMax", value => gun.m_burstShotsMax = value);
        wrote |= Set(values, "m_burstShotDelay", value => gun.m_burstShotDelay = value);
        return wrote;
    }

    private bool ApplyScanner(EnemyScanner scanner, WeaponFieldBlock block, uint itemId)
    {
        bool wrote = false;
        var values = block.Values;
        wrote |= Set(values, "m_scanConeDotMin", value => scanner.m_scanConeDotMin = value);
        wrote |= Set(values, "m_maxScanWorldRadius", value => scanner.m_maxScanWorldRadius = value);
        wrote |= Set(values, "m_scanDelay", value => scanner.m_scanDelay = value);
        wrote |= Set(values, "m_pulseDuration", value => scanner.m_pulseDuration = value);
        wrote |= Set(values, "m_rechargeDuration", value => scanner.m_rechargeDuration = value);
        // Static scanner inputs are scoped at their native read entries, never at Setup/Wield.
        if (wrote) Applied("scanner", itemId);
        return wrote;
    }

    private bool ApplyMineInteraction(MineDeployerFirstPerson mine, WeaponFieldBlock block, uint itemId)
    {
        bool wrote = false;
        var values = block.Values;
        // `PlacementTime` is the one native member `m_deployPickupInteractionDuration`: placing and picking up share it.
        wrote |= Set(values, "PlacementTime", value => mine.m_deployPickupInteractionDuration = value);
        if (Set(values, "PlacementCooldown", value => mine.m_timeBetweenPlacements = value)) wrote = true;
        if (wrote) Applied("mine-interaction", itemId);
        return wrote;
    }

    private bool ApplyMineInstance(MineDeployerInstance mine, WeaponFieldEntry entry, uint itemId)
    {
        bool wrote = false;
        // Each payload block selects its own component; none requires the deployer's numeric fields.
        var values = entry.TryBlock("forge.etc.MineDeployer", out var block) ? block.Values : null;
        var explosive = mine.m_detonation?.TryCast<MineDeployerInstance_Detonate_Explosive>();
        if (values != null && explosive != null)
        {
            bool any = false;
            any |= Set(values, "Delay", value => explosive.m_delay = value);
            any |= Set(values, "Radius", value => explosive.m_radius = value);
            any |= Set(values, "DistanceMin", value => explosive.m_distanceMin = value);
            any |= Set(values, "DistanceMax", value => explosive.m_distanceMax = value);
            any |= Set(values, "DamageMin", value => explosive.m_damageMin = value);
            any |= Set(values, "DamageMax", value => explosive.m_damageMax = value);
            any |= Set(values, "Force", value => explosive.m_explosionForce = value);
            if (any) Applied("mine-explosive", itemId);
            wrote |= any;
        }
        else if (values != null && Values(values, "Delay", "Radius", "DistanceMin", "DistanceMax", "DamageMin", "DamageMax", "Force"))
            // This mine instance carries no explosive detonation component.
            Refused("no-explosive:" + Number(itemId), "unavailable", "weapon.forge.etc.MineDeployer.*", itemId);
        var glue = mine.m_detonation?.TryCast<MineDeployerInstance_Detonate_Glue>();
        var foam = entry.TryBlock("forge.etc.MineFoam", out var foamBlock) ? foamBlock.Values : values == null ? null : First(values, "FoamData");
        if (glue != null && foam != null)
        {
            bool any = false;
            any |= Set(foam, "BubbleDelay", value => glue.m_delay = value);
            any |= Set(foam, "BatchDelay", value => glue.m_explosionDelay = value);
            any |= SetInteger(foam, "BubbleCount", value => glue.m_projCount = value);
            var angle = foam.Single("BubbleAngle");
            var speedMin = foam.Single("BubbleSpeedMin");
            var speedMax = foam.Single("BubbleSpeedMax");
            if (angle != null) { glue.m_projAngMinMax = new Vector2(-angle.Value, angle.Value); any = true; }
            if (speedMin != null || speedMax != null)
            {
                var current = glue.m_projVelMinMax;
                glue.m_projVelMinMax = new Vector2(speedMin ?? current.x, speedMax ?? current.y);
                any = true;
            }
            if (any) Applied("mine-foam", itemId);
            wrote |= any;
        }
        else if (foam != null)
            // This mine instance carries no glue detonation component.
            Refused("no-glue:" + Number(itemId), "unavailable", "weapon.forge.etc.MineFoam.*", itemId);
        var beam = entry.TryBlock("forge.etc.MineBeam", out var beamBlock) ? beamBlock.Values : values == null ? null : First(values, "BeamData");
        var laser = mine.m_detection?.TryCast<MineDeployerInstance_Detect_Laser>();
        if (laser != null && beam != null)
        {
            bool any = false;
            any |= Set(beam, "Length", value => laser.m_maxLineDistance = value);
            var line = laser.m_lineRenderer;
            if (line != null)
                any |= Set(beam, "Width", value => { line.startWidth = value; line.endWidth = value; });
            else if (beam.Has("Width"))
                Refused("no-line-width:" + Number(itemId), "unavailable", "weapon.forge.etc.MineBeam.Width", itemId);
            var colour = ParseColour(beam.Text("Color"));
            if (beam.Has("Color") && colour == null)
                Refused("colour:beam:" + Number(itemId), "type", "weapon.forge.etc.MineBeam.Color", itemId);
            if (line != null && colour != null)
            {
                line.startColor = colour.Value; line.endColor = colour.Value;
                any = true;
            }
            else if (colour != null)
                // This mine carries no laser line renderer.
                Refused("no-line:" + Number(itemId), "unavailable", "weapon.forge.etc.MineBeam.Color", itemId);
            if (any) Applied("mine-beam", itemId);
            wrote |= any;
        }
        else if (beam != null)
            // This mine instance carries no laser detection component.
            Refused("no-laser:" + Number(itemId), "unavailable", "weapon.forge.etc.MineBeam.*", itemId);
        var light = entry.TryBlock("forge.etc.MineLight", out var lightBlock) ? lightBlock.Values : values == null ? null : First(values, "LightData");
        if (light != null && mine.m_light != null)
        {
            bool any = false;
            any |= Set(light, "Intensity", value => mine.m_light.intensity = value);
            var colour = ParseColour(light.Text("Color"));
            if (light.Has("Color") && colour == null)
                Refused("colour:light:" + Number(itemId), "type", "weapon.forge.etc.MineLight.Color", itemId);
            if (colour != null) { mine.m_light.color = colour.Value; any = true; }
            if (any) Applied("mine-light", itemId);
            wrote |= any;
        }
        else if (light != null)
            // This mine instance carries no light.
            Refused("no-light:" + Number(itemId), "unavailable", "weapon.forge.etc.MineLight.*", itemId);
        return wrote;
    }

    private bool ApplySentryInteraction(SentryGunFirstPerson sentry, WeaponFieldBlock block, uint itemId)
    {
        bool wrote = false;
        var values = block.Values;
        wrote |= Set(values, "PlacementTime", value => sentry.m_deployPickupInteractionDuration = value);
        if (wrote) Applied("sentry-interaction", itemId);
        return wrote;
    }

    private bool ApplySentry(SentryGunInstance sentry, WeaponFieldBlock block, uint itemId)
    {
        bool wrote = false;
        var values = block.Values;
        wrote |= Set(values, "ScanDelay", value => sentry.m_initialScanDelay = value);
        var colour = ParseColour(values.Text("ScanColor"));
        if (values.Has("ScanColor") && colour == null)
            Refused("colour:sentry:" + Number(itemId), "type", "weapon.forge.etc.Sentry.ScanColor", itemId);
        var target = ParseColour(values.Text("TargetColor"));
        if (values.Has("TargetColor") && target == null)
            Refused("colour:sentry-target:" + Number(itemId), "type", "weapon.forge.etc.Sentry.TargetColor", itemId);
        if (colour != null || target != null)
        {
            var visuals = sentry.m_visuals?.TryCast<SentryGunInstance_ScannerVisuals_Plane>();
            if (visuals != null)
            {
                if (colour != null) visuals.m_scanningColor = colour.Value;
                if (target != null) visuals.m_hasTargetColor = target.Value;
                wrote = true;
            }
            else
                // This sentry instance carries no scanner visuals component.
                Refused("no-visuals:" + Number(itemId), "unavailable", "weapon.forge.etc.Sentry.ScanColor/TargetColor", itemId);
        }
        if (wrote) Applied("sentry", itemId);
        return wrote;
    }

    /// <summary>A present number field, written through the caller's own setter. Absent means the document said
    /// nothing, which is not a zero.</summary>
    private static bool Set(WeaponFieldBag values, string path, Action<float> write)
    {
        var value = values.Single(path);
        if (value == null) return false;
        write(value.Value);
        return true;
    }

    private bool SetInteger(WeaponFieldBag values, string path, Action<int> write)
    {
        var value = values.Integer(path);
        if (value == null) return false;
        if (value is < int.MinValue or > int.MaxValue)
        {
            // The stated value does not fit the native integer member.
            Refused("range:" + path, "range", path, 0, RuntimeLogValue.Of("value", value.Value));
            return false;
        }
        write((int)value.Value);
        return true;
    }

    /// <summary>The first entry of a `list` path, which is how the mine's own `FoamData`, `BeamData` and
    /// `LightData` carry the payload values. The document loader refuses these together with the payload's own
    /// block id, so at most one spelling is ever present.</summary>
    private static WeaponFieldBag? First(WeaponFieldBag values, string path)
    {
        var items = values.Items(path);
        return items is { Length: > 0 } ? items[0] : null;
    }

    private static bool Values(WeaponFieldBag values, params string[] paths)
    {
        foreach (var path in paths) if (values.Has(path)) return true;
        return false;
    }

    /// <summary>A colour as the catalog declares it: `#RRGGBB` or `#RRGGBBAA`, each channel two hex digits. A
    /// text that is not that shape is refused here rather than silently becoming white.</summary>
    internal static Color? ParseColour(string? text)
    {
        if (text == null || text.Length is not (7 or 9) || text[0] != '#') return null;
        var channels = new[] { 255, 255, 255, 255 };
        for (int index = 0; index < 4; index++)
        {
            int at = 1 + index * 2;
            if (at >= text.Length) break;
            var high = Hex(text[at]);
            var low = Hex(text[at + 1]);
            if (high < 0 || low < 0) return null;
            channels[index] = high * 16 + low;
        }
        return new Color(channels[0] / 255f, channels[1] / 255f, channels[2] / 255f, channels[3] / 255f);
    }

    private static int Hex(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => -1
    };

    /// <summary>One component's authored fields were written on the item block <paramref name="itemId"/>.</summary>
    private void Applied(string target, uint itemId, string? source = null)
    {
        if (!_log.Logs(RuntimeLogLevel.Info)) return;
        _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.FieldApplied, new RuntimeLogFacts
        {
            Values = source == null
                ? new[] { RuntimeLogValue.Of("target", target), RuntimeLogValue.Of("item", (long)itemId) }
                : new[] { RuntimeLogValue.Of("target", target), RuntimeLogValue.Of("item", (long)itemId), RuntimeLogValue.Quote("source", source) }
        });
    }

    /// <summary>An authored field that was not written, recorded once per <paramref name="key"/>. A data problem the
    /// author must fix, so it is an error; the numbers are the values that disagreed or did not fit.</summary>
    private void Refused(string key, string reason, string field, uint itemId, params RuntimeLogValue[] numbers)
    {
        var first = _once.ShouldReport(key);
        if (!first || !_log.Logs(RuntimeLogLevel.Error)) return;
        var values = new List<RuntimeLogValue>(3 + numbers.Length) { RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Of("field", field) };
        if (itemId != 0) values.Add(RuntimeLogValue.Of("item", (long)itemId));
        values.AddRange(numbers);
        // log-values-max: 5 -- reason, field, item and at most the 2 numbers of the largest caller.
        _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.FieldRejected, new RuntimeLogFacts { Values = values });
    }

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
}

internal sealed class WeaponScannerReport(WeaponFieldEntry entry)
{
    internal WeaponFieldEntry Entry { get; } = entry;
    internal bool Written { get; set; }
}

internal sealed class WeaponScannerBinding(EnemyScanner scanner, uint itemId, float? duration, int? distance, WeaponScannerReport? report)
{
    internal EnemyScanner Scanner { get; } = scanner;
    internal IntPtr Pointer { get; } = scanner.Pointer;
    internal uint ItemId { get; } = itemId;
    internal float? Duration { get; } = duration;
    internal int? Distance { get; } = distance;
    internal WeaponScannerReport? Report { get; } = report;
    internal bool Configured => Duration != null || Distance != null;
}

/// <summary>A synchronous native call's scalar inputs. No authored value survives the
/// finalizer, including exceptions and nested calls for another or vanilla tool.</summary>
internal struct WeaponScannerCall
{
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static float _baselineDuration;
    [ThreadStatic] private static int _baselineDistance;
    /// <summary>Whether a prepared call is open on this thread: only then does an unauthored nested call still have
    /// to hold the outer call's baseline.</summary>
    internal static bool InCall => _depth != 0;
    private readonly bool _prepared;
    private readonly float _beforeDuration, _duration;
    private readonly int _beforeDistance, _distance;
    private readonly WeaponLog? _log;
    private readonly WeaponScannerBinding? _binding;
    private bool _entered, _wroteDuration, _wroteDistance;

    internal WeaponScannerCall(float? duration, int? distance, WeaponLog? log = null,
        WeaponScannerBinding? binding = null)
    {
        _prepared = true;
        _beforeDuration = EnemyScanner.s_tagDuration;
        _beforeDistance = EnemyScanner.s_maxCourseNodeDistance;
        _duration = duration ?? (_depth == 0 ? _beforeDuration : _baselineDuration);
        _distance = distance ?? (_depth == 0 ? _beforeDistance : _baselineDistance);
        _entered = _wroteDuration = _wroteDistance = false;
        _log = log; _binding = binding;
    }

    internal void Apply()
    {
        if (!_prepared) return;
        if (_depth == 0) { _baselineDuration = _beforeDuration; _baselineDistance = _beforeDistance; }
        _depth++;
        _entered = true;
        // Record intent before a setter: even a write-then-throw must be unwound.
        if (_duration != _beforeDuration) { _wroteDuration = true; EnemyScanner.s_tagDuration = _duration; }
        if (_distance != _beforeDistance) { _wroteDistance = true; EnemyScanner.s_maxCourseNodeDistance = _distance; }
        var report = _binding?.Report;
        if (report != null && !report.Written)
        {
            report.Written = true;
            if (_log!.Logs(RuntimeLogLevel.Info)) _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.FieldApplied, new RuntimeLogFacts
            {
                Values = new[] { RuntimeLogValue.Of("target", "scanner-call"), RuntimeLogValue.Of("item", (long)report.Entry.Id),
                    RuntimeLogValue.Quote("source", report.Entry.Source), RuntimeLogValue.Of("courseDistance", (long)_distance),
                    RuntimeLogValue.Of("tagDuration", (double)_duration) }
            });
        }
    }

    internal Exception? Restore(Exception? original)
    {
        if (!_entered) return original;
        _entered = false;
        try
        {
            try { if (_wroteDuration) EnemyScanner.s_tagDuration = _beforeDuration; }
            catch (Exception error) { original = Combine(original, error); }
            try { if (_wroteDistance) EnemyScanner.s_maxCourseNodeDistance = _beforeDistance; }
            catch (Exception error) { original = Combine(original, error); }
        }
        finally { _depth--; }
        return original;
    }

    private static Exception Combine(Exception? original, Exception cleanup)
        => original == null ? cleanup : new AggregateException("Weapon scanner call restoration failed.", original, cleanup);
}
