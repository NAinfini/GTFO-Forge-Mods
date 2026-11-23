using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The one whole-profile read this package owns: `forge.query.weapon.profile`, the read of the spread and
/// recoil numbers one equipment instance's own block carries right now.
///
/// <b>What one reading is.</b> Nothing here is a Forge-side ledger: the numbers are the instance's own
/// `BulletWeaponArchetype.m_archetypeData` (`ShotgunConeSize`, `HipFireSpread`, `AimSpread`) and the
/// `RecoilDataBlock` its `RecoilDataID` names (`horizontalScale.Min`, `verticalScale.Min`, `spring`, `power.Min`),
/// read at the moment the query step runs. Those are exactly the members
/// <see cref="WeaponOverrideContract.SpreadCapability"/> and <see cref="WeaponOverrideContract.RecoilCapability"/>
/// write, so a plan that changed a weapon number reads back the value the instance really carries — including the
/// clone the override applier installed, which is why the read goes through the same instance lookup that write
/// uses and not through the game's shared block table.
///
/// <b>Why a block the read cannot answer is refused.</b> An instance this machine cannot name, an archetype whose
/// block pointer is gone, and a recoil half the block's own `RecoilDataID` does not resolve are all refusals with
/// their own codes. A profile that answered zeros would read exactly like a weapon whose spread really is zero,
/// and the read's whole purpose is to let a plan branch on the value it wrote.
///
/// <b>Scope.</b> The named rows write host-side and are not copied to a client, so only the host's instance
/// carries what a plan wrote; the read answers whatever block the instance on this machine holds and claims
/// nothing about another peer. The read writes nothing: no pointer swap, no clone, no cache.
///
/// The body lives in <see cref="WeaponProfileReads"/> so this declaration travels into every process that
/// registers this provider, and the native half supplies the source that really reaches the instance. A process
/// with no native half refuses the read by code rather than declaring a row nothing answers.</summary>
internal static class WeaponProfileReadContract
{
    /// <summary>The row's canonical id; the row itself is the generated block table's.</summary>
    public const string CapabilityId = Blocks.QueryWeaponProfile.Id;

    /// <summary>This provider's one binding of that row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.query.weapon.profile";

    /// <summary>The evaluator name, which is also the shape key: a registration cannot supply a body whose ports
    /// were never declared.</summary>
    public const string HandlerName = "gtfo.weapon.profile";

    /// <summary>The permission the read carries: it reads an equipment instance's own block, which is the same
    /// subject the wield and magazine reads already carry, so the name is not split per row.</summary>
    public const string ReadPermission = ModuleDefinition.WieldReadPermission;

    /// <summary>The ports the evaluator really reads: the equipment life the question is about and the seven
    /// numbers the instance's own block carries.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("equipment").Outputs("spread_cone", "spread_movement_scale", "spread_aim_scale",
            "recoil_horizontal", "recoil_vertical", "recoil_recovery", "recoil_camera_kick");

    /// <summary>The evaluator table, taken from the reads that own the body so a shape and its evaluator can
    /// never be registered apart.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => WeaponProfileReads.Evaluators();

    /// <summary>The row's answer: one reading of the block, under the reviewed graph's own port names.</summary>
    public static void Answer(WeaponProfileSample sample, EvaluationOutputs outputs)
    {
        outputs.Number(0, sample.SpreadCone);
        outputs.Number(1, sample.SpreadMovementScale);
        outputs.Number(2, sample.SpreadAimScale);
        outputs.Number(3, sample.RecoilHorizontal);
        outputs.Number(4, sample.RecoilVertical);
        outputs.Number(5, sample.RecoilRecovery);
        outputs.Number(6, sample.RecoilCameraKick);
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, HandlerName, "observe", new[] { ReadPermission }, Shape)
    };
}
