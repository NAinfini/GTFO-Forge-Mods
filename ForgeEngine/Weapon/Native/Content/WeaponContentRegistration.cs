using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using Gear;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ForgeCore;
using SNetwork;
using InventorySlot = Player.InventorySlot;
using PlayerBackpack = Player.PlayerBackpack;
using PlayerBackpackManager = Player.PlayerBackpackManager;

namespace ForgeWeapon.Native;

internal sealed partial class WeaponNativeSession
{
    /// <summary>The native build's strict check of one entry's thermal sight (Native/Thermal/ThermalSight.cs).</summary>
    static partial void NativeThermalCheck(WeaponFieldSet entry, Action<string> refused);

    private OnceReporter? _projectionOnce;

    partial void NativeContentRegister()
    {
        WeaponFieldHooks.Bind(this);
        ObserveContentPlacementLifecycle();
        Identity.Registration.RegisterContentDomain("weapon", entry => InstallContent(entry.Id, entry.BlockIds, entry.ContentJson, entry.Path));
        ContentEntryGeneration.RegisterDomain(this);
    }

    internal ContentRegistrationResult InstallContent(string id, IReadOnlyDictionary<string, uint> issued, string json, string path,
        Func<string, uint, JsonElement>? originalBlock = null)
    {
        try
        {
            var definition = WeaponContentDefinition.Read(id, issued, json, path,
                originalBlock ?? ((type, number) => WeaponContentReferences.ProjectOriginalBlock(NativeDataBlockTables.RequireBlock(type, number), type)));
            foreach (var original in definition.Originals) NativeDataBlockTables.RequireBlock(original.Type, original.Id);
            // Validate only this entry strictly: an unrelated, previously refused disk projection must not
            // prevent a valid content entry from being admitted.
            void Refused(string reason) => throw new InvalidOperationException(reason);
            var candidate = WeaponFieldSet.Empty.With(definition.Fields);
            GearPartTransformData.Project(candidate, Refused);
            GearPartFlashlightData.Project(candidate, Refused);
            WeaponFireNoiseData.Project(candidate, Refused);
            NativeThermalCheck(candidate, Refused);
            var fields = Fields.With(definition.Fields);
            // Prepare every consumer before publishing any native row.
            var projected = Project(fields);
            string modelOwner = "weapon:" + id;
            var modelRegistration = WeaponModelAssemblies.Prepare(modelOwner, definition.Models, path, _report);
            var models = Models.With(issued[id].ToString(System.Globalization.CultureInfo.InvariantCulture), modelOwner, definition.Models, definition.Appearance, definition.PartAppearances, definition.Grips,
                definition.LeftHandShape, definition.RightHandShape, definition.PartPoints, definition.Followers,
                definition.Blocks.FirstOrDefault(row => row.Type == "GearCategory")?.PersistentId ?? 0);
            modelRegistration.Commit();
            try
            {
                WeaponGearCatalogue.Register(definition, () => ContentEntryGeneration.Publish(definition.Blocks.Select(block => new NativeDataBlockRegistration(
                    block.Type, block.Name, block.PersistentId, block.Json, block.SourceId)).ToArray(), strictFields: true));
            }
            catch { modelRegistration.Rollback(); throw; }
            // A generation that does not stand puts the previous registration back.
            if (ContentEntryGeneration.Current is { } generation) { generation.OnUndo(modelRegistration.Rollback); generation.OnRelease(modelRegistration.Settle); }
            _nextModelOwners?.Add(modelOwner);
            // All parsing/projection/allocation and native readback succeeded. Commit the prepared snapshot.
            Use(fields, projected, models);
            return ContentRegistrationResult.Accept();
        }
        catch (Exception error) { return NativeContentRefusal.Of(error, id, "weapon-content-invalid"); }
    }

    private readonly record struct Projection(GearPartTransformApplier Parts, GearPartFlashlightApplier Flashlights, WeaponFireNoise Noise,
        WeaponFieldApplier Applier);

