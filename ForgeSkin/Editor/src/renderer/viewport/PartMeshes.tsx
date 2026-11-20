// Replaced part models in the 3D view: the node's own meshes stop drawing and the imported model is drawn in its place,
// with the node's (painted) material, as the plugin does in game (mesh swap, same material, children untouched).
import { useGLTF } from '@react-three/drei';
import { createPortal } from '@react-three/fiber';
import { useEffect, useMemo } from 'react';
import type { BufferGeometry, Material, Mesh, Object3D } from 'three';
import { fileUrl } from '../protocol';
import { partMeshKey, useStore } from '../store';
import { ownMeshes, partTarget, useSceneVersion } from './refs';

function Replacement({ glb, node }: { glb: string; node: Object3D }) {
  const gltf = useGLTF(fileUrl(glb));
  // Marked for the sight lens on this node (SightLens.tsx), which draws on the replacement.
  const scene = useMemo(() => Object.assign(gltf.scene.clone(true), { userData: { sfReplacement: true } }), [gltf.scene]);
  useEffect(() => {
    const own = ownMeshes(node);
    // The node's whole material set, in slot order (glTF split a multi-material model into one mesh per slot). The imported
    // model is one submesh, which Unity draws once with every material of the renderer, one over the other: so does the view.
    const materials = own.flatMap((m) => (Array.isArray(m.material) ? m.material : [m.material]));
    // Per mesh, so a material other meshes share keeps drawing them.
    const hidden = own.map((m) => {
      const original = m.material;
      const off = (Array.isArray(original) ? original[0] : original).clone();
      off.visible = false;
      m.material = off;
      return [m, original] as const;
    });
    const layered: BufferGeometry[] = [];
    scene.traverse((o) => {
      const mesh = o as Mesh;
      if (!mesh.isMesh || !materials.length) return;
      if (materials.length === 1) return void (mesh.material = materials[0]);
      // The loader's geometry is shared with other copies of this model: the groups go on a copy of it.
      const g = mesh.geometry.clone();
      const count = g.index?.count ?? g.getAttribute('position').count;
      materials.forEach((_, i) => g.addGroup(0, count, i));
      layered.push((mesh.geometry = g));
      mesh.material = materials;
    });
    return () => {
      hidden.forEach(([m, original]) => ((m.material as Material).dispose(), (m.material = original)));
      layered.forEach((g) => g.dispose());
    };
  }, [scene, node]);
  return createPortal(<primitive object={scene} />, node);
}

export function PartMeshes() {
  const parts = useStore((s) => s.doc.parts);
  const previews = useStore((s) => (s.doc.editor as Record<string, Record<string, string> | undefined> | undefined)?.partMeshPreviews);
  useSceneVersion(); // nodes exist only after the weapon model mapped them
  return (
    <>
      {(parts ?? []).map((p) => {
        if (!p?.mesh || !p.part) return null;
        const glb = previews?.[partMeshKey(p.part, p.path)];
        const node = partTarget(p.part, p.path);
        return glb && node ? <Replacement key={partMeshKey(p.part, p.path)} glb={glb} node={node} /> : null;
      })}
    </>
  );
}
