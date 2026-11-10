using System;
using System.Collections.Generic;
using ForgeAudio;
using ForgeCore;
using Gear;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>A recorded player drawn without a player slot: a copy of an existing player's body model (clothes and animator included) as an
/// ordinary object, with no <c>PlayerAgent</c> and no <c>SNet_Player</c>, placed and animated from the recording alone. It is for a
/// recorded player the level has no bot slot for (the observer takes one of the four, the bots fill three). The copy looks like the
/// player it was copied from (the recording does not name a character), carries no script of the game, and holds nothing in its hands:
/// the donor's own wielded item is taken off the copy, since it would show every recorded player holding the donor's weapon. Its stance is the one the game plays for what the recording says the player
/// holds, on the body layers <c>PlayAnimationsForWieldedItem</c> uses, since nothing on the copy plays them. Main thread only.</summary>
internal sealed class CosmeticPlayer : IDisposable
{
    private readonly GameObject _body;
    private readonly Animator _animator;
    // The float parameters of the game's player controller (PlayerBaseRigController, read from its asset). The game no longer has Animator.parameters to list them.
    private static readonly int Forward = Animator.StringToHash("MoveBackwardForward"), Right = Animator.StringToHash("MoveLeftRight"), Crouch = Animator.StringToHash("Crouch");
    private float _crouchValue, _forward, _right;
    // The rate the game's own PlayerLocomotion.UpdateFullBodyAnimValue moves the walk values at.
    private const float WalkRate = 7.5f;
    // The game's body controller keeps movement, and the lying of a downed player, on this layer; the aim on AimLayer; a melee weapon's hold on UpperLayer.
    private const int MoveLayer = 1, AimLayer = 4, UpperLayer = 5;
    // Default_Movement, the state with empty hands, is the arms-out pose: the game's players always hold something, and so does the copy.
    private static readonly int GunMove = Animator.StringToHash("Rifle_Movement"), MeleeMove = Animator.StringToHash("Melee_Movement"), GunAim = Animator.StringToHash("RifleAimOffset"), MeleeHold = Animator.StringToHash("Sledgehammer_Idle");
    private bool _downed, _melee, _holding;
    private Quaternion _yaw = Quaternion.identity;

    // The weapon in the hands: the donor's hand socket (its clone on the copy) and the item's pose there, and the items built so far by recorded slot.
    private Transform? _socket;
    private Vector3 _socketPosition;
    private Quaternion _socketRotation = Quaternion.identity;
    private readonly Dictionary<int, (GameObject Item, Weapon? Gun, uint Id)> _items = new();
    private readonly HashSet<int> _asked = new();
    // The gear line each slot's weapon was asked for with: a slot whose line changes (a tool swapped in the level, another player in the seat) is built again.
    private readonly Dictionary<int, string> _lineOf = new();
    private int _wanted = -1;
    private readonly Vector3 _offset;
    private readonly Quaternion _turn;
    private CosmeticPlayer(GameObject body, Animator animator, Vector3 offset, Quaternion turn) { _body = body; _animator = animator; _offset = offset; _turn = turn; }

    /// <summary>The model object, for hiding it, outlining it and removing it.</summary>
    internal GameObject Body => _body;

    /// <summary>The body's animator, for the bones of its hands.</summary>
    internal Animator Animator => _animator;

    internal bool Held => _body != null && !_body.WasCollected;

