namespace ForgeOrdnance.Frames;

/// <summary>
/// The planted suit gun's shot timing without frame quantisation. The game fires at most one shot a frame, the first frame after its
/// next-shot time, so a delay that is not a whole number of frames (or shorter than one) comes out longer. This clock keeps the ideal
/// time of the next shot instead of the frame time of the last one: after every game shot <see cref="AfterShot"/> moves the ideal time
/// on by one interval, says how many more shots are already due this frame (fired through the game's own shot entry), and gives the
/// time to hand back to the game as its next-shot timer. Over any stretch of held fire the average interval is the target at any frame
/// rate, and no frame fires more than <see cref="MaxPerFrame"/> shots.
/// </summary>
internal sealed class FireClock
{
    /// <summary>Shots in one frame at most (the game's own one included).</summary>
    public const int MaxPerFrame = 4;

    /// <summary>A shot later than this past its ideal time starts a new stream (the trigger was released or the clip ran dry).</summary>
    public const float PauseSeconds = 0.25f;

    private float _due;
    private bool _running;

    /// <summary>The ideal time of the next shot (valid while firing).</summary>
    public float Due => _due;

    /// <summary>The game just fired a shot in the frame at <paramref name="now"/>. Returns the shots still due this frame (0 to
    /// <see cref="MaxPerFrame"/> - 1); <paramref name="nextTimer"/> is the next-shot time to give the game. A shot later than
    /// <see cref="PauseSeconds"/> past its ideal time starts a new stream, so no backlog is fired after a pause; a
    /// frame that would need more than the cap drops the rest, the same as a new stream.</summary>
    public int AfterShot(float now, float interval, out float nextTimer)
    {
        if (!(interval > 0f)) { _running = false; nextTimer = now; return 0; }
        if (!_running || now - _due > PauseSeconds) _due = now;
        _running = true;
        var extra = 0;
        _due += interval;
        while (_due <= now && extra < MaxPerFrame - 1)
        {
            extra++;
            _due += interval;
        }
        if (_due <= now) _due = now + interval;
        nextTimer = _due;
        return extra;
    }

    /// <summary>The stream ended (released trigger, empty clip, siege over): the next shot starts a new one.</summary>
    public void Stop() => _running = false;
}
