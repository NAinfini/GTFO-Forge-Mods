/** Builds the hot-reload tier D test packages through the website's own exporters, from the fixed inputs in reload-packs.json:
 *
 *    pnpm ts Mods/ForgeDevelopment/tests/ReloadPacks/build-reload-packs.ts [-o <dir>]
 *
 * Three packages, three versions. `ReloadD-Rundown` is a one-level Rundown whose Enemy rows are the Map Rundown's enemy rows;
 * `ReloadD-Enemies` is an enemy content pack (one entry); `ReloadD-Weapons` is a weapon content pack (one entry). Version 1 is the
 * Rundown alone; version 2 adds an enemy entry and a weapon entry; version 3 withdraws one Rundown row and the enemy entry, replaces
 * another Rundown row in place and replaces the weapon entry in place. Version 2 needs no Rundown of its own: the Rundown stays as
 * version 1 left it. Each zip is installed and admitted offline by the same code the game runs (package admission), and expected.json
 * lists, per version, the rows the scripts judge. Output goes to artifacts (default artifacts/work/reload-packs), never to Git. */
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {preloadAuthoringData} from '../../../../Tools/Forge/build-v1.ts';
import spec from './reload-packs.json' with {type: 'json'};

await preloadAuthoringData();
await import('../../../../Tools/builtin-content-ids.ts');
const {prepareV1Context} = await import('../../../../Tools/Forge/v1-context.ts');
const {v1ExportResources} = await import('../../../../Tools/Forge/v1-imported-model.ts');
const {admit, describeRejections} = await import('../../../../Tools/Forge/package-admission.ts');
const {createRundown, toPersistedRundown, validateRundown} = await import('../../../../Web/src/forge/map/map-rundown.ts');
const {applyExpeditionImport} = await import('../../../../Web/src/forge/map/map-expedition-import.ts');
const {resolveMapRoomDefinitions} = await import('../../../../Web/src/forge/map/map-room-library.ts');
const {decodeRoomArchive, writeRoomArchiveValue} = await import('../../../../Web/src/forge/room/room-library-model.ts');
const {createDraft, prototypeById, setDraftValue, validateDraft} = await import('../../../../Web/src/forge/enemy/enemy-editor-model.ts');
const {authoredEnemyVariant, editedLinkedKinds, ENEMY_LINKED_BLOCKS} = await import('../../../../Web/src/forge/enemy/enemy-native-blocks.ts');
const {spreadDropPoint, useFixedEnemy} = await import('../../../../Web/src/forge/map/map-fixed-placement.ts');
const {exportMapPackage} = await import('../../../../Web/src/features/map/map-package.ts');
const {exportEnemyContentPack} = await import('../../../../Web/src/features/enemy-editor/enemy-content-pack.ts');
const {exportWeaponContentPack} = await import('../../../../Web/src/features/workshop/weapon-content-pack.ts');
const {contentPackZipBytes} = await import('../../../../Web/src/shared/lib/content-pack-zip.ts');
const {createState} = await import('../../../../Web/src/forge/equipment/workshop-data.ts');

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../../../..');
const outFlag = process.argv.indexOf('-o');
const output = path.resolve(outFlag < 0 ? path.join(root, 'artifacts/work/reload-packs') : process.argv[outFlag + 1]!);
const icon = fs.readFileSync(path.join(root, 'Mods/ForgeEngine/icon.png'));

const {context, profile, sources, data, workshop, nativeCatalog, templates} = await prepareV1Context();
const NOW = 1;
const identity = (name: string) => ({team: spec.team, name, entry: 'rundown', version: '1.0.0', displayName: name, description: 'Hot-reload tier D test package'});

/** One Enemy row the way the Enemy editor builds it: a draft of the vanilla enemy `base` with its health set. */
async function enemyDraft(work: {id: string; name: string; health: number}) {
    const row = profile.prototypes.flatMap(prototype => prototype.variants.map(variant => ({prototype, variant}))).find(entry => entry.variant.enemyIdText === String(spec.baseEnemy));
    if (!row) throw Error('Enemy profile has no variant of enemy ' + spec.baseEnemy);
    let draft = createDraft({profile, prototypeId: row.prototype.id, variant: row.variant, id: work.id, name: work.name, now: NOW});
    draft = setDraftValue(draft, 'balancing.Health.HealthMax', work.health, NOW);
    const checked = validateDraft(draft, profile);
    if (!checked.valid) throw Error('Enemy draft ' + work.name + ' is invalid: ' + JSON.stringify(checked.issues));
    const enemyBlock = data.Enemy!.Blocks.find(block => block.persistentID === spec.baseEnemy)!;
    const linkedBlocks = Object.fromEntries(
        editedLinkedKinds(draft).map(kind => {
            const {type, field} = ENEMY_LINKED_BLOCKS[kind];
            return [kind, data[type]!.Blocks.find(block => block.persistentID === enemyBlock[field])!];
        })
    );
    return {draft, prototype: prototypeById(profile, draft.prototypeId)!, enemyBlock, linkedBlocks};
}

