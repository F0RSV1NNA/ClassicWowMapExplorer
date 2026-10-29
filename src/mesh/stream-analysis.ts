import type { ReachabilityReport } from './reachability';

export interface PageStore { put(key: string, bytes: Uint8Array): Promise<void>; get(key: string): Promise<Uint8Array> }
export interface Bounds { x: number; y: number; width: number; height: number }
export interface StreamTile { id: number; row: number; col: number; name: string; polys?: number }
export interface StreamLiquidTile extends Bounds { id: number; row: number; col: number; name: string; offset?: number }
export interface StreamBackend {
	graph(tile: StreamTile): Promise<Uint8Array>;
	liquid(tile: StreamLiquidTile): Promise<Uint8Array>;
	apply(tile: StreamTile, stranded: Uint8Array): Promise<boolean>;
}
const asBytes = (a: ArrayBufferView) => new Uint8Array(a.buffer, a.byteOffset, a.byteLength);
const align = (bytes: Uint8Array) => bytes.byteOffset % 4 ? bytes.slice() : bytes;

export function decodeGraph(raw: Uint8Array) {
	const bytes = align(raw), v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	const count = v.getUint32(0, true), edges = v.getUint32(4, true);
	if (bytes.length !== 12 + count * 20 + edges * 8) throw new Error('Invalid compact graph page');
	const centers = new Float32Array(bytes.buffer, bytes.byteOffset + 8, count * 4);
	const offsets = new Uint32Array(bytes.buffer, bytes.byteOffset + 8 + count * 16, count + 1);
	const links = new Uint32Array(bytes.buffer, bytes.byteOffset + 12 + count * 20, edges * 2);
	if (offsets[0] !== 0 || offsets[count] !== edges) throw new Error('Invalid graph adjacency offsets');
	return { bytes, count, centers, offsets, links };
}
export function decodeLiquid(raw: Uint8Array) {
	const bytes = align(raw), v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	const x = v.getInt32(0, true), y = v.getInt32(4, true), width = v.getInt32(8, true), height = v.getInt32(12, true), bodies = v.getInt32(16, true);
	if (width <= 0 || height <= 0 || bodies < 0 || bytes.length !== 20 + width * height * 8) throw new Error('Invalid liquid component page');
	return { bytes, x, y, width, height, bodies,
		heights: new Float32Array(bytes.buffer, bytes.byteOffset + 20, width * height),
		labels: new Int32Array(bytes.buffer, bytes.byteOffset + 20 + width * height * 4, width * height) };
}

// Compact global metadata only: a few integers per component, never DtPoly objects
// or every wet cell. Parent direction deliberately follows NavPrune.Union, not rank.
class Components {
	private parents = new Uint32Array(1024);
	weights = new Uint32Array(1024);
	length = 0;
	add() {
		if (this.length === this.parents.length) {
			const p = new Uint32Array(this.length * 2), w = new Uint32Array(this.length * 2);
			p.set(this.parents); w.set(this.weights); this.parents = p; this.weights = w;
		}
		const id = this.length++; this.parents[id] = id; return id;
	}
	find(id: number): number {
		while (this.parents[id] !== id) { this.parents[id] = this.parents[this.parents[id]]; id = this.parents[id]; }
		return id;
	}
	union(a: number, b: number) { a = this.find(a); b = this.find(b); if (a !== b) this.parents[b] = a; }
}

