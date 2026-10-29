import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { strFromU8, unzipSync } from 'fflate';
import { encodeBakeInput } from '../src/mesh/engine-api';
import type { MeshInput } from '../src/mesh/types';

// One small synthetic source fixture. No installed game files, route sweeps or GUI interaction.
const chunk = (id: string, body: Buffer) => {
	const h = Buffer.alloc(8); h.write(id.split('').reverse().join('')); h.writeUInt32LE(body.length, 4);
	return Buffer.concat([h, body]);
};
const terrain: Buffer[] = [], texture: Buffer[] = [];
for (let row = 0; row < 2; row++) for (let col = 0; col < 2; col++) {
	const h = Buffer.alloc(128); h.writeInt32LE(row, 4); h.writeInt32LE(col, 8);
	h.writeFloatLE(-row * 533.33333 / 16, 0x68); h.writeFloatLE(-col * 533.33333 / 16, 0x6c); h.writeFloatLE(5, 0x70);
	terrain.push(chunk('MCNK', Buffer.concat([h, chunk('MCVT', Buffer.alloc(145 * 4))])));
}
// Paint the first four texture chunks with a road texture from Mappster's own names snapshot.
for (let i = 0; i < 4; i++) texture.push(chunk('MCNK', chunk('MCLY', Buffer.alloc(16))));
const mdid = Buffer.alloc(4); mdid.writeInt32LE(186765);
const water = Buffer.alloc(256 * 12 + 24); water.writeUInt32LE(256 * 12, 0); water.writeUInt32LE(1, 4);
const w = 256 * 12; water.writeUInt16LE(42, w); water.writeFloatLE(9, w + 4); water.writeFloatLE(9, w + 8); water[w + 14] = 8; water[w + 15] = 8;
const root = Buffer.concat([...terrain, chunk('MH2O', water)]);
const tex0 = Buffer.concat([chunk('MDID', mdid), ...texture]);
const input: MeshInput = { tile: { mapId: 0, wdt: 1, x: 32, y: 32 }, origin: [0, 0, 0], liquidLayers: 1,
	positions: new Float32Array(), indices: new Uint32Array(), classes: new Uint8Array(),
	raw: { files: [{ id: 1, bytes: root }, { id: 2, bytes: tex0 }], tiles: [{ x: 32, y: 32, root: 1, obj0: 0, tex0: 2 }], liquidBanks: [[42, 0]] } };
const dir = '.cache/mesh-parity'; await mkdir(dir, { recursive: true });
await writeFile(`${dir}/source.input`, encodeBakeInput(input));
const run = (cmd: string, args: string[]) => {
	const r = spawnSync(cmd, args, { stdio: 'inherit' });
	if (r.status !== 0) throw new Error(`${cmd} failed, exit ${r.status}`);
};
run('dotnet', ['run', '--project', 'tools/mesh-native-check/check.csproj', '-c', 'Release', '--', `${dir}/source.input`, `${dir}/source.native.zip`]);
run('node', ['tools/checkMeshWasm.mjs', `${dir}/source.input`, `${dir}/source.wasm.zip`]);
const native = unzipSync(await readFile(`${dir}/source.native.zip`)), wasm = unzipSync(await readFile(`${dir}/source.wasm.zip`));
const evidence: Record<string, string> = {};
for (const name of Object.keys(native).filter((n) => n !== 'engine.json')) {
	if (!wasm[name] || !Buffer.from(native[name]).equals(Buffer.from(wasm[name]))) throw new Error(`Native/WASM mismatch: ${name}`);
	evidence[name] = createHash('sha256').update(wasm[name]).digest('hex');
	console.log(`IDENTICAL ${name}: ${evidence[name]}`);
}
const metadata = JSON.parse(strFromU8(wasm['engine.json']));
if (metadata.metrics.roadCells <= 0 || metadata.metrics.swimPolygons <= 0) throw new Error('Fixture did not exercise road and swim classification');
if (!Object.keys(wasm).some((f) => f.endsWith('.mliq'))) throw new Error('Missing native liquid sidecar');
await writeFile(`${dir}/evidence.json`, JSON.stringify({ exit: 0, engine: metadata.pipeline, metrics: metadata.metrics, hashes: evidence }, null, 2));
console.log('Raw extraction, roads, swimming, MLIQ and mesh output match native Mappster code.');
