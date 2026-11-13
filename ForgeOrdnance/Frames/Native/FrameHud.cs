using System;
using System.Collections.Generic;
using ForgeUI;
using Player;
using UnityEngine;

namespace ForgeOrdnance.Frames;

/// <summary>
/// The suit's cockpit, put together from ForgeUI templates and fed from <see cref="FrameHudModel"/>. While the suit is worn the game's
/// whole HUD is hidden except the world markers, the chat (docked at the bottom right under the inventory, lit while typed in
/// with the cockpit staying on screen) and the crosshair's hit feedback, and the cockpit takes its place: the zone, heading and altitude
/// in one row, with the shield, armour and pilot bars right under it, at the top centre; the magazine (of whatever is in hand, unlimited without one) and heat arcs either side of the
/// crosshair; objectives at the top left; the interaction prompt and subtitles under the crosshair;
/// the whole inventory at the bottom right (the special weapon slot marked as the suit's gun); the damage disc, skills, lamp, exit and
/// the squad at the bottom left; enemies out of view on the bearing ring; hit glow at the screen edges, the visor's struts, shade and
/// cracks, and the enemies in sight outlined (ForgeVisual's automatic enemy outline, on while worn). Sizes, shapes and faint defaults are
/// ForgeUI's (the cockpit design); the suit gives values, words and state colours.
/// When the game's HUD is hidden and given back follows <see cref="CockpitScope"/> (Core, tested on every path), read every frame from
/// what the local player is now (<see cref="Sync"/>); a release by ForgeUI or a failed update is taken back as soon as it can be. The power-on over the screen is ForgeUI's power-on player with the suit's
/// panels (<see cref="FrameBootScreen"/>); its NEURAL LINK bar, self-test and NEURAL LINK OK are ForgeUI's power-on message with the suit's
/// beats, colours and words (<see cref="FrameBootMessage"/>), and the cockpit's
/// parts come online one by one around it (<see cref="BootPlan.ReportAt"/>, each with ForgeUI's reveal) and go dark with the panels when
/// leaving (<see cref="CockpitParts.ShownLeaving"/>).
/// </summary>
internal sealed class FrameHud
{
    private const int BannerPriority = 20, HeldPriority = 40;
    /// <summary>The readings stand above the armour bar (priority 50) in the top centre stack.</summary>
    private const int ReadingsPriority = 60;
    /// <summary>The docked chat: its width (HUD units, the game's 450 chat box and a pad either side) and the chat lines it holds over
    /// the input row, at the bottom right under the inventory (priority 20, nearer the corner than the inventory's 10).</summary>
    private const float ChatWidth = 470f;
    private const int ChatLines = 6, ChatPriority = 20;
    /// <summary>The docked chat in the cockpit's look: its lines and the typed line in 1080p pixels, the same face as the HUD's names.</summary>
    private const float ChatTextPx = 18f, ChatInputPx = 19f;
    private const float HeldSeconds = 30f;
    /// <summary>The barrier layer over the armour bar, 1080p pixels (thick enough to read at a glance; the bar is 12).</summary>
    private const float BarrierHeight = 5f;
    private const int Markers = 8;
    /// <summary>The name plate's lowest edge over a suit's top, metres.</summary>
    private const float ThreatRadius = 20f;
    // One restrained palette: ice (cyan-white) for everything normal, orange only for emphasis and what is ready to use, red only for
    // danger, cyan only for the barrier, text on ForgeUI's dark plate.
    private static readonly Color Primary = UIKitColors.Cockpit, Warning = UIKitColors.Orange;

    private readonly FrameAlerts _alerts = new();
    private readonly CockpitScope _scope = new();
    private FrameHudModel _model = null!;
    private FrameWords _words = null!;

    private UIKitMod _ui = null!;
    private GameHudHide? _hide;
    private GameChatMove? _chat;
    private HudBanner? _banner, _heldBanner;
    private PlayerHold _held;
    private EnemyHealthBar? _enemyBars;
    private HitFeedback? _hits;
    private HudNotices? _notices;
    private HudBootMessage? _link;
    private ScreenPowerOn? _power;
    private HudSegmentBar? _bars;
    private HudSilhouette? _silhouette;
    private HudArcGauge? _clip, _heat;
    // The arcs: steady at 70 %, at full brightness for a moment when something changes (ForgeUI's flare: reload, overheat, low magazine).
    private const float ArcSteady = 0.7f, CoreAlpha = 0.85f;
    private CrosshairAttachment? _aim;
    private HudLoadout? _loadout;
    private HudLabel? _readings;
    private HudSkillSlot? _special, _rockets, _carry;
    private HudKeyHint? _lamp, _exit;
    private bool? _carryState;
    private string _carryKey = "";
    private HudTeammates? _mates;
    private HudObjectives? _objectives;
    private HudInteraction? _interaction;
    private HudSubtitles? _subtitles;
    private HudBearingArc? _threats;
    private HitEdge? _edge;
    private Visor? _visor;
    private ForgeVisual.EnemyOutline? _outline;
    private HudWaypoints? _plates;
    private HudWaypoints? _around;
    private WaypointId _aroundPoint;

    private readonly Dictionary<uint, (string Language, UIText Text)> _names = new();