// LRU pages hold adjacency plus per-polygon visitation. Dirty state goes to scratch
// storage before eviction; the graph itself is immutable throughout analysis.
class GraphCache {
	private pages = new Map<number, { graph: ReturnType<typeof decodeGraph>; state: Int32Array; dirty: boolean; size: number }>();
	private bytes = 0;
	constructor(private store: PageStore, private budget: number) {}
	async get(id: number) {
		let entry = this.pages.get(id);
		if (entry) { this.pages.delete(id); this.pages.set(id, entry); return entry; }
		const graph = decodeGraph(await this.store.get(`graph-${id}`));
		const stateBytes = align(await this.store.get(`state-${id}`));
		const state = new Int32Array(stateBytes.buffer, stateBytes.byteOffset, stateBytes.byteLength / 4);
		if (state.length !== graph.count) throw new Error('Graph state count mismatch');
		entry = { graph, state, dirty: false, size: graph.bytes.length + state.byteLength };
		this.pages.set(id, entry); this.bytes += entry.size;
		while (this.bytes > this.budget && this.pages.size > 1) {
			const [key, old] = this.pages.entries().next().value!;
			if (old.dirty) await this.store.put(`state-${key}`, asBytes(old.state));
			this.pages.delete(key); this.bytes -= old.size;
		}
		return entry;
	}
	async flush() {
		for (const [id, page] of this.pages) if (page.dirty) { await this.store.put(`state-${id}`, asBytes(page.state)); page.dirty = false; }
	}
}
class LiquidCache {
	private pages = new Map<number, ReturnType<typeof decodeLiquid>>();
	private bytes = 0;
	constructor(private store: PageStore, private budget: number) {}
	async get(id: number) {
		let page = this.pages.get(id);
		if (page) { this.pages.delete(id); this.pages.set(id, page); return page; }
		page = decodeLiquid(await this.store.get(`liquid-${id}`)); this.pages.set(id, page); this.bytes += page.bytes.length;
		while (this.bytes > this.budget && this.pages.size > 1) { const [key, old] = this.pages.entries().next().value!; this.pages.delete(key); this.bytes -= old.bytes.length; }
		return page;
	}
}
class Journal {
	private buffer = new Uint32Array(32768 * 3);
	private used = 0; private pages = 0;
	constructor(private store: PageStore) {}
	async append(tile: number, poly: number, component: number) {
		if (this.used === this.buffer.length) await this.flush();
		this.buffer[this.used++] = tile; this.buffer[this.used++] = poly; this.buffer[this.used++] = component;
	}
	async flush() {
		if (!this.used) return;
		await this.store.put(`journal-${this.pages++}`, asBytes(this.buffer.subarray(0, this.used))); this.used = 0;
	}
	async *records() {
		await this.flush();
		for (let i = 0; i < this.pages; i++) {
			const bytes = align(await this.store.get(`journal-${i}`)), values = new Uint32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 4);
			for (let j = 0; j < values.length; j += 3) yield [values[j], values[j + 1], values[j + 2]] as const;
		}
	}
}
class PagedStack {
	private buffer: Uint32Array = new Uint32Array(32768 * 2); private used = 0; private pages = 0;
	length = 0;
	constructor(private store: PageStore) {}
	async push(tile: number, poly: number) {
		if (this.used === this.buffer.length) { await this.store.put(`stack-${this.pages++}`, asBytes(this.buffer)); this.used = 0; }
		this.buffer[this.used++] = tile; this.buffer[this.used++] = poly; this.length++;
	}
	async pop() {
		if (!this.used) {
			const bytes = align(await this.store.get(`stack-${--this.pages}`));
			this.buffer = new Uint32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 4); this.used = this.buffer.length;
		}
		const poly = this.buffer[--this.used], tile = this.buffer[--this.used]; this.length--; return [tile, poly] as const;
	}
}
function inside(b: Bounds, x: number, y: number) { return x >= b.x && y >= b.y && x < b.x + b.width && y < b.y + b.height; }
export function overlaps(a: Bounds, b: Bounds, margin = 0) {
	return a.x <= b.x + b.width - 1 + margin && b.x <= a.x + a.width - 1 + margin && a.y <= b.y + b.height - 1 + margin && b.y <= a.y + a.height - 1 + margin;
}
class WaterIndex {
	private bins = new Map<string, StreamLiquidTile[]>();
	constructor(tiles: StreamLiquidTile[]) {
		for (const t of tiles) for (let y = Math.floor(t.y / 512); y <= Math.floor((t.y + t.height - 1) / 512); y++)
			for (let x = Math.floor(t.x / 512); x <= Math.floor((t.x + t.width - 1) / 512); x++) {
				const key = `${x},${y}`; let bin = this.bins.get(key); if (!bin) this.bins.set(key, bin = []); bin.push(t);
			}
	}
	at(x: number, y: number) { return this.bins.get(`${Math.floor(x / 512)},${Math.floor(y / 512)}`)?.find(t => inside(t, x, y)); }
}

export interface StreamStats { graphPages: number; polygons: number; waterPages: number; scratchBytes?: number }
/** Exact ordered directed flood, followed by NavPrune's liquid union and size policy.
 * Disk pages and bounded caches replace its continent-wide object dictionaries. */
