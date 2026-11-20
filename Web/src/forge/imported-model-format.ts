import {isRecord} from './lib/is-record.ts';

// 导入模型的格式本身（合同 §8「玩家上传模型」）：上限、描述符与 mesh 旁文件的读写、PNG 头与拒收码。格式规范归 ForgeVisual
// （`ImportedModelFormat.cs`）：这里写的描述符与 mesh 旁文件必须逐字段、逐字节与它读的一致。本文件只依赖数据，不依赖网站的资源层、
// 文案与页面：网站、服务器与 ForgeSkin 编辑器都直接用它；拒收只给码与参数，文案留给各自的调用方。

/** 格式只有技术上限（`ImportedModelFormat.cs` 的 `Max*`）：mesh 旁文件整份读成一个字节数组、子网格建成一个索引数组（.NET 最大数组长度），
 *  骨骼下标是 16 位，贴图边长是 Unity Direct3D 11 的上限。材质数、贴图数、文件大小没有上限。 */
const formatLimits = {arrayBytes: 0x7fffffc7, textureSide: 16384, bones: 65536};

/** 导入器的界限；转换器、本机库读同一份。`advised` 是网站建议值，超出只给提示（`importedModelAdvice`），不拒绝；
 *  服务器的上传限额是它自己的存储与解码策略（`upload-policy.cts`）。 */
export const importedModelLimits = Object.freeze({
    /** 贴图边长（像素）。 */
    textureSide: formatLimits.textureSide,
    bones: formatLimits.bones,
    /** 变换后任一坐标的绝对值（米）。 */
    extent: 1000,
    nameLength: 80,
    advised: Object.freeze({faces: 100_000, materials: 8, textures: 8, textureSide: 2048})
});

/** 一个模型超出建议值的项（提示码，对应 `community.warning.<码>`）；没超出硬上限，所以不拒绝。 */
export function importedModelAdvice(descriptor: ImportedModelDescriptor): string[] {
    const advised = importedModelLimits.advised,
        out: string[] = [];
    if (importedDrawnIndices(descriptor) / 3 > advised.faces) out.push('model-faces');
    if (descriptor.materials.length > advised.materials) out.push('model-materials');
    if (descriptor.textures.length > advised.textures) out.push('model-textures');
    if (descriptor.textures.some(texture => Math.max(texture.width, texture.height) > advised.textureSide)) out.push('model-texture-size');
    return out;
}

/** 子网格的索引数之和（朝向镜头的子件也算）：游戏按它建网格，子网格可以重叠，所以它可以大于 `mesh.indices`；面数与内存都按它算。 */
export const importedDrawnIndices = (descriptor: ImportedModelDescriptor) =>
    [descriptor.submeshes, ...(descriptor.facing ?? []).map(part => part.submeshes)].reduce(
        (total, list) => total + list.reduce((sum, submesh) => sum + submesh.count, 0),
        0
    );
/** 不转的那部分的顶点数：朝向镜头的子件的顶点接在它后面。 */
export const importedStillVertices = (descriptor: ImportedModelDescriptor) => descriptor.facing?.[0]?.vertexStart ?? descriptor.mesh.vertices;
export const importedMeshHeaderBytes = 16;
const meshMagic = 0x48534d46;
/** 每顶点：位置 3、法线 3、UV 2 个 float。 */
export const importedMeshVertexBytes = 32;
/** 带蒙皮（格式 2）时索引之后每顶点：4 个 uint16 骨骼下标、4 个 float 权重。 */
export const importedMeshSkinBytes = 24;
export const importedMeshExtension = 'fmesh';

/**
 * `texture` is the base-colour map (sRGB), `normalTexture` the tangent-space normal map (linear, OpenGL +Y, scale 1) and `emissiveTexture` the
 * emission map (sRGB); each is an index into the descriptor's textures or -1. The surface glows by `emissiveColor` (linear, glTF's emission factor times its strength: 1 is the colour at full brightness, above 1 brighter) times its
 * emission map; an all-zero colour is no emission and then has no emission map. A double-sided surface has no field: the converter writes its back faces into the mesh.
 */
