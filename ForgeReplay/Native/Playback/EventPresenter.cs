using ForgeCore;
using System;
using System.Collections.Generic;
using ForgeAudio;
using LevelGeneration;
using Player;
using ForgeVisual;
using UnityEngine;

namespace ForgeReplay;

/// <summary>Turns the events the timeline passes while playing into what the original game shows and plays: a tracer and the hit sound for
/// a shot, the weapon's own fire sound (recorded with the first shot of each weapon), swings, enemy death, hurt and scream voices, tongues,
/// projectiles, explosions, waves, terminals, doors and generators switching through the game's own handlers, alarms, scans, revives, pings,
/// mines, sentries and their shots, lost limbs, scout feelers, items spawned during the level, the recorded fog, a player going down and the
/// reactor. Presentation only: nothing here settles damage or changes the level's play state. Sound follows the speed
/// tier of PLAN 5.3 (everything up to 2x, key events up to 4x, none above); a door, generator or box that the tier mutes is set without
/// animation, effects always show. A jump presents nothing and instead puts the level objects into the recorded states at once. Main thread only.</summary>
internal static class EventPresenter
{
    internal static WorldStateSetter? Setter { get; set; }

    private const uint HurtVoiceGapMs = 400;
    private static readonly Dictionary<(ushort Kind, ulong Key), ulong> Last = new();
    private static readonly Dictionary<ulong, EnemyVoice> Voices = new();
    private static readonly Dictionary<int, uint> LastHurtVoice = new();
    // Every level object a recorded state was shown on, and where: a jump to before its first event puts it back as the level had it.
    private static readonly Dictionary<(ushort Kind, ulong Key), Vector3> Touched = new();
    // The state each touched object had in the replay level before the recording first set it (the level builds as the recorded one did).
    private static readonly Dictionary<(ushort Kind, ulong Key), (ulong State, ulong Extra)> Originals = new();
    // What each level object was last put into here: a jump sets only the objects whose state at the new moment differs. Setting the ~130 to 360
    // the recording has touched by mid-level on every jump (median 0 to 1 of them differ from 1 s or 5 s before, 11-06 recordings) ran every
    // door's handler again, which played the door sounds of the whole level at once (11-06) and made dragging the bar slow.
    private static readonly Dictionary<(ushort Kind, ulong Key), (ulong State, ulong Extra)> Applied = new();
    private static SoundTier _tier;

