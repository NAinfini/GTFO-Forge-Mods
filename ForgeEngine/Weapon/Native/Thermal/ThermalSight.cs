using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeVisual;
using Gear;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeapon.Native;

/// <summary>The author's thermal sight per gun, projected out of the Forge weapon documents (block `forge.thermalSight`,
/// paths `Enabled`, `HighColor`, `LowColor`). Only an entry under `offlineId` is accepted: a sight belongs to one assembled gear.</summary>
internal static class ThermalSightData
{
    internal const string BlockId = "forge.thermalSight";
    internal static readonly Color DefaultHigh = new(245f / 255f, 96f / 255f, 124f / 255f);
    internal static readonly Color DefaultLow = new(1f, 0f, 48f / 255f);

    internal static Dictionary<string, (Color High, Color Low)> Project(WeaponFieldSet set, Action<string> rejected)
    {
        var looks = new Dictionary<string, (Color, Color)>(StringComparer.Ordinal);
        foreach (var entry in set.Entries)
        {
            if (!entry.TryBlock(BlockId, out var block)) continue;
            var key = entry.Id.ToString(CultureInfo.InvariantCulture);
            if (entry.Key != WeaponFieldKey.Offline)
            {
                rejected("weapon.thermal-sight file=" + entry.Source + ": the block belongs to one gear, so the entry must be written under `offlineId`.");
                continue;
            }
            if (block.Values.Boolean("Enabled") != true) continue;
            if (!TryColor(block, "HighColor", DefaultHigh, out var high) || !TryColor(block, "LowColor", DefaultLow, out var low))
            {
                rejected("weapon.thermal-sight file=" + entry.Source + " block=" + key + ": a colour is not #RRGGBB.");
                continue;
            }
            looks[key] = (high, low);
        }
        return looks;
    }

    private static bool TryColor(WeaponFieldBlock block, string path, Color fallback, out Color color)
    {
        color = fallback;
        if (!block.Values.Has(path)) return true;
        var parsed = WeaponFieldApplier.ParseColour(block.Values.Text(path));
        if (parsed == null) return false;
        color = parsed.Value;
        return true;
    }
}

/// <summary>Turns the lens of an authored gun's sight into the game's own thermal lens, on the local first-person gun only.
///
/// The game has one thermal shader (`Unlit/HolographicSight_Thermal`). It needs no camera or render texture: the lens mesh reads
/// the main camera's G-buffer at its own screen position and looks the heat up in `_HeatTex`, so enemies and the environment are two
/// stretches of one heat axis and two colours are one gradient. The template is the thermal material of the game's Sight_10_t, borrowed
/// through ForgeCore `SightLenses` (which loads its shard and waits for somebody else's load). The lens is a copy of the sight's own glass
/// (`SightLenses.Duplicate`: same mesh, node and layer, drawn just before it); the glass stays, so its additive reticle lands on the thermal
/// picture and the template's reticles are blanked. A sight whose lens already is thermal only gets the gradient. Third-person guns
/// (`BulletWeaponSynced`, which also covers bots) and menu holders are left alone, and nothing is sent over the network: every machine sees
/// its own first-person gun.</summary>
internal sealed class ThermalSightApplier
{
    // Build 20403457, static read of Gear_Weapon_Sight S1 (sharedassets303): GearSightPart 14.
    private const string TemplatePrefab = "Assets/AssetPrefabs/Items/Gear/Parts/Sights/Sight_10_t.prefab";
    private const string TemplateNode = "Sight_10_Thermal";
    private const string LensName = "ForgeThermalLens";
    /// <summary>Where the heat axis turns from the environment into enemies: estimated from the template's parameters
    /// (environment 0.19 to 0.37, enemies 0.7 to 0.87); to be confirmed by hand in the game.</summary>
    private const float BandStart = 0.40f, BandEnd = 0.45f;

    private static readonly int HeatTex = Shader.PropertyToID("_HeatTex");
    private static readonly string[] Reticles = { "_ReticuleA", "_ReticuleB", "_ReticuleC" };

