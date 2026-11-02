using System;
using GameData;

namespace ForgeCore;

/// <summary>Adding data blocks (gear, archetypes, items ...) to the game's tables. Blocks are only added, never changed: an ID the
/// game or another mod already uses is refused, so mods cannot overwrite each other.</summary>
public static class DataBlocks
{
    private static readonly DataBlockLedger Ledger = new();

    /// <summary>A shallow copy of a donor block under a new name and ID, enabled; nested reference data is shared with the donor,
    /// so change only a nested object that was cloned too. Add it with <see cref="Add{T}"/>.</summary>
    public static T Clone<T>(T donor, string name, uint id) where T : GameDataBlockBase<T>
    {
        if (donor == null) throw new ArgumentNullException(nameof(donor));
        var copy = donor.MemberwiseClone().Cast<T>();
        copy.name = name;
        copy.persistentID = id;
        copy.internalEnabled = true;
        return copy;
    }

    /// <summary>Whether <paramref name="block"/> could be added by <paramref name="modId"/> now: Free, AlreadyAdded or Conflict.
    /// Nothing changes; use it to check a whole set before adding the first block.</summary>
    public static DataBlockResult Check<T>(string modId, T block) where T : GameDataBlockBase<T>
    {
        if (block == null) throw new ArgumentNullException(nameof(block));
        return Ledger.Decide(typeof(T).Name, block.persistentID, modId, block.name, NameOf<T>(block.persistentID));
    }

    // GetBlock logs an error for an ID the table does not hold, so it is only asked after HasBlock said yes.
    private static string? NameOf<T>(uint id) where T : GameDataBlockBase<T>
        => GameDataBlockBase<T>.HasBlock(id) ? GameDataBlockBase<T>.GetBlock(id)?.name : null;

    /// <summary>Adds <paramref name="block"/> through the game's own table entry and reads it back. A block this mod added before
    /// (same ID and name) is AlreadyAdded; any other block under that ID is a Conflict and nothing changes.</summary>
    public static DataBlockResult Add<T>(string modId, T block) where T : GameDataBlockBase<T>
    {
        var decision = Check(modId, block);
        if (decision != DataBlockResult.Free)
        {
            if (decision == DataBlockResult.Conflict)
                Log.For(modId).Error("Data block " + typeof(T).Name + "/" + block.persistentID + " (" + block.name + ") refused: the ID is taken by "
                    + (Ledger.OwnerOf(typeof(T).Name, block.persistentID) ?? "the game or another loader") + ".");
            return decision;
        }
        // The game's call can throw after inserting; ownership is recorded from the readback so a retry finds it.
        try { GameDataBlockBase<T>.AddBlock(block); }
        finally
        {
            if (NameOf<T>(block.persistentID) == block.name) Ledger.Owns(typeof(T).Name, block.persistentID, modId, block.name);
        }
        if (NameOf<T>(block.persistentID) == block.name) return DataBlockResult.Added;
        Log.For(modId).Error("Data block " + typeof(T).Name + "/" + block.persistentID + " (" + block.name + ") did not read back after adding it.");
        return DataBlockResult.ReadbackFailed;
    }
}
