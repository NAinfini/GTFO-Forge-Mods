using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enemies;
using ForgeCore;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>Plays a recording in a single-player local level: starts the level from the recording's seeds, removes what the game spawned for
/// itself, and from then on puts the recorded enemies, one puppet each, where the recording had them while the camera follows a player.
/// The game's own play does not run: nothing here settles damage. Main thread only.</summary>
internal static class Replayer
{
    private static Stream? _stream;
    private static Timeline? _timeline;
    private static PlaybackClock? _clock;
    private static Analysis? _analysis;
    private static int _follow;
    private static bool _active;
    private static bool _lateRegistered;
    private static readonly WorldStateSetter Setter = new();

    /// <summary>The recording chosen to play, from the moment the level starts loading until the replay ends. The level is a playback
    /// for exactly that long (ForgeCore <see cref="Puppets.Playback"/>), so Forge's behaviour does not run in it.</summary>
    internal static ReplayEntry? Pending
    {
        get => _pending;
        private set { _pending = value; Puppets.Playback = value != null; }
    }
    private static ReplayEntry? _pending;

    internal static bool Active => _active;
    internal static PlaybackClock? Clock => _clock;
    internal static Timeline? Track => _timeline;
    internal static Analysis? Report => _analysis;
    internal static int Follow { get => _follow; set => _follow = value; }
    internal static int RecorderSlot => Pending?.Header?.RecorderSlot ?? -1;

    /// <summary>A recording was chosen and its level is on the way: another choice now would pull the running start apart.</summary>
    internal static bool Starting => !_active && SoloDrop.State is SoloDropState.Menu or SoloDropState.Lobby or SoloDropState.Select or SoloDropState.Ready or SoloDropState.Requested;

    /// <summary>What to tell the player when the last chosen recording could not be started, one sentence, for the replay list to say once; null when
    /// nothing failed since it last said so. The cause itself goes to the log only.</summary>
    internal static ForgeUI.UIText? Failure { get; private set; }

    internal static ForgeUI.UIText? TakeFailure() { var failure = Failure; Failure = null; return failure; }

    private static void Fail(string why, ForgeUI.UIText? say = null) { Failure = say ?? ForgeUI.UIText.Key("play.failed"); Plugin.Logger.Error("The replay did not start: " + why); }

    internal static void Start(string path)
    {
        if (Starting) return;
        var entry = ReplayLibrary.List(Plugin.ReplayFolder).FirstOrDefault(e => e.Path == path);
        if (entry == null) { Fail("the recording " + Path.GetFileName(path) + " is not in the replay folder."); return; }
        if (entry.Header == null) { Fail(entry.Refusal ?? "the recording cannot be read."); return; }
        var h = entry.Header;
        if (RundownNames.Refusal(entry) is { } refusal) { Fail("the recording's level is not installed: " + entry.Level, ForgeUI.UIText.Plain(refusal)); return; }
        Pending = entry;
        try { SoloDrop.Start(new LevelId(h.RundownId, h.Tier, h.Index), new LevelSeeds(h.BuildSeed, h.HostIdSeed, h.SessionSeed)); }
        catch (Exception error) { Pending = null; Fail(error.Message); }
    }

