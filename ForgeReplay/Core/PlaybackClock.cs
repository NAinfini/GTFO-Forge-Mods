using System;

namespace ForgeReplay;

/// <summary>How much of the replay's sound plays at a speed (PLAN 5.3): everything up to 2x, only the key events (death, down, alarm, door, wave) up to 4x, nothing above.</summary>
internal enum SoundTier : byte { All, KeyOnly, Mute }

/// <summary>The replay clock: play, pause, speed from 0.25x to 8x, a frame step of one recording sample (33 ms) either way, and a jump.
/// Pure arithmetic on the recording time; the caller feeds it real frame time.</summary>
internal sealed class PlaybackClock
{
    internal static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };
    internal const uint StepMs = 33;

    private double _position;

    internal bool Playing { get; private set; }
    internal float Speed { get; private set; } = 1f;
    internal uint DurationMs { get; }
    internal uint PositionMs => (uint)Math.Min(_position, DurationMs);
    internal bool AtEnd => PositionMs >= DurationMs;

    internal PlaybackClock(uint durationMs) { DurationMs = durationMs; }

    internal void Play() { if (!AtEnd) Playing = true; }
    internal void Pause() => Playing = false;
    internal void Toggle() { if (Playing) Pause(); else Play(); }

    /// <summary>Faster (+1) or slower (-1) by one notch of <see cref="Speeds"/>; stops at the ends.</summary>
    internal void ChangeSpeed(int notches)
    {
        var index = Array.IndexOf(Speeds, Speed);
        Speed = Speeds[Math.Clamp(index + notches, 0, Speeds.Length - 1)];
    }

    /// <summary>Moves the clock by <paramref name="realSeconds"/> of real time at the current speed while playing; reaching the end pauses it.</summary>
    internal void Advance(float realSeconds)
    {
        if (!Playing) return;
        _position += realSeconds * 1000.0 * Speed;
        if (_position >= DurationMs) { _position = DurationMs; Playing = false; }
    }

    internal void Seek(uint ms) => _position = Math.Min(ms, DurationMs);

    /// <summary>One recording sample forward (+1) or back (-1), paused.</summary>
    internal void Step(int direction)
    {
        Playing = false;
        _position = direction < 0 ? Math.Max(0, _position - StepMs) : Math.Min(DurationMs, _position + StepMs);
    }

    internal static SoundTier Tier(float speed) => speed <= 2f ? SoundTier.All : speed <= 4f ? SoundTier.KeyOnly : SoundTier.Mute;
}
