using System;
using System.Collections.Generic;
using Enemies;
using ForgeUI;
using ForgeVisual;
using ForgeOrdnance;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>This player's side of a suit: the boarding prompt (close behind the suit), the black as the suit stands open on boarding
/// (<see cref="FrameSequence.Black"/>: the pilot is put on the seat under it), the camera riding the head of the pilot's leaving motion
/// (<see cref="PilotBody"/>) with the flashlight carried along (the black clears once the pilot has landed outside and the camera has
/// eased to the player's own eye, <see cref="FrameBootScreen.DownClearStart"/>), the raised
/// camera, the pilot's controls (special, rockets, leaving), sprint heat and the speed and jump of the movement hooks, the suit gun loan
/// (<see cref="MechGun"/>), the first-person suit shell (ForgeVisual's <see cref="FirstPersonWear"/>) and the HUD with its boot sequence. Everything boarding and
/// leaving show is created ahead of time (<see cref="FramePrewarm"/>, the shells of the picked and called-in models), so they only switch it on and off,
/// and the plates, the black, the power-on, the leaving motion and the power-down all run on one clock, the state's start as the suit's visual saw it:
/// a stalled frame skips ahead, it never stretches the sequence into slow motion. Follows the
/// record the host sends: boarding starts when the record names this player as pilot, and everything is released the moment it
/// does not. Every native action stays available while worn (weapons, tools, packs, jump, crouch, sprint, use, ladders); only
/// boarding and leaving hold the movement keys still (the pilot is put on the seat, and the leaving motion moves the body). No camera shake is ever added.</summary>
internal static class LocalPilot
{
    // This package's frame work, named in ForgeCore's slow-frame line.

    private const float BoardHoldSeconds = 1.0f;
    /// <summary>Within this distance of an idle suit (and looking at it) the prompt shows; boarding itself needs <see cref="FrameRules.BoardDistance"/> behind it.</summary>
    private const float HintDistance = 3.0f;
    internal const float ExitHoldSeconds = 0.8f;
    private const float AmmoReportInterval = 0.5f;
    private const float BreachInterval = 0.1f;
    private const float BreachRehit = 0.4f;
    private static float _lastBreachAt;
    private static Vector3 _lastBreachPos;
    private static readonly Dictionary<IntPtr, float> DoorHitAt = new();

    private static readonly InputAction[] Walking = { InputAction.MoveHorizontal, InputAction.MoveVertical };
    /// <summary>While boarding or leaving, the movement keys are held from the game (ForgeCore releases them too at level cleanup,
    /// death and leaving): the pilot motion moves the body.</summary>
    private static ForgeCore.InputLease? _input;

    private static void HoldWalking()
    {
        if (_input is not { IsHeld: true }) _input = ForgeCore.InputLease.Take(Plugin.Id);
        _input.Block(Walking);
    }

    private static void ReleaseInput()
    {
        _input?.Dispose();
        _input = null;
    }

    /// <summary>A charge started standing still goes where the pilot looks, so it is never wasted on the spot (any movement key takes over).</summary>
    private static ForgeCore.InputLease? _chargeInput;
    /// <summary>A planted suit stands on the ground: the jump key is held from the game.</summary>
    private static ForgeCore.InputLease? _plantInput;
    private static readonly InputAction[] Jumping = { InputAction.Jump };

    private static void TickSkillInput(FrameRecord r, float now)
    {
        var steer = r.Boosting(now) && Math.Abs(ForgeCore.InputLease.Axis(InputAction.MoveHorizontal)) < 0.1f && Math.Abs(ForgeCore.InputLease.Axis(InputAction.MoveVertical)) < 0.1f;
        if (steer && _chargeInput is not { IsHeld: true })
        {
            _chargeInput = ForgeCore.InputLease.Take(Plugin.Id);
            _chargeInput.Block(Walking);
            _chargeInput.Steer(0f, 1f);
        }
        else if (!steer && _chargeInput != null) ReleaseCharge();
        var planted = FrameRules.Planted(r);
        if (planted && _plantInput is not { IsHeld: true })
        {
            _plantInput = ForgeCore.InputLease.Take(Plugin.Id);
            _plantInput.Block(Jumping);
        }
        else if (!planted && _plantInput != null) ReleasePlant();
    }

    private static void ReleaseCharge()
    {
        _chargeInput?.Dispose();
        _chargeInput = null;
    }

    private static void ReleasePlant()
    {
        _plantInput?.Dispose();
        _plantInput = null;
    }

    private static FrameUnit? _unit;
    private static FrameState _phase = (FrameState)255;
    private static readonly FrameHud Hud = new();

    /// <summary>Registers the suit panel with ForgeUI (once, at plugin load).</summary>
    internal static void RegisterHud() => Hud.Register();
    /// <summary>The suit's words in the player's language.</summary>
    internal static FrameWords Words => Hud.Words;
    private static int _bootTone = FrameBootScreen.Groups;
    // The power-on clock (seconds since it started at the boarding's black, on the game clock) and whether it runs; whether the ready voice
    // played; the power-down clock (seconds into leaving) and whether it went dark; whether the shell is worn.
    private static float _boot, _bootStart, _down;
    private static readonly BootPlan Plan = BootPlan.Default;
    private static bool _poweredOn, _dressed, _voiced, _dark;
    private static bool _wasSprintBonus;
    private static readonly SprintHeat Heat = new();
    // The first-person shells, one per model, built during the elevator ride or a call-in's countdown (null when that build failed:
    // not retried for the same model), and the one the worn suit uses.
    private static readonly Dictionary<ulong, FirstPersonWear?> Shells = new();
    private static FirstPersonWear? _shell;
    private static FrameUnit? _shellFor;
    private static int _reportedMagazine = -1, _reportedReserve = -1;
    private static float _rocketsReadyAt;
    private static float _ammoReportAt, _exitHeld, _boardHeld, _phaseStart, _nextScan, _lastScan = -99f, _nextBreach;
    private static bool _wasBoosting, _exitSent, _boardSent, _sprinting, _sprintBonus;
    /// <summary>The boarding prompt, through ForgeUI's claim on the game's interaction prompt; its line is rebuilt only when the key or language changes.</summary>
    private static readonly InteractPrompt? BoardPrompt = UIKit.Mod(Plugin.Id).AddInteractPrompt("Board");
    private static string _promptKey = "", _promptLine = "";
    private static int _promptArmor = -1, _promptAmmo = -1;
    private static FrameWords? _promptWords;
    private static FrameRecord? _prewarmSample;
    private static FrameUnit? _boardTarget;
    // Camera: the lease on the view (ForgeCore.CameraLease) and the cockpit lift it is asked for, added on top of the game's own camera offset.
    private static ForgeCore.CameraLease? _view;
    private static float _cockpit;
    // Boarding: whether the black has come (the pilot is inside). Leaving: whether the leaving motion has started (the camera
    // rides the head), whether the pilot was put at the exit spot, the spot, and the hand-back of the camera to the eye after the hop.
    // The near clip plane is lowered while the camera rides the head.
    private static bool _inside, _out, _placed, _handedBack, _exitFound;
    // The camera's hand-back after the landing runs on past the suit letting go of the pilot.
    private static bool _handing;
    /// <summary>The idle pass ran with nothing to do (<see cref="Quiet"/>): later frames return at once until a suit, a swap or a prewarm appears.</summary>
    private static bool _settled;
    private static Vector3 _exitSpot, _handFrom;
    private static Quaternion _handFromLook = Quaternion.identity;
    private static float _handBackAt;
    private static readonly HashSet<IntPtr> Breached = new();
    /// <summary>The charge's query: weak doors and enemies ahead, on every layer (enemy-only queries are ForgeCore's).</summary>
    private static readonly ForgeCore.ColliderQuery Hits = new(64, grow: true);