export type ImportedModelMaterial = {
    name: string;
    baseColor: [number, number, number, number];
    texture: number;
    metallic: number;
    roughness: number;
    alpha: 'opaque' | 'mask';
    cutoff: number;
    normalTexture: number;
    metalTexture: number;
    emissiveColor: [number, number, number];
    emissiveTexture: number;
};
/** 一根蒙皮骨骼：`parent` 是骨骼表里更靠前的下标（根为 -1），`rest` 是它在模型自身坐标（游戏轴）里的静止姿态，列主序 4×4。 */
export type ImportedModelBone = {name: string; parent: number; rest: number[]};
/** 一个碰撞形状（源文件里一个 `COL_` 节点）的顶点数与索引数；几何在 mesh 旁文件末尾，按描述符顺序。 */
export type ImportedCollisionShape = {vertices: number; indices: number};
type ImportedSubmesh = {start: number; count: number; material: number};
/** A part that turns about the world's vertical axis to face the camera (a `FACE_` node of the source): its vertices are the run
 *  `vertexStart`..+`vertexCount` of the mesh (after the still part's, parts in order), stored in its own frame: origin at `pivot`
 *  (the node's origin in the model's frame) and its front (the node's +Z) along +Z. `yaw` (radians, Unity's turn about +Y) is the turn
 *  that stands it as authored; its `submeshes` index only its own vertices. */
export type ImportedFacingPart = {vertexStart: number; vertexCount: number; pivot: [number, number, number]; yaw: number; submeshes: ImportedSubmesh[]};
/** 格式 1 是静态模型；格式 2 多一份蒙皮骨架 `skin`，mesh 旁文件也是格式 2（多每顶点的四骨权重）。`collision` 只在静态模型上、
 *  且只在源文件带 `COL_` 节点时出现：游戏按它建碰撞，不画。 */
export type ImportedModelDescriptor = {
    format: 1 | 2;
    mesh: {sha256: string; vertices: number; indices: number};
    submeshes: {start: number; count: number; material: number}[];
    materials: ImportedModelMaterial[];
    textures: {sha256: string; width: number; height: number}[];
    skin?: {bones: ImportedModelBone[]};
    collision?: ImportedCollisionShape[];
    facing?: ImportedFacingPart[];
};

const hashPattern = /^[0-9a-f]{64}$/;

export class ImportedModelError extends Error {
    readonly code: string;
    readonly params: Record<string, string | number>;
    constructor(code: string, params: Record<string, string | number> = {}) {
        super(
            code +
                (Object.keys(params).length
                    ? ': ' +
                      Object.entries(params)
                          .map(([key, value]) => key + '=' + String(value))
                          .join(', ')
                    : '')
        );
        this.name = 'ImportedModelError';
        this.code = code;
        this.params = params;
    }
}
export function requireImported(ok: unknown, code: string, params: Record<string, string | number> = {}): asserts ok {
    if (!ok) throw new ImportedModelError(code, params);
}

const inUnit = (value: unknown): value is number => typeof value === 'number' && value >= 0 && value <= 1;
const nonNegative = (value: unknown): value is number => typeof value === 'number' && value >= 0 && Number.isFinite(Math.fround(value)); // the game reads it as a 32-bit float
function fields(value: unknown, names: readonly string[], code: string): asserts value is Record<string, unknown> {
    requireImported(isRecord(value) && Object.keys(value).length === names.length && names.every(name => Object.hasOwn(value, name)), code);
}