    private Dictionary<string, (Color High, Color Low)> _looks;
    private readonly Dictionary<(Color, Color), Texture2D> _ramps = new();
    private readonly OnceReporter _once;
    private readonly WeaponLog _log;
    // The thermal lens with its reticles blanked; each gun's lens is a copy of it.
    private Material? _template;
    private bool _failed;
    private BulletWeapon? _waiting;

    internal ThermalSightApplier(Dictionary<string, (Color High, Color Low)> looks, Action<string> report, WeaponLog log)
    {
        _looks = looks;
        _once = new OnceReporter(report);
        _log = log;
    }

    internal void Use(Dictionary<string, (Color High, Color Low)> looks) => _looks = looks;

    /// <summary>A gun with a thermal sight exists: bring the template in now, so its first wield finds it ready.</summary>
    internal void Prepare(GearPartHolder holder)
    {
        if (_template != null || _looks.Count == 0) return;
        var block = EquipmentNativeAdapter.GearBlockId(holder.GearIDRange);
        if (block != null && _looks.ContainsKey(block)) Load();
    }

    internal void Wielded(BulletWeapon weapon)
    {
        if (_looks.Count == 0 || weapon.TryCast<BulletWeaponSynced>() != null) return;
        var holder = weapon.GearPartHolder;
        if (holder == null || holder.MenuMode) return;
        var block = EquipmentNativeAdapter.GearBlockId(holder.GearIDRange);
        if (block == null || !_looks.TryGetValue(block, out var look)) return;
        if (!Load())
        {
            if (!_failed) _waiting = weapon;
            return;
        }
        Apply(holder, block, look);
    }

    /// <summary>Once per simulation tick: a gun wielded while the template was still loading gets its lens as soon as it is there,
    /// without waiting for the next wield.</summary>
    internal void Poll()
    {
        var waiting = _waiting;
        if (waiting == null) return;
        if (waiting.WasCollected || _failed) { _waiting = null; return; }
        if (!Load()) return;
        _waiting = null;
        Wielded(waiting);
    }

    /// <summary>True once the template is held. ForgeCore keeps the shard's dependency for every later level and the material copy keeps
    /// the shader and textures alive by itself.</summary>
    private bool Load()
    {
        if (_template != null) return true;
        if (_failed) return false;
        var lens = SightLenses.Of(TemplatePrefab, AssetBundleName.Gear_Weapon_Sight, AssetBundleShard.S1, TemplateNode, SightLenses.Thermal);
        if (lens.State == SightLensState.Loading) return false;
        if (lens.State == SightLensState.Failed)
        {
            _failed = true;
            _once.Report("template", WeaponLogCodes.ThermalSight + ": the game's thermal material was not found at " + TemplatePrefab + " (" + lens.Failure + "); thermal sights stay plain.");
            return false;
        }
        _template = SightLenses.Copy(lens, LensName);
        _template.hideFlags = HideFlags.HideAndDontSave;
        foreach (var reticle in Reticles) _template.SetTexture(reticle, Texture2D.blackTexture);
        return true;
    }

