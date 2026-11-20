// A gun's original pieces (the transforms under a part, PartTransform.Path): a readable name guessed from the game's node
// name (Front_Flash -> 枪口焰, a_ChargingHandle -> 拉机柄), the original name kept for hovering; and whether the author
// changed one (hidden, moved, or given another model), which is what the parts tree lists.
import type { SkinDocument } from './protocol';
import { currentItem, partTransformOf, useStore } from './store';
import { partLabel, t } from './i18n';

// Words looked for in a node name, longer ones first so a compound wins over its parts; each has a `piece.<word>` text.
const WORDS = [
  'charginghandle',
  'lightglass',
  'flashlight',
  'flashhider',
  'speedloader',
  'magazine',
  'gunlight',
  'cylinder',
  'trigger',
  'hammer',
  'muzzle',
  'barrel',
  'safety',
  'battery',
  'antenna',
  'nozzle',
  'pommel',
  'scope',
  'sight',
  'flash',
  'slide',
  'guard',
  'stock',
  'laser',
  'light',
  'screen',
  'handle',
  'shell',
  'bullet',
  'spring',
  'button',
  'cover',
  'blade',
  'lens',
  'bolt',
  'grip',
  'rail',
  'tank',
  'mag',
  'cap',
] as const;

/** The known words in a name, as people read them, or null when it has none. */
function wordsOf(name: string): string | null {
  const plain = name.toLowerCase().replace(/[^a-z]/g, '');
  const found: { at: number; end: number; word: string }[] = [];
  for (const word of WORDS) {
    for (let at = plain.indexOf(word); at >= 0; at = plain.indexOf(word, at + 1)) {
      if (!found.some((f) => at < f.end && at + word.length > f.at)) found.push({ at, end: at + word.length, word });
    }
  }
  if (!found.length) return null;
  const words = [...new Set(found.sort((a, b) => a.at - b.at).map((f) => t(`piece.${f.word}` as 'piece.mag')))].slice(0, 2);
  const number = /(\d+)$/.exec(name)?.[1];
  return words.join(t('piece.join')) + (number ? ` ${number}` : '');
}

/** The node `part`'s light shines from (a flashlight's), if it has one: a point like the hold points, moved by its layout entry. */
export function lightPoint(part: string): { part: string; path: string } | null {
  return useStore.getState().preview?.points?.find((p) => p.point === 'light' && p.part === part) ?? null;
}

/** The piece's name for people: 出光点 for the node a flashlight shines from, else the words found in the node's own name, else
 * in its model's name (a lamp body the game named " 1"), else the part's name with the piece's place among its pieces (the
 * game's names, such as Receiver_SMG_2, mean nothing to people; they stay in the row's hover text). */
export function pieceLabel(part: string, path: string): string {
  const transforms = useStore.getState().preview?.parts.find((p) => p.id === part)?.transforms ?? [];
  if (lightPoint(part)?.path === path) return t('piece.lightPoint');
  const name = path.split('/').pop() ?? path;
  const model = transforms.find((x) => x.path === path)?.model;
  const known = wordsOf(name) ?? (model ? wordsOf(model) : null);
  if (known) return known;
  const p = currentItem()?.parts.find((x) => x.id === part);
  const models = transforms.filter((x) => x.path && x.model);
  const place = models.length > 1 ? models.findIndex((x) => x.path === path) + 1 : 0;
  return p ? `${partLabel(p)} ${place || ''}`.trim() : name;
}

/** Whether the author changed this piece: hidden it, moved, turned or scaled it, or gave it another model. */
export function pieceChanged(doc: SkinDocument, part: string, path: string): boolean {
  const x = partTransformOf(doc, part, path);
  return !!x && (x.visible === false || x.position != null || x.rotation != null || x.scale != null || !!x.mesh);
}
