using System;
using System.Collections.Generic;
using System.Text;
using ForgeCore;

namespace ForgeReplay;

internal enum StartMode : byte { Launch = 0, MidJoin = 1 }

internal readonly record struct PluginEntry(string Guid, string Version, string DllHash);

/// <summary>The fingerprint of one installed package's <c>forge</c> folder (ForgeCore <c>InstalledPlugins.ContentPrints</c>).</summary>
internal readonly record struct ContentEntry(string Package, string Hash);

/// <summary>A player of the level. <paramref name="Loadout"/> is the gear JSON of the standard, special, melee and tool slots, one per line (an empty line for an empty slot).</summary>
internal readonly record struct PlayerEntry(int Slot, string Name, string Loadout = "");

internal readonly record struct NamedHash(string Name, string Hash);

/// <summary>The first chunk of a recording: everything a replay needs to decide whether it can regenerate the same map, written before the
/// level lands. Layout in DEVELOPERS.md.</summary>
internal sealed class ReplayHeader
{
    /// <summary>The recording format version: bumped when the layout of the header or of any chunk changes, or what a recorded hash is taken over.
    /// A file that does not read in this layout is refused (it shows among the unreadable files); nothing reads an older shape.</summary>
    internal const uint FormatVersion = 9;

    internal string ReplayVersion { get; set; } = "";
    internal int GameRevision { get; set; }
    internal List<PluginEntry> Plugins { get; set; } = new();
    internal uint RundownId { get; set; }
    internal int Tier { get; set; }
    internal int Index { get; set; }
    internal int BuildSeed { get; set; }
    internal int HostIdSeed { get; set; }
    internal int SessionSeed { get; set; }
    internal string GenerationHash { get; set; } = "";
    internal List<NamedHash> GenerationSections { get; set; } = new();
    internal string RecorderName { get; set; } = "";
    /// <summary>The recorder's own player slot (0 to 3), -1 when it was not seated; what every reader of a recording takes as "the recorder", so two players of one name stay apart.</summary>
    internal int RecorderSlot { get; set; } = -1;
    internal bool RecorderIsHost { get; set; }
    internal StartMode Start { get; set; }
    internal List<PlayerEntry> Players { get; set; } = new();
    /// <summary>What the installed packages shipped for the look of things (one fingerprint of each package's <c>forge</c> folder: content
    /// packs, models, sounds), so a replay can say which package differs or is missing; empty when no package ships any.</summary>
    internal List<ContentEntry> Content { get; set; } = new();
    /// <summary>What the rundown is called, as this game showed it when the level was recorded; empty when the game gave none.</summary>
    internal string RundownName { get; set; } = "";

    internal byte[] Encode()
    {
        var w = new Writer();
        w.Text(ReplayVersion); w.Int(GameRevision);
        w.Count(Plugins.Count);
        foreach (var p in Plugins) { w.Text(p.Guid); w.Text(p.Version); w.Text(p.DllHash); }
        w.Var(RundownId); w.Int(Tier); w.Int(Index); w.Int(BuildSeed); w.Int(HostIdSeed); w.Int(SessionSeed);
        w.Text(GenerationHash);
        w.Count(GenerationSections.Count);
        foreach (var s in GenerationSections) { w.Text(s.Name); w.Text(s.Hash); }
        w.Text(RecorderName); w.Int(RecorderSlot); w.Byte(RecorderIsHost ? (byte)1 : (byte)0); w.Byte((byte)Start);
        w.Count(Players.Count);
        foreach (var p in Players) { w.Int(p.Slot); w.Text(p.Name); w.Text(p.Loadout); }
        w.Count(Content.Count);
        foreach (var c in Content) { w.Text(c.Package); w.Text(c.Hash); }
        w.Text(RundownName);
        return w.ToArray();
    }

    internal static ReplayHeader Decode(byte[] data)
    {
        var r = new Reader(data);
        var h = new ReplayHeader { ReplayVersion = r.Text(), GameRevision = r.Int() };
        for (var n = r.Count(); n > 0; n--) h.Plugins.Add(new PluginEntry(r.Text(), r.Text(), r.Text()));
        h.RundownId = checked((uint)r.Var()); h.Tier = r.Int(); h.Index = r.Int(); h.BuildSeed = r.Int(); h.HostIdSeed = r.Int(); h.SessionSeed = r.Int();
        h.GenerationHash = r.Text();
        for (var n = r.Count(); n > 0; n--) h.GenerationSections.Add(new NamedHash(r.Text(), r.Text()));
        h.RecorderName = r.Text(); h.RecorderSlot = r.Int(); h.RecorderIsHost = r.Byte() == 1;
        var start = r.Byte();
        if (start > (byte)StartMode.MidJoin) throw new FormatException("The start mode " + start + " is unknown.");
        h.Start = (StartMode)start;
        for (var n = r.Count(); n > 0; n--) h.Players.Add(new PlayerEntry(r.Int(), r.Text(), r.Text()));
        for (var n = r.Count(); n > 0; n--) h.Content.Add(new ContentEntry(r.Text(), r.Text()));
        h.RundownName = r.Text();
        r.End();
        return h;
    }

    private sealed class Writer
    {
        private byte[] _buffer = new byte[256];
        private int _position;

        internal byte[] ToArray() { var copy = new byte[_position]; Array.Copy(_buffer, copy, _position); return copy; }
        private void Room(int extra) { if (_position + extra > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _position + extra)); }
        internal void Byte(byte b) { Room(1); _buffer[_position++] = b; }
        internal void Var(ulong v) { Room(Compact.MaxVarBytes); Compact.WriteVar(_buffer, ref _position, v); }
        internal void Int(int v) { Room(Compact.MaxVarBytes); Compact.WriteVarSigned(_buffer, ref _position, v); }
        internal void Count(int v) => Var((ulong)v);
        internal void Text(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            Count(bytes.Length);
            Room(bytes.Length);
            Array.Copy(bytes, 0, _buffer, _position, bytes.Length);
            _position += bytes.Length;
        }
    }

    private sealed class Reader
    {
        private const int MaxItems = 1 << 20;
        private readonly byte[] _data;
        private int _position;
        internal Reader(byte[] data) { _data = data; }
        internal byte Byte() => _position < _data.Length ? _data[_position++] : throw new FormatException("The header is cut off.");
        internal ulong Var() => Compact.ReadVar(_data, ref _position);
        internal int Int() => checked((int)Compact.ReadVarSigned(_data, ref _position));
        internal int Count()
        {
            var v = Var();
            return v <= MaxItems ? (int)v : throw new FormatException("A header list claims " + v + " items.");
        }
        internal string Text()
        {
            var length = Count();
            if (_position + length > _data.Length) throw new FormatException("A header text runs past the end.");
            var s = Encoding.UTF8.GetString(_data, _position, length);
            _position += length;
            return s;
        }
        internal void End() { if (_position != _data.Length) throw new FormatException("The header has " + (_data.Length - _position) + " unread bytes."); }
    }
}
