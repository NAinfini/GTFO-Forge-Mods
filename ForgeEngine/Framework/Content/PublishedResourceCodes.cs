namespace ForgeEngine.Framework;

/// <summary>
/// The fixed diagnostic codes one caller gives the refusals of the published-resource reader. Every code is a literal
/// the caller owns and registers; the reader never builds one. Each level names exactly the refusals its operation can
/// report, so a caller registers no code that cannot occur: a walk of the install reports <see cref="PathCode"/> and
/// <see cref="UnreadableCode"/>, a byte read adds <see cref="FileSizeCode"/>, a text read adds the two decoding refusals.
/// </summary>
public record PublishedDiscoveryCodes(string PathCode, string UnreadableCode);

/// <summary>The codes of a discovery that also reads every runtime resource it finds: the set over the cap, and one id
/// claimed with different revisions or content.</summary>
public sealed record PublishedResourceSetCodes(string PathCode, string UnreadableCode, string LimitCode, string DuplicateIdCode)
    : PublishedDiscoveryCodes(PathCode, UnreadableCode);

/// <summary>The codes of a bounded read of exact bytes.</summary>
public record PublishedBytesCodes(string PathCode, string UnreadableCode, string FileSizeCode)
    : PublishedDiscoveryCodes(PathCode, UnreadableCode);

/// <summary>The codes of a bounded read of a JSON document: strict UTF-8 text with valid syntax.</summary>
public sealed record PublishedTextCodes(string PathCode, string UnreadableCode, string FileSizeCode, string JsonCode, string Utf8Code)
    : PublishedBytesCodes(PathCode, UnreadableCode, FileSizeCode);

/// <summary>The refusals of the reader before a caller's codes name them.</summary>
internal enum PublishedRefusal { Path, Unreadable, FileSize, Json, Utf8 }