    internal static void Entered()
    {
        if (Pending == null || _active) return;
        try
        {
            _stream = new FileStream(Pending.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _timeline = new Timeline(ChunkLog.Open(_stream, ReplayHeader.FormatVersion));
            _clock = new PlaybackClock(_timeline.DurationMs);
            _analysing = Analyse(Pending.Path, Pending.Header!);
            if (!_lateRegistered) { _lateRegistered = true; Frame.OnLateUpdate(Plugin.Id, Late); }
            _active = true;
            _preparing = true; _leftElevator = false; _prepareFrom = ForgeCore.Frame.UnscaledTime;
        }
        catch (Exception error) { Fail("the recording could not be opened: " + error.Message); Stop(); ReplayScreen.Exit(); }
    }

    // The whole recording is read once for the bar, the statistics and who joined, on a worker thread over a stream and timeline of its own:
    // what it asks of the game data (a kill worth a mark, how far an enemy type hears) is read here on the game thread into plain tables first.
    private static System.Threading.Tasks.Task<Analysis>? _analysing;

    // Also what the replay list reads a recording's team tables with, so opening the details never reads a file on the game thread.
    internal static System.Threading.Tasks.Task<Analysis> Analyse(string path, ReplayHeader header, System.Threading.CancellationToken cancel = default)
    {
        var notable = new Dictionary<ulong, bool>();
        var hearing = new Dictionary<ulong, Hearing?>();
        foreach (var block in GameData.EnemyDataBlock.GetAllBlocks())
        {
            notable[block.persistentID] = Notable(block.persistentID);
            hearing[block.persistentID] = ReplayScreen.HearingOf(block.persistentID);
        }
        var exact = header.RecorderIsHost; var recorder = header.RecorderSlot;
        var damage = DamageTables();
        return System.Threading.Tasks.Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Analysis.Run(new Timeline(ChunkLog.Open(stream, ReplayHeader.FormatVersion)), exact, recorder,
                id => notable.TryGetValue(id, out var known) && known, id => hearing.TryGetValue(id, out var h) ? h : null, damage, header.Players, cancel);
        }, cancel);
    }

    // What the client's estimate of other players' damage reads, copied to plain tables on the game thread.
    private static DamageData DamageTables()
    {
        var guns = new Dictionary<ulong, GunDamage>();
        foreach (var block in GameData.ArchetypeDataBlock.GetAllBlocks())
            guns[block.persistentID] = new GunDamage(block.Damage, block.DamageFalloff.x, block.DamageFalloff.y, block.PrecisionDamageMulti, (int)block.DamageBoosterEffect, block.PiercingBullets, block.Sentry_LongRangeThreshold, block.Sentry_ShortRangeThreshold);
        // A sentry's shot carries its fire mode: 10 semi, 11 auto, 12 burst, 13 shotgun semi. The sentries' archetypes are the ones whose damage the sentry booster moves.
        var sentries = new Dictionary<ulong, ulong>();
        foreach (var block in GameData.ArchetypeDataBlock.GetAllBlocks())
            if (block.DamageBoosterEffect == AgentModifier.SentryGunDamage)
                sentries[block.ShotgunBulletCount > 0 ? 13UL : block.FireMode == eWeaponFireMode.Auto ? 11UL : block.FireMode == eWeaponFireMode.Burst ? 12UL : 10UL] = block.persistentID;
        var enemies = new Dictionary<ulong, EnemyLimbs>();
        foreach (var block in GameData.EnemyDataBlock.GetAllBlocks())
            if (GameData.EnemyBalancingDataBlock.GetBlock(block.BalancingDataId) is { } balancing)
                enemies[block.persistentID] = new EnemyLimbs(balancing.Health.WeakspotDamageMulti, balancing.Health.ArmorDamageMulti, balancing.AllowDamgeBonusFromBehind);
        var swings = new Dictionary<uint, SwingDamage>();
        foreach (var category in GameData.GearCategoryDataBlock.GetAllBlocks())
            if (GameData.MeleeArchetypeDataBlock.GetBlock(category.MeleeArchetype) is { } melee && GameData.MeleeAnimationSetDataBlock.GetBlock(melee.MeleeAnimationSet) is { } set)
                swings[category.persistentID] = new SwingDamage(melee.LightAttackDamage, melee.ChargedAttackDamage, melee.LightPrecisionMulti, melee.ChargedPrecisionMulti, melee.LightBackstabberMulti, melee.ChargedBackstabberMulti, melee.LightSleeperMulti, melee.ChargedSleeperMulti,
                    set.MaxDamageChargeTime, melee.CameraDamageRayLength + melee.AttackSphereRadius, melee.CanHitMultipleEnemies, set.FPAttackMissRight.DamageStartTime, set.FPAttackMissRight.DamageEndTime, set.FPAttackChargeUpReleaseRight.DamageStartTime, set.FPAttackChargeUpReleaseRight.DamageEndTime);
        var boosters = new Dictionary<uint, int>();
        foreach (var block in GameData.BoosterImplantEffectDataBlock.GetAllBlocks()) boosters[block.persistentID] = (int)block.Effect;
        return new DamageData(id => guns.TryGetValue(id, out var g) ? g : null, id => enemies.TryGetValue(id, out var e) ? e : null, id => swings.TryGetValue(id, out var s) ? s : null, id => boosters.TryGetValue(id, out var b) ? b : null, mode => sentries.TryGetValue(mode, out var g) ? g : null);
    }

    // The analysis is in: the replay is set up as it starts. Until then nothing of the replay moves.
    private static bool Analysed()
    {
        if (_analysing == null) return true;
        if (!_analysing.IsCompleted) return false;
        var task = _analysing; _analysing = null;
        try
        {
            _analysis = task.Result;
            // The analysis read every block once: the events whose effect lasts are in its timeline, so the first far jump reads no block to find them.
            if (_analysis.Source is { } read) _timeline!.AdoptLasting(read);
            var header = Pending!.Header!;
            WarnContent(header);
            _follow = Math.Max(0, header.RecorderSlot);
            _startMs = LandedAt(_timeline!, _follow);
            _timeline!.Seek(_startMs);
            _clock!.Seek(_startMs);
            var spawned = new List<EnemyAgent>();
            foreach (var enemy in ForgeCore.Enemies.Live) if (enemy != null) spawned.Add(enemy);
            foreach (var enemy in spawned) { try { enemy.Disable(); } catch (Exception error) { Plugin.Logger.ErrorOnce("A game-spawned enemy could not be disabled: " + error.Message); } }
            // The one camera lease belongs to ReplayCamera (taken when its screen opens). A second, newer lease here would silently own the view
            // once the background analysis finished: ReplayCamera.Look would return false and every view, the eyes included, would stay at this
            // old third-person placement (1.6 m up, 3 m back along the look, no wall cast).
            Setter.Reset(); EventPresenter.Reset(); EventPresenter.Setter = Setter; GameVoices.Begin(); EnvironmentView.Begin();
            // The level objects, spawned items, fog and downed players as the recording starts, set at once like after a jump.
            EventPresenter.Jumped(_timeline.State);
            // The game fills the slots it opens with bots; their player agents are the puppets of the recorded players (the local player is the observer).
            var everHeld = 0;
            for (var slot = 0; slot < 4; slot++) if (ReplayFeed.EverHeld(slot, header.Players, _analysis.Presence)) everHeld++;
            Puppeteer.BotsAsked = Math.Min(3, everHeld);
            Puppets.OpenBotSlots(Puppeteer.BotsAsked);
            Observer.Enter();
            _prepareFrom = ForgeCore.Frame.UnscaledTime;
            return true;
        }
        catch (Exception error) { Fail("the replay could not be opened: " + error); Stop(); ReplayScreen.Exit(); return false; }
    }

    // A package that gives enemies and the like their look may differ from the recording's: the enemy then shows in another look (the
    // vanilla one when the package is gone). Said once in the log and in the replay's skipped notice, not left to be noticed on screen.
    private static void WarnContent(ReplayHeader header)
    {
        var diff = ContentDifference.Of(header.Content, Session.ContentHere());
        foreach (var package in diff.Missing) Skipped.Note("a content package of the recording is not installed; what it adds shows in the vanilla look", package);
        foreach (var package in diff.Changed) Skipped.Note("a content package has other files than when recorded; what it adds may look different", package);
    }

    // The recording starts while its players are still falling in the elevator (at the level's build, y 1857 in the 11-05 recording), which this
    // level has already done or is doing on its own: the replay starts where the watched player has left the elevator. A mid-join recording starts at 0.
    private const uint MaxRideMs = 30000;
    private static uint LandedAt(Timeline timeline, int slot)
    {
        timeline.Seek(0);
        for (uint ms = 0; ms <= MaxRideMs && ms < timeline.DurationMs; ms += 100)
        {
            timeline.Advance(ms, null);
            var status = timeline.State.PlayerStatus[slot & 3];
            if (status == null) continue;
            var read = new PayloadReader(status);
            read.U();
            if ((PlayerLocomotion.PLOC_State)read.U() != PlayerLocomotion.PLOC_State.InElevator) return ms;
        }
        return 0;
    }

    // The clock stands, and the screen says so instead of showing a half-made scene, until the level is what the replay needs: the observer out of the
    // elevator (while in it the game allows no wield and the first-person item stays in its elevator pose), the cage and shaft gone the way the game
    // sends them away for a player dropping in, every recorded player's body held and dressed, the followed player's gear on the observer.
    private const float MaxElevatorSeconds = 60f, MaxPrepareSeconds = 30f;
    private static bool _preparing, _leftElevator;
    private static float _prepareFrom;
    private static uint _startMs;

    /// <summary>The replay is being made ready: the clock has not started.</summary>
    internal static bool Preparing => _preparing;

    private static void Prepare()
    {
        var waited = ForgeCore.Frame.UnscaledTime - _prepareFrom;
        if (!_leftElevator)
        {
            var local = Players.Local?.TryCast<PlayerAgent>();
            var riding = local == null || local.Locomotion == null || local.Locomotion.m_currentStateEnum == PlayerLocomotion.PLOC_State.InElevator;
            if (riding && waited < MaxElevatorSeconds) return;
            if (riding) Plugin.Logger.Error("The observer was still in the elevator after " + MaxElevatorSeconds + " s; the replay goes on.");
            _leftElevator = true; _prepareFrom = ForgeCore.Frame.UnscaledTime;
            try { ElevatorRide.DropinElevatorExit(); }
            catch (Exception error) { Plugin.Logger.Error("The elevator could not be sent away: " + error.Message); }
            return;
        }
        var ready = Puppeteer.AllHeld(_timeline!.State, Pending!.Header!.Players) && FirstPerson.GearOn;
        if (!ready && waited < MaxPrepareSeconds) return;
        if (!ready) Plugin.Logger.Error("The replay started before everything was ready: players " + Puppeteer.AllHeld(_timeline.State, Pending.Header.Players) + ", first-person gear " + FirstPerson.GearOn + ".");
        _preparing = false;
        _clock!.Play();
    }

    /// <summary>What the opening wait is at, all of it read from the level, for the screen.</summary>
    internal static (bool Landed, int Bodies, int BodiesWanted, int Gear, float Waited, float Limit) PrepareProgress()
    {
        var bodies = 0;
        var wanted = Pending?.Header?.Players.Count ?? 0;
        if (Pending?.Header != null) foreach (var player in Pending.Header.Players) if (Puppeteer.BodyOf(player.Slot) != null) bodies++;
        return (_leftElevator, bodies, wanted, FirstPerson.GearDone, ForgeCore.Frame.UnscaledTime - _prepareFrom, _leftElevator ? MaxPrepareSeconds : MaxElevatorSeconds);
    }

    /// <summary>The observer does not want to wait: the clock starts now, and what is missing arrives as it comes.</summary>
    internal static void SkipPreparing()
    {
        if (!_preparing || _analysing != null) return;
        if (!_leftElevator)
        {
            _leftElevator = true;
            try { ElevatorRide.DropinElevatorExit(); }
            catch (Exception error) { Plugin.Logger.Error("The elevator could not be sent away: " + error.Message); }
        }
        _preparing = false;
        _clock!.Play();
    }

    // A kill is worth a mark on the bar when the game's own data makes the enemy a scout or a boss (ReplayFeed.NotableKill); a level has thousands of others.
    private static readonly Dictionary<ulong, (bool Notable, bool Boss, bool Big)> NotableEnemies = new();
    private static HashSet<uint>? _scouts;

    private static bool Notable(ulong dataId) => Kind(dataId).Notable;

    /// <summary>A boss by the game's data (<see cref="ReplayFeed.Boss"/>): its kill is a highlight.</summary>
    internal static bool Boss(ulong dataId) => Kind(dataId).Boss;

    /// <summary>A big enemy (<see cref="ReplayFeed.Big"/>) the automatic camera may watch while it hunts.</summary>
    internal static bool Big(ulong dataId) => Kind(dataId).Big;

    private static (bool Notable, bool Boss, bool Big) Kind(ulong dataId)
    {
        if (NotableEnemies.TryGetValue(dataId, out var known)) return known;
        try
        {
            if (_scouts == null)
            {
                _scouts = new HashSet<uint>();
                foreach (var population in GameData.EnemyPopulationDataBlock.GetAllBlocks())
                    foreach (var role in population.RoleDatas)
                        if (role.Role == GameData.eEnemyRole.Scout) _scouts.Add(role.Enemy);
            }
            var block = GameData.EnemyDataBlock.GetBlock((uint)dataId);
            if (block == null) return default;
            var health = GameData.EnemyBalancingDataBlock.GetBlock(block.BalancingDataId)?.Health?.HealthMax ?? 0f;
            var type = (int)block.EnemyType;
            return NotableEnemies[dataId] = (ReplayFeed.NotableKill(_scouts.Contains((uint)dataId), type, health), ReplayFeed.Boss(type, health), ReplayFeed.Big(type));
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("An enemy's class could not be read: " + error.Message); return default; }
    }

    internal static void Stop()
    {
        _preparing = false;
        Freeze(false);
        FirstPerson.Forget();
        RestoreFirstPerson();
        Puppeteer.ReportUnshown();
        Skipped.End();
        Carried.Clear();
        Puppeteer.Clear();
        Observer.Exit();
        ReplayAssist.Reset();
        GameVoices.End();
        ForgeAudio.AudioKit.SilenceGame(false); EventPresenter.Reset(); Setter.Reset(); Listening.Reset();
        _stream?.Dispose(); _stream = null;
        _timeline = null; _clock = null; _analysis = null; _analysing = null; Pending = null; _active = false;
    }

    internal static void Tick()
    {
        // The drop ended in failure (the level is not installed, the game refused it): nothing is on the way any more, the list says why,
        // and the next choice starts afresh.
        if (Pending != null && !_active && SoloDrop.State == SoloDropState.Failed) { Pending = null; Fail(SoloDrop.Failure); }
        if (!_active || _timeline == null || _clock == null || !Analysed()) return;
        if (_preparing) Prepare();
        _clock.Advance(ForgeCore.Frame.UnscaledDeltaTime);
        _timeline.Advance(_clock.PositionMs, OnEvent);
        EventPresenter.Flush();
        Setter?.WakeDoors();
        ShownPoses.Frame(_clock.PositionMs);
        Terminals.Frame(_clock.PositionMs);
        Flights.Frame(_clock.PositionMs);
    }

    /// <summary>Jumps to a moment: the world is restored from the nearest keyframe and puppets of enemies that no longer exist are removed.</summary>
    internal static void Jump(uint ms)
    {
        _clock!.Seek(Math.Max(ms, _startMs));
        _timeline!.Seek(_clock.PositionMs);
        EventPresenter.Jumped(_timeline.State);
        Puppeteer.Jumped();
        ReplayCamera.Snap();
        _clearEffects = true; _jumpedAt = ForgeCore.Frame.UnscaledTime;
    }

    // Paused, the world stands still with the replay: the game's own time stops (its animations, its particle effects, an enemy attack's
    // state and timers, the puppets' actions) and so does Wwise, where every game sound plays (an attack's loop, a gunshot's tail, a
    // scan's hum) and every sound the replay posts (ReplaySounds). Played again, all of it goes on from where it stood: nothing starts again and nothing is doubled.
    // The replay itself runs on real time (its clock, camera, screen), so it keeps working while paused. Not while the level is being
    // prepared: the game's bots and drop need their time then.
    private static bool _frozen;
    private static float _scaleBefore = 1f;

    internal static bool GameMenuOpen() => GuiManager.MainMenuLayer is { } menu && menu.IsVisible();

    private static void Freeze(bool frozen)
    {
        if (frozen == _frozen) return;
        _frozen = frozen;
        try
        {
            if (frozen)
            {
                _scaleBefore = Time.timeScale > 0f ? Time.timeScale : 1f;
                Time.timeScale = 0f;
                AkSoundEngine.Suspend(false);
            }
            else
            {
                Time.timeScale = _scaleBefore;
                AkSoundEngine.WakeupFromSuspend();
            }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The world could not be " + (frozen ? "frozen for the pause" : "let go after the pause") + ": " + error.Message); }
    }

    // A jump: the particles in the air (an explosion's, a muzzle flash's, blood, a hit's) belong to the moment left and are cleared. A one-shot
    // effect is then over; a looping one (a light's, a fog's, a sentry's) goes on emitting, since what it belongs to is still there (the game's
    // interop does not say which systems loop, so all are cleared and only the looping ones refill). Once the jumps stop for a moment: finding
    // every particle system walks the whole scene, which was done five times a second while the bar was dragged.
    private static bool _clearEffects;
    private static float _jumpedAt;
    private const float SettleSeconds = 0.15f;

    private static void ClearEffects()
    {
        if (!_clearEffects || ForgeCore.Frame.UnscaledTime - _jumpedAt < SettleSeconds) return;
        _clearEffects = false;
        try
        {
            foreach (var system in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
                if (system != null && system.isPlaying) system.Clear(false);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The effects of the moment left could not be cleared after a jump: " + error.Message); }
    }

    // Every one-shot event the timeline passes while playing.
    private static void OnEvent(Record record)
    {
        if (record.EventType == EventKind.EnemyGone)
        {
            var read = new PayloadReader(record.Payload!);
            var entity = (int)read.U();
            Puppeteer.EnemyEnded(entity, read.U() == 1UL);
        }
        EventPresenter.Present(record, _timeline!.State, _clock!.Speed);
        FirstPerson.Event(record, _follow);
    }

    // After the game's animation: puppets are placed and animated here so the animator's own movement does not stay.
    private static void Late()
    {
        if (!_active || _timeline == null || _clock == null || _analysing != null) return;
        try
        {
            var seconds = ForgeCore.Frame.UnscaledDeltaTime;
            // Above 4x nothing sounds (PLAN 5.3): what the replay posts itself checks the tier; this holds the sounds the game's own handlers post too.
            ForgeAudio.AudioKit.SilenceGame(_clock.Playing && PlaybackClock.Tier(_clock.Speed) == SoundTier.Mute);
            // Not while the game's own menu is open: its hold-to-exit button (Exit Expedition) fills on game time and never filled in a paused replay (11-05).
            Freeze(!_clock.Playing && !_preparing && !GameMenuOpen() && !FirstPerson.Settling);
            ClearEffects();
            var eyes = FirstPerson.Active;
            if (!eyes) FirstPerson.Reset();
            HideObserver(eyes);
            if (_preparing && !_leftElevator) return;
            // In the eyes, and for the player the replay opens on. The swap is heard (the game brings each piece up), so not on every switch of the automatic camera.
            if (_preparing || eyes) FirstPerson.Keep(_timeline.State, _follow, Pending!.Header!.Players);
            Vector3? view = ReplayCamera.Mode == ViewMode.Free ? ReplayCamera.Shown : null;
            // The camera inside a head would see its inside: the viewed player's body is off while the eyes are the view.
            // A piece the viewed player's body shows for a moment (a magazine in a reload) stands right at the camera, a second one beside the first-person one.
            // Whatever the puppets' update does, the hide or the put-back of the bodies runs: a throw before it left a body hidden after the eyes were left.
            var inEyes = ReplayCamera.Mode == ViewMode.Eye || FirstPerson.Active;
            try
            {
                Puppeteer.Sync(_timeline.State, _clock.PositionMs, _follow, view, _clock.Playing ? _clock.Speed : 0f, seconds, Pending!.Header!.Players);
                Carried.Frame(_timeline.State, _follow, FirstPerson.Active);
            }
            finally { Puppeteer.HideViewed(inEyes ? _follow : -1, inEyes); }
            if (ForgeCore.Frame.UnscaledTime >= _nextSweep) { _nextSweep = ForgeCore.Frame.UnscaledTime + 1f; Observer.Sweep(); }
            ReplayAssist.Frame(_follow, ReplayCamera.Mode == ViewMode.Eye || FirstPerson.Active);
            Listening.Frame();
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Placing the replay puppets failed: " + error); }
    }

    // The observer is nobody in the recording. Its body stands where it was left: its clothes hang under the camera holder and its back items under
    // the agent, so a camera passing there saw them, a rifle, a pistol and a tool in the air. Off for the whole replay (re-checked as weapons change).
    private static readonly List<Renderer> HiddenBody = new(), HiddenHeld = new();
    private static float _nextSweep, _nextBody, _nextHeld, _nextWield, _heldHotUntil;
    private static int _wielded = -2;
    private static void HideObserver(bool eyes)
    {
        var local = Players.Local?.TryCast<PlayerAgent>();
        if (local == null || local.WasCollected || local.FPItemHolder == null) return;
        var now = ForgeCore.Frame.UnscaledTime;
        var holder = local.FPItemHolder.transform;
        if (now >= _nextBody)
        {
            _nextBody = now + 1f;
            foreach (var renderer in local.GetComponentsInChildren<Renderer>(false))
                if (renderer != null && renderer.enabled && !renderer.transform.IsChildOf(holder)) { renderer.enabled = false; HiddenBody.Add(renderer); }
            var flat = holder.root.Find("FlatTransform");
            if (flat != null)
                for (var i = 0; i < flat.childCount; i++)
                {
                    // Every player's character rig may hang here: only the observer's, never a puppet's body (a hidden one was an invisible player).
                    // A bot's rig can hang here before the bot can be told from the observer: when it can, what was hidden on it comes back.
                    var rig = flat.GetChild(i);
                    if (!rig.name.StartsWith("PlayerCharacter_rig", StringComparison.Ordinal)) continue;
                    if (!Puppeteer.OthersRig(rig)) Hide(rig.GetComponentsInChildren<Renderer>(false), HiddenBody);
                    else for (var r = HiddenBody.Count - 1; r >= 0; r--)
                        if (HiddenBody[r] != null && HiddenBody[r].transform.IsChildOf(rig)) { HiddenBody[r].enabled = true; HiddenBody.RemoveAt(r); }
                }
        }
        // The arms and what they hold show at the eyes, and only while it is what the recording says: not while the viewed player's gear is being put on
        // one piece at a time, and not when the player's hands are empty or hold what the observer cannot (a resource pack: the recording does not say which).
        if (eyes && FirstPerson.HoldsRecorded(local)) { Restore(HiddenHeld); return; }
        // At the eyes the held pieces are looked at every 0.1 s only for a second after the observer changed weapon (the new one comes up piece
        // by piece); otherwise once a second.
        if (now >= _nextWield)
        {
            _nextWield = now + 0.2f;
            var wielded = local.Inventory == null ? -1 : (int)local.Inventory.WieldedSlot;
            if (wielded != _wielded) { _wielded = wielded; _heldHotUntil = now + 1f; _nextHeld = 0f; }
        }
        if (now < _nextHeld) return;
        _nextHeld = now + (eyes && now < _heldHotUntil ? 0.1f : 1f);
        Hide(local.FPItemHolder.GetComponentsInChildren<Renderer>(false), HiddenHeld);
    }

    private static void Hide(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers, List<Renderer> into)
    {
        foreach (var renderer in renderers)
            if (renderer != null && renderer.enabled) { renderer.enabled = false; into.Add(renderer); }
    }

    private static void Restore(List<Renderer> hidden)
    {
        foreach (var renderer in hidden) if (renderer != null) renderer.enabled = true;
        hidden.Clear();
    }

    private static void RestoreFirstPerson() { Restore(HiddenBody); Restore(HiddenHeld); }

}

internal static class Version
{
    internal static long Revision => CellBuildData.GetRevision();
}
