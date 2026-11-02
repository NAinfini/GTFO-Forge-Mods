using System;
using System.Collections.Generic;
using SNetwork;
using UnityEngine;

namespace ForgeUI;

public sealed partial class UIKitMod
{
    /// <summary>
    /// Adds a tag to every teammate row of every teammates panel (<see cref="HudTeammates"/>, whichever mod made it): per player, a
    /// status picture and a short value at the row's right before the health, and a hairline share under the health. <paramref name="read"/>
    /// runs once per row per frame while a panel is drawn; return <see cref="TeammateTag.None"/> for a player the tag does not apply
    /// to, and the same string while the value is the same. A callback that throws is turned off (logged once).
    /// </summary>
    /// <param name="name">Unique and stable inside your mod, for example <c>Armour</c>.</param>
    /// <param name="read">The tag for one player.</param>
    /// <example><c>ui.AddTeammateTag("Armour", p =&gt; Suit(p) is { } s ? new TeammateTag { Bar = s.Armour } : TeammateTag.None);</c></example>
    public TeammateTagSource? AddTeammateTag(string name, Func<SNet_Player, TeammateTag> read)
    {
        if (read == null) throw new ArgumentNullException(nameof(read));
        var id = Full(name, "teammate tag");
        return id == null ? null : TeammateTags.Add(new TeammateTagSource(id, read));
    }
}

/// <summary>What one tag shows on one teammate's row. Unset parts draw nothing.</summary>
public struct TeammateTag
{
    /// <summary>Nothing for this player.</summary>
    public static TeammateTag None => default;

    /// <summary>A small status picture.</summary>
    public Sprite? Icon;
    /// <summary>A short value (a few characters).</summary>
    public string? Text;
    /// <summary>A hairline share under the health, 0..1.</summary>
    public float? Bar;
    /// <summary>Its colour; null takes the source's <see cref="TeammateTagSource.Colour"/>.</summary>
    public Color? Colour;

    internal readonly bool IsEmpty => Icon == null && string.IsNullOrEmpty(Text) && !Bar.HasValue;
}

/// <summary>One mod's teammate tag (see <see cref="UIKitMod.AddTeammateTag"/>).</summary>
public sealed class TeammateTagSource
{
    private readonly Func<SNet_Player, TeammateTag> _read;

    internal TeammateTagSource(string id, Func<SNet_Player, TeammateTag> read) { Id = id; _read = read; }

    /// <summary>The full ID, <c>modId/name</c>.</summary>
    public string Id { get; }

    /// <summary>Shown while true. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The colour of parts whose tag gives none. Default cyan.</summary>
    public Color Colour { get; set; } = UIKitColors.Faded(UIKitColors.Cyan, 0.85f);

    /// <summary>Takes the tag off every row and frees the name.</summary>
    public void Remove() => TeammateTags.Remove(this);

    internal bool Read(SNet_Player player, out TeammateTag tag)
    {
        tag = default;
        if (!Enabled) return false;
        try { tag = _read(player); }
        catch (Exception error)
        {
            Enabled = false;
            Plugin.Error("Teammate tag '" + Id + "' failed and is turned off: " + error);
            return false;
        }
        return !tag.IsEmpty;
    }
}

/// <summary>Every mod's teammate tags, in the order they were added.</summary>
internal static class TeammateTags
{
    internal static readonly List<TeammateTagSource> All = new();

    internal static TeammateTagSource? Add(TeammateTagSource source)
    {
        for (var i = 0; i < All.Count; i++)
            if (string.Equals(All[i].Id, source.Id, StringComparison.Ordinal))
            {
                Plugin.Error(source.Id + ": teammate tag not added: the name is already taken.");
                return null;
            }
        All.Add(source);
        return source;
    }

    internal static void Remove(TeammateTagSource source) => All.Remove(source);
}
