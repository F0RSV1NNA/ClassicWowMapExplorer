export interface MeshPoint { mapId: number; x: number; y: number; z: number }
export interface MeshGeometry { mapId: number; positions: number[] | Float32Array; classes: number[] | Uint8Array; triangles: number; source: 'collision' | 'navmesh'; unreachable?: boolean[] }
export interface MeshTile { mapId: number; wdt: number; x: number; y: number }
export interface MeshInput {
	edits?: import('./authoring').BakeEdits;
	tile: MeshTile;
	sourceBuild?: { product: string; version: string; buildName: string };
	/** Recast coordinates: native (WoW Y, WoW Z, WoW X), relative to origin. */
	positions: Float32Array;
	indices: Uint32Array;
	classes: Uint8Array;
	origin: [number, number, number];
	liquidLayers: number;
	liquids?: { positions: Float32Array; indices: Uint32Array; kinds: Uint8Array };
	/** Original game files for Mappster's own extractor; preview arrays above are not bake input when present. */
	raw?: {
		files: { id: number; bytes: Uint8Array }[];
		tiles: { x: number; y: number; root: number; obj0: number; tex0: number }[];
		liquidBanks: [number, number][];
	};
}
export interface MeshBake {
	executionBackend?: 'wasm' | 'native-x64';
	geometry: MeshGeometry;
	/** Native Mappster mmap/mmtile/mliq output files, excluding worker preview buffers. */
	files: Record<string, Uint8Array>;
	settings: Record<string, unknown>;
	milliseconds: number;
	quality: MeshQuality;
	liquidGeometry?: MeshGeometry;
}
export interface MeshQuality {
	pipeline: string; polygons: number; regionsSplit: number; pinholesFilled: number;
	detailRetriangulated: number; detailSpikes: number; detailBlades: number; detailClamped: number;
	longDetailEdges: number; regionSplitGrid: number;
	regionsUnwound: number; tinyRegionCells: number;
	indoorCells: number; trimCells: number; roadCells: number; railingCells: number; asymmetricLinks: number;
	swimPolygons: number; submergedPolygons: number; hazardPolygons: number; waterlineCuts: number;
	fallbacks: string[];
	stages: Record<string, number>;
}
/** Native WoW -> Explorer display, including the map's artificial layout offset. */
export function meshWorldPoint(x: number, y: number, z: number, offsetX: number, offsetY: number): [number, number, number] {
	const tile = 1600 / 3;
	return [32 * tile - y + offsetX * tile, z, 32 * tile - x + offsetY * tile];
}
