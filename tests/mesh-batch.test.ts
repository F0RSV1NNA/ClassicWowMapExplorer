import { expect, test } from 'bun:test';
import { defaultBakeConcurrency, parallelTiles, serialLane, priorMemoryFailures } from '../src/mesh/batch';

test('batch overlaps CPU jobs but serializes source and export work', async () => {
	const read = serialLane(), write = serialLane(), seen: number[] = [];
	let active = 0, peak = 0, reading = 0, writing = 0;
	await parallelTiles([0, 1, 2, 3, 4], 3, new AbortController().signal, async id => {
		await read(async () => { expect(reading++).toBe(0); await Bun.sleep(1); reading--; });
		peak = Math.max(peak, ++active); await Bun.sleep(5); active--;
		await write(async () => { expect(writing++).toBe(0); await Bun.sleep(1); seen.push(id); writing--; });
	});
	expect(peak).toBe(3); expect(seen.sort()).toEqual([0, 1, 2, 3, 4]);
});

test('fatal batch failures drain admitted jobs and stop new admission', async () => {
	const seen: number[] = []; let drained = false;
	await expect(parallelTiles([0, 1, 2, 3], 2, new AbortController().signal, async id => {
		seen.push(id);
		if (id === 0) throw new Error('disk full');
		await Bun.sleep(5); drained = true;
	})).rejects.toThrow('disk full');
	expect(drained).toBe(true); expect(seen).toEqual([0, 1]);
});

test('cancelled batches admit no new tiles', async () => {
	const abort = new AbortController(); let count = 0;
	await expect(parallelTiles([0, 1, 2], 1, abort.signal, async () => { count++; abort.abort(); })).rejects.toThrow();
	expect(count).toBe(1);
	expect(defaultBakeConcurrency(16, 4)).toBe(1);
	expect(defaultBakeConcurrency(16, 8)).toBe(2);
	expect(defaultBakeConcurrency(16, 16)).toBe(4);
});

test('resume selects only same-build memory/process failures for native-first recovery', () => {
	const build = { product: 'fixture', version: '1' };
	const previous = { sourceBuild: build, failures: ['Map 0, 31_31: OutOfMemoryException', 'Map 0, 30_48: Program terminated with exit(1)', 'Map 0, 12_13: no walkable polygons', 'Map 0 reachability: OutOfMemoryException'] };
	expect([...priorMemoryFailures(previous, build)]).toEqual(['0:31:31', '0:30:48']);
	expect(priorMemoryFailures(previous, { ...build, version: '2' }).size).toBe(0);
});
