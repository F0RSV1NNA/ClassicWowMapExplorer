import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import { unzipSync, zipSync, strFromU8 } from 'fflate';
import { analyzeStream, type PageStore } from '../src/mesh/stream-analysis';
import { streamBackend, meshTile, liquidTile } from '../src/mesh/stream-backend';
import { encodeBakeInput } from '../src/mesh/engine-api';

const dir = '.cache/mesh-stream'; await mkdir(dir, { recursive: true });
const moduleUrl = pathToFileURL(resolve('public/mesh-engine/_framework/dotnet.js')).href;
const { dotnet } = await import(moduleUrl);
const runtime = await dotnet.create(), api = (await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)).MeshEngine;
const p: number[] = [], indices: number[] = [];
const quad = (x0: number, z0: number, x1: number, z1: number, y: number) => { const a = p.length / 3; p.push(x0,y,z0,x1,y,z0,x1,y,z1,x0,y,z1); indices.push(a,a+2,a+1,a,a+3,a+2); };
quad(-600,-100,-480,-20,5); quad(-700,-160,-692,-152,5);
const combined: Record<string, Uint8Array> = {};
for (const x of [32,33]) {
	const input = encodeBakeInput({ tile: { mapId: 0, wdt: 1, x, y: 32 }, origin: [0,0,0], liquidLayers: 1,
		positions: new Float32Array(p), indices: new Uint32Array(indices), classes: new Uint8Array(indices.length/3),
		liquids: { positions: new Float32Array([-580,8,-90,-500,8,-90,-500,8,-40,-580,8,-40]), indices: new Uint32Array([0,2,1,0,3,2]), kinds: new Uint8Array([1,1]) } });
	for (const [name, bytes] of Object.entries(unzipSync(api.Bake(input)))) if (/\.(mmap|mmtile|mliq)$/.test(name)) combined[name] = bytes;
}
await writeFile(`${dir}/two-tiles.zip`, zipSync(Object.fromEntries(Object.entries(combined).sort(([a],[b]) => a.localeCompare(b)))));
const native = spawnSync('dotnet', ['run','--project','tools/mesh-native-check/check.csproj','-c','Release','--',`${dir}/two-tiles.zip`,`${dir}/reference.zip`,'reachability-archive'], { stdio: ['ignore', 'pipe', 'pipe'] });
if (native.status !== 0) throw Error(native.stdout.toString() + native.stderr.toString());
for (const [label, sourcePath, expectedPath] of [
	['scrap-and-submerged-floor', '.cache/mesh-reachability/native.unflagged.zip', '.cache/mesh-reachability/native.zip'],
	['two-tile-road-and-water-seam', `${dir}/two-tiles.zip`, `${dir}/reference.zip`],
]) {
	const files = unzipSync(await readFile(sourcePath)), expected = unzipSync(await readFile(expectedPath));
	const names = Object.keys(files).filter(n => /\.(mmap|mmtile|mliq)$/.test(n)).sort();
	const tiles = names.filter(n => n.endsWith('.mmtile')).map(meshTile), liquids = names.filter(n => n.endsWith('.mliq')).map(n => liquidTile(n, files[n]));
	const pages = new Map<string, Uint8Array>();
	const store: PageStore = { async put(k,b) { pages.set(k,b.slice()); }, async get(k) { return pages.get(k)!.slice(); } };
	const backend = streamBackend(api, 0, tiles, liquids, async n => files[n].slice(), async (n,b) => { files[n] = b.slice(); });
	const { report, stats } = await analyzeStream(tiles, liquids, backend, store, 1000, () => {}, 256);
	const reference = JSON.parse(strFromU8(expected['report.json']));
	if (Object.entries(reference).some(([key,value]) => (report as unknown as Record<string, unknown>)[key] !== value)) throw Error(`${label}: verdict differs: ${JSON.stringify({report,reference})}`);
	for (const name of names) if (!Buffer.from(files[name]).equals(Buffer.from(expected[name]))) throw Error(`${label}: ${name} differs from original NavPrune`);
	console.log(label, 'MATCHES original native NavPrune', report);
	await writeFile(`${dir}/${label}.json`, JSON.stringify({ exit: 0, report, stats }, null, 2));
}
api.EndAnalysis();
