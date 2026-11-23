using System;
using System.Collections.Generic;
using Enemies;
using ForgeAudio;
using ForgeEngine.Framework;
using ForgeEngine.Presentation.Native;
using ForgeEnemy.Profile;
using GameData;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// An author's sound in place of a native sound event: the event's field in the enemy's own EnemySFX block is written with
/// an event ID ForgeAudio reserved for that sound, so every path that reads the field, on every peer, ends here and the
/// game's sound is never posted. A game sound the author chose goes into the field as it is and stays on the game's path. Fields keep the game's value when the sound cannot play on this client. The block is the
/// variant's own copy, which the website exports for any enemy that replaces a sound.
/// </summary>
internal sealed class EnemySoundReplacement
{
    private readonly Dictionary<string, uint> _idBySound = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _soundById = new();
    /// <summary>Each enemy's own sound player and its enemy type, because a post on it says nothing else about where to play or whose it is.</summary>
    private readonly Dictionary<IntPtr, (GameObject Body, uint Type)> _bodies = new();
    /// <summary>The last play of each sound on each enemy's sound player, which a new post of it waits for.</summary>
    private readonly Dictionary<(IntPtr, string), Func<bool>> _sounding = new();

    /// <summary>The reserved ID of a sound, or 0 with the refusal in <paramref name="refusal"/>.</summary>
    private uint Reserve(string sound, out string? refusal)
    {
        refusal = CustomSoundPlayback.Refusal(sound);
        if (refusal != null) return 0;
        if (_idBySound.TryGetValue(sound, out var id)) return id;
        id = AudioKit.ReserveGameEvent(EnemyHost.ModId, Play);
        if (id == 0) { refusal = "reservation-refused"; return 0; }
        _idBySound.Add(sound, id);
        _soundById.Add(id, sound);
        return id;
    }

    /// <summary>Writes one life's replaced events. Every refusal (slot, sound and reason) goes to <paramref name="refused"/>,
    /// so fixing one slot never hides what the next one still lacks.</summary>
    internal void Apply(EnemyAgent enemy, uint type, EnemySoundEntry[] sounds, Action<EnemySoundSlot, string, string> refused)
    {
        if (enemy.Sound is { } player) _bodies[player.Pointer] = (enemy.gameObject, type);
        var block = enemy.EnemySFXData;
        foreach (var entry in sounds)
        {
            if (entry.Slot is EnemySoundSlot.MeleeForward or EnemySoundSlot.MeleeBackward) continue;
            // A game sound goes straight into the field: the game posts it as its own, with no hook in the way.
            string? refusal = null;
            var id = entry.GameEvent != 0 ? entry.GameEvent : Reserve(entry.Resource!, out refusal);
            if (id == 0) { refused(entry.Slot, entry.Resource!, refusal!); continue; }
            if (block == null) { refused(entry.Slot, entry.Resource ?? entry.GameEvent.ToString(), "no-sfx-block"); continue; }
            Write(block, entry.Slot, id);
        }
    }

    /// <summary>The two melee events live on the instance's own animator handle, not on the block.</summary>
    internal void ApplyMelee(EnemyLocomotion locomotion, EnemySoundEntry[] sounds, Action<EnemySoundSlot, string, string> refused)
    {
        var handle = locomotion.AnimHandle;
        if (handle == null) return;
        var changed = false;
        foreach (var entry in sounds)
        {
            if (entry.Slot is not (EnemySoundSlot.MeleeForward or EnemySoundSlot.MeleeBackward)) continue;
            string? refusal = null;
            var id = entry.GameEvent != 0 ? entry.GameEvent : Reserve(entry.Resource!, out refusal);
            if (id == 0) { refused(entry.Slot, entry.Resource!, refusal!); continue; }
            if (entry.Slot == EnemySoundSlot.MeleeForward) { var timing = handle.MeleeAttackFwd; timing.SFXId = id; handle.MeleeAttackFwd = timing; }
            else { var timing = handle.MeleeAttackBwd; timing.SFXId = id; handle.MeleeAttackBwd = timing; }
            changed = true;
        }
        if (changed) locomotion.AnimHandle = handle;
    }

