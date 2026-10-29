import { decodeFlaggedPreview, type AnalysisRequest, type AnalysisResult } from './reachability';
import { analyzeStream, type StreamStats } from './stream-analysis';
import { liquidTile, meshTile, streamBackend } from './stream-backend';
import { createScratch } from './scratch';

self.addEventListener('unhandledrejection', event => postMessage({ error: String(event.reason?.stack ?? event.reason) }));
self.addEventListener('message', async (event: MessageEvent<{ request: AnalysisRequest; runtimeUrl: string }>) => {
	if ('previewAck' in event.data) return;
	let api: any;
	let scratch: Awaited<ReturnType<typeof createScratch>> | undefined;
	try {
		const r = event.data.request, prefix = String(r.mapId).padStart(4, '0');
		const entries: { name: string; file: File }[] = [];
		for await (const [name, handle] of r.directory.entries()) {
			if (handle.kind === 'file' && (name === `${prefix}.mmap` || new RegExp(`^${prefix}_\\d+_\\d+\\.(mmtile|mliq)$`).test(name)))
				entries.push({ name, file: await handle.getFile() });
		}
		// Match the complete-map loader's insertion/root order. Never use undirected
		// union-find as a substitute for the original ordered directed traversal.
		entries.sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
		const files = new Map(entries.map(e => [e.name, e.file]));
		const tiles = entries.filter(e => e.name.endsWith('.mmtile')).map(e => meshTile(e.name));
		if (!tiles.length || !files.has(`${prefix}.mmap`)) throw new Error('This folder has no mesh tiles/map parameters for the selected map.');
		const names = new Set(tiles.map(t => t.name));
		let partial = !r.expectedTiles.length || r.expectedTiles.length !== names.size || r.expectedTiles.some(n => !names.has(n));
		if (r.analyze && partial && !r.allowPartial) throw new Error(`This is an incomplete or unverified map export (${tiles.length} tiles). Bake or resume the full map, or explicitly allow partial-set analysis. Partial results describe only supplied tiles.`);
		postMessage({ progress: 'Loading mesh analysis engine…' });
		const { dotnet } = await import(/* @vite-ignore */ event.data.runtimeUrl);
		const runtime = await dotnet.withDiagnosticTracing(false).create();
		api = (await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)).MeshEngine;
		const read = async (name: string) => {
			const file = files.get(name); if (!file) throw new Error(`Missing mesh input: ${name}`);
			return new Uint8Array(await file.arrayBuffer());
		};
		const write = async (name: string, data: Uint8Array | string) => {
			const handle = await r.directory.getFileHandle(name, { create: true }), file = await handle.createWritable();
			try { await file.write(typeof data === 'string' ? data : new Uint8Array(data)); await file.close(); }
			catch (e) { await file.abort().catch(() => {}); throw e; }
		};
		const marker = `reachability-${prefix}.json`;
		let report: AnalysisResult['report']; let savedTiles = 0; let stats: StreamStats | undefined;
		if (r.analyze) {
			scratch = await createScratch(); postMessage({ scratch: scratch.name });
			const liquids = [];
			for (const entry of entries.filter(e => e.name.endsWith('.mliq')))
				liquids.push(liquidTile(entry.name, new Uint8Array(await entry.file.slice(0, 39).arrayBuffer())));
			let saving = false;
			const backend = streamBackend(api, r.mapId, tiles, liquids, read, async (name, bytes) => {
				if (!saving) { await write(marker, JSON.stringify({ state: 'saving', partial, minComponent: r.minComponent, engine: 'streamed-v1' })); saving = true; }
				await write(name, bytes); savedTiles++;
			});
			const apply = backend.apply; let verified = false;
			backend.apply = async (tile, flags) => {
				if (!verified) {
					for (const entry of entries) {
						const now = await (await r.directory.getFileHandle(entry.name)).getFile();
						if (now.size !== entry.file.size || now.lastModified !== entry.file.lastModified) throw new Error('The mesh export changed during analysis. Stop other writers and rerun analysis; no new verdict was saved.');
					}
					verified = true;
				}
				return apply(tile, flags);
			};
			const result = await analyzeStream(tiles, liquids, backend, scratch, r.minComponent, text => postMessage({ progress: text }));
			report = result.report; stats = { ...result.stats, scratchBytes: scratch.bytesWritten() };
			await write(marker, JSON.stringify({ state: 'complete', partial, minComponent: r.minComponent, engine: 'streamed-v1', report, savedTiles, stats }, null, 2));
		}
		let markerState: string | undefined;
		try {
			const handle = await r.directory.getFileHandle(marker), metadata = JSON.parse(await (await handle.getFile()).text());
			markerState = metadata.state;
			if (!r.analyze && typeof metadata.partial === 'boolean') partial = metadata.partial;
		} catch { /* Imported or unclassified export. */ }
		let analyzed = r.analyze || markerState === 'complete';
		const wanted = r.preview ? `${prefix}_${String(r.preview.row).padStart(2, '0')}_${String(r.preview.col).padStart(2, '0')}.mmtile` : '';
		let geometry: AnalysisResult['geometry'];
		if (wanted || r.previewAll) {
			const radius = Math.min(1, Math.max(0, r.preview?.radius ?? 0));
			const visible = r.previewAll ? tiles : tiles.filter(t => Math.abs(t.row - r.preview!.row) <= radius && Math.abs(t.col - r.preview!.col) <= radius);
			const pieces = [];
			for (const tile of visible) {
				// Fresh snapshots: the analysis may have rewritten flags.
				api.BeginStreamWindow(r.mapId);
				const file = await (await r.directory.getFileHandle(tile.name)).getFile();
				api.PutAnalysisFile(tile.name, new Uint8Array(await file.arrayBuffer()));
				const piece = decodeFlaggedPreview(api.PreviewAnalysisTile(tile.row, tile.col), r.mapId);
				if (!markerState && piece.unreachable?.some(Boolean)) analyzed = true;
				if (!analyzed) delete piece.unreachable;
				if (r.previewAll) {
					postMessage({ progress: 'Displaying mesh tile ' + (visible.indexOf(tile) + 1) + '/' + visible.length });
					await new Promise<void>(resolve => {
						const ack = (e: MessageEvent) => { if (e.data.previewAck === tile.name) { self.removeEventListener('message', ack); resolve(); } };
						self.addEventListener('message', ack);
						postMessage({ tile: { key: tile.name, geometry: piece } }, { transfer: [(piece.positions as Float32Array).buffer, (piece.classes as Uint8Array).buffer] });
					});
				} else pieces.push(piece);
			}
			if (pieces.length) {
				const triangles = pieces.reduce((n, p) => n + p.triangles, 0), positions = new Float32Array(triangles * 9), classes = new Uint8Array(triangles), unreachable: boolean[] = [];
				let at = 0;
				for (const p of pieces) { positions.set(p.positions, at * 9); classes.set(p.classes, at); for (const flag of p.unreachable ?? new Array(p.triangles).fill(false)) unreachable.push(flag); at += p.triangles; }
				geometry = { mapId: r.mapId, source: 'navmesh', positions, classes, triangles, unreachable };
			}
		}
		if (!r.analyze && !geometry && !r.previewAll) throw new Error('No saved mesh tile beneath the camera for this map.');

		if (!analyzed && geometry) delete geometry.unreachable;
		const result: AnalysisResult = { report, geometry, partial, analyzed, savedTiles, loadedTiles: tiles.length, stats };
		const transfer = geometry ? [(geometry.positions as Float32Array).buffer, (geometry.classes as Uint8Array).buffer] : [];
		postMessage({ result }, { transfer });
	} catch (e) { postMessage({ error: (e as Error).message }); }
	finally { api?.EndAnalysis(); await scratch?.close().catch(() => {}); }
});