/** 描述符的结构读法，规则与 ForgeVisual `ImportedModelDescriptor.Parse` 一致；多余字段、缺字段、越界都拒绝。 */
export function readImportedModelDescriptor(value: unknown): ImportedModelDescriptor {
    requireImported(isRecord(value) && (value.format === 1 || value.format === 2), 'descriptor-shape');
    fields(
        value,
        [
            'format',
            'mesh',
            'submeshes',
            'materials',
            'textures',
            ...(value.format === 2 ? ['skin'] : []),
            ...['collision', 'facing'].filter(name => Object.hasOwn(value, name))
        ],
        'descriptor-shape'
    );
    const mesh = value.mesh;
    fields(mesh, ['sha256', 'vertices', 'indices'], 'descriptor-shape');
    requireImported(typeof mesh.sha256 === 'string' && hashPattern.test(mesh.sha256), 'descriptor-hash');
    const vertices = mesh.vertices,
        indices = mesh.indices;
    requireImported(Number.isSafeInteger(vertices) && (vertices as number) >= 3, 'descriptor-mesh');
    requireImported(Number.isSafeInteger(indices) && (indices as number) >= 3 && (indices as number) % 3 === 0, 'descriptor-mesh');
    requireImported(importedMeshFits(vertices as number, indices as number, value.format === 2), 'descriptor-mesh');
    requireImported(Array.isArray(value.textures), 'descriptor-texture');
    const textures = value.textures.map(texture => {
        fields(texture, ['sha256', 'width', 'height'], 'descriptor-texture');
        requireImported(typeof texture.sha256 === 'string' && hashPattern.test(texture.sha256), 'descriptor-hash');
        requireImported(
            Number.isSafeInteger(texture.width) &&
                Number.isSafeInteger(texture.height) &&
                (texture.width as number) >= 1 &&
                (texture.height as number) >= 1 &&
                (texture.width as number) <= formatLimits.textureSide &&
                (texture.height as number) <= formatLimits.textureSide,
            'descriptor-texture'
        );
        return {sha256: texture.sha256 as string, width: texture.width as number, height: texture.height as number};
    });
    requireImported(Array.isArray(value.materials) && value.materials.length >= 1, 'descriptor-material');
    const materials = value.materials.map(material => {
        fields(
            material,
            ['name', 'baseColor', 'texture', 'metallic', 'roughness', 'alpha', 'cutoff', 'normalTexture', 'metalTexture', 'emissiveColor', 'emissiveTexture'],
            'descriptor-material'
        );
        const color = material.baseColor,
            texture = material.texture,
            normal = material.normalTexture,
            metal = material.metalTexture,
            emissive = material.emissiveColor,
            emissiveTexture = material.emissiveTexture;
        requireImported(
            typeof material.name === 'string' && material.name.length > 0 && material.name.length <= 200 && !/[\u0000-\u001f\u007f]/.test(material.name),
            'descriptor-material'
        );
        requireImported(Array.isArray(color) && color.length === 4 && color.every(inUnit), 'descriptor-material');
        requireImported(Number.isSafeInteger(texture) && (texture as number) >= -1 && (texture as number) < textures.length, 'descriptor-material');
        requireImported((texture as number) < 0 || color.every(channel => channel === 1), 'descriptor-material');
        requireImported(inUnit(material.metallic) && inUnit(material.roughness) && inUnit(material.cutoff), 'descriptor-material');
        requireImported(material.alpha === 'opaque' || material.alpha === 'mask', 'descriptor-material');
        requireImported(material.alpha === 'opaque' || (texture as number) >= 0, 'descriptor-material');
        requireImported(Number.isSafeInteger(normal) && (normal as number) >= -1 && (normal as number) < textures.length, 'descriptor-material');
        requireImported(Number.isSafeInteger(metal) && (metal as number) >= -1 && (metal as number) < textures.length, 'descriptor-material');
        requireImported(
            Number.isSafeInteger(emissiveTexture) && (emissiveTexture as number) >= -1 && (emissiveTexture as number) < textures.length,
            'descriptor-material'
        );
        requireImported(Array.isArray(emissive) && emissive.length === 3 && emissive.every(nonNegative), 'descriptor-material');
        requireImported((emissiveTexture as number) < 0 || emissive.some(channel => channel > 0), 'descriptor-material');
        return {
            name: material.name,
            baseColor: [color[0], color[1], color[2], color[3]] as [number, number, number, number],
            texture: texture as number,
            metallic: material.metallic,
            roughness: material.roughness,
            alpha: material.alpha as ImportedModelMaterial['alpha'],
            cutoff: material.cutoff,
            normalTexture: normal as number,
            metalTexture: metal as number,
            emissiveColor: [emissive[0], emissive[1], emissive[2]] as [number, number, number],
            emissiveTexture: emissiveTexture as number
        };
    });
    let drawn = 0;
    const readSubmeshes = (list: unknown, code: string): ImportedSubmesh[] => {
        requireImported(Array.isArray(list) && list.length >= 1, code);
        return list.map(submesh => {
            fields(submesh, ['start', 'count', 'material'], code);
            const {start, count, material} = submesh as ImportedSubmesh;
            requireImported(
                Number.isSafeInteger(start) &&
                    Number.isSafeInteger(count) &&
                    Number.isSafeInteger(material) &&
                    start >= 0 &&
                    count >= 3 &&
                    start % 3 === 0 &&
                    count % 3 === 0 &&
                    start + count <= (indices as number) &&
                    material >= 0 &&
                    material < materials.length,
                code
            );
            // 同一段索引列两次算两次：游戏把所有子网格建成一个索引数组（与 ForgeVisual `DrawnIndices` 一致）。
            drawn += count;
            requireImported(drawn <= formatLimits.arrayBytes, code);
            return {start, count, material};
        });
    };
    const submeshes = readSubmeshes(value.submeshes, 'descriptor-submesh');
    const out: ImportedModelDescriptor = {
        format: value.format as 1 | 2,
        mesh: {sha256: mesh.sha256 as string, vertices: vertices as number, indices: indices as number},
        submeshes,
        materials,
        textures
    };
    if (value.format === 2) out.skin = {bones: readBones(value.skin)};
    if (Object.hasOwn(value, 'collision')) {
        // 碰撞只给静态模型：蒙皮模型跟着骨头动，静态碰撞体跟不上。
        requireImported(value.format === 1 && Array.isArray(value.collision) && value.collision.length >= 1, 'descriptor-collision');
        out.collision = value.collision.map(shape => {
            fields(shape, ['vertices', 'indices'], 'descriptor-collision');
            const {vertices: count, indices: total} = shape as ImportedCollisionShape;
            requireImported(Number.isSafeInteger(count) && count >= 3 && Number.isSafeInteger(total) && total >= 3 && total % 3 === 0, 'descriptor-collision');
            return {vertices: count, indices: total};
        });
        requireImported(importedMeshFits(vertices as number, indices as number, false, out.collision), 'descriptor-collision');
    }
    if (Object.hasOwn(value, 'facing')) {
        // 朝向镜头只给静态模型；子件的顶点按顺序接在不转的部分之后，一直到最后一个顶点。
        requireImported(value.format === 1 && Array.isArray(value.facing) && value.facing.length >= 1, 'descriptor-facing');
        let next = -1;
        out.facing = value.facing.map(part => {
            fields(part, ['vertexStart', 'vertexCount', 'pivot', 'yaw', 'submeshes'], 'descriptor-facing');
            const {vertexStart, vertexCount, pivot, yaw} = part as ImportedFacingPart;
            requireImported(
                Number.isSafeInteger(vertexStart) &&
                    Number.isSafeInteger(vertexCount) &&
                    vertexStart >= 3 &&
                    vertexCount >= 3 &&
                    (next < 0 || vertexStart === next) &&
                    Array.isArray(pivot) &&
                    pivot.length === 3 &&
                    pivot.every(entry => typeof entry === 'number' && Number.isFinite(Math.fround(entry))) &&
                    typeof yaw === 'number' &&
                    Number.isFinite(Math.fround(yaw)),
                'descriptor-facing'
            );
            next = vertexStart + vertexCount;
            return {
                vertexStart,
                vertexCount,
                pivot: [pivot[0], pivot[1], pivot[2]],
                yaw,
                submeshes: readSubmeshes((part as {submeshes: unknown}).submeshes, 'descriptor-facing')
            };
        });
        requireImported(next === vertices, 'descriptor-facing');
    }
    return out;
}

