import type { MeshPoint, MeshTile } from './types';
export type BrushKind = 0 | 1 | 2 | 3 | 4;
export interface Stroke { X: number; Y: number; Z: number; Radius: number; HeightBand: number; Kind: BrushKind }
export interface Link { Ax: number; Ay: number; Az: number; Bx: number; By: number; Bz: number; Radius: number; Bidirectional: boolean; Mode: 'walk' | 'portal' | 'elevator'; Note: string; UserId: number }
export interface MapEdits { Roads: Stroke[]; OffMesh: Link[] }
export interface BakeEdits { Map: MapEdits; Global: { RoadOn: number[]; RoadOff: number[] } }
export interface AuthoringProject { version: 1; product: string; maps: Record<string, MapEdits>; global: BakeEdits['Global'] }
export const emptyEdits = (): MapEdits => ({ Roads: [], OffMesh: [] });
export const newProject = (product: string): AuthoringProject => ({ version: 1, product, maps: {}, global: { RoadOn: [], RoadOff: [] } });
export function readProject(text: string, product: string): AuthoringProject {
	const p = JSON.parse(text) as AuthoringProject;
	if (p.version !== 1 || p.product !== product || !p.maps || !p.global) throw Error('Edits belong to a different product or unsupported format.');
	const finite = (v: number) => typeof v === 'number' && Number.isFinite(v);
	for (const [map, edits] of Object.entries(p.maps)) {
		if (!/^\d+$/.test(map) || !Array.isArray(edits.Roads) || !Array.isArray(edits.OffMesh) || edits.Roads.length > 100000 || edits.OffMesh.length > 10000) throw Error('Invalid edit map.');
		for (const s of edits.Roads) if (![s.X,s.Y,s.Z,s.Radius,s.HeightBand].every(finite) || s.Radius < .25 || s.Radius > 100 || s.HeightBand < .1 || s.HeightBand > 10 || ![0,1,2,3,4].includes(s.Kind)) throw Error('Invalid brush stroke.');
		const ids = new Set<number>();
		for (const l of edits.OffMesh) {
			if (![l.Ax,l.Ay,l.Az,l.Bx,l.By,l.Bz,l.Radius].every(finite) || l.Radius < .1 || l.Radius > 20 || !['walk','portal','elevator'].includes(l.Mode) || typeof l.Bidirectional !== 'boolean' || typeof l.Note !== 'string' || !Number.isSafeInteger(l.UserId) || l.UserId <= 0 || l.UserId > 2147483647 || ids.has(l.UserId)) throw Error('Invalid or duplicate connection.');
			ids.add(l.UserId);
		}
	}
	for (const list of [p.global.RoadOn, p.global.RoadOff]) if (!Array.isArray(list) || list.some(n => !Number.isSafeInteger(n) || n <= 0 || n > 2147483647)) throw Error('Invalid texture overrides.');
	if (p.global.RoadOn.some(id=>p.global.RoadOff.includes(id))) throw Error('A texture cannot be both always-road and never-road.');
	return p;
}
export function editsForTile(project: AuthoringProject, tile: MeshTile): BakeEdits {
	const map = project.maps[tile.mapId] ?? emptyEdits(), size = Math.fround(533.33333);
	const minX = (31 - tile.y) * size - 8, maxX = (32 - tile.y) * size + 8;
	const minY = (31 - tile.x) * size - 8, maxY = (32 - tile.x) * size + 8;
	return { Map: { Roads: map.Roads.filter(s => s.X + s.Radius >= minX && s.X - s.Radius <= maxX && s.Y + s.Radius >= minY && s.Y - s.Radius <= maxY), OffMesh: map.OffMesh.filter(l => l.Ax >= minX && l.Ax <= maxX && l.Ay >= minY && l.Ay <= maxY) }, Global: structuredClone(project.global) };
}
export function editFingerprint(edits?: BakeEdits): string {
	return !edits || (!edits.Map.Roads.length && !edits.Map.OffMesh.length && !edits.Global.RoadOn.length && !edits.Global.RoadOff.length) ? '' : JSON.stringify(edits);
}
export class EditHistory {
	private undoStack: AuthoringProject[] = []; private redoStack: AuthoringProject[] = [];
	constructor(public project: AuthoringProject) {}
	commit(next: AuthoringProject) { if (JSON.stringify(next) === JSON.stringify(this.project)) return; this.undoStack.push(this.project); if (this.undoStack.length > 50) this.undoStack.shift(); this.project = next; this.redoStack = []; }
	undo() { const previous = this.undoStack.pop(); if (previous) { this.redoStack.push(this.project); this.project = previous; } }
	redo() { const next = this.redoStack.pop(); if (next) { this.undoStack.push(this.project); this.project = next; } }
	get canUndo() { return this.undoStack.length > 0; } get canRedo() { return this.redoStack.length > 0; }
}
export function dab(point: MeshPoint, radius: number, band: number, kind: BrushKind): Stroke { return { X: point.x, Y: point.y, Z: point.z, Radius: radius, HeightBand: band, Kind: kind }; }