    internal static FrameUnit? Unit => _unit;

    /// <summary>Boarding, and the black has come (the camera is on the cockpit eye): the suit stops drawing for its pilot.</summary>
    internal static bool Inside => _unit != null && _inside;

    internal static bool CarriesHeavy => _unit != null && _unit.Record.Spec.CarryHeavy;

    internal static bool Piloting => _unit != null && _phase == FrameState.Piloted;

    /// <summary>The movement hook: the game's velocity for this fixed step, scaled by the worn suit and nothing else, so it starts
    /// and stops exactly as the player on foot does. Walk multiplier (boost included, zero while planted) and the sprint bonus
    /// while the heat allows it (an overheated suit's sprint is no faster than its walk). During the walk in and out the game's
    /// own walk is left as it is.</summary>
    internal static Vector3 Velocity(PlayerAgent owner, Vector3 velocity)
    {
        var unit = _unit;
        if (unit == null) return velocity;
        if (_phase != FrameState.Piloted) return velocity;
        var r = unit.Record;
        var m = r.Spec.Movement;
        var scale = FrameRules.SpeedMultiplier(r, Time.time);
        if (Time.time < _hitchUntil) scale *= HitchSpeed;
        if (_sprinting)
        {
            if (_sprintBonus) scale *= m.Sprint;
            else
            {
                var data = owner.PlayerData;
                if (data != null && data.runMoveSpeed > 0f) scale *= data.walkMoveSpeed / data.runMoveSpeed;
            }
        }
        return velocity * scale;
    }

    /// <summary>The jump hooks: how much faster the worn suit leaves the ground than the game's own jump (1 when this is not the
    /// local pilot). Height grows with the square of the take-off speed, so the suit's <see cref="MovementSpec.Jump"/> height
    /// multiplier becomes its square root here.</summary>
    internal static float JumpSpeed(PlayerAgent? owner)
    {
        var unit = _unit;
        if (unit == null || _phase != FrameState.Piloted || owner == null || !owner.IsLocallyOwned) return 1f;
        return MathF.Sqrt(unit.Record.Spec.Movement.Jump);
    }

    /// <summary>True while <paramref name="owner"/> is the local player and a suit is worn (any phase, walk in and out included): the
    /// stamina hooks skip the game's stamina then (a suit has the sprint heat instead).</summary>
    internal static bool NoStamina(PlayerAgent? owner) => _unit != null && owner != null && !owner.WasCollected && owner.IsLocallyOwned;

    /// <summary>Stamina stays full while a suit is worn, so the game's stamina speed penalty never applies; after leaving it is full.</summary>
    private static void HoldStamina(PlayerAgent agent)
    {
        var stamina = agent.Stamina;
        if (stamina != null && !stamina.WasCollected && stamina.m_currentStamina < PlayerStamina.MaxStamina) stamina.m_currentStamina = PlayerStamina.MaxStamina;
    }

    /// <summary>No suit exists and nothing is left to prepare for one (no model known, or the prewarm and the elevator ride's shells are done).</summary>
    private static bool Quiet => FrameWorld.All.Count == 0 && (!FrameWorld.HasSpecs || FramePrewarm.Done && !ElevatorRide.ElevatorRideInProgress);

