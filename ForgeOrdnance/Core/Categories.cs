using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeOrdnance;

/// <summary>One lobby tab a definition can sit under: its id (as written in a definition's <c>Category</c>), the display name,
/// the tab position and the default beam colour of its beacons.</summary>
public sealed class OrdnanceCategoryInfo
{
    internal OrdnanceCategoryInfo(string id, Func<string> name, int order, uint beamColour)
    {
        Id = id; NameOf = name; Order = order; BeamColour = beamColour;
    }

    public string Id { get; }
    public int Order { get; }
    /// <summary>The default beam colour (0xRRGGBB) of a definition in this category that gives no <c>BeamColour</c>.</summary>
    public uint BeamColour { get; }
    internal Func<string> NameOf { get; }
    /// <summary>The name shown on the tab and the card subtitle, in the player's language.</summary>
    public string Name => NameOf();
}

/// <summary>The categories known to this install. The platform keeps Supply, Defensive, Weapon and Offensive; a package that
/// brings its own kind (Frames' Exosuit) registers it with <see cref="Register"/> before definitions load.</summary>
internal sealed class CategoryRegistry
{
    public const string Supply = "Supply", Defensive = "Defensive", Weapon = "Weapon", Offensive = "Offensive";

    private readonly Dictionary<string, OrdnanceCategoryInfo> _all = new(StringComparer.Ordinal);

    public CategoryRegistry(Func<string, Func<string>> builtinName)
    {
        Register(Supply, builtinName(Supply), 0, 0x3FA9FF);
        Register(Defensive, builtinName(Defensive), 1, 0x7CFF8A);
        Register(Weapon, builtinName(Weapon), 2, 0xFFFFFF);
        Register(Offensive, builtinName(Offensive), 4, 0xFF6A1F);
    }

    /// <summary>The colour of a category the registry does not know (never reached for a parsed definition).</summary>
    public const uint UnknownBeamColour = 0xFF6A1F;

    /// <summary>Adds a category. Throws when the id is not a plain name (letters and digits) or is taken.</summary>
    public void Register(string id, Func<string> name, int order, uint beamColour)
    {
        if (string.IsNullOrEmpty(id) || !id.All(char.IsLetterOrDigit)) throw new ArgumentException("A category id is a plain name of letters and digits.", nameof(id));
        if (name == null) throw new ArgumentNullException(nameof(name));
        if (!_all.TryAdd(id, new OrdnanceCategoryInfo(id, name, order, beamColour & 0xFFFFFF))) throw new InvalidOperationException("Category '" + id + "' is already registered.");
    }

    public bool TryGet(string id, out OrdnanceCategoryInfo info) => _all.TryGetValue(id, out info!);

    /// <summary>Every category in tab order.</summary>
    public IEnumerable<OrdnanceCategoryInfo> Ordered => _all.Values.OrderBy(c => c.Order).ThenBy(c => c.Id, StringComparer.Ordinal);

    public uint BeamColour(string id) => _all.TryGetValue(id, out var info) ? info.BeamColour : UnknownBeamColour;
}