    /// <summary>Presents one event passed during normal playing at <paramref name="speed"/>.</summary>
    internal static void Present(Record r, WorldState state, float speed)
    {
        _tier = PlaybackClock.Tier(speed);
        var read = new PayloadReader(r.Payload!);
        try
        {
            if (_sentryPending) SentryAfter(r);
            switch (r.EventType)
            {
                case EventKind.Shot: Shot(read, state, r.TimeMs); break;
                case EventKind.Note: ReplayEvents.Noted(new ReplayNote(r.TimeMs, read.Text(), read.Text(), read.Text(), read.U() != 0)); break;
                case EventKind.EnemyLimb:
                    {
                        var entity = (int)read.U();
                        Puppeteer.EnemyLimb(entity, read.U());
                        break;
                    }
                case EventKind.SentryShot:
                    {
                        read.U();
                        var (x, y, z) = read.Pos();
                        // The sentry and the muzzle's direction: the replay's own sentry turns and fires.
                        var sentry = read.U();
                        var (dx, dy, dz) = read.Pos();
                        // The gun's audio block and fire mode come last; a zero audio block takes them from the sentry's recorded gear, and no sound when it has none.
                        var audioId = read.U();
                        var fireMode = (int)read.U();
                        if (Deployables.Shot(sentry, new Vector3(x, y, z), new Vector3(dx, dy, dz), ref audioId, ref fireMode, out var muzzle))
                        {
                            if (_tier == SoundTier.All && audioId != 0 && Deployables.FireSound(sentry, (uint)audioId, fireMode, r.TimeMs) is { } fire) foreach (var layer in fire) Post(layer, muzzle);
                            var flash = Flashes[0] ??= Fx.Find(FlashNames[0]);
                            if (flash != null) Fx.Play(flash, muzzle, new Vector3(dx, dy, dz));
                            break;
                        }
                        _sentryPending = true; _sentryMs = r.TimeMs; _sentryAt = new Vector3(x, y, z);
                        break;
                    }
                case EventKind.Scout:
                    {
                        var entity = (int)read.U();
                        var feelersOut = read.U() == 1;
                        Puppeteer.EnemyFeelers(entity, feelersOut);
                        if (Where(state, entity, out var scoutAt)) Sound(feelersOut ? "SCOUT_FEELERS_OUT" : "SCOUT_FEELERS_IN", scoutAt, false);
                        break;
                    }
                case EventKind.ItemSpawn:
                    if (Setter != null && Setter.Spawns(state.ItemSpawns, state.ItemSpawns.Count) > 0) Skipped.Note("an item spawned during the recorded level could not be shown", "after a jump");
                    break;
                case EventKind.PlayerStatus: Downed(read); break;
                case EventKind.Melee:
                    {
                        var slot = (int)read.U() & 3;
                        var kind = read.U();
                        read.U();
                        // The puppet's weapon swings by the game's own third-person code, which also plays its swing sound. A shove is the Interact event's animation; a player with no puppet only sounds.
                        if (kind != 2 && Puppeteer.MeleeSwing(slot, kind)) break;
                        Puppeteer.CosmeticOf(slot)?.Act(EventKind.Melee, kind); // the swing's sound below plays whether or not a body state was read
                        if (Where(state, slot, out var at)) Sound(kind == 2 ? "MELEE_SHOVE" : kind == 1 ? "MELEE_SWISH_HEAVY" : "MELEE_SWISH_REGULAR", at, false);
                        break;
                    }
                case EventKind.EnemyHurt:
                    {
                        // U entity, kind, damage x10, attacker, limb, settled, taken x10, lethal, overkill, weak spot, multiplier, behind.
                        var entity = (int)read.U();
                        var kind = read.U();
                        var damage = read.U() / 10f; read.U();
                        var limb = Vec.Zero;
                        var struck = Puppeteer.LimbPoint(entity, read.U(), out limb);
                        var settled = read.U() == 1; var taken = read.U() / 10f;
                        read.U(); read.U();
                        var weak = read.U() == 1; read.U(); var behind = read.U() == 1;
                        if (!Where(state, entity, out var at)) break;
                        if (kind == 0)
                        {
                            // The blood comes from the limb that was hit, where the puppet has it.
                            var bleed = struck ? limb : at;
                            Sound("BULLETHITENEMYSYNC", bleed, false);
                            Impact(ref _blood, "Game:FX_3D_Blood_Impact", bleed, Vec.Up);
                        }
                        // The number over the limb hit; off above 2x, where they would only pile up.
                        if (speed <= 2f) ReplayScreen.DamagePop(struck ? limb : at + Vec.Up, settled ? taken : damage, weak, behind);
                        if (!LastHurtVoice.TryGetValue(entity, out var before) || r.TimeMs - before >= HurtVoiceGapMs)
                        {
                            LastHurtVoice[entity] = r.TimeMs;
                            HoldSound(VoiceOf(state, entity).Hurt, at);
                        }
                        break;
                    }
                case EventKind.EnemyGone:
                    {
                        // The timeline reports this before it forgets the enemy, so its family and last place are still there.
                        var entity = (int)read.U();
                        if (read.U() == 1 && (uint)entity < WorldState.MaxEntities && state.Poses[entity].Present)
                        {
                            ref var p = ref state.Poses[entity];
                            var place = new Vector3(p.X, p.Y, p.Z);
                            var voice = VoiceOf(state, entity);
                            Sound(voice.Death, place, true);
                            Burst(voice.DeathFx, place);
                        }
                        LastHurtVoice.Remove(entity);
                        break;
                    }
                case EventKind.Say:
                    {
                        if (_tier != SoundTier.All) break;
                        var said = ReplayExtras.Said(r.Payload!);
                        if (said.Slot < 0) break;
                        if (Puppeteer.PlayerOf(said.Slot) is { } speaker) speaker.Say(said.Event, said.Seed, said.InDialog, said.StartDialog, said.Subtitle);
                        else Puppeteer.CosmeticOf(said.Slot)?.Say(said.Slot, said.Event);
                        break;
                    }
                case EventKind.EnemyBeat:
                    {
                        // A sleeper's heartbeat: the puppet plays it as the game does where it arrives (glow, beat, its sound).
                        var (entity, strength, x, y, z) = ReplayExtras.Beat(r.Payload!);
                        Puppeteer.EnemyBeat(entity, strength, new Vector3(x, y, z));
                        break;
                    }
                case EventKind.Wake:
                    {
                        // The animation is the puppet's, from the recorded state; the game's scream belongs to the part of the wake-up state a puppet does not run.
                        var woken = (int)read.U();
                        if (Where(state, woken, out var wakeAt)) HoldSound(VoiceOf(state, woken).Scream, wakeAt);
                        break;
                    }
                case EventKind.Tongue:
                    {
                        var entity = (int)read.U();
                        var phase = read.U();
                        var (x, y, z) = read.Pos();
                        var at = new Vector3(x, y, z);
                        read.U();
                        var voice = VoiceOf(state, entity);
                        // The puppet's own tongue or tentacle reaches out at the recorded launch and comes back by itself. Its sounds are held: the game's tentacle may post them itself.
                        if (phase == 2)
                        {
                            if (Puppeteer.EnemyTongue(entity, (int)read.U(), at)) Launched.Add(entity);
                            if (voice.TongueOut.Length > 0) HoldSound(voice.TongueOut, at);
                        }
                        else if (phase == 0)
                        {
                            if (Launched.Contains(entity)) break;
                            Puppeteer.EnemyTongue(entity, 0, at);
                            if (voice.TongueOut.Length > 0) HoldSound(voice.TongueOut, at);
                        }
                        else
                        {
                            Launched.Remove(entity);
                            if (voice.TongueBack.Length > 0) HoldSound(voice.TongueBack, at);
                        }
                        break;
                    }
                case EventKind.Projectile:
                    {
                        var kind = read.U();
                        var id = read.U();
                        var (x, y, z) = read.Pos();
                        // A destroyed foam blob records no place. An aimed projectile flies as the game's own (Flights) from its recorded flight
                        // samples (kinds 3 and 4); the infection spit effect once shown here at the muzzle was not the game's look for it (11-06).
                        if (kind == 0)
                        {
                            Flights.Fired(ReplayExtras.AimedProjectile(r.Payload!).Type);
                            Sound("SHOOTERFIREMEDIUMPROJECTILE", new Vector3(x, y, z), false);
                        }
                        else if (kind == 1) FoamLaunch(r.TimeMs, new Vector3(x, y, z));
                        else if (kind == 3)
                        {
                            var type = read.U();
                            var (dx, dy, dz) = read.Pos();
                            Flights.Sample(id, r.TimeMs, new Vector3(x, y, z), type, new Vector3(dx, dy, dz));
                        }
                        else if (kind == 4) Flights.Ended(id);
                        break;
                    }
                case EventKind.Wave:
                    if (read.U() == 1 && Camera.main != null) Sound("PLAY_WAVE_DISTANT_ROAR", Camera.main.transform.position, true);
                    break;
                case EventKind.Terminal:
                    {
                        read.U();
                        var (x, y, z) = read.Pos();
                        Sound("TERMINAL_INPUT_3RD_PERSON", new Vector3(x, y, z), false);
                        break;
                    }
                case EventKind.TerminalLine:
                case EventKind.TerminalInput:
                case EventKind.TerminalState:
                    Terminals.Present(r, _tier == SoundTier.All);
                    break;
                case EventKind.PlayerHurt:
                    if (Where(state, (int)read.U() & 3, out var hurtAt)) Sound("PLAYERHURTGENERIC", hurtAt, false);
                    break;
                case EventKind.EnemyAttack:
                    {
                        var entity = (int)read.U();
                        var attackState = (Enemies.ES_StateEnum)read.U();
                        var animation = (int)read.U();
                        var (x, y, z) = read.Pos();
                        Puppeteer.EnemyAttack(entity, attackState, new Vector3(x, y, z), animation, read.U() / 1000f);
                        break;
                    }
                case EventKind.EnemyHit:
                    {
                        var entity = (int)read.U();
                        var type = (ES_HitreactType)read.U();
                        var direction = (ImpactDirection)read.U();
                        Puppeteer.EnemyHit(entity, type, direction, (int)read.U());
                        break;
                    }
                case EventKind.Scream:
                    {
                        var entity = (int)read.U();
                        var kind = read.U();
                        if (kind == 0) Puppeteer.EnemyScream(entity, (int)read.U());
                        if (Where(state, entity, out var screamAt)) Sound(kind == 0 ? VoiceOf(state, entity).Scream : "SCOUT_DETECT_SCREAM", screamAt, false);
                        break;
                    }
                case EventKind.Interact:
                    {
                        var interactSlot = (int)read.U();
                        var interactor = Puppeteer.PlayerOf(interactSlot);
                        if (interactor != null)
                        {
                            var interactType = (pGenericInteractAnimation.TypeEnum)read.U();
                            if (interactType == pGenericInteractAnimation.TypeEnum.Reload) Puppeteer.NoteReload(interactSlot);
                            interactor.Interact(interactType);
                        }
                        else if (Puppeteer.CosmeticOf(interactSlot)?.Act(EventKind.Interact, read.U()) != true) Skipped.Note("the body copy of a fourth player plays no interact, throw or melee animation (its animator states are not known)", "interact");
                        break;
                    }
                case EventKind.Throw:
                    {
                        var throwSlot = (int)read.U();
                        var thrower = Puppeteer.PlayerOf(throwSlot);
                        if (thrower != null) thrower.Throw((pPlayerThrowStatus.StatusEnum)read.U());
                        else if (Puppeteer.CosmeticOf(throwSlot)?.Act(EventKind.Throw, read.U()) != true) Skipped.Note("the body copy of a fourth player plays no interact, throw or melee animation (its animator states are not known)", "throw");
                        break;
                    }
                case EventKind.Revive:
                    if (Where(state, (int)read.U() & 3, out var reviveAt)) Sound("REVIVEEND", reviveAt, false);
                    break;
                case EventKind.Ping:
                    {
                        read.U();
                        var (x, y, z) = read.Pos();
                        Sound("PING_PLACED_GENERIC", new Vector3(x, y, z), false);
                        break;
                    }
                case EventKind.ShownFx:
                case EventKind.ShownSound:
                case EventKind.ShownHeld:
                case EventKind.ShownLoop:
                case EventKind.ShownModelState:
                case EventKind.ShownPose:
                    ShownPlayback.Present(r.EventType, r.Payload!, state, _tier, r.TimeMs);
                    break;
                case EventKind.Explosion:
                    {
                        var kind = read.U();
                        var (x, y, z) = read.Pos();
                        var at = new Vector3(x, y, z);
                        Deployables.Exploded(at);
                        // The game's explosive-mine event stays silent when posted by name (ForgeAudio GameEvents), so only the effect shows for it.
                        if (kind == 1) Sound("GLUE_MINE_EXPLODE", at, false);
                        if (kind == 1) Impact(ref _foamBurst, "Game:FX_TripmineGlue", at, Vec.Up); else Impact(ref _mineBurst, "Game:FX_Tripmine", at, Vec.Up);
                        break;
                    }
                default:
                    if (Timeline.IsStateEvent(r.EventType) || LevelPiece(r.EventType)) StateChanged(r.EventType, read);
                    break;
            }
        }
        catch (Exception error) { Skipped.Note("event " + r.EventType + " could not be presented", error.Message); }
    }

