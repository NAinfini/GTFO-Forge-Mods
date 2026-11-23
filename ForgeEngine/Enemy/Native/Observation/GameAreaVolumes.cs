using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEngine.GameBindings;

/// <summary>
/// The one native body every effect volume of this release is placed through: the game's own `EV_Sphere`, the
/// manager that makes it real (`EffectVolumeManager.RegisterVolume`) and the fog sphere that draws it
/// (`FogSphereAllocator`). It is the game-bound half of <see cref="IAreaVolumeBody"/>; the packages that place
/// volumes declare their rows and their result columns, and nothing of the sphere, the fog or the manager calls is
/// written a second time anywhere (`ForgeEnemy/Native/Behavior/EnemyVolumeActions.cs` used to hold the only copy).
///
/// Two things are true of every placement here and are what the ledger's own contract rests on:
/// <list type="number">
/// <item>The fog sphere is allocated first and the volume registered second, so a submission that throws on the way
/// in leaves no sphere behind: the volume is what a row promises and the sphere is drawn for it, not instead of it.
/// A throw unregisters whatever was already registered and returns the sphere before it escapes.</item>
/// <item>A fog sphere the game's own budget refused leaves a health/infection volume registered and answers
/// `Drawn: false`. For environmental fog it refuses the placement: there is no other native body to commit.</item>
/// </list>
///
/// A volume is moved by writing the sphere's own `position` and re-pointing the fog sphere at the same place and
/// its own remembered range, so a move never reallocates and never changes the size the volume was placed with.
/// Environmental fog has only the fog allocation. Releasing unregisters any effect volume and gives the fog sphere back;
/// the manager's own list is the authority on
/// whether the sphere is still there, so a release of one it already dropped is not an error.
/// </summary>
internal sealed class GameAreaVolumes : IAreaVolumeBody
{
    /// <summary>The one body of this process. It holds no state of its own: every placement's state lives in the
    /// object it hands back, so two packages placing volumes never share a record.</summary>
    public static GameAreaVolumes Shared { get; } = new();

    /// <summary>Authoring readback of the exact native objects returned by Place. The ledger identifies the
    /// objects; the native manager and fields independently establish their presence and current values.</summary>
    internal static JsonElement ReadNative(object body)
    {
        if (body is not Volume volume) throw new RuntimeContractException("enemy-test-volume-identity", "Unknown native volume body.");
        object? sphereState = null, fogState = null;
        if (volume.Sphere is { } sphere)
        {
            if (volume.SphereReleased || EffectVolumeManager.volumes?.Contains(sphere) != true)
                throw new RuntimeContractException("enemy-test-volume-readback-missing", "The native manager no longer holds this sphere.");
            sphereState = new
            {
                position = new[] { sphere.position.x, sphere.position.y, sphere.position.z },
                minRadius = sphere.minRadius,
                maxRadius = sphere.maxRadius,
                scale = sphere.modificationScale,
                contents = (int)sphere.contents,
                modification = (int)sphere.modification,
                sphere.invert,
                sphere.effectOrder
            };
        }
        if (volume.Fog is { } fog)
        {
            if (volume.FogReleased || !fog.IsAllocated || fog.m_sphere == null)
                throw new RuntimeContractException("enemy-test-fog-readback-missing", "The exact fog allocation is unavailable.");
            var data = fog.m_sphere.m_data;
            fogState = new
            {
                position = new[] { data.Position.x, data.Position.y, data.Position.z },
                inverseRangeSquared = data.InvRangeSqr,
                density = data.Density,
                radiance = new[] { data.Radiance.x, data.Radiance.y, data.Radiance.z }
            };
        }
        if (sphereState == null && fogState == null)
            throw new RuntimeContractException("enemy-test-volume-readback-missing", "No native body remains.");
        return RuntimeJson.From(new { sphere = sphereState, fog = fogState });
    }

    /// <summary>The density the fog sphere is allocated with, which is the game's own scream-pop density
    /// (`ES_ScoutScream.SCREAM_POP_DENSITY`). The volume rows carry no density parameter, so it takes the value
    /// the game's own visible volume uses rather than inventing a second one.</summary>
    private const float FogDensity = 0.5f;
    /// <summary>The radiance the fog sphere is allocated with. The volume rows carry no colour parameter, so the
    /// volume is drawn in the neutral tone the catalog's own fog rows use rather than in a colour no row can
    /// explain.</summary>
    private static readonly Color FogRadiance = new(0.55f, 0.65f, 0.75f, 1f);
    private const float FogIntensity = 1f;

    /// <summary>One placed volume's own two native objects: the sphere the manager holds and the fog sphere drawn
    /// for it, absent when the game's budget had no room, plus the outer radius the fog was allocated with — read
    /// back from the sphere so a move re-points it at the same size rather than at a second, remembered one.</summary>
    private sealed record Volume(EV_Sphere? Sphere, FogSphereAllocator? Fog, float Range)
    {
        internal bool SphereReleased;
        internal bool FogReleased;
    }