    /// <summary>The suit's name in capitals, from its definition (<see cref="FrameWorld.DefinitionName"/>), read again when the language
    /// changes. Empty (one error line) when no loaded definition has the record's key.</summary>
    private UIText NameOf(FrameRecord r)
    {
        var language = UIKit.Language;
        if (_names.TryGetValue(r.DefinitionKey, out var known) && known.Language == language) return known.Text;
        if (FrameWorld.DefinitionName(r.DefinitionKey) is not { } name)
        {
            Plugin.Logger.ErrorOnce("Exosuit " + r.Spec.Model + " has no name: no loaded definition has key " + r.DefinitionKey + ".");
            _names[r.DefinitionKey] = (language, UIText.Empty);
            return UIText.Empty;
        }
        var text = UIText.Plain(name);
        _names[r.DefinitionKey] = (language, text);
        return text;
    }

    // The suit being shown and the power-on's plan.
    private FrameRecord? _record;
    private BootPlan _plan = BootPlan.Default;
    private bool _barsRolled;
    private float _slide;
    /// <summary>A failed update puts the parts off until this time (game clock), then they are drawn again: a fault never lasts the ride.</summary>
    private float _faultUntil = float.NegativeInfinity;
    private const float RetrySeconds = 1f;
    /// <summary>The game's HUD hide and chat move are taken again no sooner than this after ForgeUI refused or released them.</summary>
    private float _takeAgainAt = float.NegativeInfinity;
    /// <summary>The parts the screen check looks at, and whether this ride's cockpit has been seen whole on screen.</summary>
    private HudWidget?[] _parts = Array.Empty<HudWidget?>();
    private bool _onScreen;
    private float _nextScreenCheck, _missingSince = -1f;

    internal FrameWords Words => _words;
    internal CockpitPhase Phase => _scope.Phase;

