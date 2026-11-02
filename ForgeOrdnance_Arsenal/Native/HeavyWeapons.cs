using System;
using System.Collections.Generic;
using System.Linq;
using ForgeOrdnance;
using ForgeVisual;
using GameData;
using Gear;
using Player;
using SNetwork;
using UnityEngine;
using LoanedGun = ForgeCore.LoanedGun;
using Object = UnityEngine.Object;

namespace ForgeOrdnanceArsenal;

/// <summary>Racks in drop pods (every peer; ForgeOrdnance does their claiming), the host's loan book, the local player's lent weapon, and the look of lent
/// weapons in every player's hands.</summary>
internal static class HeavyWeapons
{
    private const float SwapTimeout = 6f;
    private const float SkinScanSeconds = 0.5f;
    /// <summary>The look on each lent weapon in a player's hands, by the weapon item.</summary>
    private static readonly Dictionary<IntPtr, WornLook> Skins = new();

    /// <summary>A look and the game's own sight nodes it replaced (put back when the look comes off).</summary>
    private sealed class WornLook
    {
        internal Dressed Dressed = null!;
        /// <summary>The weapon whose sight nodes point at the model; null while they are the game's own.</summary>
        internal ItemEquippable? Gear;
        internal Transform? Sight, NativeHolderSight, NativeWeaponSight;
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
        internal bool Presented;
        /// <summary>Host: the launcher loan built from the claimant's installed gear, kept until the claim is committed.</summary>
        internal LauncherLoan? PendingLauncher;
        internal bool Taken => Claim?.IsTaken == true;
    }

    /// <summary><c>Stashed</c>: the player's own special weapon is in the slot and the heavy weapon waits, with its magazine and reserve, in
    /// <see cref="_heavyStash"/>. The select-special key switches between the two while the special weapon is in hand. <c>Suspended</c>:
    /// another plugin's newer lease holds the slot (an exosuit's gun); the heavy weapon waits in <see cref="_heavyStash"/> with the host's
    /// loan kept, and comes back into the slot when that lease ends if it was in hand when it began.</summary>
    private enum Phase { None, Equipping, AwaitingCommit, Active, Returning, Reconciling, Stashed, Suspended }

    private static readonly List<Rack> Racks = new();
    private static readonly HashSet<(ulong Summoner, uint ThrowId)> UnavailableRacks = new();
    private static readonly LoanBook Loans = new();
    private static readonly Dictionary<ulong, LauncherLoan> Launchers = new();
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
    /// <summary>The player's own special weapon and who holds the slot, shared with every other plugin that lends a gun into it.</summary>
    private static ForgeCore.SpecialSlot Slot => ForgeCore.Gear.SpecialSlot;
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

    internal static Guid ActiveLauncherIdentity => _launcherIdentity;

