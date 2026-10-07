namespace ForgeWeaponEnergyLabExperimental.Native;

// Sequence watermarks outlive the renderer, so re-arming cannot replay a launch or its presentation.
internal sealed class RemotePeerState
{
    internal uint Arm, LastLaunchSequence, LastVisualSequence;

    internal bool BeginLaunch(uint sequence)
    {
        if (sequence == 0 || LastLaunchSequence != 0 && unchecked((int)(sequence - LastLaunchSequence)) <= 0) return false;
        LastLaunchSequence = sequence;
        return true;
    }
}
