using System;
using System.Reflection;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The map-object categories this provider addresses. A category is a field of an address and of an
/// observation, never a second entity namespace: one `gtfo.map_object` namespace holds every map object and the
/// category travels with the object. Door and terminal retain the shared `MapObjectKind` spelling; the
/// additional host names follow the website's address categories exactly, so an address and a plan's
/// `map-object` attachment category name one kind the same way.</summary>
internal static class MapObjectCategories
{
    public static readonly string Door = Name(MapObjectKind.Door);
    public static readonly string Terminal = Name(MapObjectKind.Terminal);
    public const string Sentry = "sentry";
    public const string Reactor = "reactor";
    public const string Uplink = "uplink";
    /// <summary>The random spawn rules (contract M16): the host is "the instances this rule produced", not one object.</summary>
    public const string ItemRule = "item-rule";
    public const string EnemyRule = "enemy-rule";
    public const string ModelRule = "model-rule";

    /// <summary>The rule id of a rule address `&lt;category&gt;/0/0/0/&lt;id&gt;`, or null when the text is not one of that category.</summary>
    public static string? RuleId(string category, string? address)
    {
        if (address == null) return null;
        string[] parts = address.Split('/');
        return parts.Length == 5 && parts[0] == category && parts[1] == "0" && parts[2] == "0" && parts[3] == "0"
            && SpawnRuleManifest.IdIsValid(parts[4]) ? parts[4] : null;
    }