    /// <summary>Registers every element (once, at plugin load); all stay hidden until a suit is worn.</summary>
    internal void Register()
    {
        _ui = UIKit.Mod(Plugin.Id);
        _words = MakeWords();
        _model = new FrameHudModel(_words);
        UIKit.LanguageChanged += () =>
        {
            _words = MakeWords();
            _model.Words = _words;
            if (_record != null) _link?.Prepare(FrameBootMessage.Words(_words, Percent(_record)));
        };
        FrameHudArt.Register(_ui);

        _hide = _ui.HideGameHud("Pilot");
        // The chat stands on the bottom right corner's edge, under the inventory, in the cockpit's face and colours (the game's backings
        // not drawn, players' names in their own colours, alerts orange); lit in the cockpit's colour while typed in.
        _chat = _ui.MoveGameChat("PilotChat", HudRegion.BottomRight, ChatPriority, ChatWidth, ChatLines);
        if (_chat != null)
        {
            _chat.Accent = Primary;
            _chat.Look = new ChatLook
            {
                TextPx = ChatTextPx,
                InputPx = ChatInputPx,
                Text = UIKitColors.Faded(Primary, 0.88f),
                Alert = Warning,
                Input = UIKitColors.Bright,
                Caret = Primary,
                IdleLines = 3,
                IdleSeconds = 10f,
                IdleOpacity = 0.85f,
            };
        }
        if (_hide != null) _hide.Released += why => KitReleased("game HUD hide", why);
        if (_chat != null) _chat.Released += why => KitReleased("chat move", why);

        _banner = _ui.AddBanner("Banner", BannerPriority);
        _heldBanner = _ui.AddBanner("Held", HeldPriority);
        _notices = _ui.AddNotices("Notices");
        _link = _ui.AddBootMessage("Link", FrameBootMessage.Beats, FrameBootMessage.Look);

        _silhouette = _ui.AddSilhouette("Damage", FrameHudArt.Silhouette, new HudSlot(HudRegion.BottomLeft, 52, 170f, 190f, row: 1));
        if (_silhouette != null) { _silhouette.TickRing = true; _silhouette.Caption = UIText.Key("hud.hull"); }
        _bars = _ui.AddSegmentBar("Armor", () => _model.ArmorFraction, FrameHudModel.Segments);
        if (_bars != null)
        {
            // The barrier: a cyan layer on the bar's top edge with its points and a shield mark left of the bar, drawn only while it
            // stands or is broken (a planted-only barrier shows once planted); hits flash it, a break flashes it brighter.
            _bars.OverlayShown = () => _model.BarrierShown;
            _bars.OverlayProgress = () => _model.BarrierRecovery;
            _bars.OverlayHeight = BarrierHeight;
            _bars.OverlayIcon = FrameHudArt.Shield;
            _bars.Under = () => UIKit.Vitals.Health;
            _bars.Infection = () => UIKit.Vitals.Infection;
            // The bar eases to a hit and numbers its armour points; low armour is red with a white flash for each segment lost,
            // flashing without pause when the suit is about to fail.
            _bars.Colour = Alpha(Primary, CoreAlpha);
            _bars.EaseRate = FrameHudModel.ArmorEase;
            _bars.WarnAt = FrameHudModel.WarnArmor;
            _bars.CriticalAt = FrameHudModel.LowArmor;
            _bars.CriticalPulse = MeterPulse.FlashOnLoss;
            _bars.FailAt = FrameAlerts.FailingArmor;
        }

        _clip = _ui.AddArcGauge("Magazine", () => UIKit.Inventory.Held.Fraction, ArcSide.Left);
        if (_clip != null)
        {
            _clip.Refilling = () => _model.ReloadProgress;
            _clip.EndReadout = () => UIKit.Inventory.HeldText;
            _clip.InnerRing = true;
            _clip.Colour = Primary;
            _clip.SteadyAlpha = ArcSteady;
        }
        _heat = _ui.AddArcGauge("Heat", () => _model.Heat, ArcSide.Right);
        if (_heat != null)
        {
            _heat.Colour = Primary;
            _heat.SteadyAlpha = ArcSteady;
            _heat.Critical = () => _model.Overheated;
            _heat.HigherIsWorse = true;
            _heat.EndIcon = FrameHudArt.Heat;
            _heat.Cells = 24;
            _heat.InnerRing = true;
            _heat.CriticalLabel = UIText.Key("hud.overheat");
        }
        _aim = _ui.AddCrosshairAttachment("Aim");
        if (_aim != null) { _aim.Dot = true; _aim.HideWhileAiming = true; _aim.Ring = false; _aim.Ticks = 4; _aim.TickLength = 8f; _aim.Radius = 18f; }

        // The game's inventory, every slot switchable while piloting; the special weapon slot holds the suit's gun while worn.
        _loadout = _ui.AddLoadout("Loadout");
        if (_loadout != null)
        {
            _loadout.Title = UIText.Key("hud.loadout");
            _loadout.AmmoSource = SuitGunAmmo;
        }
        // One row centred right above the armour bar: the zone, the heading and the altitude.
        _readings = _ui.AddLabel("Readings", new HudSlot(HudRegion.TopCenter, ReadingsPriority, 480f, 28f),
            () => UIKit.Location.Zone, () => UIKit.Location.Heading, () => UIKit.Location.Altitude);
        if (_readings != null) { _readings.Inline = true; _readings.Align = HudAlign.Centre; }

        _special = _ui.AddSkillSlot("Special", () => _model.SpecialRemaining, 1f, FrameKeys.Special.Name, new HudSlot(HudRegion.BottomLeft, 51, 90f, 112f, row: 1));
        if (_special != null) _special.Progress = () => _model.SpecialProgress;
        _rockets = _ui.AddSkillSlot("Rockets", () => _model.RocketRemaining, 1f, FrameKeys.Rockets.Name, new HudSlot(HudRegion.BottomLeft, 50, 90f, 112f, row: 1));
        if (_rockets != null) { _rockets.Icon = FrameHudArt.Rockets; _rockets.Label = UIText.Key("hud.rockets"); }
        // The heavy object of a carrying suit: a skill like the others (V), greyed while nothing is carried.
        _carry = _ui.AddSkillSlot("Carry", () => 0f, 1f, FrameKeys.Special.Name, new HudSlot(HudRegion.BottomLeft, 49, 90f, 112f, row: 1));
        if (_carry != null) { _carry.Icon = FrameHudArt.Carry; _carry.Count = () => _carryState == null ? 0 : 1; }
        _lamp = _ui.AddKeyHint("Lamp", InputAction.Flashlight, UIText.Key("hud.lamp"), new HudSlot(HudRegion.BottomLeft, 45, 160f, 30f, row: 2));
        if (_lamp != null) { _lamp.Icon = FrameHudArt.Lamp; _lamp.FadeWhenFamiliar(); }
        _exit = _ui.AddKeyHint("Exit", FrameKeys.Exit, UIText.Key("hud.exit"), new HudSlot(HudRegion.BottomLeft, 44, 160f, 30f, row: 2));
        if (_exit != null) { _exit.Icon = FrameHudArt.Exit; _exit.HoldSeconds = LocalPilot.ExitHoldSeconds; _exit.FadeWhenFamiliar(); }
        // The squad: chamfered rows on a dark plate, segmented health, a pilot's armour as a thin ice line; 16 units clear of LAMP / EXIT.
        _mates = _ui.AddTeammates("Mates", new HudSlot(HudRegion.BottomLeft, 30, 290f, 142f, space: 16f));
        // A pilot's armour as a thin line under the health, on every teammates panel.
        var armour = _ui.AddTeammateTag("Armour", player => FrameWorld.PilotUnit(player.Lookup) is { } unit
            ? new TeammateTag { Bar = Math.Clamp(unit.Record.Armor / Math.Max(1f, unit.Record.Spec.Armor), 0f, 1f) }
            : TeammateTag.None);
        if (armour != null) armour.Colour = UIKitColors.Faded(Primary, 0.6f);
        if (_mates != null)
        {
            _mates.Width = 290f; _mates.RowHeight = 36f; _mates.RowStep = 42f; _mates.Segments = 10;
            _mates.RowBacking = UIKitColors.Faded(UIKitColors.Plate, 0.5f);
            _mates.RowFrame = UIKitColors.Faded(Primary, 0.22f);
            _mates.IconColour = UIKitColors.Faded(Primary, 0.85f);
            _mates.Header = UIText.Key("hud.squad");
            _mates.HeaderIcon = FrameHudArt.Teammate;
            _mates.AmmoWarnColour = UIKitColors.Orange;
            _mates.Icon = player => FrameWorld.PilotUnit(player.Lookup) is { } unit ? FrameHudArt.Mark(unit.Record.Spec.Model) : null;
        }

        // The objectives: a narrow column on a chamfered dark plate with an ice accent bar; small tags, a mid-size objective, a fainter step.
        _objectives = _ui.AddObjectives("Objectives", new HudSlot(HudRegion.TopLeft, 50, 370f, 100f));
        if (_objectives != null)
        {
            _objectives.TagIcon = FrameHudArt.Objective;
            _objectives.Ruler = true;
            _objectives.Width = 330f; _objectives.MainLines = 3;
            _objectives.TagPx = 12f; _objectives.HeaderPx = 12f; _objectives.TextPx = 15f; _objectives.SubPx = 12.5f;
            _objectives.HeaderColour = UIKitColors.Faded(Primary, 0.55f);
            _objectives.TextColour = UIKitColors.Faded(Primary, 0.93f);
            _objectives.SubColour = UIKitColors.Faded(Primary, 0.58f);
            _objectives.Backing = UIKitColors.Faded(UIKitColors.Plate, 0.46f); _objectives.Chamfer = 10f;
            _objectives.AccentBar = UIKitColors.Faded(Primary, 0.42f);
        }
        // The prompt, the scan line and its player marks sit where the game puts its own prompt (read from it), drawn in the cockpit's face.
        _interaction = _ui.AddInteraction("Interaction");
        if (_interaction != null)
        {
            _interaction.KeyBox = true;
            _interaction.ProgressStyle = InteractionProgress.Underline;
            _interaction.AtGamePrompt = true;
            // The message line (reactor steps and codes, scans) keeps the game's own place, not the prompt's stack in mid-screen.
            _interaction.AtGameMessage = true;
        }
        _subtitles = _ui.AddSubtitles("Subtitles");
        _threats = _ui.AddBearingArc("Threats", 24);
        if (_threats != null)
        {
            _threats.AutoEnemies(ThreatRadius, 0.25f);
            _threats.HideInView = true;
            _threats.RearIcon = FrameHudArt.ArrowAround;
            _threats.RearText = UIText.Key("hud.behind");
            // The locator beacon and terminal PING results go round the ring; the teammates' arrows do not.
            _threats.Compass = true;
            _threats.CompassTeammates = false;
        }
        _edge = _ui.AddHitEdge("Hits");
        _visor = _ui.AddVisor("Visor");
        if (_visor != null) _visor.CracksPicture = FrameHudArt.VisorCracks;
        _outline = ForgeVisual.Outlines.AddEnemyOutline(Plugin.Id, "Cockpit");
        _enemyBars = _ui.AddEnemyHealthBars("Cockpit");
        // Numbers, kill marks and the kill confirmation; the cockpit has no hit marker round the crosshair. The numbers are 1.4 times the kit's
        // default 15 px so they read through the visor.
        _hits = _ui.AddHitFeedback("Cockpit", new HitFeedbackStyle { HitMarker = false, NumberPx = 21f });
        _plates = _ui.AddWaypoints("Suit", Markers);
        if (_plates != null)
        {
            _plates.ShowDistance = false;
            _plates.EdgeArrow = false;
            _plates.FadeStart = 25f;
            _plates.FadeEnd = 40f;
            _plates.BarColour = Primary;
            _plates.Above = true;
        }
        _around = _ui.AddWaypoints("Around", 1);
        if (_around != null) _around.ShowDistance = false;

        _parts = new HudWidget?[] { _bars, _readings, _objectives, _special, _rockets, _lamp, _exit, _carry, _silhouette, _mates, _loadout, _threats, _clip, _heat };
        Hide();
        if (_hide == null || _chat == null || _bars == null || _link == null)
            Plugin.Logger.ErrorOnce("Part of the exosuit cockpit could not be registered with ForgeUI (see its log above); the game's HUD stays as it is while piloting.");
    }

