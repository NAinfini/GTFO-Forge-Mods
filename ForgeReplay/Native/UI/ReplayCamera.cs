using System;
using ForgeCore;
using ForgeUI;
using UnityEngine;

namespace ForgeReplay;

/// <summary>
/// How the observer watches a replay. Two states: watching (the mouse turns the view, W A S D with E up and Q down fly the free camera,
/// Shift is faster, the wheel sets its speed; behind a player the mouse circles the player and the wheel sets the distance) and using the
/// screen (Control held, or tapped to stay: the HUD pointer clicks the time bar, cards and buttons and the view stays still). The views:
/// at a player's eyes (with the game's first-person arms and gun), behind a player turning with the player's view (chase), behind a player
/// turned only by the mouse while it keeps to the player's place (orbit, the first view), free and above a player looking down. 1 to 4 or a card
/// picks a player (again: behind and eyes swap), [ and ] the previous and next, V first person, chase and orbit in turn, F free, T above, C the
/// automatic camera on and off; Space plays and pauses, the arrows jump 5 s, Shift and an arrow the previous or next mark, up and down the speed,
/// comma and period step a frame. Everything works while paused. A change of view or of the player watched glides there (under half a second)
/// unless the two are far apart. The automatic camera only chooses whom to watch, in whichever view is on; choosing a player by hand stops it. The camera has a lease of its own, newer
/// than the replay's, so it owns the view; the game's own look, movement and weapon keys are held off the whole time, and the system
/// cursor is never unlocked. An enemy can be watched instead of a player (<see cref="Enemy"/>: G, a click on it while using the screen, its
/// line in the kill feed or the highlights, or the automatic camera): behind it, circled or chased; when it is gone the view goes back to the
/// player. The free camera can mark stops of a film path (K, Shift+K clears) and fly it (L) on <see cref="CameraPath"/>, the screen hidden.
/// </summary>
internal static class ReplayCamera
{
    private const float FollowUp = 0.45f, Fast = 4f, LookScale = 1f, EyeForward = 0.12f, GlideSeconds = 0.45f, GlideFarthest = 25f;
    private const float OrbitTilt = 8f, SlowestFree = 1.5f, FastestFree = 48f, NearestFollow = 1.5f, FarthestFollow = 8f;
    private static readonly InputAction[] Blocked =
    {
        InputAction.LookHorizontal, InputAction.LookVertical, InputAction.MoveHorizontal, InputAction.MoveVertical, InputAction.Jump,
        InputAction.Run, InputAction.Crouch, InputAction.Fire, InputAction.Aim, InputAction.Reload, InputAction.Use, InputAction.Melee,
        InputAction.ScrollItems, InputAction.SelectStandard, InputAction.SelectSpecial, InputAction.SelectMelee, InputAction.SelectTool,
        InputAction.SelectConsumable, InputAction.SelectResourcePack, InputAction.SelectHackingTool, InputAction.ToggleGear,
        InputAction.Flashlight, InputAction.ToggleMap, InputAction.ToggleObjectives, InputAction.NavMarkerPing, InputAction.UseOnAlly,
        InputAction.ToggleCommunicationMenu, InputAction.ShowTeammateInfo,
        // The game's menu key: only the replay's Esc handling opens the menu (Esc closes its own layers first); the lease lets the key through while the menu has the focus, so Esc still closes it.
        InputAction.MenuToggle,
    };

    private static CameraLease? _view;
    private static InputLease? _input;
    private static PointerToggle? _pointer;
    private static bool _late, _placed;
    private static float _yaw, _pitch, _height = 18f, _speed = 6f, _distance = 3.2f, _orbitYaw, _orbitPitch, _glide = 1f, _heading, _clear;
    private static Vector3 _free, _shown, _velocity, _glideFrom;
    private static Quaternion _shownTurn = Vec.Identity, _glideTurn = Vec.Identity;
    private static ViewMode _lastMode;
    private static int _lastSlot = -1;

    internal static ViewMode Mode { get; private set; } = ViewMode.Orbit;

    /// <summary>The enemy watched (its entity in the recording), -1 while a player is.</summary>
    internal static int Enemy { get; private set; } = -1;

    /// <summary>Whom the view is on: the enemy watched, else the player.</summary>
    internal static int Target => Enemy >= 0 ? Enemy : Replayer.Follow;

