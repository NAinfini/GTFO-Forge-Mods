using System.Collections.Generic;

namespace ForgeOrdnance;

/// <summary>The host's book of claimable things lying in drop pods (a heavy weapon on a rack): which are open to take, and which
/// player holds a reservation on one. A reservation never consumes the thing: it ends in <see cref="Confirm"/> (taken) or
/// <see cref="Cancel"/> (open again). What a player does with it afterwards is the claiming package's own business.</summary>
internal sealed class ClaimBook
{
    private readonly Dictionary<(ulong Summoner, uint ThrowId, int Bay), (ulong Player, uint Attempt)> _claims = new();
    private readonly HashSet<(ulong, uint, int)> _open = new();

    public void Open(ulong summoner, uint throwId, int bay = 0) => _open.Add((summoner, throwId, bay));

    public bool IsOpen(ulong summoner, uint throwId, int bay = 0) => _open.Contains((summoner, throwId, bay));

    /// <summary>Reserves it for <paramref name="player"/>'s <paramref name="attempt"/>: only when it is open, not reserved, and the
    /// player holds no other reservation.</summary>
    public bool TryClaim(ulong summoner, uint throwId, int bay, ulong player, uint attempt)
    {
        var key = (summoner, throwId, bay);
        if (attempt == 0 || !_open.Contains(key) || _claims.ContainsKey(key)) return false;
        foreach (var claim in _claims.Values) if (claim.Player == player) return false;
        _claims[key] = (player, attempt);
        return true;
    }

    public bool Matches(ulong summoner, uint throwId, int bay, ulong player, uint attempt)
        => _claims.TryGetValue((summoner, throwId, bay), out var claim) && claim == (player, attempt);

    /// <summary>The reservation becomes a taking: the thing is no longer open. False when the reservation is not this one.</summary>
    public bool Confirm(ulong summoner, uint throwId, int bay, ulong player, uint attempt)
    {
        if (!Matches(summoner, throwId, bay, player, attempt) || !_open.Remove((summoner, throwId, bay))) return false;
        _claims.Remove((summoner, throwId, bay));
        return true;
    }

    public bool Cancel(ulong summoner, uint throwId, int bay, ulong player, uint attempt)
        => _open.Contains((summoner, throwId, bay)) && Matches(summoner, throwId, bay, player, attempt) && _claims.Remove((summoner, throwId, bay));

    public bool IsReserved(ulong summoner, uint throwId, int bay = 0) => _claims.ContainsKey((summoner, throwId, bay));

    public void Close(ulong summoner, uint throwId, int bay = 0)
    {
        _open.Remove((summoner, throwId, bay));
        _claims.Remove((summoner, throwId, bay));
    }

    public void Clear() { _claims.Clear(); _open.Clear(); }
}
