using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ForgeOrdnance.Arsenal;
using ForgeOrdnance.Frames;
using GTFO.API;
using HarmonyLib;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>The one plugin of the mod's three parts: the call-in carrier (<c>Carrier/</c>), then its two content sets, Arsenal and
/// the exosuits (<c>Frames/</c>), whose payload types, category and looks are registered here before the definition files are read.
/// It is the only place that names all three: each of the game's moments has one handler here, which runs the parts' steps in order.</summary>
[BepInPlugin(Id, "Forge Ordnance", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.3")]
// MTFO marks the game as modded (achievements and public matchmaking off) and provides the CustomPath the definitions live in.
[BepInDependency("com.dak.MTFO", ">=4.6.3")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeOrdnance";
    internal const string Version = "1.0.3"; // package-version: NAinfini-ForgeOrdnance
    private static OrdnanceController? _controller;
    private static OrdnanceHud? _hud;
    /// <summary>The content parts that loaded; the steps of one that did not are left out everywhere.</summary>
    private static bool _arsenal, _frames;
    /// <summary>Whether a level is built (until it is left), what of the level's art each part has loaded in it, whether the effect library has been
    /// read and which parts have found their moments in it.</summary>
    private static bool _levelBuilt, _arsenalLevel, _framesLevel, _fx, _arsenalFx, _framesFx;

    /// <summary>This mod's ForgeCore log.</summary>
    internal static ForgeCore.ModLog Logger = null!;
    internal static OrdnanceSession Session { get; } = new();

    /// <summary>The folder the mod is installed in. Each part's files are in its own folder under it: <c>Carrier</c>, <c>Arsenal</c>, <c>Frames</c>.</summary>
    internal static readonly string Folder = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);
        ForgeCore.Settings.Adopt(Id, Config);
        OrdnanceKeys.Register();
        LoadoutStore.Load();

        OrdnanceApi.RegisterBuiltIn(ItemPayload.TypeName, new ItemPayload());
        OrdnanceApi.RegisterBuiltIn(GearPayload.TypeName, new GearPayload());
        OrdnanceNetwork.Register(Session);
        LobbyRules.Register();

        var harmony = new Harmony(Id);
        try
        {
            harmony.PatchAll(typeof(Plugin).Assembly);
            // One handler of a pickup's state change: hold it in a shut pod, check a placed pack, play the pickup sound.
            ForgeCore.GameHooks.PickupItemState.After((call, _) =>
            {
                SupplyHoldPatch.Changed(call.Item, call.New, call.Recall);
                EmptyPackGuard.Changed(call.Item, call.New, call.Old, call.Recall);
                SupplyPickupSound.Changed(call.Old, call.New, call.Recall);
            });
            BeaconLookHook.Subscribe();
            BallThrowHook.Subscribe();
            TurretLookHook.Subscribe();
            SummonedTurretStrength.Subscribe();
            SummonedTurretToolSlot.Subscribe();
            SentryMarkerHook.Subscribe();
            _controller = new OrdnanceController(Session);
            Session.CallInProgress = () => _controller.CallInProgress;
            _hud = new OrdnanceHud(_controller);
            PartSounds.Carrier = PartSounds.Load(Id, "Carrier");
            OrdnanceLoadout.Register();
            LobbySync.Register();
            // The mod's one handler of each of the game's moments: each runs the carrier's steps, then Arsenal's, then the exosuits'.
            ForgeCore.Frame.OnUpdate(Id, Tick);
            ForgeCore.Frame.OnLateUpdate(Id, LateTick);
            ForgeCore.Frame.OnQuit(Id, PartSounds.Shutdown);
            ForgeCore.Items.Spawned += ItemSpawned;
            GameDataAPI.OnGameDataInitialized += GameDataReady;
            LevelAPI.OnBuildDone += LevelBuilt;
            LevelAPI.OnEnterLevel += EnterLevel;
            ForgeCore.GameEvents.LevelEnded += LeaveLevel;
            ForgeCore.GameEvents.CheckpointReached += CheckpointReached;
            ForgeCore.GameEvents.CheckpointReloaded += CheckpointReloaded;
            ForgeCore.GameEvents.MasterChanged += HostChanged;
            // A migration can end with the same master, which MasterChanged does not report.
            ForgeCore.GameEvents.MasterMigrated += MigrationCompleted;
            // A pick that arrives once the level is built (a late joiner), and a call-in no pick listed, load their art then.
            OrdnanceApi.PickedParametersChanged += () => { if (_levelBuilt) PrepareLevel(); };
            Session.BeaconStarted += (_, _, definition) => Arriving(definition);
            Session.BeaconArrived += (_, definition) => Arriving(definition);
        }
        catch (Exception error)
        {
            Logger.ErrorOnce("Forge Ordnance initialization failed: " + error);
            harmony.UnpatchSelf();
            throw;
        }

        // The content parts, each on its own: one that fails leaves the carrier and the other part working, and none of its steps run.
        try { LoadArsenal(); _arsenal = true; }
        catch (Exception error)
        {
            PartSounds.Unload(ref PartSounds.Arsenal);
            Logger.ErrorOnce("Arsenal initialization failed: " + error);
        }
        try { LoadFrames(); _frames = true; }
        catch (Exception error)
        {
            PartSounds.Unload(ref PartSounds.Frames);
            Logger.ErrorOnce("Exosuit initialization failed: " + error);
        }
    }

    private static void LoadArsenal()
    {
        ArsenalKeys.Register();
        StrikePayload.Type = OrdnanceApi.RegisterBuiltIn(StrikeSpec.TypeName, new StrikePayload());
        HeavyWeaponPayload.Type = OrdnanceApi.RegisterBuiltIn(HeavyWeaponSpec.TypeName, new HeavyWeaponPayload());
        DeployablePayload.Type = OrdnanceApi.RegisterBuiltIn(DeployableSpec.TypeName, new DeployablePayload());
        ArsenalNetwork.Register();
        PartSounds.Arsenal = PartSounds.Load(Messages.ArsenalId, "Arsenal");
        // The grenade launcher's explosion is drawn under ForgeCore's shared name from this mod's own copy (Vfx/HeavyBlast/).
        ForgeVisual.HeavyBlast.Register(Id, "Vfx/HeavyBlast/forge-visual-fx.bundle", "Vfx/HeavyBlast/fx-slots.forge");
        // Before the definition files are read: the turret definitions name looks registered here.
        ArsenalArt.Register();
        HeavyWeapons.Subscribe();
        LauncherFire.Subscribe();
        WithheldBullet.Subscribe();
        LauncherReload.Subscribe();
        HomingFire.Subscribe();
    }

    private static void LoadFrames()
    {
        FrameKeys.Register();
        // The exosuit tab: between Weapon and Offensive, in the colour of the suit beacons.
        var exosuitTab = ForgeUI.UIKit.Mod(Id);
        OrdnanceApi.Categories.Register("Exosuit", () => exosuitTab.Text("category.Exosuit"), 3, 0xFFC23D);
        FramesPayload.Type = OrdnanceApi.RegisterBuiltIn(FrameSpecParser.TypeName, new FramesPayload());
        FrameNetwork.Register();
        PilotBullet.Subscribe();
        ForgeCore.PlayerHits.Redirect(Id, PilotDamage.Redirect);
        ForgeCore.PlayerHits.GateInfection(Id, PilotInfection.Allow);
        PartSounds.Frames = PartSounds.Load(Messages.FramesId, "Frames");
        LocalPilot.RegisterHud();
        FrameAssets.Register();
    }

    // ---- the game's moments, one handler each ----------------------------------------------------------------

    private static void GameDataReady()
    {
        Guard("Adding the summoning ball to the game's items", BeaconItem.Register);
        Guard("Reading the call-in definitions", OrdnanceContent.Load);
        // Heavy weapons are named once the definitions are read, so one already in a backpack after a save reload has its name.
        if (_arsenal) Guard("Naming the heavy weapons", ArsenalText.NameHeavyWeapons);
    }

    private static void LevelBuilt()
    {
        _levelBuilt = true;
        if (_arsenal) Guard("Starting the level's strike effects", StrikeVisuals.BeginLevel);
        PrepareLevel();
    }

    /// <summary>What this level can call in is loaded while it is built: every call-in <see cref="OrdnanceApi.PickedParameters"/> lists (the
    /// players' picks, the level's drops and loadouts, every <c>Callable: false</c> definition). Nothing of a part none of them uses is read.</summary>
    private static void PrepareLevel()
    {
        foreach (var type in OrdnanceApi.TypeNames)
        {
            var picked = new List<object>();
            LobbySync.PickedParameters(type, picked);
            if (picked.Count > 0) Prepare(picked);
        }
    }

    /// <summary>A call-in is presented on this peer: what it needs is loaded now if the level's picks did not list it.</summary>
    private static void Arriving(OrdnanceDefinition definition)
    {
        if (_levelBuilt) Prepare(new List<object> { definition.Parameters });
    }

    /// <summary>Loads what call-ins with these parameters draw, each thing once: the carrier's pod, shell, ball and the mod's one
    /// effect library for any call-in, and a turret look's model; Arsenal's models and sprite sheets for its strikes, heavy weapons, devices
    /// and turret looks; a suit's art and the pilot's body for a suit. Each part finds its moments in the library the first time it is
    /// prepared; a library that cannot be read is reported once and its moments draw nothing (a strike keeps its sprite effects).</summary>
    private static void Prepare(List<object> parameters)
    {
        Guard("Loading the call-ins' art", OrdnanceAssets.Load);
        ReadOnce(ref _fx, "Reading the library effects", OrdnanceFxSlots.Load);
        var arsenalModels = new List<ForgeVisual.ModelHandle?>();
        var arsenal = false;
        foreach (var p in parameters)
            switch (p)
            {
                case GearParameters { Look.Length: > 0 } gear when OrdnanceTurrets.TryGet(gear.Look, out var look, out _):
                    _ = look.Ready;
                    arsenal |= gear.Look.StartsWith(ArsenalArt.Pack + ":", StringComparison.Ordinal);
                    break;
                case StrikeSpec:
                    arsenalModels.AddRange(ArsenalArt.ShellLooks);
                    arsenal = true;
                    break;
                case HeavyWeaponSpec heavy:
                    arsenalModels.Add(ArsenalArt.Weapon(heavy.Model));
                    arsenalModels.Add(ArsenalArt.Rocket);
                    arsenalModels.Add(ArsenalArt.Grenade);
                    arsenal = true;
                    break;
                case DeployableSpec device:
                    arsenalModels.Add(ForgeVisual.Models.Get(device.Head));
                    arsenalModels.Add(device.Munition.Length > 0 ? ForgeVisual.Models.Get(device.Munition) : ArsenalArt.Bomblet);
                    arsenal = true;
                    break;
                case FrameSpec suit when _frames:
                    if (!_framesLevel) { _framesLevel = true; Guard("Loading the exosuits' art", FrameAssets.LevelBuilt); }
                    ReadOnce(ref _framesFx, "Finding the exosuits' library effects", FramesFxSlots.Load);
                    Guard("Loading an exosuit's art", () => FrameAssets.Prepare(suit));
                    break;
            }
        if (!arsenal || !_arsenal) return;
        var first = !_arsenalLevel;
        _arsenalLevel = true;
        ReadOnce(ref _arsenalFx, "Finding Arsenal's library effects", ArsenalFxSlots.Load);
        Guard("Loading Arsenal's art", () => ArsenalArt.Load(arsenalModels, first));
    }

    private static void ReadOnce(ref bool read, string what, Action step)
    {
        if (read) return;
        read = true;
        Guard(what, step);
    }

    private static void EnterLevel()
    {
        Guard("Entering the level", Session.EnterLevel);
        if (_arsenal) Guard("Entering the level (strike effects)", StrikeBursts.EnterLevel);
        if (_frames) Guard("Entering the level (exosuits)", FrameWorld.LevelEntered);
    }

    /// <summary>Each step on its own: one that throws must not leave the others' level state for the next level.</summary>
    private static void LeaveLevel()
    {
        _levelBuilt = _arsenalLevel = _framesLevel = false;
        Guard("Leaving the level (session)", Session.LeaveLevel);
        Guard("Leaving the level (sentries)", GearPayload.Clear);
        Guard("Leaving the level (beacon looks)", BeaconLook.Clear);
        Guard("Leaving the level (turret looks)", TurretLooks.Clear);
        Guard("Leaving the level (turret sounds)", OrdnanceTurrets.Clear);
        Guard("Leaving the level (controller)", () => _controller?.LevelLeft());
        if (_arsenal)
        {
            Guard("Leaving the level (strike effects)", StrikeBursts.LeaveLevel);
            VoidArsenal(ArsenalVoid.LevelCleanup);
        }
        if (_frames) Guard("Exosuit level cleanup", FrameWorld.LevelCleanup);
    }

    /// <summary>Host: the mod's one checkpoint copy, the carrier's slot book and after it the suits as they stand; the heavy weapon supply
    /// points are kept by the host itself.</summary>
    private static void CheckpointReached()
    {
        if (!ForgeCore.Session.IsHost) return;
        if (_arsenal) Guard("Heavy weapon supply checkpoint", HeavyWeapons.CheckpointReached);
        var suits = Array.Empty<byte>();
        if (_frames) Guard("Exosuit checkpoint", () => suits = FrameWorld.CaptureCheckpoint());
        Guard("Checkpoint", () => Session.PublishCheckpoint(suits));
    }

    private static void CheckpointReloaded()
    {
        Guard("Checkpoint reload", Session.CheckpointReloaded);
        if (_arsenal)
        {
            Guard("Checkpoint reload (strike beams)", StrikeVisuals.CloseReplay);
            VoidArsenal(ArsenalVoid.CheckpointReload);
        }
        if (_frames) Guard("Exosuit checkpoint reload", FrameWorld.CheckpointReloaded);
    }

    /// <summary>The host changed, on this peer too (a client that stays a client sees the new master as well).</summary>
    private static void HostChanged() => AuthorityChanged(Session.HostChanged);

    private static void MigrationCompleted() => AuthorityChanged(Session.MigrationCompleted);

    /// <summary>The carrier goes first: it drops the states the old host kept, and the suits send theirs again after it, in the same call.</summary>
    private static void AuthorityChanged(Action carrier)
    {
        Guard("Host change", carrier);
        // Everything of Arsenal's in flight is void, except the racks still lying in their pods.
        if (_arsenal) VoidArsenal(ArsenalVoid.HostChange);
        if (_frames) Guard("Exosuit host change", FrameWorld.AuthorityChanged);
    }

    private static void ItemSpawned(pItemSpawnData data, ItemReplicator replicator)
    {
        BeaconSpawnHook.OnSpawned(data, replicator);
        if (!_arsenal) return;
        try { DeployableActions.OnItemSpawned(data, replicator); }
        catch (Exception error) { Logger.ErrorOnce("ordnance-deployable-spawn-failed", "a deployable's spawn handler failed: " + error); }
    }

    /// <summary>Everything of Arsenal's in flight is dropped: shells, zones, beams, racks (not at a host change); a lent weapon goes back
    /// (except on level exit, where the game resets the inventory itself).</summary>
    private static void VoidArsenal(ArsenalVoid why) => Guard("Clearing the Arsenal state (" + why + ")", () =>
    {
        Strikes.Clear();
        StrikeVisuals.Clear();
        HeavyWeapons.Clear(why);
        Grenades.Clear();
        HomingFire.Clear();
        Deployables.Clear();
    });

    /// <summary>A step whose failure is logged and stops there, so the steps after it still run.</summary>
    private static void Guard(string what, Action step)
    {
        try { step(); }
        catch (Exception error) { Logger.ErrorOnce(what + " failed: " + error); }
    }

    public override bool Unload() => false;

    /// <summary>Every frame, from ForgeCore's <c>Frame.OnUpdate</c>: the time and who hosts are read once and handed down. Each part
    /// on its own, so a part that throws stops only its own frame.</summary>
    private static void Tick()
    {
        var now = ForgeCore.Frame.GameTime;
        var host = ForgeCore.Session.IsHost;
        try
        {
            LobbyRules.Tick();
            LobbySync.Tick();
            _controller?.Update(now);
            Deliveries.Tick(now);
            OrdnanceArrival.Tick(now);
            OrdnanceClaims.Tick(now);
            BeaconLook.Tick(now);
            if (_controller != null) _hud?.Update(_controller, Session, now);
        }
        catch (Exception error) { Logger.ErrorOnce("ordnance-frame-failed", "the call-ins' frame failed: " + error); }
        if (_arsenal)
        {
            try
            {
                if (host) Strikes.Tick(now);
                StrikeVisuals.Tick(now);
                HeavyWeapons.Tick(now);
                Grenades.Tick(now);
                Deployables.Tick(now);
                ArcTrails.Tick(now);
            }
            catch (Exception error) { Logger.ErrorOnce("ordnance-arsenal-frame-failed", "Arsenal's frame failed: " + error); }
        }
        if (_frames)
        {
            try { FrameWorld.Tick(now, host); }
            catch (Exception error) { Logger.ErrorOnce("ordnance-frames-frame-failed", "the exosuits' frame failed: " + error); }
        }
    }

    /// <summary>After the game's animation, from ForgeCore's <c>Frame.OnLateUpdate</c>.</summary>
    private static void LateTick()
    {
        var now = ForgeCore.Frame.GameTime;
        try { TurretLooks.LateTick(now); }
        catch (Exception error) { Logger.ErrorOnce("ordnance-late-frame-failed", "the turret looks' frame failed: " + error); }
        if (!_frames) return;
        try { FrameWorld.LateTick(now); }
        catch (Exception error) { Logger.ErrorOnce("ordnance-frames-late-frame-failed", "the exosuits' late frame failed: " + error); }
    }
}
