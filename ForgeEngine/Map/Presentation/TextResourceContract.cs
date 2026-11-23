using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The `text` resource kind: one original Text data block, addressed by the block's own `persistentID`. The block
/// is the game's own per-language carrier — every language column lives in it and the localization service holds
/// the language the machine is reading in — so a card that takes a text by id writes a reference to the block
/// rather than a string, and the text a player sees is resolved where it is shown.
///
/// The kind is not a second text format: the resource id is the block's `persistentID`, which is the same id the
/// website's resource catalog registers, and the reading end of the kind is the game's own localization
/// (<see cref="ForgeMap.Native.TextResourceTable"/>), never a Forge-side copy of the text.
///
/// <see cref="TextValue"/> and <see cref="TryText"/> are the one place the two entries a text card declares as a
/// pair are read. The contract's own pairing rule (`Web/src/forge/graph.ts`) refuses a node that fills both
/// entries and one that fills neither where one is required, so a plan that reaches a handler has exactly one of
/// them — and a request that arrives with both, compiled elsewhere, still takes the stored block, which is what
/// the card's own `meaning` says. Nothing here resolves text: this half is game-independent, so a case can drive
/// every branch without the game.
/// </summary>
internal static class TextResourceContract
{
    public const string ReferencePrefix = "forge:text:";
    /// <summary>The resource kind's own name in the shared resource-kind table.</summary>
    public const string ResourceKind = "text";
    /// <summary>The website's own schema id for one text resource instance.</summary>
    public const string ResourceSchema = "forge.resource.text";

    /// <summary>The compiled reference carried by authored map fields. The id names the exported vanilla text
    /// block and the primary language is the author's explicit fallback column. A numeric value is only present
    /// on runtime-composed counter text; persisted authored fields do not accept that suffix.</summary>
    public readonly record struct AuthoredReference(uint Id, string PrimaryLanguage, double? Value);

    public static bool TryParseReference(string? reference, bool allowValue, out AuthoredReference parsed)
    {
        parsed = default;
        if (reference == null || !reference.StartsWith(ReferencePrefix, StringComparison.Ordinal)) return false;
        var payload = reference.AsSpan(ReferencePrefix.Length);
        int valueSeparator = payload.IndexOf(':');
        if (valueSeparator >= 0 && !allowValue) return false;
        var header = valueSeparator < 0 ? payload : payload[..valueSeparator];
        int languageSeparator = header.IndexOf('@');
        if (languageSeparator <= 0 || languageSeparator == header.Length - 1) return false;
        if (!uint.TryParse(header[..languageSeparator], NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0)
            return false;
        string primary = header[(languageSeparator + 1)..].ToString();
        bool languageKnown = false;
        foreach (var member in RuntimeEnumSets.Members("text_language"))
            if (string.Equals(primary, member, StringComparison.Ordinal)) { languageKnown = true; break; }
        if (!languageKnown) return false;
        double? value = null;
        if (valueSeparator >= 0)
        {
            if (!double.TryParse(payload[(valueSeparator + 1)..], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double number)
                || !double.IsFinite(number)) return false;
            value = number;
        }
        parsed = new AuthoredReference(id, primary, value);
        return true;
    }

    /// <summary>The provider that owns the `text` kind, handed in by the half that can read the install's Text
    /// table. Apart from the kind's own name this contract decides nothing about a resource: enumeration and
    /// resolution belong to the half that holds the blocks, exactly as every other resource kind's do.</summary>
    public static RuntimeResourceProvider Provider(Func<IReadOnlyList<ResourceRef>> enumerate, Func<string, ResourceRef?> resolve)
        => RuntimeResourceProvider.Of(enumerate, resolve);

    /// <summary>
    /// One text a text card writes, as the entry it was read from: the literal text a plan computed, or the id of
    /// the original Text block the id entry named. Exactly one of the two is set, and <see cref="Stored"/> says
    /// which — the id zero is not used as a sentinel, because zero is a value an id can have and a form that reads
    /// it as "no block" would silently write an empty line instead.
    /// </summary>
    public readonly struct TextValue
    {
        private TextValue(string? literal, uint id, bool stored)
        {
            Literal = literal;
            Id = id;
            Stored = stored;
        }

        /// <summary>The literal text the plan computed, or null when this value is a stored block.</summary>
        public string? Literal { get; }
        /// <summary>The stored block's own `persistentID`, or zero when this value is the literal text.</summary>
        public uint Id { get; }
        /// <summary>Whether this value is the original Text block the id entry named rather than the literal.</summary>
        public bool Stored { get; }
        /// <summary>Whether this value carries a text at all: a stored block, or a literal that is not empty. A
        /// card that needs one refuses a request that has neither.</summary>
        public bool HasText => Stored || !string.IsNullOrEmpty(Literal);

        internal static TextValue OfLiteral(string? literal) => new(literal, 0, false);
        internal static TextValue OfBlock(uint id) => new(null, id, true);
    }

    /// <summary>
    /// The one two-entry read every text card makes, in one place. The stored block an id entry names is the text
    /// when that entry carries a `text` reference — a request that fills both entries was compiled elsewhere and
    /// the stored block still wins, which is the card's own precedence — and the literal the plan computed is the
    /// text otherwise. A text entry that carries neither is not an error here: an empty literal is what the
    /// calling card's own required-text rule refuses, with its own code.
    ///
    /// A reference of another kind, or an id that is not a block id, is `text-resource-missing`, the refusal a
    /// request answers with when its text entry named a stored block the row cannot turn into a text this machine
    /// can write: the row is told so by name instead of falling back to the literal or to an empty line, because a
    /// stored reference the author wired is not an absent one. A block id no Text block has is refused before dispatch: the plan's own compiled reference is
    /// resolved against this kind's provider at load, so it ends as the kernel's `stale-resource`.
    /// </summary>
    public static bool TryText(CommandContext context, HandlerSlot literal, HandlerSlot stored,
        out TextValue text, out string? missing)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TryResource(stored, out string kind, out string storedId))
        {
            if (kind != ResourceKind
                || !uint.TryParse(storedId, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            {
                text = default;
                missing = ResultCode.TextResourceMissing;
                return false;
            }
            text = TextValue.OfBlock(id);
            missing = null;
            return true;
        }
        text = TextValue.OfLiteral(context.Text(literal));
        missing = null;
        return true;
    }
}
