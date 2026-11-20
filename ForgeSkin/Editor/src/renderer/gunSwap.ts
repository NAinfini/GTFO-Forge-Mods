// 整把枪 · 整枪换模 (PLAN §4.2): an imported model in place of the whole gun, with the existing pieces only. The model is an
// ordinary attachment on the receiver's root (else the first part's), every part of the game's gun is hidden (a part's hiding
// leaves the skin's attachments), and the four hold points are their nodes' part layout. Where the model and the hold points go
// is the backend's one rule (gun.seat: ForgeCore GunHold); this module only reads the 3D view's gear frame for it and writes the
// answer. The model's socket nodes and bounds are kept in the editor state (never packed) for 重新自动摆.
import { Box3, Matrix4, Quaternion, Vector3 } from 'three';
import { call, type GunSeat, type HoldPoint, type MeshImport, type PartTransform, type SkinDocument } from './protocol';
import { dropAttachment, dropEmptyParts, partTransformOf, useStore } from './store';
import { overridesFor, threeToUnity, unityToThree } from './space';
import { tidySight } from './sight';
import { t } from './i18n';
import { displayOffset, partBox, partTarget, viewRefs } from './viewport/refs';

interface WholeGun {
  attachment: string;
  sockets: MeshImport['sockets'];
  bounds: MeshImport['bounds'];
}

/** The skin's whole-gun model, while its attachment is there. */
export function wholeGunOf(doc: SkinDocument): WholeGun | null {
  const w = (doc.editor as { wholeGun?: WholeGun } | null | undefined)?.wholeGun;
  return w && doc.attachments?.some((a) => a?.id === w.attachment) ? w : null;
}

/** The game's parts of the open gun that have a node in the 3D view (ammo pieces are inside the mag). */
function gunParts(): string[] {
  const p = useStore.getState().preview;
  return (p?.parts ?? []).filter((x) => !x.ammo && partTarget(x.id, null)).map((x) => x.id);
}

/** Where the model hangs: the receiver's root, else the first part's. */
const anchorPart = (parts: string[]) => (parts.includes('receiver') ? 'receiver' : parts[0]);

/** A hold point's game pose (its node without the skin's layout) in the gear's frame, Unity space. */
function nativePose(point: HoldPoint): { at: number[]; rotation: number[]; part: string; path: string } | null {
  const root = viewRefs.root;
  const key = useStore.getState().preview?.points?.find((p) => p.point === point);
  const obj = key && partTarget(key.part, key.path);
  const vanilla = obj && viewRefs.vanilla.get(obj);
  if (!root || !key || !obj?.parent || !vanilla) return null;
  root.updateMatrixWorld(true);
  const local = new Matrix4().compose(vanilla.position.clone().add(displayOffset(obj)), vanilla.quaternion, vanilla.scale);
  const inGear = new Matrix4().copy(root.matrixWorld).invert().multiply(obj.parent.matrixWorld).multiply(local);
  const p = new Vector3(),
    q = new Quaternion();
  inGear.decompose(p, q, new Vector3());
  // The preview mirrors Unity across Z (space.ts).
  return { at: [p.x, p.y, -p.z], rotation: [-q.x, -q.y, q.z, q.w], part: key.part, path: key.path };
}

/** The game's gun's bounds in the gear's frame, Unity space. */
function gunBounds(parts: string[]): { min: number[]; max: number[] } | null {
  const root = viewRefs.root;
  if (!root) return null;
  const box = new Box3();
  for (const id of parts) box.union(partBox(id, root));
  return box.isEmpty() ? null : { min: [box.min.x, box.min.y, -box.max.z], max: [box.max.x, box.max.y, -box.min.z] };
}

/** A gear-frame Unity point as a world point of the 3D view. */
const world = (at: number[]) => viewRefs.root!.localToWorld(new Vector3(at[0], at[1], -at[2]));

