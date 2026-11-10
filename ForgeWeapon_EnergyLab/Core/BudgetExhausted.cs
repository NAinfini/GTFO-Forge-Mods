using System;

namespace ForgeWeaponEnergyLab;

/// <summary>A fixed-size query or table ran out of room, so its answer would be cut short. Whatever needed that answer (one blast, one
/// projectile, one chain) is dropped and the error is logged once; the gun itself keeps working.</summary>
internal sealed class BudgetExhausted : InvalidOperationException
{
    public BudgetExhausted(string message) : base(message) { }
}