/** 蒙皮骨架：1 到 65536 根（骨骼下标 16 位），名字唯一，父骨在前，静止姿态 16 个有限数。规则与 ForgeVisual 一致。 */
function readBones(value: unknown): ImportedModelBone[] {
    fields(value, ['bones'], 'descriptor-skin');
    const rows = value.bones;
    requireImported(Array.isArray(rows) && rows.length >= 1 && rows.length <= formatLimits.bones, 'descriptor-skin');
    const names = new Set<string>();
    return rows.map((bone, index) => {
        fields(bone, ['name', 'parent', 'rest'], 'descriptor-skin');
        const {name, parent, rest} = bone;
        requireImported(
            typeof name === 'string' && name.length > 0 && name.length <= 200 && !/[\u0000-\u001f\u007f]/.test(name) && !names.has(name),
            'descriptor-skin'
        );
        names.add(name);
        requireImported(Number.isSafeInteger(parent) && (parent as number) >= -1 && (parent as number) < index, 'descriptor-skin');
        requireImported(
            Array.isArray(rest) && rest.length === 16 && rest.every(entry => typeof entry === 'number' && Number.isFinite(Math.fround(entry))),
            'descriptor-skin'
        );
        return {name, parent: parent as number, rest: [...(rest as number[])]};
    });
}

const pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
export interface PngHeader {
    width: number;
    height: number;
    bitDepth: number;
    colorType: number;
    interlace: number;
}
/** PNG 的 IHDR：只读前 29 字节，不解码；不是 PNG 或 IHDR 不对返回 null。 */
export function readPngHeader(bytes: Uint8Array): PngHeader | null {
    if (bytes.length < 29 || !pngSignature.every((byte, index) => bytes[index] === byte)) return null;
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (view.getUint32(8) !== 13 || String.fromCharCode(bytes[12]!, bytes[13]!, bytes[14]!, bytes[15]!) !== 'IHDR') return null;
    const width = view.getUint32(16),
        height = view.getUint32(20);
    if (width < 1 || height < 1) return null;
    return {width, height, bitDepth: bytes[24]!, colorType: bytes[25]!, interlace: bytes[28]!};
}

/** 服务端准入对带文字、时间与 EXIF 块的 PNG 只告警、不拒绝（它不能改字节：revision 是这些字节的哈希），所以转换时在客户端去掉这些块，
 *  让告警不出现；像素流（IDAT）和其余块原样保留。块长越界时不动，交给准入报错。 */
export function stripPngMetadata(bytes: Uint8Array): Uint8Array {
    const drop = new Set(['eXIf', 'tEXt', 'zTXt', 'iTXt', 'tIME']),
        view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength),
        kept: Uint8Array[] = [bytes.subarray(0, 8)];
    let at = 8,
        dropped = false;
    while (at + 12 <= bytes.length) {
        const end = at + 12 + view.getUint32(at);
        if (end > bytes.length) return bytes;
        if (drop.has(String.fromCharCode(bytes[at + 4]!, bytes[at + 5]!, bytes[at + 6]!, bytes[at + 7]!))) dropped = true;
        else kept.push(bytes.subarray(at, end));
        at = end;
    }
    if (!dropped || at !== bytes.length) return bytes;
    const out = new Uint8Array(kept.reduce((total, part) => total + part.length, 0));
    let cursor = 0;
    for (const part of kept) {
        out.set(part, cursor);
        cursor += part.length;
    }
    return out;
}

/** 每顶点四根骨骼的下标与权重（权重和为 1）。 */
export interface ImportedMeshSkin {
    joints: Uint16Array;
    weights: Float32Array;
}
/** 一个碰撞形状的几何：每顶点 3 个 float 位置，每三角 3 个 uint32 索引（模型自身坐标、Unity 轴与绕向）。 */
export interface ImportedCollisionMesh {
    positions: Float32Array;
    indices: Uint32Array;
}
const collisionBytes = (shapes: readonly ImportedCollisionShape[] = []) => shapes.reduce((total, shape) => total + shape.vertices * 12 + shape.indices * 4, 0);
const meshBytes = (vertices: number, indices: number, skinned: boolean, collision?: readonly ImportedCollisionShape[]) =>
    importedMeshHeaderBytes + vertices * importedMeshVertexBytes + indices * 4 + (skinned ? vertices * importedMeshSkinBytes : 0) + collisionBytes(collision);
/** 这些顶点与索引（和碰撞形状）的 mesh 旁文件能否被游戏整份读成一个字节数组。 */
export const importedMeshFits = (vertices: number, indices: number, skinned: boolean, collision?: readonly ImportedCollisionShape[]) =>
    meshBytes(vertices, indices, skinned, collision) <= formatLimits.arrayBytes;
/** mesh 旁文件：`FMSH`、格式（1 静态、2 蒙皮）、顶点数、索引数（各 uint32 小端），然后每顶点 8 个 float，然后 uint32 索引；
 *  格式 2 再接每顶点 4 个 uint16 骨骼下标与 4 个 float 权重。描述符列了 `collision` 时，末尾按顺序接每个碰撞形状的位置（每顶点 3 个
 *  float）与索引（uint32）；没有碰撞的模型字节不变。 */