    private const float EnemyEye = 1.2f;
    private static readonly CameraPath Path = new();
    private static float _pathAt = -1f;
    private static readonly System.Collections.Generic.List<int> Ranked = new();

    /// <summary>True while the film path is flown: the screen hides itself.</summary>
    internal static bool Flying => _pathAt >= 0f;

    /// <summary>Watches <paramref name="entity"/> (the eyes become chasing it); the player stays chosen for when it is gone. Chosen
    /// <paramref name="byHand"/>, the free camera goes behind it; the automatic camera leaves the view as it is.</summary>
    internal static void WatchEnemy(int entity, bool byHand = true)
    {
        if (entity < 4) return;
        if (byHand && Mode == ViewMode.Free) SetMode(ViewMode.Orbit);
        Enemy = entity;
    }

    /// <summary>Back to the player.</summary>
    internal static void StopEnemy() => Enemy = -1;

    // G: the enemy in the middle of the view, or (none there, or that one already watched) the next one near whom the view is on.
    private static void NextEnemy()
    {
        if (Replayer.Track is not { } track) return;
        var state = track.State;
        var ahead = _shownTurn * Vec.Forward;
        var pick = EnemyPick.Aimed(state, _shown.x, _shown.y, _shown.z, ahead.x, ahead.y, ahead.z);
        if (pick < 0 || pick == Enemy)
        {
            ref var at = ref state.Poses[Target];
            var from = at.Present ? new Vector3(at.X, at.Y, at.Z) : _shown;
            EnemyPick.Ranked(state, from.x, from.y, from.z, Replayer.Big, Ranked);
            pick = EnemyPick.Next(Ranked, Enemy);
        }
        if (pick < 0) { ReplayScreen.Notice("enemy.none"); return; }
        WatchEnemy(pick);
        ReplayScreen.ManualChoice();
    }

    // The film path's keys: a stop where the free camera is (in the free camera only), clear, fly or stop flying.
    private static void PathKeys()
    {
        if (ReplayKeys.Pressed(ReplayKeys.PathMark)) PathMark();
        if (ReplayKeys.Pressed(ReplayKeys.PathClear)) PathClear();
        if (ReplayKeys.Pressed(ReplayKeys.PathPlay)) PathPlay();
    }

    private static void PathMark()
    {
        if (Mode != ViewMode.Free) ReplayScreen.Notice("path.freeonly");
        else { Path.Add(_free.x, _free.y, _free.z, _yaw, _pitch, _speed); ReplayScreen.Notice("path.marked", Path.Keys.Count); }
    }

    private static void PathClear() { Path.Clear(); _pathAt = -1f; ReplayScreen.Notice("path.cleared"); }

    private static void PathPlay()
    {
        if (Flying) _pathAt = -1f;
        else if (Path.Keys.Count < 2) ReplayScreen.Notice("path.few");
        else { SetMode(ViewMode.Free); _pathAt = 0f; }
    }

    /// <summary>The film path's keys by name (K, Shift+K, L): mark, clear, play (or stop when flying), and the line that says what is there.</summary>
    internal static string Film(string action)
    {
        if (_view == null) return "no replay";
        switch (action.ToLowerInvariant())
        {
            case "mark": PathMark(); break;
            case "clear": PathClear(); break;
            case "play": case "stop": PathPlay(); break;
            case "state": break;
            default: return "no film action " + action + "; one of mark, play, stop, clear, state";
        }
        return "film points=" + Path.Keys.Count + " flying=" + (Flying ? "yes" : "no") + " view=" + Mode;
    }

    /// <summary>Stops flying the film path (Esc).</summary>
    internal static void Land() => _pathAt = -1f;

    /// <summary>Using the screen (pointer shown, view still) rather than watching: Control held or tapped (ForgeUI
    /// <see cref="PointerToggle"/>).</summary>
    internal static bool Ui => _pointer?.Using == true;

    /// <summary>True while the screen's camera shows a player's eyes; the first-person arms and gun follow it.</summary>
    internal static bool Eyes => _view != null && Mode == ViewMode.Eye && Enemy < 0;

    /// <summary>Where the camera was put last frame.</summary>
    internal static Vector3 Shown => _shown;