    private FrameWords MakeWords()
    {
        var mod = _ui;
        return new FrameWords(key => mod.Text(key));
    }

    private static int Percent(FrameRecord r) => (int)Math.Round(100f * Math.Clamp(r.Armor / Math.Max(1f, r.Spec.Armor), 0f, 1f));

    // ---- the hide-and-restore state ---------------------------------------------------------------------------

    /// <summary>Every frame (and at once where the pilot's state moves): the cockpit takes the phase the local player is in now
    /// (<see cref="CockpitScope.For"/>), and the game's HUD hide and chat move are made to agree with it, taken again (at most once a
    /// second) whenever they are wanted and ForgeUI does not hold them. Nothing is allocated unless something changes.</summary>
    internal void Sync(CockpitPhase phase, float now)
    {
        var before = _scope.Phase;
        if (phase != before)
        {
            var shown = _scope.Set(phase);
            if (_outline != null) _outline.Active = _scope.Phase == CockpitPhase.Worn;
            if (_enemyBars != null) _enemyBars.Active = _scope.Phase == CockpitPhase.Worn;
            if (_hits != null) _hits.Active = _scope.Phase == CockpitPhase.Worn;
            if (!_scope.Cockpit) Parts(false);
            if (!_scope.Visor) _visor?.Hide();
            if (phase is CockpitPhase.Off or CockpitPhase.Boarding) { _onScreen = false; _missingSince = -1f; }
        }
        var want = _scope.HideGameHud;
        if (_hide != null && _hide.Active != want && (!want || now >= _takeAgainAt))
        {
            _hide.Active = want;
            if (_hide.Active != want) NotTaken("game HUD hide", now);
        }
        if (_chat != null && _chat.Active != want && (!want || now >= _takeAgainAt))
        {
            _chat.Active = want;
            if (_chat.Active != want) NotTaken("chat move", now);
        }
    }