    private static string Name(MapObjectKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary>Addresses for additional authored map hosts. An uplink uses its terminal's native serial. Reactors
/// have numeric keys but no entity event. A level sentry uses the author's system-object id.
/// Parsing a category does not claim that this world contains an instance or that it publishes a trigger.</summary>
internal static class MapObjectHostAddress
{
    public static MapObjectReference? TryParse(string? text)
    {
        if (text == null) return null;
        string[] parts = text.Split('/');
        if (parts.Length != 5 || !Coordinate(parts[1]) || !Coordinate(parts[2])
            || !Coordinate(parts[3]) || parts[2] is not ("0" or "1" or "2")) return null;
        string category = parts[0];
        if (category is MapObjectCategories.Reactor or MapObjectCategories.Uplink
            && !Coordinate(parts[4])) return null;
        return category switch
        {
            MapObjectCategories.Reactor or MapObjectCategories.Uplink
                => MapObjectReference.TryParse(text, category, MapObjectReference.IsCoordinate),
            MapObjectCategories.Sentry => MapObjectReference.TryParse(text, category, SentryKey),
            _ => null
        };
    }

    private static bool Coordinate(string text)
        => text.Length != 0 && (text.Length == 1 || text[0] != '0')
            && int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value) && value >= 0;

    private static bool SentryKey(string key)
    {
        const string prefix = "sentry-";
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || key.Length < prefix.Length + 8
            || key.Length > prefix.Length + 48) return false;
        for (int i = prefix.Length; i < key.Length; i++)
            if (key[i] is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
        return true;
    }
}

/// <summary>Whether a key segment is one a category writes. Each category owns its key grammar, because the
/// two keys are different kinds of name: a door's is the fixed role token `security`, a terminal's is a
/// zero-based placement index.</summary>
internal delegate bool MapObjectKeyIsValid(string key);

/// <summary>
/// One map object's address, built only from keys the authoring side can derive before the level exists — this
/// is the format the plan `attachments[]` `map-object` reference and the website use, so both sides agree on
/// one spelling.
///
///   &lt;category&gt;/&lt;dimension&gt;/&lt;layer&gt;/&lt;zone&gt;/&lt;key&gt;
///
/// `category` is the lower-case name of a `MapObjectKind` value. `dimension`, `layer` and `zone` are the
/// coordinates of the zone the object belongs to, as native values: `dimension` is `LG_Zone.m_dimensionIndex`,
/// `layer` is `LG_Zone.m_layer.m_type`'s own ordinal (main/secondary/third), and `zone` is
/// `LG_Zone.LocalIndex`. Every coordinate is a decimal written without a leading zero; a coordinate that did
/// not read yields no address at all, so an address that exists always names a real zone.
///
/// `key` names the object inside that zone. A door carries the closed vocabulary token `security`: the zone's
/// entrance gate, which is at most one per zone because a zone has exactly one entrance. A terminal carries
/// the zero-based index of its placement in the zone's own terminal list, so several terminals in one zone are
/// distinct without any id the level builder hands out.
///
/// None of these keys is a runtime identifier: the door mapper id and the terminal sync id are assigned while
/// the level is being built, which is why neither is part of an address any more.
///
/// Only the category's own address factory builds one, and a factory refuses to build an address from keys
/// that do not read, so an address that exists always names an object.
///
/// An address never carries a native instance: reading an instance's address re-reads these keys from that
/// instance, so an id built for one address can never answer for another, and a level change invalidates an
/// earlier id through the kernel's world epoch instead of through a table this provider would clear.
/// </summary>
internal sealed record MapObjectReference(string Category, string Dimension, string Layer, string Zone,
    string Key)
{
    private string? CompactText { get; init; }

    public override string ToString() => CompactText ?? Category + "/" + Dimension + "/" + Layer + "/" + Zone + "/" + Key;

    /// <summary>An authored object whose id is already globally unique does not need invented zone coordinates.
    /// The compact spelling remains a map-object address and still carries the category separately for attachment
    /// matching; only categories that own such an author id may call this factory.</summary>
    internal static MapObjectReference Compact(string category, string key)
        => new(category, "", "", "", key) { CompactText = category + "/" + key };

    /// <summary>Reads an address of one expected category back, with the key grammar that category's own
    /// address factory writes. A five-segment address of that category is this provider's; every other string —
    /// another category, another shape, a coordinate that is not a plain decimal, a key the category does not
    /// use — is not, so a plan attachment or an entity id that something else built is refused rather than
    /// reinterpreted.</summary>
    public static MapObjectReference? TryParse(string? text, string expectedCategory, MapObjectKeyIsValid keyIsValid)
    {
        if (text == null) return null;
        var parts = text.Split('/');
        if (parts.Length != 5 || parts[0] != expectedCategory || !keyIsValid(parts[4])) return null;
        for (var i = 1; i != 4; i++) if (!IsCoordinate(parts[i])) return null;
        return new MapObjectReference(parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    /// <summary>A decimal coordinate written without a leading zero. The parser stops at the first non-digit,
    /// so a leading zero, a sign, a blank coordinate and a trailing non-digit all leave the text unparsed.
    /// The categories share it, because every coordinate segment of every address is written the same way.</summary>
    internal static bool IsCoordinate(string text) => TryCoordinate(text, out _);

    /// <summary>The same reading with the number it found, from a slice of an address.</summary>
    internal static bool TryCoordinate(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        int index = 0;
        for (; index != text.Length; index++)
        {
            char digit = text[index];
            if (digit is < '0' or > '9') break;
            if (index == 0 && digit == '0') { index++; break; }
            value = checked(value * 10 + (digit - '0'));
        }
        if (index != text.Length || text.Length == 0) return false;
        return value >= 0;
    }
}

/// <summary>
/// A map-object reference as two machines of one world pass it inside a message: the address in its own grammar, with the
/// zone coordinates and a numeric key as numbers instead of text, then the life. It names nothing a second way — what is
/// read back is the same entity id the address spells — and it carries no world: a message is of one world, and the
/// reader stamps that world on what it reads. Nothing here says the object exists. The kernel judges the reference like
/// any other (`IsEntityCurrent`), so an address this world does not hold, or an earlier life, is stale there and is never
/// another object.
///
///   byte shape, then
///   Text      the whole address, a length-prefixed UTF-8 string (an authored id: a placed model, a room's own door)
///   Security  dimension, layer, zone             `door/d/l/z/security`
///   Weak      dimension, layer, zone, serial     `door/d/l/z/weak&lt;serial&gt;`
///   Terminal  dimension, layer, zone, index      `terminal/d/l/z/&lt;index&gt;`
///   Uplink    dimension, layer, zone, serial     `uplink/d/l/z/&lt;serial&gt;`
///   then the life.
///
/// Every number is a 7-bit encoded integer that is not negative. The writer and the reader are UTF-8, as every body of
/// the engine's channel is.
/// </summary>
internal static class MapObjectWire
{
    private const string Prefix = EntityKinds.MapObject + ":";
    private const byte Text = 0, Security = 1, Weak = 2, Terminal = 3, Uplink = 4;

    internal static void Write(System.IO.BinaryWriter writer, EntityReference reference)
    {
        if (!EntityKinds.Is(reference, EntityKinds.MapObject)) throw new ArgumentException("Not a map object.", nameof(reference));
        var address = reference.Id.AsSpan(Prefix.Length);
        Span<int> numbers = stackalloc int[4];
        var shape = Shape(address, numbers);
        writer.Write(shape);
        if (shape == Text)
        {
            writer.Write7BitEncodedInt(System.Text.Encoding.UTF8.GetByteCount(address));
            writer.Write(address);
        }
        else for (var i = 0; i < (shape == Security ? 3 : 4); i++) writer.Write7BitEncodedInt(numbers[i]);
        writer.Write7BitEncodedInt64(reference.LifeEpoch);
    }

    /// <summary>The reference a body holds, in the world the body belongs to. A shape this build does not write, a negative
    /// number and a body that ends early are a <see cref="FormatException"/> or the reader's own end-of-stream.</summary>
    internal static EntityReference Read(System.IO.BinaryReader reader, long world)
    {
        var shape = reader.ReadByte();
        string id;
        if (shape == Text) id = Prefix + reader.ReadString();
        else
        {
            if (shape > Uplink) throw new FormatException("unknown map-object shape");
            int dimension = Count(reader), layer = Count(reader), zone = Count(reader);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            id = shape switch
            {
                Security => string.Create(culture, $"{Prefix}{MapObjectCategories.Door}/{dimension}/{layer}/{zone}/{MapObjectDoorAddress.Security}"),
                Weak => string.Create(culture, $"{Prefix}{MapObjectCategories.Door}/{dimension}/{layer}/{zone}/{MapObjectDoorAddress.WeakPrefix}{Count(reader)}"),
                Terminal => string.Create(culture, $"{Prefix}{MapObjectCategories.Terminal}/{dimension}/{layer}/{zone}/{Count(reader)}"),
                _ => string.Create(culture, $"{Prefix}{MapObjectCategories.Uplink}/{dimension}/{layer}/{zone}/{Count(reader)}")
            };
        }
        var life = reader.Read7BitEncodedInt64();
        if (life < 0) throw new FormatException("negative life");
        return new EntityReference(id, world, life);
    }

    private static int Count(System.IO.BinaryReader reader)
    {
        var value = reader.Read7BitEncodedInt();
        return value >= 0 ? value : throw new FormatException("negative number");
    }

    /// <summary>The numeric shape an address has and its numbers, or <see cref="Text"/> for every other address. A number
    /// is read exactly as an address writes one, so what the reader spells back is the same text.</summary>
    private static byte Shape(ReadOnlySpan<char> address, Span<int> numbers)
    {
        var slash = address.IndexOf('/');
        if (slash < 0) return Text;
        var category = address[..slash];
        bool door = category.SequenceEqual(MapObjectCategories.Door), terminal = category.SequenceEqual(MapObjectCategories.Terminal);
        if (!door && !terminal && !category.SequenceEqual(MapObjectCategories.Uplink)) return Text;
        var rest = address[(slash + 1)..];
        for (var i = 0; i < 3; i++)
        {
            slash = rest.IndexOf('/');
            if (slash < 0 || !MapObjectReference.TryCoordinate(rest[..slash], out numbers[i])) return Text;
            rest = rest[(slash + 1)..];
        }
        if (!door) return !MapObjectReference.TryCoordinate(rest, out numbers[3]) ? Text : terminal ? Terminal : Uplink;
        if (rest.SequenceEqual(MapObjectDoorAddress.Security)) return Security;
        return rest.StartsWith(MapObjectDoorAddress.WeakPrefix)
            && MapObjectReference.TryCoordinate(rest[MapObjectDoorAddress.WeakPrefix.Length..], out numbers[3]) ? Weak : Text;
    }
}

/// <summary>The address of one door: the coordinates of the zone it guards, and the fixed key its own zone
/// role gives it. A door the game's own level generation did not make the entrance gate of a zone has no
/// address at all. A bulkhead entrance uses its destination zone, including the target layer's root zone.</summary>
internal static class MapObjectDoorAddress
{
    public static readonly string Category = MapObjectCategories.Door;

    /// <summary>The one door key there is: a zone's entrance gate. A zone is entered through exactly one gate,
    /// so the entrance does not need a number, and a zone with no entrance gate has no `security` door.</summary>
    public const string Security = "security";

    /// <summary>The prefix of the key a weak door carries. A weak door is a door of the same `door` category —
    /// it is opened, closed and locked through the same door actions, so it is the same kind of object and an
    /// author wires one identity — but it is not a zone's entrance gate, so it cannot carry the `security` token:
    /// several weak doors stand in one zone and the entrance key names exactly one object. Its own per-instance
    /// serial follows the prefix, which is the one ordinal the level already assigns per object.</summary>
    public const string WeakPrefix = "weak";

    /// <summary>Reads a door address: this category, three coordinates and either the fixed `security` key or a
    /// `weak<serial>` key.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, IsDoorKey);

    /// <summary>Whether a key is one this category writes: the entrance token, or a weak door's serial.</summary>
    public static bool IsDoorKey(string key) => key == Security || ParseWeakSerial(key) != null || ParseWeakRoom(key) != null
        || InteriorDoorIdentity.ParseKey(key) != null;

    public const string WeakRoomPrefix = "weakroom-";
    public static MapObjectReference? CreateWeakRoom(int dimension, int layer, int zone, string instance)
        => dimension < 0 || layer is < 0 or > 2 || zone < 0 || string.IsNullOrWhiteSpace(instance) ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                WeakRoomPrefix + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(instance)).ToLowerInvariant());

    /// <summary>Reversible author room identity, never the native random door serial or a list index.</summary>
    public static string? ParseWeakRoom(string key)
    {
        if (key == null || !key.StartsWith(WeakRoomPrefix, StringComparison.Ordinal)) return null;
        string hex = key[WeakRoomPrefix.Length..];
        if (hex.Length is < 2 or > 1024 || hex.Length % 2 != 0) return null;
        foreach (char value in hex) if (value is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        try
        {
            var value = new System.Text.UTF8Encoding(false, true).GetString(Convert.FromHexString(hex));
            if (value.Length == 0 || value.Trim() != value) return null;
            foreach (char character in value) if (character is <= '\u001f' or '\u007f') return null;
            return value;
        }
        catch (ArgumentException) { return null; }
    }

    public static MapObjectReference? Create(int? dimension, int? layer, int? zone)
        => dimension == null || layer == null || zone == null
            ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                Security);

    /// <summary>The address of one weak door: the coordinates of the zone it stands in and its own serial inside
    /// that zone. The three coordinates are the same text an entrance address writes, so the zone an author
    /// filters on and the zone a weak door reports are one spelling.</summary>
    public static MapObjectReference? CreateWeak(int? dimension, int? layer, int? zone, int? serial)
        => dimension == null || layer == null || zone == null || serial == null || serial < 0
            ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                WeakKey(serial.Value));

