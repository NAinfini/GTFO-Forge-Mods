using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ForgeCore;
using ForgeEngine.Framework;

namespace ForgeEngine.Network.Tests;

/// <summary>
/// The engine's content identity: what two sides must share, and what differs when they do not. Whether two sides match is
/// the channel's handshake, so the refusals run on it: each side reads the other's identity from the channel's content
/// detail and names the first real difference.
/// </summary>
internal static class HandshakeChecks
{
    private static readonly RuntimeIdentitySummary Runtime = new("NAinfini.ForgeEngine", "1.0.0", "1.0.0", "20403457");
    private static readonly NetworkContentSnapshot NoContent = NetworkContentSnapshot.FromWords(new ulong[NetworkContentSnapshot.CategoryCount * NetworkContentSnapshot.WordsPerCategory]);
    private static readonly PlanIdentity[] Plans =
    {
        new("forge.plan.alpha", "resource.alpha", "r3", "binding.a,binding.b", "level:31:A:0", new string('A', 64)),
        new("forge.plan.beta", "resource.beta", "r1", "binding.c", "level:31:A:1", new string('B', 64))
    };
    private static readonly string[] Pack = { "Sample-Pack" };
    private static readonly (uint, string)[] Enemies = { (13, "Striker_Wave"), (1002602066, "NAinfini_Forged"), (1019379685, "NAinfini_Mother") };
    private static NetworkContentSnapshot Capture(string root, IReadOnlyList<string> packages) => NetworkContentSnapshot.Capture(root, packages, Enemies);

    internal static void Run(Suite suite)
    {
        PlanDigest(suite);
        Refusals(suite);
        ContentSnapshots(suite);
    }

    /// <summary>One side of a session wired as the game-facing half wires it: the channel holds until a plan set is adopted,
    /// then compares the handshake's text and hands a refused player this side's identity.</summary>
    private sealed class Side
    {
        private readonly Machine _machine;
        private NetworkHandshake _handshake;
        internal HandshakeEvaluation? Verdict;
        internal string Explanation = "";
        internal int Synced;

        internal Side(Machine machine)
        {
            _machine = machine;
            _handshake = new NetworkHandshake(Runtime);
            machine.Channel.HoldContent();
            machine.Channel.PeerSynced += _ => Synced++;
            machine.Channel.Refused += (player, why) =>
            {
                Verdict = _handshake.Refuse(player, machine.Net.Role, machine.Channel.FormatDiffers(player), why, machine.Channel.ContentDetailOf(player));
                Explanation = _handshake.Explain(player, Verdict.Value.Code);
            };
        }

        internal void Adopt(IReadOnlyList<PlanIdentity> plans, NetworkContentSnapshot? content = null, RuntimeIdentitySummary? runtime = null)
        {
            if (runtime != null) _handshake = new NetworkHandshake(runtime.Value);
            _handshake.AdoptPlans(plans, content ?? NoContent);
            Announce(_handshake.Local().Write(), _handshake.ContentText());
        }

        internal void Announce(byte[] detail, string content)
        {
            Verdict = null;
            _machine.Channel.ContentDetail = detail;
            _machine.Channel.Content = content;
        }
    }

    private static NetworkHandshake Adopted(IReadOnlyList<PlanIdentity> plans, NetworkContentSnapshot? content = null)
    {
        var handshake = new NetworkHandshake(Runtime);
        handshake.AdoptPlans(plans, content ?? NoContent);
        return handshake;
    }

