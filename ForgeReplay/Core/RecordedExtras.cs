using System;
using System.Collections.Generic;

namespace ForgeReplay;

/// <summary>What a player's hands and body say beyond PlayerStatus (event 62): the magazine and stamina exist only on the machine that
/// owns the player, so a slot without one has <see cref="ReplayExtras.Gun"/> false. <see cref="Aiming"/> is the old recordings' 10 Hz aim
/// bit; a recording of this version writes aim only on its transitions (event 108, <see cref="ReplayExtras.Aim"/>) and leaves the bit 0.</summary>
internal readonly record struct PlayerGun(int Clip, int ClipSize, bool Aiming, int StaminaPercent);

/// <summary>Where a player is (event 88). <see cref="Zone"/> is the number players read on the door signs, -1 unknown.</summary>
internal readonly record struct PlayerPlace(int Dimension, int Zone, int Layer);

/// <summary>What an enemy is doing beyond its alert (event 69). <see cref="Target"/> is the player slot, -1 none (a client mostly has none).</summary>
internal readonly record struct EnemyMind(int Target, bool Tagged, bool Detecting, int GluePercent);

/// <summary>How an enemy looks (event 71): the game's size and seed and the AgentMode it spawned in.</summary>
internal readonly record struct EnemyLook(float Size, int Seed, int Mode);

/// <summary>One thing thrown or spawned as an instance (event 72): moving or lying, gone, where. <see cref="Extra"/> is the glow stick's colour, or a fog sphere's state in the low four bits and range x 10 above.</summary>
internal readonly record struct ThrownState(ulong Key, int Phase, ulong Extra, float X, float Y, float Z, uint ItemId, int OwnerSlot);

/// <summary>One line a character said (event 64), to hand back to the game's voice.</summary>
internal readonly record struct SaidLine(int Voice, uint Event, uint Seed, uint InDialog, uint StartDialog, uint Subtitle, int Slot);

/// <summary>One level event the game scheduled (event 65). <see cref="Type"/> is <c>eWardenObjectiveEventType</c>; it happens <see cref="DelayS"/> later.</summary>
internal readonly record struct WorldEventLine(int Type, bool Accepted, int Trigger, int Layer, int Dimension, int Zone, float DelayS, float DurationS, bool Enabled,
    uint Sound, uint Dialogue, uint Fog, float FogTransitionS, int Count, uint Enemy, float X, float Y, float Z, string Intel, string SubHeader, string SubText, string Subtitle, string Filter);

/// <summary>A line a terminal printed (event 73); <see cref="Type"/> 255 is a plain line.</summary>
internal readonly record struct TerminalPrint(ulong Key, int Type, float WaitS, string Text);

/// <summary>The lights of the level (event 66): per dimension, layer and zone the game's light mode byte, and the latest light sequence.</summary>
internal sealed record ZoneLightMap(byte[][][] Modes, int SequenceSections, float SequenceDelayS, float X, float Y, float Z);

/// <summary>The objective timer on screen (event 68).</summary>
internal readonly record struct TimerShown(bool Active, int Seconds, bool Blinking, string Title);

