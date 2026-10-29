import { mkdir, readFile, writeFile, readdir } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { analyzeStream, type PageStore } from '../src/mesh/stream-analysis';
import { streamBackend, meshTile, liquidTile } from '../src/mesh/stream-backend';

// Purpose-built memory/coverage check: 16 separate underwater floor patches in one
// connected body, with >4.5 million wet cells. Scratch pages are real disk files.
const root = '.cache/mesh-stream-scale', source = `${root}/source`, scratch = `${root}/scratch`;
await mkdir(scratch, { recursive: true });
const generated = spawnSync('dotnet', ['run','--project','tools/mesh-native-check/check.csproj','-c','Release','--','water-stress',source], { stdio: ['ignore','pipe','pipe'] });
if (generated.status !== 0) throw Error(generated.stdout.toString() + generated.stderr.toString());
const names = (await readdir(source)).sort(), read = async (n: string) => new Uint8Array(await readFile(`${source}/${n}`));
const tiles = names.filter(n => n.endsWith('.mmtile')).map(meshTile), liquids = [];
let wetCells = 0;
for (const name of names.filter(n => n.endsWith('.mliq'))) {
	const bytes = await read(name); wetCells += new DataView(bytes.buffer, bytes.byteOffset).getInt32(35, true);
	liquids.push(liquidTile(name, bytes));
}
if (wetCells <= 3_000_000) throw Error('Fixture did not exceed the old wet-cell limit');
const runtimeUrl = pathToFileURL(resolve('public/mesh-engine/_framework/dotnet.js')).href;
const { dotnet } = await import(runtimeUrl), runtime = await dotnet.create();
const api = (await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)).MeshEngine;
let scratchBytes = 0;
const store: PageStore = { async put(k,b) { await writeFile(`${scratch}/${k}`, b); scratchBytes += b.length; }, async get(k) { return new Uint8Array(await readFile(`${scratch}/${k}`)); } };
const backend = streamBackend(api, 0, tiles, liquids, read, async (n,b) => { await writeFile(`${source}/${n}`, b); });
const started = performance.now();
const { report, stats } = await analyzeStream(tiles, liquids, backend, store, 1000, () => {}, 4 * 1024 * 1024);
api.EndAnalysis();
if (report.Components !== 1 || report.Largest !== report.Polys || report.Stranded !== 0) throw Error(`Water seam coverage failed: ${JSON.stringify(report)}`);
const evidence = { exit: 0, wetCells, cacheBudgetPerKind: 4 * 1024 * 1024, scratchBytes, milliseconds: performance.now()-started, report, stats, nodeMemory: process.memoryUsage() };
await writeFile(`${root}/evidence.json`, JSON.stringify(evidence, null, 2));
console.log(evidence);