    /// <summary>Every consumer of a field set, prepared before any native row of the content is published.</summary>
    private Projection Project(WeaponFieldSet fields)
    {
        // Preserve this session's exact clone and pending-request ownership across content snapshots.
        // No native write or disposal is introduced after the table publication commit point.
        var extras = GearParts.ExtraSpawner;
        // Every install projects the whole accumulated field set again, so a refusal of an earlier document
        // would be written once per entry; the projections report through one counting gate instead.
        var once = _projectionOnce ??= new OnceReporter(_report);
        void Projected(string message) => once.Report(message, message);
        var parts = new GearPartTransformApplier(GearPartTransformData.Project(fields, Projected), _report, _note, Log, extras);
        var flashlights = new GearPartFlashlightApplier(GearPartFlashlightData.Project(fields, Projected), _report, Log);
        var noise = new WeaponFireNoise(WeaponFireNoiseData.Project(fields, Projected), _report, Log);
        WeaponFieldHooks.RequireOwner(this);
        var applier = new WeaponFieldApplier(fields, Log);
        WeaponFieldHooks.PrepareScannerBindings(applier);
        // The per-frame bodies this content needs go on with its preparation, before any native table is committed.
        WeaponFieldHooks.EnsureScanner(fields);
        if (noise.BlockCount != 0) WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.NoiseSetter);
        NativeThermalSights(fields, _report, Log); // A later refusal leaves a look for a gear that never exists, which nothing reads.
        return new(parts, flashlights, noise, applier);
    }

    private void Use(WeaponFieldSet fields, Projection projected, WeaponModelAssemblies models)
    {
        Fields = fields; GearParts = projected.Parts; Models = models; Flashlights = projected.Flashlights; FireNoise = projected.Noise;
        WeaponFieldHooks.Use(projected.Applier);
    }
}

/// <summary>Late content must join both native gear indexes, not just the DataBlock tables the startup loader
/// already scanned. Prepare private copies first; a failed table transaction restores the exact original pools.</summary>
internal static class WeaponGearCatalogue
{
    private enum NativeCatalogueState { AwaitingOfflineLoad, Ready, Unknown }
    private static bool _unknown;