export async function analyzeStream(tiles: StreamTile[], liquids: StreamLiquidTile[], backend: StreamBackend, store: PageStore,
	minComponent: number, progress: (text: string) => void = () => {}, cacheBudget = 32 * 1024 * 1024): Promise<{ report: ReachabilityReport; stats: StreamStats }> {
	const byId = new Map(tiles.map(t => [t.id, t])), cache = new GraphCache(store, cacheBudget), waterCache = new LiquidCache(store, cacheBudget);
	const components = new Components(), waterComponents = new Components();
	let polygons = 0;
	for (let i = 0; i < tiles.length; i++) {
		const tile = tiles[i]; progress(`Building graph pages ${i + 1}/${tiles.length}: ${tile.name}`);
		const bytes = await backend.graph(tile), graph = decodeGraph(bytes); tile.polys = graph.count; polygons += graph.count;
		await store.put(`graph-${tile.id}`, bytes);
		const state = new Int32Array(graph.count); state.fill(-1); await store.put(`state-${tile.id}`, asBytes(state));
	}
	const stack = new PagedStack(store), journal = new Journal(store); let visited = 0;
	for (const tile of tiles) for (let p = 0; p < tile.polys!; p++) {
		const root = await cache.get(tile.id); if (root.state[p] >= 0) continue;
		const id = components.add(); root.state[p] = id; root.dirty = true;
		if (liquids.length) await journal.append(tile.id, p, id);
		await stack.push(tile.id, p);
		while (stack.length) {
			const [tid, poly] = await stack.pop(), page = await cache.get(tid), g = page.graph;
			components.weights[id]++; visited++;
			if (visited % 32768 === 0) progress(`Tracing connectivity ${visited.toLocaleString()}/${polygons.toLocaleString()} polygons`);
			for (let e = g.offsets[poly]; e < g.offsets[poly + 1]; e++) {
				const nt = g.links[e * 2], np = g.links[e * 2 + 1];
				if (!byId.has(nt)) throw new Error('Graph references a tile missing from this export');
				const neighbor = await cache.get(nt);
				if (np >= neighbor.state.length) throw new Error('Graph references an invalid polygon');
				if (neighbor.state[np] >= 0) continue;
				neighbor.state[np] = id; neighbor.dirty = true;
				if (liquids.length) await journal.append(nt, np, id);
				await stack.push(nt, np);
			}
		}
	}
	await cache.flush(); await journal.flush();
	for (let i = 0; i < liquids.length; i++) {
		const t = liquids[i]; progress(`Building liquid pages ${i + 1}/${liquids.length}`);
		const bytes = await backend.liquid(t), page = decodeLiquid(bytes);
		Object.assign(t, { x: page.x, y: page.y, width: page.width, height: page.height, offset: waterComponents.length });
		for (let b = 0; b < page.bodies; b++) waterComponents.add();
		await store.put(`liquid-${t.id}`, bytes);
	}
	// Join shared boundary cells and their four-neighbour edges, never diagonal cells.
	for (let i = 0; i < liquids.length; i++) {
		const a = liquids[i]; progress(`Joining liquid seams ${i + 1}/${liquids.length}`);
		for (let j = i + 1; j < liquids.length; j++) {
			const b = liquids[j]; if (!overlaps(a, b, 1)) continue;
			const pa = await waterCache.get(a.id), pb = await waterCache.get(b.id);
			for (let y = Math.max(a.y, b.y - 1); y < Math.min(a.y + a.height, b.y + b.height + 1); y++)
				for (let x = Math.max(a.x, b.x - 1); x < Math.min(a.x + a.width, b.x + b.width + 1); x++) {
					const ia = (y - a.y) * a.width + x - a.x, la = pa.labels[ia]; if (la < 0) continue;
					for (const [dx, dy] of [[0,0],[1,0],[-1,0],[0,1],[0,-1]]) {
						const nx = x + dx, ny = y + dy; if (!inside(b, nx, ny)) continue;
						const ib = (ny - b.y) * b.width + nx - b.x, lb = pb.labels[ib];
						if (lb >= 0 && Math.abs(Math.fround(pa.heights[ia] - pb.heights[ib])) <= 2) waterComponents.union(a.offset! + la, b.offset! + lb);
					}
				}
		}
	}
	if (liquids.length) {
		const waterIndex = new WaterIndex(liquids), first = new Map<number, number>(); let processed = 0;
		// The visitation journal preserves the original dictionary insertion order, even
		// for one-way links and liquid bridges. A union-find-only graph flood is NOT equivalent.
		for await (const [tid, p, component] of journal.records()) {
			const g = (await cache.get(tid)).graph, c = p * 4;
			if (++processed % 32768 === 0) progress(`Applying swim connectivity ${processed.toLocaleString()}/${polygons.toLocaleString()}`);
			if (g.centers[c + 2] > Math.fround(g.centers[c + 3] + Math.fround(2.2))) continue;
			const x = Math.floor(g.centers[c]), y = Math.floor(g.centers[c + 1]), t = waterIndex.at(x, y);
			if (!t) continue;
			const page = await waterCache.get(t.id), label = page.labels[(y - t.y) * t.width + x - t.x]; if (label < 0) continue;
			const body = waterComponents.find(t.offset! + label), previous = first.get(body);
			if (previous === undefined) first.set(body, component); else components.union(previous, component);
		}
	}
	const sizes = new Float64Array(components.length);
	for (let c = 0; c < components.length; c++) sizes[components.find(c)] += components.weights[c];
	let mainland = 0, count = 0;
	for (let c = 0; c < sizes.length; c++) { if (sizes[c] > 0) count++; if (sizes[c] > sizes[mainland]) mainland = c; }
	if (!polygons) throw new Error('No polygons in supplied map');
	let stranded = 0, rewritten = 0;
	const threshold = minComponent <= 0 ? 2147483647 : minComponent;
	for (let i = 0; i < tiles.length; i++) {
		const tile = tiles[i], state = (await cache.get(tile.id)).state, flags = new Uint8Array(state.length);
		for (let p = 0; p < flags.length; p++) {
			const root = components.find(state[p]);
			if (root !== mainland && sizes[root] < threshold) { flags[p] = 1; stranded++; }
		}
		progress(`Saving reachability ${i + 1}/${tiles.length}: ${tile.name}`);
		if (await backend.apply(tile, flags)) rewritten++;
	}
	return { report: { Tiles: tiles.length, Polys: polygons, Components: count, Largest: sizes[mainland], Stranded: stranded,
		Rewritten: rewritten, StrandedPercent: stranded * 100 / polygons, Error: '' }, stats: { graphPages: tiles.length, polygons, waterPages: liquids.length } };
}