    private void NotTaken(string what, float now)
    {
        _takeAgainAt = now + RetrySeconds;
        Plugin.Logger.ErrorOnce("Exosuit cockpit: ForgeUI did not take the " + what + " while " + _scope.Phase + " (its reason is in ForgeUI's log line above); the game's HUD shows with the cockpit and it is asked again every " + ForgeCore.Log.Num(RetrySeconds) + " s.");
    }

    /// <summary>ForgeUI gave the game's HUD back by itself. The next <see cref="Sync"/> decides: a pilot who is not alive or out of the
    /// level is <see cref="CockpitPhase.Suspended"/>; anyone still worn takes it again after <see cref="RetrySeconds"/>.</summary>
    private void KitReleased(string what, GameHudRelease why)
    {
        _takeAgainAt = Time.time + RetrySeconds;
    }

    // ---- boarding ---------------------------------------------------------------------------------------------

    /// <summary>A new boarding: the suit's words, gun name, special and rockets set once; a HUD that failed earlier is retried.</summary>
    internal void Board(FrameRecord r, BootPlan plan)
    {
        _faultUntil = float.NegativeInfinity;
        _takeAgainAt = float.NegativeInfinity;
        _onScreen = false;
        _missingSince = -1f;
        _record = r;
        _plan = plan;
        _link?.Stop();
        _barsRolled = false;
        _bars?.Restart();
        _alerts.Reset();
        _link?.Prepare(FrameBootMessage.Words(_words, Percent(r)));
        if (_silhouette != null) { _silhouette.Center = FrameHudArt.Mark(r.Spec.Model); _silhouette.CenterLabel = NameOf(r); }
        var spec = r.Spec;
        if (_special != null)
        {
            var kind = spec.Boost != null ? FrameSpecialKind.Charge : spec.Siege != null ? FrameSpecialKind.Siege : spec.Scan != null ? FrameSpecialKind.Scan : FrameSpecialKind.None;
            _special.Icon = FrameHudArt.Skill(kind);
            _special.Label = kind switch
            {
                FrameSpecialKind.Charge => UIText.Key("special.charge"),
                FrameSpecialKind.Siege => UIText.Key("special.siege"),
                FrameSpecialKind.Scan => UIText.Key("special.scan"),
                _ => UIText.Empty,
            };
            _special.Cooldown = spec.Boost?.Cooldown ?? 0f;
            _special.Key = kind is FrameSpecialKind.Charge or FrameSpecialKind.Siege ? FrameKeys.Special.Name : UIText.Empty;
        }
        if (_rockets != null && spec.Rockets != null) { _rockets.Cooldown = spec.Rockets.Cooldown; _rockets.Key = FrameKeys.Rockets.Name; }
        if (_bars != null)
        {
            _bars.Overlay = spec.Shield != null ? () => _model.BarrierFraction : null;
            _bars.OverlayReadoutMax = spec.Shield?.Capacity ?? 0f;
            _bars.ReadoutMax = Math.Max(1f, spec.Armor);
        }
        _silhouette?.Reset();
    }

    /// <summary>Boarding: the screen fades to black (<paramref name="black"/> 0..1) as the suit opens, the visor's corners sliding in with it.</summary>
    internal void Entering(float black)
    {
        _power?.Fade(black);
        _slide = black;
        ShowVisor(0f, 0f);
    }

    /// <summary>Worn, <paramref name="boot"/> seconds after the power-on started: the panels over the screen, the parts reporting in under them.</summary>
    internal void Update(FrameRecord r, FrameGunReading gun, float heat, bool overheated, float now, float lastScanAt, float rocketsReadyAt, float boot)
    {
        _slide = 1f;
        _power?.PowerOn(boot);
        if (now < _faultUntil) return;
        try
        {
            _model.Update(r, gun, heat, overheated, now, lastScanAt, rocketsReadyAt);
            var alert = _alerts.Next(r);
            if (alert != FrameAlert.None)
            {
                Banner(FrameAlerts.Text(alert, _words), FrameAlerts.Tone(alert), FrameAlerts.Seconds(alert));
            }
            Held(UIKit.Vitals.Held);
            Feed(r);
            _link?.Play(BootPlan.MessageAt(boot));
            // The bars report in with their readout rolling up from 0.
            var barsShown = _scope.Cockpit && _plan.Shown(CockpitPart.Bars, boot);
            if (barsShown && !_barsRolled)
            {
                _barsRolled = true;
                if (boot < _plan.Settled) { _bars?.RollUp(); _visor?.Sweep(0.6f); }
            }
            // During the power-on each part comes online with ForgeUI's reveal; a skipped or finished one just shows.
            var revealing = boot < _plan.Settled;
            for (var p = 0; p < CockpitParts.Count; p++) Part((CockpitPart)p, _scope.Cockpit && _plan.Shown((CockpitPart)p, boot), revealing);
            Show(_interaction, _scope.Cockpit);
            Show(_subtitles, _scope.Cockpit);
            ShowVisor(VisorMath.CracksFor(_model.ArmorFraction), overheated ? 1f : Math.Clamp((_model.Heat - 0.7f) / 0.3f, 0f, 1f));
            if (_scope.Cockpit && boot >= _plan.Settled) CheckOnScreen(boot, now);
        }
        catch (Exception error) { Fault(error); }
    }

