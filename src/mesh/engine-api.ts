import { strFromU8, unzipSync } from 'fflate';
import type { MeshBake, MeshInput, MeshQuality } from './types';

/** Private in-process adapter format, versioned independently of game/runtime mesh files. */
export function encodeBakeInput(input: MeshInput): Uint8Array {
	const base = encodeBaseInput(input);
	if (!input.edits) return base;
	const json = new TextEncoder().encode(JSON.stringify(input.edits));
	const out = new Uint8Array(base.length + 4 + json.length); out.set(base);
	new DataView(out.buffer).setInt32(base.length, json.length, true); out.set(json, base.length + 4); return out;
}
function encodeBaseInput(input: MeshInput): Uint8Array {
	if (input.raw) return encodeRawInput(input);
	const solid = { positions: input.positions, indices: input.indices, kinds: input.classes };
	const liquid = input.liquids ?? { positions: new Float32Array(), indices: new Uint32Array(), kinds: new Uint8Array() };
	const size = (g: typeof liquid) => 8 + g.positions.byteLength + g.indices.byteLength + g.kinds.byteLength;
	const out = new Uint8Array(28 + size(solid) + size(liquid));
	const view = new DataView(out.buffer); let at = 0;
	const int = (n: number) => { view.setInt32(at, n, true); at += 4; };
	int(0x31424D45); int(input.tile.mapId); int(input.tile.x); int(input.tile.y);
	for (const n of input.origin) { view.setFloat32(at, n, true); at += 4; }
	for (const g of [solid, liquid]) {
		if (g.positions.length % 3 || g.indices.length % 3 || g.kinds.length !== g.indices.length / 3) throw new Error('Invalid source triangle arrays');
		int(g.positions.length / 3); int(g.indices.length);
		for (const n of g.positions) { view.setFloat32(at, n, true); at += 4; }
		for (const n of g.indices) int(n);
		out.set(g.kinds, at); at += g.kinds.length;
	}
	return out;
}

function encodeRawInput(input: MeshInput): Uint8Array {
	const raw = input.raw!;
	const out = new Uint8Array(28 + raw.files.reduce((n, f) => n + 8 + f.bytes.length, 0) + raw.liquidBanks.length * 3 + raw.tiles.length * 20);
	const v = new DataView(out.buffer); let p = 0;
	const int = (n: number) => { v.setInt32(p, n, true); p += 4; };
	int(0x32524D45); int(input.tile.mapId); int(input.tile.x); int(input.tile.y); int(raw.files.length);
	for (const f of raw.files) { int(f.id); int(f.bytes.length); out.set(f.bytes, p); p += f.bytes.length; }
	int(raw.liquidBanks.length);
	for (const [id, bank] of raw.liquidBanks) { v.setUint16(p, id, true); p += 2; out[p++] = bank; }
	int(raw.tiles.length);
	for (const t of raw.tiles) { int(t.x); int(t.y); int(t.root); int(t.obj0); int(t.tex0); }
	return out;
}

export function decodeBakeOutput(data: Uint8Array, input: MeshInput): MeshBake {
	const files = unzipSync(data);
	const metadata = JSON.parse(strFromU8(files['engine.json']));
	const preview = (bytes: Uint8Array) => {
		const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
		const triangles = view.getInt32(0, true);
		if (triangles < 0 || bytes.length !== 4 + triangles * 40) throw new Error('Invalid Mappster preview data');
		const positions: number[] = [], classes: number[] = []; let at = 4;
		for (let t = 0; t < triangles; t++) {
			classes.push(view.getInt32(at, true)); at += 4;
			for (let v = 0; v < 9; v++, at += 4) positions.push(view.getFloat32(at, true));
		}
		return { mapId: input.tile.mapId, positions, classes, triangles, source: 'navmesh' as const };
	};
	const geometry = preview(files['preview.bin']);
	const liquid = preview(files['liquid-preview.bin']);
	delete files['preview.bin']; delete files['liquid-preview.bin']; delete files['engine.json'];
	const m = metadata.metrics;
	const fallbacks: string[] = [];
	for (const name of ['coarserContours', 'monotoneTiles', 'splitGaveUp', 'swimBuildFellBack']) if (m[name]) fallbacks.push(`${name}: ${m[name]}`);
	if (m.fallbackWhy) fallbacks.push(m.fallbackWhy);
	const quality: MeshQuality = { ...m, pipeline: metadata.pipeline, fallbacks };
	const liquidGeometry = liquid.triangles ? liquid : undefined;
	return { geometry, files, settings: metadata.settings, milliseconds: metadata.milliseconds, quality, liquidGeometry };
}