    internal static void Register(WeaponContentDefinition definition, Action publishTables)
    {
        if (_unknown) throw new RuntimeContractException("content-native-state-unknown", "Weapon gear catalogue requires a process restart.");
        var block = definition.Blocks.SingleOrDefault(row => row.Type == "PlayerOfflineGear");
        var manager = GearManager.Current;
        // Before the game's containers exist, its own LoadOfflineGearDatas will scan these newly published rows.
        if (block == null || manager == null || manager.m_allGearPerInstanceKey == null || manager.m_gearPerSlot == null)
        { publishTables(); return; }
        var originalIndex = manager.m_allGearPerInstanceKey;
        var originalPools = manager.m_gearPerSlot;
        var state = State(originalIndex, originalPools);
        // SetupGearInOfflineMode calls LoadOfflineGearDatas and then tail-calls OnGearLoadingDone on the same
        // simulation thread. Before that call, publish only the DataBlock: the native loader will add it to the
        // slot pool and OnGearLoadingDone will build the instance-key index. Pre-populating either collection here
        // gives the native pass a second copy and made it report every Forge record as a duplicate.
        if (state == NativeCatalogueState.AwaitingOfflineLoad) { publishTables(); return; }
        if (state != NativeCatalogueState.Ready)
            throw new RuntimeContractException("content-native-state-unknown",
                "Weapon gear catalogue pools and instance-key index are only partially initialized.");
        using var document = JsonDocument.Parse(block.Json);
        var root = document.RootElement;
        var type = root.TryGetProperty("Type", out var value)
            ? value.ValueKind == JsonValueKind.String ? Enum.Parse<eOfflineGearType>(value.GetString()!, false) : (eOfflineGearType)value.GetInt32()
            : (PlayerOfflineGearDataBlock.GetBlock(block.SourceId!.Value)
                ?? throw new InvalidOperationException("weapon-gear-source: " + block.Id)).Type;
        if ((int)type is not (1 or 2)) throw new InvalidOperationException("weapon-gear-type: " + block.Id);
        var record = EquipmentNativeAdapter.GearBlockPrefix + block.PersistentId.ToString(CultureInfo.InvariantCulture);
        // The previous content generation's gear of this record is replaced in its place; any other holder is a collision.
        GearIDRange? previous = null;
        bool known = originalIndex.ContainsKey(record);
        bool replaces = known && ContentEntryGeneration.Current is { } generation && generation.Previously("PlayerOfflineGear", block.PersistentId)
            && (previous = originalIndex[record]) != null;
        if (!replaces && known) throw new RuntimeContractException("content-id-collision", record);
        // Same constructor and identity fields as the native ParseAndStashGear (reviewed in w5-gear-block).
        var gear = new GearIDRange(root.GetProperty("GearJSON").GetString()!)
        { PlayfabItemId = block.Id, PlayfabItemInstanceId = record, OfflineGearType = type };
        var category = definition.Blocks.Single(row => row.Type == "GearCategory");
        if (gear.GetCompID(eGearComponent.Category) != category.PersistentId)
            throw new InvalidOperationException("weapon-gear-category: " + block.Id);
        uint itemId = gear.GetCompID(eGearComponent.BaseItem);
        var ownedItem = definition.Blocks.SingleOrDefault(row => row.Type == "Item" && row.PersistentId == itemId);
        int slot;
        if (ownedItem == null)
        {
            var item = ItemDataBlock.GetBlock(itemId);
            if (item == null) throw new InvalidOperationException("weapon-gear-base-item: " + block.Id);
            slot = (int)item.inventorySlot;
        }
        else
        {
            using var itemDocument = JsonDocument.Parse(ownedItem.Json);
            if (!itemDocument.RootElement.TryGetProperty("inventorySlot", out var itemSlot))
                throw new InvalidOperationException("weapon-gear-base-item: " + block.Id);
            slot = itemSlot.ValueKind == JsonValueKind.String
                ? (int)Enum.Parse<Player.InventorySlot>(itemSlot.GetString()!, false)
                : itemSlot.GetInt32();
        }
        if (slot < 0 || slot >= originalPools.Length || originalPools[slot] == null)
            throw new InvalidOperationException("weapon-gear-slot: " + block.Id);
        var index = new Il2CppSystem.Collections.Generic.Dictionary<string, GearIDRange>();
        foreach (var pair in originalIndex) index.Add(pair.Key, replaces && pair.Key == record ? gear : pair.Value);
        if (!replaces) index.Add(record, gear);
        var pools = new Il2CppReferenceArray<Il2CppSystem.Collections.Generic.List<GearIDRange>>(originalPools.Length);
        // Once published, the new array is the only holder of every slot list and of the new pool: stored with the collector's
        // write barrier, or the incremental collector frees them while the gear table still points at them.
        // A replaced gear keeps its place when it stays in its slot; a gear that moved to another slot goes to that slot's end.
        bool placed = false;
        for (int at = 0; at < pools.Length; at++)
        {
            var list = originalPools[at];
            if (previous != null && list != null && Contains(list, previous))
            {
                var copy = new Il2CppSystem.Collections.Generic.List<GearIDRange>();
                foreach (var held in list)
                    if (held.Pointer != previous.Pointer) copy.Add(held);
                    else if (at == slot) { copy.Add(gear); placed = true; }
                list = copy;
            }
            Il2CppArrays.Store(pools, at, list);
        }
        if (!placed)
        {
            var pool = new Il2CppSystem.Collections.Generic.List<GearIDRange>();
            foreach (var held in pools[slot]) pool.Add(held);
            pool.Add(gear); Il2CppArrays.Store(pools, slot, pool);
        }
        Publish(manager, originalIndex, originalPools, index, pools, block.Id, publishTables);
        if (_replaced != null) _replaced[record] = gear;
    }

    private static bool Contains(Il2CppSystem.Collections.Generic.List<GearIDRange> list, GearIDRange gear)
    {
        foreach (var held in list) if (held != null && held.Pointer == gear.Pointer) return true;
        return false;
    }

    // All callers run synchronously on the simulation thread; no consumer observes the prepared indexes before the native table
    // commit. A failure puts the original containers back; within a content generation, a generation that does not stand does too.
    private static void Publish(GearManager manager, Il2CppSystem.Collections.Generic.Dictionary<string, GearIDRange> originalIndex,
        Il2CppReferenceArray<Il2CppSystem.Collections.Generic.List<GearIDRange>> originalPools,
        Il2CppSystem.Collections.Generic.Dictionary<string, GearIDRange> index,
        Il2CppReferenceArray<Il2CppSystem.Collections.Generic.List<GearIDRange>> pools, string label, Action? publishTables)
    {
        try
        {
            manager.m_allGearPerInstanceKey = index;
            manager.m_gearPerSlot = pools;
            if (manager.m_allGearPerInstanceKey.Pointer != index.Pointer || manager.m_gearPerSlot.Pointer != pools.Pointer)
                throw new InvalidOperationException("weapon-gear-publication: " + label);
            publishTables?.Invoke();
        }
        catch (Exception error)
        {
            try { Restore(manager, originalIndex, originalPools); }
            catch (Exception rollback)
            {
                _unknown = true;
                throw new RuntimeContractException("content-native-state-unknown", new AggregateException(error, rollback).Message);
            }
            throw;
        }
        ContentEntryGeneration.Current?.OnUndo(() => Restore(manager, originalIndex, originalPools));
    }