    /// <summary>Once the power-on has settled, twice a second until it is: every part the cockpit shows must be on screen (placed by
    /// ForgeUI). A part that is not is logged with ForgeUI's reason (its layout state, not built), and logged again when the whole cockpit
    /// is on screen, so a cockpit missing in a player's game says why in the log.</summary>
    private void CheckOnScreen(float boot, float now)
    {
        if (_onScreen) return;
        if (now < _nextScreenCheck) return;
        _nextScreenCheck = now + 0.5f;
        var missing = 0;
        for (var i = 0; i < _parts.Length; i++)
            if (_parts[i] is { Visible: true } w && w.HudElement.State is not (LayoutState.Placed or LayoutState.Free)) missing++;
        if (missing == 0)
        {
            _onScreen = true;

            return;
        }
        if (_missingSince >= 0f) return;
        _missingSince = now;
        var b = new System.Text.StringBuilder();
        for (var i = 0; i < _parts.Length; i++)
        {
            if (_parts[i] is not { Visible: true } w || w.HudElement.State is LayoutState.Placed or LayoutState.Free) continue;
            b.Append(b.Length == 0 ? "" : ", ").Append(w.Id).Append(' ').Append(w.HudElement.State).Append(w.HudElement.Root == null ? " (not built)" : "");
        }
        Plugin.Logger.ErrorOnce("Exosuit cockpit: " + missing + " of its parts are shown by the suit but not on screen " + ForgeCore.Log.Num(boot)
            + " s into the power-on: " + b + " (ForgeUI's HUD line above says why: a game screen open, no HUD camera, the screen not measured, a failed element).");
    }

    /// <summary>Leaving, <paramref name="down"/> seconds in: the power-down over the last picture; each part goes dark with the panel over it.</summary>
    internal void Leaving(float down, float now)
    {
        _power?.PowerDown(down);
        if (now < _faultUntil) return;
        try
        {
            for (var p = 0; p < CockpitParts.Count; p++) Part((CockpitPart)p, _scope.Cockpit && CockpitParts.ShownLeaving((CockpitPart)p, down));
            Show(_interaction, false);
            Show(_subtitles, false);
            _aim?.ClearMark();
            _link?.PowerDown(down);
            if (down >= FrameBootScreen.DownDark) _visor?.Hide();
        }
        catch (Exception error) { Fault(error); }
    }

    /// <summary>Everything off (released, or the level ended).</summary>
    internal void Hide()
    {
        _power?.Hide();
        _alerts.Reset();
        Held(PlayerHold.None);
        _record = null;
        Parts(false);
        _visor?.Hide();
        _edge?.Clear();
        _aim?.ClearMark();
        _link?.Stop();
        _plates?.Clear();
        _around?.Clear();
    }

    /// <summary>A banner over everything else while something holds the pilot (grabbed or stunned), gone the moment it lets go.</summary>
    private void Held(PlayerHold held)
    {
        if (held == _held) return;
        _held = held;
        if (held == PlayerHold.None) { _heldBanner?.Hide(); return; }
        _heldBanner?.Show(held == PlayerHold.Grabbed ? _words.Grabbed : _words.Stunned, NoticeTone.Alert, HeldSeconds);
    }

    // ---- the parts ------------------------------------------------------------------------------------------

    private static void Show(HudWidget? widget, bool visible, bool reveal = false)
    {
        if (widget == null || widget.Visible == visible) return;
        widget.Visible = visible;
        if (visible && reveal) widget.Reveal();
    }

    private void Parts(bool shown)
    {
        for (var p = 0; p < CockpitParts.Count; p++) Part((CockpitPart)p, shown);
        Show(_interaction, shown);
        Show(_subtitles, shown);
    }

    private void Part(CockpitPart part, bool shown, bool reveal = false)
    {
        var r = _record;
        switch (part)
        {
            case CockpitPart.Bars: Show(_bars, shown, reveal); Show(_readings, shown, reveal); break;
            case CockpitPart.Objectives: Show(_objectives, shown, reveal); break;
            case CockpitPart.Skills:
                Show(_special, shown && r != null && (r.Spec.Boost != null || r.Spec.Siege != null || r.Spec.Scan != null), reveal);
                Show(_rockets, shown && r?.Spec.Rockets != null, reveal);
                Show(_lamp, shown, reveal);
                Show(_exit, shown, reveal);
                Show(_carry, shown && r?.Spec.CarryHeavy == true, reveal);
                Show(_silhouette, shown, reveal);
                break;
            case CockpitPart.Teammates: Show(_mates, shown, reveal); break;
            case CockpitPart.Weapon: Show(_loadout, shown, reveal); break;
            case CockpitPart.Radar: Show(_threats, shown, reveal); break;
            case CockpitPart.Arcs:
                Show(_clip, shown, reveal);
                Show(_heat, shown, reveal);
                if (_aim != null && _aim.Visible != shown) _aim.Visible = shown;
                break;
        }
    }

