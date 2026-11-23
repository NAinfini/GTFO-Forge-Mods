using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeWeapon.Native;

/// <summary>Loadout policies, read once at plugin start into one install-wide snapshot.
///
/// A package writes one file per rundown at `plugins/&lt;package&gt;/forge/loadout.forge` — the same one-level package
/// rule plans and gear parts use. The file is the website's own canonical export, `JSON.stringify(policy) + "\n"`,
/// and a file is accepted only when the bytes on disk are that canonical form byte for byte: the pins inside
/// describe the very bytes that carry them, so a lenient parse would let a digest and its content drift apart.
/// Every rejection is per file, carries one reason, and leaves the other files alone; the snapshot is immutable
/// once loaded — no watch, no reload, restart to change it.
///
/// All three pins the strict contract requires (`gameAssemblySha256`, `plugins`, `sources`) are checked here before
/// a file is accepted. The bytes they name are handed in as <see cref="LoadoutPinSource"/>, so this type is a pure
/// function of a root directory and that source: no game, loader or host type is touched, and a test can hash
/// fixture bytes instead of a real install.</summary>
internal static class LoadoutPolicyData
{
    /// <summary>One policy file's byte budget, the website's `maxPolicyBytes`.</summary>
    internal const int MaximumFileBytes = PublishedResources.MaximumFileBytes;
    /// <summary>The contract's pinned plugin band and pinned source cap.</summary>
    internal const int MinimumPlugins = 2, MaximumPlugins = 64, MaximumSources = 4096;
    /// <summary>The bytes all pinned sources and plugins of one policy may hash to, together: the same budget a
    /// single published file has.</summary>
    internal const long MaximumPinnedBytes = PublishedResources.MaximumFileBytes;
    /// <summary>Entries across all three slots; an explicit empty array leaves that slot empty.</summary>
    internal const int MaximumSlotEntries = 256;
    private const string PolicyFormat = "gtfo-forge-loadout";
    private const string PolicyFileName = "loadout.forge";
    private const string Shape = "loadout-policy-shape";
    private static readonly PublishedTextCodes Codes = new("loadout-policy-path", "loadout-policy-unreadable",
        "loadout-policy-size", "loadout-policy-json", "loadout-policy-utf8");
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly string[] PolicyKeys = { "format", "projectId", "rundownId", "gameAssemblySha256", "plugins", "sources", "slots" };
    private static readonly string[] PluginKeys = { "guid", "sha256" };
    private static readonly string[] SourceKeys = { "path", "sha256" };
    private static readonly string[] SlotKeys = { "GearStandard", "GearSpecial", "GearClass" };
    private static readonly string[] EntryKeys = { "offlineGearId", "packetSha256" };

    /// <summary>Every policy under `plugins/`, verified and keyed by rundown id. Files that fail — as bytes, as a
    /// policy or against their own pins — are reported once each with `rejected` and are otherwise ignored, and a
    /// rundown claimed by more than one file is withdrawn whole (see the conflict rule below).</summary>
    internal static LoadoutPolicySnapshot Load(string bepInExRoot, LoadoutPinSource pins, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(rejected);
        var parsed = new List<ForgeLoadoutPolicy>();
        var discovered = ContentPackages.Read(bepInExRoot, ContentPackages.EngineReader, "forge", PolicyFileName, Codes,
            (PublishedResourceFailure failure) => rejected(Diagnostic(failure.Relative, failure.Code, failure.Reason)));
        foreach (var document in discovered)
        {
            if (!TryLoad(bepInExRoot, document, pins, out var policy, out var code, out var reason))
            {
                rejected(Diagnostic(document.Relative, code!, reason!));
                continue;
            }
            parsed.Add(policy!);
        }
        // One rundown has one policy for the whole install. When two or more files claim the same rundown the
        // rundown is withdrawn whole — the first file is not kept as a winner — so which policy an install ends up
        // with never depends on the order the files were found, and every claiming file is named once.
        var claims = new Dictionary<uint, int>();
        foreach (var policy in parsed)
            claims[policy.RundownId] = claims.TryGetValue(policy.RundownId, out var count) ? count + 1 : 1;
        var accepted = new Dictionary<uint, ForgeLoadoutPolicy>();
        foreach (var policy in parsed)
        {
            if (claims[policy.RundownId] > 1)
            {
                rejected(Diagnostic(policy.RelativePath, "loadout-policy-conflict", "Rundown " + Number(policy.RundownId)
                    + " is claimed by " + Number(claims[policy.RundownId]) + " policies; none of them takes effect."));
                continue;
            }
            accepted.Add(policy.RundownId, policy);
        }
        return new LoadoutPolicySnapshot(accepted);
    }

