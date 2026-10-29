import { mkdir, readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';

const root = resolve(import.meta.dir, '..'), engine = resolve(root, 'src/mesh/engine');
const provenance = JSON.parse(await readFile(resolve(engine, 'provenance.json'), 'utf8'));
for (const [file, expected] of Object.entries(provenance.files)) {
	const actual = createHash('sha256').update(await readFile(resolve(engine, file === 'tilesets.csv' ? file : `Core/${file}`))).digest('hex');
	if (actual !== expected) throw Error(`Vendored bake source changed: ${file}`);
}
for (const [file, expected] of Object.entries(provenance.adapterFiles)) {
	const actual = createHash('sha256').update(await readFile(resolve(engine, file))).digest('hex');
	if (actual !== expected) throw Error(`Explorer mesh adapter changed: ${file}`);
}
const out = resolve(root, 'release/MapExplorer/mesh-engine'); await mkdir(out, { recursive: true });
const result = spawnSync('dotnet', ['publish', resolve(root, 'src/mesh/engine-native/engine-native.csproj'), '-c', 'Release', '-o', out, '--nologo', '-v', 'minimal'], { cwd: root, stdio: 'inherit' });
if (result.status !== 0) process.exit(result.status ?? 1);
