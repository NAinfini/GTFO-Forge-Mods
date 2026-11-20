// Everything that can be added to one part, in one menu (the "+" on a part row): textures, paint layers, dynamic layers, screens,
// models and replacement models for the part's pieces or the whole part. New kinds of per-part content get their item here.
import { Menu, Text } from '@mantine/core';
import { IconBox, IconBrush, IconDeviceDesktop, IconLetterT, IconMovie, IconPhoto, IconReplace, IconStack2 } from '@tabler/icons-react';
import type { ReactElement } from 'react';
import { importMesh } from '../actions';
import { attachmentMaterial, type CatalogPart, type LayerKind } from '../protocol';
import { useStore } from '../store';
import { addLayer } from './DynamicInspector';
import { FEATURE } from './Features';
import { addLiveryLayer, LIVERY_KINDS } from './LiveryPanel';
import { modelPieces, modelState, replacePartModel, replaceWholePart, whyNot, type ModelState } from './PartModel';
import { pieceLabel } from '../pieces';
import { ShapeMenuItems, SubMenu } from './ShapePanel';
import { t } from '../i18n';
import { addCodeEffect } from '../codeFx';

const LAYERS: { kind: LayerKind; label: () => string; icon: typeof IconPhoto }[] = [
  { kind: 'image', label: () => t('dynamicInspector.k1'), icon: IconPhoto },
  { kind: 'sequence', label: () => t('dynamicInspector.k2'), icon: IconStack2 },
  { kind: 'video', label: () => t('dynamicInspector.k3'), icon: IconMovie },
  { kind: 'text', label: () => t('dynamicInspector.k4'), icon: IconLetterT },
];

/** Paint layers and animated layers for one material (a part's or an attachment's model), as two sub-menus. */
function PaintMenuItems({ material, part, before }: { material: string; part: string; before: () => void }) {
  return (
    <>
      <SubMenu icon={<IconBrush size={14} color={FEATURE.livery.color} />} label={t('livery.title')}>
        {LIVERY_KINDS.map((k) => (
          <Menu.Item key={k.id} leftSection={<k.icon size={14} color={FEATURE.livery.color} />} onClick={() => (before(), addLiveryLayer(material, part, k.id))}>
            {k.label()}
          </Menu.Item>
        ))}
      </SubMenu>
      <SubMenu icon={<IconMovie size={14} color={FEATURE.layer.color} />} label={t('dynamicInspector.k33')}>
        {LAYERS.map((l) => (
          <Menu.Item key={l.kind} leftSection={<l.icon size={14} color={FEATURE.layer.color} />} onClick={() => (before(), void addLayer(material, part, l.kind))}>
            {l.label()}
          </Menu.Item>
        ))}
      </SubMenu>
    </>
  );
}

/** What can be added to an attachment's model: the same paint and animated layers as on a part. */
export function AttachmentAddMenu({ id, children }: { id: string; children: ReactElement }) {
  const material = attachmentMaterial(id);
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>{children}</Menu.Target>
      <Menu.Dropdown onClick={(e) => e.stopPropagation()}>
        <PaintMenuItems material={material} part={material} before={() => useStore.getState().select({ kind: 'attachment', id })} />
      </Menu.Dropdown>
    </Menu>
  );
}

/** A replace entry, off with its reason when the game cannot replace that model. */
function ReplaceItem({ state, label, onClick }: { state: ModelState; label: string; onClick: () => void }) {
  return (
    <Menu.Item leftSection={<IconReplace size={14} />} disabled={state !== 'ok'} onClick={onClick}>
      {label}
      {state !== 'ok' && (
        <Text size="xs" c="dimmed">
          {whyNot(state)}
        </Text>
      )}
    </Menu.Item>
  );
}

/** Replacing the node's own model, or for a node without one the models of the pieces under it (替换模型 › 枪身 / 镜片); on a
 * part root also the whole part. */
function ReplaceItems({ part, path, here }: { part: string; path: string | null; here: () => void }) {
  const state = modelState(part, path);
  const pieces = state === 'empty' ? modelPieces(part, path) : [];
  const { select } = useStore.getState();
  return (
    <>
      {pieces.length ? (
        <SubMenu icon={<IconReplace size={14} />} label={t('partModel.replacePieces')}>
          {pieces.map((p) => (
            <ReplaceItem key={p.path} state={p.state} label={pieceLabel(part, p.path)} onClick={() => (select({ kind: 'part', id: part, sub: p.path }), void replacePartModel(part, p.path))} />
          ))}
        </SubMenu>
      ) : (
        <ReplaceItem state={state} label={t('partModel.replace')} onClick={() => (here(), void replacePartModel(part, path))} />
      )}
      {!path && (
        <Menu.Item leftSection={<IconReplace size={14} />} onClick={() => void replaceWholePart(part)}>
          {t('partModel.wholePart')}
        </Menu.Item>
      )}
    </>
  );
}

export function PartAddMenu({ part, path, children }: { part: CatalogPart; path?: string | null; children: ReactElement }) {
  const { select, addAttachment } = useStore.getState();
  const here = () => select({ kind: 'part', id: part.id, sub: path ?? null });
  const material = part.materials[0];
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>{children}</Menu.Target>
      <Menu.Dropdown onClick={(e) => e.stopPropagation()}>
        <Menu.Item leftSection={<IconPhoto size={14} color={FEATURE.texture.color} />} onClick={here}>
          {t('addMenu.texture')}
        </Menu.Item>
        {material && (
          <>
            <PaintMenuItems material={material} part={part.id} before={here} />
            <Menu.Label>{t('codeFx.title')}</Menu.Label>
            <Menu.Item leftSection={<FEATURE.fx.icon size={14} color={FEATURE.fx.color} />} onClick={() => (here(), addCodeEffect({ material, part: part.id }))}>
              {t('codeFx.addOnPart')}
            </Menu.Item>
          </>
        )}
        <Menu.Label>{t('skinInspector.partAttachments')}</Menu.Label>
        <Menu.Item leftSection={<IconDeviceDesktop size={14} color={FEATURE.attachment.color} />} onClick={() => (here(), addAttachment('screen'))}>
          {t('leftRail.k18')}
        </Menu.Item>
        <Menu.Item leftSection={<IconBox size={14} color={FEATURE.attachment.color} />} onClick={() => (here(), void importMesh())}>
          {t('leftRail.k19')}
        </Menu.Item>
        <ShapeMenuItems color={FEATURE.attachment.color} before={here} />
        <Menu.Divider />
        <ReplaceItems part={part.id} path={path ?? null} here={here} />
      </Menu.Dropdown>
    </Menu>
  );
}
