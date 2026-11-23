using System.Linq;
using System.Text;
using Enemies;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>ForgeEnemy's by-name entry to a live enemy's tag marker, for a driver with no keyboard (ForgeDevelopment's
/// <c>drive_cli.py call static:ForgeEnemy.Native.EnemyControl &lt;Method&gt;</c>). Every method returns at once with one line. Main thread only.</summary>
internal static class EnemyControl
{
    /// <summary>The living enemies nearest the local player first, up to <paramref name="count"/>, one line each: global id, data block id and distance.</summary>
    public static string List(int count = 10)
    {
        var me = ForgeCore.Players.Local;
        var from = me != null && !me.WasCollected ? me.Position : Vector3.zero;
        var rows = Object.FindObjectsOfType<EnemyAgent>().Where(e => e != null && !e.WasCollected && e.Alive)
            .OrderBy(e => Vector3.Distance(from, e.Position)).Take(count).ToArray();
        if (rows.Length == 0) return "no living enemies";
        var text = new StringBuilder();
        foreach (var e in rows)
        {
            if (text.Length > 0) text.Append('\n');
            text.Append("enemy ").Append(e.GlobalID).Append(" type=").Append(e.EnemyDataID).Append(" distance=").Append(Vector3.Distance(from, e.Position).ToString("0.0"));
        }
        return text.ToString();
    }

    /// <summary>Places the tag marker on enemy <paramref name="globalId"/> (the game's own <c>SyncPlaceNavMarkerTag</c>, which ForgeEnemy's marker icon, colour and blink follow)
    /// and reads it back: "tagged" with the sprite each marker renderer shows (the game's own "symbol_hostile" when the enemy has no marker icon), or "refused" with why.</summary>
    public static string Tag(int globalId)
    {
        var enemy = Object.FindObjectsOfType<EnemyAgent>().FirstOrDefault(e => e != null && !e.WasCollected && e.GlobalID == globalId);
        if (enemy == null) return "refused: no enemy " + globalId;
        if (!enemy.Alive) return "refused: enemy " + globalId + " is dead";
        enemy.SyncPlaceNavMarkerTag();
        var marker = enemy.m_tagMarker;
        if (marker == null || marker.WasCollected) return "refused: the game placed no marker";
        var part = marker.m_enemySubObj;
        var sprites = part == null ? System.Array.Empty<string>()
            : part.gameObject.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r.sprite != null).Select(r => r.sprite.name).ToArray();
        return "tagged enemy " + globalId + " sprites=" + (sprites.Length == 0 ? "none" : string.Join(",", sprites));
    }
}