    /// <summary>Only the host's committed loan and native gear definition can authorize a grenade.</summary>
    internal static bool AuthorizeGrenade(ulong sender, ref GrenadeShot shot, out float delay, out string reason)
    {
        delay = 0f;
        reason = "sender is not the shooter or not a human player";
        if (sender != shot.Shooter || !ArsenalNetwork.IsPlayer(sender)) return false;
        reason = "no active host launcher loan";
        if (!Launchers.TryGetValue(sender, out var loan)) return false;
        var agent = ForgeCore.Level.PlayerByLookup(sender);
        reason = "launcher is not held by a live borrower";
        if (agent == null || !agent.Alive || agent.Inventory?.WieldedItem?.ItemDataBlock?.inventorySlot != InventorySlot.GearSpecial
            || !PlayerBackpackManager.TryGetBackpack(agent.Owner, out var pack) || pack == null
            || !pack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item) || item == null || !item.IsLoaded
            || item.GearIDRange?.GetChecksum() != loan.GearChecksum
            || item.GearIDRange.PublicGearName != Marker(loan.Spec)) return false;
        var direction = new Vector3(shot.VX, shot.VY, shot.VZ);
        reason = "invalid aim direction";
        if (!float.IsFinite(direction.sqrMagnitude) || direction.sqrMagnitude < 0.9f || direction.sqrMagnitude > 1.1f) return false;
        var origin = new Vector3(shot.OX, shot.OY, shot.OZ);
        if (!loan.TrySchedule(shot.Loan, shot.Sequence, Time.time, (origin - agent.EyePosition).sqrMagnitude, out delay, out reason)) return false;
        // The submitted origin is only a reach proof. Launch from the host-observed eye so the tolerance cannot move the
        // grenade through a thin wall.
        shot.OX = agent.EyePosition.x; shot.OY = agent.EyePosition.y; shot.OZ = agent.EyePosition.z;
        var grenade = loan.Spec.Grenade!;
        var velocity = direction.normalized * grenade.Speed + Vector3.up * 1.5f;
        shot.VX = velocity.x; shot.VY = velocity.y; shot.VZ = velocity.z;
        shot.Damage = grenade.Damage;
        shot.Radius = grenade.Radius;
        return true;
    }

    // ---- arrival (every peer) and claims -------------------------------------------------------------------------

    internal static void ShowRack(OrdnancePresentation p)
    {
        if (UnavailableRacks.Remove((p.SummonerLookup, p.ThrowId)))
        {
            return;
        }
        var rack = new Rack { Summoner = p.SummonerLookup, ThrowId = p.ThrowId, P = (HeavyParameters)p.Parameters, Id = p.Definition.Id, Definition = p.Definition, Fallback = p.Position, Presented = true };
        Racks.Add(rack);
        var pod = OrdnanceDelivery.StartPod(p);
        rack.Pod = pod;
        rack.Claim = AddClaimable(rack, pod, p.Position + Vector3.up * 0.4f, OrdnanceDelivery.PodContentsDelay(p.Arrival));
        if (pod == null)
        {
            // A direct arrival, or no drop pod art: the weapon lies on the landing point from the moment its contents are due.
            return;
        }
    }

    /// <summary>Hands the rack to ForgeOrdnance's claims: the pod's weapon bay (or the landing point without a pod) is where it can be taken.</summary>
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
        if (pod == null) return OrdnanceDelivery.AddClaimable(rack.Summoner, rack.ThrowId, position, openAfter, spec);
        rack.Bay = (int)(rack.ThrowId % (uint)pod.WeaponBayCount);
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
            var instance = model.Spawn(rack.Fallback + Vector3.up * 0.15f, ArsenalArt.FrontToForward);
            if (instance == null) return;
            instance.name = "ForgeOrdnanceArsenal.Rack";
            rack.Look = instance;
        }
    }

    internal static void HostArrived(OrdnanceExecution e)
    {
        var summoner = e.SummonerLookup;
        var throwId = e.ThrowId;
        var p = (HeavyParameters)e.Parameters;
        var record = Wire.Rack(new RackRecord(p.Spec, p.GearId, e.Position.x, e.Position.y, e.Position.z, e.Rotation.eulerAngles.y, e.Definition.Id));
        OrdnanceDelivery.HostAfter(OrdnanceDelivery.PodContentsDelay(e.Arrival), () =>
        {
            // Kept until the weapon is taken, so a player who joins meanwhile sees it and can take it.
            if (!OrdnanceApi.SendState(HeavyWeaponSpec.TypeName, summoner, throwId, (byte)DataTag.Rack, record))
            {
                OrdnanceApi.SendData(HeavyWeaponSpec.TypeName, summoner, throwId, (byte)DataTag.RackUnavailable, Array.Empty<byte>());
                OrdnanceDelivery.HostReportPodEmpty(summoner, throwId);
                return;
            }
            OrdnanceDelivery.HostOpenClaim(summoner, throwId);
        });
    }

    /// <summary>Host, after the platform's checks (alive, in reach, open, not reserved): a borrower cannot take a second weapon, and the
    /// gear's native effects must be loaded here.</summary>
    private static string? HostCheck(Rack rack, PlayerAgent agent)
    {
        if (HasBorrowedGear(agent) || (agent.Owner != null && Loans.HasLoan(agent.Owner.Lookup))) return "the requester is already borrowing a heavy weapon";
        try { LoanedGun.Require(BorrowedRange(rack.P)); }
        catch (Exception error)
        {
            Plugin.Error("Host refused unloaded heavy weapon effects: " + error);
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
        catch (Exception error) { Plugin.Error("Heavy weapon installation refused: " + error); Abort(); }
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
            var category = GearCategoryDataBlock.GetBlock(expected.GetCompID(eGearComponent.Category));
            var archetypeId = category == null ? 0u : (eWeaponFireMode)expected.GetCompID(eGearComponent.FireMode) switch
            {
                eWeaponFireMode.Semi => category.SemiArchetype,
                eWeaponFireMode.Burst => category.BurstArchetype,
                eWeaponFireMode.Auto => category.AutoArchetype,
                eWeaponFireMode.SemiBurst => category.SemiBurstArchetype,
                _ => 0u,
            };
            var archetype = ArchetypeDataBlock.GetBlock(archetypeId);
            var pool = pack.AmmoStorage.SpecialAmmo;
            // The same numbers the borrower's weapon is given (Tune): the vanilla block with this call-in's factory settings over it.
            var effective = archetype == null ? null : rack.P.Spec.Tuning.Over(Readings(archetype));
            if (effective == null || !effective.Valid()) return ClaimVerdict.Refuse("host gear has invalid ammunition or cadence");
            var rounds = effective.Rounds(rack.P.Spec.AmmoRatio, pool.AmmoMaxCap);
            if (!(rounds > 0 && rounds <= int.MaxValue)) return ClaimVerdict.Refuse("host gear has invalid ammunition or cadence");
            launcher = new LauncherLoan(rack.P.Spec, expected.GetChecksum(), (int)rounds, effective.ShotDelay, Time.time);
        }
        rack.PendingLauncher = launcher;
        return ClaimVerdict.Accept(launcher?.Identity ?? Guid.Empty);
    }

    /// <summary>Every peer: the rack was taken. The host starts the loan; the claimant's weapon becomes active.</summary>
    private static void Committed(Rack rack, ClaimResult result)
    {
        if (SNet.IsMaster)
        {
            Loans.Commit(rack.Summoner, rack.ThrowId, result.Player, result.Attempt);
            if (rack.PendingLauncher != null) Launchers[result.Player] = rack.PendingLauncher;
            rack.PendingLauncher = null;
            OrdnanceApi.ClearState(HeavyWeaponSpec.TypeName, rack.Summoner, rack.ThrowId);
            OrdnanceDelivery.HostReportPodEmpty(rack.Summoner, rack.ThrowId);
        }
        Destroy(rack.Look);
        rack.Look = null;
        if (!result.Ours || _phase != Phase.AwaitingCommit) return;
        _launcherIdentity = result.Token;
        _phase = Phase.Active;
        _loan = new HeavyLoan(Time.time);
        var agent = PlayerManager.GetLocalPlayerAgent();
        if (agent != null) ArsenalAudio.PlayAttached(ArsenalAudio.Cue.Handover, agent.gameObject,
            (int)agent.DimensionIndex, agent.IsLocallyOwned);
    }

    /// <summary>Host: a borrower's loan notice (returned, or switched to their own weapon).</summary>
    internal static void OnLoanNotice(ulong sender, LoanNotice m)
    {
        if (!SNet.IsMaster || !ArsenalNetwork.IsPlayer(sender)) return;
        if (m.Action == LoanAction.Stashed) Loans.Stash(m.Summoner, m.ThrowId, sender, m.Attempt);
        else if (Loans.Release(m.Summoner, m.ThrowId, sender, m.Attempt)) Launchers.Remove(sender);
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
        if ((DataTag)d.Tag == DataTag.Rack)
        {
            if (!Wire.TryRack(d.Data, out var record))
            {
                if (Wire.UnknownRackModel(d.Data, out var hash))
                    Plugin.Report("A heavy weapon rack was dropped: its model hash " + hash.ToString("x8") + " is not one of this install's heavy weapon models (the host's heavy weapon packs must be installed here too).");
                return;
            }
            if (!Racks.Exists(r => r.Summoner == d.SummonerLookup && r.ThrowId == d.ThrowId)) RackFromState(d, record);
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
    private static void RackFromState(OrdnancePayloadData d, in RackRecord record)
    {
        var landing = new Vector3(record.X, record.Y, record.Z);
        var rack = new Rack { Summoner = d.SummonerLookup, ThrowId = d.ThrowId, P = new HeavyParameters(record.Spec, record.GearId), Id = record.Id, Fallback = landing };
        Racks.Add(rack);
        var pod = OrdnanceDelivery.FindPod(d.SummonerLookup, d.ThrowId);
        rack.Pod = pod;
        rack.Claim = AddClaimable(rack, pod, landing + Vector3.up * 0.4f, 0f);
        // Without a replayed pod nothing will open it: the weapon lies where it landed.
        if (pod == null) OpenRack(rack);
    }

    // ---- the local player ---------------------------------------------------------------------------------------

    internal static void Tick(float now)
    {
        // The replicated backpack is authoritative for whether a loan still exists. A return notice is only the fast
        // path; this also releases the loan if that message was dropped or there was no master while it was sent.
        if (SNet.IsMaster && Loans.Any) Loans.ReconcileLoans(HostLoanStillPresent, ReleaseHostLoan);
        try { LocalLoan(now); }
        catch (Exception error) { Plugin.Error("Heavy weapon loan failed: " + error); Abort(); }
        if (now >= _nextSkinScan)
        {
            _nextSkinScan = now + SkinScanSeconds;
            try { ScanSkins(); }
            catch (Exception error) { Plugin.Error("Heavy weapon look update failed: " + error); }
        }
    }

    /// <summary>The prompt while the local player aims at a rack. Another plugin's lease on top (piloting an exosuit) means no heavy weapon
    /// can be taken, and the prompt says why. Text is rebuilt only when something it depends on changed.</summary>
    private static ClaimPrompt RackPrompt(Rack rack)
    {
        var top = Slot.Top;
        var refusal = top != null && top != _lease ? top : null;
        if (_promptRack != rack || _promptPhase != _phase || _promptLanguage != ArsenalText.Version || _promptRefusal != refusal)
        {
            _promptRack = rack;
            _promptPhase = _phase;
            _promptLanguage = ArsenalText.Version;
            _promptRefusal = refusal;
            _promptText = refusal != null ? ArsenalText.T("rack.slotTaken", refusal.Refusal)
                : _phase == Phase.None ? ArsenalText.T("rack.pickUp", rack.Definition != null ? OrdnanceApi.DisplayName(rack.Definition) : ArsenalText.CallIn(rack.Id)) : ArsenalText.T("rack.inHand");
            _promptKey = refusal == null && _phase == Phase.None ? "[E]" : "";
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

    /// <summary>The block's copyable properties, listed once (reflection is not repeated in the frame the gun is raised).</summary>
    private static readonly System.Reflection.PropertyInfo[] BlockProperties = typeof(ArchetypeDataBlock)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();

    /// <summary>The lent instance gets its own copy of the archetype with the call-in's factory settings over it (the shared block also
    /// drives the stock weapon and stays as the game has it). Everything not set stays native, and the game's own firing, hit and ammunition
    /// code reads the copy. Returns the numbers now in force, or null when the weapon has no archetype to tune.</summary>
    private static ArchetypeTuning? Tune(BulletWeapon weapon, HeavyWeaponSpec spec)
    {
        var archetype = weapon.m_archeType;
        var vanilla = archetype?.m_archetypeData;
        if (archetype == null || vanilla == null) { Plugin.Error("The heavy weapon has no archetype; its factory settings cannot be applied."); return null; }
        var native = Readings(vanilla);
        var effective = spec.Tuning.Over(native);
        if (spec.Tuning.IsEmpty) return effective;
        var own = new ArchetypeDataBlock();
        foreach (var property in BlockProperties) property.SetValue(own, property.GetValue(vanilla));
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
        own.persistentID = vanilla.persistentID;
        // The weapon holds the block twice and the game reads the two for different numbers: the archetype's copy for the firing rhythm and
        // spread, the item's copy (ItemEquippable.ArchetypeData) for damage, weak-spot and stagger multipliers, piercing and reload.
        // Swapping only the archetype's left a tuned damage and precision with no effect on the hit.
        archetype.m_archetypeData = own;
        weapon.ArchetypeData = own;
        if (archetype.m_archetypeData == null || weapon.ArchetypeData == null
            || !Readings(archetype.m_archetypeData).SameAs(effective) || !Readings(weapon.ArchetypeData).SameAs(effective))
            Plugin.Error("The heavy weapon's archetype did not read back with its factory settings.");
        return effective;
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
        _lease = Slot.Take(Plugin.Id, () => ArsenalText.T("rack.inHand"));
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
        if (SNet.IsMaster) OnLoanNotice(SNet.LocalPlayer.Lookup, message);
        else if (SNet.Master != null) ArsenalNetwork.ToHost(LoanNotice.Name, message);
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
            Plugin.Error("Checkpoint special weapon did not finish assembling; restoring the saved original weapon.");
            Return();
            return true;
        }
        var agent = PlayerManager.GetLocalPlayerAgent();
        if (agent == null || agent.WasCollected) { ReconciliationTimedOut(); return; }
        if (_phase is Phase.Equipping or Phase.AwaitingCommit && now - _phaseStarted > SwapTimeout)
            throw new InvalidOperationException("Heavy weapon installation/commit timed out.");
        if (_phase == Phase.Returning && now - _phaseStarted > SwapTimeout)
        {
            // Retain the managed restoration record and retry; never silently forget a broken return.
            Plugin.Error("Special weapon restoration is still pending; retrying.");
            _returnRequested = false;
            _phaseStarted = now;
        }
        if (_phase == Phase.Returning && !_returnRequested) RequestReturn();
        if (!LoanedGun.TryLocalSpecial(out var backpack, out var item)) { ReconciliationTimedOut(); return; }
        var name = item!.GearIDRange?.PublicGearName;
        // The game spawns the incoming gun's parts one per frame, with the hand empty meanwhile; they come in the frame the gun stands in the slot.
        if (_phase is Phase.Equipping or Phase.Returning) LoanedGun.FinishParts(item!);
        var ready = LoanedGun.Settled(item!, out var instance);

        if (_phase == Phase.Reconciling)
        {
            if (!ready) { ReconciliationTimedOut(); return; } // Native recall has finished, but gear assembly may still be asynchronous.
            if (TryMarker(name, out _)) Return();
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
                    var resume = _heavyStash;
                    var tuned = Tune(instance!, _lent.Spec);
                    // A weapon taken back from the stash keeps exactly the magazine and reserve it was switched away with.
                    if (!ForgeCore.Gear.WriteLocalAmmo(AmmoType.Special, instance!, resume?.Pool ?? _lent.Spec.AmmoRatio * backpack.AmmoStorage.SpecialAmmo.AmmoMaxCap,
                            resume?.Clip ?? (_lent.Spec.Tuning.IsEmpty || tuned == null ? instance!.GetMaxClip() : (int)tuned.Magazine)))
                        Plugin.Error("The heavy weapon's ammunition did not read back as written.");
                    // The model's sight node replaces the game's before the wield: the game reads it once, while the gun is raised, to place the gun
                    // and the arms. Dressed after the wield (the periodic scan) it left them where the hidden native sight put them until the
                    // next raise or aim.
                    ScanSkins();
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
                if (_loan!.Update(now, instance!.GetCurrentClip(), backpack.AmmoStorage.SpecialAmmo.BulletsInPack, drop, !agent.Alive) != LoanEnd.None) { Return(); return; }
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
                    Return();
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
                    // Back from another plugin's lease that began with the heavy weapon in hand: it comes back as it was.
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
                    Plugin.Error("The special weapon's ammunition did not read back as written.");
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

    private static void Return()
    {
        if (Slot.Own == null) { End(); return; }
        var agent = PlayerManager.GetLocalPlayerAgent();
        if (agent != null && !agent.WasCollected)
        {
            ArsenalAudio.PlayReturn(agent.gameObject, (int)agent.DimensionIndex);
            StrikeBursts.ReturnGlow(agent, Time.time);
        }
        _phase = Phase.Returning;
        _phaseStarted = Time.time;
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
            // The slot is another plugin's now: give the heavy weapon up without touching it.
            Plugin.Error("Heavy weapon loan failed while another plugin held the special slot; the heavy weapon is given up.");
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
        catch (Exception error) { Plugin.Error("Original special weapon restoration remains pending: " + error); }
    }

    /// <summary>The loan is over: the slot's lease goes, and with no other lease left the own weapon is taken as being in the slot
    /// (special ammunition pickups go through the game again).</summary>
    private static void End()
    {
        var lease = _lease;
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

    /// <summary>The game assembles a gun's parts over several frames, so a weapon is dressed only once its item reports it loaded, and
    /// every scan hides parts the game added since (a sight or stock that showed through the model, or a second gun overlapping it).
    /// When parts were added the look is put on again, so it takes the whole gun's length and place.</summary>
    private static void ScanSkins()
    {
        if (ArsenalArt.Bundle == null) return;
        PruneSkins();
        var players = PlayerManager.PlayerAgentsInLevel;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null || player.WasCollected || player.Owner == null) continue;
            if (!PlayerBackpackManager.TryGetBackpack(player.Owner, out var backpack) || backpack == null) continue;
            if (!backpack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item) || item == null || !item.IsLoaded) continue;
            if (!TryMarker(item.GearIDRange?.PublicGearName, out var heavy)) continue;
            var instance = item.Instance;
            if (instance == null || instance.WasCollected) continue;
            var late = 0;
            if (Skins.TryGetValue(instance.Pointer, out var worn))
            {
                late = worn.Dressed.HideLate();
                if (late == 0) continue;
                RestoreSight(worn);
                var look = worn.Dressed.Look;
                worn.Dressed.Undo();
                // Gone at once, so the new fit neither measures nor finds the old look.
                Object.DestroyImmediate(look);
                Skins.Remove(instance.Pointer);
            }
            else if (HasWeaponLook(instance.transform)) continue;
            Skin(instance, heavy, player.IsLocallyOwned, late);
        }
    }

    private static void PruneSkins()
    {
        if (Skins.Count == 0) return;
        List<IntPtr>? gone = null;
        foreach (var pair in Skins)
            if (!pair.Value.Dressed.Alive) (gone ??= new()).Add(pair.Key);
        if (gone != null) foreach (var key in gone) Skins.Remove(key);
    }

    private static bool HasWeaponLook(Transform gear)
    {
        for (var i = 0; i < gear.childCount; i++)
            if (gear.GetChild(i).gameObject.name == LookName) return true;
        return false;
    }

    private const string LookName = "ForgeOrdnanceArsenal.WeaponLook";

    /// <summary>The model goes where the game's hands are: its two hand sockets on the game gun's hand nodes (so the hands hold its pistol grip
    /// and fore grip in the first and third person views) and its bore on the game's bore; its magazine hangs on the game's magazine node,
    /// so the game's reload animation takes it off and puts it back.</summary>
    private static DressOptions SkinLook(GunNodes gun) => new()
    {
        HideNative = true, Layer = true, Rotation = ArsenalArt.FrontToForward, Name = LookName, Gun = gun,
    };

    /// <summary>The game gun's hand, bore and magazine nodes: the aligns the hands are put on, from the weapon itself or else its part holder.</summary>
    private static GunNodes NodesOf(ItemEquippable? weapon)
    {
        var parts = weapon != null ? weapon.GearPartHolder : null;
        return new GunNodes
        {
            RightHand = weapon != null && weapon.m_rightHandGripAlign != null ? weapon.m_rightHandGripAlign : parts?.m_aRightHand,
            LeftHand = weapon != null && weapon.m_leftHandGripAlign != null ? weapon.m_leftHandGripAlign : parts?.m_aLeftHand,
            Muzzle = weapon != null && weapon.MuzzleAlign != null ? weapon.MuzzleAlign : parts?.m_aMuzzle,
            Magazine = parts?.m_aMag,
        };
    }

    /// <summary>The model's aim point: an empty at the centre of its optic's window, with the model's front as its forward.</summary>
    internal const string SightSocket = "SightLook";

    /// <summary>Hides the game gear's meshes (effects stay) and puts this package's model in its place, on the gear's layer. The game aims
    /// a gun by lining its sight node up with the camera; the hidden game sight would put the camera inside the model, so the model's
    /// own <see cref="SightSocket"/> takes that role.</summary>
    private static void Skin(Item gear, string heavy, bool local, int late)
    {
        var model = ArsenalArt.Weapon(heavy);
        if (model == null) return;
        var dressed = Models.Dress(gear.gameObject, model, SkinLook(NodesOf(gear.TryCast<ItemEquippable>())));
        if (dressed == null) return;
        var worn = new WornLook { Dressed = dressed };
        Skins[gear.Pointer] = worn;
        AimThroughSight(worn, gear, heavy);
    }

    private static void AimThroughSight(WornLook worn, Item item, string heavy)
    {
        // The model was checked for this socket when its payload was accepted (ArsenalArt.CheckWeaponModel).
        var sight = Models.Find(worn.Dressed.Look.transform, SightSocket);
        if (sight == null) return;
        var gear = item.TryCast<ItemEquippable>();
        if (gear == null)
        {
            Plugin.Error("The " + heavy + " in a player's hands is not an equippable weapon; aiming lines up the game's hidden sight instead of the model's.");
            return;
        }
        var parts = gear.GearPartHolder;
        worn.NativeHolderSight = parts != null ? parts.m_aSightLook : null;
        worn.NativeWeaponSight = gear.SightLookAlign;
        worn.Gear = gear;
        worn.Sight = sight;
        if (parts != null) parts.m_aSightLook = sight;
        gear.SightLookAlign = sight;
    }

    /// <summary>Before a look comes off: the game's sight nodes again, unless the game has set its own since.</summary>
    private static void RestoreSight(WornLook worn)
    {
        if (worn.Gear == null || worn.Sight == null) return;
        var parts = worn.Gear.GearPartHolder;
        if (parts != null && parts.m_aSightLook == worn.Sight) parts.m_aSightLook = worn.NativeHolderSight!;
        if (worn.Gear.SightLookAlign == worn.Sight) worn.Gear.SightLookAlign = worn.NativeWeaponSight!;
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
        if (!pack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item) || item == null || !item.IsLoaded || item.GearIDRange == null) return true;
        // A stashed heavy weapon is not in the slot; the borrower's own special weapon is.
        return Loans.Observe(player, TryMarker(item.GearIDRange.PublicGearName, out _));
    }

    private static void ReleaseHostLoan(ulong player)
    {
        Launchers.Remove(player);
    }

    /// <summary>Deliveries are transient; installed loans and checkpoint restoration have separate lifetimes.</summary>
    internal static void Clear(string why)
    {
        foreach (var rack in Racks) Destroy(rack.Look);
        Racks.Clear();
        UnavailableRacks.Clear();
        Launchers.Clear();
        _promptRack = null;
        _promptText = _promptKey = "";
        if (_phase == Phase.None) _claim = null;
        if (why == "host change")
        {
            // A returning former host cannot trust its pre-migration loan book. Native backpack markers still prevent a
            // player who is actually borrowing from taking another rack.
            Loans.Clear();
            if (_phase is Phase.Equipping or Phase.AwaitingCommit) Abort();
            return; // Active/returning loans survive migration.
        }
        Loans.Clear();
        if (why == "checkpoint reload")
        {
            // The framework has already put the shared slot back as it was at the checkpoint. A stashed heavy weapon does not survive
            // the reload; a heavy weapon the game brought back into the slot goes back for the own weapon as it was at the checkpoint.
            var reconcile = _phase != Phase.None || Slot.CheckpointHolder == Plugin.Id;
            var lease = _lease;
            Reset();
            if (!reconcile) return;
            _lease = lease is { Held: true } ? lease : Slot.Take(Plugin.Id, () => ArsenalText.T("rack.inHand"));
            _lease.Parked = false;
            _phase = Phase.Reconciling;
            _phaseStarted = Time.time;
            return;
        }
        End();
    }
}
