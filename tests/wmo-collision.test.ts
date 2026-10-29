import { expect, test } from 'bun:test';
import { parseWmoGroup } from '../src/formats/wmo';
import { wmoCollisionIndices } from '../src/mesh/geometry';

function chunk(id: string, body: Uint8Array): Uint8Array {
	const out = new Uint8Array(body.length + 8);
	out.set([...id].reverse().map(c => c.charCodeAt(0)));
	new DataView(out.buffer).setUint32(4, body.length, true);
	out.set(body, 8);
	return out;
}
function join(parts: Uint8Array[]): Uint8Array {
	const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
	let at = 0;
	for (const p of parts) { out.set(p, at); at += p.length; }
	return out;
}

for (const wide of [false, true]) test(`WMO ${wide ? 'MPY2/MOVX' : 'MOPY/MOVI'} preserves established mesh policy and invisible-wall records`, () => {
	const flags = [0, 4, 8, 12, 16, 32, 36, 40, 44, 64, 128, 34];
	const materials = new Uint8Array(flags.length * (wide ? 4 : 2));
	const mv = new DataView(materials.buffer);
	for (let t = 0; t < flags.length; t++) {
		if (wide) { mv.setUint16(t * 4, flags[t] | 0x100, true); mv.setUint16(t * 4 + 2, 0xffff, true); }
		else { materials[t * 2] = flags[t]; materials[t * 2 + 1] = 0xff; }
	}
	const base = wide ? 65536 : 0;
	const indices = new Uint8Array(flags.length * 3 * (wide ? 4 : 2));
	const iv = new DataView(indices.buffer);
	for (let i = 0; i < flags.length * 3; i++) {
		if (wide) iv.setUint32(i * 4, base + i, true);
		else iv.setUint16(i * 2, i, true);
	}
	const group = parseWmoGroup(chunk('MOGP', join([new Uint8Array(68), chunk(wide ? 'MPY2' : 'MOPY', materials), chunk(wide ? 'MOVX' : 'MOVI', indices)])));
	if (wide) expect(group.triangleFlags).toBeUndefined();
	else expect(Array.from(group.triangleFlags!)).toEqual(flags);
	expect(group.collisionOnly).toEqual([2, 3, 7, 8]);
	// MPY2 collision-only records add upstream wall highlighting. They must
	// not silently change the accepted baker's unclassified-face policy.
	expect(wmoCollisionIndices(group.indices, group.triangleFlags)).toEqual(wide
		? Array.from({ length: flags.length * 3 }, (_, i) => base + i)
		: [2, 3, 5, 7, 8, 11].flatMap(t => [base+t*3, base+t*3+1, base+t*3+2]));
});
