import { strToU8, zipSync } from 'fflate';
import type { AsyncStorageApi } from '../worker/protocol';
import type { Viewer } from '../viewer/viewer';
import { TILE_SIZE } from '../formats/adt';
import { runBake } from './client';
import { defaultBakeConcurrency, parallelTiles, serialLane, priorMemoryFailures } from './batch';
import { previewGeometry } from './geometry';
import { tileMetadata } from './project';
import type { MeshBake, MeshInput, MeshTile } from './types';
import { runReachability } from './reachability-client';
import { saveTileExport, tileExportCurrent, writeExport } from './export';
import { meshPositionDetails } from './position';
import { encodeBakeInput } from './engine-api';
import { setUpAuthoring } from './editor';

export function setUpMesh(viewer: Viewer, storage: AsyncStorageApi, product: string, sourceBuild?: MeshInput['sourceBuild']): void {
	const buildInfo = sourceBuild ? { product: sourceBuild.product, version: sourceBuild.version, buildName: sourceBuild.buildName } : undefined;
	const el = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
	const status = el('mesh-status');
	status.style.whiteSpace = 'pre-line';
	const progress = el<HTMLProgressElement>('mesh-progress');
	const progressLabel = el('mesh-progress-label');
	let baking = false;
	const discovering = (label: string) => {
		baking = true;
		el('mesh-progress-panel').hidden = false;
		progress.removeAttribute('value');
		progressLabel.textContent = label;
	};
	const tileProgress = (done: number, total: number, detail: string) => {
		el('mesh-progress-panel').hidden = false;
		progress.max = Math.max(1, total); progress.value = done;
		progressLabel.textContent = `${done.toLocaleString()} / ${total.toLocaleString()} tiles (${total ? Math.floor(done / total * 100) : 0}%) — ${detail}`;
	};
	let controller: AbortController | null = null;
	let followController: AbortController | null = null;
	let followKey = '', folderRevision = 0;
	let wholeMapVisible = false;
	let last: { bake: MeshBake; metadata: ReturnType<typeof tileMetadata> } | null = null;
	let editorDirectory: FileSystemDirectoryHandle | null = null;
	let authoring: ReturnType<typeof setUpAuthoring> | undefined;
	const editorMap = el<HTMLSelectElement>('mesh-editor-map');
	const editorStatus = el('mesh-editor-status');
	const positionText = el<HTMLTextAreaElement>('mesh-position');
	let qualityKey = '';
	const updatePosition = () => {
		const point = viewer.meshPosition(), details = point ? meshPositionDetails(point) : null;
		if (document.activeElement !== positionText) positionText.value = details?.text ?? 'Move over a map to read native coordinates.';
		for (const id of ['mesh-copy-position', 'mesh-copy-report', 'mesh-copy-here']) el<HTMLButtonElement>(id).disabled = !point;
		const key = `${folderRevision}:${details?.metadata}`;
		if (editorDirectory && details?.metadata && key !== qualityKey) {
			qualityKey = key;
			void editorDirectory.getFileHandle(details.metadata).then(h => h.getFile()).then(f => f.text()).then(JSON.parse).then(metadata => {
				if (key !== qualityKey) return;
				const quality = metadata.quality;
				el('mesh-tile-quality').textContent = quality?.swimBuildFellBack > 0
					? 'Bake warning: this tile used the plain Recast fallback. Copy its problem report before tuning holes or railings.'
					: quality?.fallbackWhy ? 'This tile recovered after a bake retry. Copy its problem report for the recorded cause.' : 'Saved tile has no recorded bake fallback.';
			}).catch(() => { if (key === qualityKey) el('mesh-tile-quality').textContent = 'No saved tile metadata at this camera position.'; });
		}
	};
	const copyPosition = async (report: boolean) => {
		const point = viewer.meshPosition(); if (!point) return;
		const details = meshPositionDetails(point);
		let text = details.text;
		if (report) {
			text += `\nView: ${viewer.shareLink()}\nProduct: ${product}\nReachability display: ${el<HTMLSelectElement>('mesh-reachability-view').value}`;
			if (editorDirectory && details.metadata) {
				text += `\nExport: ${editorDirectory.name}`;
				try {
					const file = await (await editorDirectory.getFileHandle(details.metadata)).getFile();
					const metadata = JSON.parse(await file.text());
					const keys = ['CellSize', 'CellHeight', 'AgentHeight', 'AgentRadius', 'AgentMaxClimb', 'AgentMaxSlope', 'DetailSampleDist', 'DetailSampleMaxError', 'BuriedMargin', 'BlockDoodads', 'IndoorStepCut'];
					text += '\nSaved tile diagnostics:\n' + JSON.stringify({ saved: new Date(file.lastModified).toISOString(), sourceBuild: metadata.sourceBuild, executionBackend: metadata.executionBackend, settings: Object.fromEntries(keys.filter(k => k in (metadata.settings ?? {})).map(k => [k, metadata.settings[k]])), quality: metadata.quality }, null, 2);
				} catch (e) { text += `\nTile metadata unavailable: ${(e as Error).message}`; }
				try {
					const marker = JSON.parse(await (await (await editorDirectory.getFileHandle(`reachability-${String(point.mapId).padStart(4, '0')}.json`)).getFile()).text());
					text += '\nReachability: ' + JSON.stringify({ state: marker.state, partial: marker.partial, report: marker.report });
				} catch { text += '\nReachability: no saved analysis marker.'; }
			} else text += '\nOpen the saved mesh export in Mesh Editor to include its tile diagnostics.';
		}
		positionText.value = text; positionText.focus(); positionText.select();
		try {
			await navigator.clipboard.writeText(text);
			el('mesh-position-status').textContent = report ? 'Mesh problem report copied.' : 'Camera map, tile and coordinates copied.';
			el('mesh-copy-here').textContent = 'Copied'; setTimeout(() => { el('mesh-copy-here').textContent = 'Copy camera position'; }, 2000);
		} catch {
			el<HTMLDetailsElement>('mesh-options-menu').open = true; positionText.focus(); positionText.select();
			el('mesh-position-status').textContent = 'Text selected under Camera position; press Ctrl+C to copy.';
		}
	};
	for (const id of ['mesh-copy-position', 'mesh-copy-here']) el(id).addEventListener('click', () => void copyPosition(false));
	el('mesh-copy-report').addEventListener('click', () => void copyPosition(true));
	const render = () => {
		for (const id of ['mesh-collision', 'mesh-bake-area', 'mesh-bake-map', 'mesh-bake-world']) el<HTMLButtonElement>(id).disabled = !!controller;
		el<HTMLButtonElement>('mesh-cancel').disabled = !controller;
		el<HTMLButtonElement>('mesh-navmesh').disabled = !!controller || !last;
		el<HTMLButtonElement>('mesh-export').disabled = !!controller || !last;
		el<HTMLButtonElement>('mesh-liquid').disabled = !!controller || !last?.bake.liquidGeometry;
		el<HTMLButtonElement>('mesh-editor-open').disabled = !!controller;
		for (const id of ['mesh-editor-analyze', 'mesh-editor-show', 'mesh-editor-show-all', 'mesh-editor-rebake']) el<HTMLButtonElement>(id).disabled = !!controller || !editorDirectory || !editorMap.value;
		el<HTMLButtonElement>('mesh-editor-capture').disabled = !!controller;
		el<HTMLInputElement>('mesh-resume-export').disabled = !!controller || !editorDirectory;
		el('mesh-bake-map').textContent = el<HTMLInputElement>('mesh-resume-export').checked ? 'Continue current map in open export' : 'Bake current terrain map to folder';
		el('mesh-bake-world').textContent = el<HTMLInputElement>('mesh-resume-export').checked ? 'Continue all terrain maps in open export' : 'Bake all terrain maps to folder';
		el<HTMLButtonElement>('mesh-editor-cancel').disabled = !controller;
		el('mesh-editor-progress').hidden = !controller;
		editorMap.disabled = !!controller || !editorDirectory;
		const p = viewer.meshPosition();
		el('mesh-here').textContent = p ? `Map ${p.mapId}; tile ${meshPositionDetails(p).col}_${meshPositionDetails(p).row}; ${p.x.toFixed(1)}, ${p.y.toFixed(1)}, ${p.z.toFixed(1)}` : 'Move over a map to choose its source tile.';
		updatePosition();
		if (!controller) void updateCameraMesh();
		authoring?.refresh();
	};
	const wireframe = () => el<HTMLSelectElement>('mesh-view').value === 'wireframe';
	const reachabilityView = el<HTMLSelectElement>('mesh-reachability-view');
	const minComponent = el<HTMLInputElement>('mesh-min-component');
	const autoAnalyze = el<HTMLInputElement>('mesh-auto-analyze');
	const bakeWorkers = el<HTMLSelectElement>('mesh-bake-workers');
	bakeWorkers.value = String(defaultBakeConcurrency(navigator.hardwareConcurrency, (navigator as Navigator & { deviceMemory?: number }).deviceMemory));
	const allowPartial = el<HTMLInputElement>('mesh-allow-partial');
	const rebakeNeighbours = el<HTMLInputElement>('mesh-rebake-neighbours');
	const followCamera = el<HTMLInputElement>('mesh-follow-camera');
	const previewRadius = el<HTMLSelectElement>('mesh-preview-radius');
	try {
		const saved = JSON.parse(localStorage.getItem('mesh-options') ?? '{}');
		if ([1, 2, 3, 4, 6, 8].includes(saved.workers)) bakeWorkers.value = String(Math.min(4, saved.workers));
		if (['wireframe', 'solid'].includes(saved.view)) el<HTMLSelectElement>('mesh-view').value = saved.view;
		if (['all', 'hide', 'only'].includes(saved.reachability)) reachabilityView.value = saved.reachability;
		if (Number.isSafeInteger(saved.minComponent) && saved.minComponent >= 0 && saved.minComponent <= 2147483647) minComponent.value = String(saved.minComponent);
		autoAnalyze.checked = saved.version === 2 ? saved.autoAnalyze !== false : true;
		rebakeNeighbours.checked = saved.neighbours !== false; allowPartial.checked = saved.allowPartial === true;
		followCamera.checked = saved.follow !== false;
		if (saved.radius === '0' || saved.radius === '1') previewRadius.value = saved.radius;
	} catch { /* Defaults remain usable if preference storage is unavailable. */ }
	const applyOptions = () => {
		viewer.setMeshPreviewWireframe(wireframe());
		viewer.setMeshReachabilityView(reachabilityView.value as 'all' | 'hide' | 'only');
		if (!followCamera.checked) { followController?.abort(); followController = null; followKey = ''; }
		try { localStorage.setItem('mesh-options', JSON.stringify({ version: 2, workers: Number(bakeWorkers.value), view: el<HTMLSelectElement>('mesh-view').value, reachability: reachabilityView.value, minComponent: Number(minComponent.value), autoAnalyze: autoAnalyze.checked, neighbours: rebakeNeighbours.checked, follow: followCamera.checked, radius: previewRadius.value, allowPartial: allowPartial.checked })); } catch { /* Optional persistence. */ }
	};
	for (const id of ['mesh-bake-workers', 'mesh-view', 'mesh-reachability-view', 'mesh-min-component', 'mesh-auto-analyze', 'mesh-rebake-neighbours', 'mesh-follow-camera', 'mesh-preview-radius', 'mesh-allow-partial']) el(id).addEventListener('change', applyOptions);
	el('mesh-resume-export').addEventListener('change', render);
	applyOptions();
	const action = async (fn: (signal: AbortSignal) => Promise<void>) => {
		if (controller) return;
		followController?.abort(); followController = null; followKey = '';
		controller = new AbortController(); render();
		try { await fn(controller.signal); }
		catch (e) {
			const cancelled = (e as Error).name === 'AbortError';
			status.textContent = cancelled ? 'Cancelled. Completed tile files are retained.' : (e as Error).message;
			editorStatus.textContent = cancelled ? 'Cancelled. Flags already saved remain; rerun analysis to recompute the set.' : status.textContent;
			if (baking) {
				if (!progress.hasAttribute('value')) { progress.max = 1; progress.value = 0; }
				progressLabel.textContent += cancelled ? ' — Cancelled' : ' — Failed';
			}
		}
		finally { baking = false; controller = null; render(); }
	};
	const tileName = (t: MeshTile) => `${String(t.mapId).padStart(4, '0')}_${String(t.y).padStart(2, '0')}_${String(t.x).padStart(2, '0')}.mmtile`;
	const openEditorDirectory = async (directory: FileSystemDirectoryHandle, selectedMap?: number) => {
		const ids: number[] = [];
		for await (const [name, handle] of directory.entries()) if (handle.kind === 'file' && /^\d+\.mmap$/.test(name)) ids.push(Number(name.split('.')[0]));
		if (!ids.length) throw new Error('Select the export folder containing the .mmap and .mmtile files.');
		editorDirectory = directory; folderRevision++; editorMap.replaceChildren();
		await authoring?.openFolder(directory);
		for (const id of ids.sort((a, b) => a - b)) editorMap.add(new Option(`Map ${id}`, String(id)));
		const preferred = selectedMap ?? viewer.meshPosition()?.mapId;
		if (preferred != null && ids.includes(preferred)) editorMap.value = String(preferred);
		editorStatus.textContent = `${directory.name}: ${ids.length} maps. Analysis checks the supplied set against the source tile inventory before marking unreachable polygons.`;
	};
	const showSavedMap = async (directory: FileSystemDirectoryHandle, mapId: number, signal: AbortSignal, clear = true) => {
		wholeMapVisible = true;
		if (clear) viewer.clearMeshPreview();
		let count = 0, triangles = 0;
		const result = await runReachability({ directory, mapId, analyze: false, minComponent: 1000, allowPartial: true, expectedTiles: [], previewAll: true }, signal, text => { editorStatus.textContent = text; }, async (geometry, key) => {
			signal.throwIfAborted(); await viewer.showMeshPreview(geometry, wireframe(), key);
			count++; triangles += geometry.triangles;
			el('mesh-camera-status').textContent = count + ' tiles / ' + triangles.toLocaleString() + ' triangles displayed across map ' + mapId;
			await new Promise(resolve => setTimeout(resolve, 0));
		});
		editorStatus.textContent = count + ' saved tiles displayed across the whole map. ' + (result.analyzed ? result.partial ? 'PARTIAL reachability: pink is relative to supplied tiles only.' : 'Reachability complete; unreachable polygons are pink.' : 'Reachability not classified. Analyse after recovering failed tiles, or explicitly allow partial-set analysis.');
		return result;
	};
	const inspectExport = async (directory: FileSystemDirectoryHandle, mapId: number, analyze: boolean, signal: AbortSignal, expected?: string[], automaticPartial = false) => {
		const threshold = Number(minComponent.value);
		if (!minComponent.value.trim() || !Number.isSafeInteger(threshold) || threshold < 0 || threshold > 2147483647) throw new Error('The separate-area threshold must be a whole number from 0 to 2147483647.');
		let expectedTiles = expected ?? [];
		if (analyze && !expected) {
			try { expectedTiles = (await storage.meshTiles(mapId)).map(tileName); }
			catch { if (!allowPartial.checked) throw new Error('Cannot verify this map inventory. Enable partial-set analysis to analyse only the supplied export.'); }
		}
		signal.throwIfAborted();
		const p = viewer.meshPosition();
		const preview = p?.mapId === mapId ? { row: Math.floor(32 - p.x / TILE_SIZE), col: Math.floor(32 - p.y / TILE_SIZE), radius: Number(previewRadius.value) } : undefined;
		const result = await runReachability({ directory, mapId, analyze, minComponent: threshold, allowPartial: automaticPartial || allowPartial.checked, expectedTiles, preview }, signal, text => { editorStatus.textContent = text; status.textContent = text; if (baking) progressLabel.textContent = text; });
		signal.throwIfAborted();
		if (analyze && last?.metadata.tile.mapId === mapId) last = null; // Never offer an older unflagged download after saving flags.
		if (analyze) await showSavedMap(directory, mapId, signal);
		else if (result.geometry) { wholeMapVisible = false; await viewer.showMeshPreview(result.geometry, wireframe()); }
		const r = result.report;
		editorStatus.textContent = r
			? `${result.partial ? 'PARTIAL TILE SET — relative to supplied tiles only.\n' : ''}${r.Stranded.toLocaleString()} / ${r.Polys.toLocaleString()} polygons unreachable (${r.StrandedPercent.toFixed(2)}%).\n${r.Components.toLocaleString()} components; largest ${r.Largest.toLocaleString()}.\nFlags saved in ${result.savedTiles} tiles. Whole map displayed; unreachable polygons are pink.`
			: result.analyzed ? 'Saved polygon flags displayed for the tile under the camera. Pink indicates flagged unreachable surfaces; viewing does not recompute connectivity.' : 'Tile loaded, but no reachability analysis or stranded flags were found. Run analysis to classify it; unmarked surfaces are not proof of reachability.';
		return result;
	};
	const updateCameraMesh = async () => {
		if (wholeMapVisible || !followCamera.checked || !editorDirectory || controller) return;
		const p = viewer.meshPosition(); if (!p) return;
		const row = Math.floor(32 - p.x / TILE_SIZE), col = Math.floor(32 - p.y / TILE_SIZE), radius = Number(previewRadius.value);
		const key = `${folderRevision}:${p.mapId}:${row}:${col}:${radius}`;
		if (key === followKey) return;
		followController?.abort(); const current = new AbortController(); followController = current; followKey = key;
		if (!Array.from(editorMap.options).some(o => Number(o.value) === p.mapId)) {
			viewer.clearMeshPreview(); el('mesh-camera-status').textContent = `No saved mesh for camera map ${p.mapId}.`; followController = null; return;
		}
		el('mesh-camera-status').textContent = `Loading saved mesh around ${row}_${col}…`;
		try {
			const result = await runReachability({ directory: editorDirectory, mapId: p.mapId, analyze: false, minComponent: 1000, allowPartial: true, expectedTiles: [], preview: { row, col, radius } }, current.signal, () => {});
			if (current.signal.aborted || controller || key !== followKey) return;
			if (result.geometry) await viewer.showMeshPreview(result.geometry, wireframe());
			el('mesh-camera-status').textContent = `${result.geometry?.triangles.toLocaleString()} saved triangles loaded for map ${p.mapId}${result.analyzed ? result.partial ? ' — partial/unverified tile-set flags.' : '.' : ' — reachability not yet classified.'}`;
		} catch (e) {
			if (!current.signal.aborted && !controller) { viewer.clearMeshPreview(); el('mesh-camera-status').textContent = (e as Error).message; }
		} finally { if (followController === current) followController = null; }
	};
	el('mesh-editor-open').addEventListener('click', () => {
		if (controller) return;
		if (!('showDirectoryPicker' in window)) { editorStatus.textContent = 'Mesh folder editing requires Chromium writable-folder access.'; return; }
		const folder = (window as unknown as { showDirectoryPicker(options: object): Promise<FileSystemDirectoryHandle> }).showDirectoryPicker({ id: 'mesh-output', mode: 'readwrite' });
		void action(async signal => { const directory = await folder; signal.throwIfAborted(); await openEditorDirectory(directory); await showSavedMap(directory, Number(editorMap.value), signal); });
	});
	for (const [id, analyze] of [['mesh-editor-analyze', true], ['mesh-editor-show', false]] as const) el(id).addEventListener('click', () => void action(async signal => {
		if (editorDirectory) await inspectExport(editorDirectory, Number(editorMap.value), analyze, signal);
	}));
	el('mesh-editor-show-all').addEventListener('click', () => void action(async signal => { if (editorDirectory) await showSavedMap(editorDirectory, Number(editorMap.value), signal); }));
	el('mesh-editor-cancel').addEventListener('click', () => controller?.abort());
	const selected = async (): Promise<MeshTile> => {
		const p = viewer.meshPosition();
		if (!p) throw new Error('Move over a map first.');
		const x = Math.floor(32 - p.y / TILE_SIZE), y = Math.floor(32 - p.x / TILE_SIZE);
		const tile = (await storage.meshTiles(p.mapId)).find((t) => t.x === x && t.y === y);
		if (!tile) throw new Error(`No source terrain tile at ${x}_${y} in native map ${p.mapId}.`);
		return tile;
	};
	const source = async (tile: MeshTile, signal: AbortSignal, preview = false): Promise<MeshInput> => {
		signal.throwIfAborted();
		status.textContent = `Reading collision for map ${tile.mapId}, tile ${tile.x}_${tile.y}, including neighbouring edges…`;
		const input = await storage.meshSource(tile, preview);
		authoring?.attach(input);
		signal.throwIfAborted();
		return input;
	};
	const prepare = serialLane();
	const recoverOneAtATime = serialLane();
	authoring=setUpAuthoring(viewer,storage,product,{busy:()=>!!controller,directory:()=>editorDirectory,changed:()=>{last=null;}});
	const bake = async (tile: MeshTile, signal: AbortSignal, lowMemory = false) => {
		await authoring!.flush();
		let sourceMs = 0;
		const input = await prepare(async () => { signal.throwIfAborted(); const start = performance.now(); const result = await source(tile, signal); sourceMs = performance.now() - start; return result; });
		const metadata = tileMetadata(input, product);
		metadata.lowMemoryRecovery = lowMemory;
		status.textContent = input.raw ? `Extracting and baking ${input.raw.files.length} source files…` : `Baking ${input.indices.length / 3} source triangles in a background worker…`;
		const bakeStart = performance.now();
		const execute = () => {
			signal.throwIfAborted();
			return runBake(input, signal, (stage) => { status.textContent = `Map ${tile.mapId}, tile ${tile.x}_${tile.y}: ${stage}…`; }, lowMemory);
		};
		const result = await (lowMemory ? recoverOneAtATime(execute) : execute());
		signal.throwIfAborted();
		metadata.quality = result.quality;
		metadata.pipeline = result.quality.pipeline;
		metadata.settings = result.settings ?? null;
		metadata.executionBackend = result.executionBackend ?? 'wasm';
		metadata.files = Object.fromEntries(Object.entries(result.files).map(([name, bytes]) => [name, bytes.length]));
		const baked = { bake: result, metadata, timing: { sourceMs, bakeMs: performance.now() - bakeStart } };
		metadata.performance = baked.timing;
		last = baked;
		return baked;
	};
	el('mesh-edit-rebake').addEventListener('click',()=>void action(async signal=>{
		if(!editorDirectory||!editorMap.value)throw Error('Open a mesh export folder first.');
		await authoring!.flush();
		const mapId=Number(editorMap.value),inventory=await storage.meshTiles(mapId);
		const update=await authoring!.changedTiles(inventory,editorDirectory);
		if(!update.length){editorStatus.textContent='No tiles differ from their saved authoring state.';return;}
		// Validate every replacement before starting; do not mix client builds.
		for(const tile of update){try{const old=JSON.parse(await(await(await editorDirectory.getFileHandle(`${mapId}-${tile.x}-${tile.y}.json`)).getFile()).text());if((old.product&&old.product!==product)||(old.sourceBuild&&JSON.stringify(old.sourceBuild)!==JSON.stringify(buildInfo)))throw Error('This export belongs to different game data.');}catch(e){if(!['NotFoundError','SyntaxError'].includes((e as Error).name))throw e;}}
		discovering('Rebaking edited tiles and dependent borders…');let done=0;
		await authoring!.saveTo(editorDirectory);
		for(const tile of update){signal.throwIfAborted();tileProgress(done,update.length,`Updating ${tile.x}_${tile.y}`);const result=await bake(tile,signal,true);await saveTileExport(editorDirectory,tile,result.bake,result.metadata);wholeMapVisible=true;await viewer.showMeshPreview(result.bake.geometry,wireframe(),tileName(tile));done++;}
		folderRevision++;followKey='';
		if(autoAnalyze.checked)await inspectExport(editorDirectory,mapId,true,signal,inventory.map(tileName));
		tileProgress(done,update.length,'Edited tiles updated');editorStatus.textContent=`${done} edited/dependent tiles rebaked.${autoAnalyze.checked?' Reachability recomputed.':' Run reachability analysis to update automatic flags.'}`;
	}));
	el('mesh-editor-rebake').addEventListener('click', () => void action(async signal => {
		if (!editorDirectory) return;
		const tile = await selected();
		if (tile.mapId !== Number(editorMap.value)) throw new Error('Move the camera over the selected export map before rebaking.');
		try {
			const old = JSON.parse(await (await (await editorDirectory.getFileHandle(`${tile.mapId}-${tile.x}-${tile.y}.json`)).getFile()).text());
			if ((old.product && old.product !== product) || (old.sourceBuild && JSON.stringify(old.sourceBuild) !== JSON.stringify(buildInfo))) throw new Error('This export belongs to different game data. Rebuild the continent with the current client instead of mixing a single tile into it.');
		} catch (e) { if (!['NotFoundError', 'SyntaxError'].includes((e as Error).name)) throw e; }
		const inventory = await storage.meshTiles(tile.mapId);
		const update = inventory.filter(t => rebakeNeighbours.checked ? Math.abs(t.x - tile.x) <= 1 && Math.abs(t.y - tile.y) <= 1 : t.x === tile.x && t.y === tile.y);
		let done = 0; discovering('Updating selected tile and its dependent border tiles…');
		for (const t of update) {
			signal.throwIfAborted(); tileProgress(done, update.length, `Updating ${t.x}_${t.y}`);
			const result = await bake(t, signal, true);
			await saveTileExport(editorDirectory, t, result.bake, result.metadata);
			wholeMapVisible = true; await viewer.showMeshPreview(result.bake.geometry, wireframe(), tileName(t));
			done++; tileProgress(done, update.length, 'Updated');
		}
		if (autoAnalyze.checked) {
			discovering('Recomputing reachability against the assembled map…');
			await inspectExport(editorDirectory, tile.mapId, true, signal, inventory.map(tileName));
			tileProgress(done, update.length, 'Updated and map reachability recomputed');
		} else {
			await showSavedMap(editorDirectory, tile.mapId, signal);
			editorStatus.textContent = `${done} tiles updated. Previous reachability is stale; run analysis to recompute flags against the whole map.`;
			status.textContent = editorStatus.textContent;
		}
	}));
	el('mesh-editor-capture').addEventListener('click', () => void action(async signal => {
		// Source capture also works with a single unsaved preview. Pick its
		// destination while the click still supplies browser user activation.
		if (!editorDirectory && !('showDirectoryPicker' in window)) throw Error('Source capture requires Chromium writable-folder access.');
		const destination = editorDirectory ?? await (window as unknown as { showDirectoryPicker(options: object): Promise<FileSystemDirectoryHandle> }).showDirectoryPicker({ id: 'mesh-diagnostics', mode: 'readwrite' });
		const tile = await selected(), camera = viewer.meshPosition(), view = viewer.shareLink();
		discovering('Collecting the original tile collision source...');
		const input = await source(tile, signal);
		const directory = await destination.getDirectoryHandle('diagnostics', { create: true });
		const name = `${tile.mapId}-${tile.x}-${tile.y}`;
		signal.throwIfAborted();
		status.textContent = 'Saving the exact original tile source for collision/mesh comparison...';
		const bytes = encodeBakeInput(input);
		await writeExport(directory, `${name}.input`, bytes);
		const capturedMesh = !!last && last.metadata.tile.mapId === tile.mapId && last.metadata.tile.x === tile.x && last.metadata.tile.y === tile.y;
		if (capturedMesh && last) {
			const meshDirectory = await directory.getDirectoryHandle(`${name}-baked`, { create: true });
			await saveTileExport(meshDirectory, tile, last.bake, last.metadata);
		}
		await writeExport(directory, `${name}.json`, JSON.stringify({ tile, camera, view, sourceBuild: buildInfo, bytes: bytes.length, capturedMesh }, null, 2));
		editorStatus.textContent = status.textContent = `Captured ${destination.name}/diagnostics/${name}.input (${(bytes.length / 1048576).toFixed(1)} MB).${capturedMesh ? ' Current baked tile copied alongside the source.' : ''} Saved mesh was not changed; no bake was run.`;
	}));
	el('mesh-collision').addEventListener('click', () => void action(async (signal) => {
		wholeMapVisible = false; followCamera.checked = false; applyOptions();
		const input = await source(await selected(), signal, true);
		await viewer.showMeshPreview(previewGeometry(input), wireframe());
		status.textContent = `${input.indices.length / 3} collision triangles shown. ${input.liquidLayers} terrain liquid layers detected.`;
	}));
	el('mesh-bake-area').addEventListener('click', () => void action(async (signal) => {
		followCamera.checked = false; applyOptions();
		discovering('Preparing tile — progress updates when baking finishes');
		const result = await bake(await selected(), signal);
		await viewer.showMeshPreview(result.bake.geometry, wireframe());
		tileProgress(1, 1, 'Complete');
		status.textContent = `${result.bake.geometry.triangles.toLocaleString()} ground triangles baked in ${(result.bake.milliseconds / 1000).toFixed(1)} seconds.`;
		const q = result.bake.quality;
		el('mesh-output').textContent = `${q.polygons.toLocaleString()} polygons; ${q.indoorCells} indoor cells; ${q.railingCells} railing cells removed; ${q.swimPolygons} swim polygons; ${q.hazardPolygons} under hazards; ${q.detailSpikes} spikes fixed; ${q.detailClamped} heights clamped. ${q.fallbacks.join('; ')}`;
	}));
	el('mesh-cancel').addEventListener('click', () => {
		controller?.abort();
		status.textContent = 'Cancelling. An in-progress storage read must finish; the bake worker stops immediately.';
	});
	el('mesh-navmesh').addEventListener('click', () => void action(async () => {
		followCamera.checked = false; applyOptions();
		if (last) await viewer.showMeshPreview(last.bake.geometry, wireframe());
	}));
	el('mesh-liquid').addEventListener('click', () => void action(async () => {
		followCamera.checked = false; applyOptions();
		if (last?.bake.liquidGeometry) await viewer.showMeshPreview(last.bake.liquidGeometry, wireframe());
	}));
	el('mesh-clear').addEventListener('click', () => { followCamera.checked = false; applyOptions(); viewer.clearMeshPreview(); });
	el('mesh-export').addEventListener('click', () => {
		if (!last) return;
		const files: Record<string, Uint8Array> = { ...last.bake.files, 'project.json': strToU8(JSON.stringify(last.metadata, null, 2)) };
		const zip = zipSync(files, { level: 0 });
		const url = URL.createObjectURL(new Blob([zip]));
		const link = document.createElement('a');
		const t = last.metadata.tile;
		link.href = url; link.download = `mesh-${t.mapId}-${t.x}-${t.y}.zip`; link.click();
		setTimeout(() => URL.revokeObjectURL(url), 1000);
	});
	for (const scope of ['map', 'world']) el(`mesh-bake-${scope}`).addEventListener('click', () => {
		if (controller) return;
		if (!('showDirectoryPicker' in window)) { status.textContent = 'Map/world jobs need a Chromium browser with writable folder access. Single-tile download remains available.'; return; }
		// Request the folder directly from the click, before asynchronous source reads consume user activation.
		const resume = el<HTMLInputElement>('mesh-resume-export').checked;
		if (resume && !editorDirectory) { status.textContent = 'Open the export folder in Mesh Editor first.'; return; }
		const folder = resume ? Promise.resolve(editorDirectory!) : (window as unknown as { showDirectoryPicker(options: object): Promise<FileSystemDirectoryHandle> }).showDirectoryPicker({ id: 'mesh-output', mode: 'readwrite' });
		const p = viewer.meshPosition();
		void action(async (signal) => {
			discovering('Discovering maps and tiles…');
			const root = await folder;
			signal.throwIfAborted();
			if (!p) throw new Error('Move over a map first.');
			const dir = resume ? root : await root.getDirectoryHandle(`ExplorerMesh-${Date.now()}`, { create: true });
			const concurrency = Math.min(4, Number(bakeWorkers.value));
			let knownHeavy = new Set<string>();
			if (resume) {
				try { knownHeavy = priorMemoryFailures(JSON.parse(await (await (await dir.getFileHandle('job.json')).getFile()).text()), buildInfo); }
				catch (e) { if (!['NotFoundError', 'SyntaxError'].includes((e as Error).name)) throw e; }
			}
			wholeMapVisible = true; if (!resume) viewer.clearMeshPreview();
			const started = performance.now(), persist = serialLane();
			const report = { concurrency, elapsedMs: 0, sourceMs: 0, bakeMs: 0, exportMs: 0, format: 'map-explorer-mappster', product, sourceBuild: buildInfo, state: 'running', completed: 0, written: 0, reused: 0, attempted: 0, total: 0, failedTiles: 0, reachability: [] as { mapId: number; state: string; partial?: boolean }[], memoryRetries: [] as { tile: MeshTile; originalError: string; recovered: boolean }[], failures: [] as string[] };
			const write = (name: string, data: Uint8Array | string) => writeExport(dir, name, data);
			await authoring!.flush();await authoring!.saveTo(dir);
			const showBatchProgress = (detail: string) => {
				const minutes = (performance.now() - started) / 60000;
				const rate = report.written && minutes > 0 ? (report.written / minutes).toFixed(1) + ' tiles/min; ' : '';
				tileProgress(report.attempted, report.total, concurrency + ' bake lanes; ' + rate + detail);
			};
			try {
				const maps = scope === 'map' ? [{ id: p.mapId }] : await storage.listMaps();
				const jobs: { id: number; tiles: MeshTile[] }[] = [];
				for (const map of maps) {
					signal.throwIfAborted();
					let tiles: MeshTile[];
					try { tiles = await storage.meshTiles(map.id); }
					catch (e) { report.failures.push(`Map ${map.id}: ${(e as Error).message}`); continue; }
					jobs.push({ id: map.id, tiles }); report.total += tiles.length;
					progressLabel.textContent = `Discovering tiles: ${jobs.length} maps, ${report.total.toLocaleString()} tiles found…`;
				}
				tileProgress(0, report.total, 'Ready to bake');
				await write('job.json', JSON.stringify(report, null, 2));
				for (const map of jobs) {
					const failuresBeforeMap = report.failedTiles;
					const retry: { tile: MeshTile; error: string }[] = [];
					let recovering = false;
					const runTile = async (tile: MeshTile) => {
						signal.throwIfAborted();
						if (resume && await tileExportCurrent(dir, tile, product, buildInfo,authoring!.fingerprint(tile))) {
							report.completed++; report.attempted++; report.reused++;
							showBatchProgress(`${report.completed} ready (${report.reused} reused), ${report.failedTiles} failed`);
							return;
						}
						showBatchProgress(`Map ${map.id}, tile ${tile.x}_${tile.y}; ${report.completed} ready, ${report.failedTiles} failed`);
						let result: Awaited<ReturnType<typeof bake>>;
						const nativeFirst = knownHeavy.has(`${tile.mapId}:${tile.x}:${tile.y}`);
						try { result = await bake(tile, signal, recovering || nativeFirst); }
						catch (e) {
							signal.throwIfAborted();
							const message = `Map ${map.id}, ${tile.x}_${tile.y}: ${(e as Error).message}`;
							report.failures.push(message);
							if (!recovering && !nativeFirst && /OutOfMemory|exit\(1\)/i.test(message)) retry.push({ tile, error: message });
							report.attempted++; report.failedTiles++;
							showBatchProgress(`${report.completed} ready, ${report.failedTiles} failed`);
							await persist(() => write('job.json', JSON.stringify(report, null, 2))); return;
						}
						// Never race shared mmap, completion markers or job.json writes.
						await persist(async () => {
							signal.throwIfAborted();
							const saveStart = performance.now();
							await saveTileExport(dir, tile, result.bake, result.metadata);
							try { await viewer.showMeshPreview(result.bake.geometry, wireframe(), tileName(tile)); }
							catch (e) { el('mesh-camera-status').textContent = 'Tile saved; display failed: ' + (e as Error).message; }
							report.exportMs += performance.now() - saveStart;
							report.sourceMs += result.timing.sourceMs; report.bakeMs += result.timing.bakeMs;
							report.completed++; report.written++; report.attempted++;
							report.elapsedMs = performance.now() - started;
							showBatchProgress(report.reused + ' reused; ' + report.failedTiles + ' failed');
							await write('job.json', JSON.stringify(report, null, 2));
							el('mesh-output').textContent = report.completed + ' tiles ready. ' + dir.name;
						});
					};
					await parallelTiles(map.tiles, concurrency, signal, runTile);
					recovering = true;
					for (const failed of retry) {
						signal.throwIfAborted();
						status.textContent = 'Retrying failed tile in isolation: ' + failed.tile.x + '_' + failed.tile.y;
						report.failedTiles--; report.attempted--;
						report.failures.splice(report.failures.indexOf(failed.error), 1);
						const before = report.failedTiles;
						await runTile(failed.tile);
						report.memoryRetries.push({ tile: failed.tile, originalError: failed.error, recovered: report.failedTiles === before });
					}
					await openEditorDirectory(dir, map.id);
					if (resume) await showSavedMap(dir, map.id, signal, false);
					if (autoAnalyze.checked) {
						try {
							discovering('Analysing available tiles for map ' + map.id + (report.failedTiles !== failuresBeforeMap ? ' (PARTIAL: failed tiles missing)' : ''));
							const result = await inspectExport(dir, map.id, true, signal, map.tiles.map(tileName), true);
							report.reachability.push({ mapId: map.id, state: 'complete', partial: result.partial });
						} catch (e) { signal.throwIfAborted(); report.reachability.push({ mapId: map.id, state: 'failed' }); report.failures.push('Map ' + map.id + ' reachability: ' + (e as Error).message); }
					} else report.reachability.push({ mapId: map.id, state: 'disabled' });
				}
				report.state = report.failedTiles ? 'incomplete' : report.failures.length ? 'complete-with-issues' : 'complete';
				tileProgress(report.attempted, report.total, report.failures.length ? `Finished with ${report.failures.length} issues; see job.json` : 'Complete');
				status.textContent = `Bake finished: ${report.completed} tiles ready (${report.written} written, ${report.reused} reused), ${report.failedTiles} failed tiles. ${report.failedTiles ? 'Incomplete map; check Mesh Editor for reachability status. ' : ''}See job.json for details.`;
			} catch (e) { report.state = signal.aborted ? 'cancelled' : 'failed'; throw e; }
			finally { report.elapsedMs = performance.now() - started; await write('job.json', JSON.stringify(report, null, 2)); }
		});
	});
	const timer = setInterval(render, 1000);
	const positionTimer = setInterval(updatePosition, 250);
	window.addEventListener('pagehide', () => { controller?.abort(); followController?.abort(); clearInterval(timer); clearInterval(positionTimer); }, { once: true });
	render();
}
