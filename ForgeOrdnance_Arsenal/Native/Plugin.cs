using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ForgeAudio;
using ForgeOrdnance;
using GTFO.API;
using HarmonyLib;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

[BepInPlugin(Id, "Forge Ordnance Arsenal", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.0")]
[BepInDependency("NAinfini.ForgeOrdnance", ">=1.0.1")]
[BepInDependency(ForgeUI.UIKit.PluginId, ">=1.0.0")]
[BepInDependency(AudioKit.PluginId, ">=1.0.0")]
[BepInDependency("NAinfini.ForgeVisual", ">=1.0.0")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeOrdnanceArsenal";
    internal const string Version = "1.0.1"; // package-version: NAinfini-ForgeOrdnance_Arsenal

    /// <summary>This mod's ForgeCore log: errors are the only log lines, and `ErrorOnce` writes each exact text once.</summary>
    internal static readonly ForgeCore.ModLog Logger = ForgeCore.Log.For(Id);

    public override void Load()
    {
        ArsenalKeys.Register();
        // Registration is independent: a refused type must not strand the other registered type without its driver.
        var strike = OrdnanceApi.RegisterPayload(StrikeSpec.TypeName, new StrikePayload());
        var heavy = OrdnanceApi.RegisterPayload(HeavyWeaponSpec.TypeName, new HeavyWeaponPayload());
        if (!strike || !heavy)
            Logger.ErrorOnce("Forge Ordnance refused Arsenal payload type(s): " + (!strike ? StrikeSpec.TypeName + " " : "")
                + (!heavy ? HeavyWeaponSpec.TypeName : "") + "; see its registration error. Accepted types remain available.");
        var deployable = OrdnanceApi.RegisterPayload(DeployableSpec.TypeName, new DeployablePayload());
        if (!deployable)
            Logger.ErrorOnce("Forge Ordnance refused Arsenal payload type " + DeployableSpec.TypeName + "; see its registration error. The standing devices are not available.");
        if (!strike && !heavy && !deployable) return;
        ArsenalNetwork.Register();
        var harmony = new Harmony(Id);
        try
        {
            ArsenalAudio.Register();
            // Before ForgeOrdnance reads the definition files: the turret definitions name looks registered here.
            ArsenalArt.Register();
            // Heavy weapons are named once the definitions are read, so one already in a backpack after a save reload has its name.
            OrdnanceApi.DefinitionsReady += ArsenalText.NameHeavyWeapons;
            harmony.PatchAll(typeof(Plugin).Assembly);
            ForgeCore.Items.Spawned += DeployableActions.OnItemSpawned;
            HeavyWeapons.Subscribe();
            LauncherFire.Subscribe();
            WithheldBullet.Subscribe();
            LauncherReload.Subscribe();
            HomingFire.Subscribe();
            ForgeCore.Frame.OnUpdate(Id, Tick);
            ForgeCore.Frame.OnQuit(Id, ArsenalAudio.Dispose);
            LevelAPI.OnBuildDone += ArsenalArt.Load;
            LevelAPI.OnEnterLevel += StrikeBursts.EnterLevel;
            ForgeCore.GameEvents.LevelEnded += () => { StrikeBursts.LeaveLevel(); Void("level cleanup"); };
            ForgeCore.GameEvents.CheckpointReloaded += () => { StrikeVisuals.CloseReplay(); Void("checkpoint reload"); };
            ForgeCore.GameEvents.MasterChanged += HostChanged;
            // A migration can end with the same master, which MasterChanged does not report; ForgeOrdnance voids its own state then as well.
            ForgeCore.GameEvents.MasterMigrated += HostChanged;
        }
        catch (Exception error)
        {
            ArsenalAudio.Dispose();
            Logger.ErrorOnce("Forge Ordnance Arsenal initialization failed: " + error);
            harmony.UnpatchSelf();
            throw;
        }
    }

    public override bool Unload() => false;

    /// <summary>Everything in flight is dropped: shells, zones, beams, racks (not at a host change); a lent weapon goes back (except on level exit,
    /// where the game resets the inventory itself).</summary>
    private static void Void(string why)
    {
        try
        {
            ArsenalNetwork.Clear();
            Strikes.Clear();
            StrikeVisuals.Clear();
            HeavyWeapons.Clear(why);
            Grenades.Clear();
            HomingFire.Clear();
            ArsenalTurrets.Clear();
            Deployables.Clear();
        }
        catch (Exception error) { Logger.ErrorOnce("Clearing the Arsenal state failed (" + why + "): " + error); }
    }

    /// <summary>The host changed on this peer too (a client that stays a client sees the new master as well): everything in flight is void, except the racks
    /// still lying in their pods (<see cref="HeavyWeapons.Clear"/>).</summary>
    internal static void HostChanged() => Void("host change");

    internal static void Tick()
    {
        var now = Time.time;
        ArsenalNetwork.Pump();
        if (ForgeCore.Session.IsHost) Strikes.Tick(now);
        StrikeVisuals.Tick(now);
        HeavyWeapons.Tick(now);
        Grenades.Tick(now);
        Deployables.Tick(now);
        ArcTrails.Tick();
    }
}
