// Replacing a part's models with imported .glb files: one node's own model (the node keeps its material, children and layout), or
// the whole part (its root hidden and the model hung on it, as the whole-gun model is). A part root is often an empty node with
// its models on the nodes under it, so a node without a model of its own offers those; what the game cannot swap says why.
import { Button, Group, Stack, Text } from '@mantine/core';
import { IconArrowBackUp, IconBox, IconBulb, IconReplace } from '@tabler/icons-react';
import { Vector3, type Object3D } from 'three';
import { importMesh } from '../actions';
import { placePoint } from '../gunSwap';
import { lightPoint, pieceLabel } from '../pieces';
import { baseName, call, type MeshImport, type PartTransform, type SkinDocument } from '../protocol';
import { partTransformOf, useStore } from '../store';
import { ownMeshes, partTarget, useSceneVersion } from '../viewport/refs';
import { Section } from './Features';
import { t } from '../i18n';

/** What replacing a node's own model can do: replace it, or why not. The plugin swaps the model of a plain renderer only, so a
 * skinned model cannot be replaced, and a node may have no model at all. */
export type ModelState = 'ok' | 'skinned' | 'empty';

export function modelState(part: string, path?: string | null): ModelState {
  const node = partTarget(part, path);
  if (!node || !ownMeshes(node).length) return 'empty';
  const own = useStore
    .getState()
    .preview?.parts.find((p) => p.id === part)
    ?.transforms.find((x) => (x.path ?? null) === (path ?? null));
  return own?.skinned ? 'skinned' : 'ok';
}

/** The pieces under (part, path) with a model of their own (under the root: every piece of the part), each with its state. */
export function modelPieces(part: string, path?: string | null): { path: string; state: ModelState }[] {
  const list = useStore.getState().preview?.parts.find((p) => p.id === part)?.transforms ?? [];
  return list.flatMap((x) => (x.path && (!path || x.path.startsWith(`${path}/`)) ? [{ path: x.path, state: modelState(part, x.path) }] : [])).filter((x) => x.state !== 'empty');
}

/** The name of the model on a piece (the mesh asset's), or empty. */
const pieceModel = (part: string, path: string) =>
  useStore
    .getState()
    .preview?.parts.find((p) => p.id === part)
    ?.transforms.find((x) => x.path === path)?.model ?? '';

/** Why a replace entry is off, for people. */
export const whyNot = (state: ModelState) => (state === 'skinned' ? t('partModel.skinned') : t('partModel.empty'));

const entry = (d: SkinDocument, part: string, path: string | null): PartTransform => {
  let e = partTransformOf(d, part, path);
  if (!e) d.parts!.push((e = { part, path, position: null, rotation: null, scale: null, visible: null }));
  return e;
};

/** Where an imported model with a `light` node puts the part's light: that node, a Unity point in `frame`'s space, as a world
 * point of the 3D view (`turned`: the model hangs half round Y, as an attachment in the glTF way does). */
function lightFrom(r: MeshImport, frame: Object3D | null, turned: boolean): Vector3 | null {
  const at = r.sockets.light;
  if (!at || !frame) return null;
  return frame.localToWorld(turned ? new Vector3(-at[0], at[1], at[2]) : new Vector3(at[0], at[1], -at[2]));
}

/** After a flashlight's model changed without a light node: the light still shines from where it was; select it to be dragged. */
function showLight(part: string) {
  const s = useStore.getState();
  const key = lightPoint(part);
  if (!key) return;
  s.toast('info', t('partModel.lightStays'));
  s.select({ kind: 'part', id: key.part, sub: key.path });
}

/** Picks a .glb and puts it in place of the node's own model. On a flashlight the light goes to the model's `light` node (else
 * the author is told and given the light to drag), and the lens pieces can be hidden with the lamp body replaced. */
export async function replacePartModel(part: string, path?: string | null): Promise<void> {
  const s = useStore.getState();
  const [glb] = await window.forge.openDialog('file', [{ name: t('actions.k24'), extensions: ['glb', 'gltf'] }]);
  if (!glb) return;
  useStore.setState({ busy: t('actions.k25') });
  try {
    const r = await call('mesh.import', { glb });
    const key = lightPoint(part);
    const at = key && lightFrom(r, partTarget(part, path), false);
    s.setPartMesh(part, path, r, (d) => void (key && at && placePoint(d, key, at)));
    s.toast('ok', t('partModel.done'));
    // The lamp body (any piece of a flashlight but its lens) carries the light and the lens with it.
    const isLens = (p: string) => /glass|lens/i.test(`${p} ${pieceModel(part, p)}`);
    if (!key || !path || isLens(path)) return;
    if (!at) showLight(part);
    const lenses = modelPieces(part).filter((p) => p.path !== path && isLens(p.path));
    if (lenses.length)
      s.toast('info', t('partModel.hideLens'), {
        label: t('partModel.hideLensAction'),
        run: () => useStore.getState().edit((d) => lenses.forEach((p) => (entry(d, part, p.path).visible = false))),
      });
  } catch (e) {
    s.fail(e);
  } finally {
    useStore.setState({ busy: null });
  }
}