    private static void Write(EnemySFXDataBlock block, EnemySoundSlot slot, uint id)
    {
        switch (slot)
        {
            case EnemySoundSlot.Walk: block.SFX_ID_walk = id; break;
            case EnemySoundSlot.Run: block.SFX_ID_run = id; break;
            case EnemySoundSlot.HeartbeatPulse: block.SFX_ID_heartbeatPulse = id; break;
            // The sleeper's idle grunt: its EnemyVoice posts it again after each random wait, and once when it starts sensing.
            case EnemySoundSlot.HibernateIdle: block.SFX_ID_hibernateIdle = id; break;
            case EnemySoundSlot.DetectionStart: block.SFX_ID_hibernateDetectionStart = id; break;
            case EnemySoundSlot.WakeUp: block.SFX_ID_hibernateWakeUp = id; break;
            case EnemySoundSlot.Scream: block.SFX_ID_scream = id; break;
            case EnemySoundSlot.StuckInGlue: block.SFX_ID_stuckInGlue = id; break;
            case EnemySoundSlot.ReleaseFromGlue: block.SFX_ID_releaseFromGlue = id; break;
            // The game picks between these two by whether this client is the target; to an author they are one event.
            case EnemySoundSlot.AttackWindUp: block.SFX_ID_attackWindUp = id; block.SFX_ID_attackWindUp_NotLocalTarget = id; break;
            case EnemySoundSlot.AttackFire: block.SFX_ID_attackFire = id; break;
            case EnemySoundSlot.HurtSmall: block.SFX_ID_hurtSmall = id; break;
            case EnemySoundSlot.HurtBig: block.SFX_ID_hurtBig = id; break;
            case EnemySoundSlot.SleepingDeath: block.SFX_ID_hibernateDie = id; break;
            case EnemySoundSlot.Death: block.SFX_ID_die = id; break;
            case EnemySoundSlot.JumpStart: block.SFX_ID_JumpStart = id; break;
            case EnemySoundSlot.JumpInAir: block.SFX_ID_JumpInAir = id; break;
            case EnemySoundSlot.JumpLand: block.SFX_ID_JumpLand = id; break;
        }
    }

    /// <summary>The game posted a reserved event (<see cref="AudioKit.ReserveGameEvent"/>).</summary>
    private void Play(uint eventId, IntPtr soundPlayer, Vector3 position, bool positioned)
    {
        if (!_soundById.TryGetValue(eventId, out var sound)) return;
        // The game posts some events again while the last post of them still sounds (a sleeper's heartbeat every beat, its
        // idle grunt on a timer). A custom sound runs to its end on its enemy; a post that comes while it is still heard
        // starts no second copy over it.
        var key = (soundPlayer, sound);
        if (soundPlayer != IntPtr.Zero && _sounding.TryGetValue(key, out var heard) && heard()) return;
        string? refusal;
        Func<bool>? sounding = null;
        var known = _bodies.TryGetValue(soundPlayer, out var owner);
        if (positioned) refusal = CustomSoundPlayback.Play(sound, position, out sounding);
        else if (known && owner.Body != null && !owner.Body.WasCollected) refusal = CustomSoundPlayback.Play(sound, owner.Body, out sounding);
        else refusal = ResultCode.AudioFollowUnavailable;
        if (sounding != null && soundPlayer != IntPtr.Zero) _sounding[key] = sounding;
        Played?.Invoke(known ? owner.Type : 0, sound, refusal);
    }

    /// <summary>The game posted a replaced event: the enemy type it was posted on (0 when the sound player is no enemy's), the sound,
    /// and why it did not start (null when it did).</summary>
    internal Action<uint, string, string?>? Played;

    /// <summary>The level ended and its enemies with it. The reserved IDs stay: they name sounds, not lives.</summary>
    internal void ClearWorld() { _bodies.Clear(); _sounding.Clear(); }
}