    private static FxHandle? _blood, _concrete, _mineBurst, _foamBurst;
    // The enemies whose recorded tongue launch the replay started: their "out" moment does not start it again.
    private static readonly HashSet<int> Launched = new();
    private static readonly Dictionary<string, FxHandle?> DeathBursts = new();
    private static void Burst(string name, Vector3 at)
    {
        if (!DeathBursts.TryGetValue(name, out var handle)) DeathBursts[name] = handle = Fx.Find(name);
        if (handle != null) Fx.Play(handle, at, Vec.Up);
    }
    private static readonly FxHandle?[] Flashes = new FxHandle?[4];
    private static readonly string[] FlashNames = { "Game:AssaultRifle_MuzzleFlash_3RD", "Game:Pistol_MuzzleFlash_3RD", "Game:Shotgun_MuzzleFlash_3RD", "Game:SubMachine_MuzzleFlash_3RD" };

    // The flash of the weapon the player puppet holds, or the game's flash of the weapon's class where the shooter has no puppet.
    private static Vector3 MuzzleFlash(int slot, ulong flash, Vector3 origin, Vector3 end)
    {
        if (Puppeteer.PlayerOf(slot)?.MuzzleFlash(out var muzzle) == true) return muzzle;
        if (Puppeteer.CosmeticOf(slot)?.MuzzleFlash(out var cosmeticMuzzle) == true) return cosmeticMuzzle;
        var index = (int)Math.Min(flash, 3UL);
        var handle = Flashes[index] ??= Fx.Find(FlashNames[index]);
        if (handle != null) Fx.Play(handle, origin, end - origin);
        return origin;
    }

