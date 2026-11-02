namespace ForgeOrdnance;

/// <summary>The interaction sounds Ordnance plays through ForgeAudio. The order is the index into the cue table in
/// <see cref="OrdnanceCues"/>; each sound's minimum gap is its <c>minInterval</c> in <c>audio/cues.json</c>. Key* and the
/// other <c>Local</c> sounds are heard by the local player only, the rest are world events every client triggers from its
/// own synchronised state.</summary>
internal enum OrdnanceSound
{
    KeyUp, KeyDown, KeyLeft, KeyRight, KeyWrong, CodeComplete, CallOpen, CallCancel, Denied,
    Click, SlotSelect, PickConfirm, PickClear, BeaconThrow,
    BeaconLand, BeaconTick, DropUnlatch, DropLeave, DropDebris,
}
