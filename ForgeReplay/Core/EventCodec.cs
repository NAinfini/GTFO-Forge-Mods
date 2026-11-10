using System;
using System.Text;
using ForgeCore;

namespace ForgeReplay;

/// <summary>Event types of a data block. The payload layout of each is in DEVELOPERS.md; "Pos" is three signed centimetre integers.</summary>
internal static class EventKind
{
    internal const ushort EnemySpawn = 1, EnemyGone = 2, EnemyHurt = 3, EnemyLimb = 4;
    internal const ushort PlayerStatus = 10, Shot = 11, Melee = 12, PlayerHurt = 13, Revive = 14, Pack = 15;
    internal const ushort Door = 20, Scan = 21, Puzzle = 22, Wave = 23, Terminal = 24, Generator = 25, ItemSpawn = 26, ItemState = 27, Checkpoint = 28, Chat = 29;
    internal const ushort Interact = 30, Throw = 31, Sentry = 32, SentryShot = 33, Mine = 34, Explosion = 35, Tongue = 36, Projectile = 37, Scream = 38, Scout = 39,
        Noise = 40, Ping = 41, ResourceBox = 42, Reactor = 43, Environment = 44, Presence = 45, HostChanged = 46, GameLog = 47, WeaponSound = 48,
        EnemyAttack = 49, EnemyHit = 50, EnemyState = 51, Reload = 52, Wake = 53, PlayerMotion = 54,
        PlayerKit = 55, ReviveBy = 56, Supply = 57, Objective = 58, Disinfection = 59, Music = 60, Foam = 61;
    internal const ushort PlayerGun = 62, MeleeCharge = 63, Say = 64, WorldEvent = 65, ZoneLights = 66, Intel = 67, ObjectiveTimer = 68, EnemyMind = 69, EnemyBeat = 70, EnemyLook = 71, Thrown = 72,
        TerminalLine = 73, TerminalState = 74, TerminalInput = 75, WeakLock = 76, Bulkhead = 77, HsuActivator = 78, Portal = 79, GeneratorCluster = 80, FogTurbine = 81, Cluster = 82, Holopath = 83,
        ScanMove = 84, Spitter = 85, Boosters = 86, InteractHold = 87, PlayerPlace = 88, ReactorText = 89,
        EnemyHealth = 90, PlayerHealth = 91, WakeUp = 92, SentryAmmo = 93,
        ShownName = 94, ShownFx = 95, ShownSound = 96, ShownPose = 98, ShownModelState = 99,
        MeleeHit = 100, Note = 101, ShownHeld = 102, ShownLoop = 103,
        HostHit = 104, HostWake = 105, HostLink = 106, PlayerAim = 108, HostSupply = 109, HostPack = 110, HostHurt = 111, HostRay = 112;

    /// <summary>Events whose latest payload per first U is what is true at a moment, kept in <see cref="WorldState.Extras"/> (read through <see cref="ReplayExtras"/>).</summary>
    internal static bool IsExtraState(ushort kind) => kind is PlayerGun or MeleeCharge or ZoneLights or ObjectiveTimer or EnemyMind or EnemyLook or WakeUp or Thrown or TerminalState or WeakLock or Bulkhead or HsuActivator
        or Portal or GeneratorCluster or FogTurbine or Cluster or Holopath or ScanMove or Spitter or Boosters or InteractHold or PlayerPlace or ReactorText or SentryAmmo or PlayerAim or ShownName or ShownPose or ShownModelState or ShownHeld or ShownLoop;

    /// <summary>A state event that says its object is gone for good (foam phase 2, a thrown item's phase 2, a shown pose's stop, a shown model's end, a sentry taken back or a mine gone off or picked up): all put the phase in the second number. Such a
    /// payload stays in the delta record, but the object leaves the keyframe table and the timeline's state, so nothing that ended is written or kept again. A held shown thing
    /// (<see cref="ShownHeld"/>) ends at its release number and a looping sound (<see cref="ShownLoop"/>) at name 0, both in the second number.</summary>
    internal static bool Ends(ushort kind, ReadOnlySpan<byte> payload)
    {
        if (kind != Foam && kind != Thrown && kind != ShownPose && kind != ShownModelState && kind != Sentry && kind != Mine && kind != ShownHeld && kind != ShownLoop) return false;
        var at = 0;
        while (at < payload.Length && (payload[at] & 0x80) != 0) at++;
        at++;
        if (at >= payload.Length) return false;
        // A sentry or a mine is standing at 0 and gone at any other number; a loop is over at 0, a held thing at ShownCodec.Release; the others end at 2.
        return kind switch { Sentry or Mine => payload[at] != 0, ShownLoop => payload[at] == 0, ShownHeld => payload[at] == ShownCodec.Release, _ => payload[at] == 2 };
    }
}