    /// <summary>The serial one weak door's key carries, or null when the key is not that shape. A key with a
    /// leading zero, a sign, no digits or a second `weak` segment is refused rather than reinterpreted.</summary>
    public static int? ParseWeakSerial(string key)
        => key != null && key.StartsWith(WeakPrefix, StringComparison.Ordinal)
            && MapObjectReference.IsCoordinate(key[WeakPrefix.Length..])
            && int.TryParse(key[WeakPrefix.Length..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int serial)
            ? serial : null;

    private static string WeakKey(int serial) => WeakPrefix + serial.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Coordinate(int? value)
        => value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The address of one terminal: the coordinates of the zone it stands in, and its own zero-based index
/// in that zone's terminal list. The index is the placement's position, which is the one ordinal the authoring
/// side already fixes by ordering the zone's placements. Uplink terminals use their native serial instead:
/// objective terminals do not have an authoring-time ordinal in the ordinary placement list.</summary>
internal static class MapObjectTerminalAddress
{
    public static readonly string Category = MapObjectCategories.Terminal;

    /// <summary>Both address categories name the same native terminal type and use its existing events.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, MapObjectReference.IsCoordinate)
            ?? (MapObjectHostAddress.TryParse(text) is { Category: MapObjectCategories.Uplink } uplink ? uplink : null);

    public static MapObjectReference? CreateUplink(int dimension, int layer, int zone, int serial)
        => dimension < 0 || layer is < 0 or > 2 || zone < 0 || serial < 0 ? null
            : new MapObjectReference(MapObjectCategories.Uplink, Coordinate(dimension), Coordinate(layer),
                Coordinate(zone), Coordinate(serial));

    public static MapObjectReference? Create(int? dimension, int? layer, int? zone, int? placementIndex)
        => dimension == null || layer == null || zone == null || placementIndex == null
            ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                Coordinate(placementIndex));

    private static string Coordinate(int? value)
        => value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The address of one power generator: the coordinates of the zone it stands in and the level's own
/// serial for it — the same five segments a door and a terminal are addressed by (ruling 130.1). It is declared
/// here, with the other two categories' addresses, because this is where the grammar (`MapObjectReference`)
/// lives.
///
/// A placed core carries its explicit author identity. The native consumer resolves that identity against
/// build provenance before registration; a core serial and a zone search are never alternative identities.
/// A core of a native group that no author placement names carries its group's serial and its index in the group.
///
/// The category carries a second key form, exactly as the door category carries its `weak<serial>` one:
/// `group<serial>` names the generator *group* — `LevelGeneration.LG_PowerGeneratorCluster` — whose members the
/// group fact reports. A group is not an authoring-time object either, and a key form of its own rather than a
/// second category is what keeps one host (`generator`) and one address namespace while a group and a generator
/// that happen to carry the same serial stay two addresses.</summary>
internal static class MapObjectGeneratorAddress
{
    /// <summary>The category every generator address carries. It is a map-object category: a generator is one
    /// more kind of object the level places, addressed like a door and a terminal.</summary>
    public static readonly string Category = "generator";

    /// <summary>The prefix of the key a generator group carries.</summary>
    public const string GroupPrefix = "group";

    /// <summary>Reads a generator address: this category, three zone coordinates and the author's placement
    /// key for one core, a `member&lt;serial&gt;-&lt;index&gt;` key for an unplaced group member, or a `group&lt;serial&gt;`
    /// key for an unplaced group.
    /// Any other text — another category, another segment count, a coordinate with a leading zero —
    /// is not one of this category's addresses.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, IsGeneratorKey);

    /// <summary>The prefix of the key a group member with no author placement carries.</summary>
    public const string MemberPrefix = "member";

    /// <summary>Whether a key names an author placement, a native group or a native group member.</summary>
    public static bool IsGeneratorKey(string key)
        => ParseAuthorId(key) != null || ParseGroupSerial(key) != null || ParseMember(key) != null;

    /// <summary>The address of one core of a native group that no author placement names: the group's zone, its serial
    /// and the core's index in the group's own member array (`m_generators`), written as `member&lt;serial&gt;-&lt;index&gt;`.
    /// The native group setup fills that array in order from the level's build seed, so every peer generating the
    /// same level reads the same index (evidence/generator-placement-identity-20403457.json, clusterMemberAddress).</summary>
    public static MapObjectReference? CreateMember(int dimension, int layer, int zone, int serial, int index)
        => dimension < 0 || layer is < 0 or > 2 || zone < 0 || serial < 0 || index < 0 ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                MemberPrefix + Coordinate(serial) + "-" + Coordinate(index));

    /// <summary>The group serial and member index one member key carries, or null when the key is not that shape.</summary>
    public static (int Serial, int Index)? ParseMember(string? key)
    {
        if (key == null || !key.StartsWith(MemberPrefix, StringComparison.Ordinal)) return null;
        var parts = key[MemberPrefix.Length..].Split('-');
        return parts.Length == 2 && MapObjectReference.IsCoordinate(parts[0]) && MapObjectReference.IsCoordinate(parts[1])
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var serial)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            ? (serial, index) : null;
    }

