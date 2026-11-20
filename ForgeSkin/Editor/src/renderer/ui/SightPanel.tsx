// 整把枪 · 瞄准镜 (sight.ts): the lens, its reticle, the zoom and the aim point, as the game draws a sight.
import { Button, Group, Select, Text, Tooltip } from '@mantine/core';
import { IconAdjustments, IconCrosshair, IconEye, IconFocus2, IconHandClick, IconSquarePlus, IconTarget, IconTrash, IconZoomIn } from '@tabler/icons-react';
import { addPane, aimAtLens, aimPoint, editSight, LENS, lensStyles, sightView, useSights, useSightView, zoomSteps } from '../sight';
import { useStore } from '../store';
import { t } from '../i18n';
import { Empty, Section } from './Features';
import { ReticleSection } from './SkinInspector';

export function SightPanel() {
  const sight = useStore((s) => s.doc.sight ?? null);
  const attachments = useStore((s) => s.doc.attachments);
  const loaded = useStore((s) => !!s.preview?.params[LENS]);
  const point = useStore((s) => aimPoint(s.preview));
  const list = useSights();
  const view = useSightView();
  const styles = list ? lensStyles(list) : [];
  const steps = list ? zoomSteps(list) : [];
  const lens = sight?.lens ?? null;
  const where = !lens
    ? t('sight.lensNone')
    : lens.attachment
      ? t('sight.lensAttachment', { v0: attachments?.find((a) => a?.id === lens.attachment)?.name || lens.attachment })
      : lens.size
        ? t('sight.lensPane', { v0: Math.round(lens.size[0] * 1000), v1: Math.round(lens.size[1] * 1000) })
        : t('sight.lensNode', { v0: lens.path ? `${lens.part}/${lens.path}` : lens.part });
  const step = (s: (typeof steps)[number]) => ({ value: String(s.id), label: t('sight.zoomStep', { v0: s.name, v1: s.world, v2: s.gear }) });
  return (
    <>
      <Section id="sight-lens" title={t('sight.lens')} icon={IconFocus2} color="var(--scan)" info={t('sight.lensInfo')}>
        <Text size="xs" c={lens ? undefined : 'dimmed'} mb={8}>
          {where}
        </Text>
        <Group gap={6} grow mb={8}>
          <Button size="xs" variant={view.picking ? 'filled' : 'light'} leftSection={<IconHandClick size={14} />} onClick={() => sightView.set({ picking: !view.picking })}>
            {view.picking ? t('sight.picking') : t('sight.pick')}
          </Button>
          <Button size="xs" variant="light" leftSection={<IconSquarePlus size={14} />} disabled={!point} onClick={() => addPane(sight?.style ?? styles[0]?.id ?? null)}>
            {t('sight.addPane')}
          </Button>
        </Group>
        {lens && (
          <>
            <Select
              size="xs"
              label={t('sight.style')}
              data={styles.map((s) => ({ value: String(s.id), label: s.name }))}
              value={sight?.style != null ? String(sight.style) : null}
              onChange={(v) => v && editSight((s) => void (s.style = +v))}
              allowDeselect={false}
              mb={8}
            />
            <Group gap={6} grow>
              {lens.size && (
                <Button size="xs" variant="light" leftSection={<IconAdjustments size={14} />} onClick={() => useStore.getState().select({ kind: 'lens', id: 'lens' })}>
                  {t('sight.movePane')}
                </Button>
              )}
              <Button size="xs" variant="subtle" color="gray" leftSection={<IconTrash size={14} />} onClick={() => editSight((s) => void (s.lens = null))}>
                {t('sight.removeLens')}
              </Button>
            </Group>
          </>
        )}
      </Section>
      {lens && loaded ? (
        <ReticleSection material={LENS} part={lens.part ?? 'sight'} />
      ) : (
        <Section id="sight-reticle" title={t('skinInspector.reticle')} icon={IconCrosshair} color="var(--warn)" info={t('sight.reticleInfo')}>
          <Empty>{lens ? t('sight.reticleLoading') : t('sight.reticleNeedsLens')}</Empty>
        </Section>
      )}
      <Section id="sight-zoom" title={t('sight.zoom')} icon={IconZoomIn} color="var(--ok)" info={t('sight.zoomInfo')}>
        <Select
          size="xs"
          aria-label={t('sight.zoom')}
          data={[{ value: '', label: t('sight.zoomOwn') }, ...steps.map(step)]}
          value={sight?.zoom != null ? String(sight.zoom) : ''}
          onChange={(v) => editSight((s) => void (s.zoom = v ? +v : null))}
          allowDeselect={false}
        />
      </Section>
      <Section id="sight-aim" title={t('sight.aim')} icon={IconTarget} color="var(--flare)" info={t('sight.aimInfo')}>
        {point ? (
          <>
            <Button fullWidth size="md" color="orange" variant={view.aiming ? 'outline' : 'filled'} leftSection={<IconEye size={18} />} onClick={() => sightView.set({ aiming: !view.aiming })}>
              {view.aiming ? t('sight.aimPreviewOff') : t('sight.aimPreview')}
            </Button>
            {view.aiming && sight?.zoom == null && (
              <Text size="xs" c="dimmed" mt={6}>
                {t('sight.aimFallback')}
              </Text>
            )}
            <Group gap={6} grow mt={8}>
              <Button size="xs" variant="light" onClick={() => useStore.getState().select({ kind: 'part', id: point.part, sub: point.path })}>
                {t('sight.selectAim')}
              </Button>
              <Tooltip label={t('sight.aimNeedsLens')} disabled={!!lens}>
                <Button size="xs" variant="light" data-disabled={!lens || undefined} onClick={() => lens && (aimAtLens() || useStore.getState().toast('error', t('sight.aimFailed')))}>
                  {t('sight.aimAtLens')}
                </Button>
              </Tooltip>
            </Group>
          </>
        ) : (
          <Empty>{t('sight.noAimPoint')}</Empty>
        )}
      </Section>
    </>
  );
}
