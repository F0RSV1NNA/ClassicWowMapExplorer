import { ENGINE_SOURCE, tileMetadata } from './project';
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
	await removeMissingOK(directory, name);
	for (const [file, bytes] of Object.entries(bake.files)) await writeExport(directory, file, bytes);
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
		if ((meta.authoringFingerprint ?? '') !== authoringFingerprint) return false;
		if (meta.version !== 3 || meta.product !== product || meta.engineSource !== ENGINE_SOURCE || !build || JSON.stringify(meta.sourceBuild) !== JSON.stringify(build) || meta.tile?.mapId !== tile.mapId || meta.tile?.x !== tile.x || meta.tile?.y !== tile.y || !meta.files?.[tileFile]) return false;
		for (const [name, size] of Object.entries(meta.files)) if ((await (await directory.getFileHandle(name)).getFile()).size !== size) return false;
		return true;
	} catch (e) {
		if (['NotFoundError', 'SyntaxError'].includes((e as Error).name)) return false;
		throw e;
	}
}