    /// <summary>The SHA-256 of one absolute path, streamed from a single opened handle and never read past `budget`
    /// bytes: the length is taken from that same handle and the loop stops at the budget, so a file that grows or is
    /// swapped after the open cannot slip past the cap. An unreadable file answers a null digest rather than an
    /// exception, because a pin is a statement about a file and an unreadable file is the pin's own rejection.</summary>
    internal static PinFileDigest HashBounded(string path, long budget)
    {
        ArgumentNullException.ThrowIfNull(path);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            if (stream.Length > budget) return new PinFileDigest(null, stream.Length, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > budget) return new PinFileDigest(null, total, true);
                hash.AppendData(buffer, 0, read);
            }
            return new PinFileDigest(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException or ArgumentException)
        { return default; }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
    /// <summary>One discovered file from its path to its verified policy: path discipline, byte reading, the strict
    /// canonical parse and the three pins, in that order. The first failing step is the one reason reported.</summary>
    private static bool TryLoad(string root, PublishedResource document, LoadoutPinSource pins,
        out ForgeLoadoutPolicy? policy, out string? code, out string? reason)
    {
        policy = null; code = null; reason = null;
        PublishedResourceFailure? failure = null;
        if (!PublishedResources.TryRead(document, MaximumFileBytes, Codes,
            refusal => failure = refusal, out var text, out var bytes))
            return Reject(failure!.Value.Code, failure.Value.Reason, out code, out reason);
        if (bytes!.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Reject("loadout-policy-bom", "The file starts with a UTF-8 BOM.", out code, out reason);
        if (!TryParse(document.Relative, bytes, text!, out policy, out code, out reason)) return false;
        if (!VerifyPins(root, policy!, pins, out code, out reason)) { policy = null; return false; }
        return true;
    }

    /// <summary>The contract's own parse: the exact key sets, the ranges and spelling of every field, then the
    /// document normalized into the canonical order, and finally one byte comparison that makes every other
    /// spelling of the same document a different file — including a list spelled in another order.</summary>
    private static bool TryParse(string relative, byte[] bytes, string text, out ForgeLoadoutPolicy? policy, out string? code, out string? reason)
    {
        policy = null; code = null; reason = null;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException error) { return Reject("loadout-policy-json", error.Message, out code, out reason); }
        using (document)
        {
            var root = document.RootElement;
            if (!ExactFields(root, PolicyKeys, "The policy", out reason))
                return Reject(Shape, reason!, out code, out reason);
            if (!Text(root, "format", out var format) || !string.Equals(format, PolicyFormat, StringComparison.Ordinal))
                return Reject(Shape, "`format` is not " + PolicyFormat + ".", out code, out reason);
            if (!Text(root, "projectId", out var projectId) || !ProjectId(projectId))
                return Reject(Shape, "`projectId` is not 1 to 128 letters, digits or underscores.", out code, out reason);
            if (!BlockId(root, "rundownId", out var rundownId))
                return Reject(Shape, "`rundownId` is not a rundown id.", out code, out reason);
            if (!Text(root, "gameAssemblySha256", out var gameAssembly) || !Sha256Text(gameAssembly))
                return Reject(Shape, "`gameAssemblySha256` is not a lowercase SHA-256 digest.", out code, out reason);
            if (!root.TryGetProperty("plugins", out var pluginArray) || pluginArray.ValueKind != JsonValueKind.Array)
                return Reject(Shape, "`plugins` is not an array.", out code, out reason);
            int pluginCount = pluginArray.GetArrayLength();
            if (pluginCount < MinimumPlugins || pluginCount > MaximumPlugins)
            {
                return Reject(Shape, "The policy pins " + Number(pluginCount) + " plugins; the band is "
                    + Number(MinimumPlugins) + " to " + Number(MaximumPlugins) + ".", out code, out reason);
            }
            var plugins = new LoadoutPluginPin[pluginCount];
            var pluginGuids = new HashSet<string>(StringComparer.Ordinal);
            int pluginIndex = 0;
            foreach (var entry in pluginArray.EnumerateArray())
            {
                if (!PluginEntry(entry, "`plugins[" + Number(pluginIndex) + "]`", out var pin, out reason))
                    return Reject(Shape, reason!, out code, out reason);
                if (!pluginGuids.Add(pin.Guid.ToLowerInvariant()))
                    return Reject(Shape, "Plugin `" + pin.Guid + "` is pinned twice.", out code, out reason);
                plugins[pluginIndex++] = pin;
            }
            if (!root.TryGetProperty("sources", out var sourceArray) || sourceArray.ValueKind != JsonValueKind.Array)
                return Reject(Shape, "`sources` is not an array.", out code, out reason);
            int sourceCount = sourceArray.GetArrayLength();
            if (sourceCount < 1 || sourceCount > MaximumSources)
            {
                return Reject(Shape, "The policy pins " + Number(sourceCount) + " sources; the band is 1 to "
                    + Number(MaximumSources) + ".", out code, out reason);
            }
            var sources = new LoadoutSourcePin[sourceCount];
            var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
            int sourceIndex = 0;
            foreach (var entry in sourceArray.EnumerateArray())
            {
                if (!SourceEntry(entry, "`sources[" + Number(sourceIndex) + "]`", out var pin, out reason))
                    return Reject(Shape, reason!, out code, out reason);
                if (!sourcePaths.Add(pin.Path.ToLowerInvariant()))
                    return Reject(Shape, "Source `" + pin.Path + "` is pinned twice.", out code, out reason);
                sources[sourceIndex++] = pin;
            }
            if (!root.TryGetProperty("slots", out var slots) || !ExactFields(slots, SlotKeys, "`slots`", out reason))
                return Reject(Shape, reason!, out code, out reason);
            var slotPolicies = new LoadoutSlotPolicy[SlotKeys.Length];
            var slotIds = new HashSet<uint>();
            int total = 0;
            for (int index = 0; index < SlotKeys.Length; index++)
            {
                var name = SlotKeys[index];
                if (!slots.TryGetProperty(name, out var entries) || entries.ValueKind != JsonValueKind.Array)
                    return Reject(Shape, "`slots." + name + "` is not an array.", out code, out reason);
                int count = entries.GetArrayLength();
                total += count;
                if (total > MaximumSlotEntries)
                {
                    return Reject(Shape, "The policy carries more than " + Number(MaximumSlotEntries)
                        + " slot entries.", out code, out reason);
                }
                var ids = new uint[count];
                var packets = new string[count];
                int entry = 0;
                foreach (var item in entries.EnumerateArray())
                {
                    if (!SlotEntry(item, "`slots." + name + "[" + Number(entry) + "]`", out var id, out var packet, out reason))
                        return Reject(Shape, reason!, out code, out reason);
                    if (!slotIds.Add(id))
                        return Reject(Shape, "Offline gear " + Number(id) + " is listed twice.", out code, out reason);
                    ids[entry] = id; packets[entry] = packet; entry++;
                }
                // The canonical order of a list is the contract's own: plugins and sources by their ordinal key,
                // a slot's entries by ascending block id. A file that spells the same list in another order is a
                // different file, which is what the byte comparison below is then able to say.
                Array.Sort(ids, packets);
                slotPolicies[index] = new LoadoutSlotPolicy(ids, packets);
            }
            Array.Sort(plugins, static (left, right) => string.CompareOrdinal(left.Guid, right.Guid));
            Array.Sort(sources, static (left, right) => string.CompareOrdinal(left.Path, right.Path));
            var candidate = new ForgeLoadoutPolicy(relative, projectId!, rundownId, gameAssembly!,
                plugins, sources, slotPolicies);
            var canonical = Canonical(candidate);
            if (!canonical.AsSpan().SequenceEqual(bytes))
            {
                return Reject("loadout-policy-canonical", "The file is not the policy's own canonical bytes.",
                    out code, out reason);
            }
            policy = candidate;
            return true;
        }
    }

