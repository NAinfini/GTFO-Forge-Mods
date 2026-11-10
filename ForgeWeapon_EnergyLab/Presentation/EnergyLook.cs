using System;
using System.Collections.Generic;
using System.Linq;
using ForgeAudio;
using ForgeVisual;
using ForgeWeaponEnergyLab.Native;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>What one gun plays and draws: the sounds and effects its definition's <c>Look</c> names, found once at load, and for everything
/// else the shipped ones. The presentation asks the look of the gun in the hand for every sound (<see cref="Cue"/>) and effect
/// (<see cref="Effect"/>), so a gun without a <c>Look</c> plays and draws exactly what it always did.</summary>
internal sealed class EnergyLook
{
    /// <summary>The shipped sounds and effects; what every gun without a <c>Look</c> uses.</summary>
    internal static readonly EnergyLook Default = new(new Dictionary<string, AudioCue>(), new Dictionary<FxHandle, FxHandle>());

    private readonly Dictionary<string, AudioCue> _cues;
    private readonly Dictionary<FxHandle, FxHandle> _effects;

    private EnergyLook(Dictionary<string, AudioCue> cues, Dictionary<FxHandle, FxHandle> effects) { _cues = cues; _effects = effects; }

    /// <summary>The sound that plays for the shipped cue <paramref name="name"/>.</summary>
    internal AudioCue Cue(string name) => _cues.Count != 0 && _cues.TryGetValue(name, out var cue) ? cue : EnergyAudio.Cue(name);

    /// <summary>The effect to draw in place of <paramref name="shipped"/> (null stays null: its slot was missing and has been reported).</summary>
    internal FxHandle? Effect(FxHandle? shipped)
        => shipped != null && _effects.Count != 0 && _effects.TryGetValue(shipped, out var other) ? other : shipped;

    private static readonly Dictionary<uint, EnergyLook> Resolved = new();
    private static readonly Dictionary<uint, Waiting> Pending = new();

    private sealed record Waiting(EnergyGear Gear, float Due);

    /// <summary>The look of <paramref name="gear"/>, <see cref="Default"/> until <see cref="ResolveAll"/> has run.</summary>
    internal static EnergyLook Of(EnergyGear gear) => Resolved.TryGetValue(gear.CategoryId, out var look) ? look : Default;

    /// <summary>Looks up what every gun's <c>Look</c> names once the effect library is found and this package's sounds have decoded.
    /// A third-party pack still decoding is not waited for: that gun's effects resolve now and its sounds when the pack finishes.</summary>
    internal static void ResolveAll()
    {
        foreach (var gear in EnergyGears.All)
        {
            if (EnergyAudio.PackOf(gear.Definition.Id) is { IsLoaded: false } && gear.Definition.Look is { Sounds.Count: > 0 })
            {
                // One gun's own sound set still decoding; its effects can resolve, its sounds are reported per cue when they arrive.
                Resolved[gear.CategoryId] = Resolve(gear, soundsReady: false);
                Pending[gear.CategoryId] = new(gear, Time.realtimeSinceStartup + PresentationPreload.AudioLoadSeconds);
                continue;
            }
            ResolveInto(gear, soundsReady: true);
        }
    }

    /// <summary>Finishes the guns whose own pack was still decoding. A pack that misses its deadline is that gun's own failure,
    /// reported per sound; no other gun, and no later phase of the preload, is affected.</summary>
    internal static void ResolvePending()
    {
        if (Pending.Count == 0) return;
        foreach (var row in Pending.ToArray())
        {
            var waiting = row.Value;
            if (EnergyAudio.PackOf(waiting.Gear.Definition.Id) is { IsLoaded: true }) { Pending.Remove(row.Key); ResolveInto(waiting.Gear, soundsReady: true); }
            else if (Time.realtimeSinceStartup >= waiting.Due) { Pending.Remove(row.Key); ReportTimedOut(waiting.Gear); }
        }
    }

    private static void ResolveInto(EnergyGear gear, bool soundsReady)
    {
        try { Resolved[gear.CategoryId] = Resolve(gear, soundsReady); }
        catch (Exception error)
        {
            Resolved[gear.CategoryId] = Default;
            Plugin.Logger.ErrorOnce($"{gear.Definition.Id} look could not be resolved; it plays and draws the shipped sounds and effects: {error}");
        }
    }

    private static void ReportTimedOut(EnergyGear gear)
    {
        // The whole look is this gun's own: effects keep what ResolveAll found, the sounds below report themselves.
        foreach (var (name, reference) in gear.Definition.Look!.Sounds)
            Plugin.Logger.ErrorOnce($"{gear.Definition.Id} Look.Sounds.{name}: sound '{reference}' did not decode in time; the gun plays its own.");
        ResolveInto(gear, soundsReady: false);
    }

    /// <summary>The look of <paramref name="gear"/>: each name its definition's <c>Look</c> gives is looked up now; one that cannot be found
    /// is reported once and that sound or effect stays the shipped one. <paramref name="soundsReady"/> is false while the gun's own
    /// sound pack is still decoding, so its sounds are left to <see cref="ResolvePending"/> rather than reported as missing.</summary>
    private static EnergyLook Resolve(EnergyGear gear, bool soundsReady)
    {
        if (gear.Definition.Look is not { } look || look.Sounds.Count == 0 && look.Fx.Count == 0) return Default;
        var id = gear.Definition.Id;
        var cues = new Dictionary<string, AudioCue>(StringComparer.Ordinal);
        if (soundsReady)
            foreach (var (name, reference) in look.Sounds)
            {
                if (FindCue(gear, reference) is { } cue) cues[name] = cue;
                else Plugin.Logger.ErrorOnce($"{id} Look.Sounds.{name}: no sound '{reference}' (a cue of this gun's audio folder, or identity/cue of a loaded ForgeAudio library); the gun plays its own.");
            }
        var effects = new Dictionary<FxHandle, FxHandle>();
        foreach (var (slot, reference) in look.Fx)
        {
            var shipped = EnergyVfx.Library.Find(slot);
            if (shipped == null) { Plugin.Logger.ErrorOnce($"{id} Look.Fx.{slot}: the effect library has no such slot; the gun draws its own."); continue; }
            // ForgeVisual reports an unknown name itself (once).
            if (ForgeVisual.Fx.Find(reference) is { } other) effects[shipped] = other;
            else Plugin.Logger.ErrorOnce($"{id} Look.Fx.{slot}: no effect '{reference}' (Pack:item of a registered bundle, or Game:name); the gun draws its own.");
        }
        return new EnergyLook(cues, effects);
    }

    private static AudioCue? FindCue(EnergyGear gear, string reference)
    {
        var slash = reference.IndexOf('/');
        if (slash > 0)
            return AudioLibrary.Find(reference[..slash]) is { } other && other.Has(reference[(slash + 1)..]) ? other.Cue(reference[(slash + 1)..]) : null;
        var pack = EnergyAudio.PackOf(gear.Definition.Id);
        if (pack != null && pack.Has(reference)) return pack.Cue(reference);
        return EnergyAudio.Library.Has(reference) ? EnergyAudio.Library.Cue(reference) : null;
    }
}