    internal static void Tick(float now)
    {
        // No suit exists or is coming, nothing is worn, handed back or lent: the last idle pass left the cockpit off and the plates empty.
        if (_settled && _unit == null && !_handing && !ForgeCore.CarrySlot.Pending && Quiet && MechGun.Idle) return;
        _settled = false;
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected || SNet.LocalPlayer == null)
        {
            if (_unit != null) Release("local player gone");
            return;
        }
        MechGun.Tick(now);
        ForgeCore.CarrySlot.Settle(now);
        var me = SNet.LocalPlayer.Lookup;
        Hud.WorldMarkers();
        if (_unit == null)
        {
            var mine = FrameWorld.PilotUnit(me);
            if (mine != null) Begin(mine, agent, now);
            else
            {
                Hud.Sync(CockpitPhase.Off);
                TickBoarding(agent, now);
                _settled = !_handing && !ForgeCore.CarrySlot.Pending && Quiet && MechGun.Idle;
                return;
            }
        }
        var unit = _unit!;
        var r = unit.Record;
        HoldStamina(agent);
        if (!r.HasPilot || r.Pilot != me || !Contains(unit))
        {
            // The host ends the leaving on the landing's own moment (Idle, no pilot), ahead of this frame's Leave: land first, but only
            // for a leaving that ran to its end (not one the game cut off: a grab, a warp, a down pilot).
            if (_phase == FrameState.Exiting && _out && !_placed && r.State == FrameState.Idle && !r.HasPilot && !FrameWorld.IsDown(agent) && !FrameWorld.IsSeized(agent)
                && (agent.Position - new Vector3(r.X, r.Y, r.Z)).sqrMagnitude <= FrameRules.TetherReach * FrameRules.TetherReach) Land(unit, agent, now);
            Release("the host released the suit (" + r.State + ")", _handing);
            return;
        }
        if (r.State != _phase)
        {
            Phase(unit, agent, now);
        }
        var elapsed = now - _phaseStart;
        var taken = Taken(r, agent);
        switch (_phase)
        {
            case FrameState.Entering:
                // The player stays where they stand while the suit opens and the screen fades out; at the black the pilot is on the seat and
                // the power-on starts. The plates closing play on for everyone else, on the same state clock.
                if (!_inside && !taken && elapsed >= FrameSequence.Black) GetInside(unit, agent, now);
                if (_poweredOn || _inside) PowerOn(unit, agent, now);
                else Hud.Entering(FrameSequence.EnterFade(elapsed));
                // The last block starts to come on, on the state's clock: the movement opens on this very frame, without waiting for the
                // host's state message (which only confirms it); the last blocks, the seams and the NEURAL LINK message play on while the
                // pilot walks.
                if (_input != null && FrameSequence.ControlsOpen(elapsed))
                {
                    ReleaseInput();
                }
                break;
            case FrameState.Exiting:
                _down = elapsed;
                Hud.Leaving(_down);
                if (!_dark && _down >= FrameBootScreen.DownDark) { _dark = true; SyncHud(agent); }
                if (!taken) Leave(unit, agent, elapsed, now);
                break;
            case FrameState.Piloted:
                Ride(unit, agent, now);
                PowerOn(unit, agent, now);
                break;
        }
        // Every frame, not only where the state moves: the cockpit follows what this player is now, so a missed or merged record,
        // a release by ForgeUI or a pilot briefly not alive is corrected on the next frame.
        SyncHud(agent);
    }

    /// <summary>The cockpit's phase from this player's state now (<see cref="CockpitScope.For"/>). ForgeUI holds the game's HUD only in
    /// a level with a living local player; without that the cockpit waits (<see cref="CockpitPhase.Suspended"/>).</summary>
    private static void SyncHud(PlayerAgent agent)
    {
        var holds = !agent.WasCollected && agent.Alive && GameStateManager.CurrentStateName == eGameStateName.InLevel;
        Hud.Sync(CockpitScope.For(_unit != null, _phase, _inside, _dark, holds));
    }

    /// <summary>The power-on: starts at the boarding's black (the shell is put on under it), then runs on the game clock from that moment (a
    /// stalled frame skips ahead), with its tones, and draws the HUD under it. It runs on after the boarding ended (the pilot already moves):
    /// the message locks and folds while the pilot walks.</summary>
    private static void PowerOn(FrameUnit unit, PlayerAgent agent, float now)
    {
        var r = unit.Record;
        if (!_poweredOn)
        {
            _poweredOn = true;
            // The power-on's clock is the state's clock from the black, so the last block starts to come on exactly when the controls
            // open (a late frame or a held pilot never shifts it).
            _bootStart = _phase == FrameState.Entering ? _phaseStart + FrameSequence.Black : now;
            _bootTone = 0;
            _voiced = false;
            Dress(unit, agent);
        }
        _boot = Math.Min(now - _bootStart, Plan.Settled + 1f);
        BootSounds(unit, _boot);
        Hud.Update(r, MechGun.Reading, Heat.Value, Heat.Overheated, now, _lastScan, Math.Max(_rocketsReadyAt, r.RocketsReadyAt), _boot);
    }

    /// <summary>One rising tone as each block comes online, and the ready voice with NEURAL LINK OK, on the power-on's own clock (a power-on
    /// this pilot did not see start stays silent).</summary>
    private static void BootSounds(FrameUnit unit, float boot)
    {
        if (!_voiced && boot >= Plan.LinkSteady)
        {
            _voiced = true;
            unit.Visual?.Sound(FrameSound.VoiceReady);
        }
        if (boot >= Plan.Assembled) { _bootTone = FrameBootScreen.Groups; return; }
        while (_bootTone < FrameBootScreen.Groups && boot >= Plan.ToneAt(_bootTone))
            FrameAudio.Local(FrameSound.HudSection, FrameAudio.Section(_bootTone++));
    }

    // ---- the first-person shell ---------------------------------------------------------------------------------

    /// <summary>The shell of <paramref name="spec"/>, built now unless it is built already (or its build failed: logged once, not retried);
    /// one that no longer exists (the player's first-person body was replaced) is built again.</summary>
    private static FirstPersonWear? EnsureShell(FrameSpec spec, PlayerAgent agent)
    {
        // The art is registered when the level has been built, after the elevator ride starts: before that nothing can be spawned,
        // and a missing art bundle is not a failed build to remember.
        if (FrameAssets.Bundle == null) return null;
        var key = FrameSpecKey.Of(spec);
        if (Shells.TryGetValue(key, out var shell) && (shell == null || shell.Alive)) return shell;
        shell?.Destroy();
        shell = BuildShell(spec, agent);
        Shells[key] = shell;
        return shell;
    }

    /// <summary>The shell of <paramref name="spec"/>: the suit model's lower body (<see cref="FrameSpec.Shell"/>, mapped by
    /// <see cref="FrameBodyRig.ShellBones"/>) worn on the game's own first-person body through ForgeVisual, the opening parts shut on it.
    /// Null (logged) when it cannot be built; the game's own body then stays.</summary>
    private static FirstPersonWear? BuildShell(FrameSpec spec, PlayerAgent agent)
    {
        if (spec.Shell.Length == 0) return null;
        if (Models.Get(spec.Model) is not { } look)
        {
            Plugin.Logger.ErrorOnce("Exosuit first-person shell: ForgeVisual has no model " + spec.Model + "; the game's own body stays.");
            return null;
        }
        return Models.WearFirstPerson(agent, look, new FirstPersonWearOptions
        {
            Bones = FrameBodyRig.ShellBones(spec.Shell),
            FollowParent = FrameBodyRig.HingedParts(spec.Opening),
            Label = spec.Model,
        });
    }

    /// <summary>The shell of the suit this player wears, lit in that suit's colour.</summary>
    private static void UseShell(FrameUnit unit, PlayerAgent agent)
    {
        _shell = EnsureShell(unit.Record.Spec, agent);
        if (unit.Visual != null) _shell?.SetColor(FrameVisual.EmissiveColor, unit.Visual.GlowColour);
        _shellFor = unit;
    }

    /// <summary>A shell costs about a quarter of a second to build, so none is built while a suit is being boarded. During the elevator ride
    /// (the player cannot move yet) the shells of the models the players picked in the lobby are built, one per frame (none when nobody
    /// picked a suit); a model nobody picked is built when a suit of it is called in (<see cref="Called"/>), before it lands.</summary>
    private static void BuildShellsInElevator(PlayerAgent agent)
    {
        if (!ElevatorRide.ElevatorRideInProgress || FrameAssets.Bundle == null) return;
        OrdnanceApi.PickedParameters(FrameSpecParser.TypeName, Picked);
        for (var i = 0; i < Picked.Count; i++)
        {
            if (Picked[i] is not FrameSpec spec || spec.Shell.Length == 0 || Built(spec)) continue;
            BuildAhead(spec, agent);
            return;
        }
    }

    /// <summary>A suit placed in the level (neither picked in the lobby nor called in) gets its shell as soon as the level has it, one per
    /// frame, so boarding it for the first time builds nothing.</summary>
    private static void BuildShellsOfPlacedSuits(PlayerAgent agent)
    {
        if (FrameAssets.Bundle == null) return;
        foreach (var unit in FrameWorld.All)
        {
            var spec = unit.Record.Spec;
            if (spec.Shell.Length == 0 || Built(spec)) continue;
            BuildAhead(spec, agent);
            return;
        }
    }

    private static readonly List<object> Picked = new(8);

    private static bool Built(FrameSpec spec) => Shells.TryGetValue(FrameSpecKey.Of(spec), out var built) && (built == null || built.Alive);

    private static void BuildAhead(FrameSpec spec, PlayerAgent agent)
    {
        var body = agent.FPItemHolder == null ? null : agent.FPItemHolder.FPSArms;
        if (body == null || body.WasCollected) return;
        EnsureShell(spec, agent);
    }

    /// <summary>A suit of <paramref name="spec"/> was called in (its beacon landed, the drop is counting down): its shell is built now,
    /// long before the suit lands, unless the elevator ride built it.</summary>
    internal static void Called(FrameSpec spec)
    {
        if (spec.Shell.Length == 0 || Built(spec)) return;
        var agent = ForgeCore.Players.Local;
        if (agent != null && !agent.WasCollected) BuildAhead(spec, agent);
    }

    /// <summary>The suit's lights changed colour (<see cref="FrameVisual"/>): the first-person shell's lights follow.</summary>
    internal static void Glow(FrameUnit unit, Color colour)
    {
        if (_shellFor == unit) _shell?.SetColor(FrameVisual.EmissiveColor, colour);
    }

    /// <summary>Puts the shell on (built here only when nothing built it ahead, for example a late joiner's).</summary>
    private static void Dress(FrameUnit unit, PlayerAgent agent)
    {
        UseShell(unit, agent);
        _shell?.Activate();
        _dressed = true;
    }

    private static void Undress()
    {
        _dressed = false;
        _shell?.Deactivate();
    }

    /// <summary>The level ended or the player is gone: the shell and what was built ahead are dropped.</summary>
    private static void DropShell()
    {
        _dressed = false;
        foreach (var shell in Shells.Values) shell?.Destroy();
        Shells.Clear();
        _shell = null;
        _shellFor = null;
    }

    internal static void LateTick(float now)
    {
        if (_unit == null) { HandBack(now); return; }
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected) return;
        _shell?.Follow();
        var camera = agent.FPSCamera;
        if (_view == null || camera == null || camera.WasCollected) return;
        var r = _unit.Record;
        var body = _unit.Visual?.Body;
        if (_phase is FrameState.Entering or FrameState.Exiting && Taken(r, agent))
        {
            // The game took the pilot: its own view, at its own height, until the host frees the suit.
            body?.FirstPerson(false);
            _view.NearClip(null);
            _view.Lift(0f);
            return;
        }
        // Boarding until the black: the player's own view, at player height.
        if (_phase == FrameState.Entering && !_inside) { _view.Lift(0f); return; }
        // The leaving motion: the camera rides the pilot's head (the black covers it until the head has pulled out of the shell). The
        // flashlight goes with the camera.
        if (_phase == FrameState.Exiting && _out && !_handedBack)
        {
            _view.Lift(0f);
            if (body != null)
            {
                body.FirstPerson(true, camera.m_camera);
                if (body.HasEye)
                {
                    _view.NearClip(FrameSequence.NearClip);
                    _view.Look(body.Eye, body.EyeRotation);
                    _handFrom = body.Eye;
                    _handFromLook = body.EyeRotation;
                }
            }
            return;
        }
        if (_phase == FrameState.Exiting && _handedBack)
        {
            _view.Lift(0f);
            HandBack(now);
            return;
        }
        var lift = _phase is FrameState.Entering or FrameState.Piloted or FrameState.Exiting ? _cockpit : 0f;
        _view.Lift(lift);
    }


    /// <summary>The plates stand open and the screen is black (<see cref="FrameSequence.Black"/>): the player is put on the seat facing the
    /// suit's forward, the power-on starting in the same frame. Everyone else sees the pilot gone and the plates closing on the state's clock.</summary>
    private static void GetInside(FrameUnit unit, PlayerAgent agent, float now)
    {
        _inside = true;
        var r = unit.Record;
        Hud.Entering(1f);
        SyncHud(agent);
        FrameSequence.Ahead(r.X, r.Z, r.Yaw, FrameSequence.CockpitBack, out var x, out var z);
        ForgeCore.Players.Warp(agent, agent.DimensionIndex, new Vector3(x, r.Y, z), Quaternion.Euler(0f, r.Yaw, 0f) * Vector3.forward, ForgeCore.WarpMode.Local);
    }

    /// <summary>Leaving, on the state's clock: once the plates stand open the shell comes off and the camera rides the head out of the suit,
    /// still under the black, which stays until the landing and the camera's hand-back are done (<see cref="FrameBootScreen.DownClearStart"/>),
    /// so the inside of the suit is never shown; at the
    /// landing the pilot is put at the first exit spot the game's navigation and the level's geometry allow (<see cref="ForgeCore.Level.FindStandSpot"/>, straight
    /// behind the suit where the motion lands when it is free), facing the suit's forward, and from that frame the pilot moves: the movement
    /// keys are given back and the camera eases from the head to the player's own eye (<see cref="HandBack"/>) while the suit closes on its
    /// own. The host ends the leaving on the same state clock (<see cref="FrameSequence.ExitSeconds"/>).</summary>
    private static void Leave(FrameUnit unit, PlayerAgent agent, float elapsed, float now)
    {
        var r = unit.Record;
        if (!_out && elapsed >= FrameSequence.ExitClipStart)
        {
            _out = true;
            if (_dressed) Undress();
            _exitFound = ForgeCore.Level.FindStandSpot(new Vector3(r.X, r.Y, r.Z), r.Yaw, FrameSequence.ExitClearance, true, out _exitSpot, out _);
        }
        if (_out && !_placed && elapsed >= FrameSequence.ExitClipStart + PilotMotion.Land) Land(unit, agent, now);
    }

    /// <summary>The landing: the pilot is put at the exit spot facing the suit's forward, the controls are given back and the camera starts to ease to the player's eye.</summary>
    private static void Land(FrameUnit unit, PlayerAgent agent, float now)
    {
        _placed = true;
        var look = Quaternion.Euler(0f, unit.Record.Yaw, 0f) * Vector3.forward;
        if (_exitFound) ForgeCore.Players.Warp(agent, agent.DimensionIndex, _exitSpot, look, ForgeCore.WarpMode.Local);
        else if (agent.FPSCamera is { } camera && !camera.WasCollected) camera.SetLookDirection(look);
        ReleaseInput();
        _handedBack = _handing = true;
        _handBackAt = now;
        unit.Visual?.Body?.FirstPerson(false);
        _view?.NearClip(null);
    }

    /// <summary>After the landing: the camera eases from where the head left it to the player's own eye over
    /// <see cref="FrameSequence.HandBackSeconds"/>, on after the suit let go of the pilot, while the player already moves.</summary>
    private static void HandBack(float now)
    {
        if (!_handing) return;
        var k = FrameSequence.Smooth((now - _handBackAt) / FrameSequence.HandBackSeconds);
        var agent = ForgeCore.Players.Local;
        var camera = agent == null || agent.WasCollected ? null : agent.FPSCamera;
        if (k >= 1f || camera == null || camera.WasCollected)
        {
            _handing = false;
            if (_unit == null) EndView();
            return;
        }
        var view = camera.transform;
        _view?.Look(Vector3.Lerp(_handFrom, view.position, k), Quaternion.Slerp(_handFromLook, view.rotation, k));
    }

    /// <summary>Boarding or leaving, whether the game took this pilot (<see cref="FrameRules.Interrupted"/>: held, or put away from the suit
    /// by a warp or a dimension change). The host then ends the sequence with the suit empty where it stands; until its record arrives the
    /// camera stays with the player and the pilot is never put down beside the suit (which would pull a warped pilot back across the level or
    /// out of a dimension). Logged once per sequence.</summary>
    private static bool Taken(FrameRecord r, PlayerAgent agent)
    {
        var feet = agent.Position;
        var seized = FrameWorld.IsSeized(agent);
        return FrameRules.Interrupted(r, seized, feet.x, feet.y, feet.z);
    }



    // ---- boarding ---------------------------------------------------------------------------------------------

    private static void TickBoarding(PlayerAgent agent, float now)
    {
        Prepare(agent, now);
        FrameUnit? target = null;
        var behind = true;
        if (FocusStateManager.CurrentState == eFocusState.FPS && !FrameWorld.IsDown(agent) && !FrameWorld.IsSeized(agent))
        {
            var camera = agent.FPSCamera;
            var feet = agent.Position;
            var look = camera != null ? camera.transform.forward : agent.Rotation * Vector3.forward;
            look.y = 0f;
            var best = HintDistance;
            var all = FrameWorld.All;
            for (var i = 0; i < all.Count; i++)
            {
                var r = all[i].Record;
                if (r.State != FrameState.Idle || all[i].Visual == null) continue;
                var to = new Vector3(r.X - feet.x, 0f, r.Z - feet.z);
                var distance = to.magnitude;
                if (distance > best || Math.Abs(r.Y - feet.y) > 1.5f) continue;
                if (distance > 0.5f && Vector3.Dot(to / distance, look.normalized) < 0.3f) continue;
                best = distance;
                target = all[i];
                // The suit is boarded through its back: close behind it.
                var facing = Quaternion.Euler(0f, r.Yaw, 0f) * Vector3.forward;
                behind = distance >= 0.3f && distance <= FrameRules.BoardDistance && Vector3.Dot(facing, -to / distance) < -0.5f;
            }
        }
        if (target != _boardTarget) { ResetBoardHold(); _boardSent = false; _boardTarget = target; }
        if (target == null) { ClearPrompt(); return; }
        var words = Hud.Words;
        var tr = target.Record;
        if (!behind)
        {
            BoardPrompt?.Show(words.BoardHeader, words.BoardBehind);
            BoardPrompt?.Progress(-1f);
            FrameSequence.Ahead(tr.X, tr.Z, tr.Yaw, -FrameRules.BoardSpot, out var ex, out var ez);
            Hud.Around(new Vector3(ex, tr.Y, ez));
            ResetBoardHold();
            return;
        }
        Hud.Around(null);
        var key = UIKitKeys.Binding(InputAction.Use, "E");
        var armor = (int)Math.Round(100f * Math.Clamp(tr.Armor / Math.Max(1f, tr.Spec.Armor), 0f, 1f));
        var ammo = (int)Math.Round(100f * Math.Clamp((tr.Magazine + tr.Reserve) / (float)Math.Max(1, tr.Spec.Weapon.Magazine + tr.Spec.Weapon.Reserve), 0f, 1f));
        if (key != _promptKey || armor != _promptArmor || ammo != _promptAmmo || !ReferenceEquals(words, _promptWords))
        {
            _promptKey = key;
            _promptArmor = armor;
            _promptAmmo = ammo;
            _promptWords = words;
            _promptLine = FrameWords.Format(words.BoardHold, key) + "   " + FrameWords.Format(words.BoardArmor, armor) + "   " + FrameWords.Format(words.BoardAmmo, ammo);
        }
        BoardPrompt?.Show(words.BoardHeader, _promptLine);
        if (InputMapper.GetButton != null && InputMapper.GetButton.Invoke(InputAction.Use, eFocusState.FPS))
        {
            _boardHeld += Time.deltaTime;
            BoardPrompt?.Progress(_boardHeld / BoardHoldSeconds);
            if (_boardHeld >= BoardHoldSeconds && !_boardSent)
            {
                _boardSent = true;
                HoldSound.Complete();
                FrameWorld.Request(target, FrameRequestKind.Enter);
            }
        }
        else
        {
            ResetBoardHold();
            _boardSent = false;
            BoardPrompt?.Progress(-1f);
        }
    }

    /// <summary>While no suit is worn: the first-person shells of the picked models during the elevator ride
    /// (<see cref="BuildShellsInElevator"/>), of the suits placed in the level (<see cref="BuildShellsOfPlacedSuits"/>) and the prewarm (a step per frame, from the start of the level, so its one-time costs never
    /// meet a suit's landing), so neither a landing nor a boarding builds anything.</summary>
    private static void Prepare(PlayerAgent agent, float now)
    {
        BuildShellsInElevator(agent);
        BuildShellsOfPlacedSuits(agent);
        if (FramePrewarm.Done) return;
        var spec = FrameWorld.AnySpec;
        if (spec == null) return;
        if (_prewarmSample == null || !ReferenceEquals(_prewarmSample.Spec, spec)) _prewarmSample = new FrameRecord(0, 0, spec);
        FramePrewarm.Tick(Hud, _prewarmSample, now);
    }

    /// <summary>The boarding key was let go, the target changed or the player stepped out of position: a hold that had not
    /// completed sounds its cancel, a completed one just ends.</summary>
    private static void ResetBoardHold()
    {
        if (_boardHeld > 0f && !_boardSent) HoldSound.Cancel();
        _boardHeld = 0f;
    }

    private static void ClearPrompt()
    {
        BoardPrompt?.Hide();
        Hud.Around(null);
    }

    // ---- worn -------------------------------------------------------------------------------------------------

    private static void Begin(FrameUnit unit, PlayerAgent agent, float now)
    {
        PilotLazyHooks.Hooks.Ensure();
        ClearPrompt();
        _unit = unit;
        _phase = (FrameState)255;
        var spec = unit.Record.Spec;
        _reportedMagazine = unit.Record.Magazine;
        _reportedReserve = unit.Record.Reserve;
        _wasBoosting = unit.Record.Boosting(now);
        _rocketsReadyAt = 0f;
        _nextScan = now + 1f;
        _lastScan = -99f;
        _exitHeld = 0f;
        _poweredOn = false;
        _boot = _bootStart = _down = 0f;
        _dark = false;
        _bootTone = FrameBootScreen.Groups;
        _wasSprintBonus = false;
        Heat.Reset();
        Hud.Board(unit.Record, Plan);
        var camera = agent.FPSCamera;
        if (camera != null && !camera.WasCollected)
        {
            var eye = camera.transform.position.y - agent.Position.y;
            _cockpit = Math.Max(0f, spec.EyeHeight - eye);
            _view?.Dispose();
            _view = ForgeCore.CameraLease.Take(Plugin.Id);
        }
        else Plugin.Logger.ErrorOnce("Exosuit boarding: the local camera is not available; the view stays at player height.");
        _inside = _out = _placed = _handedBack = _handing = false;
    }

    private static void Phase(FrameUnit unit, PlayerAgent agent, float now)
    {
        var r = unit.Record;
        _phase = r.State;
        // The suit's visual changed state this same frame (it ticks first) and holds the state's start: one clock for plates, motion and screen.
        _phaseStart = unit.Visual?.StateStart ?? now;
        _exitHeld = 0f;
        _exitSent = false;
        ReleaseCharge();
        ReleasePlant();
        switch (r.State)
        {
            case FrameState.Entering:
                // The player stands still until the last block of the power-on starts to come on (the controls open then, <see cref="BootPlan.ControlsAt"/>).
                HoldWalking();
                // The shell is normally built already (the elevator ride or the call-in's countdown); otherwise (a suit this player never saw
                // called in, as a late joiner) it is built here, as the boarding starts.
                UseShell(unit, agent);
                break;
            case FrameState.Piloted:
                ReleaseInput();
                // A boarding this player did not see reach the black (a late record): the view is simply the cockpit's from now on.
                _inside = true;
                // Boarding in view already started the power-on; anything else (a joiner, a reload) is worn at once with no boot to watch.
                if (!_poweredOn)
                {
                    _poweredOn = true;
                    _bootStart = now - Plan.Settled;
                    _boot = Plan.Settled;
                    _bootTone = FrameBootScreen.Groups;
                    _voiced = true;
                    Dress(unit, agent);
                }
                MechGun.Begin(unit, now);
                break;
            case FrameState.Exiting:
                {
                    // The movement keys stay still until the landing (Leave); the leaving motion moves the body and the camera, the pilot lands behind.
                    HoldWalking();
                    // The own weapon is back long before the landing gives the controls back: its parts come at the game's own pace.
                    MechGun.End(now, paced: true);
                    _poweredOn = false;
                    _down = 0f;
                    _dark = false;
                    _out = _placed = _handedBack = _exitFound = false;
                    _view?.NearClip(null);
                    break;
                }
        }
        SyncHud(agent);
    }

    private static void Ride(FrameUnit unit, PlayerAgent agent, float now)
    {
        var r = unit.Record;
        var spec = r.Spec;
        var dt = Time.deltaTime;
        TickSkillInput(r, now);

        // Sprint heat: the game's own sprint (its key, its state) while moving.
        var locomotion = agent.Locomotion;
        var running = locomotion != null && locomotion.m_currentStateEnum == PlayerLocomotion.PLOC_State.Run;
        var moving = agent.PlayerCharacterController != null && new Vector3(agent.PlayerCharacterController.LastMove.x, 0f, agent.PlayerCharacterController.LastMove.z).sqrMagnitude > 1e-6f;
        _sprinting = running;
        _sprintBonus = Heat.Step(spec.Movement, running && moving, dt);
        unit.Visual?.Overheat(Heat.Overheated);
        if (_sprintBonus != _wasSprintBonus)
        {
            // The bonus ends when the sprint stops and when the suit overheats; its start has no sound of its own (the footsteps only).
            _wasSprintBonus = _sprintBonus;
            if (!_sprintBonus) unit.Visual?.Sound(FrameSound.BoostEnd);
        }

        // Suit gun stock: reports may only lower the host's record.
        if (MechGun.Active && (MechGun.Clip != _reportedMagazine || MechGun.Reserve != _reportedReserve) && now >= _ammoReportAt)
            ReportAmmo(MechGun.Clip, MechGun.Reserve);


        if (FocusStateManager.CurrentState != eFocusState.FPS) { Hud.RocketMark(null, false); return; }
        // Held by the game (a snatcher, a tank's tentacle, a trap, a stun, controls taken): the suit's own keys wait as the game's do;
        // the game's own weapon rules stay as they are. A leave held down starts over.
        if (FrameWorld.IsSeized(agent))
        {
            Hud.RocketMark(null, false);
            if (_exitHeld > 0f && !_exitSent) HoldSound.Cancel();
            _exitHeld = 0f;
            _exitSent = false;
            Hud.ExitHold(false, dt);
            return;
        }

        if (spec.CarryHeavy)
        {
            ForgeCore.CarrySlot.Track(agent);
            Hud.CarryHint(ForgeCore.CarrySlot.InHands);
        }

        // Rockets: their own key, at the point under the crosshair; while ready, the crosshair marks where they would land.
        var rocketsReady = spec.Rockets != null && now >= Math.Max(_rocketsReadyAt, r.RocketsReadyAt);
        if (rocketsReady) RocketMark(agent, spec.Rockets!);
        else Hud.RocketMark(null, false);
        if (rocketsReady && FrameKeys.Rockets.Pressed) Rockets(unit, agent, spec.Rockets!, now);
        if (InputMapper.GetButtonDown != null && InputMapper.GetButtonDown.Invoke(InputAction.Flashlight, eFocusState.FPS)) Hud.LampUsed();

        // Special.
        if (FrameKeys.Special.Pressed)
        {
            if (spec.Boost != null) FrameWorld.Request(unit, FrameRequestKind.Boost);
            else if (spec.Siege != null) FrameWorld.Request(unit, FrameRequestKind.Siege, value: FrameRules.Planted(r) ? (byte)0 : (byte)1);
            else if (spec.CarryHeavy) ForgeCore.CarrySlot.Toggle(agent, now);
        }
        // A new charge is the rising edge of Boosting: on a guest BoostEndsAt is recomputed from every state message, so it cannot tell charges apart.
        var boosting = r.Boosting(now);
        if (boosting && !_wasBoosting) { Breached.Clear(); DoorHitAt.Clear(); _lastBreachAt = 0f; }
        _wasBoosting = boosting;
        if (spec.Boost != null && boosting && now >= _nextBreach) { _nextBreach = now + BreachInterval; Breach(agent, spec.Boost); }
        if (spec.Scan != null && now >= _nextScan) { _nextScan = now + spec.Scan.Interval; _lastScan = now; Scan(agent, spec.Scan); }

        // Leaving: the exit key's box fills as it is held (the hold itself is timed here).
        var exitDown = FrameKeys.Exit.Held;
        Hud.ExitHold(exitDown, dt);
        if (exitDown)
        {
            _exitHeld += dt;
            if (_exitHeld >= ExitHoldSeconds && !_exitSent)
            {
                _exitSent = true;
                HoldSound.Complete();
                if (MechGun.Active) ReportAmmo(MechGun.Clip, MechGun.Reserve);
                FrameWorld.Request(unit, FrameRequestKind.Exit);
            }
        }
        else
        {
            if (_exitHeld > 0f && !_exitSent) HoldSound.Cancel();
            _exitHeld = 0f;
            _exitSent = false;
        }
    }

    /// <summary>Tells the host what is left of the suit gun's stock (it keeps it for the next pilot).</summary>
    internal static void ReportAmmo(int clip, int reserve)
    {
        if (_unit == null) return;
        _ammoReportAt = Time.time + AmmoReportInterval;
        _reportedMagazine = clip;
        _reportedReserve = reserve;
        FrameWorld.Request(_unit, FrameRequestKind.Ammo, clip, reserve);
    }

    /// <summary>The local player's weapon hit something (any weapon: the mech hands hold it): other peers see a tracer and a
    /// flash from the suit's gun hand. At most once per frame.</summary>
    internal static void Shot(Vector3 hitPoint)
    {
        var unit = _unit;
        if (unit == null || _phase != FrameState.Piloted || unit.Visual == null) return;
        var frame = Time.frameCount;
        if (frame == _shotFrame) return;
        _shotFrame = frame;
        var muzzle = unit.Visual.Muzzle;
        FrameNetwork.Fire(new FrameFireMessage
        {
            Thrower = unit.Record.Thrower,
            ThrowId = unit.Record.ThrowId,
            OriginX = muzzle.x,
            OriginY = muzzle.y,
            OriginZ = muzzle.z,
            TargetX = hitPoint.x,
            TargetY = hitPoint.y,
            TargetZ = hitPoint.z,
            Shots = 1,
        });
    }

    private static int _shotFrame = -1;

    private static void Rockets(FrameUnit unit, PlayerAgent agent, RocketsSpec rockets, float now)
    {
        var camera = agent.FPSCamera;
        if (camera == null || camera.WasCollected) return;
        var origin = camera.transform.position;
        var forward = camera.transform.forward;
        var struck = ForgeCore.Combat.ShooterRay(agent, origin, forward, rockets.Range, out var hit);
        if (struck && !FrameRules.RocketsClear(rockets, hit.distance))
        {
            Hud.Refused(FrameRequestKind.Rockets, Hud.Words.TooClose);
            return;
        }
        var target = struck ? hit.point : origin + forward * rockets.Range;
        _rocketsReadyAt = now + 0.5f;
        FrameNetwork.RocketToHost(new FrameRocketMessage { Thrower = unit.Record.Thrower, ThrowId = unit.Record.ThrowId, TargetX = target.x, TargetY = target.y, TargetZ = target.z, Epoch = OrdnanceApi.WorldEpoch, Drive = unit.Record.Drive });
    }

    /// <summary>Where a salvo fired now would land (the same ray as <see cref="Rockets"/>), dim when the point is beyond the salvo's range or so close its blast would reach the suit.</summary>
    private static void RocketMark(PlayerAgent agent, RocketsSpec rockets)
    {
        var camera = agent.FPSCamera;
        if (camera == null || camera.WasCollected) { Hud.RocketMark(null, false); return; }
        var origin = camera.transform.position;
        var forward = camera.transform.forward;
        if (ForgeCore.Combat.ShooterRay(agent, origin, forward, rockets.Range * 1.5f, out var hit)) Hud.RocketMark(hit.point, hit.distance <= rockets.Range && FrameRules.RocketsClear(rockets, hit.distance));
        else Hud.RocketMark(null, false);
    }

    /// <summary>The host reported a hit on this player's suit: its direction lights the cockpit.</summary>
    internal static void Hit(FrameUnit unit, FrameHitMessage m)
    {
        if (_unit != unit) return;
        Hud.Hit(m.SourceX, m.SourceZ, m.Damage, m.Shielded != 0);
    }

    /// <summary>Any peer: a salvo of <paramref name="unit"/> left; when it is this player's suit, the HUD counts down.</summary>
    internal static void RocketsLaunched(FrameUnit unit, float now)
    {
        if (_unit == unit && unit.Record.Spec.Rockets != null) _rocketsReadyAt = now + unit.Record.Spec.Rockets.Cooldown;
    }

    /// <summary>Assault charge: weak doors ahead take the game's explosion door damage, enemies ahead take a melee hit with heavy
    /// stagger (the game's own door and melee entries, called from this client as the game's melee does).</summary>
    private static void Breach(PlayerAgent agent, BoostSpec boost)
    {
        var forward = agent.Rotation * Vector3.forward;
        forward.y = 0f;
        forward.Normalize();
        // The sphere also covers the ground the suit crossed since the last look (a charge is several metres a look), so a door is
        // never stepped over between two looks.
        var now = Time.time;
        var travelled = _lastBreachAt > 0f && now - _lastBreachAt < 0.5f ? Mathf.Min(Vector3.Distance(agent.Position, _lastBreachPos), 3f) : 0f;
        _lastBreachPos = agent.Position;
        _lastBreachAt = now;
        var centre = agent.Position + Vector3.up * 1.1f + forward * (1.2f - travelled * 0.5f);
        var count = Hits.Sphere(centre, 1.3f + travelled * 0.5f, ~0, QueryTriggerInteraction.Collide);
        for (var i = 0; i < count; i++)
        {
            var collider = Hits[i];
            if (collider == null) continue;
            if (boost.BreachDoors)
            {
                var door = collider.GetComponentInParent<LG_WeakDoor>();
                if (door != null)
                {
                    // A door that one hit did not break is hit again every BreachRehit seconds for as long as the charge lasts; one that
                    // is open, opening or already destroyed is left alone.
                    if (door.LastStatus is eDoorStatus.Open or eDoorStatus.Opening or eDoorStatus.Destroyed) continue;
                    if (DoorHitAt.TryGetValue(door.Pointer, out var at) && now < at) continue;
                    DoorHitAt[door.Pointer] = now + BreachRehit;
                    door.AttemptDamage(eDoorDamageType.Explosion, centre, agent);
                    _unit?.Visual?.Sound(FrameSound.BreachHit);
                    Bump(collider.bounds.ClosestPoint(centre), forward);
                    continue;
                }
            }
            if (boost.Shove > 0f)
            {
                var enemy = collider.GetComponentInParent<EnemyAgent>();
                if (enemy != null && enemy.Alive && Breached.Add(enemy.Pointer))
                {
                    ForgeCore.Combat.Melee(enemy.Damage, boost.Shove, agent, enemy.Position, forward, 10f);
                    _unit?.Visual?.Sound(FrameSound.ShoveHit);
                    Bump(collider.bounds.ClosestPoint(centre), forward);
                }
            }
        }
    }

    private const float BumpGap = 0.6f;
    /// <summary>The bump is felt: for this long the charge runs at this share of its speed (no camera movement).</summary>
    private const float HitchSeconds = 0.12f, HitchSpeed = 0.35f;
    private static float _nextBump, _hitchUntil;

    /// <summary>The charge's shoulder meets a door or an enemy: this suit plays the bash and its impact, and every other peer is told to (at most
    /// one bash per <see cref="BumpGap"/>, so a stretch of wall or a crowd is one bump).</summary>
    private static void Bump(Vector3 point, Vector3 forward)
    {
        var unit = _unit;
        var now = Time.time;
        if (unit?.Visual == null || now < _nextBump) return;
        _nextBump = now + BumpGap;
        _hitchUntil = now + HitchSeconds;
        unit.Visual.Bash(point, forward);
        FrameNetwork.Bump(new FrameBumpMessage { Thrower = unit.Record.Thrower, ThrowId = unit.Record.ThrowId, X = point.x, Y = point.y, Z = point.z, DirX = forward.x, DirZ = forward.z });
    }

    /// <summary>Scout passive scan: every awake enemy within the radius (sleeping ones are skipped) is tagged through the bio tracker's own entry; a soft tone when it found any.</summary>
    private static void Scan(PlayerAgent agent, ScanSpec scan)
    {
        var tagged = false;
        foreach (var found in ForgeCore.Enemies.Near(agent.Position, scan.Radius))
        {
            var enemy = found!;
            if (!FrameRules.ScanTags(ForgeCore.Enemies.Alert(enemy))) continue;
            ToolSyncManager.WantToTagEnemy(enemy);
            tagged = true;
        }
        if (tagged) FrameAudio.Local(FrameSound.HudSection, FrameAudio.Section(0));
    }

    // ---- release ----------------------------------------------------------------------------------------------

    /// <summary>One release step on its own: a step that fails (a destroyed game object while a level unloads, a session already gone) is
    /// logged and never stops the steps after it, so no cockpit, shell, camera or screen effect stays behind.</summary>
    private static void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Exosuit release step '" + what + "' failed: " + error); }
    }

    private static void EndView()
    {
        if (_view == null) return;
        var view = _view;
        _view = null;
        Step("camera", view.Dispose);
    }

    /// <param name="reason">Why the suit was released.</param>
    /// <param name="keepView">The camera's hand-back after a landing is still running: its lease stays until <see cref="HandBack"/> ends it.</param>
    internal static void Release(string reason, bool keepView = false)
    {
        Step("hit marks", Hits.Clear);
        _boardTarget = null;
        _wasSprintBonus = false;
        Step("prompt", ClearPrompt);
        if (_unit == null)
        {
            if (!keepView) { _handing = false; EndView(); }
            return;
        }
        var unit = _unit;
        // The own special weapon comes back first, while the suit's last stock can still be reported.
        Step("first-person body", () => unit.Visual?.Body?.FirstPerson(false));
        Step("special weapon", () => MechGun.End(Time.time));
        if (unit.Record.Spec.CarryHeavy)
        {
            Step("carried object", ForgeCore.CarrySlot.GiveBack);
            Hud.CarryHint(null);
        }
        _unit = null;
        _phase = (FrameState)255;
        Step("input", ReleaseInput);
        Step("charge input", ReleaseCharge);
        Step("planted input", ReleasePlant);
        _hitchUntil = 0f;
        _poweredOn = false;
        if (_dressed) Step("shell", Undress);
        _dressed = false;
        if (!keepView)
        {
            _handing = false;
            EndView();
        }
        _inside = _out = _placed = _handedBack = false;
        _cockpit = 0f;
        _sprinting = _sprintBonus = false;
        Step("cockpit phase", () => Hud.Sync(CockpitPhase.Off));
        Step("cockpit", Hud.Hide);
    }
    internal static void Ejected(FrameEjection reason)
    {
        // Destroyed or forced out takes the middle of the screen; leaving by choice needs no message (the power-down shows it). A
        // boarding or leaving the game interrupted (PilotTaken) shows nothing over the game's own grab or warp.
        var words = Hud.Words;
        if (reason == FrameEjection.Destroyed) Hud.Banner(words.Destroyed, FrameAlertTone.Alert, 2.2f);
        else if (reason == FrameEjection.PilotDown) Hud.Banner(words.ForcedOut, FrameAlertTone.Alert, 2.2f);
    }

    /// <summary>A line in the cockpit's notice list.</summary>
    internal static void Notice(string text) => Hud.Notice(text);

    internal static void Refused(FrameRequestKind kind, FrameRejection reason)
    {
        _boardSent = false;
        _boardHeld = 0f;
        var words = Hud.Words;
        var text = reason switch
        {
            FrameRejection.TooFar when kind == FrameRequestKind.Rockets => words.RocketRange,
            FrameRejection.NotIdle => words.NotFree,
            FrameRejection.TooFar => words.TooFar,
            FrameRejection.AlreadyPiloting => words.AlreadyIn,
            FrameRejection.Cooling => words.NotReady,
            FrameRejection.Planted => words.Planted,
            FrameRejection.Charging => words.Charging,
            FrameRejection.PilotDown => words.PilotDown,
            // FrameRejection.Seized: the game holds the player.
            _ => words.NotNow,
        };
        Hud.Refused(kind, text);
    }

    /// <summary>Level end: the HUD and loan state of this level are gone.</summary>
    internal static void LevelCleanup()
    {
        Step("release", () => Release("level cleanup"));
        _settled = false;
        Step("special weapon", MechGun.Clear);
        Step("shell", DropShell);
        Step("prewarm", FramePrewarm.LevelEnded);
    }

    private static bool Contains(FrameUnit unit)
    {
        var all = FrameWorld.All;
        for (var i = 0; i < all.Count; i++) if (ReferenceEquals(all[i], unit)) return true;
        return false;
    }
}