/** Writes the seat into `d`: the model's pose on its anchor, every part hidden, the hold points moved (their game rotation kept). */
function apply(d: SkinDocument, id: string, seat: GunSeat, parts: string[]) {
  const a = d.attachments?.find((x) => x?.id === id);
  const anchor = a && partTarget(a.part ?? '', null);
  const root = viewRefs.root;
  if (!a || !anchor || !root) return;
  // The model turned as the seat says in the gear's frame, at the seat's scale.
  const own = unityToThree({ position: [0, 0, 0], rotation: seat.rotation, scale: [1, 1, 1] }).quaternion;
  const turn = anchor.getWorldQuaternion(new Quaternion()).invert().multiply(root.getWorldQuaternion(new Quaternion())).multiply(own);
  const size = (seat.scale * root.getWorldScale(new Vector3()).x) / anchor.getWorldScale(new Vector3()).x;
  const pose = threeToUnity(anchor.worldToLocal(world(seat.origin)), turn, new Vector3(size, size, size));
  a.transform = null;
  a.position = pose.position;
  a.rotation = pose.rotation;
  a.scale = pose.scale;
  const entry = (part: string, path: string | null): PartTransform => {
    let e = partTransformOf(d, part, path);
    if (!e) d.parts!.push((e = { part, path, position: null, rotation: null, scale: null, visible: null }));
    return e;
  };
  for (const p of parts) entry(p, null).visible = false;
  const points: [HoldPoint, number[] | null][] = [
    ['rightHand', seat.rightHand],
    ['leftHand', seat.leftHand],
    ['muzzle', seat.muzzle],
    ['sight', seat.sight],
  ];
  for (const [point, at] of points) {
    const key = useStore.getState().preview?.points?.find((p) => p.point === point);
    // A point the model does not give stays the game's.
    if (key) placePoint(d, key, at && world(at));
  }
  dropEmptyParts(d);
}

/** Puts the point node `key` (a hold point or a flashlight's light) at `at`, a world point of the 3D view, as its part layout
 * entry's position in `d`; its game rotation is kept. Null puts it back where the game has it. False when the node is not in
 * the view. */
export function placePoint(d: SkinDocument, key: { part: string; path: string }, at: Vector3 | null): boolean {
  const obj = partTarget(key.part, key.path);
  const vanilla = obj && viewRefs.vanilla.get(obj);
  if (!obj?.parent || !vanilla) return false;
  let e = partTransformOf(d, key.part, key.path);
  if (!e) d.parts!.push((e = { part: key.part, path: key.path, position: null, rotation: null, scale: null, visible: null }));
  e.position = at ? overridesFor(vanilla, threeToUnity(obj.parent.worldToLocal(at.clone()).sub(displayOffset(obj)), vanilla.quaternion, vanilla.scale)).position : null;
  e.rotation = null;
  return true;
}

/** The seat of the model with `sockets` and `bounds` at `scale` (null: the rule's own), or null with a message logged. */
async function seat(sockets: MeshImport['sockets'], bounds: MeshImport['bounds'], scale: number | null, parts: string[]): Promise<GunSeat | null> {
  const right = nativePose('rightHand');
  const left = nativePose('leftHand');
  const box = gunBounds(parts);
  if (!right || !left || !box) {
    useStore.getState().toast('error', t('wholeGun.noHands'));
    return null;
  }
  return call('gun.seat', {
    native: { rightHand: right, leftHand: left, muzzle: nativePose('muzzle')?.at ?? null, sight: nativePose('sight')?.at ?? null, bounds: box },
    model: { sockets, bounds },
    scale,
  });
}

function report(r: GunSeat) {
  const s = useStore.getState();
  const found = r.sockets.length ? r.sockets.map((p) => t(`leftRail.hold.${p}`)).join(', ') : t('wholeGun.noSockets');
  s.log(t('wholeGun.seated', { v0: found, v1: r.shownLength.toFixed(2) }));
  if (r.shownLength < r.lengthMin || r.shownLength > r.lengthMax) s.toast('info', t('wholeGun.length', { v0: r.shownLength.toFixed(2), v1: r.lengthMin.toFixed(2), v2: r.lengthMax.toFixed(2) }));
}