    private static void Impact(ref FxHandle? cache, string name, Vector3 at, Vector3 normal)
    {
        cache ??= Fx.Find(name);
        if (cache != null) Fx.Play(cache, at, normal);
    }

    private static void Shot(PayloadReader read, WorldState state, uint timeMs)
    {
        var shooter = read.U();
        var item = read.U();
        var (ox, oy, oz) = read.Pos();
        var (ex, ey, ez) = read.Pos();
        var hit = read.U() == 1;
        // How the ray ended (1 level geometry, 2 an enemy, 3 a player).
        var hitKind = read.U();
        var origin = new Vector3(ox, oy, oz);
        var end = new Vector3(ex, ey, ez);
        var slot = (int)shooter & 3;
        var flash = 0UL;
        if (state.Objects.TryGetValue((EventKind.WeaponSound, item), out var note)) { var r = new PayloadReader(note); r.U(); r.Text(); flash = r.U(); }
        // The recorded origin is where the game cast the ray from, the shooter's eye: the visible bullet leaves the gun the viewer sees (the first-person
        // one plays its own flash with the shot's animation), as the game draws it.
        if (!FirstPerson.Muzzle(slot, out var from)) from = MuzzleFlash(slot, flash, origin, end);
        Fx.Tracer(from, end);
        if (hitKind == 2) Impact(ref _blood, "Game:FX_3D_Blood_Impact", end, origin - end);
        // One fire sound per trigger pull: a shotgun's pellets are separate shots of the same moment and sound once, as the game's synced fire does,
        // with every layer of the gun's per-shot list (body, mechanics, tail) at the muzzle; likewise one hit sound per moment.
        var first = _shotMs[slot] != timeMs || _shotItem[slot] != item;
        _shotMs[slot] = timeMs; _shotItem[slot] = item;
        if (first && state.Objects.TryGetValue((EventKind.WeaponSound, item), out var sound))
        {
            var weapon = new PayloadReader(sound);
            weapon.U();
            var name = weapon.Text();
            if (Deployables.ShotList(name) is { } layers) foreach (var layer in layers) Sound(layer, from, false);
            else if (name.Length > 0) Sound(name, from, false);
        }
        if (hitKind == 1) Impact(ref _concrete, "Game:FX_BulletImpact_Concrete", end, origin - end);
        if (first && (hitKind == 1 || hitKind == 0 && hit)) Sound("BULLETHITCONCRETESYNC", end, false);
    }
    // The foam gun's own burst plays one sound for the whole burst (GLUEGUN_BURST_05 to _12 by the number of blobs; a single blob plays GLUEGUNFIRE1SHOT),
    // not one per blob: a burst is the blobs launched within BurstGapMs of each other, counted from the analysed recording and sounded at its first blob.
    private const uint BurstGapMs = 250;
    private static uint _foamBurstMs = uint.MaxValue;

