import { expect, test } from 'bun:test';
import { saveTileExport, saveTransportExport, savedMetadataFile, tileExportCurrent } from '../src/mesh/export';
import { sameMeshEngineSource, ENGINE_SOURCE, tileMetadata } from '../src/mesh/project';
import type { MeshBake, MeshInput } from '../src/mesh/types';

const tile = { mapId: 0, wdt: 1, x: 32, y: 32 }, build = { product: 'test', version: '1', buildName: 'build-one' };
const input: MeshInput = { tile, sourceBuild: build, positions: new Float32Array(), indices: new Uint32Array(), classes: new Uint8Array(), origin: [0,0,0], liquidLayers: 0 };
interface TestDirectory { handle: FileSystemDirectoryHandle; files: Map<string,Uint8Array>; children: Map<string,TestDirectory>; failOn(name:string):void }
function directory(): TestDirectory {
	const files = new Map<string, Uint8Array>(); let fail = '';
	const children = new Map<string, TestDirectory>();
	const handle = {
		async getDirectoryHandle(name: string) { let child=children.get(name);if(!child){child=directory();children.set(name,child);}return child.handle; },
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
	return { handle, files, children, failOn(name: string) { fail = name; } };
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


test('global WMO completion includes all output tiles and replacement removes only its obsolete owned files',async()=>{
    const d=directory(),tile={mapId:33,wdt:1,x:32,y:32,kind:'global-wmo' as const};
    const inputGlobal={...input,tile};
    const first={files:{'0033.mmap':new Uint8Array(40),'0033_35_37.mmtile':new Uint8Array(64),'0033_35_38.mmtile':new Uint8Array(64)}} as unknown as MeshBake;
    await saveTileExport(d.handle,tile,first,tileMetadata(inputGlobal,'test'));
    expect(await tileExportCurrent(d.handle,tile,'test',build)).toBe(true);
    d.files.set('0000_35_38.mmtile',new Uint8Array(64));
    const second={files:{'0033.mmap':new Uint8Array(40),'0033_35_37.mmtile':new Uint8Array(80)}} as unknown as MeshBake;
    await saveTileExport(d.handle,tile,second,tileMetadata(inputGlobal,'test'));
    expect(d.files.has('0033_35_38.mmtile')).toBe(false);
    expect(d.files.has('0000_35_38.mmtile')).toBe(true);
    expect(await tileExportCurrent(d.handle,tile,'test',build)).toBe(true);
});
test('transport exports preserve the six-digit FDID and invalidate completion on failure',async()=>{
    const d=directory(),model=116306;
    const deck={files:{'116306.mmap':new Uint8Array(40),'116306_32_32.mmtile':new Uint8Array(64)}} as unknown as MeshBake;
    await saveTransportExport(d.handle,model,deck,tileMetadata(input,'test'));
    const child=d.children.get('transports')!;
    const meta=JSON.parse(new TextDecoder().decode(child.files.get('116306.transport.json')));
    expect(meta.modelFileID).toBe(model);expect(meta.coordinateSystem).toBe('mappster-modf-180-local');expect(meta.displayPlacementExported).toBe(false);
    child.failOn('116306_32_32.mmtile');
    await expect(saveTransportExport(d.handle,model,deck,tileMetadata(input,'test'))).rejects.toThrow('Disk full');
    expect(child.files.has('116306.transport.json')).toBe(false);
    expect(d.files.size).toBe(0);
});
test('transport export rejects files carrying a world-map identity',async()=>{
    const d=directory();
    await expect(saveTransportExport(d.handle,116306,baked,tileMetadata(input,'test'))).rejects.toThrow('Incomplete transport');
});

test('instance diagnostics resolve the complete-job marker for an emitted tile away from its placeholder',async()=>{
    const d=directory(),t={mapId:33,wdt:1,x:32,y:32,kind:'global-wmo' as const};
    const result={files:{'0033.mmap':new Uint8Array(40),'0033_35_37.mmtile':new Uint8Array(64)}}as unknown as MeshBake;
    await saveTileExport(d.handle,t,result,tileMetadata({...input,tile:t},'test'));
    const f=await savedMetadataFile(d.handle,{mapId:33,x:37,y:35});
    expect(JSON.parse(await f.text()).tile.kind).toBe('global-wmo');
    await expect(savedMetadataFile(d.handle,{mapId:33,x:37,y:36})).rejects.toThrow('No completed instance');
});


test('native runner execution-only upgrade reuses completed tiles but geometry changes do not',async()=>{
 const d=directory(),meta=tileMetadata(input,'test');
 const old=JSON.parse(meta.engineSource);old.adapters['../engine-native/Runner.cs']='previous-runner-hash';
 meta.engineSource=JSON.stringify(old);
 const bake={files:{'0000.mmap':new Uint8Array(40),'0000_32_32.mmtile':new Uint8Array(64)}} as unknown as MeshBake;
 await saveTileExport(d.handle,tile,bake,meta);
 expect(sameMeshEngineSource(meta.engineSource,ENGINE_SOURCE)).toBe(true);
 expect(await tileExportCurrent(d.handle,tile,'test',build)).toBe(true);
 old.core['Nav/NavBake.cs']='different-geometry';meta.engineSource=JSON.stringify(old);
 await saveTileExport(d.handle,tile,bake,meta);
 expect(await tileExportCurrent(d.handle,tile,'test',build)).toBe(false);
 expect(sameMeshEngineSource('malformed',ENGINE_SOURCE)).toBe(false);
});


test('byte-limit failure-only repair preserves resume identity for successful old tiles',()=>{
 const old=JSON.parse(ENGINE_SOURCE);old.core['Nav/SourceDetailEdges.cs']="6b3de32292cdf054845c80b17a65f9b54f267824140f150b2b60b092445b6a17";
 expect(sameMeshEngineSource(JSON.stringify(old),ENGINE_SOURCE)).toBe(true);
 old.core['Nav/SourceDetailEdges.cs']='unrecognized-revision';
 expect(sameMeshEngineSource(JSON.stringify(old),ENGINE_SOURCE)).toBe(false);
});
