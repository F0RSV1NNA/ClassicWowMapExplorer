import { createServer } from 'node:http';
import { readFile, readdir, mkdir, writeFile } from 'node:fs/promises';
import { resolve, extname } from 'node:path';
import { spawn } from 'node:child_process';
import { unzipSync } from 'fflate';
import { createHash } from 'node:crypto';

// A single real-browser worker check, without opening game data or the Explorer world view.
const assets = await readdir('dist/assets');
const lowMemory = process.argv.includes('--low-memory');
const expectedFile = process.argv.find(a => a.startsWith('--expected='))?.slice('--expected='.length);
const inputFile = process.argv.find(a => a.startsWith('--input='))?.slice('--input='.length) ?? '.cache/mesh-parity/source.input';
const scriptUrl = process.argv.find(a => a.startsWith('--script='))?.slice('--script='.length);
const partial = process.argv.includes('--partial');
const overlay = process.argv.includes('--overlay');
const wholeMap = process.argv.includes('--whole-map');
const analysis = wholeMap || process.argv.includes('--reachability');
const parallel = Math.max(1, Math.min(8, Number(process.argv.find(a => a.startsWith('--parallel='))?.split('=')[1] ?? 1)));
if (!Number.isInteger(parallel)) throw Error('Invalid parallel worker count');
const evidenceDir = analysis ? '.cache/mesh-reachability' : '.cache/mesh-parity';
const fixture = analysis ? unzipSync(await readFile(wholeMap ? '.cache/mesh-stream/two-tiles.zip' : `${evidenceDir}/native.unflagged.zip`)) : {};
const wholeExpected = wholeMap ? unzipSync(await readFile('.cache/mesh-stream/reference.zip')) : undefined;
const fixtureNames = Object.keys(fixture).filter(n => /\.(mmap|mmtile|mliq)$/.test(n));
const worker = assets.find((f) => (analysis ? /^reachability\.worker-.*\.js$/ : /^bake\.worker-.*\.js$/).test(f));
if (!worker) throw Error('Build Explorer first');
const server = createServer(async (req, res) => {
	try {
		if (req.url === '/') { res.setHeader('Content-Type', 'text/html'); res.end('<title>Mesh worker check</title>'); return; }
		if (req.url === '/overlay-check.js') { res.setHeader('Content-Type', 'text/javascript'); res.end(await readFile('.cache/mesh-overlay-check.js')); return; }
		if (req.url === '/analysis-list') { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(fixtureNames)); return; }
		if (req.url.startsWith('/analysis-file/')) { const name = req.url.slice(15); if (!fixtureNames.includes(name)) throw Error('Unknown fixture file'); res.end(fixture[name]); return; }
		const path = req.url === '/fixture' ? resolve(inputFile) : resolve('dist', '.' + decodeURI(req.url));
		if (!path.startsWith(resolve('dist') + '\\') && req.url !== '/fixture') throw Error('Invalid path');
		res.setHeader('Content-Type', ({ '.js': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm' })[extname(path)] ?? 'application/octet-stream');
		res.end(await readFile(path));
	} catch { res.statusCode = 404; res.end(); }
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const url = process.argv.find(arg => arg.startsWith('--server='))?.slice(9) ?? `http://127.0.0.1:${server.address().port}`;
const profile = resolve(`.cache/mesh-browser-profile-${Date.now()}`); await mkdir(profile, { recursive: true });
const edge = spawn('C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', ['--headless', ...(overlay ? ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] : ['--disable-gpu']), '--no-first-run', '--remote-debugging-port=0', `--user-data-dir=${profile}`, url], { windowsHide: true, stdio: 'ignore' });
let ws;
try {
	let port;
	for (let i = 0; i < 100; i++) {
		try { port = Number((await readFile(`${profile}/DevToolsActivePort`, 'utf8')).split('\n')[0]); break; } catch { await new Promise((r) => setTimeout(r, 100)); }
	}
	if (!port) throw Error('Headless Edge did not start');
	const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
	const page = targets.find((t) => t.type === 'page');
	ws = new WebSocket(page.webSocketDebuggerUrl);
	await new Promise((r, reject) => { ws.onopen = r; ws.onerror = reject; });
	let next = 1; const pending = new Map();
	ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id) { pending.get(m.id)?.(m); pending.delete(m.id); }
		else if (m.method === 'Runtime.consoleAPICalled') console.log('BROWSER', m.params.args.map((a) => a.value ?? a.description).join(' '));
		else if (m.method === 'Runtime.exceptionThrown') console.log('EXCEPTION', JSON.stringify(m.params));
		else if (m.method === 'Target.attachedToTarget') { ws.send(JSON.stringify({ id: next++, sessionId: m.params.sessionId, method: 'Runtime.enable', params: {} })); }
	};
	const call = (method, params) => new Promise((r) => { const id = next++; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });
	await call('Runtime.enable', {});
	await call('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: false, flatten: true });
	await call('Page.navigate', { url });
	for (let i = 0; i < 50; i++) {
		const state = await call('Runtime.evaluate', { expression: 'location.href + ":" + document.readyState', returnByValue: true });
		if (state.result?.result?.value === url + '/:complete') break;
		await new Promise((r) => setTimeout(r, 100));
	}
	const expression = `(async () => {
		if (${JSON.stringify(scriptUrl??'')}) return (await import(${JSON.stringify(scriptUrl??'')})).check();
		if (${overlay}) return (await import('/overlay-check.js')).checkOverlay();
		if (${analysis}) {
			const names = await (await fetch('/analysis-list')).json();
			const root = await navigator.storage.getDirectory();
			const directory = await root.getDirectoryHandle('reachability-fixture-' + Date.now(), { create: true });
			for (const name of names) { const h = await directory.getFileHandle(name, { create: true }); const w = await h.createWritable(); await w.write(await (await fetch('/analysis-file/' + name)).arrayBuffer()); await w.close(); }
			let streamedTiles = 0, streamedTriangles = 0, streamedPink = 0;
			const worker = new Worker('/assets/${worker}', { type: 'module' });
			try { return await new Promise((resolve, reject) => {
				const timeout = setTimeout(() => reject(Error('Analysis worker timed out')), 60000);
				worker.onerror = e => { clearTimeout(timeout); reject(Error(e.message)); };
				worker.onmessage = async e => {
					if (e.data.tile) { const t = e.data.tile; streamedTiles++; streamedTriangles += t.geometry.triangles; streamedPink += t.geometry.unreachable?.filter(Boolean).length ?? 0; worker.postMessage({ previewAck: t.key }); return; }
					if (e.data.scratch) return;
					if (e.data.progress) { console.log(e.data.progress); return; }
					clearTimeout(timeout);
					if (e.data.error) { reject(Error(e.data.error)); return; }
					const r = e.data.result, hashes = {};
					for (const name of names) { const bytes = await (await (await directory.getFileHandle(name)).getFile()).arrayBuffer(); hashes[name] = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), n => n.toString(16).padStart(2, '0')).join(''); }
					resolve({ polygons: r.report.Polys, triangles: r.geometry?.triangles ?? streamedTriangles, streamedTiles, partial: r.partial, stranded: r.report.Stranded, pinkTriangles: r.geometry?.unreachable.filter(Boolean).length ?? streamedPink, savedTiles: r.savedTiles, hashes });
				};
				worker.postMessage({ request: { directory, mapId: 0, analyze: true, minComponent: 1000, allowPartial: ${partial}, expectedTiles: names.filter(n => n.endsWith('.mmtile')).concat(${partial} ? ['0000_00_00.mmtile'] : []), previewAll: ${wholeMap}, preview: { row: 32, col: 32 } }, runtimeUrl: location.origin + '/mesh-engine/_framework/dotnet.js' });
			}); } finally { worker.terminate(); }
		}
		const data = new DataView(await (await fetch('/fixture')).arrayBuffer()); let at = 4;
		const int = () => { const n = data.getInt32(at, true); at += 4; return n; };
		const mapId = int(), x = int(), y = int(); let origin=[0,0,0],positions=new Float32Array(),indices=new Uint32Array(),classes=new Uint8Array(),liquids,raw;
		if(data.getUint32(0,true)===0x31424d45){
			origin=Array.from({length:3},()=>{const n=data.getFloat32(at,true);at+=4;return n;});
			const geometry=()=>{const nv=int(),ni=int();const p=new Float32Array(nv*3),i=new Uint32Array(ni);for(let n=0;n<p.length;n++,at+=4)p[n]=data.getFloat32(at,true);for(let n=0;n<i.length;n++)i[n]=int();const k=new Uint8Array(data.buffer.slice(at,at+ni/3));at+=ni/3;return {p,i,k};};
			const g=geometry(),l=geometry();positions=g.p;indices=g.i;classes=g.k;liquids={positions:l.p,indices:l.i,kinds:l.k};
		}else{
		const count = int(), files = [];
		for (let i = 0; i < count; i++) { const id = int(), size = int(); const bytes = new Uint8Array(data.buffer.slice(at, at + size)); at += size; files.push({ id, bytes }); }
		const liquidBanks = []; const banks = int(); for (let i = 0; i < banks; i++) { liquidBanks.push([data.getUint16(at, true), data.getUint8(at + 2)]); at += 3; }
		const tiles = []; const n = int(); for (let i = 0; i < n; i++) tiles.push({ x: int(), y: int(), root: int(), obj0: int(), tex0: int() });
		raw={files,tiles,liquidBanks};}
		let edits;if(at<data.byteLength){const length=int();edits=JSON.parse(new TextDecoder().decode(new Uint8Array(data.buffer,at,length)));}
		const run = async () => {
		const worker = new Worker('/assets/${worker}', { type: 'module' });
		try { return await new Promise((resolve, reject) => {
			const timeout = setTimeout(() => reject(Error('Worker timed out')), 60000);
			worker.onerror = e => { clearTimeout(timeout); reject(Error(e.message)); };
			worker.onmessage = async e => {
				if (e.data.progress) { console.log(e.data.progress); return; }
				clearTimeout(timeout);
				if (e.data.error) { reject(Error(e.data.error)); return; }
				const r = e.data.result;
				const hashes = {};
				for (const [name, bytes] of Object.entries(r.files)) hashes[name] = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), n => n.toString(16).padStart(2, '0')).join('');
				resolve({ executionBackend: r.executionBackend ?? 'wasm', pipeline: r.quality.pipeline, polygons: r.quality.polygons, triangles: r.geometry.triangles, hashes });
			};
			worker.postMessage({ input: { tile: { mapId, wdt: 1, x, y }, origin, liquidLayers: 1, positions,indices,classes,liquids,raw,edits }, lowMemory: ${lowMemory}, runtimeUrl: location.origin + '/mesh-engine/_framework/dotnet.js' });
		}); } finally { worker.terminate(); }
		};
		const start = performance.now();
		const workers = await Promise.all(Array.from({ length: ${parallel} }, run));
		return { ...workers[0], workers, parallel: ${parallel}, lowMemory: ${lowMemory}, elapsedMs: performance.now() - start };
	})()`;
	const answer = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
	if (answer.error || answer.result.exceptionDetails) throw Error(JSON.stringify(answer));
	const result = answer.result.result.value;
	if(scriptUrl){console.log(JSON.stringify({exit:0,...result},null,2));}
	else if (overlay) { if (!result.allTrianglesPreserved) throw Error('Overlay check failed'); await writeFile('.cache/mesh-parity/overlay.json', JSON.stringify({ exit: 0, ...result }, null, 2)); console.log(JSON.stringify(result, null, 2)); } else {
	const expected = wholeMap ? { report: JSON.parse(new TextDecoder().decode(wholeExpected['report.json'])), hashes: Object.fromEntries(Object.entries(wholeExpected).filter(([n]) => /\.(mmap|mmtile|mliq)$/.test(n)).map(([n,b]) => [n, createHash('sha256').update(b).digest('hex')])) } : JSON.parse(await readFile(expectedFile ?? `${evidenceDir}/evidence.json`, 'utf8'));
	if (process.argv.some(arg => arg.startsWith('--server=')) && lowMemory && result.executionBackend !== 'native-x64') throw Error('Expected native x64 recovery');
	if (analysis && result.partial !== partial) throw Error('Partial-set classification was lost');
	if (wholeMap && result.streamedTiles !== 2) throw Error('Whole-map preview did not stream both tiles');
	for (const checked of result.workers ?? [result]) for (const [name, hash] of Object.entries(checked.hashes)) if (expected.hashes[name] !== hash) throw Error(`Browser output mismatch: ${name}`);
	if (!result.polygons || !result.triangles) throw Error('Browser returned empty geometry');
	if (analysis && (result.stranded !== expected.report.Stranded || !result.pinkTriangles || result.savedTiles !== 1)) throw Error('Browser did not save and display the expected reachability flags');
	const workerHash = createHash('sha256').update(await readFile(`dist/assets/${worker}`)).digest('hex');
	await writeFile(`${evidenceDir}/${wholeMap ? partial ? 'browser-partial-map' : 'browser-whole-map' : 'browser'}.json`, JSON.stringify({ exit: 0, worker, workerHash, ...result }, null, 2));
	console.log(JSON.stringify(result, null, 2));
	}
} finally { ws?.close(); edge.kill(); server.closeAllConnections(); await new Promise((r) => server.close(r)); }
