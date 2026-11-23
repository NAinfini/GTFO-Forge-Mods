using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// One plan file after its single read: the file's bytes decoded once (strict UTF-8, the optional BOM dropped), parsed
/// once into a value that outlives the buffer, judged once by the runtime's document rules and hashed once. Every
/// later step — the host's content admission, the kernel's identity pass and the plan parser — reads this one object
/// instead of decoding, parsing and validating the same text again, so a refusal reaches each of them with the code
/// and message <see cref="RuntimeJson.Parse"/> of the same text would have given.
/// </summary>
internal sealed class PlanDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private readonly RuntimeContractException? refusal;

    private PlanDocument(string digest, int fileLength, JsonElement syntax, string? syntaxError, RuntimeContractException? refusal)
    { Digest = digest; FileLength = fileLength; Syntax = syntax; SyntaxError = syntaxError; this.refusal = refusal; }

    /// <summary>SHA-256 of the file's own bytes, including any BOM and outer whitespace.</summary>
    internal string Digest { get; }
    internal int FileLength { get; }
    /// <summary>The text parsed with the package JSON syntax (<see cref="PublishedResources.JsonOptions"/>) and no
    /// other judgement; default where <see cref="SyntaxError"/> is set. Host checks that only ask a question of a
    /// plan's text — not whether the runtime admits it — read this.</summary>
    internal JsonElement Syntax { get; }
    /// <summary>The message of the parse error under the package JSON syntax, or null when the text parses.</summary>
    internal string? SyntaxError { get; }

    /// <summary>A plan file as read from disk. Throws <see cref="DecoderFallbackException"/> where the bytes are not
    /// UTF-8; a parse error is kept in <see cref="SyntaxError"/> for the reader to refuse.</summary>
    internal static PlanDocument FromFile(ReadOnlyMemory<byte> file)
    {
        StrictUtf8.GetCharCount(file.Span);
        return Build(file, file.Span.StartsWith(Utf8Bom) ? file[Utf8Bom.Length..] : file);
    }

    /// <summary>A plan given as text. Never throws for the text's content: the refusal the kernel owes it is raised
    /// where the kernel asks for the plan.</summary>
    internal static PlanDocument FromText(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return Build(bytes, bytes);
    }

    private static PlanDocument Build(ReadOnlyMemory<byte> file, ReadOnlyMemory<byte> json)
    {
        JsonElement syntax = default;
        string? syntaxError = null;
        try
        {
            using var parsed = JsonDocument.Parse(json, PublishedResources.JsonOptions);
            // The clone is what outlives the read buffer and is its own document no caller can dispose.
            syntax = parsed.RootElement.Clone();
        }
        catch (JsonException error) { syntaxError = error.Message; }
        // The runtime's document rules, applied to the bytes in one pass and in the order RuntimeJson.Parse applies them.
        RuntimeContractException? refusal = null;
        try { RawJson.Validate(json.Span); }
        catch (RuntimeContractException error) { refusal = error; }
        return new PlanDocument(Convert.ToHexString(SHA256.HashData(file.Span)), file.Length, syntax, syntaxError, refusal);
    }

    /// <summary>The plan as the runtime admits it: the parsed value, or the refusal <see cref="RuntimeJson.Parse"/>
    /// gives the same text.</summary>
    internal JsonElement Plan()
    {
        if (refusal != null) throw new RuntimeContractException(refusal.Code, refusal.Message);
        return Syntax;
    }
}
