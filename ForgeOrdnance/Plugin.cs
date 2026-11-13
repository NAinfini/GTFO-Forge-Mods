using System.Linq;
using System;
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
[BepInDependency("NAinfini.ForgeCore", ">=1.0.0")]
// MTFO marks the game as modded (achievements and public matchmaking off) and provides the CustomPath the definitions live in.
[BepInDependency("com.dak.MTFO")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeOrdnance";
    internal const string Version = "1.0.1"; // package-version: NAinfini-ForgeOrdnance
    private static OrdnanceController? _controller;
    private static OrdnanceHud? _hud;
    /// <summary>The content parts that loaded; the steps of one that did not are left out everywhere.</summary>
    private static bool _arsenal, _frames;
    private static bool _effectsRead;

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
            SupplyHoldPatch.Subscribe();
            EmptyPackGuard.Subscribe();
            ForgeCore.GameHooks.PickupItemState.After((call, _) => SupplyPickupSound.Changed(call.Old, call.New, call.Recall));
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
        Guard("Loading the call-ins' art", OrdnanceAssets.Load);
        if (!_effectsRead) { _effectsRead = true; ReadEffects(); }
        if (_arsenal)
        {
            Guard("Starting the level's strike effects", StrikeVisuals.BeginLevel);
            Guard("Loading Arsenal's art", ArsenalArt.Load);
        }
        if (_frames)
        {
            Guard("Loading the exosuits' art", FrameAssets.LevelBuilt);
            Guard("Loading the pilot's body", PilotBody.Load);
        }
    }

    /// <summary>The three parts' effect libraries (each its own bundle and slot table), read at one moment: when the first level has
    /// been built, ahead of anything that plays an effect. A library that cannot be read is reported once and its moments draw
    /// nothing (a strike keeps its sprite effects).</summary>
    private static void ReadEffects()
    {
        Guard("Reading the call-ins' library effects", OrdnanceFxSlots.Load);
        if (_arsenal) Guard("Reading Arsenal's library effects", ArsenalFxSlots.Load);
        if (_frames) Guard("Reading the exosuits' library effects", FramesFxSlots.Load);
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

    /// <summary>Host: the mod's one checkpoint copy, the carrier's slot book and after it the suits as they stand.</summary>
    private static void CheckpointReached()
    {
        if (!ForgeCore.Session.IsHost) return;
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
        var now = Time.time;
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
        var now = Time.time;
        try { TurretLooks.LateTick(now); }
        catch (Exception error) { Logger.ErrorOnce("ordnance-late-frame-failed", "the turret looks' frame failed: " + error); }
        if (!_frames) return;
        try { FrameWorld.LateTick(now); }
        catch (Exception error) { Logger.ErrorOnce("ordnance-frames-late-frame-failed", "the exosuits' late frame failed: " + error); }
    }
}
