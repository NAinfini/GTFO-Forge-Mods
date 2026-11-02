using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>The answer of <c>DataBlocks.Check</c> and <c>DataBlocks.Add</c>.</summary>
public enum DataBlockResult
{
    /// <summary>The ID is free (<c>Check</c> only).</summary>
    Free,
    /// <summary>Added and read back.</summary>
    Added,
    /// <summary>This mod added the same block (same ID and name) before; nothing changed.</summary>
    AlreadyAdded,
    /// <summary>The ID belongs to the game or another mod (or this mod under another name); nothing changed.</summary>
    Conflict,
    /// <summary>The game's table did not show the block after adding it.</summary>
    ReadbackFailed,
}

/// <summary>Which mod added which data block: blocks are only added, never changed, and an ID taken by anyone else is refused.</summary>
internal sealed class DataBlockLedger
{
    private readonly Dictionary<(string Table, uint Id), (string Mod, string Name)> _owners = new();

    /// <summary>Whether <paramref name="modId"/> may add block <paramref name="id"/> named <paramref name="name"/> to
    /// <paramref name="table"/>, given the name of the block the table already has under that ID (null: none).</summary>
    internal DataBlockResult Decide(string table, uint id, string modId, string name, string? existingName)
    {
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id), "Data block ID 0 is never valid.");
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name the data block.", nameof(name));
        if (existingName == null) return DataBlockResult.Free;
        return _owners.TryGetValue((table, id), out var owner) && owner.Mod == modId && owner.Name == name && existingName == name
            ? DataBlockResult.AlreadyAdded
            : DataBlockResult.Conflict;
    }

    internal void Owns(string table, uint id, string modId, string name) => _owners[(table, id)] = (modId, name);

    internal string? OwnerOf(string table, uint id) => _owners.TryGetValue((table, id), out var owner) ? owner.Mod : null;
}
