import { buildTerrainMesh } from '../explorer/terrainMesh';
import type { Mat4 } from '../explorer/mat4';
import type { MeshGeometry, MeshInput } from './types';

/** Surface only: render skirts must never become navigation walls. */
export function terrainCollision(outer: Float32Array, inner: Float32Array, n: number, size: number, holes: Uint8Array) {
	const mesh = buildTerrainMesh(outer, inner, n, size, holes, 0);
	const count = (n + 1) ** 2 + n ** 2;
	return { positions: mesh.positions.slice(0, count * 3), indices: mesh.indices.filter((_, i, all) => {
		const start = i - i % 3;
		return all[start] < count && all[start + 1] < count && all[start + 2] < count;
	}) };
}

export function transformPoint(m: Mat4, x: number, y: number, z: number): [number, number, number] {
	return [m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14]];
}

/** Physical WMO faces: explicit collision, or rendered non-detail geometry.
 * MOPY 0x04 means detail, not "no collision"; 0x08 overrides detail.
 * See TrinityCore src/tools/vmap4_extractor/wmo.{h,cpp}.
 */
export function wmoCollisionIndices(indices: ArrayLike<number>, flags?: Uint8Array): number[] {
    return Array.from(indices).filter((_, i) => {
        const flag = flags?.[Math.floor(i / 3)];
        return flag === undefined || !!(flag & 0x08) || !!(flag & 0x20) && !(flag & 0x04);
    });
}

export function previewGeometry(input: MeshInput, positions: ArrayLike<number> = input.positions, indices: ArrayLike<number> = input.indices, navmesh = false): MeshGeometry {
	const soup = new Array<number>(indices.length * 3);
	for (let i = 0; i < indices.length; i++) {
		const v = indices[i] * 3;
		soup[i * 3] = positions[v + 2] + input.origin[2];
		soup[i * 3 + 1] = positions[v] + input.origin[0];
		soup[i * 3 + 2] = positions[v + 1] + input.origin[1];
	}
	return { mapId: input.tile.mapId, positions: soup, classes: navmesh ? new Array(indices.length / 3).fill(11) : Array.from(input.classes), triangles: indices.length / 3, source: navmesh ? 'navmesh' : 'collision' };
}