    /// <summary>The author identity survives layout reordering and changes to the native serial. Zone placements
    /// carry author: ids; door/objective placements carry the explicit fixed object's item: id.</summary>
    public static string? AuthorKey(string? id)
    {
        if (id == null) return null;
        var prefix = id.StartsWith("author:", StringComparison.Ordinal) ? "author"
            : id.StartsWith("item:", StringComparison.Ordinal) ? "item" : null;
        if (prefix == null) return null;
        var token = id[(prefix.Length + 1)..];
        if (token.Length is < 1 or > 64) return null;
        foreach (var c in token) if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')) return null;
        return prefix + "-" + token;
    }

    public static string? ParseAuthorId(string key)
    {
        var prefix = key.StartsWith("author-", StringComparison.Ordinal) ? "author"
            : key.StartsWith("item-", StringComparison.Ordinal) ? "item" : null;
        if (prefix == null) return null;
        var id = prefix + ":" + key[(prefix.Length + 1)..];
        return AuthorKey(id) == key ? id : null;
    }

    public static MapObjectReference? CreateAuthor(int dimension, int layer, int zone, string id)
        => dimension < 0 || layer is < 0 or > 2 || zone < 0 || AuthorKey(id) is not { } key ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone), key);

    /// <summary>The serial one group's key carries, or null when the key is not that shape. A key with a leading
    /// zero, a sign, no digits or a second `group` segment is refused rather than reinterpreted.</summary>
    public static int? ParseGroupSerial(string? key)
        => key != null && key.StartsWith(GroupPrefix, StringComparison.Ordinal)
            && MapObjectReference.IsCoordinate(key[GroupPrefix.Length..])
            && int.TryParse(key[GroupPrefix.Length..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int serial)
            ? serial : null;

    /// <summary>Whether one key names a group rather than a single generator.</summary>
    public static bool IsGroupKey(string key) => ParseGroupSerial(key) != null;

    /// <summary>The address of one generator group: the coordinates of the zone its members stand in and its own
    /// serial, carried under the group key form so a group and a generator of one zone never share a text.</summary>
    public static MapObjectReference? CreateGroup(int? dimension, int? layer, int? zone, int? serial)
        => dimension == null || layer == null || zone == null || serial == null || serial < 0
            ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                GroupPrefix + Coordinate(serial));

    private static string Coordinate(int? value)
        => value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The native `eDoorStatus` as this provider publishes it: the reader never synthesizes a state, so
