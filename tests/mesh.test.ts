import { expect, test } from 'bun:test';
import { meshWorldPoint } from '../src/mesh/types';
import { terrainCollision, wmoCollisionIndices } from '../src/mesh/geometry';
import { soundBankField } from '../src/mesh/liquid-classification';
import { encodeBakeInput } from '../src/mesh/engine-api';
import { decodeFlaggedPreview } from '../src/mesh/reachability';

test('saved polygon reachability is a flag independent of surface area', () => {
	const bytes = new Uint8Array(48), view = new DataView(bytes.buffer);
	view.setInt32(0, 1, true); view.setInt32(4, 12, true); view.setInt32(8, 0x101, true);
	view.setFloat32(12, 123, true);
	const geometry = decodeFlaggedPreview(bytes, 0);
	expect(Array.from(geometry.classes)).toEqual([12]);
	expect(geometry.unreachable).toEqual([true]);
	expect(geometry.positions[0]).toBe(123);
	expect(() => decodeFlaggedPreview(bytes.slice(0, 47), 0)).toThrow('Invalid flagged');
});

test('LiquidType uses the field from the actual layout, including non-inline IDs and array fields', () => {
	expect(soundBankField('4397CEE6')).toBe(3);
	expect(soundBankField(0x4397CEE6)).toBe(3);
	expect(soundBankField(0xD1ECEEC9)).toBe(3);
	expect(soundBankField(0x3313BBF3)).toBe(13);
	expect(soundBankField('D1ECEEC9')).toBe(3);
	expect(soundBankField('3313BBF3')).toBe(13);
	expect(() => soundBankField('DEADBEEF')).toThrow('no bundled SoundBank');
});

test('raw bake adapter preserves source bytes and native row/column declarations', () => {
	const bytes = new Uint8Array([8, 4, 2]);
	const encoded = encodeBakeInput({ tile: { mapId: 0, wdt: 1, x: 31, y: 47 }, origin: [0, 0, 0], liquidLayers: 0,
		positions: new Float32Array(), indices: new Uint32Array(), classes: new Uint8Array(),
		raw: { files: [{ id: 123, bytes }], liquidBanks: [[42, 2]], tiles: [{ x: 47, y: 31, root: 123, obj0: 0, tex0: 0 }] } });
	const view = new DataView(encoded.buffer);
	expect(view.getUint32(0, true)).toBe(0x32524D45);
	expect(Array.from(encoded.slice(28, 31))).toEqual([8, 4, 2]);
	expect(Array.from(bytes)).toEqual([8, 4, 2]);
	expect(encoded.length).toBe(62);
});

test('mesh overlay preserves native height and continent layout offsets', () => {
	const origin = 32 * (1600 / 3);
	expect(meshWorldPoint(100, 200, 30, 0, 0)).toEqual([origin - 200, 30, origin - 100]);
	const moved = meshWorldPoint(100, 200, 30, 70, -4);
	expect(moved[0]).toBeCloseTo(origin - 200 + 70 * (1600 / 3));
	expect(moved[1]).toBe(30);
	expect(moved[2]).toBeCloseTo(origin - 100 - 4 * (1600 / 3));
});

test('collision terrain keeps holes and upward winding without render skirts', () => {
	const mesh = terrainCollision(new Float32Array(9).fill(5), new Float32Array(4).fill(5), 2, 8, new Uint8Array([0, 1, 0, 0]));
	expect(mesh.positions.length / 3).toBe(13);
	expect(mesh.indices.length / 3).toBe(12);
	for (let i = 1; i < mesh.positions.length; i += 3) expect(mesh.positions[i]).toBe(5);
	for (let t = 0; t < mesh.indices.length; t += 3) {
		const [a, b, c] = Array.from(mesh.indices.slice(t, t + 3), (v) => v * 3);
		const ny = (mesh.positions[b + 2] - mesh.positions[a + 2]) * (mesh.positions[c] - mesh.positions[a]) - (mesh.positions[b] - mesh.positions[a]) * (mesh.positions[c + 2] - mesh.positions[a + 2]);
		expect(ny).toBeGreaterThan(0);
	}
});

test('WMO collision uses physical MOPY bits, with explicit collision overriding detail', () => {
    const flags = new Uint8Array([0x00, 0x04, 0x08, 0x0c, 0x10, 0x20, 0x24, 0x28, 0x2c, 0x40, 0x80, 0x22]);
    const indices = Uint16Array.from({ length: flags.length * 3 }, (_, i) => i);
    const kept = [2, 3, 5, 7, 8, 11].flatMap((t) => [t * 3, t * 3 + 1, t * 3 + 2]);
    expect(wmoCollisionIndices(indices, flags)).toEqual(kept);
    expect(wmoCollisionIndices(new Uint16Array([0, 1, 2]))).toEqual([0, 1, 2]);
});
