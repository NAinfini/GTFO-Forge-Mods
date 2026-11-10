namespace ForgeReplay;

/// <summary>Where the camera is: flying free, behind a player and turning as the player looks (Follow), behind a player and turned only by the
/// observer's mouse (Orbit), at a player's eyes (the camera position only, no arms or gun), or above a player looking down.</summary>
internal enum ViewMode : byte { Free, Follow, Eye, Overhead, Orbit }

/// <summary>The camera choices of the replay screen that do not need the game: choosing a player, the next player, the eye height.</summary>
internal static class ReplayView
{
    /// <summary>A player's number was pressed (or their card clicked): another player is watched the same way (free becomes orbiting);
    /// the player already watched switches between behind and eyes (from free or above it goes to orbiting).</summary>
    internal static ViewMode Select(ViewMode mode, int current, int chosen)
    {
        if (chosen != current) return mode == ViewMode.Free ? ViewMode.Orbit : mode;
        return mode is ViewMode.Follow or ViewMode.Orbit ? ViewMode.Eye : ViewMode.Orbit;
    }

    /// <summary>Left click, the V key and the camera button: orbiting the player, chasing it, its eyes, the free camera, and round again
    /// (from above it goes back to orbiting).</summary>
    internal static ViewMode NextPersonView(ViewMode mode) => mode switch
    {
        ViewMode.Orbit => ViewMode.Follow,
        ViewMode.Follow => ViewMode.Eye,
        ViewMode.Eye => ViewMode.Free,
        _ => ViewMode.Orbit,
    };

    /// <summary>The next player in <paramref name="direction"/> (+1 or -1) who is in the recording, or <paramref name="current"/> when
    /// nobody else is.</summary>
    internal static int Next(bool[] present, int current, int direction)
    {
        var n = present.Length;
        for (var step = 1; step <= n; step++)
        {
            var slot = ((current + direction * step) % n + n) % n;
            if (present[slot]) return slot;
        }
        return current;
    }

    /// <summary>Eye height above the recorded feet, in metres, for the game's locomotion state (PLOC_State: 1 crouching, 7 downed).</summary>
    internal static float EyeHeight(int locomotion) => locomotion switch { 1 => 0.95f, 7 => 0.45f, _ => 1.6f };
}
