import type { MeshGeometry } from './types';

export interface ReachabilityReport {
	Tiles: number; Polys: number; Components: number; Largest: number;
	Stranded: number; Rewritten: number; StrandedPercent: number; Error: string;
}
export interface AnalysisRequest {
	directory: FileSystemDirectoryHandle; mapId: number; analyze: boolean;
	minComponent: number; allowPartial: boolean; expectedTiles: string[];
	preview?: { row: number; col: number; radius?: number };
	previewAll?: boolean;
}
export interface AnalysisResult { report?: ReachabilityReport; geometry?: MeshGeometry; partial: boolean; analyzed: boolean; savedTiles: number; loadedTiles: number; stats?: { graphPages: number; polygons: number; waterPages: number; scratchBytes?: number } }

export function decodeFlaggedPreview(bytes: Uint8Array, mapId: number): MeshGeometry {
	const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	const triangles = v.getInt32(0, true);
	if (triangles < 0 || bytes.length !== 4 + triangles * 44) throw new Error('Invalid flagged mesh preview');
	const positions = new Float32Array(triangles * 9), classes = new Uint8Array(triangles), unreachable: boolean[] = []; let at = 4;
	for (let t = 0; t < triangles; t++) {
		classes[t] = v.getInt32(at, true); unreachable.push(classes[t] === 4 || !!(v.getInt32(at + 4, true) & 0x100)); at += 8;
		for (let k = 0; k < 9; k++, at += 4) positions[t * 9 + k] = v.getFloat32(at, true);
	}
	return { mapId, positions, classes, triangles, unreachable, source: 'navmesh' };
}