    private static void Restore(GearManager manager, Il2CppSystem.Collections.Generic.Dictionary<string, GearIDRange> index,
        Il2CppReferenceArray<Il2CppSystem.Collections.Generic.List<GearIDRange>> pools)
    {
        manager.m_gearPerSlot = pools;
        manager.m_allGearPerInstanceKey = index;
        if (manager.m_allGearPerInstanceKey.Pointer != index.Pointer || manager.m_gearPerSlot.Pointer != pools.Pointer)
        {
            _unknown = true;
            throw new InvalidOperationException("Weapon gear catalogue rollback readback differs.");
        }
    }

    // Within a content generation: the gear this generation published under each record.
    private static Dictionary<string, GearIDRange>? _replaced;

    internal static void BeginGeneration() => _replaced = new Dictionary<string, GearIDRange>(StringComparer.Ordinal);

    /// <summary>After a generation's entries: the records of the gear rows it withdrew leave both native indexes, then every
    /// lobby loadout (this machine's player and, on the host, its bots) that holds a Forge gear is equipped again through the
    /// game's own entry: a replaced gear with its new record, a withdrawn one with the first gear of the slot's pool that is not a
    /// Forge entry (logged once per player and slot). The game rebuilds its favourites from the new pools first
    /// (<c>GearManager.RescanFavorites</c>). A generation that does not stand gets the previous containers, favourites and gear
    /// back.</summary>
    internal static void EndGeneration(ContentEntryGeneration generation, WeaponLog log)
    {
        var replaced = _replaced ?? new Dictionary<string, GearIDRange>(StringComparer.Ordinal);
        _replaced = null;
        var manager = GearManager.Current;
        if (manager == null || manager.m_allGearPerInstanceKey == null || manager.m_gearPerSlot == null
            || State(manager.m_allGearPerInstanceKey, manager.m_gearPerSlot) != NativeCatalogueState.Ready) return;
        var withdrawn = new HashSet<string>(generation.Withdrawn.Where(row => row.Type == "PlayerOfflineGear")
            .Select(row => EquipmentNativeAdapter.GearBlockPrefix + row.Id.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal);
        if (withdrawn.Count != 0) Withdraw(manager, withdrawn);
        if (replaced.Count == 0 && withdrawn.Count == 0) return;
        Reinstall(manager, generation, replaced, withdrawn, log);
    }

    private static void Withdraw(GearManager manager, HashSet<string> records)
    {
        var originalIndex = manager.m_allGearPerInstanceKey;
        var originalPools = manager.m_gearPerSlot;
        var index = new Il2CppSystem.Collections.Generic.Dictionary<string, GearIDRange>();
        foreach (var pair in originalIndex) if (!records.Contains(pair.Key)) index.Add(pair.Key, pair.Value);
        var pools = new Il2CppReferenceArray<Il2CppSystem.Collections.Generic.List<GearIDRange>>(originalPools.Length);
        for (int at = 0; at < pools.Length; at++)
        {
            var list = originalPools[at];
            bool holds = false;
            if (list != null) foreach (var held in list) holds |= held != null && records.Contains(held.PlayfabItemInstanceId ?? "");
            if (holds)
            {
                var copy = new Il2CppSystem.Collections.Generic.List<GearIDRange>();
                foreach (var held in list!) if (held != null && !records.Contains(held.PlayfabItemInstanceId ?? "")) copy.Add(held);
                list = copy;
            }
            Il2CppArrays.Store(pools, at, list);
        }
        Publish(manager, originalIndex, originalPools, index, pools, "withdrawn", null);
    }

    private static readonly InventorySlot[] GearSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearMelee, InventorySlot.GearClass };

