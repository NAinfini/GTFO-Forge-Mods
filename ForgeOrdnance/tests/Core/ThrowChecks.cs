using System.Reflection;
using ForgeOrdnance;
using GameData;
using Player;
using UnityEngine;

internal static class ThrowChecks
{
    internal static void Run(Action<bool, string> check, OrdnanceDefinition definition)
    {
        check(BeaconItem.BlockId > 0 && BeaconItem.BlockId <= ushort.MaxValue, "ball id fits native PrefabID");
        check(checked((ushort)BeaconItem.BlockId) == BeaconItem.BlockId && BeaconItem.BlockId == Messages.BeaconItemId,
            "native and protocol ball ids survive ushort wire roundtrip unchanged");
        ItemDataBlock.Blocks[114] = new() { name = "GlowStick", persistentID = 114 };
        var other = new ItemDataBlock { name = BeaconItem.BlockName, persistentID = BeaconItem.BlockId };
        ItemDataBlock.Blocks[BeaconItem.BlockId] = other;
        BeaconItem.Register();
        check(!BeaconItem.Ready && ReferenceEquals(ItemDataBlock.GetBlock(BeaconItem.BlockId), other), "even a same-name foreign block is never overwritten or adopted");
        ItemDataBlock.Blocks.Remove(BeaconItem.BlockId);
        // A prefab can be registered directly, without an ItemDataBlock, in any item mode.
        for (var mode = 0; mode < 3; mode++)
        {
            ItemSpawnManager.m_loadedPrefabsPerItemMode = new Dictionary<uint, object>?[3];
            ItemSpawnManager.m_loadedPrefabsPerItemMode[mode] = new() { [BeaconItem.BlockId] = new() };
            BeaconItem.Register();
            check(!BeaconItem.Ready && ItemDataBlock.GetBlock(BeaconItem.BlockId) == null, "prefab collision in mode " + mode + " refuses registration");
        }
        ItemSpawnManager.m_loadedPrefabsPerItemMode = null;
        BeaconItem.Register();
        var registered = ItemDataBlock.GetBlock(BeaconItem.BlockId);
        BeaconItem.Register();
        check(BeaconItem.Ready && BeaconItem.Failure == null && registered != null && registered.ConsumableAmmoMax == 1
            && ReferenceEquals(registered, ItemDataBlock.GetBlock(BeaconItem.BlockId)), "free id registers a one-charge ball idempotently");
        ItemDataBlock.Blocks[BeaconItem.BlockId] = other;
        BeaconItem.Register();
        check(!BeaconItem.Ready && ReferenceEquals(ItemDataBlock.GetBlock(BeaconItem.BlockId), other), "later replacement disables call-ins instead of trusting stale Ready");
        ItemDataBlock.Blocks[BeaconItem.BlockId] = registered!;
        BeaconItem.Register();
        check(BeaconItem.Ready && BeaconItem.Failure == null, "registration recognizes only its own native block on retry");

        var session = new OrdnanceSession { Loadout = new[] { definition } };
        session.Book.SetLoadout(OrdnanceSession.Local, session.Loadout, 1);
        var controller = new OrdnanceController(session);
        string? notice = null;
        controller.NoticePosted += text => notice = text;
        void Invoke(string name, params object[] args) => typeof(OrdnanceController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, args);
        void Frame(float seconds = 0.1f)
        {
            Time.time += seconds; Time.frameCount++;
            controller.Update();
            Input.Down.Clear(); ForgeCore.InputLease.Pressed.Clear();
        }
        ItemReplicationManager.delItemCallback callback = null!;
        void Holding()
        {
            controller.LevelLeft();
            PlayerManager.Agent = new();
            PlayerBackpackManager.LocalBackpack = new() { Item = new() { ItemID = 42 } };
            PlayerBackpackManager.LocalBackpack.AmmoStorage.ConsumableAmmo.AmmoInPack = 2.5f;
            PlayerBackpackManager.Restores = PlayerBackpackManager.Clears = ThrownBeacon.Created = 0;
            Plugin.Errors.Clear();
            Input.Down.Clear(); ForgeCore.InputLease.Pressed.Clear(); FocusStateManager.CurrentState = eFocusState.FPS;
            Invoke("BeginCarry", PlayerManager.Agent);
            Invoke("BallInHand", PlayerManager.Agent, true);
            Invoke("BeginHolding", definition);
            callback = null!;
            BeaconItem.CaptureThrow(new() { itemID_gearCRC = BeaconItem.BlockId }, ref callback);
        }
        void Spawn(ulong owner = 1, ItemReplicationManager.delItemCallback? request = null)
        {
            var item = new Item();
            var player = new PlayerAgent { Lookup = owner };
            if (owner == 1) (request ?? callback).Invoke(item, player);
            BeaconItem.NotifySpawn(new() { itemData = new() { itemID_gearCRC = BeaconItem.BlockId }, owner = new() { Player = player } },
                new() { Item = item });
        }
        void Restored(string name)
        {
            check(PlayerBackpackManager.Restores == 1 && PlayerBackpackManager.LocalBackpack!.Item?.ItemID == 42
                && PlayerBackpackManager.LocalBackpack.AmmoStorage.ConsumableAmmo.AmmoInPack == 2.5f
                && PlayerManager.Agent!.Inventory.WieldedSlot == InventorySlot.Special && PlayerManager.Agent.Sync.Wields == 1,
                name + ": consumable, exact ammo and weapon restored once");
            Frame(); Frame();
            check(PlayerBackpackManager.Restores == 1 && PlayerManager.Agent!.Sync.Wields == 1, name + ": later frames do not restore again");
        }

        OrdnanceCues.Sounds.Clear();
        Invoke("Typed", Direction.Down);
        check(OrdnanceCues.Sounds.SequenceEqual(new[] { OrdnanceSound.KeyDown }), "a correct code prefix plays the pitch of its own direction");
        OrdnanceCues.Sounds.Clear();
        Invoke("Typed", Direction.Left);
        check(OrdnanceCues.Sounds.SequenceEqual(new[] { OrdnanceSound.KeyWrong }), "a code that matches nothing plays the wrong-code sound and not the refusal sound");

        Holding();
        check(OrdnanceCues.Sounds.Contains(OrdnanceSound.CodeComplete), "accepting the code plays the code-complete sound");
        check(HandPulse.Started == 1, "accepting the code pulses the ball's light once, locally");
        Frame(20);
        check(controller.CurrentPhase == OrdnancePhase.Holding && PlayerBackpackManager.Restores == 0, "a charged ball really in hand stays Holding without an arbitrary timeout");
        OrdnanceCues.Sounds.Clear();
        Input.Down.Add(KeyCode.Mouse3); Frame();
        Restored("call key put-away");
        check(OrdnanceCues.Sounds.SequenceEqual(new[] { OrdnanceSound.CallCancel }), "cancelling the call plays the cancel sound once");

        Holding(); ForgeCore.InputLease.Pressed.Add(InputAction.SelectStandard); Frame(); Restored("weapon switch put-away");
        Holding(); PlayerManager.Agent!.Inventory.WieldedSlot = InventorySlot.Standard; Frame(); Restored("wielded-slot switch put-away");

        Holding();
        PlayerBackpackManager.LocalBackpack!.AmmoStorage.ConsumableAmmo.AmmoInPack = 0;
        PlayerManager.Agent!.Inventory.WieldedSlot = InventorySlot.Standard;
        ForgeCore.InputLease.Pressed.Add(InputAction.SelectStandard); // auto-wield and consumption in the same frame
        Frame();
        check(controller.CurrentPhase == OrdnancePhase.Idle && controller.Held == null && !OrdnanceController.InputHeld,
            "zero-charge ball still in slot leaves Holding/THROW before switch cancellation");
        Spawn(); Spawn();
        check(ThrownBeacon.Created == 1, "consumption then repeated spawn binds exactly one throw");
        Frame(3.1f); Restored("consumed ball still in slot");

        Holding(); Spawn();
        check(controller.CurrentPhase == OrdnancePhase.Idle, "spawn before consumption leaves Holding/THROW");
        PlayerBackpackManager.LocalBackpack!.AmmoStorage.ConsumableAmmo.AmmoInPack = 0;
        Frame(); Spawn(); Frame(3.1f);
        check(ThrownBeacon.Created == 1, "spawn then consumption does not transition or bind twice");
        Restored("spawn first");

        Holding(); PlayerBackpackManager.LocalBackpack!.Item = null; Frame();
        check(controller.CurrentPhase == OrdnancePhase.Idle, "ball leaving slot detects throw without spawn hook");
        Frame(); Frame(); Restored("slot emptied");
        Input.Down.Add(KeyCode.Mouse3); Frame();
        check(PlayerBackpackManager.LocalBackpack.Item?.ItemID == 42, "new call cannot replace an unresolved throw's definition");
        Frame(5.1f);
        check(notice == "The beacon never appeared." && Plugin.Errors.Any(s => s.Contains("never appeared")), "missing replicated spawn reports finite timeout and keeps restored inventory");
        Restored("missing spawn");

        Holding(); PlayerBackpackManager.LocalBackpack!.AmmoStorage.ConsumableAmmo.AmmoInPack = 0;
        Frame(); Frame(3.1f); Restored("zero charges, failed replication and no native cleanup");
        Frame(2.1f);
        check(controller.CurrentPhase == OrdnancePhase.Idle && notice == "The beacon never appeared.", "original stuck case terminates with an error even with no spawn or cleanup callback");

        Holding();
        ThrowWatch.ThrowStarted((Gear.ThrowingWeapon)PlayerManager.Agent!.Inventory.WieldedItem!);
        Frame(); Frame(5.1f);
        check(controller.CurrentPhase == OrdnancePhase.Idle && notice == "The beacon never appeared.", "throw failing before charge consumption exits at finite deadline");
        Restored("failed throw before consumption");

        Holding();
        var ball = (Gear.ThrowingWeapon)PlayerManager.Agent!.Inventory.WieldedItem!;
        var stick = new Gear.ThrowingWeapon { ItemDataBlock = GameData.ItemDataBlock.GetBlock(114) ?? ball.ItemDataBlock };
        ThrowWatch.SilenceNativeThrow(ball);
        check(ball.m_throwSoundEvent == "" && stick.m_throwSoundEvent == "PLAY_THROW", "the ball loses its native throw sound; other throwables keep theirs");
        Holding(); Spawn(2);
        ThrowWatch.ThrowStarted(new() { ItemDataBlock = registered, Owner = new() { Lookup = 2 } });
        Frame(6f);
        check(controller.CurrentPhase == OrdnancePhase.Holding && ThrownBeacon.Created == 0, "another player's ball cannot finish the local throw");
        PlayerBackpackManager.LocalBackpack = null; Frame();
        check(controller.CurrentPhase == OrdnancePhase.Holding, "temporarily unavailable backpack is not evidence of consumption");
        Holding();
        var oldCallback = callback;
        PlayerBackpackManager.LocalBackpack!.Item = null; Frame(); Frame(5.1f);
        Holding();
        var freshCallback = callback;
        Spawn(request: oldCallback);
        check(controller.CurrentPhase == OrdnancePhase.Holding && ThrownBeacon.Created == 0 && PlayerBackpackManager.Restores == 0,
            "late expired A callback and its generic spawn cannot bind or consume B");
        Spawn(request: freshCallback);
        check(ThrownBeacon.Created == 1, "B binds only its own native callback after delayed A");
        Frame(3.1f); Restored("B after delayed A");

        Holding();
        GTFO.API.EventAPI.ReachCheckpoint();
        check(PlayerBackpackManager.LocalBackpack!.Item?.ItemID == 42 && PlayerBackpackManager.Restores == 1,
            "checkpoint taken while holding remembers and restores original consumable");
        PlayerBackpackManager.LocalBackpack.Item = new() { ItemID = 99 };
        PlayerBackpackManager.LocalBackpack.AmmoStorage.ConsumableAmmo.AmmoInPack = 7;
        Invoke("BeginCarry", PlayerManager.Agent!); Invoke("BeginHolding", definition);
        CheckpointManager.IsReloadingCheckpoint = true;
        GTFO.API.EventAPI.ReloadCheckpoint();
        Frame(); Frame();
        check(PlayerBackpackManager.Restores == 1, "checkpoint original is not written while native reload runs");
        CheckpointManager.IsReloadingCheckpoint = false;
        SNetwork.SNet.Capture.IsRecalling = true; Frame();
        check(PlayerBackpackManager.Restores == 1, "checkpoint restoration also waits for packet recall");
        PlayerBackpackManager.LocalBackpack.Item = new() { ItemID = BeaconItem.BlockId };
        SNetwork.SNet.Capture.IsRecalling = false;
        PlayerManager.Agent!.Alive = false; Frame();
        check(PlayerBackpackManager.Restores == 1, "checkpoint restoration stays pending while downed");
        PlayerManager.Agent.Alive = true; Frame();
        check(PlayerBackpackManager.Restores == 2 && PlayerBackpackManager.LocalBackpack.Item?.ItemID == 42
            && PlayerBackpackManager.LocalBackpack.AmmoStorage.ConsumableAmmo.AmmoInPack == 2.5f,
            "native-replayed ball is replaced by checkpoint original, never the post-checkpoint item");
        GTFO.API.EventAPI.ReloadCheckpoint(); Frame(); Frame();
        check(PlayerBackpackManager.Restores == 3 && PlayerBackpackManager.LocalBackpack.Item?.ItemID == 42,
            "repeated reloads retain the checkpoint carry snapshot");
        GTFO.API.EventAPI.ReachCheckpoint(); // no temporary carry this time: native inventory owns the new checkpoint
        PlayerBackpackManager.LocalBackpack.Item = new() { ItemID = 99 };
        GTFO.API.EventAPI.ReloadCheckpoint(); Frame(); Frame();
        check(PlayerBackpackManager.Restores == 3 && PlayerBackpackManager.LocalBackpack.Item?.ItemID == 99,
            "a checkpoint without a temporary ball clears its old carry snapshot");

        Holding(); session.ThrowFloor = 40; Spawn();
        var thrown = (List<ThrownBeacon>)typeof(OrdnanceController).GetField("_thrown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!;
        check(thrown.Count == 1 && thrown[0].ThrowId == 41, "restarted client resumes above host throw floor");
        session.ThrowFloor = 0;
        controller.LevelLeft();
    }
}
