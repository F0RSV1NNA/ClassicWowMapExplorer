import { ENGINE_SOURCE, tileMetadata, sameMeshEngineSource } from './project';
import type { MeshBake, MeshInput, MeshTile } from './types';

export async function writeExport(directory: FileSystemDirectoryHandle, name: string, data: string | Uint8Array) {
	const file = await (await directory.getFileHandle(name, { create: true })).createWritable();
	try { await file.write(typeof data === 'string' ? data : new Uint8Array(data)); await file.close(); }
	catch (e) { await file.abort().catch(() => {}); throw e; }
}
async function removeMissingOK(directory: FileSystemDirectoryHandle, name: string) {
	try { await directory.removeEntry(name); } catch (e) { if ((e as DOMException).name !== 'NotFoundError') throw e; }
}
export async function saveTileExport(directory: FileSystemDirectoryHandle, tile: MeshTile, bake: MeshBake, metadata: ReturnType<typeof tileMetadata>) {
	const name = `${tile.mapId}-${tile.x}-${tile.y}.json`, prefix = String(tile.mapId).padStart(4, '0');
	await writeExport(directory, `reachability-${prefix}.json`, JSON.stringify({ state: 'needs-analysis', reason: 'tile updated', tile }));
	// A metadata file is the completion marker. Remove it before touching output so
	// a cancelled write can never be mistaken for a resumable completed tile.
    let oldFiles: string[]=[];
    if (tile.kind === 'global-wmo') {
        try {oldFiles=Object.keys(JSON.parse(await(await(await directory.getFileHandle(name)).getFile()).text()).files??{});}
        catch(e){if(!['NotFoundError','SyntaxError'].includes((e as Error).name))throw e;}
    }
    await removeMissingOK(directory,name);
	for (const [file, bytes] of Object.entries(bake.files)) await writeExport(directory, file, bytes);
    for(const file of oldFiles)
        if(new RegExp(`^${prefix}_[0-9]+_[0-9]+\\.(mmtile|mliq|links\\.json)$`).test(file)&&!bake.files[file])await removeMissingOK(directory,file);
	const liquid = `${prefix}_${String(tile.y).padStart(2, '0')}_${String(tile.x).padStart(2, '0')}.mliq`;
	if (!bake.files[liquid]) await removeMissingOK(directory, liquid);
	const links = liquid.replace(/\.mliq$/, '.links.json');
	if (!bake.files[links]) await removeMissingOK(directory, links);
	metadata.files = Object.fromEntries(Object.entries(bake.files).map(([file, bytes]) => [file, bytes.length]));
	await writeExport(directory, name, JSON.stringify(metadata, null, 2));
}
export async function tileExportCurrent(directory: FileSystemDirectoryHandle, tile: MeshTile, product: string, build: MeshInput['sourceBuild'], authoringFingerprint = '') {
	try {
		const meta = JSON.parse(await (await (await directory.getFileHandle(`${tile.mapId}-${tile.x}-${tile.y}.json`)).getFile()).text());
		const tileFile = `${String(tile.mapId).padStart(4, '0')}_${String(tile.y).padStart(2, '0')}_${String(tile.x).padStart(2, '0')}.mmtile`;
		if ((meta.authoringFingerprint ?? '') !== authoringFingerprint || meta.tile?.kind !== tile.kind) return false;
		if (meta.version !== 3 || meta.product !== product || !sameMeshEngineSource(meta.engineSource,ENGINE_SOURCE) || !build || JSON.stringify(meta.sourceBuild) !== JSON.stringify(build) || meta.tile?.mapId !== tile.mapId || meta.tile?.x !== tile.x || meta.tile?.y !== tile.y || !(tile.kind === 'global-wmo' ? Object.keys(meta.files ?? {}).some(f => f.endsWith('.mmtile')) : meta.files?.[tileFile])) return false;
		for (const [name, size] of Object.entries(meta.files)) if ((await (await directory.getFileHandle(name)).getFile()).size !== size) return false;
		return true;
	} catch (e) {
		if (['NotFoundError', 'SyntaxError'].includes((e as Error).name)) return false;
		throw e;
	}
}

/** Transport identities are model FDIDs, never world map IDs. Completion is written last. */
export async function saveTransportExport(root: FileSystemDirectoryHandle, model: number, bake: MeshBake, metadata: ReturnType<typeof tileMetadata>) {
    const directory=await root.getDirectoryHandle('transports',{create:true}), prefix=String(model).padStart(4,'0'), marker=`${model}.transport.json`;
    if (!Number.isSafeInteger(model)||model<=0||!bake.files[`${prefix}.mmap`]||!Object.keys(bake.files).some(n=>n.startsWith(prefix+'_')&&n.endsWith('.mmtile'))) throw Error('Incomplete transport bake.');
    const own=new RegExp(`^${prefix}(\\.mmap|_[0-9]+_[0-9]+\\.(mmtile|mliq|links\\.json))$`);
    for(const name of Object.keys(bake.files))if(!own.test(name))throw Error('Transport output contains an unexpected file identity.');
    let old: string[]=[];
    try{old=Object.keys(JSON.parse(await(await(await directory.getFileHandle(marker)).getFile()).text()).files??{});}catch(e){if(!['NotFoundError','SyntaxError'].includes((e as Error).name))throw e;}
    await removeMissingOK(directory,marker);
    for(const [name,bytes]of Object.entries(bake.files))await writeExport(directory,name,bytes);
    for(const name of old)if(own.test(name)&&!bake.files[name])await removeMissingOK(directory,name);
    metadata.files=Object.fromEntries(Object.entries(bake.files).map(([name,bytes])=>[name,bytes.length]));
    await writeExport(directory,marker,JSON.stringify({...metadata,modelFileID:model,coordinateSystem:'mappster-modf-180-local',displayPlacementExported:false},null,2));
}

/** Whole-instance jobs have one completion marker covering every emitted tile. */
export async function savedMetadataFile(directory:FileSystemDirectoryHandle,tile:Pick<MeshTile,'mapId'|'x'|'y'>):Promise<File>{
    const name=`${tile.mapId}-${tile.x}-${tile.y}.json`;
    try{return await(await directory.getFileHandle(name)).getFile();}
    catch(e){if((e as Error).name!=='NotFoundError')throw e;}
    const file=await(await directory.getFileHandle(`${tile.mapId}-32-32.json`)).getFile();
    const meta=JSON.parse(await file.text()),native=`${String(tile.mapId).padStart(4,'0')}_${String(tile.y).padStart(2,'0')}_${String(tile.x).padStart(2,'0')}.mmtile`;
    if(meta.tile?.kind!=='global-wmo'||!meta.files?.[native])throw new DOMException('No completed instance tile metadata.','NotFoundError');
    return file;
}
