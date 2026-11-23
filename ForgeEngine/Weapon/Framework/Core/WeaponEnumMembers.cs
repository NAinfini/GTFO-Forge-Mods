using System;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The wire value of one member of a shared enum set. An enum port travels as the member's index in the set the port
/// names, and the kernel refuses a member name there as `invalid-enum`; the index is resolved from the SDK's own
/// table once per publisher (a static field) and never spelled a second time in this package. A member the set does
/// not have is a defect in this package, so it fails where the publisher is built rather than at the first fact.
/// </summary>
internal static class WeaponEnumMembers
{
    public static int Index(string set, string member)
    {
        var members = RuntimeEnumSets.Members(set);
        for (var index = 0; index < members.Count; index++)
            if (string.Equals(members[index], member, StringComparison.Ordinal)) return index;
        throw new InvalidOperationException("The SDK's " + set + " set has no member " + member + ".");
    }
}