    /// <summary>One pinned plugin entry: exactly `guid` and `sha256`, both in the contract's spelling.</summary>
    private static bool PluginEntry(JsonElement entry, string path, out LoadoutPluginPin pin, out string? reason)
    {
        pin = default; reason = null;
        if (!ExactFields(entry, PluginKeys, path, out reason)) return false;
        if (!Text(entry, "guid", out var guid) || !PluginGuid(guid))
        { reason = path + " does not carry a plugin `guid`."; return false; }
        if (!Text(entry, "sha256", out var sha) || !Sha256Text(sha))
        { reason = path + " does not carry a lowercase `sha256`."; return false; }
        pin = new LoadoutPluginPin(guid!, sha!);
        return true;
    }

    /// <summary>One pinned source entry: exactly `path` and `sha256`, the path in the website's own install-relative
    /// spelling. The path is only checked as text here; whether it resolves inside this install is the pin's
    /// business, checked against the BepInEx root after the policy itself is accepted.</summary>
    private static bool SourceEntry(JsonElement entry, string path, out LoadoutSourcePin pin, out string? reason)
    {
        pin = default; reason = null;
        if (!ExactFields(entry, SourceKeys, path, out reason)) return false;
        if (!Text(entry, "path", out var relative) || !SourcePath(relative, out reason))
        { reason = path + " does not carry a package-relative source path."; return false; }
        if (!Text(entry, "sha256", out var sha) || !Sha256Text(sha))
        { reason = path + " does not carry a lowercase `sha256`."; return false; }
        pin = new LoadoutSourcePin(relative!, sha!);
        return true;
    }