    private static void FoamLaunch(uint timeMs, Vector3 at)
    {
        var launches = Replayer.Report?.FoamLaunches;
        if (launches == null) { Sound("GLUEGUNFIRE1SHOT", at, false); return; }
        var i = launches.BinarySearch(timeMs);
        if (i < 0 || timeMs == _foamBurstMs) return;
        while (i > 0 && launches[i - 1] == timeMs) i--;
        if (i > 0 && timeMs - launches[i - 1] <= BurstGapMs) return;
        _foamBurstMs = timeMs;
        var blobs = 1;
        for (var k = i + 1; k < launches.Count && launches[k] - launches[k - 1] <= BurstGapMs; k++) blobs++;
        Sound(blobs == 1 ? "GLUEGUNFIRE1SHOT" : "GLUEGUN_BURST_" + Math.Clamp(blobs, 5, 12).ToString("00"), at, false);
    }
    private static readonly uint[] _shotMs = { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue };
    private static readonly ulong[] _shotItem = new ulong[4];

    // A sentry shot names only the gun's place. The game's bullet resolves in the same call, so when it hit an enemy the next event is that
    // enemy's bullet hurt at the same moment: the tracer then runs to the hit limb of the enemy's puppet. Otherwise (a miss, a hit settled on
    // another player's machine) only the muzzle flash shows, since the recording has no direction for the shot. The fire sound is the one
    // of the weapon audio data block the shot names (Deployables.FireSound); the hit sound comes with the hurt event.
    private static bool _sentryPending;
    private static uint _sentryMs;
    private static Vector3 _sentryAt;

    private static void SentryAfter(Record next)
    {
        _sentryPending = false;
        if (next.EventType == EventKind.EnemyHurt && next.TimeMs == _sentryMs)
        {
            var hurt = new PayloadReader(next.Payload!);
            var entity = (int)hurt.U();
            var kind = hurt.U();
            hurt.U(); hurt.U();
            if (kind == 0 && Puppeteer.LimbPoint(entity, hurt.U(), out var hit)) { SentryShow(hit); return; }
        }
        SentryShow(null);
    }

    private static void SentryShow(Vector3? hit)
    {
        var flash = Flashes[0] ??= Fx.Find(FlashNames[0]);
        if (flash != null) Fx.Play(flash, _sentryAt, hit is { } toward ? toward - _sentryAt : Vec.Zero);
        if (hit is { } end) Fx.Tracer(_sentryAt, end);
    }

    // eDown in PlayerStatus: the game has no third-person sound event for a player going down (AK.EVENTS holds PLAYER_DOWNED_1ST_PERSON only,
    // and the TEAMMATES_DOWN_* events belong to the music), so the first-person one plays when the camera sees through that player's eyes.
    private static readonly bool[] Down = new bool[4];

    private static void Downed(PayloadReader read)
    {
        var slot = (int)read.U() & 3;
        for (var i = 0; i < 6; i++) read.U();
        var down = read.U() == 1;
        if (down == Down[slot]) return;
        Down[slot] = down;
        if (down && slot == Replayer.Follow && ReplayCamera.Mode == ViewMode.Eye && Camera.main != null) Sound("PLAYER_DOWNED_1ST_PERSON", Camera.main.transform.position, true);
    }

    private static void DownedAt(WorldState state)
    {
        for (var slot = 0; slot < 4; slot++)
        {
            Down[slot] = false;
            if (state.PlayerStatus[slot] is not { } status) continue;
            var read = new PayloadReader(status);
            for (var i = 0; i < 7; i++) read.U();
            Down[slot] = read.U() == 1;
        }
    }