    private static void Reinstall(GearManager manager, ContentEntryGeneration generation, Dictionary<string, GearIDRange> replaced,
        HashSet<string> withdrawn, WeaponLog log)
    {
        // What the game held before, put back when the generation does not stand.
        var favorites = GearManager.FavoritesData;
        var lastEquipped = manager.m_lastEquippedGearPerSlot;
        var favoritePools = manager.m_favoriteGearPerSlot;
        var saved = favorites == null ? null : new[] { favorites.LastEquipped_Standard, favorites.LastEquipped_Special, favorites.LastEquipped_Melee, favorites.LastEquipped_Class };
        var held = new List<(SNet_Player Player, InventorySlot Slot, GearIDRange Gear)>();
        foreach (var (player, backpack) in Loadouts())
            foreach (var slot in GearSlots)
                if (backpack.Slots[(int)slot]?.GearIDRange is { } gear) held.Add((player, slot, gear));
        generation.OnUndo(() =>
        {
            PutBack(manager, lastEquipped, favoritePools, favorites, saved);
            foreach (var (player, slot, gear) in held)
                if (replaced.ContainsKey(gear.PlayfabItemInstanceId ?? "") || withdrawn.Contains(gear.PlayfabItemInstanceId ?? "")) Equip(player, gear);
        });
        manager.RescanFavorites();
        foreach (var (player, slot, gear) in held)
        {
            var record = gear.PlayfabItemInstanceId ?? "";
            string who = player.IsBot ? "bot" : "local";
            if (replaced.TryGetValue(record, out var next))
            {
                var status = Equip(player, next);
                var level = status == "Ready" ? RuntimeLogLevel.Info : RuntimeLogLevel.Error;
                if (log.Logs(level))
                    log.Write(level, WeaponLogCodes.LoadoutGearReequipped, new RuntimeLogFacts
                    {
                        Values = new[] { RuntimeLogValue.Of("slot", slot.ToString()), RuntimeLogValue.Of("record", record), RuntimeLogValue.Of("player", who), RuntimeLogValue.Of("status", status) }
                    });
                continue;
            }
            if (!withdrawn.Contains(record)) continue;
            var fallback = Fallback(manager, slot);
            string result = "no-fallback";
            if (fallback != null)
            {
                if (!player.IsBot) GearManager.RegisterGearInSlotAsEquipped(fallback.PlayfabItemInstanceId, slot);
                result = Equip(player, fallback);
            }
            if (log.Logs(RuntimeLogLevel.Error))
                log.Write(RuntimeLogLevel.Error, WeaponLogCodes.LoadoutGearWithdrawn, new RuntimeLogFacts
                {
                    Values = new[]
                    {
                        RuntimeLogValue.Of("slot", slot.ToString()), RuntimeLogValue.Of("withdrawn", record),
                        RuntimeLogValue.Of("fallback", fallback?.PlayfabItemInstanceId ?? ""), RuntimeLogValue.Of("player", who), RuntimeLogValue.Of("status", result)
                    }
                });
        }
    }

    private static void PutBack(GearManager manager, Il2CppReferenceArray<GearIDRange> lastEquipped,
        Il2CppReferenceArray<Il2CppSystem.Collections.Generic.List<GearIDRange>> favoritePools, GearFavoritesData? favorites, string[]? saved)
    {
        manager.m_lastEquippedGearPerSlot = lastEquipped;
        manager.m_favoriteGearPerSlot = favoritePools;
        if (favorites == null || saved == null) return;
        favorites.LastEquipped_Standard = saved[0]; favorites.LastEquipped_Special = saved[1];
        favorites.LastEquipped_Melee = saved[2]; favorites.LastEquipped_Class = saved[3];
    }

    /// <summary>This machine's player and, on the host, its bots: the loadouts this machine equips.</summary>
    private static List<(SNet_Player Player, PlayerBackpack Backpack)> Loadouts()
    {
        // Bots are not in SNet.LobbyPlayers (the humans only): the slots hold both.
        var found = new List<(SNet_Player, PlayerBackpack)>();
        foreach (var player in SNet.Slots.SlottedPlayers)
        {
            if (player == null || !(player.IsLocal || player.IsBot && SNet.IsMaster)) continue;
            if (PlayerBackpackManager.TryGetBackpack(player, out var backpack) && backpack?.Slots != null) found.Add((player, backpack));
        }
        return found;
    }

    /// <summary>The slot's first gear that is not a Forge entry (the game's own pool order), else its first gear.</summary>
    private static GearIDRange? Fallback(GearManager manager, InventorySlot slot)
    {
        var pool = manager.m_gearPerSlot[(int)slot];
        if (pool == null || pool.Count == 0) return null;
        foreach (var gear in pool)
        {
            var id = EquipmentNativeAdapter.GearBlockId(gear);
            if (id == null || !NativeDataBlockTables.IsPublished("PlayerOfflineGear", uint.Parse(id, CultureInfo.InvariantCulture))) return gear;
        }
        return pool[0];
    }

