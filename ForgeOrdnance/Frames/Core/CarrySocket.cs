namespace ForgeOrdnance.Frames;

/// <summary>Where a heavy object a suit carries hangs on the suit's back, in the suit's own frame (x right, y up, z forward). The
/// object's near face sits <see cref="Gap"/> behind the rearmost point of the shell, so it never sinks into the plates, and its centre
/// is at chest height, centred between the shoulders. The suit's own size and the object's own size decide the rest.</summary>
internal static class CarrySocket
{
    /// <summary>Clearance between the shell's rearmost point and the object (metres).</summary>
    public const float Gap = 0.05f;

    /// <summary>The object's centre: <paramref name="backZ"/> is the shell's rearmost z, <paramref name="chestY"/> the chest's height and
    /// <paramref name="depth"/> the object's extent along the suit's forward axis.</summary>
    public static (float X, float Y, float Z) Centre(float backZ, float chestY, float depth) => (0f, chestY, backZ - Gap - depth * 0.5f);
}
