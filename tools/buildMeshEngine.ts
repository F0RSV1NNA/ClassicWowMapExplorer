import { cp, mkdir, readFile, readdir } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';

const root = resolve(import.meta.dir, '..');
const engine = resolve(root, 'src/mesh/engine');
const provenance = JSON.parse(await readFile(resolve(engine, 'provenance.json'), 'utf8'));
for (const [file, hash] of Object.entries(provenance.files)) {
	const actual = createHash('sha256').update(await readFile(resolve(engine, file === 'tilesets.csv' ? file : `Core/${file}`))).digest('hex');
	if (actual !== hash) throw new Error(`Vendored Mappster source changed: ${file}. Review and update provenance deliberately.`);
}
for (const [file, hash] of Object.entries(provenance.adapterFiles)) {
	const actual = createHash('sha256').update(await readFile(resolve(engine, file))).digest('hex');
	if (actual !== hash) throw new Error(`Explorer mesh adapter changed: ${file}. Review and update provenance deliberately.`);
}
const build = spawnSync('dotnet', ['publish', resolve(engine, 'engine.csproj'), '-c', 'Release', '--nologo', '-v', 'minimal'], { cwd: root, stdio: 'inherit' });
if (build.status !== 0) process.exit(build.status ?? 1);
const from = resolve(engine, 'bin/Release/net10.0/publish/wwwroot/_framework');
const to = resolve(root, 'public/mesh-engine/_framework');
await mkdir(to, { recursive: true });
for (const file of await readdir(from)) {
	// Compression is the web server's concern. Keep the complete uncompressed runtime.
	if (!/\.(br|gz)$/.test(file)) await cp(resolve(from, file), resolve(to, file));
}
console.log('Bundled Mappster browser engine ready.');
