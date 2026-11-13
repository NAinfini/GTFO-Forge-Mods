using System;
using System.Collections.Generic;
using System.Linq;
using ForgeVisual;
using GameData;
using Gear;
using Player;
using SNetwork;
using UnityEngine;
using LoanedGun = ForgeCore.LoanedGun;
using Object = UnityEngine.Object;

namespace ForgeOrdnance.Arsenal;

/// <summary>Racks in drop pods (every peer; the carrier does their claiming), the host's loan book, the local player's lent weapon, and the look of lent
/// weapons in every player's hands.</summary>
internal static class HeavyWeapons
{
    private const float SwapTimeout = 6f;
    private const float SkinScanSeconds = 0.5f;
    /// <summary>How long a raise waits for a model that is not ready.</summary>
    private const float ModelWait = 1.5f;
    /// <summary>Part spawns of the incoming gun done per frame of a switch: the game builds a gun one part a frame (a dozen frames, hand empty);
    /// every part in one frame was the hitch of a switch, so a few a frame keep each frame short and the hand is empty for a few frames.</summary>
    private const int PartsPerFrame = 3;
    private static readonly Dictionary<string, ArchetypeDataBlock> OwnBlocks = new();
    private static readonly Dictionary<string, RecoilDataBlock> OwnRecoil = new();
    /// <summary>The look on each lent weapon in a player's hands, by the weapon item.</summary>
    private static readonly Dictionary<IntPtr, WornLook> Skins = new();

    /// <summary>A look on a lent weapon.</summary>
    private sealed class WornLook
    {
        internal GunMount Gun = null!;
        /// <summary>The model id of the heavy weapon this look stands for (<c>Arsenal:heavy-mg</c>).</summary>
        internal string Heavy = "";
    }

    private sealed class Rack
    {
        internal ulong Summoner;
        internal uint ThrowId;
        internal HeavyParameters P = null!;
        /// <summary>The call-in definition's Id, and the definition itself when this peer saw the call arrive (null for a rack rebuilt from a late join's record).</summary>
        internal string Id = "";
        internal OrdnanceDefinition? Definition;
        internal OrdnancePod? Pod;
        /// <summary>The platform's claim of this rack (reach, aim, prompt, reservation and messages are its).</summary>
        internal OrdnanceClaimable? Claim;
        internal int Bay;
        internal GameObject? Look;
        internal Vector3 Fallback;
        /// <summary>Where the call landed and which way it faced: what a new host writes into the rack's kept record.</summary>
        internal Vector3 Landing;
        internal float Yaw;
        internal bool Presented;
        /// <summary>Host: the launcher loan built from the claimant's installed gear, kept until the claim is committed.</summary>
        internal LauncherLoan? PendingLauncher;
        internal bool Taken => Claim?.IsTaken == true;
    }

    /// <summary><c>Stashed</c>: the player's own special weapon is in the slot and the heavy weapon waits, with its magazine and reserve, in
    /// <see cref="_heavyStash"/>. The select-special key switches between the two while the special weapon is in hand. <c>Suspended</c>:
    /// another lender's newer lease holds the slot (an exosuit's gun); the heavy weapon waits in <see cref="_heavyStash"/> with the host's
    /// loan kept, and comes back into the slot when that lease ends if it was in hand when it began.</summary>
    private enum Phase { None, Equipping, AwaitingCommit, Active, Returning, Reconciling, Stashed, Suspended }

    private static readonly List<Rack> Racks = new();
    private static readonly HashSet<(ulong Summoner, uint ThrowId)> UnavailableRacks = new();
    /// <summary>Host: the racks of each call still lying in their pod (bay to kept record).</summary>
    private static readonly Dictionary<(ulong Summoner, uint ThrowId), Dictionary<int, byte[]>> HostRacks = new();
    private static readonly LoanBook Loans = new();
    private static readonly Dictionary<ulong, LauncherLoan> Launchers = new();
    /// <summary>Host after a host change: borrowers whose launcher loan was adopted from their first shot (the old host's book is gone).</summary>
    private static readonly HashSet<ulong> AdoptedLaunchers = new();
    private static bool _adoptLaunchers;
    /// <summary>Host after a host change: racks that were not yet open to take when it took over, opened as their pods open.</summary>
    private static readonly List<Rack> HostOpenPending = new();
    private static Guid _launcherIdentity;
    private static float _nextSkinScan;
    private static Rack? _promptRack;
    private static Phase _promptPhase;
    private static ForgeCore.SpecialSlotLease? _promptRefusal;
    private static int _promptLanguage = -1;
    private static string _promptText = "";
    private static string _promptKey = "";

    private static Phase _phase;
    private static HeavyParameters? _lent;
    /// <summary>The player's own special weapon and who holds the slot, shared with everything else that lends a gun into it.</summary>
    private static ForgeCore.SpecialSlot Slot => ForgeCore.Gear.SpecialSlot;
    /// <summary>This lender's name in the slot. The suit gun (<c>SuitGunLoan.Owner</c>) has its own: a checkpoint remembers whose gun was in the slot.</summary>
    private const string SlotOwner = Messages.ArsenalId;
    private static ForgeCore.SpecialSlotLease? _lease;
    /// <summary>Suspended with the heavy weapon in hand (it comes back when the newer lease ends); resuming with it.</summary>
    private static bool _suspendedInHand, _resumeInHand;
    private static GearIDRange? _expected;
    /// <summary>The claim this player's loan came from; its attempt number also names the loan to the host.</summary>
    private static ClaimInstall? _claim;
    private static bool _returnRequested;
    private static HeavyLoan? _loan;
    private static float _phaseStarted;
    private static AmmoStash? _heavyStash;
    private static float _switchReadyAt;
    /// <summary>Pressing the key again right after a switch only repeats it; a new switch waits this long.</summary>
    private const float SwitchCooldown = 0.5f;

    /// <summary>The public gear name that marks a lent weapon on every peer (the gear range replicates it): <c>Forge Heavy</c> and the
    /// model hash, 20 characters (the repo's other markers assume a limit of about 25).</summary>
    internal static string Marker(HeavyWeaponSpec spec) => spec.Marker;

    /// <summary>The model id of the heavy weapon a public gear name marks; false for any other name, and for a marker whose model this install never accepted.</summary>
    private static bool TryMarker(string? name, out string model) => HeavyModels.TryMarkerId(name, out model);

    /// <summary>The local player's lent weapon when it fires grenades, for <see cref="Grenades"/>; null otherwise.</summary>
    internal static HeavyParameters? ActiveLauncher => _phase == Phase.Active && _lent?.Spec.Grenade != null ? _lent : null;

    /// <summary>The local player's lent weapon when its bullets home in on enemies, for <see cref="HomingFire"/>; null otherwise.</summary>
    /// <summary>The model id of the lent weapon while it is in hand (<c>Arsenal:heavy-mg</c>), or null.</summary>
    internal static string? ActiveModel => _phase == Phase.Active ? _lent?.Spec.Model : null;
    internal static HomingSpec? ActiveHoming => _phase == Phase.Active ? _lent?.Spec.Homing : null;

    internal static Guid ActiveLauncherIdentity => _launcherIdentity;

