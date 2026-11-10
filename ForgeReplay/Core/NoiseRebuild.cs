using System.Collections.Generic;

namespace ForgeReplay;

/// <summary>A player action that a sleeping enemy can hear (PLAN 5.6, the noise row).</summary>
internal enum NoiseAction : byte { Run, Jump, Land, Shot, Melee, DoorOpen, DoorBreak }

/// <summary>One player action read from a recording: when, who, what and where.</summary>
internal readonly record struct PlayerAction(uint TimeMs, int Slot, NoiseAction Action, float X, float Y, float Z);

/// <summary>How far an enemy type hears a player, from its <c>EnemyDetectionDataBlock</c>: a walk, run, jump or landing inside
/// <paramref name="Movement"/> (<c>movementDetectionDistance</c>), a shot inside <paramref name="Gunfire"/> (<c>weaponDetectionDistanceMax</c>,
/// the far end of the range the game narrows by node distance, which a recording does not hold).</summary>
internal readonly record struct Hearing(float Movement, float Gunfire);

/// <summary>Reads the player actions that make noise from what every machine sees: locomotion changes (run, jump, land), shots (a
/// shotgun's pellets are one), swings, and door changes with the player standing at the door. The game gives these noises no radius of
/// their own: a sleeping enemy hears them inside its own detection distances (<see cref="Hearing"/>), so the noise is rebuilt as the
/// action and its place, and the radius is the hearing enemy's. The host's Noise events (the game's <c>NoiseManager</c> noises) are a
/// different thing: in the host recordings read for this (DEVELOPERS.md, noise) none of them came from these actions.</summary>
internal sealed class NoiseRebuild
{
    // PlayerLocomotion.PLOC_State and LevelGeneration.eDoorStatus, as the game's interop declares them.
    internal const int Run = 2, Jump = 3, Land = 5;
    private const int DoorOpen = 10, DoorDestroyed = 11, DoorOpening = 16;
    /// <summary>How far from a door the player who opened or broke it may stand.</summary>
    internal const float DoorReach = 4f;

    private readonly WorldState _state;
    private readonly int[] _locomotion = { -1, -1, -1, -1 };
    private readonly uint[] _shotMs = { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue };
    private readonly Dictionary<ulong, ulong> _doors = new();

    internal List<PlayerAction> Actions { get; } = new();

    internal NoiseRebuild(WorldState state) { _state = state; }

    /// <summary>The player's locomotion state as last recorded (PLOC_State), -1 before the first status.</summary>
    internal int Locomotion(int slot) => _locomotion[slot & 3];

    /// <summary>One event, in time order, with <see cref="WorldState"/> at that moment.</summary>
    internal void Take(uint timeMs, ushort type, PayloadReader read)
    {
        switch (type)
        {
            case EventKind.PlayerStatus:
                {
                    var slot = (int)read.U() & 3;
                    var locomotion = (int)read.U();
                    var before = _locomotion[slot];
                    _locomotion[slot] = locomotion;
                    if (before < 0 || before == locomotion) break;
                    if (locomotion == Run) AtPlayer(timeMs, slot, NoiseAction.Run);
                    else if (locomotion == Jump) AtPlayer(timeMs, slot, NoiseAction.Jump);
                    else if (locomotion == Land) AtPlayer(timeMs, slot, NoiseAction.Land);
                    break;
                }
            case EventKind.Shot:
                {
                    var slot = (int)read.U() & 3; read.U();
                    var (x, y, z) = read.Pos();
                    if (_shotMs[slot] == timeMs) break;
                    _shotMs[slot] = timeMs;
                    Actions.Add(new PlayerAction(timeMs, slot, NoiseAction.Shot, x, y, z));
                    break;
                }
            case EventKind.Melee: AtPlayer(timeMs, (int)read.U() & 3, NoiseAction.Melee); break;
            case EventKind.Door:
                {
                    var key = read.U(); var state = read.U(); read.U();
                    var (x, y, z) = read.Pos();
                    // Keyframe blocks write every door again: only a change is an action, and the first sight of a door is none.
                    var known = _doors.TryGetValue(key, out var before);
                    _doors[key] = state;
                    if (!known || before == state) break;
                    NoiseAction action;
                    if (state == DoorDestroyed) action = NoiseAction.DoorBreak;
                    else if ((state == DoorOpening || state == DoorOpen) && before != DoorOpening && before != DoorOpen) action = NoiseAction.DoorOpen;
                    else break;
                    var slot = Nearest(x, y, z);
                    if (slot >= 0) Actions.Add(new PlayerAction(timeMs, slot, action, x, y, z));
                    break;
                }
        }
    }

    /// <summary>The distance at which an enemy with <paramref name="hearing"/> hears <paramref name="action"/>. A walk, run, jump, landing and shot are the
    /// game data's own distances. A melee hit, a door opening and a door breaking have none in the game data, and the attenuation range of their sound events
    /// is not readable from the files available to this package, so they are estimated from the sound they are like: a melee hit and a door opening as loud as
    /// a run (<see cref="Hearing.Movement"/>), a door broken as loud as a shot (<see cref="Hearing.Gunfire"/>). Whoever wakes an enemy that way is always an
    /// estimate (marked ~).</summary>
    internal static float Reach(NoiseAction action, Hearing hearing) => action switch
    {
        NoiseAction.Run or NoiseAction.Jump or NoiseAction.Land or NoiseAction.Melee or NoiseAction.DoorOpen => hearing.Movement,
        NoiseAction.Shot or NoiseAction.DoorBreak => hearing.Gunfire,
        _ => float.NaN,
    };

    private void AtPlayer(uint timeMs, int slot, NoiseAction action)
    {
        ref var p = ref _state.Poses[slot];
        if (p.Present) Actions.Add(new PlayerAction(timeMs, slot, action, p.X, p.Y, p.Z));
    }

    private int Nearest(float x, float y, float z)
    {
        var best = -1; var bestSquared = DoorReach * DoorReach;
        for (var slot = 0; slot < 4; slot++)
        {
            ref var p = ref _state.Poses[slot];
            if (!p.Present) continue;
            var squared = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) + (p.Z - z) * (p.Z - z);
            if (squared <= bestSquared) { bestSquared = squared; best = slot; }
        }
        return best;
    }
}