    /// <summary>How the camera was turned last frame.</summary>
    internal static Quaternion ShownTurn => _shownTurn;

    internal static void On()
    {
        Mode = ViewMode.Orbit; Enemy = -1; Path.Clear(); _pathAt = -1f; _placed = false; _height = 18f; _distance = 3.2f; _orbitYaw = _orbitPitch = 0f; _velocity = Vec.Zero; _glide = 1f; _lastSlot = -1;
        _pointer ??= HudPointer.Toggle(Plugin.Id);
        _pointer.Enabled = true;
        _view = CameraLease.Take(Plugin.Id);
        _input = InputLease.Take(Plugin.Id);
        _input.Block(Blocked);
        // After the replay's own late step, which places the puppets this frame.
        if (!_late) { _late = true; Frame.OnLateUpdate(Plugin.Id, Late); }
    }

    internal static void Off()
    {
        _view?.Dispose(); _view = null;
        _input?.Dispose(); _input = null;
        if (_pointer != null) _pointer.Enabled = false;
    }

    /// <summary>Per frame: the keys. <paramref name="present"/> tells which players the recording has.</summary>
    internal static void Keys(bool[] present, PlaybackClock clock)
    {
        if (_view == null) return;
        if (_menuDown) { _menuDown = false; _input?.Release(InputAction.MenuToggle, requireConsumed: false); }
        if (ReplayKeys.Pressed(ReplayKeys.Play)) clock.Toggle();
        // A tap of an arrow jumps 5 s; held, the right one plays at 4x until it comes up (then the speed and pause it had), the left one
        // jumps back 5 s every quarter second. With Shift (their own actions) the previous or next mark, as do the mouse's side buttons (ReplayScreen).
        if (ReplayKeys.Pressed(ReplayKeys.Forward)) Replayer.Jump(clock.PositionMs + 5000);
        if (ReplayKeys.Pressed(ReplayKeys.Back)) Replayer.Jump(clock.PositionMs > 5000 ? clock.PositionMs - 5000 : 0);
        if (ReplayKeys.Pressed(ReplayKeys.FastForward)) { _fastFrom = (clock.Speed, clock.Playing); SetSpeed(clock, 4f); clock.Play(); }
        if (ReplayKeys.Released(ReplayKeys.FastForward) && _fastFrom is { } from)
        {
            _fastFrom = null;
            SetSpeed(clock, from.Speed);
            if (!from.Playing) clock.Pause();
        }
        if (ReplayKeys.Held(ReplayKeys.Rewind) && ForgeCore.Frame.UnscaledTime >= _rewindAt)
        {
            _rewindAt = ForgeCore.Frame.UnscaledTime + 0.25f;
            Replayer.Jump(clock.PositionMs > 5000 ? clock.PositionMs - 5000 : 0);
        }
        if (ReplayKeys.Pressed(ReplayKeys.ToStart)) Replayer.Jump(0);
        if (ReplayKeys.Pressed(ReplayKeys.ToEnd)) Replayer.Jump(clock.DurationMs > 500 ? clock.DurationMs - 500 : 0);
        if (ReplayKeys.Pressed(ReplayKeys.Faster)) clock.ChangeSpeed(1);
        if (ReplayKeys.Pressed(ReplayKeys.Slower)) clock.ChangeSpeed(-1);
        if (ReplayKeys.Pressed(ReplayKeys.FrameForward)) clock.Step(1);
        if (ReplayKeys.Pressed(ReplayKeys.FrameBack)) { clock.Step(-1); Replayer.Jump(clock.PositionMs); }
        for (var slot = 0; slot < 4; slot++)
            if (ReplayKeys.Pressed(ReplayKeys.Watch[slot]) && present[slot]) Select(slot);
        if (ReplayKeys.Pressed(ReplayKeys.PlayerPreviousKey)) Cycle(present, -1);
        if (ReplayKeys.Pressed(ReplayKeys.PlayerNextKey)) Cycle(present, 1);
        if (ReplayKeys.Pressed(ReplayKeys.CameraNextKey)) SetMode(ReplayView.NextPersonView(Mode));
        // The mouse buttons go round the views (left) and the players (right), unless the click is on the screen's own controls (using the
        // screen, on the time bar, a card, a button or an open panel). Watching, they are the game's fire and aim, which are held off.
        var free = !Ui || !ReplayScreen.PointerNearHud;
        // Using the screen, a click on an enemy in the picture watches it; elsewhere the left button goes round the views.
        if (free && ReplayKeys.Pressed(ReplayKeys.CameraNext) && Ui && ReplayScreen.EnemyUnderPointer() is var clicked and >= 0) { WatchEnemy(clicked); ReplayScreen.ManualChoice(); }
        else if (free && ReplayKeys.Pressed(ReplayKeys.CameraNext)) SetMode(ReplayView.NextPersonView(Mode));
        if (free && ReplayKeys.Pressed(ReplayKeys.PlayerNext)) Cycle(present, 1);
        if (free && ReplayKeys.Pressed(ReplayKeys.PlayerPrevious)) Cycle(present, -1);
        if (free && ReplayKeys.Pressed(ReplayKeys.Reset)) Reset();
        if (ReplayKeys.Pressed(ReplayKeys.Free)) ToggleView(ViewMode.Free);
        if (ReplayKeys.Pressed(ReplayKeys.Above)) SetMode(Mode == ViewMode.Overhead ? ViewMode.Orbit : ViewMode.Overhead);
        if (ReplayKeys.Pressed(ReplayKeys.Auto)) ReplayScreen.ToggleAuto();
        if (ReplayKeys.Pressed(ReplayKeys.Enemy)) NextEnemy();
        PathKeys();
    }

