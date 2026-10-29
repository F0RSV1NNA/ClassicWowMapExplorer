import { expect, test } from 'bun:test';
import { saveTileExport, tileExportCurrent } from '../src/mesh/export';
import { tileMetadata } from '../src/mesh/project';
import type { MeshBake, MeshInput } from '../src/mesh/types';

const tile = { mapId: 0, wdt: 1, x: 32, y: 32 }, build = { product: 'test', version: '1', buildName: 'build-one' };
const input: MeshInput = { tile, sourceBuild: build, positions: new Float32Array(), indices: new Uint32Array(), classes: new Uint8Array(), origin: [0,0,0], liquidLayers: 0 };
function directory() {
	const files = new Map<string, Uint8Array>(); let fail = '';
	const handle = {
		async removeEntry(name: string) { if (!files.delete(name)) throw new DOMException('Missing', 'NotFoundError'); },
		async getFileHandle(name: string, options?: { create?: boolean }) {
			if (!files.has(name) && !options?.create) throw new DOMException('Missing', 'NotFoundError');
			return {
				async getFile() { const b = files.get(name)!; return { size: b.length, async text() { return new TextDecoder().decode(b); } }; },
				async createWritable() {
					if (name === fail) throw new DOMException('Disk full', 'QuotaExceededError');
					let pending: Uint8Array;
					return { async write(data: string | Uint8Array) { pending = typeof data === 'string' ? new TextEncoder().encode(data) : data.slice(); }, async close() { files.set(name, pending); }, async abort() {} };
				},
			};
		},
	} as unknown as FileSystemDirectoryHandle;
	return { handle, files, failOn(name: string) { fail = name; } };
}
const baked = { files: { '0000.mmap': new Uint8Array(40), '0000_32_32.mmtile': new Uint8Array(64) } } as unknown as MeshBake;

test('resume accepts completed outputs only for the same engine and game build', async () => {
	const d = directory();
	await saveTileExport(d.handle, tile, baked, tileMetadata(input, 'test'));
	expect(await tileExportCurrent(d.handle, tile, 'test', build)).toBe(true);
	expect(await tileExportCurrent(d.handle, tile, 'test', { ...build, buildName: 'updated-client' })).toBe(false);
	d.files.set('0000_32_32.mmtile', new Uint8Array(32));
	expect(await tileExportCurrent(d.handle, tile, 'test', build)).toBe(false);
});
test('interrupted tile replacement invalidates its old completion marker before changing outputs', async () => {
	const d = directory(); await saveTileExport(d.handle, tile, baked, tileMetadata(input, 'test'));
	d.failOn('0000_32_32.mmtile');
	await expect(saveTileExport(d.handle, tile, baked, tileMetadata(input, 'test'))).rejects.toThrow('Disk full');
	expect(d.files.has('0-32-32.json')).toBe(false);
	expect(await tileExportCurrent(d.handle, tile, 'test', build)).toBe(false);
	expect(JSON.parse(new TextDecoder().decode(d.files.get('reachability-0000.json'))).state).toBe('needs-analysis');
});
test('resume rejects completion markers missing the current adapter fingerprint', async () => {
	const d = directory(); await saveTileExport(d.handle, tile, baked, tileMetadata(input, 'test'));
	const metadata = JSON.parse(new TextDecoder().decode(d.files.get('0-32-32.json')));
	metadata.engineSource = JSON.stringify(JSON.parse(metadata.engineSource).core);
	d.files.set('0-32-32.json', new TextEncoder().encode(JSON.stringify(metadata)));
	expect(await tileExportCurrent(d.handle, tile, 'test', build)).toBe(false);
});
test('rebaking a newly dry tile removes its obsolete liquid sidecar', async () => {
	const d = directory(); d.files.set('0000_32_32.mliq', new Uint8Array(100));
	await saveTileExport(d.handle, tile, baked, tileMetadata(input, 'test'));
	expect(d.files.has('0000_32_32.mliq')).toBe(false);
	expect(await tileExportCurrent(d.handle, tile, 'test', build)).toBe(true);
});
test('edit fingerprints invalidate affected completion markers and deleted links lose their sidecar',async()=>{
	const d=directory(),meta=tileMetadata(input,'test');meta.authoringFingerprint='edited';
	d.files.set('0000_32_32.links.json',new Uint8Array(20));
	await saveTileExport(d.handle,tile,baked,meta);
	expect(d.files.has('0000_32_32.links.json')).toBe(false);
	expect(await tileExportCurrent(d.handle,tile,'test',build,'edited')).toBe(true);
	expect(await tileExportCurrent(d.handle,tile,'test',build,'changed')).toBe(false);
});
