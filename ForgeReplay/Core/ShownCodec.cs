using System;

namespace ForgeReplay;

/// <summary>An effect the libraries drew (event 95, <see cref="EventKind.ShownFx"/>) as plain numbers: <see cref="Kind"/> is ForgeCore's <c>ShownKind</c> number
/// (<see cref="ShownCodec.Release"/> ends the held thing under <see cref="Key"/>), <see cref="Name"/> a table entry (the effect's name, or a beam's or trail's
/// look), <see cref="Size"/> is NaN when the effect's own is meant, <see cref="Colour"/> is RGBA with red lowest.</summary>
internal readonly record struct ShownFxRecord(byte Kind, int Name, ulong Key, float Ax, float Ay, float Az, float Bx, float By, float Bz, float Size, uint Colour, bool Head, float Seconds, float Fade);

/// <summary>A sound the libraries played (event 96, <see cref="EventKind.ShownSound"/>); <see cref="Name"/> 0 is the end of the loop <see cref="Loop"/>.</summary>
internal readonly record struct ShownSoundRecord(int Name, byte Place, bool Self, float X, float Y, float Z, int Variant, float Pitch, float Gain, ulong Loop);

/// <summary>A named joint clip playing on a player or enemy (event 98, <see cref="EventKind.ShownPose"/>, a kept state per <see cref="ShownCodec.PoseKey"/>): <see cref="Anchor"/>
/// is 1 for a player slot and 2 for an enemy entity, <see cref="Name"/> a table entry, <see cref="StartMs"/> the recording time the clip was at its first frame (may be
/// negative), <see cref="Joints"/> the bit per canonical joint the clip drives and <see cref="Weights"/> (null: all 1) one percent per set bit, lowest first. A stop
/// (<see cref="Stopped"/>) keeps only the key and <see cref="StopFade"/> (s); it retires the state.</summary>
internal readonly record struct ShownPoseRecord(bool Stopped, byte Anchor, int Id, int Track, int Name, long StartMs, bool Loop, float Weight, float FadeIn, float FadeOut, ulong Joints, byte[]? Weights, float StopFade);

/// <summary>A model a library made, or the renderers it turned off (event 99, <see cref="EventKind.ShownModelState"/>, a state kept per <see cref="Key"/> so a jump can build the model
/// again): <see cref="Phase"/> 0 model <see cref="Name"/> at its current pose (local to its anchor when <see cref="Anchor"/> is 1 or 2, else the world's), 1 renderers hidden under
/// the anchor (<see cref="Anchor"/> 3: a thing that is no player or enemy, found again by its world position in <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/>), 2 ended (retires the state).</summary>
internal readonly record struct ShownModelRecord(ulong Key, byte Phase, int Name, byte Anchor, int Id, string Path, float X, float Y, float Z, float Qx, float Qy, float Qz, float Qw, float Sx, float Sy, float Sz);

/// <summary>The payload layouts of what the libraries showed, written by ShownRecorder and read by ShownPlayback (DEVELOPERS.md, events 94 to 97).</summary>
internal static class ShownCodec
{
    internal const uint White = 0xFFFFFFFF;
    /// <summary>The kind numbers of ForgeCore's <c>ShownKind</c> the layout branches on.</summary>
    internal const byte Play = 0, Line = 1, Hold = 2, HeldLine = 3, Release = 4, Beam = 5, Trail = 6, Decal = 7;

    private static bool DirectionB(byte kind) => kind is Play or Hold or Decal;
    private static bool PointB(byte kind) => kind is Line or HeldLine or Beam;

    /// <summary>A table entry (event 94): the text of number <paramref name="id"/>.</summary>
    internal static PayloadWriter Name(PayloadWriter w, int id, string text, int maxBytes = 200) => w.U((ulong)id).Text(text, maxBytes);

    internal static (int Id, string Text) ReadName(PayloadReader r) => ((int)r.U(), r.Text());

    /// <summary>The key of a body's track in the state table: the entity, then which kind of body, then the track (0 to 3).</summary>
    internal static ulong PoseKey(byte anchor, int id, int track) => ((ulong)id << 3) | ((ulong)(anchor - 1) << 2) | (uint)(track & 3);