/// every value of the enum has a name and an unknown value is refused instead of reported as closed.
/// `interaction_phase` classifies only the statuses that are an interaction transition; a status that is not
/// one carries null rather than a phase the door was never in.</summary>
internal static class MapObjectDoorStatus
{
    /// <summary>Whether the native status is a locked state. A door whose own status says it is locked is
    /// locked whatever component put it there.</summary>
    public static bool IsLocked(int status) => status is 3 or 4 or 5 or 6 or 7 or 15;

    /// <summary>Whether the door's own status is the glue state it is held in
    /// (`eDoorStatus.GluedMax`, dump.cs:688678). Glue is an obstruction the door itself reports, not a lock: a
    /// plan that wants to know whether the way is blocked reads this rather than inferring it from `closed`.</summary>
    public static bool IsGlued(int status) => status == 12;

    /// <summary>Whether the door's own status is one of the two states where an attempt to open it stuck — in
    /// glue (`TryOpenStuckInGlue`) or against something broken (`TryOpenStuckBroken`), dump.cs:688679-688680. Both
    /// are the door failing to move rather than a state it settled in.</summary>
    public static bool IsStuck(int status) => status is 13 or 14;

    public static string Name(int status) => status switch
    {
        0 => "none",
        1 => "closed",
        2 => "closed_broken_cant_open",
        3 => "closed_locked_with_key_item",
        4 => "closed_locked_with_chained_puzzle_alarm",
        5 => "closed_locked_with_chained_puzzle",
        6 => "closed_locked_with_power_generator",
        7 => "closed_locked_with_no_key",
        8 => "chained_puzzle_activated",
        9 => "unlocked",
        10 => "open",
        11 => "destroyed",
        12 => "glued_max",
        13 => "try_open_stuck_in_glue",
        14 => "try_open_stuck_broken",
        15 => "closed_locked_with_bulkhead_dc",
        16 => "opening",
        _ => throw new RuntimeContractException("map-object-door-status", "Unknown door status value.")
    };

