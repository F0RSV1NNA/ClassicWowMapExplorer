import { expect, test } from 'bun:test';
import { meshPositionDetails } from '../src/mesh/position';
import { TILE_SIZE } from '../src/formats/adt';

test('native Stormwind coordinates match export column/row and filename ordering', () => {
	const details = meshPositionDetails({ mapId: 0, x: -8550.6, y: 728.2, z: 101.7 });
	expect([details.col, details.row]).toEqual([30, 48]);
	expect(details.file).toBe('0000_48_30.mmtile');
	expect(details.metadata).toBe('0-30-48.json');
	expect(details.text).toContain('X: -8550.600  Y: 728.200  Z: 101.700');
});

test('tile boundaries use native axes and out-of-grid points never invent a filename', () => {
	expect(meshPositionDetails({ mapId: 571, x: TILE_SIZE, y: 0, z: 5 }).file).toBe('0571_31_32.mmtile');
	const outside = meshPositionDetails({ mapId: 0, x: TILE_SIZE * 40, y: 0, z: 5 });
	expect(outside.file).toBeNull(); expect(outside.metadata).toBeNull();
});