    private static string Equip(SNet_Player player, GearIDRange gear)
        => (player.IsBot ? ForgeCore.Gear.EquipBot(player, gear) : ForgeCore.Gear.EquipLocal(gear)).Status.ToString();

    /// <summary>The native catalogue's own readback boundary. Before its one offline load both collections contain
    /// no gear. After <c>OnGearLoadingDone</c>, every gear already in a slot pool has its instance id in the index.
    /// The native load and index pass are one synchronous call chain, so a partial relationship is not a phase a
    /// content registration may join; it is refused rather than guessed.</summary>
    private static NativeCatalogueState State(Il2CppSystem.Collections.Generic.Dictionary<string, GearIDRange> index,
        Il2CppReferenceArray<Il2CppSystem.Collections.Generic.List<GearIDRange>> pools)
    {
        var records = new HashSet<string>(StringComparer.Ordinal);
        bool any = false;
        for (int slot = 0; slot < pools.Length; slot++)
        {
            var pool = pools[slot];
            if (pool == null) continue;
            foreach (var gear in pool)
            {
                any = true;
                var record = gear?.PlayfabItemInstanceId;
                if (string.IsNullOrEmpty(record) || !records.Add(record) || !index.ContainsKey(record))
                    return NativeCatalogueState.Unknown;
            }
        }
        if (!any) return index.Count == 0 ? NativeCatalogueState.AwaitingOfflineLoad : NativeCatalogueState.Unknown;
        return NativeCatalogueState.Ready;
    }
}


internal sealed partial class WeaponNativeSession
{
    // Only invalidates asynchronous requests. Runtime remains the source of world and session lifetime.
    private long _contentGeneration;
    private readonly List<RuntimeWorkLease> _modelBoundaries = new();

    private void ObserveContentPlacementLifecycle()
    {
        Identity.Registration.ObserveLifecycle(value =>
        {
            if (value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged)
            {
                ReleaseModelBoundaries();
                if (value.Current.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped))
                {
                    long epoch = value.Current.WorldEpoch;
                    _modelBoundaries.Add(RuntimeUpstreamEvents.Subscribe(RuntimeUpstreamKind.BuildStarted,
                        "ForgeWeapon.Models.BuildStarted", epoch, _ => Guard(static session => session.Models.AwaitLevel()), replayCurrent: false));
                    _modelBoundaries.Add(RuntimeUpstreamEvents.Subscribe(RuntimeUpstreamKind.BuildDone,
                        "ForgeWeapon.Models.BuildDone", epoch, _ => Guard(static session => session.Models.LevelBuilt()), replayCurrent: false));
                    _modelBoundaries.Add(RuntimeUpstreamEvents.Subscribe(RuntimeUpstreamKind.LevelCleanup,
                        "ForgeWeapon.Models.LevelCleanup", epoch, _ => Guard(static session => session.Models.LevelEnded()), replayCurrent: false));
                }
            }
            if (value.Kind is RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored
                || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            {
                _contentGeneration++;
                ReleaseNativeContent(detach: value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped);
            }
        });
    }

    private void ReleaseModelBoundaries()
    {
        foreach (var lease in _modelBoundaries) lease.Dispose();
        _modelBoundaries.Clear();
    }
    // Host owner must call this at startup failure, domain Fault and normal Dispose.
    // Kept in this partial so ownership and retries remain inside Content.
    partial void PrepareExtraResourceRetirement();
    partial void NativeContentDetach()
    {
        // Capture eligible resource receipts before invalidating their placement permission.
        PrepareExtraResourceRetirement();
        ContentEntryGeneration.UnregisterDomain(this);
        _contentGeneration++;
        ReleaseNativeContent(detach: true);
    }

    private void ReleaseNativeContent(bool detach)
    {
        List<Exception>? errors = null;
        try { if (detach) ReleaseModelBoundaries(); }
        catch (Exception error) { (errors ??= new()).Add(error); }
        try
        {
            if (detach) WeaponFieldHooks.Detach(this);
        }
        catch (Exception error) { (errors ??= new()).Add(error); }
        try
        {
            if (GearParts?.ExtraSpawner is { } extras)
            {
                if (detach) extras.Dispose();
                else extras.Clear();
            }
        }
        catch (Exception error) { (errors ??= new()).Add(error); }
        try { if (detach) Models?.Dispose(); }
        catch (Exception error) { (errors ??= new()).Add(error); }
        if (errors != null) throw new AggregateException("Weapon content cleanup failed.", errors);
    }

}