/// <summary>Reads the events numbered 62 and up: persisting ones from <see cref="WorldState.Extras"/> at the moment the state is at (a jump
/// restores them, a keyframe block writes them again), one-shot ones from the payload the timeline reports. Layouts in DEVELOPERS.md.</summary>
internal static class ReplayExtras
{
    internal static bool Gun(WorldState state, int slot, out PlayerGun gun)
    {
        gun = default;
        if (!state.Extras.TryGetValue((EventKind.PlayerGun, (ulong)slot), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        var clip = (int)r.U(); var size = (int)r.U(); var flags = r.U(); var stamina = (int)r.U();
        gun = new PlayerGun(clip - 1, size, (flags & 1) != 0, stamina - 1);
        return true;
    }

    /// <summary>Whether the player aims down sights, as of the last recorded transition (event 108); false with none, which is a recording before the event.</summary>
    internal static bool Aim(WorldState state, int slot, out bool aiming)
    {
        aiming = false;
        if (!state.Extras.TryGetValue((EventKind.PlayerAim, (ulong)slot), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        aiming = r.U() != 0;
        return true;
    }

    internal static bool MeleeCharging(WorldState state, int slot)
    {
        if (!state.Extras.TryGetValue((EventKind.MeleeCharge, (ulong)slot), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        return r.U() != 0;
    }

    /// <summary>A timed hold on something (a door, a box, a revive): whether it is going on and where.</summary>
    internal static bool Holding(WorldState state, int slot, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (!state.Extras.TryGetValue((EventKind.InteractHold, (ulong)slot), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        if (r.U() == 0) return false;
        (x, y, z) = r.Pos();
        return true;
    }

    internal static bool Place(WorldState state, int slot, out PlayerPlace place)
    {
        place = default;
        if (!state.Extras.TryGetValue((EventKind.PlayerPlace, (ulong)slot), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        place = new PlayerPlace((int)r.U(), (int)r.U() - 1, (int)r.U());
        return true;
    }

    /// <summary>The boosters a player wears (event 86): for each, its data block id and uses, with its effects as id and value.</summary>
    internal static List<(uint Id, int Uses, List<(uint Effect, float Value)> Effects)> Boosters(WorldState state, int slot)
    {
        var all = new List<(uint, int, List<(uint, float)>)>();
        if (!state.Extras.TryGetValue((EventKind.Boosters, (ulong)slot), out var payload)) return all;
        var r = PayloadReader.Over(payload); r.U();
        for (var count = r.U(); count > 0; count--)
        {
            var id = (uint)r.U(); var uses = (int)r.U();
            var effects = new List<(uint, float)>();
            for (var n = r.U(); n > 0; n--) effects.Add(((uint)r.U(), r.S() / 1000f));
            all.Add((id, uses, effects));
        }
        return all;
    }

    internal static bool Mind(WorldState state, int entity, out EnemyMind mind)
    {
        mind = default;
        if (!state.Extras.TryGetValue((EventKind.EnemyMind, (ulong)entity), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        var target = (int)r.U() - 1; var flags = r.U();
        mind = new EnemyMind(target, (flags & 1) != 0, (flags & 2) != 0, (int)r.U());
        return true;
    }

    /// <summary>The wake-up the game played for the enemy (WakeUp, 92): the turning set or not, the animation's index, the delay before it.</summary>
    internal static bool WakeUp(WorldState state, int entity, out (bool Turn, int Index, uint DelayMs) wake)
    {
        wake = default;
        if (!state.Extras.TryGetValue((EventKind.WakeUp, (ulong)entity), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        wake = (r.U() == 1, (int)Math.Min(r.U(), int.MaxValue), (uint)Math.Min(r.U(), uint.MaxValue));
        return true;
    }

    internal static bool Look(WorldState state, int entity, out EnemyLook look)
    {
        look = default;
        if (!state.Extras.TryGetValue((EventKind.EnemyLook, (ulong)entity), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        look = new EnemyLook(r.U() / 1000f, (int)r.S(), (int)r.U());
        return true;
    }

    internal static ThrownState Thrown(byte[] payload)
    {
        var r = PayloadReader.Over(payload);
        var key = r.U(); var phase = (int)r.U(); var extra = r.U(); var (x, y, z) = r.Pos();
        return new ThrownState(key, phase, extra, x, y, z, (uint)r.U(), (int)r.U() - 1);
    }

    /// <summary>Everything thrown that has not gone by this moment.</summary>
    internal static IEnumerable<ThrownState> ThrownNow(WorldState state)
    {
        foreach (var (id, payload) in state.Extras)
        {
            if (id.Kind != EventKind.Thrown) continue;
            var thrown = Thrown(payload);
            if (thrown.Phase != 2) yield return thrown;
        }
    }

    internal static ZoneLightMap? Lights(WorldState state)
    {
        if (!state.Extras.TryGetValue((EventKind.ZoneLights, 0UL), out var payload)) return null;
        var r = PayloadReader.Over(payload); r.U();
        var modes = new byte[r.Count()][][];
        for (var d = 0; d < modes.Length; d++)
        {
            modes[d] = new byte[r.Count()][];
            for (var l = 0; l < modes[d].Length; l++)
            {
                modes[d][l] = new byte[r.Count()];
                for (var z = 0; z < modes[d][l].Length; z++) modes[d][l][z] = (byte)r.U();
            }
        }
        var sections = (int)r.U(); var delay = r.U() / 1000f; var (x, y, zz) = r.Pos();
        return new ZoneLightMap(modes, sections, delay, x, y, zz);
    }

    internal static bool Timer(WorldState state, out TimerShown timer)
    {
        timer = default;
        if (!state.Extras.TryGetValue((EventKind.ObjectiveTimer, 0UL), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        timer = new TimerShown(r.U() != 0, (int)r.U(), r.U() != 0, r.Text());
        return true;
    }

    /// <summary>The reactor's text above its console: shown, the message, the words around its timer.</summary>
    internal static bool ReactorText(WorldState state, ulong key, out string message, out string before, out string after)
    {
        message = before = after = "";
        if (!state.Extras.TryGetValue((EventKind.ReactorText, key), out var payload)) return false;
        var r = PayloadReader.Over(payload); r.U();
        var shown = r.U() != 0;
        message = r.Text(); before = r.Text(); after = r.Text();
        return shown;
    }

    internal static SaidLine Said(byte[] payload)
    {
        var r = PayloadReader.Over(payload);
        return new SaidLine((int)r.U(), (uint)r.U(), (uint)r.U(), (uint)r.U(), (uint)r.U(), (uint)r.U(), (int)r.U() - 1);
    }

    internal static WorldEventLine WorldEvent(byte[] payload)
    {
        var r = PayloadReader.Over(payload);
        var type = (int)r.U(); var accepted = r.U() != 0; var trigger = (int)r.U(); var layer = (int)r.U(); var dimension = (int)r.U(); var zone = (int)r.U();
        var delay = r.U() / 100f; var duration = r.U() / 100f; var enabled = r.U() != 0; var sound = (uint)r.U(); var dialogue = (uint)r.U(); var fog = (uint)r.U(); var transition = r.U() / 100f;
        var count = (int)r.U(); var enemy = (uint)r.U(); var (x, y, z) = r.Pos();
        return new WorldEventLine(type, accepted, trigger, layer, dimension, zone, delay, duration, enabled, sound, dialogue, fog, transition, count, enemy, x, y, z, r.Text(), r.Text(), r.Text(), r.Text(), r.Text());
    }

    /// <summary>An Intel event: kind (0 warden intel, 1 new sub objective, 2 priority), seconds shown (0 the game's default), the text.</summary>
    internal static (int Kind, float DurationS, string Text) Intel(byte[] payload)
    {
        var r = PayloadReader.Over(payload);
        return ((int)r.U(), r.U() / 10f, r.Text());
    }

    internal static TerminalPrint Printed(byte[] payload)
    {
        var r = PayloadReader.Over(payload);
        return new TerminalPrint(r.U(), (int)r.U(), r.U() / 1000f, r.Text());
    }

    /// <summary>A TerminalInput event: the terminal's key and the text as typed so far.</summary>
    internal static (ulong Key, string Text) Typed(byte[] payload)
    {
        var r = PayloadReader.Over(payload);
        return (r.U(), r.Text());
    }

    /// <summary>An EnemyBeat event: the sleeper, the strength of its heartbeat, what it senses.</summary>
    internal static (int Entity, float Strength, float X, float Y, float Z) Beat(byte[] payload)
    {
        var r = PayloadReader.Over(payload);
        var entity = (int)r.U(); var strength = r.U() / 100f; var (x, y, z) = r.Pos();
        return (entity, strength, x, y, z);
    }

    /// <summary>The Projectile tail for kind 0 (after Pos): burst size, projectile type, direction, and who it aims at: a player slot, -1 none.</summary>
    internal static (int Burst, int Type, float Dx, float Dy, float Dz, int Target) AimedProjectile(byte[] payload)
    {
        var r = PayloadReader.Over(payload); r.U(); r.U(); r.Pos();
        var burst = (int)r.U(); var type = (int)r.U(); var (x, y, z) = r.Pos();
        return (burst, type, x, y, z, (int)r.U() - 1);
    }
}
