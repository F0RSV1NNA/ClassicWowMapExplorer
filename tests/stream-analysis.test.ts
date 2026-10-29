import { expect, test } from 'bun:test';
import { analyzeStream, type PageStore, type StreamBackend, type StreamLiquidTile, type StreamTile } from '../src/mesh/stream-analysis';

function memoryStore() {
	const files = new Map<string, Uint8Array>();
	const store: PageStore = { async put(k, b) { files.set(k, b.slice()); }, async get(k) { const b = files.get(k); if (!b) throw Error(k); return b.slice(); } };
	return { files, store };
}
function graph(links: [number, number][][], centers?: number[][]) {
	const edges = links.flat(), bytes = new Uint8Array(12 + links.length * 20 + edges.length * 8), v = new DataView(bytes.buffer);
	v.setUint32(0, links.length, true); v.setUint32(4, edges.length, true);
	for (let p = 0; p < links.length; p++) for (let k = 0; k < 4; k++) v.setFloat32(8 + p * 16 + k * 4, centers?.[p]?.[k] ?? (k === 3 ? -3.402823466e38 : 0), true);
	let at = 8 + links.length * 16, offset = 0;
	for (const list of links) { v.setUint32(at, offset, true); at += 4; offset += list.length; }
	v.setUint32(at, offset, true); at += 4;
	for (const pair of edges) for (const n of pair) { v.setUint32(at, n, true); at += 4; }
	return bytes;
}
const tile = (id: number): StreamTile => ({ id, row: 0, col: id, name: `tile-${id}` });

test('streamed flood preserves directed traversal instead of merging one-way links as undirected edges', async () => {
	const { store } = memoryStore(); let flags: number[] = [];
	const backend: StreamBackend = { async graph() { return graph([[[1,2]], [[1,2]], []]); }, async liquid() { throw Error('Unexpected water'); }, async apply(_, f) { flags = [...f]; return true; } };
	const { report } = await analyzeStream([tile(1)], [], backend, store, 1000);
	expect(report.Components).toBe(2); expect(report.Largest).toBe(2); expect(flags).toEqual([0, 1, 0]);
});

test('dirty visitation pages survive eviction while a route crosses tile boundaries', async () => {
	const { store } = memoryStore(), output = new Map<number, number[]>();
	const pages = new Map([[1, graph([[[2,0]]])], [2, graph([[[1,0]], []])]]);
	const backend: StreamBackend = { async graph(t) { return pages.get(t.id)!; }, async liquid() { throw Error('Unexpected water'); }, async apply(t, f) { output.set(t.id, [...f]); return true; } };
	const { report } = await analyzeStream([tile(1), tile(2)], [], backend, store, 1000, undefined, 1);
	expect(report.Polys).toBe(3); expect(report.Components).toBe(2); expect(report.Stranded).toBe(1);
	expect(output.get(1)).toEqual([0]); expect(output.get(2)).toEqual([0, 1]);
});

function water(t: StreamLiquidTile) {
	const bytes = new Uint8Array(20 + t.width * t.height * 8), v = new DataView(bytes.buffer);
	[t.x, t.y, t.width, t.height, 1].forEach((n, i) => v.setInt32(i * 4, n, true));
	for (let i = 0; i < t.width * t.height; i++) { v.setFloat32(20 + i * 4, 2, true); v.setInt32(20 + t.width * t.height * 4 + i * 4, 0, true); }
	return bytes;
}
test('liquid pages join across four-neighbour seams and never join merely diagonal cells', async () => {
	for (const diagonal of [false, true]) {
		const liquids: StreamLiquidTile[] = [{ id: 1, row: 0, col: 0, name: 'a', x: -2, y: 0, width: 2, height: 1 }, { id: 2, row: 0, col: 1, name: 'b', x: 0, y: diagonal ? 1 : 0, width: 2, height: 1 }];
		const { store } = memoryStore();
		const backend: StreamBackend = { async graph() { return graph([[], []], [[-2,0,0,2], [1,diagonal ? 1 : 0,0,2]]); }, async liquid(t) { return water(t); }, async apply() { return true; } };
		const { report } = await analyzeStream([tile(1)], liquids, backend, store, 1000, undefined, 1);
		expect(report.Components).toBe(diagonal ? 2 : 1); expect(report.Stranded).toBe(diagonal ? 1 : 0);
	}
});

test('deep traversal spills its stack into scratch pages without losing pending polygons', async () => {
	const n = 33000, links: [number, number][][] = Array.from({ length: n + 1 }, () => []);
	links[0] = Array.from({ length: n }, (_, p) => [1, p + 1]);
	const bytes = graph(links), { files, store } = memoryStore();
	const backend: StreamBackend = { async graph() { return bytes; }, async liquid() { throw Error('Unexpected water'); }, async apply(_, flags) { expect(flags.some(Boolean)).toBe(false); return false; } };
	const { report } = await analyzeStream([tile(1)], [], backend, store, 1000);
	expect(report.Polys).toBe(n + 1); expect(report.Components).toBe(1); expect(files.has('stack-0')).toBe(true);
});
