using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeCore;
using ForgeEngine.Framework;
using GameData;
using Gear;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>What one gear's documents say about its flashlight part, projected out of the Forge weapon documents
/// (block `forge.vanilla.flashlight`). Three independent things, each optional, at least one present:
///
/// - `m_settingsID`: which `FlashlightSettingsDataBlock` row the flashlight reads. The game has no data-block field
///   that points a gear at a flashlight row. The reference is the serialized `Gear.GearPartFlashlight.m_settingsID`
///   on the flashlight part prefab itself (Flashlight_A → 2, B → 3, C → 5, D → 6, E → 7), and
///   `ItemEquippable.OnWield` hands that component to `PlayerInventoryBase.OnItemEquippableFlashlightWielded`, which
///   looks the row up with `GameDataBlockBase.GetBlock(uint)` and writes its range, angle, colour, intensity and
///   cookie onto the player's light (`evidence/flashlight-settings.json`). Every gear that fits the same part
///   therefore reads the same row; a gear with its own values ships its own row and names it here.
/// - `LightPosition` + `LightRotation`: where the light comes out, as the pose of the part's own
///   `GearPartFlashlight.m_lightAlign` transform, in the flashlight part's local frame. The light is the game's
///   own light on the part's own mount; only the mount moves, so the switch, the battery and what other players
///   see stay the game's.
/// - `HideShell`: the vanilla flashlight model is not drawn. The part object stays active, because the mount and
///   the light live under it; `SetActive(false)` on the part (what an `Enabled: false` pose does) would take them
///   with it.
///
/// The values themselves are never read by this package: the game's own reader reads them out of the data block.
/// Everything is written on this gear's own spawned part, so no other gear that fits the same part is touched.</summary>
internal static class GearPartFlashlightData
{
    internal const string BlockId = "forge.vanilla.flashlight";
    internal const string SettingsPath = "m_settingsID";
    internal const string HideShellPath = "HideShell";
    internal const string LightPositionPath = "LightPosition";
    internal const string LightRotationPath = "LightRotation";

    /// <summary>Every accepted block, keyed by the offline gear id the entry was written under. A flashlight part
    /// belongs to one assembled gear, and the holder that spawns it names that gear by its offline gear id only, so
    /// an entry written under an item or archetype id is refused rather than guessed onto a gear.</summary>
    internal static GearPartFlashlightSnapshot Project(WeaponFieldSet set, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(rejected);
        var rows = new Dictionary<string, GearPartFlashlightMount>(StringComparer.Ordinal);
        foreach (var entry in set.Entries)
        {
            if (!entry.TryBlock(BlockId, out var block)) continue;
            if (entry.Key != WeaponFieldKey.Offline)
            {
                rejected("weapon.flashlight-settings file=" + entry.Source + ": the `" + BlockId + "` block is written under "
                    + (entry.Key == WeaponFieldKey.Item ? "`itemId`" : "`archetypeId`")
                    + "; a flashlight part belongs to one gear, so the entry must be written under `offlineId`.");
                continue;
            }
            var key = entry.Id.ToString(CultureInfo.InvariantCulture);
            // The document loader stores a reference as its canonical decimal text and has already refused any other
            // spelling, so a present path always parses.
            var text = block.Values.Text(SettingsPath);
            uint? settings = null;
            if (text != null)
            {
                if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    rejected("weapon.flashlight-settings file=" + entry.Source + " block=" + key + ": the block names no `"
                        + SettingsPath + "` row.");
                    continue;
                }
                settings = parsed;
            }
            var position = block.Values.Vector(LightPositionPath);
            var rotation = block.Values.Vector(LightRotationPath);
            if ((position == null) != (rotation == null))
            {
                rejected("weapon.flashlight-settings file=" + entry.Source + " block=" + key + ": `" + LightPositionPath + "` and `"
                    + LightRotationPath + "` are written together; the mount is placed and aimed as one pose.");
                continue;
            }
            var hide = block.Values.Boolean(HideShellPath) == true;
            if (settings == null && position == null && !hide)
            {
                rejected("weapon.flashlight-settings file=" + entry.Source + " block=" + key + ": the block names no `"
                    + SettingsPath + "` row, no light pose and no hidden shell.");
                continue;
            }
            rows[key] = new GearPartFlashlightMount(settings,
                position == null ? null : new Vector3(position[0], position[1], position[2]),
                rotation == null ? null : new Vector3(rotation[0], rotation[1], rotation[2]), hide);
        }
        return new GearPartFlashlightSnapshot(rows);
    }
}

internal sealed class GearPartFlashlightMount
{
    internal GearPartFlashlightMount(uint? settings, Vector3? lightPosition, Vector3? lightEulerAngles, bool hideShell)
    { Settings = settings; LightPosition = lightPosition; LightEulerAngles = lightEulerAngles; HideShell = hideShell; }
    internal uint? Settings { get; }
    internal Vector3? LightPosition { get; }
    internal Vector3? LightEulerAngles { get; }
    internal bool HideShell { get; }
}