    /// <summary>Only the host's committed loan and native gear definition can authorize a grenade.</summary>
    internal static bool AuthorizeGrenade(ulong sender, ref GrenadeShot shot, out float delay, out string reason)
    {
        delay = 0f;
        reason = "sender is not the shooter or not a human player";
        if (sender != shot.Shooter || !OrdnanceNetwork.Channel.IsPlayer(sender)) return false;
        reason = "no active host launcher loan";
        if (!Launchers.TryGetValue(sender, out var loan) && !TryAdoptLauncher(sender, shot.Loan, out loan)) return false;
        var agent = ForgeCore.Level.PlayerByLookup(sender);
        reason = "launcher is not held by a live borrower";
        if (agent == null || !agent.Alive || agent.Inventory?.WieldedItem?.ItemDataBlock?.inventorySlot != InventorySlot.GearSpecial
            || !PlayerBackpackManager.TryGetBackpack(agent.Owner, out var pack) || pack == null
            || !pack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item) || item == null || !item.IsLoaded) return false;
        var gear = item.GearIDRange;
        if (gear?.GetChecksum() != loan.GearChecksum || gear.PublicGearName != Marker(loan.Spec)) return false;
        var direction = new Vector3(shot.VX, shot.VY, shot.VZ);
        reason = "invalid aim direction";
        if (!float.IsFinite(Vec.SqrLength(direction)) || Vec.SqrLength(direction) < 0.9f || Vec.SqrLength(direction) > 1.1f) return false;
        var origin = new Vector3(shot.OX, shot.OY, shot.OZ);
        var eye = agent.EyePosition;
        if (!loan.TrySchedule(shot.Loan, shot.Sequence, Time.time, Vec.SqrLength(origin - eye), out delay, out reason)) return false;
        // The submitted origin is the shooter's muzzle (or the eye plus the spawn offset on a gun with no muzzle point), inside the reach
        // proof above. The host flies from it only when nothing solid lies between its own eye and it, so the tolerance cannot move the
        // grenade through a thin wall; otherwise it flies from the eye.
        var launch = origin;
        var reach = Vec.Length(launch - eye);
        if (reach > 0.01f && Grenades.Blocked(eye, (launch - eye) * (1f / reach), reach)) launch = eye;
        shot.OX = launch.x; shot.OY = launch.y; shot.OZ = launch.z;
        var grenade = loan.Spec.Grenade!;
        var velocity = direction.normalized * grenade.Speed + Vec.Up * 1.5f;
        shot.VX = velocity.x; shot.VY = velocity.y; shot.VZ = velocity.z;
        shot.Damage = grenade.Damage;
        shot.Radius = grenade.Radius;
        shot.TrueEdge = grenade.TrueDamage ? grenade.EdgeShare : -1f;
        return true;
    }

    // ---- arrival (every peer) and claims -------------------------------------------------------------------------

    internal static void ShowRack(OrdnancePresentation p)
    {
        if (UnavailableRacks.Remove((p.SummonerLookup, p.ThrowId))) return;
        var parameters = (HeavyParameters)p.Parameters;
        var pod = OrdnanceDelivery.StartPod(p);
        // One rack per weapon bay of the pod; a direct arrival (or no pod art) lays them side by side on the landing point.
        var count = pod == null ? parameters.Spec.Count : Math.Min(parameters.Spec.Count, pod.WeaponBayCount);
        for (var bay = 0; bay < count; bay++)
        {
            var spot = p.Position + p.Rotation * Vector3.right * Spread(bay, count);
            var rack = new Rack { Summoner = p.SummonerLookup, ThrowId = p.ThrowId, P = parameters, Id = p.Definition.Id, Definition = p.Definition, Fallback = spot, Landing = p.Position, Yaw = p.Rotation.eulerAngles.y, Presented = true, Bay = bay };
            Racks.Add(rack);
            rack.Pod = pod;
            rack.Claim = AddClaimable(rack, pod, spot + Vec.Up * 0.4f, OrdnanceDelivery.PodContentsDelay(p.Arrival));
        }
    }

    /// <summary>Sideways offset of rack <paramref name="bay"/> of <paramref name="count"/> lying side by side without a pod.</summary>
    private static float Spread(int bay, int count) => (bay - (count - 1) * 0.5f) * 0.9f;

    /// <summary>One line per rack lying in a pod or on the ground: call number, bay, ammunition numbers (the driver's readback).</summary>
    internal static string Describe()
    {
        if (Racks.Count == 0) return "no racks";
        return string.Join("\n", Racks.Select(r => "rack " + r.Id + " throw " + r.ThrowId + " bay " + r.Bay + " ammoRatio=" + r.P.Spec.AmmoRatio.ToString("0.##") + " magazine=" + (float.IsNaN(r.P.Spec.Tuning.Magazine) ? "default" : r.P.Spec.Tuning.Magazine.ToString("0")) + " ammoCost=" + (float.IsNaN(r.P.Spec.Tuning.AmmoCost) ? "default" : r.P.Spec.Tuning.AmmoCost.ToString("0.##")) + (r.Taken ? " taken" : "")));
    }

    private static byte Tag(int bay) => (byte)((int)DataTag.Rack + bay);

    private static int BayOf(byte tag) => tag - (int)DataTag.Rack;

    /// <summary>Hands the rack to the carrier's claims: the pod's weapon bay (or the landing point without a pod) is where it can be taken.</summary>
    private static OrdnanceClaimable AddClaimable(Rack rack, OrdnancePod? pod, Vector3 position, float openAfter)
    {
        var spec = new ClaimableSpec
        {
            Prompt = () => RackPrompt(rack),
            HostCheck = agent => HostCheck(rack, agent),
            Install = claim => Install(rack, claim),
            Cancelled = _ => { if (_phase is Phase.Equipping or Phase.AwaitingCommit) Abort(); },
            HostVerify = agent => Verify(rack, agent),
            Committed = result => Committed(rack, result),
            Opened = () => OpenRack(rack),
            Removed = () => RemoveRack(rack),
        };
        if (pod == null) return OrdnanceDelivery.AddClaimable(rack.Summoner, rack.ThrowId, position, openAfter, spec, rack.Bay);
        return pod.AddClaimable(rack.Bay, spec);
    }

    private static void OpenRack(Rack rack)
    {
        if (rack.Taken || rack.Look != null) return;
        var model = ArsenalArt.Weapon(rack.P.Spec.Model);
        var bay = rack.Pod?.WeaponBayAt(rack.Bay);
        if (model == null) return;
        if (bay is { } room)
        {
            // Real size, standing upright in the pod's weapon bay; it falls, opens and leaves with the pod.
            var instance = model.Spawn(room.Position, room.Rotation);
            if (instance == null) return;
            rack.Look = room.Fit(instance, ArsenalArt.FrontToForward, out _, out _);
        }
        else
        {
            var instance = model.Spawn(rack.Fallback + Vec.Up * 0.15f, ArsenalArt.FrontToForward);
            if (instance == null) return;
            instance.name = "ForgeOrdnance.Arsenal.Rack";
            rack.Look = instance;
        }
    }

    internal static void HostArrived(OrdnanceExecution e)
    {
        var summoner = e.SummonerLookup;
        var throwId = e.ThrowId;
        var p = (HeavyParameters)e.Parameters;
        if (e.Overrides.Count != 0)
            p = p with { Spec = p.Spec.WithAmmo(e.Overrides.TryGetValue("AmmoRatio", out var ratio) ? (float)ratio : null, e.Overrides.TryGetValue("Magazine", out var rounds) ? (float)rounds : null, e.Overrides.TryGetValue("AmmoCost", out var cost) ? (float)cost : null) };
        var record = Wire.Rack(new RackRecord(p.Spec, p.GearId, e.Position.x, e.Position.y, e.Position.z, e.Rotation.eulerAngles.y, e.Definition.Id));
        OrdnanceDelivery.HostAfter(OrdnanceDelivery.PodContentsDelay(e.Arrival), () =>
        {
            // Kept until the weapon is taken, so a player who joins meanwhile sees it and can take it. The pod leaves once every
            // rack of it has been taken (RackTaken), the same rule as a supply pod.
            var kept = new Dictionary<int, byte[]>();
            for (var bay = 0; bay < p.Spec.Count; bay++)
            {
                if (!OrdnanceApi.SendState(HeavyWeaponPayload.Type, summoner, throwId, Tag(bay), record))
                {
                    OrdnanceApi.ClearState(HeavyWeaponPayload.Type, summoner, throwId);
                    OrdnanceApi.SendData(HeavyWeaponPayload.Type, summoner, throwId, (byte)DataTag.RackUnavailable, Array.Empty<byte>());
                    OrdnanceDelivery.HostReportPodEmpty(summoner, throwId);
                    return;
                }
                kept[bay] = record;
            }
            HostRacks[(summoner, throwId)] = kept;
            foreach (var bay in kept.Keys) OrdnanceDelivery.HostOpenClaim(summoner, throwId, bay);
        });
    }

    /// <summary>Host: a rack was taken. Late joiners see only the racks still lying there; when none is left the pod is told it is
    /// empty and leaves after its linger time.</summary>
    private static void RackTaken(Rack rack)
    {
        var key = (rack.Summoner, rack.ThrowId);
        OrdnanceApi.ClearState(HeavyWeaponPayload.Type, rack.Summoner, rack.ThrowId);
        if (HostRacks.TryGetValue(key, out var left))
        {
            left.Remove(rack.Bay);
            foreach (var (bay, record) in left) OrdnanceApi.SendState(HeavyWeaponPayload.Type, rack.Summoner, rack.ThrowId, Tag(bay), record);
            if (left.Count != 0) return;
            HostRacks.Remove(key);
        }
        OrdnanceDelivery.HostReportPodEmpty(rack.Summoner, rack.ThrowId);
    }

    /// <summary>Host, after the platform's checks (alive, in reach, open, not reserved): a borrower cannot take a second weapon, and the
    /// gear's native effects must be loaded here.</summary>
    private static string? HostCheck(Rack rack, PlayerAgent agent)
    {
        if (HasBorrowedGear(agent) || (agent.Owner != null && Loans.HasLoan(agent.Owner.Lookup))) return "the requester is already borrowing a heavy weapon";
        try { LoanedGun.Require(BorrowedRange(rack.P)); }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Host refused unloaded heavy weapon effects: " + error);
            return "the host has not loaded the heavy weapon's effects";
        }
        return null;
    }

    private static bool HasBorrowedGear(PlayerAgent agent)
        => agent.Owner != null && PlayerBackpackManager.TryGetBackpack(agent.Owner, out var pack) && pack != null
            && pack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item) && item != null
            && TryMarker(item.GearIDRange?.PublicGearName, out _);

    /// <summary>Claimant: the host reserved the rack for this player; the lent gear goes into the special slot.</summary>
    private static void Install(Rack rack, ClaimInstall claim)
    {
        if (_phase != Phase.None) { claim.Failed(); return; }
        _claim = claim;
        try { BeginLoan(rack.P); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Heavy weapon installation refused: " + error); Abort(); }
    }

    /// <summary>Host, every frame once the claimant reported the gear installed: its public name and checksum must be the borrowed gear's;
    /// a launcher gets its loan (cadence, clip and round cost from the host's own gear definition, never the recipient's readings).</summary>
    private static ClaimVerdict Verify(Rack rack, PlayerAgent agent)
    {
        if (!PlayerBackpackManager.TryGetBackpack(agent.Owner, out var pack) || pack == null
            || !pack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item) || item == null || !item.IsLoaded
            || item.Instance == null || item.Instance.WasCollected || item.GearIDRange?.PublicGearName != Marker(rack.P.Spec)) return ClaimVerdict.Wait;
        var expected = BorrowedRange(rack.P);
        if (item.GearIDRange.GetChecksum() != expected.GetChecksum()) return ClaimVerdict.Wait;
        LauncherLoan? launcher = null;
        if (rack.P.Spec.Grenade != null)
        {
            var pool = pack.AmmoStorage.SpecialAmmo;
            var effective = EffectiveNumbers(rack.P, expected);
            var rounds = effective == null ? 0 : effective.Rounds(rack.P.Spec.AmmoRatio, pool.AmmoMaxCap);
            if (effective == null || !(rounds > 0 && rounds <= int.MaxValue)) return ClaimVerdict.Refuse("host gear has invalid ammunition or cadence");
            launcher = new LauncherLoan(rack.P.Spec, expected.GetChecksum(), (int)rounds, effective.ShotDelay, Time.time);
        }
        rack.PendingLauncher = launcher;
        return ClaimVerdict.Accept(launcher?.Identity ?? Guid.Empty);
    }

    /// <summary>The numbers a borrower's weapon is given (<see cref="Tune"/>): the vanilla block of the gear's archetype with this call-in's factory
    /// settings over it; null when that gives no valid ammunition or cadence.</summary>
    private static ArchetypeTuning? EffectiveNumbers(HeavyParameters p, GearIDRange gear)
    {
        var archetype = ArchetypeDataBlock.GetBlock(ForgeCore.Gear.ArchetypeId(gear));
        var effective = archetype == null ? null : p.Spec.Tuning.Over(Readings(archetype));
        return effective != null && effective.Valid() ? effective : null;
    }

    /// <summary>Host, after a host change: the old host's loan book is gone, so a borrower's first shot is the proof of a loan this host never saw.
    /// It is adopted only when the host's own replicated backpack shows that borrower holding a launcher's marker whose gear checksum is the
    /// checksum of the one definition with that model, with the identity the shot names; the budget is what that backpack still holds.</summary>
    private static bool TryAdoptLauncher(ulong sender, Guid identity, out LauncherLoan loan)
    {
        loan = null!;
        if (!_adoptLaunchers || !ForgeCore.Session.IsHost || identity == Guid.Empty) return false;
        var agent = ForgeCore.Level.PlayerByLookup(sender);
        if (agent == null || !agent.Alive || !PlayerBackpackManager.TryGetBackpack(agent.Owner, out var pack) || pack == null
            || !pack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item) || item == null || !item.IsLoaded || item.GearIDRange == null
            || !TryMarker(item.GearIDRange.PublicGearName, out var model)) return false;
        foreach (var definition in OrdnanceContent.Set.Definitions)
        {
            if (definition.Parameters is not HeavyParameters p || p.Spec.Grenade == null || p.Spec.Model != model) continue;
            var expected = BorrowedRange(p);
            if (expected.GetChecksum() != item.GearIDRange.GetChecksum() || EffectiveNumbers(p, expected) is not { } effective) continue;
            var rounds = Math.Floor(pack.AmmoStorage.SpecialAmmo.AmmoInPack / effective.AmmoCost) + effective.Magazine;
            if (!(rounds > 0 && rounds <= int.MaxValue)) return false;
            loan = Launchers[sender] = new LauncherLoan(p.Spec, expected.GetChecksum(), (int)rounds, effective.ShotDelay, Time.time, identity);
            AdoptedLaunchers.Add(sender);
            return true;
        }
        return false;
    }

    /// <summary>Every peer: the rack was taken. The host starts the loan; the claimant's weapon becomes active.</summary>
    private static void Committed(Rack rack, ClaimResult result)
    {
        if (ForgeCore.Session.IsHost)
        {
            Loans.Commit(rack.Summoner, rack.ThrowId, result.Player, result.Attempt);
            if (rack.PendingLauncher != null) Launchers[result.Player] = rack.PendingLauncher;
            rack.PendingLauncher = null;
            RackTaken(rack);
        }
        Destroy(rack.Look);
        rack.Look = null;
        if (!result.Ours || _phase != Phase.AwaitingCommit) return;
        _launcherIdentity = result.Token;
        _phase = Phase.Active;
        _loan = new HeavyLoan(Time.time);
        var agent = ForgeCore.Players.Local;
        if (agent != null) ArsenalAudio.PlayAttached(ArsenalAudio.Cue.Handover, agent.gameObject,
            (int)agent.DimensionIndex, agent.IsLocallyOwned);
    }

    /// <summary>Host: a borrower's loan notice (returned, or switched to their own weapon).</summary>
    internal static void OnLoanNotice(ulong sender, LoanNotice m)
    {
        // An action outside the enum is a malformed packet and is dropped, not read as Returned.
        if (!ForgeCore.Session.IsHost || !OrdnanceNetwork.Channel.IsPlayer(sender) || !Enum.IsDefined(m.Action)) return;
        if (m.Action == LoanAction.Stashed) Loans.Stash(m.Summoner, m.ThrowId, sender, m.Attempt);
        else if (Loans.Release(m.Summoner, m.ThrowId, sender, m.Attempt) || AdoptedLaunchers.Remove(sender)) Launchers.Remove(sender);
    }

    internal static void OnData(OrdnancePayloadData d)
    {
        if ((DataTag)d.Tag == DataTag.RackUnavailable)
        {
            if (d.Data.Length != 0) return;
            var presentationPending = true;
            for (var i = Racks.Count - 1; i >= 0; i--)
                if (Racks[i].Summoner == d.SummonerLookup && Racks[i].ThrowId == d.ThrowId)
                {
                    var rack = Racks[i];
                    presentationPending &= !rack.Presented;
                    rack.Claim?.Withdraw();
                    RemoveRack(rack);
                }
            if (presentationPending) UnavailableRacks.Add((d.SummonerLookup, d.ThrowId));
            return;
        }
        if (BayOf(d.Tag) is >= 0 and < HeavyWeaponSpec.MaxCount)
        {
            var bay = BayOf(d.Tag);
            if (!Wire.TryRack(d.Data, out var record))
            {
                if (Wire.UnknownRackModel(d.Data, out var hash))
                    Plugin.Logger.Error("A heavy weapon rack was dropped: its model hash " + hash.ToString("x8") + " is not one of this install's heavy weapon models (the host's heavy weapon packs must be installed here too).");
                return;
            }
            var existing = Racks.Find(r => r.Summoner == d.SummonerLookup && r.ThrowId == d.ThrowId && r.Bay == bay);
            // A rack shown from the arrival holds the definition's numbers; the host's record is the placement's (its Overrides), the same one a late joiner builds from.
            if (existing == null) RackFromState(d, record, bay);
            else existing.P = new HeavyParameters(record.Spec, record.GearId);
        }
    }

    /// <summary>The claim is over (the pod left, or the rack was withdrawn): its look goes.</summary>
    private static void RemoveRack(Rack rack)
    {
        Destroy(rack.Look);
        rack.Look = null;
        Racks.Remove(rack);
    }

    /// <summary>A rack this peer did not see arrive (it joined later): the weapon where the pod's weapon anchor is, open to take.
    /// Track the replayed pod's departure when one is available.</summary>
    private static void RackFromState(OrdnancePayloadData d, in RackRecord record, int bay)
    {
        var landing = new Vector3(record.X, record.Y, record.Z);
        var pod = OrdnanceDelivery.FindPod(d.SummonerLookup, d.ThrowId);
        // The same spacing the arrival used (ShowRack): by the racks the call laid down, not by the most a pod could hold.
        var count = pod == null ? record.Spec.Count : Math.Min(record.Spec.Count, pod.WeaponBayCount);
        var spot = landing + Quaternion.Euler(0f, record.Yaw, 0f) * Vector3.right * Spread(bay, count);
        var rack = new Rack { Summoner = d.SummonerLookup, ThrowId = d.ThrowId, P = new HeavyParameters(record.Spec, record.GearId), Id = record.Id, Fallback = spot, Landing = landing, Yaw = record.Yaw, Bay = bay };
        Racks.Add(rack);
        rack.Pod = pod;
        rack.Claim = AddClaimable(rack, pod, spot + Vec.Up * 0.4f, 0f);
        // Without a replayed pod nothing will open it: the weapon lies where it landed.
        if (pod == null) OpenRack(rack);
    }

    // ---- the local player ---------------------------------------------------------------------------------------

    internal static void Tick(float now)
    {
        // The replicated backpack is authoritative for whether a loan still exists. A return notice is only the fast
        // path; this also releases the loan if that message was dropped or there was no master while it was sent.
        if (ForgeCore.Session.IsHost && Loans.Any) Loans.ReconcileLoans(HostLoanStillPresent, ReleaseHostLoan);
        if (HostOpenPending.Count != 0 && ForgeCore.Session.IsHost) OpenAdopted();
        try { LocalLoan(now); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Heavy weapon loan failed: " + error); Abort(); }
        if (now >= _nextSkinScan)
        {
            _nextSkinScan = now + SkinScanSeconds;
            try { ScanSkins(); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("arsenal-heavy-look-failed", "heavy weapon look update failed: " + error); }
        }
    }

    /// <summary>The prompt while the local player aims at a rack. Another lender's lease on top (an exosuit's gun) means no heavy weapon
    /// can be taken, and the prompt says why. Text is rebuilt only when something it depends on changed.</summary>
    private static ClaimPrompt RackPrompt(Rack rack)
    {
        var top = Slot.Top;
        var refusal = top != null && top != _lease ? top : null;
        if (_promptRack != rack || _promptPhase != _phase || _promptLanguage != ForgeUI.UITexts.Version || _promptRefusal != refusal)
        {
            _promptRack = rack;
            _promptPhase = _phase;
            _promptLanguage = ForgeUI.UITexts.Version;
            _promptRefusal = refusal;
            _promptText = refusal != null ? OrdnanceText.T("rack.slotTaken", refusal.Refusal)
                : _phase == Phase.None ? OrdnanceText.T("rack.pickUp", rack.Definition != null ? OrdnanceContent.Name(rack.Definition) : ArsenalText.CallIn(rack.Id)) : OrdnanceText.T("rack.inHand");
            _promptKey = refusal == null && _phase == Phase.None ? "[" + ForgeUI.UIKitKeys.Binding(InputAction.Use, "E") + "]" : "";
        }
        return new ClaimPrompt(_promptText, _promptKey, refusal == null && _phase == Phase.None);
    }

    private static GearIDRange BorrowedRange(HeavyParameters p)
    {
        var block = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(p.GearId)
            ?? throw new InvalidOperationException("Offline gear block " + p.GearId + " is gone.");
        return new GearIDRange(block.GearJSON) { PublicGearName = Marker(p.Spec) };
    }

    /// <summary>The block's numbers a call-in's factory settings may set.</summary>
    private static ArchetypeTuning Readings(ArchetypeDataBlock b) => new(new[]
    {
        b.Damage, b.ShotDelay, b.SpecialCooldownTime, b.SpecialChargetupTime, b.DefaultClipSize, b.DefaultReloadTime,
        b.PrecisionDamageMulti, b.StaggerDamageMulti, b.PiercingBullets ? b.PiercingDamageCountLimit : 0f, b.CostOfBullet,
        b.DamageFalloff.x, b.DamageFalloff.y,
    });

    /// <summary>The lent instance gets its own copy of the archetype with the call-in's factory settings over it (the shared block also
    /// drives the stock weapon and stays as the game has it). Everything not set stays native, and the game's own firing, hit and ammunition
    /// code reads the copy. Returns the numbers now in force, or null when the weapon has no archetype to tune.</summary>
    private static ArchetypeTuning? Tune(BulletWeapon weapon, HeavyWeaponSpec spec)
    {
        var archetype = weapon.m_archeType;
        var vanilla = archetype?.m_archetypeData;
        if (archetype == null || vanilla == null) { Plugin.Logger.ErrorOnce("The heavy weapon has no archetype; its factory settings cannot be applied."); return null; }
        Kick(weapon, spec);
        var native = Readings(vanilla);
        var effective = spec.Tuning.Over(native);
        if (spec.Tuning.IsEmpty) return effective;
        // The copy is built once per weapon (the reflection copy of every property is the slow part) and handed to each new instance of it.
        // Two definitions may share a model, so the key is the base block and the numbers in force, not the model alone.
        var key = Marker(spec) + "|" + vanilla.persistentID + "|" + effective.Key;
        if (!OwnBlocks.TryGetValue(key, out var own))
        {
            own = ForgeCore.Gear.CopyArchetype(vanilla);
            own.Damage = effective.Damage;
            own.ShotDelay = effective.ShotDelay;
            own.SpecialCooldownTime = effective.Cooldown;
            own.SpecialChargetupTime = effective.Chargeup;
            own.DefaultClipSize = (int)effective.Magazine;
            own.DefaultReloadTime = effective.Reload;
            own.PrecisionDamageMulti = effective.Precision;
            own.StaggerDamageMulti = effective.Stagger;
            own.PiercingBullets = effective.Pierce > 0f;
            if (effective.Pierce > 0f) own.PiercingDamageCountLimit = (int)effective.Pierce;
            own.CostOfBullet = effective.AmmoCost;
            own.DamageFalloff = new UnityEngine.Vector2(effective.FalloffStart, effective.FalloffEnd);
            OwnBlocks[key] = own;
        }
        // The weapon holds the block twice and the game reads the two for different numbers: the archetype's copy for the firing rhythm and
        // spread, the item's copy (ItemEquippable.ArchetypeData) for damage, weak-spot and stagger multipliers, piercing and reload.
        // Swapping only the archetype's left a tuned damage and precision with no effect on the hit.
        archetype.m_archetypeData = own;
        weapon.ArchetypeData = own;
        if (archetype.m_archetypeData == null || weapon.ArchetypeData == null
            || !Readings(archetype.m_archetypeData).SameAs(effective) || !Readings(weapon.ArchetypeData).SameAs(effective))
            Plugin.Logger.ErrorOnce("The heavy weapon's archetype did not read back with its factory settings.");
        return effective;
    }
    /// <summary>How hard the gun kicks: the game's own recoil of the lent gun, scaled, on a copy this weapon keeps (the block is shared with the stock gun).</summary>
    private static void Kick(BulletWeapon weapon, HeavyWeaponSpec spec)
    {
        if (spec.RecoilScale == 1f) return;
        var archetype = weapon.m_archeType;
        // The base gear and the scale decide the copy (two definitions may share a model); a copy is never scaled again.
        var key = Marker(spec) + "|" + (spec.GearId?.ToString() ?? spec.GearName) + "|" + spec.RecoilScale;
        if (!OwnRecoil.TryGetValue(key, out var own))
        {
            var source = archetype != null && archetype.m_recoilData != null ? archetype.m_recoilData : ((ItemEquippable)weapon).RecoilData;
            if (source == null) { Plugin.Logger.ErrorOnce("arsenal-heavy-recoil-missing", "the heavy weapon has no recoil block; it kicks as the game's gun does."); return; }
            OwnRecoil[key] = own = ForgeCore.Gear.ScaledRecoil(source, spec.RecoilScale);
        }
        ((ItemEquippable)weapon).RecoilData = own;
        if (archetype != null) archetype.m_recoilData = own;
    }

    private static void BeginLoan(HeavyParameters p)
    {
        if (_phase != Phase.None) throw new InvalidOperationException("This player already holds a loan.");
        if (Slot.Taken) throw new InvalidOperationException("The special slot is lent to " + Slot.Top!.Owner + ".");
        if (!LoanedGun.TryLocalSpecial(out _, out var item) || item!.GearIDRange == null || !item.IsLoaded)
            throw new InvalidOperationException("The original special slot is not ready.");
        var range = BorrowedRange(p);
        LoanedGun.Require(range);
        var saved = item.GearIDRange;
        _lease = Slot.Take(SlotOwner, () => OrdnanceText.T("rack.inHand"));
        Slot.Remember(saved.ToJSON(), saved.GetChecksum());
        LoanedGun.OwnLeaves();
        _expected = range;
        _lent = p;
        _phase = Phase.Equipping;
        _phaseStarted = Time.time;
        LoanedGun.Equip(range);
    }

    /// <summary>Tells the host what became of the loan (it must not read the marker leaving the slot as the loan ending).</summary>
    private static void SendLoan(LoanAction action)
    {
        if (_claim == null || SNet.LocalPlayer == null) return;
        var message = new LoanNotice { Summoner = _claim.Summoner, ThrowId = _claim.ThrowId, Attempt = _claim.Attempt, Action = action };
        if (ForgeCore.Session.IsHost) OnLoanNotice(SNet.LocalPlayer.Lookup, message);
        else ArsenalNetwork.Loan.ToHost(message);
    }

    private static void LocalLoan(float now)
    {
        if (_phase == Phase.None) return;
        if (_lease is { Suspended: true })
        {
            Suspend(now);
            if (_lease.Parked) return;
        }
        else if (_lease is { Parked: true }) Resume(now);
        if (_phase == Phase.Reconciling && !ForgeCore.Checkpoint.Settled) { _phaseStarted = now; return; }
        bool ReconciliationTimedOut()
        {
            if (_phase != Phase.Reconciling || now - _phaseStarted <= SwapTimeout) return false;
            Plugin.Logger.ErrorOnce("Checkpoint special weapon did not finish assembling; restoring the saved original weapon.");
            Return(now);
            return true;
        }
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected) { ReconciliationTimedOut(); return; }
        if (_phase is Phase.Equipping or Phase.AwaitingCommit && now - _phaseStarted > SwapTimeout)
            throw new InvalidOperationException("Heavy weapon installation/commit timed out.");
        if (_phase == Phase.Returning && now - _phaseStarted > SwapTimeout)
        {
            // Retain the managed restoration record and retry; never silently forget a broken return.
            Plugin.Logger.ErrorOnce("Special weapon restoration is still pending; retrying.");
            _returnRequested = false;
            _phaseStarted = now;
        }
        if (_phase == Phase.Returning && !_returnRequested) RequestReturn();
        if (!LoanedGun.TryLocalSpecial(out var backpack, out var item)) { ReconciliationTimedOut(); return; }
        var name = item!.GearIDRange?.PublicGearName;
        // The game spawns the incoming gun's parts one per frame, with the hand empty meanwhile; they come in the frame the gun stands in the slot.
        if (_phase is Phase.Equipping or Phase.Returning) LoanedGun.FinishParts(item!, PartsPerFrame);
        var ready = LoanedGun.Settled(item!, out var instance);

        if (_phase == Phase.Reconciling)
        {
            if (!ready) { ReconciliationTimedOut(); return; } // Native recall has finished, but gear assembly may still be asynchronous.
            if (TryMarker(name, out _)) Return(now);
            else
            {
                // The checkpoint contained ordinary gear; keep its native ammunition.
                if (item.GearIDRange?.GetChecksum() == Slot.Own?.Checksum) Slot.OwnReturned();
                End();
            }
            return;
        }
        switch (_phase)
        {
            case Phase.Equipping:
                if (!agent.Alive && _heavyStash != null) { Abort(); return; }
                if (ready && name == Marker(_lent!.Spec) && item.GearIDRange!.GetChecksum() == _expected!.GetChecksum())
                {
                    // The hand never raises the bare game gun: the wield waits for the model (a short while, for a model that cannot load).
                    ScanSkins();
                    if (!Skins.ContainsKey(instance!.Pointer) && ArsenalArt.Weapon(_lent.Spec.Model) is { Ready: false } && now - _phaseStarted < ModelWait) return;
                    var resume = _heavyStash;
                    var tuned = Tune(instance!, _lent.Spec);
                    // The pool took its price of a round from the game's gun when the item was made; it must charge what this weapon charges.
                    if (tuned != null) ForgeCore.Gear.SetLocalAmmoType(AmmoType.Special, tuned.AmmoCost, (int)tuned.Magazine);
                    // A weapon taken back from the stash keeps exactly the magazine and reserve it was switched away with.
                    if (!ForgeCore.Gear.WriteLocalAmmo(AmmoType.Special, instance!, resume?.Pool ?? _lent.Spec.AmmoRatio * backpack.AmmoStorage.SpecialAmmo.AmmoMaxCap,
                            resume?.Clip ?? (_lent.Spec.Tuning.IsEmpty || tuned == null ? instance!.GetMaxClip() : (int)tuned.Magazine)))
                        Plugin.Logger.ErrorOnce("The heavy weapon's ammunition did not read back as written.");
                    agent.Sync.WantsToWieldSlot(InventorySlot.GearSpecial);
                    if (resume != null)
                    {
                        _heavyStash = null;
                        _loan = new HeavyLoan(now);
                        _switchReadyAt = now + SwitchCooldown;
                        _phase = Phase.Active;
                        return;
                    }
                    _phase = Phase.AwaitingCommit;
                    _claim?.Installed();
                }
                return;
            case Phase.AwaitingCommit:
                if (!agent.Alive) Abort();
                return;
            case Phase.Active:
                if (name != Marker(_lent!.Spec)) { SendLoan(LoanAction.Returned); End(); return; }
                if (!ready) return;
                var drop = ArsenalKeys.Drop.Pressed && FocusStateManager.CurrentState == eFocusState.FPS;
                if (_loan!.Update(now, instance!.GetCurrentClip(), backpack.AmmoStorage.SpecialAmmo.BulletsInPack, drop, !agent.Alive) != LoanEnd.None) { Return(now); return; }
                if (SwitchPressed(agent, instance, now)) Stash(backpack, instance, now);
                return;
            case Phase.Stashed:
                if (!agent.Alive || ArsenalKeys.Drop.Pressed && FocusStateManager.CurrentState == eFocusState.FPS)
                {
                    // Down or dropped: the heavy weapon is gone; the player's own weapon is already in the slot.
                    SendLoan(LoanAction.Returned);
                    End();
                    return;
                }
                if (!ready) return;
                if (TryMarker(name, out _))
                {
                    // Not expected while stashed (a checkpoint recall goes through reconciliation); put the player's weapon back.
                    _heavyStash = null;
                    Return(now);
                    return;
                }
                if (item.GearIDRange!.GetChecksum() != Slot.Own?.Checksum)
                {
                    SendLoan(LoanAction.Returned);
                    End();
                    return;
                }
                if (_resumeInHand)
                {
                    // Back from another lender's lease that began with the heavy weapon in hand: it comes back as it was.
                    _resumeInHand = false;
                    Unstash(now);
                    return;
                }
                if (SwitchPressed(agent, instance!, now)) Unstash(now);
                return;
            case Phase.Returning:
                var saved = Slot.Own;
                if (saved == null || !ready || TryMarker(name, out _) || item.GearIDRange!.GetChecksum() != saved.Checksum) return;
                if (!ForgeCore.Gear.WriteLocalAmmo(AmmoType.Special, instance!, saved.Pool, saved.Clip))
                    Plugin.Logger.ErrorOnce("The special weapon's ammunition did not read back as written.");
                Slot.OwnReturned();
                if (_heavyStash != null)
                {
                    agent.Sync.WantsToWieldSlot(InventorySlot.GearSpecial);
                    _phase = Phase.Stashed;
                    _switchReadyAt = now + SwitchCooldown;
                    SendLoan(LoanAction.Stashed);
                    return;
                }
                SendLoan(LoanAction.Returned);
                End();
                return;
        }
    }

    /// <summary>A newer lease (an exosuit's gun) holds the slot. A settled loan parks: the heavy weapon in hand keeps its magazine and
    /// reserve (read live until the newer lease swaps it out), the host's loan is kept as stashed, and nothing here touches the slot,
    /// the select key or a drop until the lease ends. A swap of this loan's own still on its way finishes first.</summary>
    private static void Suspend(float now)
    {
        switch (_phase)
        {
            case Phase.Suspended:
                if (_suspendedInHand && HeavyInSlot(out var b, out var w)) _heavyStash = new AmmoStash(b.AmmoStorage.SpecialAmmo.AmmoInPack, w.GetCurrentClip());
                return;
            case Phase.Reconciling:
                _lease!.Parked = true;
                return;
            case Phase.Active:
                if (!HeavyInSlot(out var backpack, out var heavy)) return;
                _heavyStash = new AmmoStash(backpack.AmmoStorage.SpecialAmmo.AmmoInPack, heavy.GetCurrentClip());
                _suspendedInHand = true;
                SendLoan(LoanAction.Stashed);
                break;
            case Phase.Stashed:
                _suspendedInHand = false;
                break;
            default:
                return;
        }
        _phase = Phase.Suspended;
        _lease!.Parked = true;
    }

    /// <summary>The newer lease ended (the player's own special weapon is back in the slot): the heavy weapon comes back if it was in hand.</summary>
    private static void Resume(float now)
    {
        _lease!.Parked = false;
        _phaseStarted = now;
        if (_phase != Phase.Suspended) return;
        _phase = Phase.Stashed;
        _resumeInHand = _suspendedInHand;
        _suspendedInHand = false;
        _switchReadyAt = now + SwitchCooldown;
    }

    private static bool HeavyInSlot(out PlayerBackpack backpack, out BulletWeapon heavy)
    {
        heavy = null!;
        if (!LoanedGun.TryLocalSpecial(out backpack, out var item) || !item!.IsLoaded || !TryMarker(item.GearIDRange?.PublicGearName, out _)) return false;
        var weapon = item.Instance?.TryCast<BulletWeapon>();
        if (weapon == null || weapon.WasCollected) return false;
        heavy = weapon;
        return true;
    }

    private static void Return(float now)
    {
        if (Slot.Own == null) { End(); return; }
        var agent = ForgeCore.Players.Local;
        if (agent != null && !agent.WasCollected)
        {
            // Heard by its own player only (2D): the loan is local state.
            ArsenalAudio.PlayAttached(ArsenalAudio.Cue.WeaponReturn, agent.gameObject, (int)agent.DimensionIndex, isSelf: true);
            StrikeBursts.ReturnGlow(agent, now);
        }
        _phase = Phase.Returning;
        _phaseStarted = now;
        _returnRequested = false;
        RequestReturn();
    }

    /// <summary>The select-special key again while the special weapon is already in hand (the game itself does nothing then).
    /// Not while reloading, so no magazine is read mid-transfer, and not right after a switch.</summary>
    private static bool SwitchPressed(PlayerAgent agent, BulletWeapon weapon, float now)
        => now >= _switchReadyAt && !weapon.IsReloading && FocusStateManager.CurrentState == eFocusState.FPS
            && agent.Inventory?.WieldedItem?.ItemDataBlock?.inventorySlot == InventorySlot.GearSpecial
            && InputMapper.GetButtonDown.Invoke(InputAction.SelectSpecial, eFocusState.FPS);

    /// <summary>Heavy weapon to the stash, the player's own special weapon back into the slot.</summary>
    private static void Stash(PlayerBackpack backpack, BulletWeapon heavy, float now)
    {
        _heavyStash = new AmmoStash(backpack.AmmoStorage.SpecialAmmo.AmmoInPack, heavy.GetCurrentClip());
        // Tell the host first: it must not read the marker leaving the slot as the loan ending.
        SendLoan(LoanAction.Stashed);
        _phase = Phase.Returning;
        _phaseStarted = now;
        _returnRequested = false;
        RequestReturn();
    }

    /// <summary>The player's own special weapon (live magazine and reserve saved) to the stash side, the heavy weapon back into the slot.</summary>
    private static void Unstash(float now)
    {
        var range = _expected!;
        LoanedGun.Require(range);
        LoanedGun.OwnLeaves();
        _phase = Phase.Equipping;
        _phaseStarted = now;
        LoanedGun.Equip(range);
    }

    private static void RequestReturn()
    {
        if (LoanedGun.EquipOwn()) _returnRequested = true;

    }

    private static void Abort()
    {
        if (_lease is { Suspended: true })
        {
            // The slot is another lender's now: give the heavy weapon up without touching it.
            Plugin.Logger.ErrorOnce("Heavy weapon loan failed while another lender held the special slot; the heavy weapon is given up.");
            SendLoan(LoanAction.Returned);
            End();
            return;
        }
        // Move out of the pending phase before the host synchronously broadcasts its cancellation.
        var restore = _lease != null && Slot.OwnAway && Slot.Own != null;
        _phase = restore ? Phase.Returning : Phase.None;
        // A failed switch back to a stashed heavy weapon is not a failed claim: the rack was taken long ago.
        if (_heavyStash == null) _claim?.Failed();
        if (!restore) { End(); return; }
        _phaseStarted = Time.time;
        _returnRequested = false;
        try { RequestReturn(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Original special weapon restoration remains pending: " + error); }
    }

    /// <summary>The loan is over: the slot's lease goes, and with no other lease left the own weapon is taken as being in the slot
    /// (special ammunition pickups go through the game again).</summary>
    private static void End()
    {
        var lease = _lease;
        // A swap this loan asked for and left in flight stops collecting the game's part spawners; a suspended lease's slot, and any swap in it, is another lender's.
        if (lease is { Suspended: false }) LoanedGun.EndSwap();
        Reset();
        lease?.Dispose();
        if (!Slot.Taken && Slot.OwnAway) Slot.OwnReturned();
    }

    private static void Reset()
    {
        _phase = Phase.None;
        _lent = null;
        _launcherIdentity = Guid.Empty;
        _lease = null;
        _suspendedInHand = _resumeInHand = false;
        _expected = null;
        _loan = null;
        _heavyStash = null;
        _switchReadyAt = 0f;
        _returnRequested = false;
        _claim = null;
    }

    // ---- the look in every player's hands -----------------------------------------------------------------------

    /// <summary>The moment any player's lent gun is assembled or raised it gets its model, so no peer sees the bare game gun. The scan is the retry for a model not yet ready.</summary>
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.GearPartsSpawned.After((parts, _) => SkinAtOnce(parts));
        ForgeCore.GameHooks.BulletWeaponWielded.After((weapon, _) => SkinAtOnce(weapon.GearPartHolder));
    }

    private static void SkinAtOnce(GearPartHolder? parts)
    {
        try
        {
            if (parts == null || ArsenalArt.Bundle == null || GunMount.Of(parts) != null || !TryMarker(parts.GearIDRange?.PublicGearName, out var heavy)) return;
            var gear = parts.GetComponentInParent<ItemEquippable>();
            if (gear == null) return;
            // Raised before its parts stand (another player's gun can be): nothing to seat on yet, and the parts-spawned hook comes.
            var nodes = GunNodes.Of(parts, gear);
            if (nodes.RightHand != null && nodes.LeftHand != null && nodes.Muzzle != null) Skin(gear, heavy);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("arsenal-heavy-look-failed", "heavy weapon look update failed: " + error); }
    }

    /// <summary>The game assembles a gun's parts over several frames, so a weapon gets its model only once its item reports it loaded,
    /// and every scan lets the mount hide parts the game added since and take over replaced nodes.</summary>
    private static void ScanSkins()
    {
        if (ArsenalArt.Bundle == null) return;
        PruneSkins();
        ForgeCore.Players.Each(static info => ScanSkin(info.Agent));
    }

    private static void ScanSkin(PlayerAgent player)
    {
        var owner = player.Owner;
        if (owner == null) return;
        if (!PlayerBackpackManager.TryGetBackpack(owner, out var backpack) || backpack == null) return;
        if (!backpack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item) || item == null || !item.IsLoaded) return;
        if (!TryMarker(item.GearIDRange?.PublicGearName, out var heavy)) return;
        var instance = item.Instance;
        if (instance == null || instance.WasCollected) return;
        if (Skins.TryGetValue(instance.Pointer, out var worn))
        {
            worn.Gun.Refresh();
            return;
        }
        Skin(instance, heavy);
    }

    /// <summary>The heavy weapon's model id when the game weapon at <paramref name="pointer"/> wears one of this package's looks (any player's).</summary>
    internal static bool TryWornModel(IntPtr pointer, out string model)
    {
        // A look from a level that ended is pruned only at the next scan; its address may already be another gun's.
        if (Skins.TryGetValue(pointer, out var worn) && worn.Gun.Alive) { model = worn.Heavy; return true; }
        model = "";
        return false;
    }

    private static void PruneSkins()
    {
        if (Skins.Count == 0) return;
        List<IntPtr>? gone = null;
        foreach (var pair in Skins)
            if (!pair.Value.Gun.Alive) (gone ??= new()).Add(pair.Key);
        if (gone != null) foreach (var key in gone) Skins.Remove(key);
    }

    /// <summary>The model goes on through ForgeVisual's gun mount, the same way as every gun model: this package gives only the model
    /// (its hand, bore and sight points are in the model), which way its export faces and how big it is in the hand.</summary>
    private static void Skin(Item gear, string heavy)
    {
        var model = ArsenalArt.Weapon(heavy);
        if (model == null || !model.Ready) return;
        var parts = gear.TryCast<ItemEquippable>()?.GearPartHolder;
        if (parts == null)
        {
            Plugin.Logger.ErrorOnce("The " + heavy + " in a player's hands has no gear part holder, so it shows the game's gun.");
            return;
        }
        if (GunMount.Of(parts) != null) return;
        try { Skins[gear.Pointer] = new WornLook { Gun = GunMount.Create(parts, model.Prefab, Mount), Heavy = heavy }; }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The " + heavy + " model could not go on the game's gun, which shows instead: " + error); }
    }

    /// <summary>Every heavy weapon model goes on the same way; its size and place in the hands are ForgeVisual's gun-holding rule.</summary>
    private static readonly GunMountOptions Mount = new()
    {
        Front = ArsenalArt.FrontToForward,
        Name = "ForgeOrdnance.Arsenal.WeaponLook",
        // Each model's game hold preset (Data/gun-grips.json); ForgeVisual keeps the model's hand sockets and derives a missing left hand from it.
        Grips = GunGrips.Read(typeof(HeavyWeapons).Assembly, "ForgeOrdnance.Arsenal.Data.gun-grips.json"),
        // Which ForgeVisual sight each model wears (Data/gun-sights.json).
        Sights = GunSights.Read(typeof(HeavyWeapons).Assembly, "ForgeOrdnance.Arsenal.Data.gun-sights.json"),
    };

    /// <summary>The length (m) of the heavy weapon model in <paramref name="player"/>'s hands as the rule shows it (a launcher's tube: muzzle to back);
    /// the rule's longest gun when that player holds no model here.</summary>
    internal static float HeldLength(ulong player)
    {
        var item = ForgeCore.Level.PlayerByLookup(player)?.Inventory?.WieldedItem;
        return item != null && Skins.TryGetValue(item.Pointer, out var worn) && worn.Gun.Alive ? worn.Gun.Hold.ShownLength : GunMount.Rules.LengthMax;
    }

    private static void Destroy(GameObject? root)
    {
        if (root != null && !root.WasCollected) Object.Destroy(root);
    }

    private static bool HostLoanStillPresent(ulong player)
    {
        var agent = ForgeCore.Level.PlayerByLookup(player);
        if (agent == null) return false;
        if (!PlayerBackpackManager.TryGetBackpack(agent.Owner, out var pack) || pack == null) return true;
        if (!pack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item) || item == null || !item.IsLoaded) return true;
        var gear = item.GearIDRange;
        if (gear == null) return true;
        // A stashed heavy weapon is not in the slot; the borrower's own special weapon is.
        return Loans.Observe(player, TryMarker(gear.PublicGearName, out _));
    }

    private static void ReleaseHostLoan(ulong player)
    {
        Launchers.Remove(player);
    }

    /// <summary>A host change. Every peer holds the same record of each rack still lying in its pod, so the racks stay and the new host opens them to
    /// take again; the loan book does not survive (a returning former host cannot trust its pre-migration book). Native backpack markers still prevent
    /// a player who is actually borrowing from taking another rack, and a launcher's borrower is adopted from the first shot (<see cref="TryAdoptLauncher"/>).
    /// Active/returning loans survive migration.</summary>
    private static void HostChanged()
    {
        UnavailableRacks.Clear();
        HostRacks.Clear();
        HostOpenPending.Clear();
        Launchers.Clear();
        AdoptedLaunchers.Clear();
        _adoptLaunchers = true;
        _promptRack = null;
        _promptText = _promptKey = "";
        if (_phase == Phase.None) _claim = null;
        Loans.Clear();
        if (_phase is Phase.Equipping or Phase.AwaitingCommit) Abort();
        var host = ForgeCore.Session.IsHost;
        var held = new HashSet<(ulong, uint)>();
        foreach (var rack in Racks) if (!rack.Taken) held.Add((rack.Summoner, rack.ThrowId));
        // A call whose racks were all taken but whose old host never told the pod it was empty: the pod leaves now.
        var emptied = new HashSet<(ulong, uint)>();
        foreach (var rack in Racks) if (rack.Pod != null && !held.Contains((rack.Summoner, rack.ThrowId))) emptied.Add((rack.Summoner, rack.ThrowId));
        Racks.RemoveAll(rack => rack.Taken);
        if (!host) return;
        foreach (var (summoner, throwId) in emptied) OrdnanceDelivery.HostReportPodEmpty(summoner, throwId);
        foreach (var rack in Racks)
        {
            var key = (rack.Summoner, rack.ThrowId);
            if (!HostRacks.TryGetValue(key, out var kept)) HostRacks[key] = kept = new Dictionary<int, byte[]>();
            var record = Wire.Rack(new RackRecord(rack.P.Spec, rack.P.GearId, rack.Landing.x, rack.Landing.y, rack.Landing.z, rack.Yaw, rack.Id));
            // Kept for a player who joins later, as the old host kept it.
            if (!OrdnanceApi.SendState(HeavyWeaponPayload.Type, rack.Summoner, rack.ThrowId, Tag(rack.Bay), record)) continue;
            kept[rack.Bay] = record;
            HostOpenPending.Add(rack);
        }
    }

    /// <summary>Host, every frame: opens to take the racks it adopted at a host change as soon as their pods are open.</summary>
    private static void OpenAdopted()
    {
        for (var i = HostOpenPending.Count - 1; i >= 0; i--)
        {
            var rack = HostOpenPending[i];
            if (rack.Claim == null || rack.Claim.IsTaken || !Racks.Contains(rack)) { HostOpenPending.RemoveAt(i); continue; }
            if (!rack.Claim.IsReady) continue;
            HostOpenPending.RemoveAt(i);
            OrdnanceDelivery.HostOpenClaim(rack.Summoner, rack.ThrowId, rack.Bay);
        }
    }

    /// <summary>Deliveries are transient; installed loans and checkpoint restoration have separate lifetimes.</summary>
    internal static void Clear(ArsenalVoid why)
    {
        if (why == ArsenalVoid.HostChange) { HostChanged(); return; }
        foreach (var rack in Racks) Destroy(rack.Look);
        Racks.Clear();
        UnavailableRacks.Clear();
        HostRacks.Clear();
        HostOpenPending.Clear();
        Launchers.Clear();
        AdoptedLaunchers.Clear();
        _adoptLaunchers = false;
        _promptRack = null;
        _promptText = _promptKey = "";
        if (_phase == Phase.None) _claim = null;
        Loans.Clear();
        OwnBlocks.Clear();
        OwnRecoil.Clear();
        if (why == ArsenalVoid.CheckpointReload)
        {
            // The framework has already put the shared slot back as it was at the checkpoint. A stashed heavy weapon does not survive
            // the reload; a heavy weapon the game brought back into the slot goes back for the own weapon as it was at the checkpoint.
            var reconcile = _phase != Phase.None || Slot.CheckpointHolder == SlotOwner;
            var lease = _lease;
            Reset();
            if (!reconcile) return;
            _lease = lease is { Held: true } ? lease : Slot.Take(SlotOwner, () => OrdnanceText.T("rack.inHand"));
            _lease.Parked = false;
            _phase = Phase.Reconciling;
            _phaseStarted = Time.time;
            return;
        }
        End();
    }
}

/// <summary>Why everything of Arsenal's in flight is dropped (<see cref="HeavyWeapons.Clear"/> treats each its own way).</summary>
internal enum ArsenalVoid { LevelCleanup, CheckpointReload, HostChange }