    private static bool _menuDown;

    /// <summary>Esc tapped with nothing of the replay's open: the game's own menu, as Esc opens it (the key is held off the game meanwhile).</summary>
    internal static void GameMenu()
    {
        if (_input == null || Replayer.GameMenuOpen()) return;
        _input.Press(InputAction.MenuToggle);
        _menuDown = true;
    }

    private static (float Speed, bool Playing)? _fastFrom;
    private static float _rewindAt;

    private static void SetSpeed(PlaybackClock clock, float speed)
    {
        var want = Array.IndexOf(PlaybackClock.Speeds, speed);
        var have = Array.IndexOf(PlaybackClock.Speeds, clock.Speed);
        if (want >= 0 && have >= 0) clock.ChangeSpeed(want - have);
    }

    /// <summary>The middle button: orbiting the player watched, from behind, at the first distance (out of a free flight that went astray).</summary>
    internal static void Reset()
    {
        SetMode(ViewMode.Orbit);
        _distance = 3.2f; _orbitPitch = 0f; _behind = true;
    }

    private static bool _behind;

    /// <summary>A jump in time: the camera is put at the new place at once rather than eased there (easing across the level put the view
    /// outside the rooms for a moment, which draws nothing).</summary>
    internal static void Snap() => _snap = true;

    /// <summary>The free camera at a place, looking the way given (ReplayControl.LookAt).</summary>
    internal static void PlaceFree(Vector3 at, float yaw, float pitch)
    {
        SetMode(ViewMode.Free);
        _free = at; _yaw = yaw; _pitch = pitch; _velocity = Vec.Zero; _snap = true;
    }
    private static bool _snap;

    /// <summary>A player's number or card: another player is watched the same way, the same one swaps behind and eyes.</summary>
    internal static void Select(int slot)
    {
        if (slot != Replayer.Follow && Mode != ViewMode.Orbit) _orbitYaw = _orbitPitch = 0f;
        SetMode(ReplayView.Select(Mode, Replayer.Follow, slot));
        Replayer.Follow = slot; Enemy = -1;
        ReplayScreen.ManualChoice();
    }

    // The previous or next player, watched the same way.
    private static void Watch(int slot)
    {
        if (slot == Replayer.Follow && Enemy < 0) return;
        if (Mode != ViewMode.Orbit) _orbitYaw = _orbitPitch = 0f;
        SetMode(ReplayView.Select(Mode, Replayer.Follow, slot));
        Replayer.Follow = slot; Enemy = -1;
        ReplayScreen.ManualChoice();
    }

    /// <summary>The free camera button: into that view, and out of it to orbiting the player. The automatic camera goes on choosing whom to watch.</summary>
    internal static void ToggleView(ViewMode mode) => SetMode(Mode == mode ? ViewMode.Orbit : mode);