    /// <summary>The `interaction_phase` member the status itself is, as the wire index the port carries, or
    /// null for a status that is not an interaction transition. Locked statuses are the state a lock holds the
    /// door in, not an interaction this callback performed, so they carry no phase either.</summary>
    public static int? Phase(int status) => status switch
    {
        8 => MapObjectPhases.Requested,
        16 => MapObjectPhases.Started,
        2 or 9 or 10 or 11 or 12 or 13 or 14 => MapObjectPhases.Completed,
        _ => null
    };
}

/// <summary>The framework's `interaction_phase` members in the order the enum set declares them: an enum port
/// carries the member index, never the name, so this is the vocabulary the `phase` output publishes.</summary>
internal static class MapObjectPhases
{
    public const int Requested = 0, Started = 1, Completed = 2, Cancelled = 3, Failed = 4;

    public static string Name(int phase) => phase switch
    {
        Requested => "requested",
        Started => "started",
        Completed => "completed",
        Cancelled => "cancelled",
        Failed => "failed",
        _ => throw new RuntimeContractException("map-object-phase", "Unknown interaction phase index.")
    };
}

/// <summary>The framework's `execution_outcome` members in the order the enum set declares them, for the same
/// reason: the `outcome` output is a member index.</summary>
internal static class MapObjectOutcomes
{
    public const int Succeeded = 0, Partial = 1, Rejected = 2, Failed = 3, Cancelled = 4, Expired = 5;

    public static string Name(int outcome) => outcome switch
    {
        Succeeded => "succeeded",
        Partial => "partial",
        Rejected => "rejected",
        Failed => "failed",
        Cancelled => "cancelled",
        Expired => "expired",
        _ => throw new RuntimeContractException("map-object-outcome", "Unknown execution outcome index.")
    };
}

/// <summary>The native `TERM_State` as this provider publishes it, with the two facts the catalog asks a
/// terminal for: whether a player is at the terminal, and what the terminal's own state means as an outcome.</summary>
internal static class MapObjectTerminalState
{
    public static string Name(int state) => state switch
    {
        0 => "sleeping",
        1 => "awake",
        2 => "player_interacting",
        3 => "data_mining",
        4 => "hacked",
        5 => "code_puzzle",
        6 => "input_test",
        7 => "reactor_error",
        8 => "ask_to_play_log_audio",
        9 => "do_play_audio_file",
        10 => "audio_loop_error",
        11 => "ping",
        12 => "password_protected",
        13 => "enter_password",
        _ => throw new RuntimeContractException("map-object-terminal-state", "Unknown terminal state value.")
    };