    internal static PayloadWriter Write(PayloadWriter w, in ShownPoseRecord p)
    {
        w.U(PoseKey(p.Anchor, p.Id, p.Track));
        if (p.Stopped) return w.U(2).U((ulong)Math.Max(0L, (long)MathF.Round(p.StopFade * 1000f)));
        var flags = (p.Loop ? 1UL : 0UL) | (p.Weights != null ? 2UL : 0UL);
        w.U(0).U((ulong)p.Name).S(p.StartMs).U(flags)
            .U((ulong)Math.Round(p.Weight * 100f)).U((ulong)Math.Round(p.FadeIn * 100f)).U((ulong)Math.Round(p.FadeOut * 100f)).U(p.Joints);
        if (p.Weights != null) foreach (var weight in p.Weights) w.U(weight);
        return w;
    }

    /// <summary>A pose payload as written; a stop has only <see cref="ShownPoseRecord.StopFade"/> and the key (read back as the anchor, id and track it came from).</summary>
    internal static ShownPoseRecord ReadPose(PayloadReader r)
    {
        var key = r.U();
        var anchor = (byte)(((key >> 2) & 1) + 1);
        var id = (int)(key >> 3);
        var track = (int)(key & 3);
        if (r.U() == 2) return new ShownPoseRecord(true, anchor, id, track, 0, 0, false, 0, 0, 0, 0, null, r.U() / 1000f);
        var name = (int)r.U();
        var start = r.S();
        var flags = r.U();
        var weight = r.U() / 100f;
        var fadeIn = r.U() / 100f;
        var fadeOut = r.U() / 100f;
        var joints = r.U();
        byte[]? weights = null;
        if ((flags & 2) != 0)
        {
            weights = new byte[System.Numerics.BitOperations.PopCount(joints)];
            for (var i = 0; i < weights.Length; i++) weights[i] = (byte)r.U();
        }
        return new ShownPoseRecord(false, anchor, id, track, name, start, (flags & 1) != 0, weight, fadeIn, fadeOut, joints, weights, 0);
    }

    internal const byte ModelPresent = 0, ModelHidden = 1, ModelEnded = 2, WorldRoot = 3;

    /// <summary>The key of a model (<paramref name="id"/>: its instance key) or of a hidden-renderers list (its token number) in the state table.</summary>
    internal static ulong ModelKey(ulong id, bool hidden) => id << 1 | (hidden ? 1UL : 0UL);

    internal static PayloadWriter Write(PayloadWriter w, in ShownModelRecord m)
    {
        w.U(m.Key).U(m.Phase);
        if (m.Phase == ModelEnded) return w;
        if (m.Phase == ModelPresent) w.U((ulong)m.Name);
        w.U(m.Anchor).U((ulong)m.Id).Text(m.Path);
        if (m.Phase == ModelHidden && m.Anchor != WorldRoot) return w;
        w.Pos(m.X, m.Y, m.Z);
        if (m.Phase == ModelPresent)
            w.S((long)MathF.Round(m.Qx * 10000f)).S((long)MathF.Round(m.Qy * 10000f)).S((long)MathF.Round(m.Qz * 10000f)).S((long)MathF.Round(m.Qw * 10000f))
                .S((long)MathF.Round(m.Sx * 1000f)).S((long)MathF.Round(m.Sy * 1000f)).S((long)MathF.Round(m.Sz * 1000f));
        return w;
    }

    internal static ShownModelRecord EndedModel(ulong key) => new(key, ModelEnded, 0, 0, 0, "", 0, 0, 0, 0, 0, 0, 1, 1, 1, 1);

    internal static ShownModelRecord ReadModel(PayloadReader r)
    {
        var key = r.U();
        var phase = (byte)r.U();
        if (phase == ModelEnded) return EndedModel(key);
        var name = phase == ModelPresent ? (int)r.U() : 0;
        var anchor = (byte)r.U();
        var id = (int)r.U();
        var path = r.Text();
        if (phase == ModelHidden && anchor != WorldRoot) return new ShownModelRecord(key, phase, 0, anchor, id, path, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1);
        var (x, y, z) = r.Pos();
        if (phase == ModelHidden) return new ShownModelRecord(key, phase, 0, anchor, id, path, x, y, z, 0, 0, 0, 1, 1, 1, 1);
        return new ShownModelRecord(key, phase, name, anchor, id, path, x, y, z, r.S() / 10000f, r.S() / 10000f, r.S() / 10000f, r.S() / 10000f, r.S() / 1000f, r.S() / 1000f, r.S() / 1000f);
    }

