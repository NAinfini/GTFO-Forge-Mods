using ForgeAudio;

namespace ForgeOrdnance.Frames;

/// <summary>The suit gun's own sounds, heard by the local pilot only: the start and the end of a reload and a trigger pull on an empty
/// magazine. ForgeAudio raises those actions on the weapon the suit gun is claimed on (<see cref="MechGun"/> claims it when the gun
/// settles in the slot and lets go when it leaves); the cues are this package's. Nothing is looked up by name while playing.</summary>
internal sealed class MechGunSound : IWeaponActionHandler
{
    private const WeaponAction All = WeaponAction.ReloadStart | WeaponAction.ReloadDone | WeaponAction.DryFire;
    private static readonly MechGunSound Handler = new();
    private static WeaponActionClaim? _claim;

    /// <summary>The suit gun <paramref name="gun"/> settled in the slot.</summary>
    internal static void Claim(Item gun)
    {
        Release();
        _claim = AudioKit.ClaimWeaponActions(Plugin.Id, gun, All, Handler);
    }

    /// <summary>The suit gun left the slot.</summary>
    internal static void Release()
    {
        _claim?.Dispose();
        _claim = null;
    }

    public void Play(WeaponAction action)
    {
        switch (action)
        {
            case WeaponAction.ReloadStart: FrameAudio.Local(FrameSound.ReloadStart); break;
            case WeaponAction.ReloadDone: FrameAudio.Local(FrameSound.ReloadDone); break;
            case WeaponAction.DryFire: FrameAudio.Local(FrameSound.DryFire); break;
        }
    }
}
