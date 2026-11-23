namespace GameData
{
    /// <summary>A stand-in for one game table with the three containers the transaction writes (<c>Wrapper.Blocks</c>,
    /// <c>s_blockByID</c>, <c>s_blockIDByName</c>) and <c>GetBlock</c>. An ID in <see cref="Unreadable"/> does not resolve, which makes
    /// a readback fail after the write.</summary>
    public sealed class ProbeTableDataBlock
    {
        public sealed class Holder { public List<ProbeTableDataBlock> Blocks { get; } = new(); }
        public static Holder Wrapper { get; private set; } = new();
        public static Dictionary<uint, ProbeTableDataBlock> s_blockByID { get; private set; } = new();
        public static Dictionary<string, uint> s_blockIDByName { get; private set; } = new();
        public static readonly HashSet<uint> Unreadable = new();
        public static ProbeTableDataBlock? GetBlock(uint id) => !Unreadable.Contains(id) && s_blockByID.TryGetValue(id, out var block) ? block : null;

        public uint persistentID;
        public string name = "";
        public int Index;

        public static ProbeTableDataBlock Row(uint id, string name) => new() { persistentID = id, name = name };

        public static void Reset(params ProbeTableDataBlock[] rows)
        {
            Wrapper = new(); s_blockByID = new(); s_blockIDByName = new(); Unreadable.Clear();
            // The game's own Index does not match the position either; the transaction must not depend on it.
            for (int i = 0; i < rows.Length; i++) { rows[i].Index = 10 + i; Wrapper.Blocks.Add(rows[i]); s_blockByID.Add(rows[i].persistentID, rows[i]); s_blockIDByName.Add(rows[i].name, rows[i].persistentID); }
        }
    }
}

namespace ForgeCore.Tests
{
    using GameData;
    using Table = GameData.ProbeTableDataBlock;

    /// <summary>The native table transaction (core algorithm and resource integrity): add, same-slot replace and remove commit and revert
    /// exactly; a failed readback rolls the whole transaction back to the previous tables; and the ownership audit catches a deliberately
    /// broken index (detection), turning the rollback into <see cref="NativeDataBlockAuditException"/> logged once.</summary>
    internal static class DataBlockChecks
    {
        private static string Shape() => string.Join(",", Table.Wrapper.Blocks.Select(row => row.persistentID + ":" + row.Index))
            + "|" + Table.s_blockByID.Count + "|" + Table.s_blockIDByName.Count;

        internal static void Run(Action<bool, string> check, List<string> errors)
        {
            Table.Reset(Table.Row(1, "vanilla.a"), Table.Row(2, "vanilla.b"), Table.Row(3, "vanilla.c"));
            var a = Table.Row(101, "mod.a"); var b = Table.Row(102, "mod.b");
            var first = new NativeDataBlockTables.Transaction("Test.Mod");
            first.Add(a); first.Add(b);
            first.Commit(); first.Release();
            check(Shape() == "1:10,2:11,3:12,101:3,102:4|5|5" && NativeDataBlockTables.IsPublished("ProbeTable", 101)
                && NativeDataBlockTables.AuditOwnership().Count == 0, "data blocks: added rows go to the end of all three containers and the audit is clean");

            var before = Shape();
            // Row 102 changes its ID to 103 under the same name: the add is staged before the removal and still takes the name the
            // removal lets go of, while an add of that name without the removal in its own transaction is a collision.
            var a2 = Table.Row(101, "mod.a"); var c = Table.Row(103, "mod.b");
            var alone = new NativeDataBlockTables.Transaction("Test.Mod");
            alone.Add(Table.Row(103, "mod.b"));
            bool collided = false;
            try { alone.Commit(); } catch (NativeDataBlockConflictException) { collided = true; }
            var second = new NativeDataBlockTables.Transaction("Test.Mod");
            second.Replace(a2); second.Add(c); second.Remove("ProbeTable", 102);
            second.Commit();
            bool committed = Table.Wrapper.Blocks[3] == a2 && Table.s_blockByID[101] == a2 && !Table.s_blockByID.ContainsKey(102)
                && Table.s_blockIDByName["mod.b"] == 103 && Shape() == "1:10,2:11,3:12,101:3,103:4|5|5";
            second.Revert();
            check(collided && committed && Shape() == before && Table.Wrapper.Blocks[3] == a && Table.Wrapper.Blocks[4] == b
                && Table.s_blockIDByName["mod.b"] == 102 && NativeDataBlockTables.AuditOwnership().Count == 0,
                "data blocks: replace keeps the slot, remove renumbers from its position, a removed row's name is free for an add of the same transaction only, and revert restores objects, positions and Index");

            var foreign = new NativeDataBlockTables.Transaction("Other.Mod");
            foreign.Remove("ProbeTable", 101);
            bool refused = false;
            try { foreign.Commit(); } catch (NativeDataBlockConflictException) { refused = true; }
            check(refused && Shape() == before, "data blocks: another mod cannot remove a row it does not own");

            // A rebuilt table holds another object under the owner's ID: the owner no longer holds the row and adds it again.
            bool held = NativeDataBlockTables.Holds("Test.Mod", "ProbeTable", 101) && !NativeDataBlockTables.Holds("Other.Mod", "ProbeTable", 101);
            Table.s_blockByID[101] = Table.Row(101, "mod.a");
            bool lost = !NativeDataBlockTables.Holds("Test.Mod", "ProbeTable", 101);
            Table.s_blockByID[101] = a;
            check(held && lost && NativeDataBlockTables.Holds("Test.Mod", "ProbeTable", 101),
                "data blocks: an owner holds its row only while the table holds the very object it published");

            // A readback failure after the writes: the replace, the removal and the add are all undone.
            var a3 = Table.Row(101, "mod.a.renamed"); var d = Table.Row(104, "mod.d");
            Table.Unreadable.Add(104);
            var failing = new NativeDataBlockTables.Transaction("Test.Mod");
            failing.Replace(a3); failing.Remove("ProbeTable", 102); failing.Add(d);
            Exception? thrown = null;
            try { failing.Commit(); } catch (Exception error) { thrown = error; }
            check(thrown is InvalidOperationException && Shape() == before && Table.Wrapper.Blocks[3] == a && Table.s_blockIDByName.ContainsKey("mod.a")
                && !Table.s_blockIDByName.ContainsKey("mod.a.renamed") && Table.Wrapper.Blocks[4] == b && NativeDataBlockTables.AuditOwnership().Count == 0,
                "data blocks: a failed readback rolls the whole transaction back and the audit passes");

            // Detection: a broken name index is reported, and a rollback that leaves it broken is an audit failure, logged once.
            Table.s_blockIDByName["vanilla.b"] = 3;
            var problems = NativeDataBlockTables.AuditOwnership();
            int logged = errors.Count;
            var broken = new NativeDataBlockTables.Transaction("Test.Mod");
            broken.Add(Table.Row(105, "mod.e"));
            Table.Unreadable.Add(105);
            Exception? audit = null;
            try { broken.Commit(); } catch (Exception error) { audit = error; }
            check(problems.Count == 1 && problems[0].Contains("vanilla.b") && audit is NativeDataBlockAuditException { InnerException: InvalidOperationException }
                && errors.Skip(logged).Count(line => line.Contains("core-data-blocks-audit-failed")) == 1 && !Table.s_blockByID.ContainsKey(105),
                "data blocks: the audit catches a broken index and a rollback that cannot fix it is reported once as an audit failure");
        }
    }
}
