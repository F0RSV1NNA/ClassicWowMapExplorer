import { decodeGraph, overlaps, type StreamBackend, type StreamLiquidTile, type StreamTile } from './stream-analysis';

export interface NativeStreamApi {
	BeginStreamWindow(map: number): void;
	PutAnalysisFile(name: string, bytes: Uint8Array): void;
	StreamGraph(row: number, col: number): Uint8Array;
	StreamLiquid(row: number, col: number): Uint8Array;
	ApplyStreamFlags(row: number, col: number, flags: Uint8Array): boolean;
	GetAnalysisFile(name: string): Uint8Array;
}
export const tileId = (row: number, col: number) => (row * 65536 + col) >>> 0;
export function meshTile(name: string): StreamTile {
	const parts = name.split('_'), row = Number(parts[1]), col = Number(parts[2].split('.')[0]);
	if (!Number.isInteger(row) || !Number.isInteger(col) || row < 0 || col < 0 || row > 65535 || col > 65535) throw new Error(`Invalid mesh tile name: ${name}`);
	return { id: tileId(row, col), row, col, name };
}
export function liquidTile(name: string, bytes: Uint8Array): StreamLiquidTile {
	const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	if (bytes.length < 39 || v.getUint32(0, true) !== 0x51494c4d || v.getInt32(4, true) !== 2) throw new Error(`Unsupported liquid file: ${name}`);
	const width = v.getInt32(20, true), height = v.getInt32(24, true), ox = v.getFloat32(8, true), oy = v.getFloat32(12, true);
	if (width <= 0 || height <= 0 || !Number.isFinite(ox) || !Number.isFinite(oy)) throw new Error(`Invalid liquid bounds: ${name}`);
	// NavPrune uses LiquidMap.Cell (one yard), including f32 addition, when keying cells.
	const x = Math.floor(ox), y = Math.floor(oy);
	return { ...meshTile(name), x, y, width: Math.floor(Math.fround(ox + width - 1)) - x + 1, height: Math.floor(Math.fround(oy + height - 1)) - y + 1 };
}

export function streamBackend(api: NativeStreamApi, mapId: number, tiles: StreamTile[], liquids: StreamLiquidTile[],
	read: (name: string) => Promise<Uint8Array>, save: (name: string, bytes: Uint8Array) => Promise<void>): StreamBackend {
	let params: Uint8Array | undefined;
	const mapName = `${String(mapId).padStart(4, '0')}.mmap`;
	const window = async (names: string[]) => {
		api.BeginStreamWindow(mapId);
		params ??= await read(mapName); api.PutAnalysisFile(mapName, params);
		for (const name of names.sort()) api.PutAnalysisFile(name, await read(name));
	};
	return {
		async graph(tile) {
			const nearTiles = tiles.filter(t => Math.abs(t.row - tile.row) <= 1 && Math.abs(t.col - tile.col) <= 1);
			const nearLiquids = liquids.filter(t => Math.abs(t.row - tile.row) <= 1 && Math.abs(t.col - tile.col) <= 1);
			await window([...nearTiles, ...nearLiquids].map(t => t.name));
			let bytes = api.StreamGraph(tile.row, tile.col);
			// Off-mesh polygon centres may lie beyond the immediate neighbourhood. Load
			// their own surface sidecars as well, matching NavPrune.LiquidSurface exactly.
			const g = decodeGraph(bytes), have = new Set(nearLiquids.map(t => t.id)); let extra = false;
			for (let p = 0; p < g.count; p++) {
				const c = p * 4, size = Math.fround(533.33333);
				const row = Math.floor(Math.fround(32 - Math.fround(g.centers[c] / size)));
				const col = Math.floor(Math.fround(32 - Math.fround(g.centers[c + 1] / size)));
				const id = tileId(row, col);
				if (have.has(id)) continue;
				const liquid = liquids.find(t => t.id === id);
				if (liquid) { api.PutAnalysisFile(liquid.name, await read(liquid.name)); have.add(id); extra = true; }
			}
			if (extra) bytes = api.StreamGraph(tile.row, tile.col);
			return bytes;
		},
		async liquid(tile) {
			await window(liquids.filter(t => overlaps(tile, t)).map(t => t.name));
			return api.StreamLiquid(tile.row, tile.col);
		},
		async apply(tile, flags) {
			await window([tile.name]);
			if (!api.ApplyStreamFlags(tile.row, tile.col, flags)) return false;
			await save(tile.name, api.GetAnalysisFile(tile.name)); return true;
		},
	};
}
