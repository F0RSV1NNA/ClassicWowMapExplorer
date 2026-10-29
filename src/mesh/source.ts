import type { MapExplorer } from '../explorer/maps';
import { KNOWN_MAPS } from '../explorer/maps';
import { DB2_FILES, loadTable } from '../explorer/clientDb';
import { compose, fromQuaternion, multiply, scaling, translation, type Mat4 } from '../explorer/mat4';
import { globalWmoPlacement, parsePlacements, type Placement } from '../explorer/objects';
import { parseAdtRoot, tileGrids, TILE_CELLS, TILE_SIZE, CHUNK_SIZE } from '../formats/adt';
import { parseM2 } from '../formats/m2';
import { parseWmoGroup, parseWmoRoot, WMO_LIQUID_CELL } from '../formats/wmo';
import { classifyLiquid, soundBankField } from './liquid-classification';
import { terrainCollision, transformPoint, wmoCollisionIndices } from './geometry';
import type { MeshInput, MeshTile } from './types';

/** Reads original collision, independently of camera streaming, textures and NPC spawns. */
export class MeshSource {
	constructor(private readonly maps: MapExplorer) {}
	private async wdtId(mapId: number): Promise<number> {
		const known = KNOWN_MAPS.find((m) => m.mapId === mapId);
		const id = known?.wdt ?? (await loadTable(this.maps.storage, DB2_FILES.Map)).getInt(mapId, 21);
		if (!id) throw new Error(`No WDT for native map ${mapId}`);
		return id;
	}
	async tiles(mapId: number): Promise<MeshTile[]> {
		const wdt = await this.wdtId(mapId);
		const map = await this.maps.wdt(wdt);
		if (map.globalWmo) throw new Error('WMO-only map baking is not implemented yet.');
		return map.tiles.flatMap((t) => t ? [{ mapId, wdt, x: t.x, y: t.y }] : []);
	}
	async extract(tile: MeshTile, preview = false): Promise<MeshInput> {
		if (tile.wdt !== await this.wdtId(tile.mapId)) throw new Error('Mesh tile belongs to a different native map.');
		const wdt = await this.maps.wdt(tile.wdt);
		const centre = 32 * TILE_SIZE;
		const origin: [number, number, number] = [centre - tile.x * TILE_SIZE, 0, centre - tile.y * TILE_SIZE];
		const positions: number[] = [], indices: number[] = [], classes: number[] = [];
		const liquidPositions: number[] = [], liquidIndices: number[] = [], liquidKinds: number[] = [];
		const files = new Map<number, Uint8Array>();
		const rawTiles: NonNullable<MeshInput['raw']>['tiles'] = [];
		const read = async (id: number) => {
			let bytes = files.get(id);
			if (!bytes) { bytes = await this.maps.storage.readFile(id); files.set(id, bytes); }
			return bytes;
		};
		let liquidTable: Awaited<ReturnType<typeof loadTable>> | null = null;
		const kindOf = async (type: number): Promise<number> => {
			liquidTable ??= await loadTable(this.maps.storage, DB2_FILES.LiquidType);
			return classifyLiquid(liquidTable, type);
		};
		const padding = 8;
		let liquidLayers = 0;
		const seen = new Set<string>();
		// A bounded, per-job model cache; no transferred arrays ever alias the viewer's cache.
		const models = new Map<number, ReturnType<typeof parseM2>>();
		const append = (verts: Float32Array, tris: ArrayLike<number>, kind: number, matrix: Mat4 | null, tx = 0, ty = 0, liquid = false) => {
			const outPositions = liquid ? liquidPositions : positions, outIndices = liquid ? liquidIndices : indices, outClasses = liquid ? liquidKinds : classes;
			const converted = new Float64Array(verts.length);
			for (let v = 0; v < verts.length; v += 3) {
				const p = matrix ? transformPoint(matrix, verts[v], verts[v + 1], verts[v + 2]) : [verts[v] + tx * TILE_SIZE, verts[v + 1], verts[v + 2] + ty * TILE_SIZE];
				converted.set([centre - p[0] - origin[0], p[1], centre - p[2] - origin[2]], v);
			}
			const remap = new Map<number, number>();
			for (let t = 0; t < tris.length; t += 3) {
				const ids = [tris[t], tris[t + 1], tris[t + 2]];
				if (ids.some((i) => i * 3 + 2 >= converted.length)) throw new Error('Collision index outside vertex array');
				// Keep all triangles intersecting the padded source tile, including those whose vertices lie outside it.
				const xs = ids.map((i) => converted[i * 3]), zs = ids.map((i) => converted[i * 3 + 2]);
				if (Math.max(...xs) < -TILE_SIZE - padding || Math.min(...xs) > padding || Math.max(...zs) < -TILE_SIZE - padding || Math.min(...zs) > padding) continue;
				for (const i of ids) {
					let mapped = remap.get(i);
					if (mapped === undefined) {
						mapped = outPositions.length / 3; remap.set(i, mapped);
						const p = converted.subarray(i * 3, i * 3 + 3);
						if (!p.every(Number.isFinite)) throw new Error('Non-finite source collision');
						outPositions.push(...p);
					}
					outIndices.push(mapped);
				}
				outClasses.push(kind);
			}
		};
		const m2 = async (fdid: number, matrix: Mat4) => {
			if (!preview) { await read(fdid); return; }
			let model = models.get(fdid);
			if (!model) { model = parseM2(await read(fdid)); models.set(fdid, model); }
			append(model.collision.positions, model.collision.indices, 2, matrix);
		};
		const object = async (p: Placement) => {
			const key = `${p.kind}:${p.uid}`;
			if (seen.has(key)) return;
			seen.add(key);
			try {
				if (p.kind === 'm2') return await m2(p.fdid, p.matrix);
				const root = parseWmoRoot(await read(p.fdid));
				if (!root.groupFdids.length) throw new Error('WMO has no file-ID group table');
				for (const fdid of root.groupFdids) {
					if (!fdid) continue;
					if (!preview) { await read(fdid); continue; }
					const g = parseWmoGroup(await read(fdid));
					if (!g) throw new Error(`Invalid WMO group ${fdid}`);
					if (g.flags & 0x4000000) continue; // Antiportal volumes are not physical surfaces.
					const solid = wmoCollisionIndices(g.indices, g.triangleFlags);
					append(g.positions, solid, 1, p.matrix);
					const l = g.liquid;
					if (l) for (let y = 0; y < l.yTiles; y++) for (let x = 0; x < l.xTiles; x++) {
						const flags = l.tiles[y * l.xTiles + x]; if ((flags & 15) === 15) continue;
						const kind = root.flags & 4 ? await kindOf(g.liquidType) : (flags & 3) + 1;
						const v: number[] = [];
						for (const [dx, dy] of [[0, 0], [1, 0], [0, 1], [1, 1]]) v.push(l.position[0] + (x + dx) * WMO_LIQUID_CELL, l.position[1] + (y + dy) * WMO_LIQUID_CELL, l.heights[(y + dy) * l.xVerts + x + dx]);
						append(new Float32Array(v), [0, 1, 3, 0, 3, 2], kind, p.matrix, 0, 0, true);
					}
				}
				const doodads = new Set<number>();
				for (const setId of new Set([0, p.doodadSet])) {
					const set = root.doodadSets[setId];
					if (!set) continue;
					for (let i = set.start; i < set.start + set.count; i++) {
						if (doodads.has(i)) continue;
						doodads.add(i);
						const d = root.doodads[i];
						if (d?.fdid) await m2(d.fdid, multiply(p.matrix, compose(translation(...d.position), fromQuaternion(...d.rotation), scaling(d.scale))));
					}
				}
			} catch (e) { throw new Error(`Collision ${p.kind} ${p.fdid}, placement ${p.uid}: ${(e as Error).message}`); }
		};
		if (!wdt.tiles[tile.y * 64 + tile.x]) throw new Error('Requested source tile does not exist');
		for (let y = Math.max(0, tile.y - 1); y <= Math.min(63, tile.y + 1); y++) {
			for (let x = Math.max(0, tile.x - 1); x <= Math.min(63, tile.x + 1); x++) {
				const source = wdt.tiles[y * 64 + x];
				if (!source) continue;
				rawTiles.push({ x: y, y: x, root: source.files.root, obj0: source.files.obj0, tex0: source.files.tex0 });
				if (source.files.tex0) await read(source.files.tex0);
				const root = parseAdtRoot(await read(source.files.root));
				if (root.chunks.length !== 256) throw new Error(`Incomplete terrain tile ${x}_${y}: ${root.chunks.length}/256 chunks`);
				if (x === tile.x && y === tile.y) liquidLayers = root.liquids.length;
				if (preview) {
					const grids = tileGrids(root);
					const terrain = terrainCollision(grids.outer, grids.inner, TILE_CELLS, TILE_SIZE, grids.holes);
					append(terrain.positions, terrain.indices, 0, null, x, y);
				}
				for (const l of preview ? root.liquids : []) {
					const v: number[] = [], tris: number[] = [], step = CHUNK_SIZE / 8;
					for (let row = 0; row <= l.height; row++) for (let col = 0; col <= l.width; col++) {
						let height = l.heights?.[row * (l.width + 1) + col] ?? l.minHeight;
						if (!Number.isFinite(height) || height < l.minHeight - 1 || height > l.maxHeight + 1) height = l.minHeight;
						v.push((l.chunk % 16) * CHUNK_SIZE + (l.x + col) * step, height, Math.floor(l.chunk / 16) * CHUNK_SIZE + (l.y + row) * step);
					}
					for (let row = 0; row < l.height; row++) for (let col = 0; col < l.width; col++) {
						if (l.exists && !l.exists[row * l.width + col]) continue;
						const a = row * (l.width + 1) + col, b = a + l.width + 1;
						tris.push(a, b, a + 1, a + 1, b, b + 1);
					}
					if (tris.length) append(new Float32Array(v), tris, await kindOf(l.type), null, x, y, true);
				}
				if (source.files.obj0) for (const p of parsePlacements(await read(source.files.obj0))) await object(p);
			}
		}
		if (wdt.globalWmo) await object(globalWmoPlacement(wdt.globalWmo));
		if ((!preview || liquidIndices.length) && !liquidTable) liquidTable = await loadTable(this.maps.storage, DB2_FILES.LiquidType);
		const liquidBanks: [number, number][] = liquidTable ? (liquidTable as Awaited<ReturnType<typeof loadTable>>).ids().filter((id) => id <= 65535).map((id) => {
			const bank = liquidTable!.getInt(id, soundBankField(liquidTable!.layoutHash));
			return [id, bank != null && bank >= 0 && bank <= 3 ? bank : 4];
		}) : [];
		return { tile, positions: new Float32Array(positions), indices: new Uint32Array(indices), classes: new Uint8Array(classes), origin, liquidLayers,
			sourceBuild: { product: this.maps.storage.stats.product, version: this.maps.storage.stats.version, buildName: this.maps.storage.stats.buildName },
			raw: { files: Array.from(files, ([id, bytes]) => ({ id, bytes: bytes.slice() })), tiles: rawTiles, liquidBanks },
			liquids: { positions: new Float32Array(liquidPositions), indices: new Uint32Array(liquidIndices), kinds: new Uint8Array(liquidKinds) } };
	}
}