    // A state event repeats at the start of every keyframe block; only a change of the state is shown. Doors, generators and boxes change
    // through the game's own handler (animation and sound) when the speed tier lets the sound play; the rest is set at once and the sound
    // is posted here, because their handlers would also run game rules (a solved scan or alarm opens things, a reactor stage starts waves).
    private static void StateChanged(ushort kind, PayloadReader read)
    {
        var key = read.U();
        var value = read.U();
        var extra = read.U();
        var (x, y, z) = read.Pos();
        // A scan's progress and who stands in it, and a foam's volume, change while its state does not.
        var seen = Seen(kind, value, extra);
        var had = Last.TryGetValue((kind, key), out var before);
        if (had && before == seen) return;
        Last[(kind, key)] = seen;
        var at = new Vector3(x, y, z);
        if (Settable(kind)) Touch(kind, key, at);
        ItemHome(kind, key, read);
        switch (kind)
        {
            case EventKind.Door: Show(kind, key, at, value, extra, true); break;
            case EventKind.Generator: case EventKind.ResourceBox: case EventKind.WeakLock: case EventKind.Disinfection: Show(kind, key, at, value, extra, false); break;
            case EventKind.Bulkhead: case EventKind.HsuActivator: case EventKind.Portal: Show(kind, key, at, value, extra, true); break;
            case EventKind.GeneratorCluster: case EventKind.FogTurbine: case EventKind.ScanMove: case EventKind.Spitter: Show(kind, key, at, value, extra, false); break;
            case EventKind.Cluster:
                // Its progress moves while its state does not; its handler reveals the holopath and switches its scans, so only a change of state is set.
                if (!had || (before & 0xFF) != value) Show(kind, key, at, value, extra, true);
                break;
            case EventKind.ItemState: Put(kind, key, at, value, extra, true); break;
            case EventKind.Puzzle:
                // The game's own handler posts the alarm's start and stop sounds of the puzzle's data block.
                Put(kind, key, at, value, extra, true);
                break;
            case EventKind.Scan:
                // The game's handler plays the scan's start and loop and counts who is inside; it is set only when the scan's state changes. A player
                // stepping in or out, or the progress, changes only the extra: setting the state again for that played the scan's start sound once
                // more each time (11-05). Set as a drop-in it leaves out the finish, and the enter and exit cues it only plays for the local player.
                var scanMoved = !had || (before & 0xFF) != value;
                if (scanMoved) Put(kind, key, at, value, extra, true);
                // The finish is heard once, when the state becomes 4, not again for each later change of who stands in it.
                if (value == 4 && scanMoved) Sound("BIOSCAN_PROGRESS_COUNTER_COMPLETE", at, true);
                var followedIn = (extra >> (8 + (Replayer.Follow & 3)) & 1) == 1 && value == 3;
                if (followedIn != ScanFollowed.Contains(key))
                {
                    if (followedIn) { ScanFollowed.Add(key); Sound("BIOSCAN_ENTER", at, true); } else { ScanFollowed.Remove(key); Sound("BIOSCAN_EXIT", at, true); }
                }
                break;
            case EventKind.Mine:
                if (value == 1) Sound("STICKYMINETRIGGER", at, false);
                Deployables.Note(kind, key, value, extra, at, read, true);
                break;
            case EventKind.Sentry:
                if (value == 0) HoldSound("SENTRYGUN_DEPLOY", at);
                Deployables.Note(kind, key, value, extra, at, read, true);
                break;
            case EventKind.Foam:
                Deployables.Note(kind, key, value, extra, at, read, true);
                break;
            case EventKind.Music:
                Put(kind, key, at, value, extra, true);
                break;
            case EventKind.Reactor:
                Put(kind, key, at, value, extra, true);
                // eReactorStatus: 2 startup, 6 shutdown
                if (value == 2) Sound("REACTOR_STARTUP", at, false); else if (value == 6) Sound("REACTOR_SHUTDOWN", at, false);
                break;
            case EventKind.Environment:
                // The value is the fog data block id; the extra (light sequence section count) and the place name no light mode to set.
                EnvironmentView.Recorded((uint)value);
                break;
        }
    }

    private static readonly HashSet<ulong> ScanFollowed = new();

    // The game shows the change itself (live) when this tier would play the object's sound, else the state is set without animation.
    private static void Show(ushort kind, ulong objectKey, Vector3 at, ulong value, ulong extra, bool key)
    {
        var live = _tier == SoundTier.All || _tier == SoundTier.KeyOnly && key;
        if (Setter != null && !Put(kind, objectKey, at, value, extra, !live)) Skipped.Note("a level object has no object of its kind near its recorded place", "kind " + kind + " near " + at);
    }

    private static EnemyVoice VoiceOf(WorldState state, int entity)
    {
        if (!state.Enemies.TryGetValue(entity, out var enemy)) return EnemyVoice.Of(null);
        if (!Voices.TryGetValue(enemy.DataId, out var voice)) Voices[enemy.DataId] = voice = EnemyVoice.Of(GameData.EnemyDataBlock.GetBlock((uint)enemy.DataId)?.name);
        return voice;
    }

    // The kinds ForgeCore's WorldStates can put into a recorded state.
    private static bool Settable(ushort kind) => kind is EventKind.Music or EventKind.Door or EventKind.Generator or EventKind.ItemState or EventKind.Puzzle or EventKind.Scan or EventKind.ResourceBox or EventKind.Reactor or EventKind.Disinfection
        || LevelPiece(kind);

    // Level objects the timeline keeps as extras (WorldState.Extras) that are shown like the state events: a door's weak lock and a scan cluster's ring.
    private static bool LevelPiece(ushort kind) => kind is EventKind.WeakLock or EventKind.Cluster || Machine(kind);

    // The level machines of events 77 to 85 (bulkhead controller, HSU, portal, generator cluster, fog turbine, moving scan, spitter), also extras. A holopath is not set:
    // its scan's or cluster's handler draws and hides it, and the event only says it was revealed once, also long after its scan finished.
    private static bool Machine(ushort kind) => kind is EventKind.Bulkhead or EventKind.HsuActivator or EventKind.Portal or EventKind.GeneratorCluster or EventKind.FogTurbine or EventKind.ScanMove or EventKind.Spitter;

    // What a state event is compared by: a scan's and a cluster's progress and a foam's volume ride in the extra, and so does whether a lock is open.
    private static ulong Seen(ushort kind, ulong value, ulong extra) => kind is EventKind.Scan or EventKind.Foam or EventKind.Cluster or EventKind.WeakLock ? value | extra << 8 : Machine(kind) ? value | extra << 16 : value;