export function writeImportedMesh(
    positions: Float32Array,
    normals: Float32Array,
    uvs: Float32Array,
    indices: Uint32Array,
    skin?: ImportedMeshSkin,
    collision: readonly ImportedCollisionMesh[] = []
): Uint8Array {
    const vertices = positions.length / 3,
        shapes = collision.map(shape => ({vertices: shape.positions.length / 3, indices: shape.indices.length})),
        bytes = new Uint8Array(meshBytes(vertices, indices.length, !!skin, shapes)),
        view = new DataView(bytes.buffer);
    view.setUint32(0, meshMagic, true);
    view.setUint32(4, skin ? 2 : 1, true);
    view.setUint32(8, vertices, true);
    view.setUint32(12, indices.length, true);
    let at = importedMeshHeaderBytes;
    for (let vertex = 0; vertex < vertices; vertex++) {
        for (let k = 0; k < 3; k++, at += 4) view.setFloat32(at, positions[vertex * 3 + k]!, true);
        for (let k = 0; k < 3; k++, at += 4) view.setFloat32(at, normals[vertex * 3 + k]!, true);
        for (let k = 0; k < 2; k++, at += 4) view.setFloat32(at, uvs[vertex * 2 + k]!, true);
    }
    for (let index = 0; index < indices.length; index++, at += 4) view.setUint32(at, indices[index]!, true);
    if (skin)
        for (let vertex = 0; vertex < vertices; vertex++) {
            for (let k = 0; k < 4; k++, at += 2) view.setUint16(at, skin.joints[vertex * 4 + k]!, true);
            for (let k = 0; k < 4; k++, at += 4) view.setFloat32(at, skin.weights[vertex * 4 + k]!, true);
        }
    for (const shape of collision) {
        for (const value of shape.positions) (view.setFloat32(at, value, true), (at += 4));
        for (const value of shape.indices) (view.setUint32(at, value, true), (at += 4));
    }
    return bytes;
}

export interface ImportedMeshData {
    positions: Float32Array;
    normals: Float32Array;
    uvs: Float32Array;
    indices: Uint32Array;
    skin?: ImportedMeshSkin;
    /** 描述符 `collision` 的每个形状，同序；没有碰撞时为空。 */
    collision: ImportedCollisionMesh[];
}
/** 读 mesh 旁文件并做 ForgeVisual 同样的检查：长度、文件头、有限数、可用法线、索引不越过顶点；蒙皮的骨骼下标在骨骼表内、权重非负且和为 1；
 *  碰撞形状的位置是有限数、索引不越过本形状的顶点。 */
export function readImportedMesh(bytes: Uint8Array, descriptor: ImportedModelDescriptor): ImportedMeshData {
    const {vertices, indices} = descriptor.mesh,
        bones = descriptor.skin?.bones.length ?? 0;
    requireImported(bytes.length === meshBytes(vertices, indices, bones > 0, descriptor.collision), 'mesh-size');
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    requireImported(view.getUint32(0, true) === meshMagic && view.getUint32(4, true) === descriptor.format, 'mesh-header');
    requireImported(view.getUint32(8, true) === vertices && view.getUint32(12, true) === indices, 'mesh-header');
    const positions = new Float32Array(vertices * 3),
        normals = new Float32Array(vertices * 3),
        uvs = new Float32Array(vertices * 2),
        triangles = new Uint32Array(indices);
    let at = importedMeshHeaderBytes;
    for (let vertex = 0; vertex < vertices; vertex++) {
        for (let k = 0; k < 3; k++, at += 4) positions[vertex * 3 + k] = view.getFloat32(at, true);
        for (let k = 0; k < 3; k++, at += 4) normals[vertex * 3 + k] = view.getFloat32(at, true);
        for (let k = 0; k < 2; k++, at += 4) uvs[vertex * 2 + k] = view.getFloat32(at, true);
        const [nx, ny, nz] = [normals[vertex * 3]!, normals[vertex * 3 + 1]!, normals[vertex * 3 + 2]!];
        requireImported(
            [...positions.subarray(vertex * 3, vertex * 3 + 3), nx, ny, nz, uvs[vertex * 2]!, uvs[vertex * 2 + 1]!].every(Number.isFinite),
            'mesh-non-finite',
            {vertex}
        );
        requireImported(nx * nx + ny * ny + nz * nz >= 0.25, 'mesh-normal', {vertex});
    }
    for (let index = 0; index < indices; index++, at += 4) {
        const value = view.getUint32(at, true);
        requireImported(value < vertices, 'mesh-index', {index});
        triangles[index] = value;
    }
    // Each part draws only its own vertices: the still part those before the first facing part, a facing part its own run.
    const runs: [ImportedSubmesh[], number, number][] = [
        [descriptor.submeshes, 0, importedStillVertices(descriptor)],
        ...(descriptor.facing ?? []).map(part => [part.submeshes, part.vertexStart, part.vertexStart + part.vertexCount] as [ImportedSubmesh[], number, number])
    ];
    for (const [list, low, high] of runs)
        for (const {start, count} of list)
            for (let index = start; index < start + count; index++)
                requireImported(triangles[index]! >= low && triangles[index]! < high, 'mesh-index', {index});
    const collision = () =>
        (descriptor.collision ?? []).map((shape, index) => {
            const points = new Float32Array(shape.vertices * 3),
                list = new Uint32Array(shape.indices);
            for (let k = 0; k < points.length; k++, at += 4) {
                points[k] = view.getFloat32(at, true);
                requireImported(Number.isFinite(points[k]), 'mesh-collision', {shape: index});
            }
            for (let k = 0; k < list.length; k++, at += 4) {
                list[k] = view.getUint32(at, true);
                requireImported(list[k]! < shape.vertices, 'mesh-collision', {shape: index});
            }
            return {positions: points, indices: list};
        });
    if (!bones) return {positions, normals, uvs, indices: triangles, collision: collision()};
    const joints = new Uint16Array(vertices * 4),
        weights = new Float32Array(vertices * 4);
    for (let vertex = 0; vertex < vertices; vertex++) {
        for (let k = 0; k < 4; k++, at += 2) {
            joints[vertex * 4 + k] = view.getUint16(at, true);
            requireImported(joints[vertex * 4 + k]! < bones, 'mesh-skin', {vertex});
        }
        let sum = 0;
        for (let k = 0; k < 4; k++, at += 4) {
            const weight = view.getFloat32(at, true);
            requireImported(Number.isFinite(weight) && weight >= 0, 'mesh-skin', {vertex});
            weights[vertex * 4 + k] = weight;
            sum += weight;
        }
        requireImported(Math.abs(sum - 1) <= 0.01, 'mesh-skin', {vertex});
    }
    return {positions, normals, uvs, indices: triangles, skin: {joints, weights}, collision: collision()};
}

