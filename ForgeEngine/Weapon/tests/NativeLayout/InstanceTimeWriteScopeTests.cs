namespace ForgeWeapon.Tests.NativeLayout;

// Managed audit-policy tests only: these do not load or impersonate the native game assemblies.
public sealed class InstanceTimeWriteScopeTests
{

    [Fact]
    public void field_writes_and_input_reads_require_the_complete_reviewed_call_site_multiset()
    {
        var sites = NativeLayoutTests.ReviewedFieldCallSites;
        Assert.Equal(46, sites.Length);
        Assert.True(NativeLayoutTests.ReviewedFieldCallSitesAreExact(sites.Reverse()));
        foreach (var index in Enumerable.Range(0, sites.Length))
        {
            Assert.False(NativeLayoutTests.ReviewedFieldCallSitesAreExact(sites.Where((_, i) => i != index)));
            Assert.False(NativeLayoutTests.ReviewedFieldCallSitesAreExact(sites.Append(sites[index])));
            var mutated = sites.ToArray();
            mutated[index] = sites[index].Split(" <- ")[0] + " <- ForgeWeapon.Native.UnrelatedWriter::Update";
            Assert.False(NativeLayoutTests.ReviewedFieldCallSitesAreExact(mutated));
            mutated[index] = "Unknown::set_Value <- " + sites[index].Split(" <- ")[1];
            Assert.False(NativeLayoutTests.ReviewedFieldCallSitesAreExact(mutated));
        }
    }

    [Fact]
    public void content_copy_and_publication_writes_reject_missing_duplicate_and_unrelated_sites()
    {
        var sites = NativeLayoutTests.ReviewedContentWriteSites;
        Assert.True(NativeLayoutTests.ReviewedContentWriteSitesAreExact(sites.Reverse()));
        foreach (var index in Enumerable.Range(0, sites.Length))
        {
            Assert.False(NativeLayoutTests.ReviewedContentWriteSitesAreExact(sites.Where((_, i) => i != index)));
            Assert.False(NativeLayoutTests.ReviewedContentWriteSitesAreExact(sites.Append(sites[index])));
            var changed = sites.ToArray();
            changed[index] = sites[index].Split(" <- ")[0] + " <- ForgeWeapon.Native.GearLoadoutSession::Offer";
            Assert.False(NativeLayoutTests.ReviewedContentWriteSitesAreExact(changed));
        }
    }

    private const string Applier = "ForgeWeapon.Native.WeaponOverrideApplier";
    private static bool Allowed(string caller, string member, string type = Applier)
        => NativeLayoutTests.InstanceTimeWriteIsScoped(type, caller, member);

    [Fact]
    public void unknown_members_and_nested_animation_mutations_are_not_admitted()
    {
        foreach (var member in new[]
        {
            "GameData.MeleeAnimationSetDataBlock::set_Unknown",
            "GameData.MeleeAnimationSetDataBlock::set_name",
            "GameData.MeleeAnimationSetDataBlock::set_persistentID",
            "GameData.MeleeAttackData::set_AttackLengthFrameTime",
            "ItemEquippable::set_Unknown",
            "Gear.MWS_ChargeUp::set_m_elapsed"
        })
            foreach (var caller in new[] { "CloneMelee", "ApplyMelee", "WithdrawMelee", "Install" })
                Assert.False(Allowed(caller, member));
    }

}
