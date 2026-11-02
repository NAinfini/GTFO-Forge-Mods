using System;
using System.Linq;
using System.Text.Json;
using ForgeOrdnance;
using ForgeVisual;
using GameData;
using Gear;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnanceArsenal.Tests;

internal static class NativeChecks
{
    internal static readonly System.Collections.Generic.List<string> LogLines = new();
    private static string LauncherMarker => HeavyModels.Marker(HeavyModels.Hash("Arsenal:grenade-launcher"));
    internal static void Run(Action<bool,string> check)
    {
        HeavyModels.Register("Arsenal:grenade-launcher");
        var spec = HeavyWeaponSpec.Parse(JsonDocument.Parse("{\"Model\":\"Arsenal:grenade-launcher\",\"Grenade\":{},\"Gear\":2,\"Chargeup\":0}").RootElement);
        HeavyParameters parameters = new(spec, 2);
        void Install(GearIDRange range)
        {
            var weapon = new BulletWeapon { gameObject=new(), Owner=PlayerManager.Local };
            PlayerBackpackManager.LocalBackpack.Item = new() { GearIDRange=range, Instance=weapon };
            if (PlayerManager.Local != null) PlayerManager.Local.Inventory.WieldedItem=weapon;
        }
        void Setup(bool pod = false)
        {
            ArsenalKeys.Register();
            Plugin.ExtraLights=new(true);
            ArsenalArt.Bundle=null;
            ArsenalAudio.PlayedAt.Clear();
            ForgeCore.SpecialSlot.Local.Clear();
            Void("level cleanup");
            StrikeVisuals.Clear();
            StrikeVisuals.BeginLevel();
            Strikes.Clear();
            Grenades.Clear();
            ArsenalNetwork.Register(); ArsenalNetwork.Clear();
            Physics.Hit=false;
            Physics.Hits=Array.Empty<Collider>();
            GuiManager.InteractionLayer=new();
            GTFO.API.NetworkAPI.BroadcastShots.Clear();
            GTFO.API.NetworkAPI.RequestedShots.Clear();
            GTFO.API.NetworkAPI.BroadcastBursts.Clear();
            OrdnanceClaims.Clear();
            OrdnanceNetwork.Reset();
            ArsenalAudio.ExplosionAt.Clear();
            DamageUtil.Explosions=0;
            Time.time=2;
            SNet.IsMaster=true;
            SNet.LocalPlayer=new() { Lookup=7 };
            SNet.Master=SNet.LocalPlayer;
            var local=new PlayerAgent { Owner=SNet.LocalPlayer, IsLocallyOwned=true, gameObject=new(), EyePosition=new(0,.4f,-1) };
            local.FPSCamera.transform.position=local.EyePosition;
            PlayerManager.Local=local;
            PlayerManager.PlayerAgentsInLevel.Clear();
            PlayerManager.PlayerAgentsInLevel.Add(local);
            PlayerBackpackManager.Backpacks.Clear();
            PlayerBackpackManager.LocalBackpack=new();
            PlayerBackpackManager.Backpacks[7]=PlayerBackpackManager.LocalBackpack;
            PlayerBackpackManager.Equip=Install;
            Install(new("1"));
            PlayerBackpackManager.LocalBackpack.Item!.Instance!.SetCurrentClip(3);
            PlayerBackpackManager.LocalBackpack.AmmoStorage.SpecialAmmo.AmmoInPack=23;
            PlayerBackpackManager.EquipCalls=0;
            GameDataBlockBase<PlayerOfflineGearDataBlock>.Blocks[2]=new() { GearJSON="2" };
            GameDataBlockBase<GearCategoryDataBlock>.Blocks[2]=new();
            GameDataBlockBase<ArchetypeDataBlock>.Blocks[2]=new();
            ForgeCore.Gear.Status=ForgeCore.GearAssetStatus.Ready;
            InputMapper.GetButtonDown=(_,_)=>false;
            Input.Drop=false;
            FocusStateManager.CurrentState=eFocusState.FPS;
            OrdnanceDelivery.Pod=pod?new():null;
            OrdnanceDelivery.Emptied=0;
            OrdnanceApi.Sent.Clear();
            OrdnanceApi.Cleared=0;
            GameObject.Created.Clear();
            StrikeEffects.Clouds=StrikeEffects.BeamsMade=StrikeEffects.BeamsReleased=0;
            Models.Dresses.Clear();
            Models.Looks.Clear();
            Fx.Played.Clear();
            StrikeBursts.Reset();
        }
        void Rack(uint id=1)
        {
            if(OrdnanceDelivery.Pod is {} podNow) { podNow.SummonerLookup=7; podNow.ThrowId=id; }
            HeavyWeapons.ShowRack(new() { SummonerLookup=7,ThrowId=id,Parameters=parameters });
            HeavyWeapons.HostArrived(new() { SummonerLookup=7,ThrowId=id,Parameters=parameters });
            OrdnanceDelivery.Pod?.Open();
            Time.time+=2;
        }
        void Frame(float now) { OrdnanceClaims.Tick(now); HeavyWeapons.Tick(now); }
        void Void(string why) { OrdnanceClaims.Clear(); HeavyWeapons.Clear(why); }
        bool IsRack(GameObject g) => g.name is "ForgeOrdnanceArsenal.Rack" or "ForgeOrdnance.BayHolder";
        int ReservedCount() => OrdnanceNetwork.States.Count(s => s.State == ClaimStateMessage.Reserved);
        void Take()
        {
            InputMapper.GetButtonDown=(_,_)=>true;
            Frame(Time.time);
            InputMapper.GetButtonDown=(_,_)=>false;
        }
        void Drop()
        {
            Input.Drop=true;
            Frame(Time.time);
            Time.time+=.1f;
            Frame(Time.time);
        }
        void Restored(string label)
        {
            var pack=PlayerBackpackManager.LocalBackpack;
            check(pack.Item?.GearIDRange?.GetChecksum()==1 && pack.Item.Instance!.GetCurrentClip()==3 && pack.AmmoStorage.SpecialAmmo.AmmoInPack==23,label);
        }

        foreach (var id in new uint[] { 1, 2 })
        {
            Setup(true);
            var twoBay = OrdnanceDelivery.Pod!;
            twoBay.WeaponBayCount = 2;
            twoBay.WeaponAnchors = new[] { new GameObject("bay0").transform, new GameObject("bay1").transform };
            twoBay.WeaponBays = new[] {
                new OrdnancePodBay(new Vector3(.22f,.065f,0), default, new Vector3(.91f,1.87f,.41f)),
                new OrdnancePodBay(new Vector3(-.22f,.065f,0), default, new Vector3(.91f,1.87f,.41f)) };
            Rack(id);
            var slot = (int)(id % 2);
            var live = GameObject.Created.Last(g=>IsRack(g));
            check(twoBay.LastAnchorIndex == slot && twoBay.LastBayIndex == slot && live.transform.Parent == twoBay.WeaponAnchors[slot],
                "native: live heavy delivery uses its selected custom bay and parents the true-size weapon to its anchor, slot=" + slot);
            Void("level cleanup");
            HeavyWeapons.OnData(new() { SummonerLookup=7, ThrowId=id, Tag=(byte)DataTag.Rack,
                Data=Wire.Rack(new RackRecord(spec,2,0,0,0,0,"replay")), Replayed=true });
            var replay = GameObject.Created.Last(g=>IsRack(g));
            check(twoBay.LastAnchorIndex == slot && twoBay.LastBayIndex == slot && replay.transform.Parent == twoBay.WeaponAnchors[slot],
                "native: retained rack uses replayed pod's indexed bay instead of built-in model measurements, slot=" + slot);
            twoBay.Leave();
            check(replay.WasCollected, "native: custom two-bay rack is removed with the replayed pod, slot=" + slot);
        }

        foreach (var withPodArt in new[] { false, true })
        {
            Setup(withPodArt);
            OrdnanceApi.RetainState = false;
            // Execute can publish before the framework's OnArrived presentation callback.
            HeavyWeapons.HostArrived(new() { SummonerLookup=7,ThrowId=1,Parameters=parameters });
            HeavyWeapons.ShowRack(new() { SummonerLookup=7,ThrowId=1,Parameters=parameters });
            OrdnanceDelivery.Pod?.Open();
            Time.time += 2;
            Take();
            check(PlayerBackpackManager.EquipCalls == 0 && OrdnanceDelivery.Emptied == 1
                && !GameObject.Created.Any(g=>IsRack(g) && !g.WasCollected),
                "native: failed rack retention cancels the later arrival and cannot grant a loan, pod=" + withPodArt);
            OrdnanceApi.RetainState = true;
        }
        Setup(); Rack();
        OrdnanceApi.RetainState = false;
        HeavyWeapons.HostArrived(new() { SummonerLookup=7,ThrowId=1,Parameters=parameters });
        Take();
        check(PlayerBackpackManager.EquipCalls == 0
            && !GameObject.Created.Any(g=>IsRack(g) && !g.WasCollected),
            "native: failed retention retracts an already visible rack without consuming a weapon");
        OrdnanceApi.RetainState = true;

        Setup(); Rack();
        ForgeCore.Gear.Status=ForgeCore.GearAssetStatus.Loading;
        Take();
        check(PlayerBackpackManager.EquipCalls==0 && OrdnanceDelivery.Emptied==0,"native: missing effects cannot mutate inventory or consume rack");
        ForgeCore.Gear.Status=ForgeCore.GearAssetStatus.Ready;
        Time.time+=2; Take();
        check(HeavyWeapons.ActiveLauncher!=null && OrdnanceDelivery.Emptied==1,"native: readiness refusal leaves rack available for a successful retry");

        var reloading = PlayerBackpackManager.LocalBackpack.Item!.Instance!;
        var reloadPostfix = typeof(LauncherReload).GetMethod("Postfix",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        ArsenalAudio.PlayedAt.Clear();
        reloading.IsReloading = false;
        reloadPostfix.Invoke(null, new object[] { reloading, true, false });
        reloading.IsReloading = true;
        reloadPostfix.Invoke(null, new object[] { reloading, true, false });
        reloadPostfix.Invoke(null, new object[] { reloading, true, true });
        check(ArsenalAudio.PlayedAt.Count(c => c == ArsenalAudio.Cue.GrenadeReload) == 1,
            "native: launcher reload sound follows one successful false-to-true reload transition");

        Setup(); Rack();
        PlayerBackpackManager.Equip=range=>
        {
            if(range.GetChecksum()==2)
            {
                PlayerBackpackManager.LocalBackpack.Item=new() { GearIDRange=range,IsLoaded=false };
                throw new InvalidOperationException("assembly failed");
            }
            Install(range);
        };
        Take();
        Restored("native: partial install exception restores original gear and exact ammunition");
        check(OrdnanceDelivery.Emptied==0 && !OrdnanceNetwork.States.Any(s=>s.State==ClaimStateMessage.Committed),"native: failed equip does not finalize or empty pod");
        PlayerBackpackManager.Equip=Install;
        Time.time+=2; Take();
        check(HeavyWeapons.ActiveLauncher!=null,"native: rollback releases reservation for retry");

        Setup(); Rack();
        PlayerBackpackManager.Equip=range=>
        {
            Install(range);
            if(range.GetChecksum()==2) PlayerBackpackManager.LocalBackpack.Item!.IsLoaded=false;
        };
        Take();
        check(OrdnanceDelivery.Emptied==0,"native: half-built item is not an installed weapon");
        Time.time+=7; Frame(Time.time); Time.time+=.1f; Frame(Time.time);
        Restored("native: asynchronous assembly timeout rolls back and restores ammunition");
        check(OrdnanceDelivery.Emptied==0,"native: timeout preserves rack contents");

        Setup(); Rack();
        var remote=new PlayerAgent { Owner=new() {Lookup=8},gameObject=new(),EyePosition=new(50,0,0) };
        PlayerManager.PlayerAgentsInLevel.Add(remote);
        PlayerBackpackManager.Backpacks[8]=new() { Item=new() { GearIDRange=new("1"),Instance=new BulletWeapon {gameObject=new()} } };
        var request=new ClaimMessage {Thrower=7,ThrowId=1,Attempt=10};
        OrdnanceNetwork.Reset();
        OrdnanceClaims.OnRequest(8,request);
        check(!OrdnanceNetwork.States.Any(),"native: host rejects a requester outside interaction range");
        remote.EyePosition=new(0,.4f,0); remote.Alive=false;
        OrdnanceClaims.OnRequest(8,request);
        check(!OrdnanceNetwork.States.Any(),"native: host rejects a downed requester");
        remote.Alive=true;
        PlayerBackpackManager.Backpacks[8].Item!.GearIDRange!.PublicGearName=LauncherMarker;
        OrdnanceClaims.OnRequest(8,request);
        check(!OrdnanceNetwork.States.Any(),"native: host rejects an existing borrowed weapon, including after migration");
        PlayerBackpackManager.Backpacks[8].Item!.GearIDRange!.PublicGearName="";
        OrdnanceClaims.OnRequest(8,request);
        check(ReservedCount()==1 && OrdnanceDelivery.Emptied==0,"native: eligible requester receives only a reservation");
        Time.time+=9; Frame(Time.time);
        check(OrdnanceNetwork.States.Any(s=>s.State==ClaimStateMessage.Cancelled),"native: abandoned reservation expires without consuming rack");

        Setup(); Rack(); Take();
        Void("host change");
        check(HeavyWeapons.ActiveLauncher!=null,"native: installed loan survives host change");
        Drop(); Restored("native: surviving loan returns original gear and ammo after migration");
        Rack(2); Take();
        check(HeavyWeapons.ActiveLauncher!=null,"native: returned player can borrow again after migration");

        Setup(true); Rack(); Take(); Drop(); Rack(2); Take();
        check(HeavyWeapons.ActiveLauncher!=null,"native: a returned loan does not block another rack while the empty pod lingers");

        Setup(); Rack(); Take(); ForgeCore.SpecialSlot.Local.CaptureCheckpoint(); Drop();
        ForgeCore.Checkpoint.Recalling=true;
        ForgeCore.Checkpoint.Recalling=false;
        ForgeCore.SpecialSlot.Local.RecallCheckpoint(); Void("checkpoint reload");
        PlayerBackpackManager.LocalBackpack.Item=null;
        var calls=PlayerBackpackManager.EquipCalls;
        Frame(Time.time);
        check(PlayerBackpackManager.EquipCalls==calls,"native: recall waits for backpack reconstruction");
        Install(new("2") { PublicGearName=LauncherMarker });
        PlayerBackpackManager.LocalBackpack.Item!.IsLoaded=false;
        Frame(Time.time);
        check(PlayerBackpackManager.EquipCalls==calls,"native: recall waits for native gear assembly");
        PlayerBackpackManager.LocalBackpack.Item.IsLoaded=true;
        Frame(Time.time); Frame(Time.time);
        Restored("native: checkpoint containing a previously returned loan restores original equipment");
        ForgeCore.Checkpoint.Recalling=true; ForgeCore.SpecialSlot.Local.RecallCheckpoint(); Void("checkpoint reload");
        Install(new("2") { PublicGearName=LauncherMarker });
        calls=PlayerBackpackManager.EquipCalls;
        Frame(Time.time);
        check(PlayerBackpackManager.EquipCalls==calls,"native: recall never equips before native completion barrier");
        ForgeCore.Checkpoint.Recalling=false; Frame(Time.time); Frame(Time.time);
        Restored("native: repeated checkpoint recalls preserve restoration data");

        Setup(); ForgeCore.SpecialSlot.Local.CaptureCheckpoint(); Rack(); Take();
        ForgeCore.Checkpoint.Recalling=true; ForgeCore.SpecialSlot.Local.RecallCheckpoint(); Void("checkpoint reload");
        Install(new("1")); PlayerBackpackManager.LocalBackpack.AmmoStorage.SpecialAmmo.AmmoInPack=77;
        calls=PlayerBackpackManager.EquipCalls;
        ForgeCore.Checkpoint.Recalling=false; Frame(Time.time);
        check(PlayerBackpackManager.EquipCalls==calls && PlayerBackpackManager.LocalBackpack.AmmoStorage.SpecialAmmo.AmmoInPack==77,"native: ordinary checkpoint gear keeps native recall ammunition");

        Setup(true);
        var pod=OrdnanceDelivery.Pod!;
        pod.Open(); // a late joiner's replayed pod is already open
        HeavyWeapons.OnData(new() {SummonerLookup=7,ThrowId=1,Tag=(byte)DataTag.Rack,Data=Wire.Rack(new RackRecord(spec,2,0,0,0,0,"replay")),Replayed=true});
        var rackLook=GameObject.Created.Last(g=>IsRack(g));
        pod.Leave();
        check(rackLook.WasCollected,"native: replayed rack subscribes to delivery departure");

        foreach(var kind in new[]{StrikeKind.Gas,StrikeKind.Stasis})
        foreach(var beforeTick in new[]{true,false})
        {
            Setup();
            var shell=new OrdnancePayloadData {SummonerLookup=7,ThrowId=2,Tag=(byte)DataTag.Shell,
                Data=Wire.Shell(new ShellRecord(StrikePlan.Look(kind),kind,0,0,0,10,1,0))};
            StrikeVisuals.Receive(shell);
            var prop=GameObject.Created.Last();
            if(!beforeTick){Time.time+=1;StrikeVisuals.Tick(Time.time);}
            StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=2,Tag=(byte)DataTag.Zone,Data=Wire.Zone(new ZoneRecord(kind,0,0,0,3,5))});
            Time.time+=1; StrikeVisuals.Tick(Time.time);
            check(!prop.WasCollected,"native: zone owns its prop with zone " + (beforeTick?"before":"after") + " visual tick ("+kind+")");
            StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=2,Tag=(byte)DataTag.ZoneEnd});
            check(prop.WasCollected,"native: zone end destroys its prop regardless of tick order ("+kind+")");
        }
        foreach(var kind in new[]{StrikeKind.Gas,StrikeKind.Stasis})
        foreach(var expiry in new[]{false,true})
        {
            Setup();
            StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=2,Tag=(byte)DataTag.Zone,Data=Wire.Zone(new ZoneRecord(kind,0,0,0,3,1))});
            StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=2,Tag=(byte)DataTag.Shell,
                Data=Wire.Shell(new ShellRecord(StrikePlan.Look(kind),kind,0,0,0,10,2,0))});
            var fallingProp=GameObject.Created.Last();
            if(expiry){Time.time+=1.1f; StrikeVisuals.Tick(Time.time);}
            else StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=2,Tag=(byte)DataTag.ZoneEnd});
            check(fallingProp.WasCollected,"native: zone " + (expiry?"expiry":"end") + " destroys even an unlanded prop ("+kind+")");
        }
        Setup();
        StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=2,Tag=(byte)DataTag.Zone,Data=Wire.Zone(new ZoneRecord(StrikeKind.Gas,0,0,0,3,2)),Replayed=true});
        var replayProp=GameObject.Created.Last(g=>g.name=="ForgeOrdnanceArsenal.Canister");
        Time.time+=3; StrikeVisuals.Tick(Time.time);
        check(replayProp.WasCollected,"native: replayed zone expiry destroys its prop");
        Setup();
        SNet.IsMaster=false;
        HeavyWeapons.OnData(new() {SummonerLookup=7,ThrowId=5,Tag=(byte)DataTag.Rack,Data=Wire.Rack(new RackRecord(spec,2,0,0,0,0,"migration")),Replayed=true});
        var migrationRack=GameObject.Created.Last(g=>IsRack(g));
        StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=6,Tag=(byte)DataTag.Beam,Data=Wire.Beam(new BeamRecord(Guid.NewGuid(),0,10,0,0,0,0,true)),Replayed=true});
        var beamRoot=GameObject.Created.Last(g=>g.name=="ForgeOrdnanceArsenal.Beam");
        Grenades.OnShot(7,new GrenadeShot {Shooter=8,OX=0,OY=0,OZ=0,VZ=32});
        var grenadeRoot=GameObject.Created.Last();
        var clearMethod=typeof(Strikes).GetField("Beams",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        var beamSpec=StrikeSpec.Parse(JsonDocument.Parse("{\"Kind\":\"Beam\"}").RootElement);
        Strikes.Start(new() {SummonerLookup=7,ThrowId=8,Parameters=beamSpec});
        SNet.Master=new() {Lookup=99};
        Plugin.HostChanged(); // what ForgeCore's master-changed event runs
        check(migrationRack.WasCollected && beamRoot.WasCollected && grenadeRoot.WasCollected && ((System.Collections.ICollection)clearMethod.GetValue(null)!).Count==0,
            "native: a peer that remains a client clears beams, racks, flights and host work on master identity change");
        // Grenade authority: run the real receive/fire paths, with observable host broadcasts and explosions.
        GrenadeShot Shot(uint sequence=1) => new() {Shooter=7,Loan=HeavyWeapons.ActiveLauncherIdentity,Sequence=sequence,
            OX=PlayerManager.Local!.EyePosition.x,OY=PlayerManager.Local.EyePosition.y,OZ=PlayerManager.Local.EyePosition.z,VZ=1};
        void Reject(GrenadeShot shot,string label,ulong sender=7)
        {
            var sent=GTFO.API.NetworkAPI.BroadcastShots.Count;
            Grenades.OnShot(sender,shot);
            check(GTFO.API.NetworkAPI.BroadcastShots.Count==sent,label);
        }
        Setup();
        Reject(Shot(),"native: no loan cannot request a host grenade");
        Rack(); Take();
        var first=Shot(); first.Damage=float.NaN; first.Radius=20000; first.OX+=1;
        Grenades.OnShot(7,first);
        var accepted=GTFO.API.NetworkAPI.BroadcastShots.Single();
        check(accepted.Damage==spec.Grenade!.Damage && accepted.Radius==spec.Grenade!.Radius && accepted.VZ==spec.Grenade!.Speed && accepted.VY==1.5f,
            "native: host replaces arbitrary client damage/radius/speed from its loan definition");
        check(accepted.OX==PlayerManager.Local!.EyePosition.x && accepted.OY==PlayerManager.Local.EyePosition.y && accepted.OZ==PlayerManager.Local.EyePosition.z,
            "native: a client origin within tolerance cannot move the grenade beyond the host-observed eye");
        Reject(first,"native: replayed shot identity is rejected");
        var invalid=Shot(2); invalid.Loan=Guid.NewGuid(); Reject(invalid,"native: different loan identity rejected");
        invalid=Shot(2); invalid.Shooter=8; Reject(invalid,"native: shooter spoof rejected");
        Reject(Shot(2),"native: non-player sender rejected",99);
        Time.time+=.5f;
        foreach(var value in new[]{float.NaN,float.PositiveInfinity,100f})
        {
            invalid=Shot(2); invalid.OX=value; Reject(invalid,"native: invalid or distant host origin rejected: "+value);
            invalid=Shot(2); invalid.VX=value; Reject(invalid,"native: malformed direction rejected: "+value);
        }
        invalid=Shot(2); invalid.VZ=0; Reject(invalid,"native: zero aim rejected");
        var wielded=PlayerManager.Local!.Inventory.WieldedItem;
        PlayerManager.Local.Inventory.WieldedItem=null;
        Reject(Shot(2),"native: holstered launcher cannot fire");
        PlayerManager.Local.Inventory.WieldedItem=wielded;
        PlayerManager.Local.Alive=false;
        Reject(Shot(2),"native: downed borrower cannot fire");
        PlayerManager.Local.Alive=true;
        var borrowed=PlayerBackpackManager.LocalBackpack.Item!.GearIDRange;
        PlayerBackpackManager.LocalBackpack.Item.GearIDRange=new("1");
        Reject(Shot(2),"native: replaced special gear cannot fire");
        PlayerBackpackManager.LocalBackpack.Item.GearIDRange=borrowed;
        Grenades.OnShot(7,Shot(2));
        check(GTFO.API.NetworkAPI.BroadcastShots.Count==2,"native: rejected requests spend no ammo, cadence or sequence");
        var beforeReturn=Shot(3);
        Drop(); Time.time+=1;
        Reject(beforeReturn,"native: returned loan cannot authorize queued shots");
        Grenades.Tick(Time.time+10);
        check(DamageUtil.Explosions==2 && DamageUtil.Damage==spec.Grenade!.Damage && DamageUtil.Radius==spec.Grenade!.Radius,
            "native: only accepted grenades reach host explosion settlement");
check(ArsenalAudio.PlayedAt.Contains(ArsenalAudio.Cue.GrenadeBurst),"native: a grenade burst plays the package's own burst sound, not the game's");

        Setup();
        parameters=new(HeavyWeaponSpec.Parse(JsonDocument.Parse("{\"Model\":\"Arsenal:grenade-launcher\",\"Grenade\":{},\"Gear\":2,\"AmmoRatio\":0.05}").RootElement),2);
        GameDataBlockBase<ArchetypeDataBlock>.Blocks[2]=new() {CostOfBullet=2,DefaultClipSize=2,ShotDelay=.5f};
        Rack(); Take();
        // Client/native ammo reports cannot replenish the independent grant (2 clip + floor(5 / 2) reserve).
        PlayerBackpackManager.LocalBackpack.AmmoStorage.SpecialAmmo.AmmoInPack=100000;
        for(uint i=1;i<=4;i++){Grenades.OnShot(7,Shot(i));Time.time+=.5f;}
        check(GTFO.API.NetworkAPI.BroadcastShots.Count==4,"native: host budget derives granted reserve/round cost and clip from its definition");
        Reject(Shot(5),"native: forged ammunition refill cannot exceed loan grant");
        var expired=Shot(6);
        ForgeCore.SpecialSlot.Local.RecallCheckpoint(); Void("checkpoint reload");
        Reject(expired,"native: checkpoint revokes launcher authorization");
        parameters=new(spec,2);

        // Factory settings: the host's budget and cadence come from the vanilla block with them laid over it (3 + floor(5 / 1) = 8 rounds, 0.25 s apart).
        Setup();
        parameters=new(HeavyWeaponSpec.Parse(JsonDocument.Parse("{\"Model\":\"Arsenal:grenade-launcher\",\"Grenade\":{},\"Gear\":2,\"AmmoRatio\":0.05,\"Magazine\":3,\"AmmoCost\":1,\"ShotDelay\":0.25}").RootElement),2);
        GameDataBlockBase<ArchetypeDataBlock>.Blocks[2]=new() {CostOfBullet=2,DefaultClipSize=2,ShotDelay=.5f};
        Rack(); Take();
        PlayerBackpackManager.LocalBackpack.AmmoStorage.SpecialAmmo.AmmoInPack=100000;
        for(uint i=1;i<=8;i++){Grenades.OnShot(7,Shot(i));Time.time+=.25f;}
        check(GTFO.API.NetworkAPI.BroadcastShots.Count==8,"native: host budget and cadence use the factory magazine, round cost and shot delay");
        Reject(Shot(9),"native: factory-set budget is exhausted after its rounds");
        parameters=new(spec,2);

        // Factory settings reach the lent instance's own archetype copy, read back; the shared block is untouched.
        Setup();
        parameters=new(HeavyWeaponSpec.Parse(JsonDocument.Parse("{\"Model\":\"Arsenal:grenade-launcher\",\"Grenade\":{},\"Gear\":2,\"Damage\":4.5,\"Pierce\":2,\"Reload\":1.5,\"PrecisionMultiplier\":0.5,\"StaggerMultiplier\":3,\"AmmoCost\":2,\"Magazine\":6,\"Chargeup\":0,\"Cooldown\":0.2,\"FalloffStart\":80,\"FalloffEnd\":200}").RootElement),2);
        Rack(); Take();
        var tuned=((BulletWeapon)PlayerBackpackManager.LocalBackpack.Item!.Instance!).m_archeType!.m_archetypeData;
        check(tuned.Damage==4.5f && tuned.PiercingBullets && tuned.PiercingDamageCountLimit==2 && tuned.DefaultReloadTime==1.5f && tuned.PrecisionDamageMulti==.5f
            && tuned.StaggerDamageMulti==3f && tuned.CostOfBullet==2f && tuned.DefaultClipSize==6 && tuned.SpecialChargetupTime==0f && tuned.SpecialCooldownTime==.2f
            && tuned.ShotDelay==.1f && tuned.DamageFalloff==new Vector2(80f,200f) && BulletWeapon.Stock.DamageFalloff==new Vector2(40f,80f) && ((BulletWeapon)PlayerBackpackManager.LocalBackpack.Item!.Instance!).ArchetypeData == tuned && BulletWeapon.Stock.Damage==9f && !BulletWeapon.Stock.PiercingBullets && BulletWeapon.Stock.DefaultClipSize==10,
            "native: factory settings land on the lent archetype copy (the weapon item holds the same copy), unset ones stay vanilla, the shared block is untouched");
        parameters=new(spec,2);

        Setup(); Rack(); Take();
        var weapon=(BulletWeapon)PlayerBackpackManager.LocalBackpack.Item!.Instance!;
        check(weapon.m_archeType!.m_archetypeData.SpecialChargetupTime==0f && weapon.m_archeType.m_archetypeData.ShotDelay==.1f
            && BulletWeapon.Stock.SpecialChargetupTime==1.5f,"native: lent launcher has no charge-up, keeps its shot delay, and the shared block keeps its charge");
        Grenades.BeforeFire(weapon); Grenades.AfterFire(weapon);
        Grenades.BeforeFire(weapon); Grenades.AfterFire(weapon);
        check(GTFO.API.NetworkAPI.BroadcastShots.Count==1,"native: local host fire uses the same loan/cadence admission as clients");
        var oldLoan=Shot(100);
        Void("host change");
        Reject(oldLoan,"native: host change fails closed for old launcher authority while loan remains returnable");

        Setup(); Rack(); Take();
        var previousIdentity=HeavyWeapons.ActiveLauncherIdentity;
        Drop(); Rack(2); Take();
        check(HeavyWeapons.ActiveLauncherIdentity!=Guid.Empty && HeavyWeapons.ActiveLauncherIdentity!=previousIdentity,
            "native: each committed grant has a distinct host-issued loan identity");
        var previousShot=Shot(); previousShot.Loan=previousIdentity;
        Reject(previousShot,"native: a previous grant identity cannot spend a new loan");

        Setup(); Rack(); Take();
        for(uint sequence=1;sequence<=4;sequence++) Grenades.OnShot(7,Shot(sequence));
        check(GTFO.API.NetworkAPI.BroadcastShots.Count==1,"native: bunched grenade requests do not bypass host fire cadence");
        for(var emitted=2;emitted<=4;emitted++)
        {
            Time.time+=.5f; Grenades.Tick(Time.time);
            check(GTFO.API.NetworkAPI.BroadcastShots.Count==emitted,"native: queued jitter-bunched grenade is emitted at the next native interval");
        }
        Time.time+=10; Grenades.Tick(Time.time);
        check(DamageUtil.Explosions==4 && GTFO.API.NetworkAPI.BroadcastBursts.Count==4,
            "native: all four valid bunched shots eventually settle once on host and broadcast their final positions");
        Setup(); Rack(); Take();
        for(uint sequence=1;sequence<=4;sequence++) Grenades.OnShot(7,Shot(sequence));
        Grenades.Clear();
        Time.time+=10; Grenades.Tick(Time.time);
        check(DamageUtil.Explosions==0 && GTFO.API.NetworkAPI.BroadcastShots.Count==1,
            "native: reset cancels both live and cadence-queued grenades");
        Setup(); Rack(); Take(); Physics.Hit=true;
        var behindWall=Shot(); behindWall.OX+=1;
        Grenades.OnShot(7,behindWall);
        check(DamageUtil.Explosions==1 && GTFO.API.NetworkAPI.BroadcastBursts.Single().X==PlayerManager.Local!.EyePosition.x,
            "native: muzzle obstruction settles at host eye sweep instead of a nearby client point beyond the wall");
        Physics.Hit=false;

        Setup(); Rack();
        var client=new PlayerAgent {Owner=new() {Lookup=8},Alive=true,EyePosition=new(0,.4f,0),gameObject=new()};
        PlayerManager.PlayerAgentsInLevel.Add(client);
        var clientWeapon=new BulletWeapon {Owner=client,gameObject=new()};
        PlayerBackpackManager.Backpacks[8]=new() {Item=new() {GearIDRange=new("1"),Instance=clientWeapon}};
        client.Inventory.WieldedItem=clientWeapon;
        var claimRequest=new ClaimMessage {Thrower=7,ThrowId=1,Attempt=50};
        OrdnanceClaims.OnRequest(8,claimRequest);
        var clientShot=new GrenadeShot {Shooter=8,Loan=Guid.NewGuid(),Sequence=1,OY=.4f,VZ=1,Damage=20000,Radius=20};
        Reject(clientShot,"native: pending reservation cannot authorize a client grenade",8);
        PlayerBackpackManager.Backpacks[8].Item!.GearIDRange=new("2") {PublicGearName=LauncherMarker};
        claimRequest.Action=ClaimMessage.Installed;
        OrdnanceClaims.OnRequest(8,claimRequest);
        var committed=OrdnanceNetwork.States.Last(s=>s.State==ClaimStateMessage.Committed);
        var clientIdentity=committed.Token;
        check(committed.Player==8 && clientIdentity!=Guid.Empty,
            "native: host gives a verified remote borrower its own launcher identity");
        clientShot.Loan=clientIdentity;
        Grenades.OnShot(8,clientShot);
        check(GTFO.API.NetworkAPI.BroadcastShots.Count==1 && GTFO.API.NetworkAPI.BroadcastShots[0].Damage==spec.Grenade!.Damage,
            "native: a real remote loan authorizes only the host definition's grenade");
        var loanNotice=new LoanNotice {Summoner=7,ThrowId=1,Attempt=claimRequest.Attempt+1,Action=LoanAction.Returned};
        HeavyWeapons.OnLoanNotice(8,loanNotice);
        Time.time+=.5f; clientShot.Sequence++;
        Grenades.OnShot(8,clientShot);
        check(GTFO.API.NetworkAPI.BroadcastShots.Count==2,"native: stale return acknowledgement cannot revoke another loan");
        loanNotice.Attempt--; HeavyWeapons.OnLoanNotice(8,loanNotice);
        Time.time+=.5f; clientShot.Sequence++;
        Reject(clientShot,"native: matching remote return revokes its launcher authorization",8);

        Setup(); Rack(); Take();
        SNet.IsMaster=false; SNet.Master=new() {Lookup=8};
        weapon=(BulletWeapon)PlayerBackpackManager.LocalBackpack.Item!.Instance!;
        Grenades.BeforeFire(weapon); Grenades.AfterFire(weapon);
        check(GTFO.API.NetworkAPI.RequestedShots.Count==1 && GTFO.API.NetworkAPI.RequestedShots[0].Loan==HeavyWeapons.ActiveLauncherIdentity
            && GTFO.API.NetworkAPI.RequestedShots[0].VZ==1 && GTFO.API.NetworkAPI.BroadcastShots.Count==0,
            "native: client sends loan identity and unit aim to the host");
        Grenades.Tick(Time.time+10);
        check(DamageUtil.Explosions==0,"native: client prediction never applies explosion damage");

        // Exercise the actual strike producer and replay/admission guards through checkpoint cleanup.
        var payload=new StrikePayload();
        void StartBeam(uint id)
        {
            payload.OnBeaconLanded(new() {SummonerLookup=7,ThrowId=id,Parameters=beamSpec});
            Strikes.Start(new() {SummonerLookup=7,ThrowId=id,Parameters=beamSpec});
            Strikes.Tick(Time.time);
        }
        OrdnancePayloadData LastBeam(uint id) => new() {SummonerLookup=7,ThrowId=id,Tag=(byte)DataTag.Beam,
            Data=OrdnanceApi.Sent.Last(m=>m.Tag==(byte)DataTag.Beam).Data};
        int BeamRoots() => GameObject.Created.Count(g=>g.name=="ForgeOrdnanceArsenal.Beam" && !g.WasCollected);
        Setup(); StartBeam(20);
        var stale=LastBeam(20);
        check(Wire.TryBeam(stale.Data,out var oldBeam) && oldBeam.Generation!=Guid.Empty && BeamRoots()==1,"native: strike producer stamps its beam generation");
        StrikeVisuals.CloseReplay();
        Strikes.Clear(); StrikeVisuals.Clear();
        StrikeVisuals.Receive(stale);
        stale.Replayed=true; StrikeVisuals.Receive(stale);
        check(BeamRoots()==0,"native: queued live and retained beam updates cannot recreate beam after checkpoint reset");
        StartBeam(21);
        var fresh=LastBeam(21);
        check(Wire.TryBeam(fresh.Data,out var newBeam) && newBeam.Generation!=oldBeam.Generation && BeamRoots()==1,"native: fresh post-checkpoint beam uses a new generation and is admitted");
        StrikeVisuals.Receive(stale);
        StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=21,Tag=(byte)DataTag.BeamEnd,Data=oldBeam.Generation.ToByteArray()});
        check(BeamRoots()==1,"native: older generation updates/ends cannot affect a fresh beam");
        StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=21,Tag=(byte)DataTag.BeamEnd,Data=newBeam.Generation.ToByteArray()});
        check(BeamRoots()==0,"native: matching generation ends the fresh beam");

        Setup();
        // No first visual existed when reset occurred; admission still rejects the unseen old generation.
        payload.OnBeaconLanded(new() {SummonerLookup=7,ThrowId=22,Parameters=beamSpec});
        var unseen=new OrdnancePayloadData {SummonerLookup=7,ThrowId=22,Tag=(byte)DataTag.Beam,
            Data=Wire.Beam(new BeamRecord(Guid.NewGuid(),0,10,0,0,0,0,false))};
        StrikeVisuals.CloseReplay();
        Strikes.Clear(); StrikeVisuals.Clear();
        StrikeVisuals.Receive(unseen); unseen.Replayed=true; StrikeVisuals.Receive(unseen);
        check(BeamRoots()==0,"native: reset rejects old beam generation even if its first update was queued");
        Setup();
        unseen.Replayed=false; StrikeVisuals.Receive(unseen);
        check(BeamRoots()==0,"native: unadmitted live call cannot create a beam");
        ArsenalAudio.PlayedAt.Clear();
        unseen.Replayed=true; StrikeVisuals.Receive(unseen);
        check(BeamRoots()==1 && ArsenalAudio.PlayedAt.Contains(ArsenalAudio.Cue.BeamLoop)
            && !ArsenalAudio.PlayedAt.Contains(ArsenalAudio.Cue.BeamStart),
            "native: late joiner restores the current beam loop without replaying its historical start");
        Time.time+=10;
        unseen.Replayed=false; StrikeVisuals.Receive(unseen);
        StrikeVisuals.Tick(Time.time+1.1f);
        check(BeamRoots()==1,"native: late joiner admits live aim updates and refreshes beam lifetime");
        StrikeVisuals.Tick(Time.time+12.1f);
        check(BeamRoots()==0,
            "native: lost BeamEnd expires the visual and loop owner without further host updates");
        unseen.Replayed=true;
        StrikeVisuals.CloseReplay(); StrikeVisuals.Clear(); StrikeVisuals.Receive(unseen);
        check(BeamRoots()==0,"native: late joiner also retires adopted generation at its next checkpoint");
        // Area fire damage reaches each living enemy once through ForgeCore's query (the query itself is tested in ForgeCore).
        Setup();
        Enemies.EnemyAgent Enemy() => new() { gameObject=new() };
        Collider Limb(Enemies.EnemyAgent e)
        {
            var c=new Collider {gameObject=new(),Owner=e};
            c.transform.SetParent(e.transform,false);
            return c;
        }
        var enemy=Enemy(); var limb=Limb(enemy); var limb2=Limb(enemy);
        Physics.Hits=new[] {limb,limb2,limb};
        var burns=enemy.Damage.Burns;
        VanillaCombat.Burn(default,5,10,null);
        check(enemy.Damage.Burns==burns+1,"native: each damage query applies one tick per enemy");

        Setup(); Rack(); Frame(Time.time);
        var prompt=GuiManager.InteractionLayer!.Prompts.Last();
        Frame(Time.time+.01f);
        check(ReferenceEquals(prompt,GuiManager.InteractionLayer.Prompts.Last()),"native: rack prompt text is reused across frames");
        Take(); Rack(2); Frame(Time.time);
        check(GuiManager.InteractionLayer.Prompts.Last()=="Heavy weapon in hand","native: loan phase refreshes rack prompt");

        Setup();
        LogLines.Clear();
        Plugin.Error("performance regression error remains visible");
        check(LogLines.Count==1,"native: errors are written");

        Setup();
        Plugin.ExtraLights.Value=false;
        var lookData=new OrdnancePayloadData {SummonerLookup=7,ThrowId=90,Tag=(byte)DataTag.Zone,
            Data=Wire.Zone(new ZoneRecord(StrikeKind.Gas,0,0,0,5,10))};
        ForgeVisual.PointLights.Held.Clear();
        StrikeVisuals.Receive(lookData);StrikeVisuals.Tick(Time.time);
        check(ForgeVisual.PointLights.Held.Count==0,"native: extra lights off holds no area light");
        lookData.Tag=(byte)DataTag.Shell;lookData.ThrowId=91;
        lookData.Data=Wire.Shell(new ShellRecord(ShellLook.Shell,StrikeKind.Area,0,0,0,10,0,5));
        StrikeVisuals.Receive(lookData);StrikeVisuals.Tick(Time.time);
        check(StrikeBursts.Calls.Contains("blast-Area") && !GameObject.Created.Any(g=>g.name=="ForgeOrdnanceArsenal.Flash"),"native: a landed shell draws its own blast and no longer borrows the game explosion or makes a loose flash light");
        Plugin.ExtraLights.Value=true;lookData.ThrowId=92;
        StrikeVisuals.Receive(lookData);StrikeVisuals.Tick(Time.time);
        check(StrikeBursts.Calls.Count(c=>c=="blast-Area")==2,"native: each landed shell draws one blast whatever the light setting (the pool gates its flash lights)");
        lookData.Tag=(byte)DataTag.Zone;lookData.ThrowId=93;
        lookData.Data=Wire.Zone(new ZoneRecord(StrikeKind.Stasis,0,0,0,5,10));
        StrikeVisuals.Receive(lookData);
        Plugin.ExtraLights.Value=false;
        StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=94,Tag=(byte)DataTag.Beam,Replayed=true,
            Data=Wire.Beam(new BeamRecord(Guid.NewGuid(),0,10,0,0,0,0,true))});
        StrikeVisuals.Tick(Time.time);
        check(ForgeVisual.PointLights.Held.Count==0 && GameObject.Created.Any(g=>g.name=="ForgeOrdnanceArsenal.BeamLoop" && !g.WasCollected),
            "native: extra lights off holds no beam light without skipping beam/audio owner");
        check(StrikeEffects.Clouds==2 && StrikeEffects.BeamsMade==1,"native: each area gets its cloud and ground mark, the beam its look");
        Plugin.ExtraLights.Value=true;StrikeVisuals.Tick(Time.time);
        check(ForgeVisual.PointLights.Held.Count==3,
            "native: enabling extra lights lights both existing areas and the beam (shared effect lights)");
        Plugin.ExtraLights.Value=false;StrikeVisuals.Tick(Time.time);
        check(ForgeVisual.PointLights.Held.Count==0,"native: turning extra lights off gives every light back");
        // Strike effects: what each moment asks of the pooled effects (StrikeFx draws them; these check the calls that feed it).
        Setup(); StrikeVisuals.Clear(); StrikeVisuals.BeginLevel(); StrikeBursts.Reset();
        Plugin.ExtraLights.Value=true;
        OrdnancePayloadData ShellData(StrikeKind kind,uint id,float fall=1f) => new() {SummonerLookup=7,ThrowId=id,Tag=(byte)DataTag.Shell,
            Data=Wire.Shell(new ShellRecord(StrikePlan.Look(kind),kind,0,0,0,12,fall,5))};
        var t0=Time.time;
        StrikeVisuals.Receive(ShellData(StrikeKind.Precision,200));
        check(StrikeBursts.Calls.Contains("trail-Precision") && StrikeBursts.Trails==1,"fx: a falling shell gets a smoke trail");
        Time.time=t0+.5f; StrikeVisuals.Tick(Time.time);
        check(StrikeBursts.Moves==1 && StrikeBursts.EndedTrails==0,"fx: the trail follows the falling shell every frame");
        Time.time=t0+1.01f; StrikeVisuals.Tick(Time.time);
        check(StrikeBursts.EndedTrails==1 && StrikeBursts.Calls.Contains("blast-Precision"),"fx: the landing ends the trail and draws the big blast");

        StrikeBursts.Reset(); t0=Time.time;
        StrikeVisuals.Receive(ShellData(StrikeKind.Cluster,201));
        Time.time=t0+.8f; StrikeVisuals.Tick(Time.time);
        check(!StrikeBursts.Calls.Contains("burst"),"fx: a cluster bomblet falls whole until it nears the floor");
        Time.time=t0+.9f; StrikeVisuals.Tick(Time.time);
        Time.time=t0+.95f; StrikeVisuals.Tick(Time.time);
        check(StrikeBursts.Calls.Count(c=>c=="burst")==1 && MathF.Abs(StrikeBursts.BurstFlights[0]-.1f)<.002f && StrikeBursts.EndedTrails==1,
            "fx: it splits once, 3.2 m up, and its sparklets have the rest of the fall to reach their points");
        Time.time=t0+1.01f; StrikeVisuals.Tick(Time.time);
        check(StrikeBursts.Calls.Contains("blast-Cluster"),"fx: the bomblet's own landing is the small blast");
        StrikeBursts.Reset();
        StrikeVisuals.Receive(ShellData(StrikeKind.Cluster,202,0f)); Time.time+=.01f; StrikeVisuals.Tick(Time.time);
        check(!StrikeBursts.Calls.Contains("burst") && StrikeBursts.Calls.Contains("blast-Cluster"),"fx: an overdue bomblet shows its landing at once, without a burst");
        StrikeBursts.Reset();
        for (var shot=0;shot<3;shot++) StrikeVisuals.Receive(ShellData(StrikeKind.Cluster,210,0f));
        StrikeVisuals.Receive(ShellData(StrikeKind.Cluster,211,0f));
        Time.time+=.01f; StrikeVisuals.Tick(Time.time);
        check(StrikeBursts.Calls.Count(c=>c=="blast-Cluster")==4 && StrikeBursts.Calls.Count(c=>c=="cluster-centre")==2,
            "fx: every bomblet lands with its small blast, but a cluster strike draws its one scorch and light once, whatever its bomblet count");

        StrikeBursts.Reset();
        StrikeVisuals.Receive(ShellData(StrikeKind.Gas,203)); Time.time+=1.01f; StrikeVisuals.Tick(Time.time);
        check(StrikeBursts.Trails==0 && StrikeBursts.Calls.Contains("gas-burst"),"fx: a gas canister is dropped without a trail and vents when it lands");
        StrikeBursts.Reset();
        StrikeVisuals.Receive(ShellData(StrikeKind.Stasis,204)); Time.time+=1.01f; StrikeVisuals.Tick(Time.time);
        check(StrikeBursts.Calls.Count(c=>c=="stasis-pulse")==1,"fx: a stasis emitter sends its shockwave when it lands");
        StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=204,Tag=(byte)DataTag.Zone,Data=Wire.Zone(new ZoneRecord(StrikeKind.Stasis,0,0,0,5,10))});
        StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=205,Tag=(byte)DataTag.Zone,Replayed=true,Data=Wire.Zone(new ZoneRecord(StrikeKind.Stasis,0,0,0,5,10))});
        check(StrikeBursts.Calls.Count(c=>c=="stasis-pulse")==1,"fx: the field that starts after the landing, and a late joiner's replay, send no second shockwave");

        Setup(); StrikeVisuals.Clear(); StrikeVisuals.BeginLevel(); StrikeBursts.Reset();
        payload.OnBeaconLanded(new() {SummonerLookup=7,ThrowId=206,Parameters=beamSpec});
        var gen=Guid.NewGuid();
        OrdnancePayloadData BeamAt(uint id,float tx,bool replay=false) => new() {SummonerLookup=7,ThrowId=id,Tag=(byte)DataTag.Beam,Replayed=replay,
            Data=Wire.Beam(new BeamRecord(gen,0,10,0,tx,0,0,true))};
        OrdnancePayloadData BeamGen(float tx) => new() {SummonerLookup=7,ThrowId=206,Tag=(byte)DataTag.Beam,Data=Wire.Beam(new BeamRecord(gen,0,10,0,tx,0,0,true))};
        t0=Time.time;
        StrikeVisuals.Receive(BeamGen(0));
        check(StrikeBursts.Calls.Contains("beam-charge") && StrikeEffects.BeamsMade==0,"fx: the sky charges before the beam is drawn");
        Time.time=t0+.3f; StrikeVisuals.Tick(Time.time);
        check(StrikeEffects.BeamsMade==0 && !StrikeBursts.Calls.Contains("beam-mark"),"fx: the beam stays undrawn during the charge");
        for(var i=0;i<80;i++){Time.time+=.02f;StrikeVisuals.Tick(Time.time);}
        check(StrikeEffects.BeamsMade==1 && StrikeBursts.Calls.Count(c=>c=="beam-mark")==1,"fx: the beam is drawn after the charge and burns its first mark where it stands");
        StrikeVisuals.Receive(BeamGen(3));
        for(var i=0;i<120;i++){Time.time+=.02f;StrikeVisuals.Tick(Time.time);}
        check(StrikeBursts.Calls.Count(c=>c=="beam-mark")>=3,"fx: the route the beam walks gets a burn mark every 0.8 m");
        StrikeBursts.Reset();
        payload.OnBeaconLanded(new() {SummonerLookup=7,ThrowId=207,Parameters=beamSpec});
        StrikeVisuals.Receive(BeamAt(207,0,true)); StrikeVisuals.Tick(Time.time+.02f);
        check(!StrikeBursts.Calls.Contains("beam-charge") && StrikeEffects.BeamsMade==2,"fx: a late joiner sees the beam at once, without the charge");

        var released=StrikeEffects.BeamsReleased;
        StrikeVisuals.Receive(new() {SummonerLookup=7,ThrowId=207,Tag=(byte)DataTag.BeamEnd,Data=gen.ToByteArray()});
        check(StrikeEffects.BeamsReleased==released+1,"fx: a beam that ends gives its lines, reticle and sparks back to ForgeVisual");
        StrikeVisuals.Clear();
        check(StrikeEffects.BeamsReleased==StrikeEffects.BeamsMade && StasisSlow.Live.Count==0,"native: clearing visuals releases every beam's look and every slow field");

        // Real remote inventory reconciliation: no Returned notification, and unknown assembly is not proof of return.
        void RemoteLoan()
        {
            Setup(); Rack();
            client=new PlayerAgent {Owner=new() {Lookup=8},Alive=true,EyePosition=new(0,.4f,0),gameObject=new()};
            PlayerManager.PlayerAgentsInLevel.Add(client);
            clientWeapon=new BulletWeapon {Owner=client,gameObject=new()};
            PlayerBackpackManager.Backpacks[8]=new() {Item=new() {GearIDRange=new("1"),Instance=clientWeapon}};
            client.Inventory.WieldedItem=clientWeapon;
            claimRequest=new ClaimMessage {Thrower=7,ThrowId=1,Attempt=50};
            OrdnanceClaims.OnRequest(8,claimRequest);
            PlayerBackpackManager.Backpacks[8].Item!.GearIDRange=new("2") {PublicGearName=LauncherMarker};
            claimRequest.Action=ClaimMessage.Installed;
            OrdnanceClaims.OnRequest(8,claimRequest);
        }
        RemoteLoan();
        PlayerBackpackManager.Backpacks[8].Item=null;
        Frame(Time.time);
        Rack(2);
        claimRequest=new ClaimMessage {Thrower=7,ThrowId=2,Attempt=51};
        var reservations=ReservedCount();
        OrdnanceClaims.OnRequest(8,claimRequest);
        check(ReservedCount()==reservations,
            "native: unavailable native inventory does not prematurely release a committed loan");
        PlayerBackpackManager.Backpacks[8].Item=new() {GearIDRange=new("1"),Instance=clientWeapon,IsLoaded=false};
        Frame(Time.time);
        OrdnanceClaims.OnRequest(8,claimRequest);
        check(ReservedCount()==reservations,
            "native: unassembled ordinary gear is not proof that a loan was returned");
        PlayerBackpackManager.Backpacks[8].Item!.IsLoaded=true;
        Frame(Time.time);
        OrdnanceClaims.OnRequest(8,claimRequest);
        check(ReservedCount()==reservations+1,
            "native: inventory return releases a loan even when Returned was lost");

        RemoteLoan();
        Void("host change");
        PlayerBackpackManager.Backpacks[8].Item!.GearIDRange=new("1"); // Returned under the intervening host.
        Void("host change");
        Rack(2);
        claimRequest=new ClaimMessage {Thrower=7,ThrowId=2,Attempt=51};
        reservations=ReservedCount();
        OrdnanceClaims.OnRequest(8,claimRequest);
        check(ReservedCount()==reservations+1,
            "native: former host accepts a borrower who returned under another host");

        Setup(); Rack(); Take();
        ArsenalArt.Bundle=new();
        Time.time=1000; Frame(Time.time);
        var looks=GameObject.Created.Count(g=>g.name=="ForgeOrdnanceArsenal.WeaponLook");
        check(looks==1,"native: first scan mounts one borrowed weapon model");
        var aimed=PlayerBackpackManager.LocalBackpack.Item!.Instance!;
        check(Models.Dresses.Count==1 && Models.Dresses[0].Options is { HideNative: true, FitLength: false, Layer: true, Name: "ForgeOrdnanceArsenal.WeaponLook" },
            "native: the model is dressed on the game weapon: its meshes hidden, on its layer");
        var holder=aimed.GearPartHolder!;
        check(Models.Dresses[0].Options.Gun is { } gun && gun.RightHand==holder.m_aRightHand && gun.LeftHand==holder.m_aLeftHand
            && gun.Muzzle==holder.m_aMuzzle && gun.Magazine==holder.m_aMag && gun.RightHand!=null && gun.Magazine!=null,
            "native: the model is seated on the game gun's own hand and bore nodes and its magazine hangs on the game's magazine node");        Transform ModelSight(Dressed d)=>Models.Find(d.Look.transform,HeavyWeapons.SightSocket)!;
        check(aimed.SightLookAlign==ModelSight(Models.Looks.Last()) && aimed.GearPartHolder!.m_aSightLook==aimed.SightLookAlign,
            "native: aiming lines up the model's own sight socket, not the hidden game sight");
        for(var migration=0;migration<3;migration++)
        {
            Void("host change");
            Time.time+=1; Frame(Time.time);
        }
        check(GameObject.Created.Count(g=>g.name=="ForgeOrdnanceArsenal.WeaponLook")==looks,
            "native: repeated host migration cannot mount duplicate skins on a surviving weapon");
        // The game adds a gun's parts over several frames: parts that appear after dressing are hidden and the look is fitted again.
        var dressedLook=Models.Looks.Last();
        dressedLook.Late=10;
        Time.time+=1; Frame(Time.time);
        check(dressedLook.Undone && dressedLook.Look.WasCollected && Models.Looks.Count(l=>!l.Look.WasCollected)==1 && Models.Dresses.Count==2,
            "native: game parts added after dressing are hidden and the look is put on again for the whole gun");
        check(aimed.SightLookAlign==ModelSight(Models.Looks.Last()) && !aimed.SightLookAlign.gameObject.WasCollected && aimed.GearPartHolder!.m_aSightLook==aimed.SightLookAlign,
            "native: the look put on again moves aiming to the new model's sight socket");
        Time.time+=1; Frame(Time.time);
        check(Models.Dresses.Count==2, "native: a look with no late parts is left as it is");

        Setup(true);
        var weaponAnchor=new GameObject("weapon_anchor").transform;
        OrdnanceDelivery.Pod!.WeaponAnchor=weaponAnchor;
        OrdnanceDelivery.Pod.WeaponBay=new OrdnancePodBay(default,default,new(.88f,1.31f,.36f));
        var upright=OrdnancePodBay.Fits;
        Rack(5);
        check(OrdnancePodBay.Fits==upright+1 && GameObject.Created.Any(g=>IsRack(g) && g.transform.Parent==weaponAnchor && !g.WasCollected),
            "native: a racked weapon stands in the pod's weapon bay and moves with the pod");
        // ---- switching between the player's own special weapon and a lent heavy weapon ----------------------------
        (uint Gear, int Clip, float Pool) Gun()
        {
            var p=PlayerBackpackManager.LocalBackpack;
            return (p.Item!.GearIDRange!.GetChecksum(), p.Item.Instance!.GetCurrentClip(), p.AmmoStorage.SpecialAmmo.AmmoInPack);
        }
        void SetGun(int clip, float pool)
        {
            var p=PlayerBackpackManager.LocalBackpack;
            p.Item!.Instance!.SetCurrentClip(clip);
            p.AmmoStorage.SpecialAmmo.AmmoInPack=pool;
        }
        void Press3()
        {
            Time.time+=.6f;
            InputMapper.GetButtonDown=(a,_)=>a==InputAction.SelectSpecial;
            Frame(Time.time);
            InputMapper.GetButtonDown=(_,_)=>false;
            Frame(Time.time); Frame(Time.time);
        }
        // A pack as ForgeOrdnance's one pickup redirect runs it: against the own weapon's kept pool while it is away.
        void Pickup(float amount)
        {
            var storage=PlayerBackpackManager.LocalBackpack.AmmoStorage;
            var slot=ForgeCore.SpecialSlot.Local;
            if(!slot.RedirectsRefill) { storage.PickupAmmo(AmmoType.Special,amount); return; }
            var held=storage.SpecialAmmo.AmmoInPack;
            storage.SpecialAmmo.AmmoInPack=slot.Own!.Pool;
            storage.PickupAmmo(AmmoType.Special,amount);
            slot.Refilled(storage.SpecialAmmo.AmmoInPack);
            storage.SpecialAmmo.AmmoInPack=held;
        }

        Setup(); Rack(); Take();
        check(Gun().Gear==2,"switch: the lent heavy weapon is in the slot");
        SetGun(4,17);
        Press3();
        check(Gun()==(1u,3,23f) && HeavyWeapons.ActiveLauncher==null,"switch: the key in hand puts the player's own weapon back with its magazine and reserve");
        SetGun(2,19);
        Press3();
        check(Gun()==(2u,4,17f) && HeavyWeapons.ActiveLauncher!=null,"switch: the key again brings the heavy weapon back with the magazine and reserve it left with");
        Press3();
        check(Gun()==(1u,2,19f),"switch: the own weapon keeps what was fired from it, both weapons are preserved over repeated switches");
        var equips=PlayerBackpackManager.EquipCalls;
        Press3(); Press3();
        check(Gun()==(1u,2,19f) && PlayerBackpackManager.EquipCalls==equips+2,"switch: each press once a switch has settled is exactly one switch");

        Setup(); Rack(); Take(); SetGun(4,17);
        equips=PlayerBackpackManager.EquipCalls;
        Time.time+=.6f;
        InputMapper.GetButtonDown=(a,_)=>a==InputAction.SelectSpecial;
        Frame(Time.time); Frame(Time.time);
        InputMapper.GetButtonDown=(_,_)=>false;
        check(PlayerBackpackManager.EquipCalls==equips+1,"switch: a key held across frames does not flicker the weapons");
        Setup(); Rack(); Take(); SetGun(4,17);
        PlayerManager.Local!.Inventory.WieldedItem=new BulletWeapon { gameObject=new(), ItemDataBlock=new() { inventorySlot=InventorySlot.GearStandard } };
        equips=PlayerBackpackManager.EquipCalls; Press3();
        check(PlayerBackpackManager.EquipCalls==equips && Gun().Gear==2,"switch: the key does not switch while another slot is in hand");
        PlayerManager.Local.Inventory.WieldedItem=PlayerBackpackManager.LocalBackpack.Item!.Instance;
        PlayerBackpackManager.LocalBackpack.Item.Instance!.IsReloading=true; Press3();
        check(PlayerBackpackManager.EquipCalls==equips && Gun().Gear==2,"switch: the key does not switch during a reload");
        PlayerBackpackManager.LocalBackpack.Item.Instance.IsReloading=false;
        FocusStateManager.CurrentState=eFocusState.Other; Press3(); FocusStateManager.CurrentState=eFocusState.FPS;
        check(PlayerBackpackManager.EquipCalls==equips,"switch: the key does not switch outside first-person play");

        // Ammunition packs: never the heavy weapon, always the player's own weapon.
        Setup(); Rack(); Take(); SetGun(4,17);
        Pickup(30);
        check(Gun()==(2u,4,17f),"refill: a pack does not refill the heavy weapon in hand");
        Press3();
        check(Gun()==(1u,3,53f),"refill: the same pack filled the own weapon's saved reserve");
        Pickup(10);
        check(Gun()==(1u,3,63f),"refill: with the own weapon in hand a pack fills it as usual");
        Press3();
        check(Gun()==(2u,4,17f),"refill: packs taken meanwhile never reached the stashed heavy weapon");
        Pickup(500);
        Press3();
        check(Gun()==(1u,3,100f),"refill: the game's own capacity still caps the own weapon's reserve");

        // The heavy weapon vanishes when spent; it cannot be switched back to.
        Setup(); Rack(); Take(); SetGun(0,0);
        Time.time+=2; Frame(Time.time); Frame(Time.time);
        check(Gun()==(1u,3,23f) && HeavyWeapons.ActiveLauncher==null,"spent: an empty heavy weapon is replaced by the own weapon");
        equips=PlayerBackpackManager.EquipCalls; Press3();
        check(PlayerBackpackManager.EquipCalls==equips && Gun().Gear==1,"spent: the key cannot bring a spent heavy weapon back");
        Pickup(5);
        check(Gun()==(1u,3,28f),"spent: packs reach the own weapon as usual afterwards");
        Rack(2); Take();
        check(Gun().Gear==2,"spent: the borrower can take another rack");

        // Spent after a switch round trip.
        Setup(); Rack(); Take(); SetGun(4,17); Press3(); Press3();
        SetGun(0,0);
        Time.time+=2; Frame(Time.time); Frame(Time.time);
        check(Gun()==(1u,3,23f),"spent: a heavy weapon emptied after switching is replaced by the own weapon as it was saved");

        // Drop and down while stashed end the loan with the own weapon in hand.
        Setup(); Rack(); Take(); SetGun(4,17); Press3(); SetGun(2,19);
        Drop();
        check(Gun()==(1u,2,19f) && HeavyWeapons.ActiveLauncher==null,"drop: dropping while stashed gives the heavy weapon up and keeps the own weapon's ammunition");
        equips=PlayerBackpackManager.EquipCalls; Press3();
        check(PlayerBackpackManager.EquipCalls==equips,"drop: nothing to switch to afterwards");
        Rack(2); Take();
        check(Gun().Gear==2,"drop: the loan was released so another rack can be taken");

        Setup(); Rack(); Take(); SetGun(4,17); Press3(); SetGun(2,19);
        PlayerManager.Local!.Alive=false;
        Frame(Time.time);
        PlayerManager.Local.Alive=true;
        check(Gun()==(1u,2,19f),"down: going down with the heavy weapon stashed leaves the own weapon and its ammunition");
        Rack(2); Take();
        check(Gun().Gear==2,"down: the loan was released so another rack can be taken");

        Setup(); Rack(); Take(); SetGun(4,17);
        PlayerManager.Local!.Alive=false;
        Frame(Time.time); Frame(Time.time);
        PlayerManager.Local.Alive=true;
        check(Gun()==(1u,3,23f),"down: going down with the heavy weapon in hand gives the own weapon back as saved");

        // The host keeps a stashed loan: no second rack, however often it reconciles.
        Setup(); Rack(); Take(); SetGun(4,17); Press3();
        Rack(2);
        for(var i=0;i<5;i++) { Time.time+=1; Frame(Time.time); }
        var reserved=ReservedCount();
        OrdnanceClaims.OnRequest(7,new ClaimMessage {Thrower=7,ThrowId=2,Attempt=90});
        check(ReservedCount()==reserved,"host: a stashed loan still blocks a second rack");
        Press3();
        check(Gun().Gear==2 && HeavyWeapons.ActiveLauncher!=null,"host: the stashed loan was still there to switch back to");

        // A switch back that cannot assemble does not lose the heavy weapon.
        Setup(); Rack(); Take(); SetGun(4,17); Press3(); SetGun(2,19);
        PlayerBackpackManager.Equip=range=>
        {
            Install(range);
            if(range.GetChecksum()==2) PlayerBackpackManager.LocalBackpack.Item!.IsLoaded=false;
        };
        Press3();
        Time.time+=7; Frame(Time.time); Frame(Time.time); Frame(Time.time);
        PlayerBackpackManager.Equip=Install;
        check(Gun()==(1u,2,19f),"switch: a heavy weapon that fails to assemble leaves the own weapon with its ammunition");
        Press3();
        check(Gun()==(2u,4,17f),"switch: after the failed attempt the heavy weapon is still in the stash");

        // Checkpoint reloads.
        Setup(); Rack(); Take(); SetGun(4,17); Press3(); SetGun(2,19);
        ForgeCore.SpecialSlot.Local.CaptureCheckpoint();
        ForgeCore.Checkpoint.Recalling=true; ForgeCore.SpecialSlot.Local.RecallCheckpoint(); Void("checkpoint reload");
        Install(new("1")); SetGun(2,19);
        equips=PlayerBackpackManager.EquipCalls;
        ForgeCore.Checkpoint.Recalling=false; Frame(Time.time); Frame(Time.time);
        check(PlayerBackpackManager.EquipCalls==equips && Gun()==(1u,2,19f),"checkpoint: reloading with the own weapon in hand keeps the native ammunition");
        Press3();
        check(PlayerBackpackManager.EquipCalls==equips && Gun().Gear==1,"checkpoint: the stashed heavy weapon does not survive a reload");
        Rack(2); Take();
        check(Gun().Gear==2,"checkpoint: the borrower can take another rack after a reload");

        Setup(); Rack(); Take(); SetGun(4,17); Pickup(30);
        ForgeCore.SpecialSlot.Local.CaptureCheckpoint();
        ForgeCore.Checkpoint.Recalling=true; ForgeCore.SpecialSlot.Local.RecallCheckpoint(); Void("checkpoint reload");
        Install(new("2") { PublicGearName=LauncherMarker });
        ForgeCore.Checkpoint.Recalling=false; Frame(Time.time); Frame(Time.time);
        check(Gun()==(1u,3,53f),"checkpoint: reloading with the heavy weapon in hand restores the own weapon including refills taken before the checkpoint");

        Setup(); Rack(); Take(); SetGun(4,17);
        Void("host change");
        Press3();
        check(Gun().Gear==1,"host change: a switch still works after the host changed");
        Void("level cleanup");
        equips=PlayerBackpackManager.EquipCalls; Press3();
        check(PlayerBackpackManager.EquipCalls==equips,"level end: nothing is left to switch to");

        // ---- an exosuit boarded with a heavy weapon (another plugin's newer lease on the shared slot) -------------------------
        // The suit side does what ForgeOrdnance_Frames does through the same SpecialSlot: take the lease, wait until it is ready, swap
        // the suit gun ("9") in, toggle between it and the player's own weapon, and on leaving put the own weapon back before the lease goes.
        const uint SuitGear=9;
        ForgeCore.SpecialSlotLease? suitLease=null;
        void SuitSwapIn()
        {
            var slot=ForgeCore.SpecialSlot.Local;
            if(Gun().Gear==slot.Own?.Checksum && !slot.OwnAway) slot.OwnLeaves(Gun().Clip,Gun().Pool);
            Install(new(SuitGear.ToString()));
            SetGun(30,90);
        }
        void SuitOwnIn()
        {
            var own=ForgeCore.SpecialSlot.Local.Own!;
            Install(new(own.GearJson));
            SetGun(own.Clip,own.Pool);
            ForgeCore.SpecialSlot.Local.OwnReturned();
        }
        bool Board()
        {
            suitLease=ForgeCore.SpecialSlot.Local.Take("NAinfini.ForgeOrdnanceFrames",()=>"Piloting an exosuit");
            Frame(Time.time);
            if(!suitLease.Ready) return false;
            SuitSwapIn();
            Frame(Time.time);
            return true;
        }
        void LeaveSuit()
        {
            if(Gun().Gear==SuitGear) SuitOwnIn();
            suitLease!.Dispose();
            Time.time+=.1f; Frame(Time.time); Frame(Time.time);
        }
        int Reservations() => ReservedCount();

        // Boarding with the heavy weapon in hand, toggling, refilling, and leaving the suit by each path that ends a session.
        foreach(var way in new[] { "leave", "wrecked", "forced out", "grabbed out", "own weapon in hand" })
        {
            Setup(); Rack(); Take(); SetGun(4,17);
            check(Board() && Gun().Gear==SuitGear,way+": boarding with the heavy weapon in hand puts it away and the suit gun comes in");
            check(HeavyWeapons.ActiveLauncher==null,way+": the put-away heavy weapon cannot fire");
            equips=PlayerBackpackManager.EquipCalls;
            Press3();
            check(PlayerBackpackManager.EquipCalls==equips && Gun().Gear==SuitGear,way+": the heavy weapon's switch key does nothing while piloting");
            Pickup(30);
            check(Gun()==(SuitGear,30,90f) && ForgeCore.SpecialSlot.Local.Own!.Pool==53f,way+": a pack while piloting fills the own weapon, not the suit gun or the heavy weapon");
            SuitOwnIn();
            check(Gun()==(1u,3,53f),way+": the suit's toggle brings the player's own weapon, never the heavy weapon");
            if(way!="own weapon in hand") SuitSwapIn();
            Rack(2);
            var held=Reservations();
            InputMapper.GetButtonDown=(_,_)=>true; Frame(Time.time); InputMapper.GetButtonDown=(_,_)=>false;
            check(Reservations()==held && GuiManager.InteractionLayer!.Prompts.Last()=="Piloting an exosuit: cannot take it",
                way+": a rack cannot be taken while piloting and says why");
            for(var i=0;i<3;i++) { Time.time+=1; Frame(Time.time); }
            check(Gun().Gear==(way=="own weapon in hand"?1u:SuitGear),way+": host reconciliation while piloting keeps the loan and leaves the slot alone");
            LeaveSuit();
            check(Gun()==(2u,4,17f) && HeavyWeapons.ActiveLauncher!=null,way+": after leaving the heavy weapon is back in hand with its magazine and reserve");
            Press3();
            check(Gun()==(1u,3,53f),way+": and the own weapon behind it kept the ammunition it was refilled to");
            check(Reservations()==held,way+": the host kept the loan throughout; no second rack was reserved");
            Pickup(10);
            check(Gun()==(1u,3,63f),way+": with the own weapon in hand, packs go through the game again");
        }

        // Boarding with the heavy weapon stashed: it stays stashed afterwards and can be switched back to.
        Setup(); Rack(); Take(); SetGun(4,17); Press3(); SetGun(2,19);
        check(Board() && Gun().Gear==SuitGear,"stashed: boarding with the heavy weapon stashed brings the suit gun");
        LeaveSuit();
        check(Gun()==(1u,2,19f),"stashed: after leaving the own weapon is in hand with its ammunition; the heavy weapon stays stashed");
        Press3();
        check(Gun()==(2u,4,17f),"stashed: the heavy weapon can be switched back to with its magazine and reserve");

        // A spent suit boarded with the heavy weapon in hand: the own weapon replaces it while piloting.
        Setup(); Rack(); Take(); SetGun(4,17);
        suitLease=ForgeCore.SpecialSlot.Local.Take("NAinfini.ForgeOrdnanceFrames",()=>"Piloting an exosuit");
        Frame(Time.time);
        check(suitLease.Ready,"spent suit: the heavy weapon is put away for the suit too");
        SuitOwnIn();
        Frame(Time.time);
        check(Gun()==(1u,3,23f),"spent suit: the own weapon is in hand while piloting");
        LeaveSuit();
        check(Gun()==(2u,4,17f),"spent suit: after leaving the heavy weapon is back in hand");

        // A checkpoint reload while piloting: the put-away heavy weapon does not survive it (as a stashed one never does).
        Setup(); Rack(); Take(); SetGun(4,17);
        Board();
        ForgeCore.Checkpoint.Recalling=true; ForgeCore.SpecialSlot.Local.RecallCheckpoint(); Void("checkpoint reload");
        ForgeCore.Checkpoint.Recalling=false;
        Install(new("1")); SetGun(3,23);
        ForgeCore.SpecialSlot.Local.OwnReturned();
        suitLease!.Dispose();
        Frame(Time.time); Frame(Time.time);
        equips=PlayerBackpackManager.EquipCalls; Press3();
        check(Gun()==(1u,3,23f) && PlayerBackpackManager.EquipCalls==equips && !ForgeCore.SpecialSlot.Local.Taken,"checkpoint: a reload while piloting ends with the own weapon and drops the put-away heavy weapon");
        Rack(2); Take();
        check(Gun().Gear==2,"checkpoint: after the reload a rack can be taken again");

        // Level end while piloting: nothing is kept.
        Setup(); Rack(); Take(); Board();
        ForgeCore.SpecialSlot.Local.Clear(); Void("level cleanup");
        check(!ForgeCore.SpecialSlot.Local.Taken && HeavyWeapons.ActiveLauncher==null,"level end: the suit's lease and the put-away heavy weapon are gone");
        Setup();
    }
}