/** 包围盒尺寸（米）：单件资源引用的快照带它，模型被删后用来画占位框。朝向镜头的子件按它在模型里的原位计入：顶点存在子件自己的坐标系里
 *  （原点在枢轴，已转过负偏航角），先转回偏航角、再加枢轴。 */
export function importedMeshSize(bytes: Uint8Array, descriptor: ImportedModelDescriptor): [number, number, number] {
    const {positions} = readImportedMesh(bytes, descriptor),
        low = [Infinity, Infinity, Infinity],
        high = [-Infinity, -Infinity, -Infinity],
        include = (x: number, y: number, z: number) => {
            for (const [axis, value] of [x, y, z].entries()) {
                if (value < low[axis]!) low[axis] = value;
                if (value > high[axis]!) high[axis] = value;
            }
        },
        vertices = positions.length / 3,
        owned = new Uint8Array(vertices);
    for (const {vertexStart, vertexCount, pivot, yaw} of descriptor.facing ?? []) {
        const [cos, sin] = [Math.cos(yaw), Math.sin(yaw)];
        for (let vertex = vertexStart; vertex < vertexStart + vertexCount; vertex++) {
            owned[vertex] = 1;
            const [x, y, z] = [positions[vertex * 3]!, positions[vertex * 3 + 1]!, positions[vertex * 3 + 2]!];
            include(pivot[0] + x * cos + z * sin, pivot[1] + y, pivot[2] - x * sin + z * cos);
        }
    }
    for (let vertex = 0; vertex < vertices; vertex++)
        if (!owned[vertex]) include(positions[vertex * 3]!, positions[vertex * 3 + 1]!, positions[vertex * 3 + 2]!);
    return [high[0]! - low[0]!, high[1]! - low[1]!, high[2]! - low[2]!];
}

/** 不转部分的每个三角（每个九个数，网格自身坐标），换回预览的轴与绕向（X 取反、绕向反过来，与 imported-model-scene.ts 同），给 model-hit-mesh.ts
 *  做受击网格。朝向镜头的子件跟着镜头转，不进受击网格。 */
export function importedMeshTriangles(bytes: Uint8Array, descriptor: ImportedModelDescriptor): number[] {
    const {positions, indices} = readImportedMesh(bytes, descriptor),
        out: number[] = [];
    const ranges = descriptor.facing ? descriptor.submeshes.map(({start, count}) => [start, start + count] as const) : [[0, indices.length] as const];
    for (const [from, end] of ranges)
        for (let at = from; at < end; at += 3)
            for (const index of [indices[at]!, indices[at + 2]!, indices[at + 1]!])
                out.push(-positions[3 * index]!, positions[3 * index + 1]!, positions[3 * index + 2]!);
    return out;
}
