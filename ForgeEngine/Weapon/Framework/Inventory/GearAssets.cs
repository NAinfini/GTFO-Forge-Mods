namespace ForgeWeapon;

/// <summary>Whether the assets the game reads while it assembles a gear (the muzzle flash prefab its effects setup indexes)
/// are loaded. The game only loads those for the gear a level started with, so any other gear must have them loaded first.</summary>
internal enum GearAssetState { Ready, Loading, Failed }

/// <summary>One look at a gear's assembly assets. `Detail` names the asset or native fact behind a `Failed`.</summary>
internal readonly record struct GearAssets(GearAssetState State, string Detail = "");