    /// <summary>The values the templates do not read themselves: the magazine's cells and number, the special's state, the siege's tightened aim.</summary>
    private void Feed(FrameRecord r)
    {
        if (_clip != null)
        {
            var held = UIKit.Inventory.Held;
            var cells = held.Limited && held.Size is > 0 and <= 60 ? held.Size : 0;
            if (_clip.Cells != cells) _clip.Cells = cells;
        }
        if (_aim != null)
        {
            _aim.Tighten = r.Sieged ? 1f : 0f;
        }
        if (_special != null) _special.Active = _model.SpecialActive;
    }

    /// <summary>The special slot's row while the suit gun stands in it: the suit gun's own rounds (reserve with the rounds waiting beyond the game's pack), against everything the suit started with; null for every other row.</summary>
    private LoadoutAmmo? SuitGunAmmo(InventorySlot slot)
    {
        if (slot != InventorySlot.GearSpecial) return null;
        if (MechGun.Incoming(_model.StockCapacity) is { } next) return new LoadoutAmmo(next.Clip, next.Reserve, next.ClipSize, next.ReserveMax);
        if (!MechGun.Active) return null;
        var size = MechGun.ClipSize;
        return new LoadoutAmmo(_model.Clip, _model.Reserve, size, Math.Max(_model.StockCapacity - size, _model.Reserve));
    }

    private static Color Alpha(Color c, float a) => new(c.r, c.g, c.b, a);

    /// <summary>The rocket salvo's landing point under the crosshair (dim when out of range); null clears it.</summary>
    internal void RocketMark(Vector3? landing, bool inRange)
    {
        if (_aim == null) return;
        if (landing is { } at && _scope.Cockpit) _aim.Mark(at, inRange);
        else _aim.ClearMark();
    }

    /// <summary>A hit the host reported. On the armour: the edge glows on its side and the damage disc's layer on that side flashes and
    /// darkens. On the barrier: only the visor's interference (the barrier line shows the loss).</summary>
    internal void Hit(float sourceX, float sourceZ, float damage, bool shielded)
    {
        var r = _record;
        if (r == null || !_scope.Cockpit) return;
        var strength = Math.Clamp(damage / Math.Max(1f, r.Spec.Armor * 0.1f), 0.25f, 1f);
        if (float.IsNaN(sourceX)) { if (!shielded) _edge?.Flash(180f, strength * 0.5f); return; }
        var azimuth = UIKit.Bearing(new Vector3(sourceX, r.Y + 1f, sourceZ));
        if (float.IsNaN(azimuth)) return;
        _visor?.Interfere(strength * 0.6f);
        if (shielded) return;
        _edge?.Flash(azimuth, strength);
        _silhouette?.Hit(azimuth, damage, 1f - _model.ArmorFraction);
    }

    /// <summary>The visor: corner brackets sliding in with boarding, cracks by armour, heat glow along the bottom edge. No side shade
    /// and no struts: the whole view stays clear.</summary>
    private void ShowVisor(float cracks, float heat)
    {
        if (_visor == null || !_scope.Visor) return;
        _visor.Show(new VisorState
        {
            Strength = 0f,
            Struts = 0f,
            Cracks = cracks,
            Heat = heat,
            StrutSlide = _slide,
        });
    }

    // ---- the suits in the world -----------------------------------------------------------------------------

    /// <summary>Every frame: a name plate over each idle suit (badge, model, armour, in the suit's colour; fades with distance), its lowest
    /// edge <see cref="FrameVisual.NameGap"/> over that model's top. A worn suit has none: the game's own marker over its pilot sits there
    /// instead (<see cref="FrameVisual"/>).</summary>
    internal void WorldMarkers()
    {
        if (_plates == null) return;
        var all = FrameWorld.All;
        _plates.Clear();
        for (var i = 0; i < all.Count; i++)
        {
            var unit = all[i];
            var r = unit.Record;
            if (unit.Visual == null || r.State != FrameState.Idle) continue;
            if (_plates.Count == _plates.Capacity) break;
            _plates.Add(new Waypoint(unit.Visual.NameAt, NameOf(r))
            {
                Icon = FrameHudArt.Mark(r.Spec.Model),
                Row = true,
                Colour = FrameVisual.NameColour(r.Spec),
                Bar = Math.Clamp(r.Armor / Math.Max(1f, r.Spec.Armor), 0f, 1f),
            });
        }
    }

    /// <summary>The arrow to walk round to the suit's back (null hides it).</summary>
    internal void Around(Vector3? entrance)
    {
        if (_around == null) return;
        if (entrance is not { } at) { _around.Remove(_aroundPoint); return; }
        var place = at + Vec.Up * 1.2f;
        if (!_around.Move(_aroundPoint, place))
            _aroundPoint = _around.Add(new Waypoint(place, UIText.Key("board.behind")) { Icon = FrameHudArt.ArrowAround });
    }

    // ---- messages ---------------------------------------------------------------------------------------------

