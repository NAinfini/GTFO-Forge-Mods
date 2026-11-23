using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ForgeMap.Generation;

internal readonly record struct CellCoordinate(int X, int Z);

/// <summary>Room yaw: degrees about +Y in [0,360), any real value. A quarter turn is exact; 0 faces +Z and 90 faces +X.</summary>
internal static class RoomYaw
{
    public const double Tolerance = 1e-3;
    public static double Normalize(double degrees)
    {
        double value = Math.Round((degrees % 360 + 360) % 360, 6);
        return value >= 360 ? 0 : value;
    }
    public static bool Same(double a, double b)
    {
        double gap = Math.Abs(Normalize(a) - Normalize(b));
        return Math.Min(gap, 360 - gap) <= Tolerance;
    }
    /// <summary>The yaw of a horizontal direction (x, z): the inverse of the outward vector (sin yaw, cos yaw) an exit's yaw declares.</summary>
    public static double OfDirection(double x, double z) => Normalize(Math.Atan2(x, z) * 180 / Math.PI);
    public static bool IsQuarter(double yaw) => Math.Abs(yaw / 90 - Math.Round(yaw / 90)) * 90 <= Tolerance / 10;
    /// <summary>Nearest compass side (0 forward, 1 right, 2 backward, 3 left) of a yaw.</summary>
    public static int Compass(double yaw) => (int)(Math.Round(Normalize(yaw) / 90) % 4);
    /// <summary>A local horizontal point turned by the yaw (90 maps +Z onto +X); exact for quarter turns.</summary>
    public static (double X, double Z) Turn(double x, double z, double yaw)
    {
        yaw = Normalize(yaw);
        if (IsQuarter(yaw))
            return (int)Math.Round(yaw / 90) switch { 0 => (x, z), 1 => (z, -x), 2 => (-x, -z), _ => (-z, x) };
        double radians = yaw * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        return (x * cos + z * sin, z * cos - x * sin);
    }
}

internal static class DeclaredFootprints
{
    // Helpers describe local cells, not inferred native tile shapes or renderer bounds.
    public static CellCoordinate[] Rectangle(int sizeX, int sizeZ)
    {
        if (sizeX <= 0 || sizeZ <= 0 || (long)sizeX * sizeZ > int.MaxValue)
            throw new GenerationRefusal("gen-format", "footprint");
        var cells = new CellCoordinate[checked(sizeX * sizeZ)];
        int index = 0;
        for (int x = 0; x < sizeX; x++)
            for (int z = 0; z < sizeZ; z++) cells[index++] = new CellCoordinate(x, z);
        return cells;
    }

    /// <summary>
    /// Anchor maps local cell (0,0) to a grid cell. The footprint rotates around that cell by the yaw with the same turn as
    /// points (<see cref="RoomYaw.Turn"/>; 90 degrees maps (x,z) -> (z,-x), front (0,1) goes to the right (1,0)), a quarter turn
    /// cell for cell, without normalizing the resulting footprint. Any other yaw occupies every grid cell the
    /// rotated cell squares' outer frame covers (45 degrees: 3x3 cells per local cell); the extra cells are waste, not an error.
    /// Irregular, disconnected and negative local coordinates remain declared data.
    /// </summary>
    public static CellCoordinate[] Place(IReadOnlyList<CellCoordinate> localCells, CellCoordinate anchor, double yaw)
    {
        if (!double.IsFinite(yaw))
            throw new GenerationRefusal("gen-rotation-invalid", "footprint.rotation");
        if (localCells is null || localCells.Count == 0)
            throw new GenerationRefusal("gen-footprint-ambiguous", "footprint");
        yaw = RoomYaw.Normalize(yaw);
        bool quarter = RoomYaw.IsQuarter(yaw);
        double radians = yaw * Math.PI / 180, half = (Math.Abs(Math.Cos(radians)) + Math.Abs(Math.Sin(radians))) / 2;
        var result = new HashSet<CellCoordinate>();
        void Add(long x, long z, bool unique)
        {
            x += anchor.X;
            z += anchor.Z;
            if (x < int.MinValue || x > int.MaxValue || z < int.MinValue || z > int.MaxValue)
                throw new GenerationRefusal("gen-grid-range", "footprint");
            if (!result.Add(new CellCoordinate((int)x, (int)z)) && unique)
                throw new GenerationRefusal("gen-footprint-ambiguous", "footprint");
        }
        foreach (CellCoordinate cell in localCells)
        {
            (double cx, double cz) = RoomYaw.Turn(cell.X, cell.Z, yaw);
            if (quarter)
            {
                Add((long)cx, (long)cz, true);
                continue;
            }
            for (long x = (long)Math.Floor(cx - half - 1); x <= (long)Math.Ceiling(cx + half + 1); x++)
                for (long z = (long)Math.Floor(cz - half - 1); z <= (long)Math.Ceiling(cz + half + 1); z++)
                    if (Math.Min(x + .5, cx + half) - Math.Max(x - .5, cx - half) > 1e-3
                        && Math.Min(z + .5, cz + half) - Math.Max(z - .5, cz - half) > 1e-3) Add(x, z, false);
        }
        return result.OrderBy(cell => cell.X).ThenBy(cell => cell.Z).ToArray();
    }
}

internal static class GenerationSequence
{
    // Ordinal belongs to the caller's frozen instance/decision; no process-wide cursor.
    public static int Index(int length, bool cycle, uint ordinal)
    {
        if (length <= 0) throw new GenerationRefusal("gen-format", "sequence.length");
        if (!cycle && ordinal >= (uint)length)
            throw new GenerationRefusal("gen-sequence-exhausted", "sequence.ordinal");
        return (int)(ordinal % (uint)length);
    }
}

internal sealed record GenerationDecisionKey(uint BuildSeed, string LevelId, string DimensionId,
    string ZoneId, string InstanceId, string DecisionKind, uint Ordinal);

internal static class GenerationRandom
{
    public const string Prefix = "forge-map-generation/random/v1";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static byte[] EncodeKey(GenerationDecisionKey key)
    {
        if (key is null) throw new GenerationRefusal("gen-format", "random.key");
        if (key.DecisionKind is not ("room" or "rotation" or "connection" or "door" or "area" or "zone" or "marker" or "light"))
            throw new GenerationRefusal("gen-format", "random.decisionKind");
        using var stream = new MemoryStream();
        WriteText(stream, Prefix, "random.prefix");
        WriteUInt32(stream, key.BuildSeed);
        WriteText(stream, key.LevelId, "random.levelId");
        WriteText(stream, key.DimensionId, "random.dimensionId");
        WriteText(stream, key.ZoneId, "random.zoneId");
        WriteText(stream, key.InstanceId, "random.instanceId");
        WriteText(stream, key.DecisionKind, "random.decisionKind");
        WriteUInt32(stream, key.Ordinal);
        return stream.ToArray();
    }

    public static uint SubSeed(GenerationDecisionKey key) => BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(EncodeKey(key)));

    private static void WriteText(Stream stream, string value, string path)
    {
        if (string.IsNullOrEmpty(value)) throw new GenerationRefusal("gen-format", path);
        byte[] bytes;
        try { bytes = Utf8.GetBytes(value); }
        catch (EncoderFallbackException) { throw new GenerationRefusal("gen-format", path); }
        WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
