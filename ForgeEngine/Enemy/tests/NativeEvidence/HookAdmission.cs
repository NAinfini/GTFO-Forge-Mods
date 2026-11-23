using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeCore;
using Mono.Cecil;

/// <summary>Re-checks <c>evidence/enemy-hooks.json</c> against what it claims to describe: the Harmony patch classes of
/// the built engine, the pinned game image and the Runtime byte policy that admits a target. The file is located from
/// this source file's repository, so the checks do not depend on where the specification under test lives.</summary>
internal static class HookAdmission
{
    private const string PolicySource = "Mods/ForgeCore/Native/HookPrologue.cs";
    private const string LengthSource = "Mods/ForgeCore/Native/HookPrologue.cs";

    internal static void Verify(string gamePath, string pluginPath, string gameSha256, Action<string, bool, string> check,
        [CallerFilePath] string source = "")
    {
        string? root = null;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, PolicySource))) { root = directory.FullName; break; }
        string? evidencePath = root == null ? null : Path.Combine(root, "Mods", "ForgeEngine", "Enemy", "evidence", "enemy-hooks.json");
        if (evidencePath == null || !File.Exists(evidencePath)) { check("enemy-hooks.present", false, "evidence/enemy-hooks.json was not found above " + source); return; }
        using var document = JsonDocument.Parse(File.ReadAllText(evidencePath));
        var spec = document.RootElement;
        var policy = spec.GetProperty("nativeAdmissionBytePolicy");
        string Sha(string relative) => ForgeCore.Hash.Sha256Hex(File.ReadAllText(Path.Combine(root!, relative)).Replace("\r\n", "\n"));
        check("enemy-hooks.game-image", string.Equals(spec.GetProperty("gameAssemblySha256").GetString(), gameSha256, StringComparison.OrdinalIgnoreCase), gameSha256);
        check("enemy-hooks.policy-source", policy.GetProperty("policySource").GetString() == PolicySource
            && policy.GetProperty("policySourceSha256").GetString() == Sha(PolicySource), "the byte policy changed since the evidence was written");
        check("enemy-hooks.overwrite-length", policy.GetProperty("overwriteLengthSource").GetString() == LengthSource
            && policy.GetProperty("overwriteLengthSourceSha256").GetString() == Sha(LengthSource)
            && policy.GetProperty("overwriteLength").GetInt32() == HookPrologue.OverwriteLength, "length=" + HookPrologue.OverwriteLength);
        check("enemy-hooks.not-game-verification", !spec.GetProperty("gameVerified").GetBoolean()
            && !policy.GetProperty("gameVerified").GetBoolean() && !policy.GetProperty("physicalInstallationVerified").GetBoolean(),
            "byte policy alone never approves a native installation");

        // The rows are the plugin's own Harmony patch classes, no more and no fewer.
        using var plugin = AssemblyDefinition.ReadAssembly(pluginPath);
        var patchClasses = plugin.MainModule.Types.Where(ForgeIl.IsEnemy).SelectMany(ForgeIl.Walk)
            .Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch")).Select(t => t.Name).ToArray();
        var rows = spec.GetProperty("hooks").EnumerateArray().ToArray();
        var named = rows.Select(r => r.GetProperty("hook").GetString()!).ToArray();
        check("enemy-hooks.rows-are-the-patch-classes", named.Length == named.Distinct().Count()
            && named.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(patchClasses.OrderBy(x => x, StringComparer.Ordinal)),
            "rows=" + named.Length + " patch classes=" + patchClasses.Length);
        check("enemy-hooks.held-back-have-reasons", rows.All(r => r.GetProperty("declared").GetBoolean() || r.TryGetProperty("pending", out var why) && !string.IsNullOrWhiteSpace(why.GetString())),
            "a class that is not declared names why");

        // Every byte the evidence records is the byte the pinned image holds, and every verdict is the verdict the
        // current policy gives those bytes.
        using var image = new PEReader(File.OpenRead(gamePath));
        byte[] Entry(string rva)
        {
            ulong address = Convert.ToUInt64(rva[2..], 16);
            var section = image.PEHeaders.SectionHeaders.First(s => address >= (ulong)s.VirtualAddress
                && address + HookPrologue.SnapshotLength <= (ulong)s.VirtualAddress + (ulong)Math.Min(s.VirtualSize, s.SizeOfRawData));
            return image.GetSectionData(section.VirtualAddress).GetContent((int)(address - (ulong)section.VirtualAddress), HookPrologue.SnapshotLength).ToArray();
        }
        var wrong = new List<string>();
        int checkedRows = 0;
        foreach (var row in rows)
        {
            string id = row.GetProperty("hook").GetString()!;
            if (row.TryGetProperty("resolution", out _) || !row.TryGetProperty("rva", out var rva) || rva.ValueKind == JsonValueKind.Null)
            { wrong.Add(id + ":unresolved"); continue; }
            var bytes = Entry(rva.GetString()!);
            checkedRows++;
            if (!string.Equals(Convert.ToHexString(bytes), row.GetProperty("first32Bytes").GetString(), StringComparison.OrdinalIgnoreCase)) wrong.Add(id + ":bytes");
            if ((HookPrologue.Rejection(bytes) ?? "pass") != row.GetProperty("admission").GetString()) wrong.Add(id + ":verdict");
        }
        check("enemy-hooks.rows-match-image-and-policy", wrong.Count == 0, string.Join(",", wrong));
        check("enemy-hooks.refusals-are-listed", policy.GetProperty("checkedTargets").GetInt32() == checkedRows
            && policy.GetProperty("refusals").GetArrayLength() == rows.Count(r => r.GetProperty("admission").GetString() != "pass"),
            "checked=" + policy.GetProperty("checkedTargets").GetInt32() + " rows=" + checkedRows);
        check("enemy-hooks.no-refusal", policy.GetProperty("refusals").GetArrayLength() == 0,
            "an entry the byte policy refuses must be moved to a call boundary or reported, not declared");

        // An entry kept at a call boundary stays there only while its own bytes are refused.
        foreach (var entry in spec.GetProperty("notHookedEntries").EnumerateArray())
        {
            string id = entry.GetProperty("type").GetString() + "." + entry.GetProperty("name").GetString();
            var bytes = Entry(entry.GetProperty("rva").GetString()!);
            string? verdict = HookPrologue.Rejection(bytes);
            check("enemy-hooks.call-boundary." + id, verdict != null && verdict == entry.GetProperty("admission").GetString()
                && string.Equals(Convert.ToHexString(bytes), entry.GetProperty("first32Bytes").GetString(), StringComparison.OrdinalIgnoreCase),
                verdict ?? "the entry now passes the byte policy: hook it directly");
        }
    }
}