    /// <summary>Big text in the middle of the screen, for full-attention moments only.</summary>
    internal void Banner(string text, FrameAlertTone tone, float seconds) => _banner?.Show(text, Tone(tone), seconds);

    /// <summary>A line in ForgeUI's notice list.</summary>
    internal void Notice(string text) => _notices?.Push(text);

    /// <summary>A refused request: the control it belongs to flashes red and comes back to full view; the reason is a notice.</summary>
    internal void Refused(FrameRequestKind kind, string reason)
    {
        if (_scope.Cockpit)
        {
            switch (kind)
            {
                case FrameRequestKind.Boost or FrameRequestKind.Siege: _special?.Deny(); break;
                case FrameRequestKind.Rockets: _rockets?.Deny(); break;
                case FrameRequestKind.Exit: _exit?.Attention(); break;
            }
        }
        Notice(reason);
    }

    /// <summary>The heavy object of a suit that carries it on the back (<see cref="ForgeCore.CarrySlot.InHands"/>): null greys the skill; true (in the hands)
    /// names the ability key as the one that puts it on the back, false (on the back) as the one that takes it off.</summary>
    internal void CarryHint(bool? inHands)
    {
        var key = FrameKeys.Special.Name;
        if (_carry == null || inHands == _carryState && key == _carryKey) return;
        _carryKey = key;
        _carryState = inHands;
        _carry.Key = key;
        _carry.Label = UIText.Key(inHands == false ? "hud.carryTake" : "hud.carryStow");
    }

    /// <summary>The exit key's hold (its key box fills); true the frame the hold completes.</summary>
    internal bool ExitHold(bool down, float dt) => _exit != null && _scope.Cockpit && _exit.Hold(down, dt);

    internal void LampUsed() => _lamp?.Used();

    private static NoticeTone Tone(FrameAlertTone tone) => tone switch
    {
        FrameAlertTone.Good => NoticeTone.Good,
        FrameAlertTone.Warn => NoticeTone.Warn,
        FrameAlertTone.Alert => NoticeTone.Alert,
        _ => NoticeTone.Info,
    };

    /// <summary>A failed update: logged (each distinct failure, repeats counted), the parts off for <see cref="RetrySeconds"/>, then the
    /// cockpit is drawn again, so one bad frame never takes the cockpit away for the rest of the ride.</summary>
    private void Fault(Exception error)
    {
        _faultUntil = Time.time + RetrySeconds;
        Plugin.Logger.ErrorOnce("The exosuit cockpit failed (" + _scope.Phase + "); its parts are off and drawn again in " + ForgeCore.Log.Num(RetrySeconds) + " s: " + error);
        try
        {
            Parts(false);
            _link?.Stop();
        }
        catch (Exception again) { Plugin.Logger.ErrorOnce("The exosuit cockpit could not put its parts away after a failure: " + again); }
    }

    // ---- prewarm ----------------------------------------------------------------------------------------------

    /// <summary>Registers the suit's two screen layers with ForgeUI (the power-on panels and the barrier frame), so its prewarm builds them
    /// with the hit glow and the visor.</summary>
    internal void RegisterScreens()
    {
        if (_power == null)
        {
            var panels = new float[FrameBootScreen.PanelCount][];
            for (var p = 0; p < panels.Length; p++)
            {
                var n = FrameBootScreen.VertexCount(p);
                var corners = new float[n * 2];
                for (var k = 0; k < n; k++) { corners[k * 2] = FrameBootScreen.X(p, k); corners[k * 2 + 1] = FrameBootScreen.Y(p, k); }
                panels[p] = corners;
            }
            var seams = new float[FrameBootScreen.SeamCount][];
            for (var i = 0; i < seams.Length; i++)
            {
                FrameBootScreen.Seam(i, out var x0, out var y0, out var x1, out var y1);
                seams[i] = new[] { x0, y0, x1, y1 };
            }
            // Cyan-white for the picture coming up, split into red, green and blue copies while it glitches (the torn bands and noise
            // bars in those colours); above an ordinary exclusive layer, so nothing else covers the picture while the suit seals and powers on.
            _power = _ui.AddPowerOn("PowerOn", new PowerOnSpec
            {
                Panels = panels,
                Seams = seams,
                Timeline = BootPlan.Default.Timeline,
                Priority = 50,
                Tint = new Color(0.80f, 0.94f, 1f, 1f),
                ChannelLeft = new Color(1f, 0.18f, 0.32f, 1f),
                ChannelMiddle = new Color(0.30f, 1f, 0.55f, 1f),
                ChannelRight = new Color(0.20f, 0.50f, 1f, 1f),
            });
        }
    }

    /// <summary>Builds and compiles the next of the suit's screen layers, drawn once off screen; its ID, or null when all are ready.</summary>
    internal string? WarmNextLayer() => _ui.WarmNextLayer();

    /// <summary>Before anyone boards: draws the power-on message once at its fullest (hidden), so its pieces and glyphs exist. False
    /// while the game's HUD is not built yet.</summary>
    internal bool WarmHud(FrameRecord sample, float now)
    {
        if (now < _faultUntil || _link == null) return true;
        try
        {
            _link.Prepare(FrameBootMessage.Words(_words, Percent(sample)));
            return _link.Warm();
        }
        catch (Exception error) { Fault(error); return true; }
    }
}