    /// <summary>One slot entry: exactly `offlineGearId` and `packetSha256`. The packet digest is kept for
    /// diagnostics only — nothing is matched on it, because the game side cannot recompute the website's packet.</summary>
    private static bool SlotEntry(JsonElement entry, string path, out uint id, out string packet, out string? reason)
    {
        id = 0; packet = ""; reason = null;
        if (!ExactFields(entry, EntryKeys, path, out reason)) return false;
        if (!BlockId(entry, "offlineGearId", out id))
        { reason = path + " does not carry an `offlineGearId`."; return false; }
        if (!Text(entry, "packetSha256", out var sha) || !Sha256Text(sha))
        { reason = path + " does not carry a lowercase `packetSha256`."; return false; }
        packet = sha!;
        return true;
    }

    /// <summary>Every pin, against the digests <see cref="LoadoutPinSource"/> streams back and the verified game
    /// assembly digest. A source is resolved inside this install first, so a policy can only ever pin the install it
    /// sits in; an unreadable, oversized or mismatching pin refuses its own file and nothing else. Sources and plugins
    /// share one byte budget per policy. The first failing pin is the one reason reported.</summary>
    private static bool VerifyPins(string root, ForgeLoadoutPolicy policy, LoadoutPinSource pins,
        out string? code, out string? reason)
    {
        code = null; reason = null;
        long remaining = MaximumPinnedBytes;
        foreach (var source in policy.Sources)
        {
            string path;
            try { path = PublishedResources.ResolveWithinRoot(root, source.Path); }
            catch (InvalidDataException error)
            { return Reject("loadout-policy-source-path", "Pinned source `" + source.Path + "`: " + error.Message, out code, out reason); }
            var digest = pins.HashFile(path, remaining);
            if (digest.OverBudget)
            {
                return Reject("loadout-policy-source-size", "Pinned source `" + source.Path + "` exceeds the "
                    + Number(MaximumPinnedBytes) + "-byte pin budget.", out code, out reason);
            }
            if (digest.Sha256 == null)
            {
                return Reject("loadout-policy-source-missing", "Pinned source `" + source.Path + "` cannot be read.",
                    out code, out reason);
            }
            remaining -= digest.Length;
            if (!string.Equals(digest.Sha256, source.Sha256, StringComparison.Ordinal))
            {
                return Reject("loadout-policy-source-mismatch", "Pinned source `" + source.Path
                    + "` does not hash to its pin.", out code, out reason);
            }
        }
        var gameSha256 = pins.GameAssemblySha256();
        if (gameSha256 == null)
            return Reject("loadout-policy-gameassembly-unreadable", "The game assembly digest is not available.", out code, out reason);
        if (!string.Equals(gameSha256, policy.GameAssemblySha256, StringComparison.Ordinal))
            return Reject("loadout-policy-gameassembly-mismatch", "The game assembly does not hash to its pin.", out code, out reason);
        foreach (var plugin in policy.Plugins)
        {
            var path = pins.PluginLocation(plugin.Guid);
            if (path == null)
                return Reject("loadout-policy-plugin-missing", "Plugin `" + plugin.Guid + "` is not installed.", out code, out reason);
            var digest = pins.HashFile(path, remaining);
            if (digest.OverBudget)
            {
                return Reject("loadout-policy-plugin-size", "Plugin `" + plugin.Guid + "` exceeds the "
                    + Number(MaximumPinnedBytes) + "-byte pin budget.", out code, out reason);
            }
            if (digest.Sha256 == null)
            {
                return Reject("loadout-policy-plugin-unreadable", "Plugin `" + plugin.Guid + "` cannot be read at "
                    + path + ".", out code, out reason);
            }
            remaining -= digest.Length;
            if (!string.Equals(digest.Sha256, plugin.Sha256, StringComparison.Ordinal))
                return Reject("loadout-policy-plugin-mismatch", "Plugin `" + plugin.Guid + "` does not hash to its pin.", out code, out reason);
        }
        return true;
    }
    /// <summary>The one canonical spelling: the website's own field order, compact separators, every list in the
    /// contract's own order (plugins and sources by ordinal key, slot entries by ascending id) and one trailing
    /// newline. Nothing here is a second opinion about the format — it is the format, and a file is accepted only
    /// when it equals this byte for byte. Every accepted string is restricted to characters JSON never escapes, so
    /// this text is also what the website's `JSON.stringify` writes.</summary>
    private static byte[] Canonical(ForgeLoadoutPolicy policy)
    {
        var text = new StringBuilder(1024);
        text.Append("{\"format\":\"").Append(PolicyFormat)
            .Append("\",\"projectId\":\"").Append(policy.ProjectId)
            .Append("\",\"rundownId\":").Append(Number(policy.RundownId))
            .Append(",\"gameAssemblySha256\":\"").Append(policy.GameAssemblySha256)
            .Append("\",\"plugins\":[");
        for (int index = 0; index < policy.Plugins.Length; index++)
        {
            if (index != 0) text.Append(',');
            text.Append("{\"guid\":\"").Append(policy.Plugins[index].Guid)
                .Append("\",\"sha256\":\"").Append(policy.Plugins[index].Sha256).Append("\"}");
        }
        text.Append("],\"sources\":[");
        for (int index = 0; index < policy.Sources.Length; index++)
        {
            if (index != 0) text.Append(',');
            text.Append("{\"path\":\"").Append(policy.Sources[index].Path)
                .Append("\",\"sha256\":\"").Append(policy.Sources[index].Sha256).Append("\"}");
        }
        text.Append("],\"slots\":{");
        for (int index = 0; index < SlotKeys.Length; index++)
        {
            if (index != 0) text.Append(',');
            text.Append('"').Append(SlotKeys[index]).Append("\":[");
            var slot = policy.Slot((LoadoutSlot)(index + 1));
            for (int entry = 0; entry < slot.OfflineGearIds.Length; entry++)
            {
                if (entry != 0) text.Append(',');
                text.Append("{\"offlineGearId\":").Append(Number(slot.OfflineGearIds[entry]))
                    .Append(",\"packetSha256\":\"").Append(slot.PacketSha256[entry]).Append("\"}");
            }
            text.Append(']');
        }
        text.Append("}}\n");
        return Utf8.GetBytes(text.ToString());
    }