internal sealed class GearPartFlashlightSnapshot
{
    internal static readonly GearPartFlashlightSnapshot Empty = new(new Dictionary<string, GearPartFlashlightMount>(StringComparer.Ordinal));
    private readonly IReadOnlyDictionary<string, GearPartFlashlightMount> _rows;
    internal GearPartFlashlightSnapshot(IReadOnlyDictionary<string, GearPartFlashlightMount> rows) { _rows = rows; }
    internal int BlockCount => _rows.Count;
    internal bool TryGet(string blockId, out GearPartFlashlightMount mount) => _rows.TryGetValue(blockId, out mount!);
}

/// <summary>Applies one gear's flashlight document to the flashlight part its holder spawned.
///
/// It runs from the same holder hook the part poses do, before the poses and added parts are written, and touches
/// only the components on this holder's own part instance: the prefab and every other holder keep what they
/// already read. Every write is absolute, so a holder that spawns again writes the same values. A gear without a
/// flashlight part and a part without the component are each reported once and nothing is written for that gear —
/// no light is moved or hidden on a part that cannot carry it. A row the game cannot find leaves the part on the row
/// it came with, as before, while the mount and the shell are still applied.</summary>
internal sealed class GearPartFlashlightApplier
{
    private readonly GearPartFlashlightSnapshot _snapshot;
    private readonly WeaponLog _log;
    private readonly OnceReporter _once;

    internal GearPartFlashlightApplier(GearPartFlashlightSnapshot snapshot, Action<string> report, WeaponLog log)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal int BlockCount => _snapshot.BlockCount;

    internal void Apply(GearPartHolder? holder, string? blockId)
    {
        if (holder == null) return;
        if (blockId == null || !_snapshot.TryGet(blockId, out var mount)) return;
        var part = holder.FlashlightPart;
        if (part == null)
        {
            Once("part:" + blockId, "weapon.flashlight-part-missing block=" + blockId
                + ": this gear has no flashlight part, so its flashlight row, light pose and hidden shell are never applied.");
            return;
        }
        var flashlight = part.GetComponent<GearPartFlashlight>();
        if (flashlight == null)
        {
            Once("component:" + blockId, "weapon.flashlight-component-missing block=" + blockId
                + ": the flashlight part carries no GearPartFlashlight, so there is no row reference or light mount to write.");
            return;
        }
        bool moved = false, hidden = false;
        if (mount.LightPosition != null && mount.LightEulerAngles != null)
        {
            var align = flashlight.m_lightAlign;
            if (align == null)
                Once("align:" + blockId, "weapon.flashlight-component-missing block=" + blockId
                    + ": the flashlight component has no light mount (m_lightAlign), so the light pose is not applied.");
            else
            {
                // Parented under the part root first, so the authored pose is in the part's own frame whatever the
                // prefab nested the mount under; the light follows its mount.
                align.SetParent(part.transform, false);
                align.localPosition = mount.LightPosition.Value;
                align.localEulerAngles = mount.LightEulerAngles.Value;
                moved = true;
            }
        }
        if (mount.HideShell)
        {
            // Renderers only: the part object stays active, because the light mount lives under it.
            foreach (var renderer in part.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            hidden = true;
        }
        long previous = 0;
        bool pointed = false;
        if (mount.Settings != null)
        {
            var settings = mount.Settings.Value;
            previous = flashlight.m_settingsID;
            if (!FlashlightSettingsDataBlock.HasBlock(settings))
                Once("row:" + blockId, "weapon.flashlight-settings-missing block=" + blockId + " settings=" + Number(settings)
                    + ": FlashlightSettingsDataBlock has no such row; the part keeps row " + Number((uint)previous) + ".");
            else
            {
                flashlight.m_settingsID = settings;
                pointed = true;
            }
        }
        if ((pointed || moved || hidden) && _log.Logs(RuntimeLogLevel.Info))
        {
            var values = new List<RuntimeLogValue> { RuntimeLogValue.Of("block", blockId), EquipmentNativeAdapter.WeaponName(blockId) };
            if (pointed) { values.Add(RuntimeLogValue.Of("settings", (long)mount.Settings!.Value)); values.Add(RuntimeLogValue.Of("previous", previous)); }
            if (moved) values.Add(RuntimeLogValue.Of("lightMoved", "yes"));
            if (hidden) values.Add(RuntimeLogValue.Of("shellHidden", "yes"));
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.FlashlightApplied, new RuntimeLogFacts { Values = values });
        }
    }

    // A holder spawns again on every view change and icon render; one line per gear and reason is the whole budget.
    private void Once(string key, string message) => _once.Report(key, message);

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
}
