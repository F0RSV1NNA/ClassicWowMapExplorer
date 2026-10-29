/** Keep CPU work bounded, stop admission on failure, and drain before returning. */
export async function parallelTiles<T>(items: readonly T[], concurrency: number, signal: AbortSignal, run: (item: T) => Promise<void>): Promise<void> {
	if (!Number.isInteger(concurrency) || concurrency < 1 || concurrency > 8) throw new Error('Bake concurrency must be 1–8.');
	let next = 0, failure: unknown, failed = false;
	await Promise.all(Array.from({ length: Math.min(concurrency, items.length) }, async () => {
		while (!failed && !signal.aborted) {
			const index = next++;
			if (index >= items.length) return;
			try { await run(items[index]); }
			catch (e) { failed = true; failure ??= e; }
		}
	}));
	signal.throwIfAborted();
	if (failed) throw failure;
}

/** Source preparation and filesystem mutations each get their own serial lane. */
export function serialLane() {
	let tail: Promise<unknown> = Promise.resolve();
	return <T>(run: () => Promise<T>): Promise<T> => {
		const result = tail.then(run);
		tail = result.catch(() => {});
		return result;
	};
}

export function defaultBakeConcurrency(cores: number, memoryGB?: number): number {
	// Reserve a core for rendering/storage; avoid multiple heaps on small devices.
	return Math.max(1, Math.min(4, Math.floor((cores || 2) / 2), memoryGB && memoryGB <= 4 ? 1 : memoryGB && memoryGB <= 8 ? 2 : 4));
}

/** Resume known OOM tiles directly on the isolated recovery backend. */
export function priorMemoryFailures(previous: unknown, sourceBuild: unknown): Set<string> {
	const result = new Set<string>();
	if (!previous || typeof previous !== 'object' || !sourceBuild) return result;
	const report = previous as { sourceBuild?: unknown; failures?: unknown };
	if (JSON.stringify(report.sourceBuild) !== JSON.stringify(sourceBuild) || !Array.isArray(report.failures)) return result;
	for (const error of report.failures) {
		if (typeof error !== 'string' || !/OutOfMemory|exit\(1\)/i.test(error)) continue;
		const tile = /^Map (\d+), (\d+)_(\d+):/.exec(error);
		if (tile) result.add(`${Number(tile[1])}:${Number(tile[2])}:${Number(tile[3])}`);
	}
	return result;
}