    internal static void SetMode(ViewMode mode)
    {
        // The view's turn as angles, read only for the two modes that start from it.
        var angles = mode is ViewMode.Free or ViewMode.Orbit && Mode != mode ? _shownTurn.eulerAngles : default;
        if (mode == ViewMode.Free && Mode != ViewMode.Free)
        {
            // The free camera starts where the view is.
            _free = _shown;
            _yaw = angles.y;
            _pitch = angles.x > 180f ? angles.x - 360f : angles.x;
            _velocity = Vec.Zero;
        }
        if (mode == ViewMode.Eye) Enemy = -1;
        if (mode == ViewMode.Follow && Mode != ViewMode.Follow) _orbitYaw = _orbitPitch = 0f;
        if (mode == ViewMode.Orbit && Mode != ViewMode.Orbit)
        {
            // The orbit starts the way the view is turned, so nothing swings when it is chosen.
            _orbitYaw = angles.y;
            _orbitPitch = Mathf.Clamp((angles.x > 180f ? angles.x - 360f : angles.x) - OrbitTilt, -35f, 60f);
        }
        Mode = mode;
    }

    /// <summary>Using the screen (true) or watching (false), as Control switches.</summary>
    internal static void SetUi(bool ui) => _pointer?.Set(ui);

    /// <summary>Wheel notches (up positive): the free camera's speed, the distance behind a player, the height above one.</summary>
    internal static void Wheel(float notches)
    {
        if (notches == 0f) return;
        switch (Mode)
        {
            case ViewMode.Free: _speed = Mathf.Clamp(_speed * Mathf.Pow(1.25f, notches), SlowestFree, FastestFree); break;
            case ViewMode.Overhead: _height = Mathf.Clamp(_height - notches * 2f, 6f, 60f); break;
            case ViewMode.Follow: case ViewMode.Orbit: _distance = Mathf.Clamp(_distance - notches * 0.4f, NearestFollow, FarthestFollow); break;
        }
    }

    /// <summary>The previous (-1) or next (1) player in the recording, watched the same way ([ and ]).</summary>
    internal static void Cycle(bool[] present, int direction) => Watch(ReplayView.Next(present, Replayer.Follow, direction));

    /// <summary>Where the eyes view of the watched player is at this moment of the clock, whether or not the camera has been placed this frame
    /// yet; false while a change of view glides (the camera is then between the two). The recorded feet and look turn, and the height of the
    /// recorded stance: the same eye <see cref="Place"/> gives the camera.</summary>
    internal static bool EyePose(out Vector3 position, out Quaternion turn)
    {
        position = default; turn = default;
        var track = Replayer.Track;
        var clock = Replayer.Clock;
        if (track == null || clock == null || _glide < 1f || Mode != ViewMode.Eye || Enemy >= 0) return false;
        var slot = Replayer.Follow;
        if (!track.State.Shown(slot, clock.PositionMs, out var x, out var y, out var z, out var qx, out var qy, out var qz, out var qw)) return false;
        turn = new Quaternion(qx, qy, qz, qw);
        position = new Vector3(x, y, z) + Vec.Up * ReplayView.EyeHeight(Locomotion(track.State.PlayerStatus[slot])) + turn * Vec.Forward * EyeForward;
        return true;
    }

