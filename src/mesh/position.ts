import { TILE_SIZE } from '../formats/adt';
import type { MeshPoint } from './types';

/** Native coordinates, independent of the viewer's continent display offsets. */
export function meshPositionDetails(point: MeshPoint) {
	const col = Math.floor(32 - point.y / TILE_SIZE), row = Math.floor(32 - point.x / TILE_SIZE);
	const valid = row >= 0 && row < 64 && col >= 0 && col < 64;
	const file = valid ? `${String(point.mapId).padStart(4, '0')}_${String(row).padStart(2, '0')}_${String(col).padStart(2, '0')}.mmtile` : null;
	const metadata = valid ? `${point.mapId}-${col}-${row}.json` : null;
	const text = `Camera (native WoW coordinates)\nMap: ${point.mapId}\nX: ${point.x.toFixed(3)}  Y: ${point.y.toFixed(3)}  Z: ${point.z.toFixed(3)}\nTile X/column: ${col}  Y/row: ${row}\nMesh: ${file ?? 'outside the terrain tile grid'}`;
	return { row, col, file, metadata, text };
}
