using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEnemy.Native;

/// <summary>The one kind-level read this provider answers besides a snapshot: where one of its own lives stands.
/// It is the `EntityZones` responder the kernel's own zone read asks, so every zone question about a `gtfo.enemy`
/// — the `where` value row, a zone-filtered selector, another provider comparing places — arrives here and nowhere
/// else. The native chain is the shared <see cref="GameCourseNodes"/> read the Player provider uses too.
///
/// A life this provider cannot place is a read that could not be made and is refused with
/// <see cref="ZoneUnknownCode"/>, never answered with a bare null: a null is the same "unknown" to the kernel, and a
/// selector that read either as "outside the zone I asked about" would quietly drop the entity from a set it may
/// well belong to.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The refusal this provider's zone read makes when it cannot place a life it owns. The kernel
    /// reports the code as this provider spelled it, so an unreadable life stays tellable apart from one that
    /// stands outside every zone.</summary>
    internal const string ZoneUnknownCode = "enemy-zone-unknown";

    /// <summary>The zone one tracked enemy life stands in: the zone its own course node belongs to.</summary>
    private EntityReference? ZoneOfEnemy(EntityReference reference)
        => GameCourseNodes.Zone(Tracked(reference), _kernel.WorldEpoch, ZoneUnknownCode, error => EnemyLog.Contained(error));

    /// <summary>Which area of its own zone one tracked enemy life stands in. A life this provider cannot place
    /// refuses both reads with the same code.</summary>
    private int? AreaOfEnemy(EntityReference reference)
        => GameCourseNodes.Area(Tracked(reference), ZoneUnknownCode, error => EnemyLog.Contained(error));

    private Enemies.EnemyAgent Tracked(EntityReference reference)
        => Resolve(reference) is { } entry ? entry.Enemy
            : throw new RuntimeContractException(ZoneUnknownCode, "This provider does not track that life.");
}
