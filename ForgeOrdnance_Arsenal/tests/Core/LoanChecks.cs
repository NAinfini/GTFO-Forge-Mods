using System.Text.Json;
using GameData;
using Gear;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnanceArsenal.Tests;

internal static class LoanChecks
{
    internal static int Run(Action<bool, string> check)
    {
        var count = 0;
        void Check(bool value, string name) { check(value, name); count++; }

        var book = new LoanBook();
        book.Commit(1, 1, 7, 1);
        Check(book.HasLoan(7), "loan reconciliation: committed loan prepared");
        var released = new List<ulong>();
        book.ReconcileLoans(_ => true, released.Add);
        Check(book.HasLoan(7) && released.Count == 0, "loan reconciliation: native borrowed gear retains claim");
        book.ReconcileLoans(_ => false, released.Add);
        Check(!book.HasLoan(7) && released.SequenceEqual(new[] { 7UL }),
            "loan reconciliation: missing borrowed gear releases the loan");

        var stashBook = new LoanBook();
        stashBook.Commit(1, 1, 7, 1);
        Check(!stashBook.IsStashed(7), "loan stash: a fresh loan is not stashed");
        Check(!stashBook.Stash(1, 1, 7, 2) && !stashBook.Stash(1, 2, 7, 1) && !stashBook.Stash(1, 1, 8, 1) && !stashBook.IsStashed(7),
            "loan stash: only the matching loan can be stashed");
        Check(stashBook.Observe(7, true) && !stashBook.Observe(7, false), "loan stash: without a stash the marker alone decides");
        Check(stashBook.Stash(1, 1, 7, 1) && stashBook.IsStashed(7) && stashBook.HasLoan(7), "loan stash: a stashed loan stays committed");
        Check(stashBook.Observe(7, true) && stashBook.IsStashed(7), "loan stash: the marker still readable right after the announcement is the old reading");
        Check(stashBook.Observe(7, false) && stashBook.Observe(7, false) && stashBook.IsStashed(7), "loan stash: the loan stands while the own weapon is in the slot");
        Check(stashBook.Observe(7, true) && !stashBook.IsStashed(7), "loan stash: the marker back after the own weapon was seen ends the stash");
        Check(!stashBook.Observe(7, false), "loan stash: a later missing marker is a return again");
        stashBook.Stash(1, 1, 7, 1);
        Check(stashBook.Release(1, 1, 7, 1) && !stashBook.IsStashed(7) && !stashBook.HasLoan(7), "loan stash: a return releases a stashed loan");
        stashBook.Commit(1, 2, 7, 2); stashBook.Stash(1, 2, 7, 2);
        stashBook.ReconcileLoans(_ => false, _ => { });
        Check(!stashBook.IsStashed(7) && !stashBook.HasLoan(7), "loan stash: reconciliation that releases a loan forgets its stash flag");
        stashBook.Commit(1, 3, 7, 3); stashBook.Stash(1, 3, 7, 3); stashBook.Clear();
        Check(!stashBook.IsStashed(7), "loan stash: clearing the book clears stashes");

        var spec = HeavyWeaponSpec.Parse(JsonDocument.Parse("{\"Model\":\"Arsenal:grenade-launcher\",\"Grenade\":{},\"Gear\":15}").RootElement);
        var launcher = new LauncherLoan(spec, 1, 10, 0.5f, 10f);
        Check(launcher.TrySchedule(launcher.Identity, 1, 10f, 0f, out var first, out _) && first == 0f,
            "launcher cadence: first authorized shot is immediate");
        Check(launcher.TrySchedule(launcher.Identity, 2, 10f, 0f, out var second, out _)
            && launcher.TrySchedule(launcher.Identity, 3, 10f, 0f, out var third, out _)
            && launcher.TrySchedule(launcher.Identity, 4, 10f, 0f, out var fourth, out _)
            && second == 0.5f && third == 1f && fourth == 1.5f,
            "launcher cadence: every jitter-bunched shot is retained on the native-cadence timeline");

        // A completed recall whose native special slot never assembles must actively restore the saved original instead of
        // keeping the player in Reconciling forever.
        SNet.IsMaster = false;
        SNet.LocalPlayer = new() { Lookup = 7 };
        PlayerManager.Local = null;
        PlayerManager.PlayerAgentsInLevel.Clear();
        PlayerBackpackManager.LocalBackpack = new();
        PlayerBackpackManager.EquipCalls = 0;
        GearIDRange? requested = null;
        PlayerBackpackManager.Equip = range => requested = range;
        ForgeCore.Gear.Status = ForgeCore.GearAssetStatus.Ready;
        Time.time = 100f;
        ForgeCore.SpecialSlot.Local.Clear();
        HeavyWeapons.Clear("level cleanup");
        // The checkpoint was taken while a heavy weapon stood in the slot; the loan ended before the reload.
        var slot = ForgeCore.SpecialSlot.Local;
        var lease = slot.Take(Plugin.Id, () => "");
        slot.Remember("1", 1);
        slot.OwnLeaves(6, 45f);
        var saved = slot.Own!;
        slot.CaptureCheckpoint();
        lease.Dispose();
        slot.OwnReturned();
        slot.RecallCheckpoint();
        HeavyWeapons.Clear("checkpoint reload");
        ForgeCore.Checkpoint.Recalling=false;
        Time.time += 6.1f;
        HeavyWeapons.Tick(Time.time);
        Check(PlayerBackpackManager.EquipCalls == 1 && requested?.GetChecksum() == saved.Checksum,
            "checkpoint reconciliation: stalled native assembly requests the saved original weapon");

        var player = new PlayerAgent { Owner = SNet.LocalPlayer, IsLocallyOwned = true, gameObject = new() };
        var original = new BulletWeapon { Owner = player, gameObject = new() };
        PlayerManager.Local = player;
        PlayerManager.PlayerAgentsInLevel.Add(player);
        PlayerBackpackManager.LocalBackpack.Item = new() { GearIDRange = requested, Instance = original, IsLoaded = true };
        HeavyWeapons.Tick(Time.time + 0.1f);
        Check(slot.Own == null && !slot.Taken && original.GetCurrentClip() == saved.Clip
            && PlayerBackpackManager.LocalBackpack.AmmoStorage.SpecialAmmo.AmmoInPack == saved.Pool,
            "checkpoint reconciliation: verified fallback restores ammunition and releases the local loan gate");
        HeavyWeapons.Clear("level cleanup");
        PlayerBackpackManager.Equip = null;

        return count;
    }
}