    /// <summary>An object carries exactly the named fields, each once: a repeated field is another document and is
    /// refused rather than resolved, and an extra field is refused rather than ignored.</summary>
    private static bool ExactFields(JsonElement element, string[] keys, string path, out string? reason)
    {
        reason = null;
        if (element.ValueKind != JsonValueKind.Object) { reason = path + " is not an object."; return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(keys, property.Name) < 0)
            { reason = path + " carries `" + property.Name + "`, which it does not define."; return false; }
            if (!seen.Add(property.Name)) { reason = path + " names `" + property.Name + "` twice."; return false; }
        }
        if (seen.Count != keys.Length) { reason = path + " is missing one of its fields."; return false; }
        return true;
    }

    /// <summary>The website's `sourcePath`: an install-relative `/`-separated path of portable name characters,
    /// with no empty, `.`, `..`, trailing-dot or reserved-device segment.</summary>
    private static bool SourcePath(string? text, out string? reason)
    {
        reason = null;
        if (text == null || text.Length == 0 || text.Length > 512)
        { reason = "A source path is 1 to 512 characters."; return false; }
        if (text[0] == '/') { reason = "A source path is relative."; return false; }
        foreach (var character in text)
        {
            if (Alphanumeric(character) || character == '.' || character == '_' || character == '-' || character == '/') continue;
            reason = "A source path carries `" + character + "`, which a package path cannot have.";
            return false;
        }
        if (ForgeCore.PosixPath.IsRelativeFile(text)) return true;
        reason = "A source path has an empty, `.` or `..` segment, a segment ending in a dot or a reserved device name.";
        return false;
    }

    /// <summary>The website's `projectId`.</summary>
    private static bool ProjectId(string? text)
    {
        if (text == null || text.Length == 0 || text.Length > 128) return false;
        foreach (var character in text)
            if (!Alphanumeric(character) && character != '_') return false;
        return true;
    }

    /// <summary>The website's plugin `guid`: a leading letter or digit, then letters, digits, dot, underscore or
    /// dash, up to 128 characters.</summary>
    private static bool PluginGuid(string? text)
    {
        if (text == null || text.Length == 0 || text.Length > 128 || !Alphanumeric(text[0])) return false;
        for (int index = 1; index < text.Length; index++)
        {
            var character = text[index];
            if (!Alphanumeric(character) && character != '.' && character != '_' && character != '-') return false;
        }
        return true;
    }

    /// <summary>Lowercase hexadecimal, the only spelling a `sha256` field may have.</summary>
    private static bool Sha256Text(string? text)
    {
        if (text == null || text.Length != 64) return false;
        foreach (var character in text)
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
        return true;
    }

    /// <summary>A `uint` block id, the same spelling `GearPartTransformData` and `EquipmentNativeAdapter` use: the
    /// number's own text, at least one, and never its signed or decimal spelling. `TryGetUInt32` already refuses a
    /// sign, a decimal point, an exponent and anything above `UInt32.MaxValue`.</summary>
    private static bool BlockId(JsonElement entry, string field, out uint value)
    {
        value = 0;
        return entry.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetUInt32(out value) && value != 0;
    }

    private static bool Text(JsonElement entry, string field, out string? value)
    {
        value = null;
        if (!entry.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return value != null;
    }

    private static bool Alphanumeric(char value)
        => (value >= '0' && value <= '9') || (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');

    private static bool Reject(string code, string reason, out string? outCode, out string? outReason)
    { outCode = code; outReason = reason; return false; }

    private static string Diagnostic(string relative, string code, string reason)
        => "weapon." + code + " file=" + relative + ": " + reason;

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The three slots a loadout policy covers, numbered as the game's own `InventorySlot` members are
/// (Standard 1, Special 2, Class 3) — the same numbers the website's packet slot byte uses.</summary>
internal enum LoadoutSlot : byte
{
    GearStandard = 1,
    GearSpecial = 2,
    GearClass = 3
}

/// <summary>The digest of one pinned file: null `Sha256` when it could not be read, `OverBudget` when it is larger
/// than the bytes still allowed.</summary>
internal readonly record struct PinFileDigest(string? Sha256, long Length, bool OverBudget);

/// <summary>Where the digests a policy pins come from. Production names the verified game assembly digest, the
/// loader's plugin locations and bounded streaming hashes of files under BepInEx, and a test names fixture files.
/// `GameAssemblySha256` answers null when no digest is available, `HashFile` answers a null digest when the file
/// cannot be read, and `PluginLocation` answers null for a GUID this install does not have.</summary>
internal sealed class LoadoutPinSource
{
    internal LoadoutPinSource(Func<string?> gameAssemblySha256, Func<string, string?> pluginLocation, Func<string, long, PinFileDigest> hashFile)
    {
        GameAssemblySha256 = gameAssemblySha256 ?? throw new ArgumentNullException(nameof(gameAssemblySha256));
        PluginLocation = pluginLocation ?? throw new ArgumentNullException(nameof(pluginLocation));
        HashFile = hashFile ?? throw new ArgumentNullException(nameof(hashFile));
    }

    /// <summary>The lowercase SHA-256 of the game assembly the `gameAssemblySha256` pin names, evaluated only when a
    /// policy file is actually verified.</summary>
    internal Func<string?> GameAssemblySha256 { get; }
    /// <summary>A BepInEx plugin GUID to the DLL the loader installed it from, or null when it is not installed.</summary>
    internal Func<string, string?> PluginLocation { get; }
    /// <summary>An absolute path and the bytes still allowed to its digest.</summary>
    internal Func<string, long, PinFileDigest> HashFile { get; }
}
/// <summary>One accepted policy: the rundown it activates for, the pins it was verified against, and the per-slot
/// allow-lists it carries. Immutable once loaded.</summary>
internal sealed class ForgeLoadoutPolicy
{
    private readonly LoadoutSlotPolicy[] _slots;
    internal ForgeLoadoutPolicy(string relativePath, string projectId, uint rundownId, string gameAssemblySha256,
        LoadoutPluginPin[] plugins, LoadoutSourcePin[] sources, LoadoutSlotPolicy[] slots)
    {
        RelativePath = relativePath; ProjectId = projectId; RundownId = rundownId;
        GameAssemblySha256 = gameAssemblySha256; Plugins = plugins; Sources = sources; _slots = slots;
    }
    /// <summary>The discovered file, relative to BepInEx, for diagnostics.</summary>
    internal string RelativePath { get; }
    internal string ProjectId { get; }
    internal uint RundownId { get; }
    internal string GameAssemblySha256 { get; }
    internal LoadoutPluginPin[] Plugins { get; }
    internal LoadoutSourcePin[] Sources { get; }
    /// <summary>One loadout slot's allow-list. Only the three defined slots are keyed, which is the only case a
    /// caller reaches after the filter's own gate.</summary>
    internal LoadoutSlotPolicy Slot(LoadoutSlot slot) => _slots[(int)slot - 1];
}

/// <summary>One slot's allow-list in the policy's own ascending order: the offline gear block ids the slot may
/// offer, and the packet digests, which are kept for diagnostics only.</summary>
internal sealed class LoadoutSlotPolicy
{
    internal LoadoutSlotPolicy(uint[] offlineGearIds, string[] packetSha256)
    { OfflineGearIds = offlineGearIds; PacketSha256 = packetSha256; }
    internal uint[] OfflineGearIds { get; }
    internal string[] PacketSha256 { get; }
}

internal readonly struct LoadoutPluginPin
{
    internal LoadoutPluginPin(string guid, string sha256) { Guid = guid; Sha256 = sha256; }
    internal string Guid { get; }
    internal string Sha256 { get; }
}

internal readonly struct LoadoutSourcePin
{
    internal LoadoutSourcePin(string path, string sha256) { Path = path; Sha256 = sha256; }
    internal string Path { get; }
    internal string Sha256 { get; }
}

/// <summary>The immutable startup snapshot: every accepted policy by its rundown id. A rundown with no accepted
/// policy is simply absent, which is what "no filtering, the game's own offer" is.</summary>
internal sealed class LoadoutPolicySnapshot
{
    internal static readonly LoadoutPolicySnapshot Empty = new(new Dictionary<uint, ForgeLoadoutPolicy>());
    private readonly IReadOnlyDictionary<uint, ForgeLoadoutPolicy> _byRundownId;
    internal LoadoutPolicySnapshot(IReadOnlyDictionary<uint, ForgeLoadoutPolicy> byRundownId) { _byRundownId = byRundownId; }
    internal int PolicyCount => _byRundownId.Count;
    internal bool TryGet(uint rundownId, out ForgeLoadoutPolicy policy) => _byRundownId.TryGetValue(rundownId, out policy!);
}