    // A pickup's state ends with the place the level had the item (the recorder writes it after the machine flag), which the setter needs to find the level's own object when the
    // recording is entered past the item's first state, and to put it back when a jump goes before it.
    private static void ItemHome(ushort kind, ulong key, PayloadReader read)
    {
        if (kind != EventKind.ItemState || Setter == null) return;
        for (var i = 0; i < 4; i++) { if (read.AtEnd) return; read.U(); }
        if (read.AtEnd) return;
        var (x, y, z) = read.Pos();
        Setter.Home(key, new Vector3(x, y, z));
    }

    private static void Touch(ushort kind, ulong key, Vector3 at)
    {
        if (!Touched.ContainsKey((kind, key)) && Setter != null && Setter.Original(kind, at, out var state, out var extra)) Originals[(kind, key)] = (state, extra);
        Touched[(kind, key)] = at;
    }
    private static bool Where(WorldState state, int entity, out Vector3 at)
    {
        var found = state.Place(entity, state.Poses[entity].TimeMs, out var x, out var y, out var z, out _, out _, out _, out _);
        at = new Vector3(x, y, z);
        return found;
    }

    private static void Sound(string name, Vector3 at, bool key)
    {
        if (_tier == SoundTier.Mute || _tier == SoundTier.KeyOnly && !key) return;
        Post(name, at);
    }

    private static void Post(string name, Vector3 at)
    {
        ReplaySounds.Post(name, at);
    }

    // A sound the game may play by itself while a state or reaction is set (a puppet's hit reaction voices the enemy's hurt for some enemies, a scan
    // set active announces itself) is held for a few frames and left out when the game has just played it: the same sound twice is a defect.
    private const int HoldFrames = 3, MatchFrames = 8;
    private static readonly List<(string Name, Vector3 At, int Due, uint Id)> HeldVoices = new();

    private static void HoldSound(string name, Vector3 at)
    {
        if (_tier == SoundTier.Mute || _tier == SoundTier.KeyOnly) return;
        if (!AudioKit.TryGameEventId(name, out var id)) return;
        HeldVoices.Add((name, at, Time.frameCount + HoldFrames, id));
    }

    /// <summary>Per frame: plays the held hurt voices that are due, unless the game played the same one meanwhile.</summary>
    internal static void Flush()
    {
        if (_sentryPending) { _sentryPending = false; SentryShow(null); }
        EnvironmentView.Frame();
        ShownPlayback.Frame();
        for (var i = HeldVoices.Count - 1; i >= 0; i--)
        {
            var held = HeldVoices[i];
            if (Time.frameCount < held.Due) continue;
            HeldVoices.RemoveAt(i);
            if (!GameVoices.PostedWithin(held.Id, MatchFrames)) Post(held.Name, held.At);
        }
    }

    // A level object before its first recorded state: as the level built it (the door state is the closest there is, the file does not say which lock it had).
    private static ulong FirstState(ushort kind) => kind switch
    {
        EventKind.Door => (ulong)eDoorStatus.Closed,
        EventKind.Generator => (ulong)ePowerGeneratorStatus.UnPowered,
        EventKind.ItemState => (ulong)ePickupItemStatus.PlacedInLevel,
        EventKind.ResourceBox => (ulong)eResourceContainerStatus.Closed,
        EventKind.Reactor => (ulong)eReactorStatus.Inactive_Idle,
        EventKind.Puzzle => (ulong)ChainedPuzzles.eChainedPuzzleStatus.Disabled,
        EventKind.Cluster => (ulong)ChainedPuzzles.eClusterStatus.Disabled,
        EventKind.Scan => (ulong)ChainedPuzzles.eBioscanStatus.Disabled,
        EventKind.Music => (ulong)MUS_State.None,
        EventKind.Disinfection => (ulong)eDisinfectionStationStatus.Awake,
        // The rest (a weak lock and the machines) read what the replay level holds (Originals) or have nothing to put back (a spitter's burst): 0.
        _ => 0UL,
    };

