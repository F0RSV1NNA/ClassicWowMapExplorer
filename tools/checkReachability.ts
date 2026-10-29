import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { unzipSync, strFromU8 } from 'fflate';
import { encodeBakeInput } from '../src/mesh/engine-api';
import { decodeFlaggedPreview } from '../src/mesh/reachability';

const positions: number[] = [], indices: number[] = [];
const quad = (a: number, b: number, c: number, d: number, height: number) => {
	const v = positions.length / 3;
	positions.push(a, height, b, c, height, b, c, height, d, a, height, d);
	indices.push(v, v + 2, v + 1, v, v + 3, v + 2);
};
quad(-100, -100, -20, -20, 5); // Main landmass.
quad(-160, -160, -152, -152, 5); // Isolated scrap, should be pink.
quad(-118, -90, -105, -75, 1); // Separated seabed, joined to mainland by its liquid body.
const input = encodeBakeInput({ tile: { mapId: 0, wdt: 1, x: 32, y: 32 }, origin: [0, 0, 0], liquidLayers: 1,
	positions: new Float32Array(positions), indices: new Uint32Array(indices), classes: new Uint8Array(indices.length / 3),
	liquids: { positions: new Float32Array([-120,8,-100,-80,8,-100,-80,8,-60,-120,8,-60]), indices: new Uint32Array([0,2,1,0,3,2]), kinds: new Uint8Array([1,1]) } });
const dir = '.cache/mesh-reachability'; await mkdir(dir, { recursive: true });
await writeFile(`${dir}/fixture.input`, input);
for (const [cmd, args] of [
	['dotnet', ['run', '--project', 'tools/mesh-native-check/check.csproj', '-c', 'Release', '--', `${dir}/fixture.input`, `${dir}/native.zip`, 'reachability']],
	['node', ['tools/checkMeshWasm.mjs', `${dir}/fixture.input`, `${dir}/wasm.zip`, 'reachability']],
] as const) {
	const r = spawnSync(cmd, [...args], { stdio: 'inherit' }); if (r.status !== 0) throw new Error(`${cmd} exit ${r.status}`);
}
const native = unzipSync(await readFile(`${dir}/native.zip`)), wasm = unzipSync(await readFile(`${dir}/wasm.zip`));
const hashes: Record<string, string> = {};
for (const name of Object.keys(native)) {
	if (!wasm[name] || !Buffer.from(native[name]).equals(Buffer.from(wasm[name]))) throw new Error(`Mismatch: ${name}`);
	hashes[name] = createHash('sha256').update(wasm[name]).digest('hex');
}
const report = JSON.parse(strFromU8(wasm['report.json']));
const preserved = JSON.parse(strFromU8(wasm['preserved.json']));
if (report.Stranded <= 0 || report.Components !== 2 || preserved.Stranded !== 0) throw new Error(`Incorrect scrap/water/separate-landmass verdict: ${JSON.stringify({ report, preserved })}`);
const preview = decodeFlaggedPreview(wasm['flagged-preview.bin'], 0);
if (!preview.unreachable?.some(Boolean) || preview.unreachable.every(Boolean)) throw new Error('Preview flags did not distinguish reachable and stranded triangles');
await writeFile(`${dir}/evidence.json`, JSON.stringify({ exit: 0, report, hashes }, null, 2));
console.log('Native/WASM flags and detail preview match; isolated scrap marked, liquid connection preserved, flags recompute correctly.', report);