/// <summary>Builds one event payload in a reused buffer, so writing an event allocates nothing. Main thread only.</summary>
internal sealed class PayloadWriter
{
    /// <summary>The largest payload: room for a player's gear JSON (four slots, about 3 KB) in a Presence event.</summary>
    internal const int Capacity = 8192;
    private readonly byte[] _buffer = new byte[Capacity];
    private int _length;

    internal ReadOnlySpan<byte> Span => new(_buffer, 0, _length);

    internal PayloadWriter Reset() { _length = 0; return this; }

    internal PayloadWriter U(ulong value) { if (Capacity - _length >= Compact.MaxVarBytes) Compact.WriteVar(_buffer, ref _length, value); return this; }

    internal PayloadWriter S(long value) { if (Capacity - _length >= Compact.MaxVarBytes) Compact.WriteVarSigned(_buffer, ref _length, value); return this; }

    internal PayloadWriter Pos(float x, float y, float z) => S((long)MathF.Round(x * 100f)).S((long)MathF.Round(y * 100f)).S((long)MathF.Round(z * 100f));

    /// <summary>Text as a count then UTF-8 bytes, cut to <paramref name="maxBytes"/> (200 for every text but a gear JSON) and to what is
    /// left of the payload, never inside a character.</summary>
    internal PayloadWriter Text(string? text, int maxBytes = 200)
    {
        text ??= "";
        if (Capacity - _length < Compact.MaxVarBytes) return this;
        var limit = Math.Min(maxBytes, Capacity - _length - Compact.MaxVarBytes);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > limit)
        {
            var end = 0; var used = 0;
            while (end < text.Length)
            {
                var step = char.IsHighSurrogate(text[end]) && end + 1 < text.Length ? 2 : 1;
                var size = Encoding.UTF8.GetByteCount(text.AsSpan(end, step));
                if (used + size > limit) break;
                used += size; end += step;
            }
            text = text.Substring(0, end); bytes = used;
        }
        U((ulong)bytes);
        _length += Encoding.UTF8.GetBytes(text, 0, text.Length, _buffer, _length);
        return this;
    }
}

/// <summary>Reads a payload written by <see cref="PayloadWriter"/>, for tests and the file dump. Anything cut off throws <see cref="FormatException"/>.</summary>
internal sealed class PayloadReader
{
    private byte[] _bytes;
    private int _at, _length;
    [ThreadStatic] private static PayloadReader? _over;

    internal PayloadReader(byte[] bytes) { _bytes = bytes; _length = bytes.Length; }

    /// <summary>A reader over <paramref name="bytes"/> that makes nothing: one per thread, aimed at the bytes (not copied) and valid until
    /// the next <see cref="Over"/> on the same thread. For the reads a replay makes every frame; a reader that is kept, handed on or used
    /// across another read takes <c>new PayloadReader(bytes)</c>.</summary>
    internal static PayloadReader Over(byte[] bytes)
    {
        var reader = _over ??= new PayloadReader(bytes);
        reader._bytes = bytes; reader._length = bytes.Length; reader._at = 0;
        return reader;
    }

    /// <summary>A reader that is loaded again and again, so reading events as they are written allocates nothing.</summary>
    internal PayloadReader() : this(new byte[PayloadWriter.Capacity]) { _length = 0; }

    internal PayloadReader Load(ReadOnlySpan<byte> payload) { payload.CopyTo(_bytes); _length = payload.Length; _at = 0; return this; }

    internal bool AtEnd => _at >= _length;

    internal ulong U() { var value = Compact.ReadVar(_bytes, ref _at); Within(); return value; }

    internal long S() { var value = Compact.ReadVarSigned(_bytes, ref _at); Within(); return value; }

    /// <summary>A count of items that follow, each at least one byte, so a count the payload cannot hold is refused before anything is made for it.</summary>
    internal int Count()
    {
        var count = U();
        if (count > (ulong)(_length - _at)) throw new FormatException("A count is larger than the rest of the payload.");
        return (int)count;
    }

    // A reader loaded again keeps the bytes of a longer payload before it behind the end: they are not this payload's.
    private void Within() { if (_at > _length) throw new FormatException("A value runs past the end of the payload."); }

    internal (float X, float Y, float Z) Pos() => (S() / 100f, S() / 100f, S() / 100f);

    internal string Text()
    {
        var size = U();
        if (size > (ulong)(_length - _at)) throw new FormatException("A text runs past the end of the payload.");
        var text = Encoding.UTF8.GetString(_bytes, _at, (int)size);
        _at += (int)size;
        return text;
    }
}