    /// <summary>Copies the body of <paramref name="donor"/>; null (with an error line) when the donor has no body animator.
    /// The copy is the donor's whole model (the object the animator hangs in, with every mesh next to the skeleton) plus the clothes that hang
    /// outside it and are skinned to its bones, re-bound to the copy's own bones: nothing on the copy points at the donor, so what the game does
    /// to the donor (hides, rebuilds or moves it) does not reach the copy.</summary>
    internal static CosmeticPlayer? Make(PlayerAgent donor)
    {
        try
        {
            var source = donor.AnimatorBody;
            if (source == null || source.WasCollected) { Plugin.Logger.ErrorOnce("A cosmetic player could not be made: the donor player has no body animator."); return null; }
            var model = ModelRoot(donor, source.transform);
            // Copied under an inactive parent and stripped there, so no copied script of the game ever wakes: only what draws and animates stays,
            // and a script waking or going away on the copy would reach for the donor's player.
            var hold = new GameObject("ForgeReplay.CosmeticPlayerHold");
            hold.SetActive(false);
            var body = UnityEngine.Object.Instantiate(model.gameObject, hold.transform);
            body.name = "ForgeReplay.CosmeticPlayer";
            Strip(body);
            var animator = body.GetComponentInChildren<Animator>(true);
            if (animator == null) { UnityEngine.Object.Destroy(hold); Plugin.Logger.ErrorOnce("A cosmetic player could not be made: the copy has no animator."); return null; }
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var clothes = CopyClothes(donor, model, body.transform, hold.transform);
            // Where the model hangs against the donor's agent, so the copy's feet stand where the recording puts the player.
            var agent = donor.transform;
            var made = new CosmeticPlayer(body, animator, agent.InverseTransformPoint(model.position), Quaternion.Inverse(agent.rotation) * model.rotation);
            body.transform.SetParent(null, true);
            UnityEngine.Object.Destroy(hold);
            body.SetActive(true);
            made.Keep(clothes);
            made.DropHeld(donor, model, body.transform);
            return made;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A cosmetic player could not be made: " + error); return null; }
    }

    // The object the animator's body hangs in under the agent (the model's root, whose children are the skeleton and the meshes); the animator's own object when it hangs directly under the agent.
    private static Transform ModelRoot(PlayerAgent donor, Transform animator)
    {
        var root = animator;
        while (root.parent != null && root.parent != donor.transform && root.parent.name != "FlatTransform") root = root.parent;
        return root;
    }

    private static void Strip(GameObject body)
    {
        // Twice: a script another one requires goes only after that one.
        for (var pass = 0; pass < 2; pass++)
            foreach (var script in body.GetComponentsInChildren<MonoBehaviour>(true)) if (script != null) UnityEngine.Object.DestroyImmediate(script);
    }

    // The same place in the copy as in the donor's model: the child indices from the model's root down.
    private static Transform? Counterpart(Transform model, Transform copy, Transform? bone)
    {
        if (bone == null || !bone.IsChildOf(model)) return null;
        var path = new Stack<int>();
        for (var node = bone; node != model; node = node.parent) path.Push(node.GetSiblingIndex());
        var found = copy;
        foreach (var index in path) found = found.GetChild(index);
        return found;
    }

    // The clothes (and the pack on the back) are skinned meshes that hang outside the model's object with the model's bones as theirs: copied, and bound to the copy's bones.
    private static int CopyClothes(PlayerAgent donor, Transform model, Transform copy, Transform under)
    {
        var count = 0;
        foreach (var skin in donor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skin == null || skin.rootBone == null || !skin.rootBone.IsChildOf(model) || skin.transform.IsChildOf(model)) continue;
            var piece = UnityEngine.Object.Instantiate(skin.gameObject, under);
            Strip(piece);
            var made = piece.GetComponent<SkinnedMeshRenderer>();
            if (made == null) { UnityEngine.Object.Destroy(piece); continue; }
            var bones = skin.bones;
            var mapped = new Transform[bones.Length];
            for (var i = 0; i < mapped.Length; i++) mapped[i] = Counterpart(model, copy, bones[i])!;
            made.bones = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>(mapped);
            made.rootBone = Counterpart(model, copy, skin.rootBone);
            piece.transform.SetParent(copy, false);
            count++;
        }
        return count;
    }