/** 替换整个部件: the part's own models hidden (its root `visible: false`) and an imported model hung on its root the way the
 * whole-gun model hangs (glTF front down the part's +Z), placed then like any attachment. One undo step. */
export async function replaceWholePart(part: string): Promise<void> {
  const root = partTarget(part, null);
  if (!root) return;
  useStore.getState().select({ kind: 'part', id: part });
  const key = lightPoint(part);
  let placed = null as boolean | null;
  await importMesh(false, undefined, (a, d, r) => {
    a.position = [0, 0, 0];
    a.rotation = [0, 180, 0];
    a.scale = [1, 1, 1];
    entry(d, part, null).visible = false;
    const at = key && lightFrom(r, root, true);
    placed = !!(key && at && placePoint(d, key, at));
  });
  if (key && placed === false) showLight(part);
}

/** One node's model: what it shows now and the buttons, or why it cannot be replaced. */
function ModelRow({ part, path, state, label }: { part: string; path: string | null; state: ModelState; label?: string }) {
  const mesh = useStore((s) => partTransformOf(s.doc, part, path)?.mesh);
  const off = !mesh && state !== 'ok';
  return (
    <Group gap={6} wrap="nowrap">
      <div style={{ flex: 1, minWidth: 0 }} title={path ?? undefined}>
        {label && (
          <Text size="xs" fw={700} truncate>
            {label}
          </Text>
        )}
        <Text size="xs" c={mesh ? undefined : 'dimmed'}>
          {mesh ? t('partModel.replaced', { v0: baseName(mesh) }) : off ? whyNot(state) : t('partModel.original')}
        </Text>
      </div>
      <Button size="xs" variant="light" disabled={off} leftSection={<IconReplace size={14} />} onClick={() => void replacePartModel(part, path)}>
        {t('partModel.replace')}
      </Button>
      {mesh && (
        <Button size="xs" variant="subtle" color="gray" leftSection={<IconArrowBackUp size={14} />} onClick={() => useStore.getState().setPartMesh(part, path, null)}>
          {t('partModel.restore')}
        </Button>
      )}
    </Group>
  );
}

export function PartModel({ part }: { part: string }) {
  const path = useStore((s) => (s.selection?.kind === 'part' ? (s.selection.sub ?? null) : null));
  useStore((s) => s.preview); // the states read the preview's nodes
  useSceneVersion();
  const state = modelState(part, path);
  const pieces = state === 'empty' ? modelPieces(part, path) : [];
  return (
    <Section title={t('partModel.title')} icon={IconBox} color="var(--scan)" info={t('partModel.info')}>
      <Stack gap={6}>
        {(state !== 'empty' || !pieces.length) && <ModelRow part={part} path={path} state={state} />}
        {pieces.map((p) => (
          <ModelRow key={p.path} part={part} path={p.path} state={p.state} label={pieceLabel(part, p.path)} />
        ))}
        {!path && (
          <Button size="xs" variant="light" leftSection={<IconReplace size={14} />} onClick={() => void replaceWholePart(part)}>
            {t('partModel.wholePart')}
          </Button>
        )}
      </Stack>
    </Section>
  );
}

/** Under a moved or replaced piece of a flashlight: the light stays where it was unless moved too. */
export function LightStays({ part, path }: { part: string; path: string | null }) {
  const key = lightPoint(part);
  const stays = useStore((s) => {
    if (!key || !path || path === key.path || key.path.startsWith(`${path}/`)) return false;
    const e = partTransformOf(s.doc, part, path);
    const l = partTransformOf(s.doc, key.part, key.path);
    return !!e && (e.position != null || e.rotation != null || e.scale != null || !!e.mesh) && l?.position == null;
  });
  if (!stays || !key) return null;
  return (
    <Group gap={6} wrap="nowrap" mt={8}>
      <Text size="xs" c="yellow" style={{ flex: 1 }}>
        {t('partModel.lightStays')}
      </Text>
      <Button size="xs" variant="light" leftSection={<IconBulb size={14} />} onClick={() => useStore.getState().select({ kind: 'part', id: key.part, sub: key.path })}>
        {t('partModel.pickLight')}
      </Button>
    </Group>
  );
}