    /// <summary>The digest replaces the plan rows, so it covers what the rows carry and is the same value whichever order the
    /// plans were declared in.</summary>
    private static void PlanDigest(Suite suite)
    {
        suite.Case("handshake: the plan set is one comparison value");
        var direct = Adopted(Plans);
        suite.Equal(direct.ContentText(), Adopted(new[] { Plans[1], Plans[0] }).ContentText(), "the declaration order does not change what is compared");
        suite.True(direct.ContentText() != Adopted(new[] { Plans[0], Plans[1] with { Mounts = "level:31:B:0" } }).ContentText(),
            "actual mounts change it even if a plan id was not updated");
        var adopting = new NetworkHandshake(Runtime);
        suite.False(adopting.PlansAdopted, "a session before plan discovery has adopted nothing");
        suite.True(adopting.AdoptPlans(Array.Empty<PlanIdentity>(), NoContent) && adopting.PlansAdopted, "a discovered set of no plans is still an adopted set");
        suite.False(adopting.AdoptPlans(Array.Empty<PlanIdentity>(), NoContent), "adopting the same set again is not a change");
        suite.True(adopting.AdoptPlans(Plans, NoContent), "adopting another set is a change");
    }

    /// <summary>One refusal of each kind, through the channel: the verdict each side reaches names the first real difference.</summary>
    private static void Refusals(Suite suite)
    {
        var session = new Session();
        var hostMachine = session.Join(1);
        var clientMachine = session.Join(2);
        var host = new Side(hostMachine);
        var client = new Side(clientMachine);
        bool Refused(string code) => host.Verdict?.Code == code && hostMachine.Channel.State(2) == PeerState.Refused
            && clientMachine.Channel.State(1) == PeerState.Refused && hostMachine.Net.Refusal(2) == NetworkCodes.HostUnmatched;

        suite.Case("handshake: the same runtime major and content play together");
        host.Adopt(Plans);
        session.Hub.Settle();
        suite.True(hostMachine.Channel.State(2) == PeerState.Unknown && host.Verdict == null && hostMachine.Net.Refusal(2) == NetworkCodes.HelloUnsent,
            "a player that has adopted no plan set yet is judged by nobody");
        client.Adopt(Plans, runtime: new RuntimeIdentitySummary("NAinfini.ForgeEngine", "1.7.3", "1.2.0", "20403457"));
        session.Hub.Settle();
        suite.True(hostMachine.Channel.State(2) == PeerState.Compatible && clientMachine.Channel.State(1) == PeerState.Compatible
            && host.Synced == 1 && client.Synced == 1 && host.Verdict == null && client.Verdict == null && hostMachine.Net.Refusal(2) == null,
            "another minor or patch of the same major is compatible");

        suite.Case("handshake: another plan set");
        client.Adopt(new[] { Plans[0] });
        session.Hub.Settle();
        suite.True(Refused(NetworkCodes.PlanSetMismatch) && client.Verdict?.Code == NetworkCodes.PlanSetMismatch, "both sides name the plan set");
        suite.True(host.Verdict!.Value.Detail.Contains("other player: 1, this player: 2"), "the explanation carries both plan counts");

        suite.Case("handshake: another runtime");
        client.Adopt(Plans, runtime: new RuntimeIdentitySummary("NAinfini.ForgeEngine", "2.0.0", "1.0.0", "20403457"));
        session.Hub.Settle();
        suite.True(Refused(NetworkCodes.RuntimeMismatch) && client.Verdict?.Code == NetworkCodes.RuntimeMismatch, "another major version is named as the runtime, not as its plans");

        suite.Case("handshake: an identity that cannot be read");
        client.Announce(new byte[] { 9, 9, 9 }, "unreadable");
        session.Hub.Settle();
        suite.True(Refused(NetworkCodes.FormatInvalid), "a refusal with no readable detail is still a refusal");

        suite.Case("handshake: another message layout");
        clientMachine.Channel.Bytes("extra", 1, static (_, _) => { });
        session.Hub.Settle();
        suite.True(Refused(NetworkCodes.ProtocolMismatch) && host.Verdict!.Value.Detail.Contains("'extra'"),
            "different message layouts outrank any difference of content, and the message is named");

        // A body format change raises its message's version: a build that speaks the earlier one is refused by name, both ways,
        // instead of reading bodies it cannot parse.
        var versions = new Session();
        var current = versions.Join(1);
        var earlier = versions.Hub.Join(2).Node.Open(NetworkProtocol.ChannelId);
        earlier.Bytes("state", 2, static (_, _) => { });
        earlier.Table<int>("migration", 2, 2 * NetworkProtocol.MaximumMigrationBytes + 64 * 1024);
        string? named = null;
        current.Channel.Refused += (_, why) => named = why;
        versions.Hub.Settle();
        suite.True(current.Channel.State(2) == PeerState.Refused && earlier.State(1) == PeerState.Refused && current.Channel.FormatDiffers(2)
            && named != null && named.Contains("'state'"), "a peer speaking an earlier state version is refused both ways and the message is named");
        suite.True(named != null && named.Contains("message 'migration' has a different kind, field layout or version"),
            "and so is one speaking the earlier migration version: its image is never read");
    }

