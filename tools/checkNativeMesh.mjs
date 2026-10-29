// Focused packaged-launcher check: synthetic input only, no install discovery or game data.
import fs from 'node:fs/promises';
import { resolve } from 'node:path';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash } from 'node:crypto';
import { unzipSync } from 'fflate';

const root = resolve('.cache/native-launcher-check');
await fs.mkdir(`${root}/app/assets`, { recursive: true }); await fs.mkdir(`${root}/mesh-engine`, { recursive: true });
await fs.copyFile('.cache/MapExplorer-native.exe', `${root}/MapExplorer.exe`);
await fs.copyFile('release/MapExplorer/mesh-engine/Explorer.Mesh.exe', `${root}/mesh-engine/Explorer.Mesh.exe`);
await fs.copyFile('.cache/mesh-parity/source.input', `${root}/app/fixture`);
await fs.writeFile(`${root}/app/index.html`, '<!doctype html><title>Native mesh fixture</title>');
const worker = (await fs.readdir('dist/assets')).find(n => /^bake\.worker-.*\.js$/.test(n));
await fs.copyFile(`dist/assets/${worker}`, `${root}/app/assets/${worker}`);
const child = spawn(`${root}/MapExplorer.exe`, ['--app-only'], { windowsHide: true, stdio: 'ignore' });
let origin;
try {
	for (let tries = 0; tries < 100 && !origin; tries++) {
		for (let port = 51980; port < 52000; port++) {
			try { const url = `http://127.0.0.1:${port}`; const capability = await (await fetch(`${url}/__mesh-native`)).json(); if (capability.available && (await (await fetch(url)).text()).includes('Native mesh fixture')) { origin = url; break; } } catch {}
		}
		if (!origin) await new Promise(r => setTimeout(r, 50));
	}
	if (!origin) throw Error('App-only launcher failed to start');
	const post = (body, headers = {}, signal) => fetch(`${origin}/__mesh-bake`, { method: 'POST', headers: { Origin: origin, 'X-Explorer-Mesh': '1', ...headers }, body, signal });
	const denied = await post(new Uint8Array(4), { Origin: 'https://example.invalid' });
	if (denied.status !== 403) throw Error(`Unexpected cross-origin status ${denied.status}`);
	const bad = await post(new Uint8Array(4));
	if (bad.status !== 500 || !(await bad.text()).includes('Unsupported bake input')) throw Error('Malformed input was not reported by native engine');
	const response = await post(await fs.readFile('.cache/mesh-parity/source.input'));
	if (!response.ok) throw Error(await response.text());
	const files = unzipSync(new Uint8Array(await response.arrayBuffer()));
	const expected = JSON.parse(await fs.readFile('.cache/mesh-parity/evidence.json', 'utf8')), hashes = {};
	for (const [name, hash] of Object.entries(expected.hashes)) {
		hashes[name] = createHash('sha256').update(files[name]).digest('hex');
		if (hashes[name] !== hash) throw Error(`Native x64 mismatch: ${name}`);
	}
	const abort = new AbortController();
	const pending = post(await fs.readFile('.cache/mesh-parity/source.input'), {}, abort.signal).catch(() => undefined);
	await new Promise(r => setTimeout(r, 15));
	const busy = await post(new Uint8Array(4));
	if (busy.status !== 429) throw Error(`Native single-job guard failed: ${busy.status}`);
	abort.abort(); await pending;
	let released = false;
	for (let i = 0; i < 30; i++) {
		const next = await post(new Uint8Array(4)); await next.text();
		if (next.status === 500) { released = true; break; }
		if (next.status !== 429) throw Error(`Unexpected cancellation status ${next.status}`);
		await new Promise(r => setTimeout(r, 50));
	}
	if (!released) throw Error('Cancelled native bake did not release its job slot');
	const browser = await promisify(execFile)(process.execPath, ['tools/checkMeshBrowser.mjs', '--low-memory', `--server=${origin}`], { windowsHide: true });
	process.stdout.write(browser.stdout);
	const hash = async path => createHash('sha256').update(await fs.readFile(path)).digest('hex');
	await fs.writeFile(`${root}/evidence.json`, JSON.stringify({ exit: 0, origin, deniedStatus: denied.status, malformedStatus: bad.status, busyStatus: busy.status, cancellationReleased: released, hashes, nativeHash: await hash(`${root}/mesh-engine/Explorer.Mesh.exe`), launcherHash: await hash(`${root}/MapExplorer.exe`) }, null, 2));
	console.log('Native HTTP, x64 output parity and browser-worker recovery passed.');
} finally { child.kill(); }