    /// <summary>After a jump, forward or back: every sound the replay started stops, what was last shown is forgotten and every level object the recording
    /// has touched is put into the state it has at that moment (back to how the level had it when that is before its first event), without animation; an object already
    /// in that state is left alone, except scans and clusters, which are always rebuilt (<see cref="PuzzleRebuild"/>). The
    /// sounds that last with a state (an alarm, a scan) belong to the object's own game handler, which stops and starts them with the state. Objects the setter
    /// cannot find or set are counted and logged once.</summary>
    internal static void Jumped(WorldState state)
    {
        ReplaySounds.StopAll();
        ShownPlayback.Jumped(state, _tier == SoundTier.All);
        Last.Clear(); LastHurtVoice.Clear(); HeldVoices.Clear(); Launched.Clear(); Flights.Clear();
        _sentryPending = false; Array.Fill(_shotMs, uint.MaxValue);
        DownedAt(state);
        // Spawned items first, so the pickup states below find the ones that exist at this moment (what cannot be made is noted by the setter).
        if (Setter != null) Setter.Spawns(state.ItemSpawns, state.ItemSpawns.Count);
        var environment = 0u;
        _jumping = true;
        try
        {
            foreach (var (touched, touchedAt) in Touched)
            {
                if (PuzzleRebuild.Owns(touched.Kind) || state.Objects.ContainsKey(touched) || state.Extras.ContainsKey(touched)) continue;
                var (first, firstExtra) = Originals.TryGetValue(touched, out var original) ? original : (FirstState(touched.Kind), 0UL);
                if (Holds(touched, first, firstExtra)) continue;
                // A pickup goes back where the level had it, not where the recording last had it.
                var back = touched.Kind == EventKind.ItemState && Setter?.Home(touched.Key) is { } home ? home : touchedAt;
                if (!Put(touched.Kind, touched.Key, back, first, firstExtra, true)) Unrestored(touched.Kind, touched.Key);
            }
            foreach (var ((kind, _), payload) in state.Extras)
                if (LevelPiece(kind) && !PuzzleRebuild.Owns(kind)) environment = JumpedTo(kind, payload, environment);
            foreach (var ((kind, _), payload) in state.Objects)
                if (kind != EventKind.WeaponSound && !PuzzleRebuild.Owns(kind)) environment = JumpedTo(kind, payload, environment);
            // Last: whatever the handlers above switched on (a door's own security scan) ends in what the recording says.
            foreach (var step in PuzzleRebuild.Steps(Touched.Keys, state))
            {
                if (step.Payload != null) { JumpedTo(step.Kind, step.Payload, environment); continue; }
                if (!Put(step.Kind, step.Key, Touched[(step.Kind, step.Key)], FirstState(step.Kind), 0UL, true)) Unrestored(step.Kind, step.Key);
                Applied.Remove((step.Kind, step.Key));
            }
        }
        finally { _jumping = false; }
        EnvironmentView.Recorded(environment);
        Terminals.Jumped(state);
        ScanFollowed.Clear();
        Deployables.Restore(state);
    }

    private const string NotRestored = "level objects could not be put into their recorded state after a jump";

    // One object of a jump that was not found: reported per object, so a later jump that does find it takes it back out of the count.
    private static void Unrestored(ushort kind, ulong key) => Skipped.Note(NotRestored, "kind " + kind + " key " + key);

    // A pickup put down is also somewhere: the same state at another place is not held, so the setter looks at where it lies.
    private static bool Holds((ushort Kind, ulong Key) id, ulong state, ulong extra) => !(id.Kind == EventKind.ItemState && (ePickupItemStatus)state == ePickupItemStatus.PlacedInLevel) && Applied.TryGetValue(id, out var had) && had == (state, extra);

    // A jump presents nothing of the time it skips: a door, lock or box the jump changes is set at once, and a sound its game handler posts
    // for it all the same is stopped (alarms and scans keep theirs: they last with the state and their handlers stop them with it).
    private static bool _jumping;
    private static readonly List<uint> Caught = new();

    /// <summary>Puts a level object into a state through the setter and remembers it (<see cref="Applied"/>); false when it was not found.</summary>
    private static bool Put(ushort kind, ulong key, Vector3 at, ulong value, ulong extra, bool instant)
    {
        if (Setter == null) return false;
        // A spitter's burst has no drop-in form: it is also muted when the speed tier would not play its sound.
        var silent = _jumping && kind is EventKind.Door or EventKind.WeakLock or EventKind.ResourceBox or EventKind.Bulkhead or EventKind.HsuActivator or EventKind.Portal || instant && kind == EventKind.Spitter;
        if (silent) GameVoices.Catch(Caught);
        bool done;
        try { done = Setter.Set(kind, key, at, value, extra, instant); }
        finally { if (silent) GameVoices.Catch(null); }
        if (silent)
        {
            foreach (var id in Caught) AudioKit.StopGameEvent(id);
            Caught.Clear();
        }
        if (done) { Applied[(kind, key)] = (value, extra); Skipped.Resolve(NotRestored, "kind " + kind + " key " + key); }
        return done;
    }

    // One state at the moment jumped to, set at once; returns the fog the moment has (the environment's state) or the one given.
    private static uint JumpedTo(ushort kind, byte[] payload, uint environment)
    {
        var read = new PayloadReader(payload);
        var key = read.U();
        var value = read.U();
        var extra = read.U();
        var (x, y, z) = read.Pos();
        // Kept as the state events compare it, so the next event of an unchanged state does not count as a change.
        Last[(kind, key)] = Seen(kind, value, extra);
        if (kind == EventKind.Environment) return (uint)value;
        if (!Settable(kind)) return environment;
        var at = new Vector3(x, y, z);
        Touch(kind, key, at);
        ItemHome(kind, key, read);
        if (Holds((kind, key), value, extra)) return environment;
        if (!Put(kind, key, at, value, extra, true)) Unrestored(kind, key);
        return environment;
    }

    internal static void Reset()
    {
        ReplaySounds.StopAll();
        ShownPlayback.Reset();
        Last.Clear(); LastHurtVoice.Clear(); HeldVoices.Clear(); Touched.Clear(); Originals.Clear(); Applied.Clear(); Caught.Clear(); _jumping = false; Launched.Clear(); Flights.Clear(); _sentryPending = false; Array.Fill(_shotMs, uint.MaxValue);
        Array.Clear(Down);
        ScanFollowed.Clear();
        Deployables.Clear();
        EnvironmentView.Reset();
        Terminals.Reset();
    }
}