    /// <summary>True exactly for the one state in which a player sits at the terminal; every other state,
    /// including the awake and idle states, is "no session".</summary>
    public static bool SessionActive(int state) => state == 2;

}

/// <summary>The native command enum's own name in this provider's vocabulary: `TERM_Command` is already the
/// accepted command, so `ViewSecurityLog` is published as `view_security_log` rather than as a number or a
/// second spelling a plan would have to match.</summary>
internal static class MapObjectTerminalCommand
{
    public const string NameUnavailable = "terminal-command-name-unavailable";
    public static bool IsUnique(int command) => command is >= 38 and <= 42;

    /// <summary>Read the interpreter's registered name; the game-independent publisher uses the same native member.</summary>
    public static string? RegisteredName(object terminal, int command)
    {
        if (!IsUnique(command)) return null;
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var type = terminal.GetType();
            var interpreter = type.GetProperty("m_command", members)?.GetValue(terminal)
                ?? type.GetField("m_command", members)?.GetValue(terminal);
            if (interpreter == null) return null;
            var method = interpreter.GetType().GetMethod("TryGetCommandName", members);
            var parameters = method?.GetParameters();
            if (parameters is not { Length: 2 } || !parameters[0].ParameterType.IsEnum
                || parameters[1].ParameterType != typeof(string).MakeByRefType()) return null;
            object?[] arguments = { Enum.ToObject(parameters[0].ParameterType, command), null };
            return method!.Invoke(interpreter, arguments) is true ? arguments[1] as string : null;
        }
        catch (Exception error) when (error is TargetInvocationException or ArgumentException or AmbiguousMatchException)
        { return null; }
    }

    /// <summary>A name lookup refusal, correlated to this terminal and binding through the existing I-DIAG values.</summary>
    public static DispatchResult RejectName(RuntimeModuleHandle registration, string binding, MapObjectReference address,
        int command, long world)
    {
        string entity = EntityKinds.MapObject + ":" + address;
        string eventId = entity + ":terminal-command-lookup:" + world.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + command.ToString(System.Globalization.CultureInfo.InvariantCulture);
        registration.Log(RuntimeLogLevel.Error, "compat." + NameUnavailable, new RuntimeLogFacts
        {
            Binding = binding,
            EventId = eventId,
            Entity = entity,
            Values = new[] { RuntimeLogValue.Of("status", "rejected"), RuntimeLogValue.Of("reason", NameUnavailable),
                RuntimeLogValue.Of("command", command) }
        });
        return new DispatchResult("rejected", NameUnavailable, eventId);
    }

    public static string Name(int command, string? registeredName = null) => IsUnique(command)
        ? !string.IsNullOrEmpty(registeredName) ? registeredName
            : throw new RuntimeContractException(NameUnavailable, "The terminal's registered command name could not be read.")
        : command switch
        {
            0 => "none",
            1 => "help",
            2 => "commands",
            3 => "cls",
            4 => "exit",
            5 => "open",
            6 => "close",
            7 => "activate",
            8 => "deactivate",
            9 => "empty_line",
            10 => "invalid_command",
            11 => "download_data",
            12 => "view_security_log",
            13 => "override",
            14 => "disable_alarm",
            15 => "locate",
            16 => "activate_beacon",
            17 => "find",
            18 => "show_list",
            19 => "query",
            20 => "ping",
            21 => "reactor_startup",
            22 => "reactor_verify",
            23 => "reactor_shutdown",
            24 => "warden_objective_special_command",
            25 => "terminal_uplink_connect",
            26 => "terminal_uplink_verify",
            27 => "terminal_uplink_confirm",
            28 => "list_logs",
            29 => "read_log",
            30 => "start",
            31 => "try_unlocking_terminal",
            32 => "warden_objective_gather_command",
            33 => "terminal_corrupted_uplink_connect",
            34 => "terminal_corrupted_uplink_verify",
            35 => "timed_connection_send",
            36 => "timed_connection_verify",
            37 => "used_command",
            43 => "info",
            _ => throw new RuntimeContractException("map-object-terminal-command", "Unknown terminal command value.")
        };
}