    private static void ContentSnapshots(Suite suite)
    {
        suite.Case("handshake: the packs a level reads");
        uint issued = 1000;
        RundownPack Rundown(string package, string name, params string[] pins)
            => new(package, "1.0.0", package, name, new[] { new RundownBlock(name, "Rundown", new System.Text.Json.Nodes.JsonObject(), ++issued) }, null, "plugins/" + package + "/forge/rundown.forge")
            { DiscoveryAdmitted = true, Dependencies = pins };
        var installed = new[] { Rundown("A-One", "one", "B-Two-1.2.3", "C-Three-0.1.0"), Rundown("D-Four", "four") };
        var one = NetworkContentSnapshot.NeededPackages(installed, installed[0].RundownId);
        suite.Equal("A-One,B-Two,C-Three", string.Join(",", one), "a level reads its Rundown pack and the packs it pins");
        suite.Equal(0, NetworkContentSnapshot.NeededPackages(installed, 7).Length, "a level of no installed Rundown reads nothing");

        suite.Case("handshake: actual pack file sets and bytes, including packs with no plans");
        var root = Path.Combine(Path.GetTempPath(), "forge-content-handshake-" + Guid.NewGuid().ToString("N"));
        var files = new[] { "forge/rundown.forge", "forge/room-build.forge", "GameData_ItemDataBlock_bin.json", "forge/resources/clip.resource.forge",
            "forge/content/pack.forge", "forge/content/weapon/weapon.forge", "forge/content/enemy/enemy.forge", "forge/content/behavior/behavior.forge",
            "forge/weapons/gun.forge", "forge/resources/board.png", "forge/resources/model.fmesh", "forge/resources/sound.ogg" };
        var names = new[] { "map", "room", "native", "audio", "native", "native", "native", "native", "native", "audio", "audio", "audio" };
        string PathOf(string relative) => relative.StartsWith("GameData_", StringComparison.Ordinal) ? Path.Combine(root, "GameData", "Sample-Pack", relative) : Path.Combine(root, "plugins", "Sample-Pack", relative);
        void Put(string relative, string contents)
        {
            var path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }
        try
        {
            foreach (var file in files) Put(file, "{\"title\":\"Original\"}");
            var initial = Capture(root, Pack);
            suite.True(initial.Same(Capture(root, Pack)), "unchanged bytes produce the same snapshot");
            // Every kind of file a level installs from is compared: a change in any of them is found and named by its category.
            for (var index = 0; index < files.Length; index++)
            {
                Put(files[index], "{\"title\":\"Different\"}");
                var changed = Capture(root, Pack);
                suite.True(Adopted(Array.Empty<PlanIdentity>(), initial).ContentText() != Adopted(Array.Empty<PlanIdentity>(), changed).ContentText(),
                    files[index] + ": changed bytes change what the channel compares");
                suite.Equal("content-" + names[index] + "-mismatch", initial.Compare(changed, SessionRole.Host).Code, files[index] + ": changed bytes name their category");
                Put(files[index], "{\"title\":\"Original\"}");
            }
            // A file's digest is kept by its length and write time: an edit of the same length is still found.
            var written = File.GetLastWriteTimeUtc(PathOf(files[1]));
            Put(files[1], "{\"title\":\"Originax\"}");
            File.SetLastWriteTimeUtc(PathOf(files[1]), written.AddSeconds(1));
            suite.Equal("content-room-mismatch", initial.Compare(Capture(root, Pack), SessionRole.Host).Code, "a same-length edit is found");
            Put(files[1], "{\"title\":\"Original\"}");
            suite.True(initial.Same(Capture(root, Pack)), "restored bytes give the full read's digest again");
            File.Delete(PathOf(files[0]));
            var missing = Capture(root, Pack);
            suite.Equal("content-map-missing", initial.Compare(missing, SessionRole.Host).Code, "missing file is explicit");
            suite.Equal("content-map-extra", missing.Compare(initial, SessionRole.Host).Code, "extra file is explicit");
            Put(files[0], "{\"title\":\"Original\"}");
            using (var locked = new FileStream(PathOf(files[0]), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var unreadable = Capture(root, Pack);
                suite.Equal("content-map-unreadable", initial.Compare(unreadable, SessionRole.Host).Code, "unreadable file is explicit");
                suite.False(unreadable.Compare(unreadable, SessionRole.Host).Accepted, "two failed reads cannot agree");
            }
            // A native entry can change without changing any compiled plan or binding identity.
            Put(files[6], "{\"title\":\"Different enemy payload with the same ID\"}");
            var edited = Capture(root, Pack);
            var samePlans = Adopted(Plans, initial);
            suite.Equal("content-native-mismatch", samePlans.Compare(Adopted(Plans, edited).Local(), SessionRole.Host).Code, "same plans cannot admit different content-entry bytes");
            suite.True(samePlans.AdoptPlans(Plans, edited), "content-only rediscovery is a new announcement");
            Put(files[6], "{\"title\":\"Original\"}");
            File.Move(PathOf(files[3]), PathOf("forge/resources/renamed.resource.forge"));
            var renamed = Capture(root, Pack);
            suite.Equal("content-audio-files", initial.Compare(renamed, SessionRole.Host).Code, "same bytes under another file name change the file set");
            File.Move(PathOf("forge/resources/renamed.resource.forge"), PathOf(files[3]));
            // No size or count cap: a 65 MB file is hashed whole and reported as different bytes, not refused.
            using (var large = new FileStream(PathOf(files[3]), FileMode.Open, FileAccess.Write)) large.SetLength(65L * 1024 * 1024);
            var big = Capture(root, Pack);
            suite.Equal("content-audio-mismatch", initial.Compare(big, SessionRole.Host).Code, "a large file compares by its bytes");
            suite.True(big.Same(Capture(root, Pack)), "a large file hashes the same twice");
            Put(files[3], "{\"title\":\"Original\"}");

            // Only the packs the level names are read: an unrelated pack changes nothing, a named one is compared and named.
            Directory.CreateDirectory(Path.Combine(root, "plugins", "Other-Pack", "forge"));
            File.WriteAllText(Path.Combine(root, "plugins", "Other-Pack", "forge", "other.forge"), "{\"title\":\"Other\"}");
            suite.True(initial.Same(Capture(root, Pack)), "an installed pack the level does not name is not compared");
            var both = Capture(root, new[] { "Sample-Pack", "Other-Pack" });
            var mismatch = initial.Compare(both, SessionRole.Host);
            suite.Equal("content-map-extra", mismatch.Code, "a named pack's files are compared");
            suite.True(both.Difference(mismatch.Code).Contains("Sample-Pack, Other-Pack"), "the explanation names the packs the level reads");
            suite.True(Capture(root, new[] { "Absent-Pack" }).Same(Capture(root, Array.Empty<string>())),
                "a named pack that is not installed reads as no files");
            Directory.Delete(Path.Combine(root, "plugins", "Other-Pack"), true);

            // The enemy table is compared whole, whatever pack its rows came from: the game's spawn packet names an enemy only by
            // its position in that table, so another pack's extra enemy (or another order) must refuse, not spawn another enemy.
            suite.Case("handshake: the loaded enemy table is compared whole");
            (uint, string) other = (913490611, "NAinfini_Other");
            var extraEnemy = NetworkContentSnapshot.Capture(root, Pack, new[] { Enemies[0], other, Enemies[1], Enemies[2] });
            suite.Equal("content-enemies-missing", extraEnemy.Compare(initial, SessionRole.Host).Code, "a host with an enemy the client lacks refuses it");
            suite.True(extraEnemy.Difference("content-enemies-missing").Contains("not only this level's"), "the explanation says every installed pack counts");
            var reordered = NetworkContentSnapshot.Capture(root, Pack, new[] { Enemies[0], Enemies[2], Enemies[1] });
            suite.Equal("content-enemies-files", initial.Compare(reordered, SessionRole.Host).Code, "the same enemies in another order refuse");
            suite.Equal("content-enemies-unreadable", initial.Compare(NetworkContentSnapshot.Capture(root, Pack, null), SessionRole.Host).Code,
                "an unreadable table refuses instead of comparing nothing");

            // The plan set is the level's too: a plan installed only in a pack the level does not read never reaches the digest.
            var (alpha, beta) = (Plans[0], Plans[1]);
            var packsOfPlan = new[] { (alpha.ContentDigest, "Sample-Pack"), (beta.ContentDigest, "Other-Pack") }
                .ToLookup(row => row.Item1, row => row.Item2);
            var levelPlans = initial.OfLevel(Plans, packsOfPlan);
            suite.Equal(1, levelPlans.Count, "a plan of a pack the level does not read is left out");
            suite.Equal(alpha, levelPlans[0], "the plan of a pack the level reads is kept");
            var gamma = beta with { PlanId = "forge.plan.gamma", ContentDigest = new string('C', 64) };
            var morePacks = new[] { (alpha.ContentDigest, "Sample-Pack"), (beta.ContentDigest, "Other-Pack"), (gamma.ContentDigest, "Third-Pack") }
                .ToLookup(row => row.Item1, row => row.Item2);
            suite.Equal(Adopted(levelPlans, initial).ContentText(), Adopted(initial.OfLevel(new[] { alpha, beta, gamma }, morePacks), initial).ContentText(),
                "installing more Forge packs does not change what is compared");
            var twoCopies = new[] { (alpha.ContentDigest, "Other-Pack"), (alpha.ContentDigest, "Sample-Pack") }.ToLookup(row => row.Item1, row => row.Item2);
            suite.Equal(1, initial.OfLevel(new[] { alpha }, twoCopies).Count, "a plan also installed in a pack the level reads is kept");
            suite.Equal("Sample-Pack", NetworkContentSnapshot.PackOf("plugins/Sample-Pack/forge/plans/a.plan.forge"), "the pack of a plan file is its plugin directory");
            suite.True(NetworkContentSnapshot.PackOf("config/forge/plans/a.plan.forge") == null, "a file outside plugins belongs to no pack");

            // A computer with no pack for the level learns the missing pack's name from what the host's channel tells it.
            suite.Case("handshake: a client is told which pack differs");
            var session = new Session();
            var host = new Side(session.Join(1));
            var clientMachine = session.Join(2);
            var client = new Side(clientMachine);
            host.Adopt(Array.Empty<PlanIdentity>(), initial);
            client.Adopt(Array.Empty<PlanIdentity>(), Capture(root, Array.Empty<string>()));
            session.Hub.Settle();
            suite.True(clientMachine.Channel.State(1) == PeerState.Refused && client.Verdict?.Code == "content-map-missing", "a client with no pack for the level refuses the host");
            suite.True(client.Explanation.Contains("Sample-Pack") && client.Explanation.Contains("not installed") && !client.Explanation.Contains("content-"),
                "the player is told which package to install");
            suite.True(host.Verdict?.Code == "content-map-missing" && host.Explanation.Contains("Sample-Pack") && !host.Explanation.Contains("content-"),
                "the host names the packs its level reads");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