async function rundownZip(version: string, rows: readonly {id: string; name: string; health: number}[], ledger?: Record<string, unknown>) {
    const project = await context.builder.build('A1', spec.level.rooms as never);
    for (const row of rows) {
        const {draft, prototype, enemyBlock, linkedBlocks} = await enemyDraft(row);
        // A Rundown row exists only where the level uses it: each is placed as a fixed enemy of the entry room.
        const room = project.rooms[1]!;
        useFixedEnemy(
            project,
            await authoredEnemyVariant({draft, prototype, enemyBlock, linkedBlocks}),
            room.id,
            1,
            spreadDropPoint(
                room,
                room.enemySpawns.map(spawn => spawn.position)
            ),
            0
        );
    }
    // The library as the export CLI reads it back from an author file: stored, parsed and its rooms resolved against the room sources.
    const single = createRundown(project),
        whole = createRundown();
    single.expeditions[0]!.id = 'expedition:reloadd';
    applyExpeditionImport(single, whole, {expeditionId: 'expedition:reloadd', tier: 'A', position: 1});
    const archive = decodeRoomArchive(writeRoomArchiveValue(whole.roomLibrary!), true);
    const rundown = (await resolveMapRoomDefinitions(validateRundown(JSON.parse(JSON.stringify(toPersistedRundown(whole)))), sources, {archive})).value;
    rundown.name = spec.rundown.title;
    rundown.packageSettings = {menuSlot: spec.rundown.menuSlot, identity: {...identity(spec.rundown.name), version}};
    if (ledger) rundown.contentIds = ledger as never;
    const result = await exportMapPackage(rundown, data, workshop, {
        ...identity(spec.rundown.name),
        version,
        icon,
        templates,
        nativeCatalog,
        language: 'zh',
        resources: await v1ExportResources()
    } as never);
    return {archive: result.archive as Uint8Array, contentIds: (result as unknown as {contentIds?: Record<string, unknown>}).contentIds};
}

async function enemyZip(version: string, entry: {id: string; name: string; health: number}) {
    const {draft, enemyBlock, linkedBlocks} = await enemyDraft(entry);
    const pack = await exportEnemyContentPack({
        team: spec.team,
        name: spec.enemies.name,
        entry: entry.id,
        version,
        draft,
        icon,
        language: 'zh',
        nativeInputs: {profile, enemyBlock, linkedBlocks}
    });
    return contentPackZipBytes(pack);
}

async function weaponZip(version: string, entry: {id: string; name: string}) {
    const state = createState(workshop, workshop.templates.find(row => row.kind === 'weapon')!.id);
    state.name = entry.name;
    const pack = await exportWeaponContentPack({team: spec.team, name: spec.weapons.name, entry: entry.id, version, state, catalog: workshop, icon, language: 'zh'});
    return contentPackZipBytes(pack);
}

const files: Record<string, Uint8Array> = {};
const v1 = await rundownZip('1.0.0', spec.rundown.v1);
files['v1/' + spec.team + '-' + spec.rundown.name + '-1.0.0.zip'] = v1.archive;
files['v2/' + spec.team + '-' + spec.enemies.name + '-2.0.0.zip'] = await enemyZip('2.0.0', spec.enemies.v2);
files['v2/' + spec.team + '-' + spec.weapons.name + '-2.0.0.zip'] = await weaponZip('2.0.0', spec.weapons.v2);
const v3 = await rundownZip('3.0.0', spec.rundown.v3, v1.contentIds);
files['v3/' + spec.team + '-' + spec.rundown.name + '-3.0.0.zip'] = v3.archive;
files['v3/' + spec.team + '-' + spec.enemies.name + '-3.0.0.zip'] = await enemyZip('3.0.0', spec.enemies.v3);
files['v3/' + spec.team + '-' + spec.weapons.name + '-3.0.0.zip'] = await weaponZip('3.0.0', spec.weapons.v3);

fs.rmSync(output, {recursive: true, force: true});
let refused = 0;
for (const [name, bytes] of Object.entries(files)) {
    const target = path.join(output, name);
    fs.mkdirSync(path.dirname(target), {recursive: true});
    fs.writeFileSync(target, bytes);
    const row = admit(target);
    if (!row.accepted) refused++;
    console.log((row.accepted ? 'admitted ' : 'REFUSED  ') + name + (row.accepted ? '' : '\n' + describeRejections(row)));
}
fs.writeFileSync(path.join(output, 'expected.json'), JSON.stringify({team: spec.team, spec}, null, 2));
if (refused) throw Error(refused + ' package(s) refused by the mod admission');