    internal static PayloadWriter Write(PayloadWriter w, in ShownFxRecord e)
    {
        if (e.Kind == Release) return w.U(e.Key).U(Release);
        var flags = 0UL;
        if (!float.IsNaN(e.Size)) flags |= 1;
        if (e.Colour != White) flags |= 2;
        if (!e.Head) flags |= 4;
        if (e.Seconds != 0f) flags |= 8;
        if (e.Fade != 0f) flags |= 16;
        w.U(e.Key).U(e.Kind).U((ulong)e.Name).U(flags).Pos(e.Ax, e.Ay, e.Az);
        // A direction is written at ten times the precision of a point: a unit vector loses its angle at a centimetre.
        if (DirectionB(e.Kind)) w.Pos(e.Bx * 10f, e.By * 10f, e.Bz * 10f);
        else if (PointB(e.Kind)) w.Pos(e.Bx, e.By, e.Bz);
        if ((flags & 1) != 0) w.S((long)MathF.Round(e.Size * 1000f));
        if ((flags & 2) != 0) w.U(e.Colour);
        if ((flags & 8) != 0) w.U((ulong)Math.Max(0L, (long)MathF.Round(e.Seconds * 100f)));
        if ((flags & 16) != 0) w.U((ulong)Math.Max(0L, (long)MathF.Round(e.Fade * 100f)));
        return w;
    }

    internal static ShownFxRecord ReadFx(PayloadReader r)
    {
        var key = r.U();
        var kind = (byte)r.U();
        if (kind == Release) return new ShownFxRecord(Release, 0, key, 0, 0, 0, 0, 0, 0, float.NaN, White, true, 0, 0);
        var name = (int)r.U();
        var flags = r.U();
        var (ax, ay, az) = r.Pos();
        float bx = 0, by = 0, bz = 0;
        if (DirectionB(kind)) { var (x, y, z) = r.Pos(); bx = x / 10f; by = y / 10f; bz = z / 10f; }
        else if (PointB(kind)) (bx, by, bz) = r.Pos();
        var size = (flags & 1) != 0 ? r.S() / 1000f : float.NaN;
        var colour = (flags & 2) != 0 ? (uint)r.U() : White;
        var seconds = (flags & 8) != 0 ? r.U() / 100f : 0f;
        var fade = (flags & 16) != 0 ? r.U() / 100f : 0f;
        return new ShownFxRecord(kind, name, key, ax, ay, az, bx, by, bz, size, colour, (flags & 4) == 0, seconds, fade);
    }

    internal static PayloadWriter Write(PayloadWriter w, in ShownSoundRecord s) =>
        s.Name == 0 ? w.U(s.Loop).U(0)
            : w.U(s.Loop).U((ulong)s.Name).U(s.Place | (s.Self ? 4UL : 0UL)).Pos(s.X, s.Y, s.Z).U((ulong)Math.Max(0, s.Variant)).U((ulong)Math.Max(0L, (long)MathF.Round(s.Pitch * 1000f)))
                .U((ulong)Math.Max(0L, (long)MathF.Round(s.Gain * 100f)));

    internal static ShownSoundRecord ReadSound(PayloadReader r)
    {
        var loop = r.U();
        var name = (int)r.U();
        if (name == 0) return new ShownSoundRecord(0, 0, false, 0, 0, 0, 0, 0, 0, loop);
        var place = r.U();
        var (x, y, z) = r.Pos();
        var variant = (int)r.U();
        var pitch = r.U() / 1000f;
        var gain = r.U() / 100f;
        return new ShownSoundRecord(name, (byte)(place & 3), (place & 4) != 0, x, y, z, variant, pitch, gain, loop);
    }
}