    private void Apply(GearPartHolder holder, string block, (Color High, Color Low) look)
    {
        var sight = holder.SightPart;
        if (sight == null)
        {
            _once.Report("empty:" + block, WeaponLogCodes.ThermalSight + ": block=" + block + " has no sight part; the thermal sight is not shown.");
            return;
        }
        var ramp = Ramp(look.High, look.Low);
        var lenses = new List<MeshRenderer>();
        bool thermal = false;
        foreach (var renderer in sight.GetComponentsInChildren<MeshRenderer>(true))
        {
            var material = renderer.sharedMaterial;
            var shader = material == null || material.shader == null ? null : material.shader.name;
            if (renderer.gameObject.name == LensName) return; // this gun was set up at an earlier wield
            if (shader == SightLenses.Thermal && renderer.gameObject.activeSelf)
            {
                renderer.material.SetTexture(HeatTex, ramp);
                thermal = true;
            }
            else if (shader == SightLenses.Holographic && renderer.gameObject.activeSelf && SightLenses.IsFlat(renderer)) lenses.Add(renderer);
        }
        if (!thermal)
        {
            if (lenses.Count == 0)
            {
                _once.Report("lens:" + block, WeaponLogCodes.ThermalSight + ": block=" + block + " sight=" + holder.SightData?.General?.Model + " has no flat lens; the thermal sight is not shown. Pick a sight with a frame.");
                return;
            }
            foreach (var glass in lenses)
            {
                var lens = new Material(_template) { name = LensName };
                lens.SetTexture(HeatTex, ramp);
                SightLenses.Duplicate(glass, lens, LensName);
            }
        }
        if (!_log.Logs(RuntimeLogLevel.Info)) return;
        _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ThermalSight, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("block", block), EquipmentNativeAdapter.WeaponName(block),
                RuntimeLogValue.Of("sight", holder.SightData?.General?.Model ?? ""), RuntimeLogValue.Of("lenses", (long)(thermal ? 0 : lenses.Count)),
                RuntimeLogValue.Of("high", "#" + ColorUtility.ToHtmlStringRGB(look.High)), RuntimeLogValue.Of("low", "#" + ColorUtility.ToHtmlStringRGB(look.Low)) }
        });
    }

    private Texture2D Ramp(Color high, Color low)
    {
        if (_ramps.TryGetValue((high, low), out var existing) && existing != null) return existing;
        var ramp = new Texture2D(256, 1, TextureFormat.RGBA32, false, false)
        {
            name = "ForgeThermalRamp",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        for (int x = 0; x < 256; x++) ramp.SetPixel(x, 0, RampColor(x / 255f, high, low));
        ramp.Apply(false, false);
        return _ramps[(high, low)] = ramp;
    }

    /// <summary>Below the band: the low colour, darker the colder, so the terrain keeps its relief. Above it: the high colour.</summary>
    private static Color RampColor(float heat, Color high, Color low)
    {
        var shade = Mathf.Clamp01(heat / BandStart);
        var cold = Color.Lerp(Color.black, low, shade * shade);
        return Color.Lerp(cold, high, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(BandStart, BandEnd, heat)));
    }
}

// ForgeCore patches both methods once. The parts hook only starts the template load; the wield hook is where a gun is known to be the
// local first-person one (at OnAllPartsSpawned the holder belongs to no player yet). Nothing is subscribed when no gun has a thermal sight.
internal sealed partial class WeaponNativeSession
{
    // Process-wide on purpose: the native session is built once per process (Plugin.Load), the two hooks below are added
    // once with the applier and act only while `Offer` answers a live session, and a later content entry only feeds the
    // same applier its looks (`Use`).
    private static ThermalSightApplier? _thermalSights;

    static partial void NativeThermalCheck(WeaponFieldSet entry, Action<string> refused) => ThermalSightData.Project(entry, refused);

    partial void PollThermalSight() => _thermalSights?.Poll();

    /// <summary>Called at session start with the disk documents and again with every content entry, which is where packed guns live.</summary>
    static partial void NativeThermalSights(WeaponFieldSet fields, Action<string> report, WeaponLog log)
    {
        var looks = ThermalSightData.Project(fields, report);
        if (_thermalSights != null)
        {
            _thermalSights.Use(looks);
            return;
        }
        if (looks.Count == 0) return;
        _thermalSights = new ThermalSightApplier(looks, report, log);
        GameHooks.GearPartsSpawned.After((holder, _) => Offer(WeaponFeature.Appearance)?.Guard(static (_, holder) => _thermalSights?.Prepare(holder), holder));
        GameHooks.BulletWeaponWielded.After((weapon, _) => Offer(WeaponFeature.Appearance)?.Guard(static (_, weapon) => _thermalSights?.Wielded(weapon), weapon));
    }
}
