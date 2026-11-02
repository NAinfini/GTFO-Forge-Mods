using System;

namespace ForgeOrdnanceFrames;

/// <summary>The rounds of the gun in the special slot as the pilot's cockpit shows them: a magazine, a reserve, the magazine size and the most the reserve holds (0: not known here).</summary>
public readonly record struct SlotRounds(int Clip, int Reserve, int ClipSize, int ReserveMax);

/// <summary>How the suit's stock sits in the special slot, and what the slot is worth while a swap is on its way. The game assembles
/// the new gun a moment after it is asked for and shows it empty until its rounds are written into it; the loan already knows what
/// they will be, so the cockpit shows that stock from the first frame of the swap and never a zero in between.</summary>
public static class SuitGunStock
{
    /// <summary>The split of the suit gun's stock: <paramref name="clip"/> rounds in the magazine (at most <paramref name="maxClip"/>) and the rest
    /// in the game's pack (at most <paramref name="packCap"/>); what does not fit waits as overflow and tops the pack up as it drains.</summary>
    public static (int Clip, int Pack, int Overflow) Split(int clip, int reserve, int maxClip, int packCap)
    {
        var loaded = Math.Min(clip, Math.Max(0, maxClip));
        var rest = clip - loaded + reserve;
        var pack = Math.Min(rest, Math.Max(0, packCap));
        return (loaded, pack, rest - pack);
    }

    /// <summary>The rounds of the gun that is on its way into the special slot (the loan's pending swap), or null when no swap is pending.
    /// <paramref name="clipSize"/> is the suit gun's magazine size (0 until it was first in the slot), <paramref name="costOfBullet"/> the
    /// game's ammunition cost of one round of the slot's pool (the own weapon's kept pool is in that unit), <paramref name="suitStock"/> all the rounds the suit starts with.</summary>
    public static SlotRounds? Incoming(SuitGunLoan loan, int clipSize, float costOfBullet, int suitStock)
    {
        switch (loan.Pending)
        {
            case LoanGun.Suit:
            {
                var clip = clipSize > 0 ? Math.Min(loan.SuitClip, clipSize) : loan.SuitClip;
                var reserve = loan.SuitClip - clip + loan.SuitReserve;
                return new SlotRounds(clip, reserve, clipSize, Math.Max(suitStock - Math.Max(clipSize, clip), reserve));
            }
            case LoanGun.Own when loan.Own is { } own:
                return new SlotRounds(own.Clip, costOfBullet > 0f ? (int)MathF.Round(own.Pool / costOfBullet) : 0, 0, 0);
            default:
                return null;
        }
    }
}