    /// <summary>One volume, placed. The four choices are mapped here — this is the only place the game's own
    /// `eEffectVolumeContents` and `eEffectVolumeModification` are named — and a member neither vocabulary declares
    /// is refused rather than mapped onto a neighbouring one, so a row that accepted a spelling the game cannot
    /// carry fails here instead of placing a volume that does something else.</summary>
    public AreaVolumePlacement Place(IReadOnlyList<double> position, AreaVolumeChoices choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        bool fogOnly = choices.Contents == "fog";
        if (!TryContents(choices.Contents, out var contents) && !fogOnly) return AreaVolumePlacement.Refused;
        if (!TryModification(choices.Modification, out var modification)) return AreaVolumePlacement.Refused;
        if (fogOnly && (choices.Modification != "inflict" || choices.MinRadius != 0
            || !double.IsFinite(choices.Scale) || choices.Scale < 0 || choices.Scale > float.MaxValue))
            return AreaVolumePlacement.Refused;
        var where = new Vector3((float)position[0], (float)position[1], (float)position[2]);
        var minimum = (float)choices.MinRadius; var maximum = (float)choices.MaxRadius;
        FogSphereAllocator? fog = null;
        EV_Sphere? sphere = null;
        var allocated = false;
        try
        {
            if (!fogOnly) sphere = new EV_Sphere
            {
                position = where,
                minRadius = minimum,
                maxRadius = maximum,
                contents = contents,
                modification = modification,
                modificationScale = (float)choices.Scale,
                invert = false,
                effectOrder = 0
            };
            // The fog sphere is allocated first, so a registration that throws leaves no sphere behind: the volume
            // is what the row promises and the sphere is drawn for it, not instead of it.
            fog = new FogSphereAllocator();
            fog.SetPositionRange(where, maximum);
            fog.SetDensity(fogOnly ? (float)choices.Scale : FogDensity);
            fog.SetRadiance(FogRadiance, FogIntensity);
            allocated = fog.TryAllocate();
            // Environmental fog has its own native allocation budget and never registers a gameplay volume.
            // With no fog allocation there is no body to commit, unlike a health/infection volume left undrawn.
            if (fogOnly && !allocated) return AreaVolumePlacement.Refused;
            if (sphere != null) EffectVolumeManager.RegisterVolume(sphere);
        }
        catch (Exception error)
        {
            NativeFault.Note("enemy.area-volume.place", error);
            // Whatever was already done is undone here, in the reverse order, so a throw of the native call cannot
            // leave a sphere the manager holds or a fog sphere nothing points at.
            if (sphere != null)
            {
                var volume = sphere;
                TryIgnore("enemy.area-volume.place-cleanup", () => EffectVolumeManager.UnregisterVolume(volume));
            }
            if (allocated && fog != null) Deallocate(fog, "enemy.area-volume.place-fog-cleanup");
            return AreaVolumePlacement.Refused;
        }
        return new AreaVolumePlacement(new Volume(sphere, allocated ? fog : null, maximum), allocated);
    }

    /// <inheritdoc/>
    public void Move(object body, IReadOnlyList<double> position)
    {
        if (body is not Volume volume) return;
        var where = new Vector3((float)position[0], (float)position[1], (float)position[2]);
        if (volume.Sphere != null && !volume.SphereReleased) volume.Sphere.position = where;
        if (volume.Fog != null && !volume.FogReleased) volume.Fog.SetPositionRange(where, volume.Range);
    }

    /// <inheritdoc/>
    public void Release(object body)
    {
        if (body is not Volume volume) return;
        List<Exception>? failures = null;
        if (volume.Sphere != null && !volume.SphereReleased)
        {
            try
            {
                EffectVolumeManager.UnregisterVolume(volume.Sphere);
                volume.SphereReleased = true;
            }
            catch (Exception error)
            {
                NativeFault.Note("enemy.area-volume.release", error);
                (failures ??= new()).Add(error);
            }
        }
        if (volume.Fog != null && !volume.FogReleased)
        {
            try
            {
                volume.Fog.Deallocate();
                volume.FogReleased = true;
            }
            catch (Exception error)
            {
                NativeFault.Note("enemy.area-volume.release-fog", error);
                (failures ??= new()).Add(error);
            }
        }
        if (failures != null) throw new AggregateException("Native area volume restoration is incomplete.", failures);
    }

    /// <summary>The `contents` parameter as the game's own enum. A value outside the shared vocabulary is refused
    /// by name rather than mapped onto a neighbouring member.</summary>
    private static bool TryContents(string member, out eEffectVolumeContents contents)
    {
        switch (member)
        {
            case "all": contents = eEffectVolumeContents.All; return true;
            case "health": contents = eEffectVolumeContents.Health; return true;
            case "infection": contents = eEffectVolumeContents.Infection; return true;
            default: contents = eEffectVolumeContents.All; return false;
        }
    }

    /// <summary>The `modification` parameter as the game's own enum.</summary>
    private static bool TryModification(string member, out eEffectVolumeModification modification)
    {
        switch (member)
        {
            case "inflict": modification = eEffectVolumeModification.Inflict; return true;
            case "shield": modification = eEffectVolumeModification.Shield; return true;
            default: modification = eEffectVolumeModification.Inflict; return false;
        }
    }

    /// <summary>One volume's end: the sphere leaves the manager's own list, or the call cannot be made. A manager
    /// that already dropped the sphere cannot stop the caller's cleanup, but a native failure remains observable.</summary>
    private static void Deallocate(FogSphereAllocator fog, string siteId) => TryIgnore(siteId, fog.Deallocate);

    private static void TryIgnore(string siteId, Action action)
    {
        try { action(); } catch (Exception error) { NativeFault.Note(siteId, error); }
    }
}