    private static void Late()
    {
        if (_view == null || !Replayer.Active) return;
        try { Place(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Placing the replay camera failed: " + error); }
    }

    // The yaw in degrees (0 to 360) of something facing along <paramref name="forward"/>: the same as <c>Quaternion.LookRotation(forward).eulerAngles.y</c>
    // for a turn with no roll, computed here (the game's interop makes eulerAngles a native call).
    private static float Heading(Vector3 forward)
    {
        var yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        return yaw < 0f ? yaw + 360f : yaw;
    }

    private static void Place()
    {
        var track = Replayer.Track;
        var clock = Replayer.Clock;
        var view = _view;
        if (track == null || clock == null || view == null) return;
        var dt = Math.Min(ForgeCore.Frame.UnscaledDeltaTime, 0.1f);
        var state = track.State;
        var slot = Replayer.Follow;
        // An enemy watched until it is gone; then the player again.
        var enemy = Enemy;
        var have = state.Shown(enemy >= 4 ? enemy : slot, clock.PositionMs, out var x, out var y, out var z, out var qx, out var qy, out var qz, out var qw);
        if (enemy >= 4 && !have) { Enemy = enemy = -1; have = state.Shown(slot, clock.PositionMs, out x, out y, out z, out qx, out qy, out qz, out qw); }
        if (!have)
        {
            // The followed player is not in the recording at this moment (or never was): watch the first one who is, rather than nobody.
            for (var other = 0; other < 4 && !have; other++)
                if (state.Poses[other].Present && state.Shown(other, clock.PositionMs, out x, out y, out z, out qx, out qy, out qz, out qw)) { have = true; Replayer.Follow = slot = other; }
        }
        var feet = new Vector3(x, y, z);
        var look = new Quaternion(qx, qy, qz, qw);
        var target = enemy >= 0 ? enemy : slot;
        // An enemy has no eyes to look through: behind it, turning with it.
        var mode = enemy >= 0 && Mode == ViewMode.Eye ? ViewMode.Follow : Mode;
        if (Flying && Mode != ViewMode.Free) _pathAt = -1f;
        var height = enemy >= 0 ? EnemyEye : ReplayView.EyeHeight(Locomotion(state.PlayerStatus[slot]));
        // Nobody to watch yet: the free camera starts at the world's origin, so wait for a player rather than show nothing.
        if (!have && !_placed) return;
        if (!have && Mode != ViewMode.Free) SetMode(ViewMode.Free);
        if (have && !_placed && Mode == ViewMode.Free) { _free = feet + Vec.Up * 2f; _yaw = Heading(look * Vec.Forward); _pitch = 0f; }

        // Watching or using the screen alike: nothing on the screen scrolls.
        // The wheel is the camera's, except over the time bar or an open list while using the screen (those scroll themselves).
        if (!(Ui && ReplayScreen.WheelTaken)) Wheel(Input.mouseScrollDelta.y);
        Vector3 position;
        Quaternion turn;
        switch (mode)
        {
            case ViewMode.Eye:
                // The watched player's own recorded feet, look turn and stance: its eyes are where the game puts them, and looking down turns
                // the view in place instead of carrying it around the body.
                position = feet + Vec.Up * height + look * Vec.Forward * EyeForward;
                turn = look;
                break;
            case ViewMode.Overhead:
                position = feet + Vec.Up * _height;
                turn = Quaternion.Euler(90f, 0f, 0f);
                break;
            case ViewMode.Free:
                {
                    // Flying eases in and out (the speed is reached in about a fifth of a second) rather than starting and stopping dead.
                    var want = Vec.Zero;
                    // Flying the film path: the curve places the camera, the keys and mouse do not.
                    if (Flying && Path.Sample(_pathAt, out var px, out var py, out var pz, out var pyaw, out var ppitch))
                    {
                        _pathAt += dt;
                        _free = new Vector3(px, py, pz); _yaw = pyaw; _pitch = ppitch; _velocity = Vec.Zero;
                        if (_pathAt > Path.Seconds) _pathAt = -1f;
                    }
                    else if (!Ui)
                    {
                        _yaw += InputLease.Axis(InputAction.LookHorizontal) * LookScale;
                        _pitch = Mathf.Clamp(_pitch - InputLease.Axis(InputAction.LookVertical) * LookScale, -89f, 89f);
                        // The game's own move keys (its settings rebind them), read through the lease that holds them off the observer's body.
                        var fly = new Vector3(Mathf.Clamp(InputLease.Axis(InputAction.MoveHorizontal), -1f, 1f), 0f, Mathf.Clamp(InputLease.Axis(InputAction.MoveVertical), -1f, 1f));
                        var lift = (ReplayKeys.HeldAnyModifiers(ReplayKeys.Rise) ? 1f : 0f) - (ReplayKeys.HeldAnyModifiers(ReplayKeys.Sink) ? 1f : 0f);
                        want = (Quaternion.Euler(_pitch, _yaw, 0f) * fly + Vec.Up * lift) * _speed * (ReplayKeys.HeldAnyModifiers(ReplayKeys.Fast) ? Fast : 1f);
                    }
                    _velocity = Vector3.Lerp(_velocity, want, 1f - Mathf.Exp(-12f * dt));
                    _free += _velocity * dt;
                    position = _free;
                    turn = Quaternion.Euler(_pitch, _yaw, 0f);
                    break;
                }
            default:
                {
                    // Behind the player, circled by the mouse, the wheel nearer or farther. Chasing, the circle turns with the player, eased; orbiting, only the mouse turns it.
                    if (!Ui)
                    {
                        _orbitYaw += InputLease.Axis(InputAction.LookHorizontal) * LookScale;
                        _orbitPitch = Mathf.Clamp(_orbitPitch - InputLease.Axis(InputAction.LookVertical) * LookScale, -35f, 60f);
                    }
                    var eye = feet + Vec.Up * height;
                    var flat = look * Vec.Forward;
                    flat.y = 0f;
                    var heading = Vec.SqrLength(flat) > 1e-4f ? Heading(flat) : 0f;
                    var same = _placed && !_snap && _lastMode == mode && _lastSlot == target;
                    _heading = same ? Mathf.LerpAngle(_heading, heading, 1f - Mathf.Exp(-12f * dt)) : heading;
                    // The first view of a replay starts behind the player.
                    if ((!_placed || _behind) && mode == ViewMode.Orbit) { _orbitYaw = heading; _behind = false; }
                    var around = Quaternion.Euler(OrbitTilt + _orbitPitch, (mode == ViewMode.Follow ? _heading : 0f) + _orbitYaw, 0f);
                    var ahead = around * Vec.Forward;
                    var level = Vec.Of(ahead.x, 0f, ahead.z);
                    var levelLength = Vec.Length(level);
                    level = levelLength > 1e-5f ? level / levelLength : Vec.Zero; // Vector3.normalized, computed here
                    var full = eye - ahead * _distance + Vec.Up * FollowUp;
                    // Not through a wall: the camera stops short of what is between the player and it. A ball rather than a thin ray, so the edge of
                    // a wall does not hit on one frame and miss on the next; quick to come in, slow to go back out.
                    var ray = full - eye;
                    var reach = Vec.Length(ray);
                    var clear = Physics.SphereCast(eye, 0.2f, ray / reach, out var hit, reach, LayerManager.MASK_WORLD) ? Math.Max(0.3f, hit.distance) : reach;
                    _clear = same ? Mathf.Lerp(_clear, clear, 1f - Mathf.Exp((clear < _clear ? -25f : -3f) * dt)) : clear;
                    var want = eye + ray / reach * Math.Min(_clear, reach);
                    position = same ? Vector3.Lerp(_shown, want, 1f - Mathf.Exp(-12f * dt)) : want;
                    // The way the camera looks does not depend on how far a wall lets it stand back.
                    turn = Quaternion.LookRotation(eye + level * 1.5f - full);
                    break;
                }
        }
        // A new view or player: glide from where the camera was, unless that is so far that gliding would sweep through the level.
        if (_placed && (mode != _lastMode || target != _lastSlot) && Vector3.Distance(_shown, position) < GlideFarthest)
        {
            _glide = 0f; _glideFrom = _shown; _glideTurn = _shownTurn;
        }
        else if (_placed && (mode != _lastMode || target != _lastSlot)) _glide = 1f;
        _lastMode = mode; _lastSlot = target;
        if (_snap) { _snap = false; _glide = 1f; }
        if (_glide < 1f)
        {
            _glide = Math.Min(1f, _glide + dt / GlideSeconds);
            var s = ForgeUI.Ease.Smoothstep(_glide);
            position = Vector3.Lerp(_glideFrom, position, s);
            turn = Quaternion.Slerp(_glideTurn, turn, s);
        }
        // The watched player's room, for a view no room holds. The free camera belongs with nobody.
        view.Look(position, turn, have && Mode != ViewMode.Free ? feet + Vec.Up : null);
        if (mode == ViewMode.Eye) FirstPerson.Frame(position, turn, state, slot, clock.PositionMs, Replayer.Pending!.Header!.Players, dt);
        _shown = position;
        _shownTurn = turn;
        _placed = true;
    }

    // The locomotion state from a PlayerStatus payload (U slot, U locomotion, ...); 0 standing without one.
    private static int Locomotion(byte[]? status)
    {
        if (status == null) return 0;
        var read = new PayloadReader(status);
        read.U();
        return (int)read.U();
    }
}