    // What makes the copy draw whatever the donor was doing when it was copied: skinning goes on off screen, nothing is forced off, a level-of-detail group shows its best level.
    private void Keep(int clothes)
    {
        // Strip took every game script off the inactive copy; a corpse's decay component is the one that must never come
        // back with a body (it would initialise a decay on a renderer that is not a corpse). It goes before the copy shows.
        foreach (var decay in _body.GetComponentsInChildren<Decay>(true)) if (decay != null) UnityEngine.Object.DestroyImmediate(decay);
        var lods = 0; var off = 0; var layers = new HashSet<int>();
        foreach (var renderer in _body.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null) continue;
            renderer.forceRenderingOff = false;
            if (!renderer.enabled) off++;
            layers.Add(renderer.gameObject.layer);
            if (renderer.TryCast<SkinnedMeshRenderer>() is { } skin) skin.updateWhenOffscreen = true;
        }
        foreach (var group in _body.GetComponentsInChildren<LODGroup>(true)) if (group != null) { group.ForceLOD(0); lods++; }
        Plugin.Diagnostic.LogDebug("A body copy was made: " + _body.GetComponentsInChildren<Renderer>(true).Length + " renderers (" + clothes + " clothes re-bound), " + off + " off as copied, layers " + string.Join(",", layers) + ", " + lods + " LOD groups.");
    }
    // The donor's wielded item hangs on its hand bone: the same child path on the copy is its clone, which goes, and where it hung (its parent and its
    // pose there) is where the copy's own weapon is put.
    private void DropHeld(PlayerAgent donor, Transform source, Transform copy)
    {
        var held = donor.Inventory?.WieldedItem;
        if (held == null || held.WasCollected || !held.transform.IsChildOf(source)) { UseHand(); return; }
        var path = new Stack<int>();
        for (var node = held.transform; node != source; node = node.parent) path.Push(node.GetSiblingIndex());
        var clone = copy;
        foreach (var index in path) clone = clone.GetChild(index);
        if (clone == copy) { UseHand(); return; }
        _socket = clone.parent;
        _socketPosition = clone.localPosition;
        _socketRotation = clone.localRotation;
        UnityEngine.Object.DestroyImmediate(clone.gameObject);
    }

    // The donor held nothing the copy could take a socket from: the weapon goes to the right hand bone, with no pose of its own.
    private void UseHand()
    {
        _socket = _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
        if (_socket == null) Skipped.Note("a cosmetic player has no hand to hold a weapon in", _body.name);
    }

    /// <summary>Shows the weapon of gear line <paramref name="gearLine"/> (the recorded player's gear in recorded slot <paramref name="slot"/>) in the hands,
    /// built once through the game's own third-person assembly and kept; the one shown before goes away. Asked again each frame while its assets load.</summary>
    internal void Hold(int slot, string gearLine)
    {
        if (!Held || _socket == null) return;
        if (_lineOf.TryGetValue(slot, out var before) && before != gearLine)
        {
            if (_items.Remove(slot, out var old) && old.Item != null && !old.Item.WasCollected) UnityEngine.Object.Destroy(old.Item);
            _asked.Remove(slot); _lineOf.Remove(slot);
        }
        if (slot != _wanted)
        {
            _wanted = slot;
            foreach (var (key, shown) in _items) shown.Item.SetActive(key == slot);
        }
        if (_items.ContainsKey(slot) || _asked.Contains(slot) || gearLine.Length == 0) return;
        var gear = new GearIDRange(gearLine);
        var reading = ForgeCore.Gear.Prepare(gear);
        if (reading.Status == GearAssetStatus.Loading) return;
        _asked.Add(slot); _lineOf[slot] = gearLine;
        if (reading.Status == GearAssetStatus.Failed) { Skipped.Note("a cosmetic player could not take its gear", reading.Detail); return; }
        GearManager.AssembleGearAsync(gear, ItemMode.ThirdPerson, new Action<ItemEquippable>(item => Made(slot, gearLine, item)));
    }

    private void Made(int slot, string gearLine, ItemEquippable item)
    {
        try
        {
            if (item == null || !Held) return;
            // The line changed while it was built: this one is old, and goes.
            if (!_lineOf.TryGetValue(slot, out var current) || current != gearLine) { UnityEngine.Object.Destroy(item.gameObject); return; }
            var root = item.gameObject;
            // Only what draws stays awake: no script of the item runs without an owner.
            // The muzzle flash's own script stays on: its Update is what ends a flash after its life time, and a flash played with that script
            // off never went out (the gun's flash stayed lit the whole replay).
            foreach (var script in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (script != null && script.TryCast<EX_SpriteMuzzleFlash>() == null) script.enabled = false;
            item.TryCast<Weapon>()?.m_muzzleFlash?.Stop();
            foreach (var collider in root.GetComponentsInChildren<Collider>(true)) if (collider != null) collider.enabled = false;
            root.transform.SetParent(_socket, false);
            root.transform.localPosition = _socketPosition;
            root.transform.localRotation = _socketRotation;
            root.SetActive(slot == _wanted);
            _items[slot] = (root, item.TryCast<Weapon>(), item.ItemDataBlock?.persistentID ?? 0);
        }
        catch (Exception error) { Skipped.Note("a cosmetic player's weapon could not be put in its hands", error.Message); }
    }

    // The game's flashlight, read from a running player (PlayerInventoryBase.m_flashlight): a spot light with these values. It hangs under the copy's head along the look direction.
    private const float LampRange = 11f, LampAngle = 40f, LampIntensity = 0.35f, LampForward = 0.2f;
    private static readonly Color LampColour = new(1f, 0.963f, 0.731f, 1f);
    private Light? _lamp;
    private Transform? _lampAt;
    private bool _lampOn;
    private Transform? _head;
    private Vector3 _look = Vector3.forward;

    /// <summary>The player's flashlight on or off: the copy has no player inventory to switch, so it carries a spot light of the game's own flashlight values.</summary>
    internal void Lamp(bool on)
    {
        if (!Held) return;
        if (_lamp == null)
        {
            if (!on) return;
            _head = _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.Head) : null;
            var holder = new GameObject("ForgeReplay.CosmeticLamp");
            holder.transform.SetParent(_body.transform, false);
            _lampAt = holder.transform;
            _lamp = holder.AddComponent<Light>();
            _lamp.type = LightType.Spot; _lamp.range = LampRange; _lamp.spotAngle = LampAngle; _lamp.intensity = LampIntensity; _lamp.color = LampColour; _lamp.shadows = LightShadows.None;
        }
        _lamp.enabled = _lampOn = on;
    }

    // Every frame: the light follows the head (with none, the point 1.6 m over the body), kept in plain fields so only the head's position is read.
    private void PlaceLamp(Vector3 body)
    {
        var sqr = Vec.SqrLength(_look);
        if (_lampAt == null || !_lampOn || sqr < 1e-6f) return;
        var at = (_head != null ? _head.position : new Vector3(body.x, body.y + 1.6f, body.z)) + _look * (LampForward / Mathf.Sqrt(sqr));
        _lampAt.SetPositionAndRotation(at, Quaternion.LookRotation(_look));
    }

    // The sound engine's character switches by recorded slot: the game gives each player slot its own character voice.
    private static readonly uint[] Characters = { AK.SWITCHES.CHARACTER.SWITCH.CH_01, AK.SWITCHES.CHARACTER.SWITCH.CH_02, AK.SWITCHES.CHARACTER.SWITCH.CH_03, AK.SWITCHES.CHARACTER.SWITCH.CH_04 };

    /// <summary>The player says one of the game's voice lines (Wwise event <paramref name="eventId"/>) at the copy's head, as the character of recorded slot
    /// <paramref name="slot"/>: the copy has no <c>PlayerVoice</c> to say it through, so the line is posted at its place with that character's switch.</summary>
    internal void Say(int slot, uint eventId)
    {
        if (!Held) return;
        var at = _head != null ? _head.position : _body.transform.position + Vector3.up * 1.6f;
        AudioKit.PostGameEvent(eventId, at, AK.SWITCHES.CHARACTER.GROUP, Characters[slot & 3]);
    }

    // The third-person state a body plays for an event of the recording: (event kind, its type number) to (layer, state hash). Filled only with numbers read
    // from a remote player's body (AnimatorStateProbe writes them to replay/animator-states.log); a body is never made to play a state nobody read.
    private static readonly Dictionary<(ushort Kind, ulong Type), (int Layer, int State)> Actions = new();

    /// <summary>Plays the body state read for this event; false (nothing played) when none was read.</summary>
    internal bool Act(ushort kind, ulong type)
    {
        if (!Held || !Actions.TryGetValue((kind, type), out var action)) return false;
        _animator.Play(action.State, action.Layer, 0f);
        return true;
    }

    /// <summary>The name of the item shown in the hands now, "" when none (for the probe).</summary>
    internal string HeldName => _items.TryGetValue(_wanted, out var shown) && shown.Item != null ? shown.Item.name : "";

    /// <summary>The object of the weapon shown in the hands now, null when none: what a pack or carried item takes the place of.</summary>
    internal GameObject? HeldWeapon => _items.TryGetValue(_wanted, out var shown) && shown.Item != null && !shown.Item.WasCollected ? shown.Item : null;

    /// <summary>The data block ID of the item the game assembled for the weapon shown, 0 when none (for the probe).</summary>
    internal uint HeldId => _items.TryGetValue(_wanted, out var shown) ? shown.Id : 0;

    /// <summary>The recorded slot whose weapon is shown, -1 when none.</summary>
    internal int HeldSlot => _wanted;

    /// <summary>Plays the muzzle flash of the weapon in the hands (no shot, no sound); false when it has none.</summary>
    internal bool MuzzleFlash(out Vector3 muzzle)
    {
        muzzle = _body != null ? _body.transform.position : Vector3.zero;
        if (!_items.TryGetValue(_wanted, out var shown) || shown.Gun == null || shown.Gun.WasCollected || shown.Gun.m_muzzleFlash == null) return false;
        try
        {
            shown.Gun.m_muzzleFlash.Play();
            if (shown.Gun.MuzzleAlign != null) muzzle = shown.Gun.MuzzleAlign.position;
            return true;
        }
        catch (Exception error) { Skipped.Note("a cosmetic player's muzzle flash could not play", error.Message); return false; }
    }
    /// <summary>Puts the body at <paramref name="position"/> turned along <paramref name="look"/> and animates it with the game's walk values
    /// <paramref name="forward"/> and <paramref name="right"/>, crouched by <paramref name="crouch"/> (0 standing, 1 crouched), holding
    /// a melee weapon (<paramref name="melee"/>) or a gun.</summary>
    internal void Drive(Vector3 position, Vector3 look, float forward, float right, float crouch, float rate, bool downed, bool melee)
    {
        if (!Held) return;
        if (downed != _downed)
        {
            _downed = downed;
            if (downed)
            {
                // Playing: the fall. Paused, a jump lands on its last frame, the pose on the floor.
                _animator.Play(PlayerPuppet.LyingState, MoveLayer, rate > 0f ? 0f : 1f);
                _animator.SetLayerWeight(AimLayer, 0f); _animator.SetLayerWeight(UpperLayer, 0f);
            }
            _holding = false;
            // Paused: the animator is at speed 0, and a jump must still show the pose.
            if (rate == 0f) { _animator.speed = 1f; _animator.Update(1f); }
        }
        if (!downed && (!_holding || melee != _melee))
        {
            _holding = true; _melee = melee;
            _animator.Play(melee ? MeleeMove : GunMove, MoveLayer);
            _animator.SetLayerWeight(AimLayer, melee ? 0f : 1f);
            _animator.SetLayerWeight(UpperLayer, melee ? 1f : 0f);
            if (melee) _animator.Play(MeleeHold, UpperLayer); else _animator.Play(GunAim, AimLayer);
            if (rate == 0f) { _animator.speed = 1f; _animator.Update(1f); }
        }
        _look = look;
        var flat = new Vector3(look.x, 0f, look.z);
        if (Vec.SqrLength(flat) > 1e-6f) _yaw = Quaternion.LookRotation(flat);
        _body.transform.SetPositionAndRotation(position + _yaw * _offset, _yaw * _turn);
        _animator.speed = rate;
        _forward = Mathf.MoveTowards(_forward, forward, WalkRate * ForgeCore.Frame.UnscaledDeltaTime);
        _right = Mathf.MoveTowards(_right, right, WalkRate * ForgeCore.Frame.UnscaledDeltaTime);
        _animator.SetFloat(Forward, _forward);
        _animator.SetFloat(Right, _right);
        _crouchValue = Mathf.MoveTowards(_crouchValue, crouch, 6f * ForgeCore.Frame.UnscaledDeltaTime);
        _animator.SetFloat(Crouch, _crouchValue);
        PlaceLamp(position);
    }

    private float _drawnAt;

    /// <summary>A present player's copy draws, whatever switched it off (only the eyes view and the player's absence may; the caller says when
    /// those apply). Looked at every two seconds; what was found off is turned on and said once with what it was, so a copy that is not seen has
    /// its cause in the log (a copy made while the donor's body was hidden took that over).</summary>
    internal void KeepDrawn(int slot)
    {
        if (!Held || ForgeCore.Frame.UnscaledTime < _drawnAt) return;
        _drawnAt = ForgeCore.Frame.UnscaledTime + 2f;
        var renderers = _body.GetComponentsInChildren<Renderer>(true);
        var on = 0;
        foreach (var renderer in renderers) if (renderer != null && renderer.enabled) on++;
        if (on > 0 && _body.activeInHierarchy) return;
        foreach (var renderer in renderers) if (renderer != null) renderer.enabled = true;
        _body.SetActive(true);
        Plugin.Logger.ErrorOnce("The body copy of player " + (slot + 1) + " had nothing drawn while present: " + renderers.Length + " renderers, " + on + " enabled, body active "
            + _body.activeInHierarchy + ", layer " + _body.layer + ", at " + _body.transform.position + "; all were turned on.");
    }

    public void Dispose()
    {
        if (Held) UnityEngine.Object.Destroy(_body); // its weapons hang under it and go with it
    }
}
