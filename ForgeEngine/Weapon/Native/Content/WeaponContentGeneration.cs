using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeWeapon.Native;

/// <summary>The weapon domain's part of a content generation (contract I-RELOAD D2): every generation's weapon entries start again
/// from the field documents read at Start and an empty model table; the gear catalogue replaces and withdraws its records with the
/// generation (<see cref="WeaponGearCatalogue"/>) and the lobby's loadouts are equipped again from the new records.</summary>
internal sealed partial class WeaponNativeSession : IContentEntryDomain
{
    // The field documents read at Start: every generation's entries start again from these.
    private WeaponFieldSet _baseFields = WeaponFieldSet.Empty;
    // The model owners the current generation's entries registered, removed when a later generation no longer has them.
    private HashSet<string> _entryModelOwners = new(StringComparer.Ordinal);
    private HashSet<string>? _nextModelOwners;

    public void BeginEntries(ContentEntryGeneration generation)
    {
        var (fields, parts, models, flashlights, noise, applier, owners) = (Fields, GearParts, Models, Flashlights, FireNoise, WeaponFieldHooks.Applier, _entryModelOwners);
        generation.OnUndo(() =>
        {
            Fields = fields; GearParts = parts; Models = models; Flashlights = flashlights; FireNoise = noise;
            if (applier != null) WeaponFieldHooks.Use(applier);
            _entryModelOwners = owners;
            _nextModelOwners = null;
        });
        Use(_baseFields, Project(_baseFields), Models.Cleared());
        _nextModelOwners = new HashSet<string>(StringComparer.Ordinal);
        WeaponGearCatalogue.BeginGeneration();
    }

    public void EndEntries(ContentEntryGeneration generation)
    {
        var withdrawnOwners = _entryModelOwners.Where(owner => !_nextModelOwners!.Contains(owner)).ToArray();
        _entryModelOwners = _nextModelOwners!;
        _nextModelOwners = null;
        if (withdrawnOwners.Length != 0)
            generation.OnRelease(() => { foreach (var owner in withdrawnOwners) NativeModelRuntime.Remove(owner); });
        WeaponGearCatalogue.EndGeneration(generation, Log);
    }
}