/** 导入整枪模型: a GLB in place of the whole gun (one already there is replaced), seated by the rule. One undo step. */
export async function importWholeGun(file?: string): Promise<void> {
  const s = useStore.getState();
  const [glb] = file ? [file] : await window.forge.openDialog('file', [{ name: t('actions.k24'), extensions: ['glb', 'gltf'] }]);
  if (!glb) return;
  const parts = gunParts();
  if (!parts.length) return s.toast('error', t('wholeGun.noHands'));
  useStore.setState({ busy: t('actions.k25') });
  try {
    const r = await call('mesh.import', { glb });
    const placed = await seat(r.sockets, r.bounds, null, parts);
    if (!placed) return;
    const old = wholeGunOf(s.doc)?.attachment;
    s.addAttachment('mesh', { forgeskinmesh: r.forgeskinmesh, glb: r.glb }, (a, ed, d) => {
      a.material = { ...a.material, textures: r.textures, color: r.color ?? [1, 1, 1, 1] };
      if (r.emissive) a.material.params = { ...a.material.params, emissiveEnabled: true, emissiveColor: r.emissive.color.slice(0, 3), emissiveIntensity: r.emissive.intensity };
      a.name = t('wholeGun.name');
      a.part = anchorPart(parts);
      if (old) dropAttachment(d, old);
      ed.wholeGun = { attachment: a.id!, sockets: r.sockets, bounds: r.bounds } satisfies WholeGun;
      apply(d, a.id!, placed, parts);
    });
    report(placed);
    for (const n of r.notes) s.log(t('actions.k7', { v0: n }));
  } catch (e) {
    s.fail(e);
  } finally {
    useStore.setState({ busy: null });
  }
}

/** 重新自动摆: the rule again at the model's current size (after the author scaled it). One undo step. */
export async function reseatWholeGun(): Promise<void> {
  const s = useStore.getState();
  const w = wholeGunOf(s.doc);
  const a = w && s.doc.attachments?.find((x) => x?.id === w.attachment);
  const anchor = a && partTarget(a.part ?? '', null);
  if (!w || !a || !anchor || !viewRefs.root) return;
  const parts = gunParts();
  try {
    const size = ((a.scale?.[0] ?? 1) * anchor.getWorldScale(new Vector3()).x) / viewRefs.root.getWorldScale(new Vector3()).x;
    const placed = await seat(w.sockets, w.bounds, size, parts);
    if (!placed) return;
    s.edit((d) => apply(d, w.attachment, placed, parts));
    report(placed);
  } catch (e) {
    s.fail(e);
  }
}

/** 撤掉整枪模型: the model goes, the gun's parts show again and the hold points go back to the game's. One undo step. */
export function removeWholeGun() {
  const s = useStore.getState();
  const w = wholeGunOf(s.doc);
  if (!w) return;
  const parts = new Set(gunParts());
  const points = new Set((s.preview?.points ?? []).map((p) => `${p.part}/${p.path}`));
  s.edit((d) => {
    dropAttachment(d, w.attachment);
    for (const e of d.parts ?? []) {
      if (!e) continue;
      if (e.path == null && parts.has(e.part ?? '')) e.visible = null;
      if (points.has(`${e.part}/${e.path}`)) e.position = e.rotation = null;
    }
    dropEmptyParts(d);
    if (d.sight?.lens?.attachment === w.attachment) d.sight.lens = null;
    tidySight(d);
    delete (d.editor as { wholeGun?: WholeGun }).wholeGun;
  });
  if (s.selection?.kind === 'attachment' && s.selection.id === w.attachment) s.select(null);
}
