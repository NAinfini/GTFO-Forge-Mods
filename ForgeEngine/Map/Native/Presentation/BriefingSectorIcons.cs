
namespace ForgeMap.Native;

/// <summary>The briefing window's ALL CLEARED icon belongs to levels that have both an additional and a secondary layer
/// (<c>RundownManager.HasAllCompletetionPossibility</c>): the game positions and shows it only then. A progression mod that
/// replaces the window's show routine can blink it in on a one-layer level, where it lands on top of the MAIN icon. After the
/// show routine, hide it again there; the game's own routine and a level with both layers are left alone.</summary>
internal static class BriefingSectorIcons
{
    internal static void Shown(CellMenu.CM_ExpeditionWindow window, bool visible)
    {
        if (!visible || window.m_data == null || RundownManager.HasAllCompletetionPossibility(window.m_data)) return;
        var icon = window.m_sectorIconAllCompleted;
        if (icon == null) return;
        icon.StopBlink();
        icon.SetVisible(false);
    }
}