/// <summary>A map object re-read in one place, from the native members and nothing else. The module publishes
/// only values a read produced, and a read whose address stopped matching belongs to no publication at all.
/// Door, terminal, generator and group members are separate sections rather than one state string, so a hook never
/// computes a fact the reader would compute again; a zone is the authored record itself, because a trigger zone
/// is a map object the package declares rather than one the level generated. Every section after the first two is
/// optional, which is what keeps a reader that has no zone from restating the record.</summary>
internal sealed record MapObjectObservation(bool AddressMatches, MapObjectDoorSnapshot? Door,
    MapObjectTerminalSnapshot? Terminal, TriggerZone? Zone = null, MapObjectGeneratorSnapshot? Generator = null,
    MapObjectGeneratorGroupSnapshot? Group = null);

/// <summary>One door as its own native members report it: its status, whether it is locked, and the key its
/// own lock component currently requires. The key is read from the gate key item the lock holds — the
/// component type the door lock was set up with — never inferred from the status name.</summary>
internal sealed record MapObjectDoorSnapshot(string State, int Status, int? Phase, bool Locked, string Key)
{
    /// <summary>The state identity a publication is deduplicated by: the status and the lock reading
    /// together, so a repeated sync of one state never publishes twice while a lock change under an unchanged
    /// status still does.</summary>
    public string StateKey => State + "|" + (Locked ? "locked" : "unlocked") + "|" + Key;
}

/// <summary>One terminal's current display state and session. Command settlement is observed at the native
/// command's result entry, separately from these state fields.</summary>
internal sealed record MapObjectTerminalSnapshot(string State, int Status, bool SessionActive)
{
    public string StateKey => State + "|" + (SessionActive ? "session" : "idle");
}

/// <summary>One generator as its own native members report it: whether it holds a cell, and the counts of the
/// group it stands in. The powered reading is the generator's own replicated status — the game has exactly two,
/// `Powered` and `UnPowered` — and the counts are the derivation the group fact publishes: how many members of
/// the owning group read `Powered` against how many the group holds. A generator that stands under no group
/// reads `(0, 0)`, which the module publishes as "one of one" rather than as a completed group.</summary>
internal sealed record MapObjectGeneratorSnapshot(bool Powered, int Connected, int Total)
{
    /// <summary>The state identity a publication is deduplicated by: the direction the cell change went, so a
    /// repeated native sync of one state never publishes twice while a second insert/remove still does.</summary>
    public string StateKey => Powered ? "cell:powered" : "cell:unpowered";

}

/// <summary>One generator group as its own members report it: how many of them read `Powered` and how many the
/// group holds. The group's own native state carries a fog step and no count, so the counts are the derivation
/// and the module publishes the group fact only when every member is powered.</summary>
internal sealed record MapObjectGeneratorGroupSnapshot(int Connected, int Total)
{
    public string StateKey => "group:" + Connected.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "/" + Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The game-bound half of one map-object category. It owns the native reads and the instance identity: an
/// address is reversible from a native instance alone, so this provider keeps no world registry of objects
/// and a reference is re-read against its instance every time it is resolved. A source is one category's
/// reader, not an entity namespace of its own — the provider registers one map-object namespace and the
/// category travels inside the address.
/// </summary>
internal interface IMapObjectSource
{
    string Category { get; }

    /// <summary>The current address of a native instance, or null when the instance is not this category's or
    /// cannot be addressed yet — never a guessed address.</summary>
    MapObjectReference? TryAddress(object instance);

    /// <summary>Reads a written address back with this category's own key grammar, or null when the text is
    /// not one of this category's addresses. The category owns this, because the two keys are different kinds
    /// of name: an address of another category, or one whose key this category does not use, is not its.</summary>
    MapObjectReference? Parse(string text);

    /// <summary>The current observation of an instance, or null when the instance cannot be read in full.</summary>
    MapObjectObservation? Read(object instance);

    /// <summary>The object's own position, or null when it cannot be read. A snapshot carries exactly three
    /// finite coordinates, so a position that did not read yields no observation rather than a zero one.</summary>
    double[]? Position(object instance);

    /// <summary>Whether the native members this address is built from still read the same way.</summary>
    bool IsCurrentAddress(object instance, MapObjectReference address);

    /// <summary>Reports an unreadable instance once per cause.</summary>
    void Report(string message);
}

/// <summary>The subject of a published fact: the addressed map object, the native instance behind it and the
/// source that read both. Keeping the source and the instance on the subject is what keeps a fact from ever
/// being published through another category's reader or from another object's members.</summary>
internal sealed record MapObjectSubject(EntityReference Reference, MapObjectReference Address,
    IMapObjectSource Source, object Instance)
{
    public string Category => Address.Category;
}
