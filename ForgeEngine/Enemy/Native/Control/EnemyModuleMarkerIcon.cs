using System;
using ForgeEnemy.Profile;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// The tag marker's icon (`forge.eec.Model.Marker.Icon`). The marker's enemy part (<c>NavMarker.m_enemySubObj</c>,
/// dump.cs 552446) draws the game's own sprite <c>symbol_hostile</c> through a <c>SpriteRenderer</c>; the icon swaps
/// that renderer's sprite for another sprite the game already loaded (the filled triangle, circle and square) and keeps
/// the drawn size: the new sprite is scaled so its bounds match the vanilla sprite's. The diamond is the square sprite
/// turned 45 degrees about the view axis and shrunk to its diagonal, because the game has no diamond sprite.
///
/// The sprites are found by name through ForgeUI (<c>UIKit.GameSprite</c>: once per name, kept). A sprite the game has not loaded, or a marker with no renderer showing the vanilla sprite, is refused by
/// name and the marker keeps its own icon. The marker object itself belongs to the game, so nothing but the renderer's
/// sprite, scale and rotation is written, and a renderer that already shows the wanted sprite is left alone.
/// </summary>
internal sealed partial class EnemyModule
{
    private bool SwapMarkerIcon(EnemyLookLife life, NavMarkerComponent? part, EnemyMarkerIcon icon)
    {
        if (part == null)
        {
            LookFailed(life, "marker-icon-renderer-missing", "m_enemySubObj");
            return false;
        }
        string spriteName = EnemyMarkerIcons.SpriteName(icon)!;
        var wanted = ForgeUI.UIKit.GameSprite(spriteName);
        if (wanted == null)
        {
            LookFailed(life, "marker-icon-sprite-missing", spriteName);
            return false;
        }
        int showing = 0;
        foreach (var renderer in part.gameObject.GetComponentsInChildren<SpriteRenderer>(true)) // Component.get_gameObject, 267895
        {
            var current = renderer.sprite;
            if (current == null) continue;
            string currentName = current.name;
            if (currentName == spriteName) { showing++; continue; }
            if (currentName != EnemyMarkerIcons.VanillaSprite) continue;
            var from = current.bounds.size; // Sprite.get_bounds
            var to = wanted.bounds.size;
            float fromSide = Math.Max(from.x, from.y), toSide = Math.Max(to.x, to.y);
            if (!(fromSide > 0f) || !(toSide > 0f))
            {
                LookFailed(life, "marker-icon-sprite-missing", spriteName + " has no size");
                return false;
            }
            float fit = fromSide / toSide * EnemyMarkerIcons.FitScale(icon);
            renderer.sprite = wanted; // SpriteRenderer.set_sprite
            var transform = renderer.transform;
            var scale = transform.localScale;
            transform.localScale = new Vector3(scale.x * fit, scale.y * fit, scale.z);
            float turn = EnemyMarkerIcons.TurnDegrees(icon);
            if (turn != 0f) transform.localRotation = transform.localRotation * Quaternion.AngleAxis(turn, new Vector3(0f, 0f, 1f));
            showing++;
        }
        if (showing == 0)
        {
            LookFailed(life, "marker-icon-renderer-missing", "SpriteRenderer " + EnemyMarkerIcons.VanillaSprite);
            return false;
        }
        return true;
    }
}
