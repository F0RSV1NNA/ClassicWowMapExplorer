import type { AnalysisRequest, AnalysisResult } from './reachability';

export function runReachability(request: AnalysisRequest, signal: AbortSignal, progress: (text: string) => void, onTile?: (geometry: NonNullable<AnalysisResult['geometry']>, key: string) => Promise<void>): Promise<AnalysisResult> {
	return new Promise((resolve, reject) => {
		if (signal.aborted) { reject(new DOMException('Cancelled', 'AbortError')); return; }
		const worker = new Worker(new URL('./reachability.worker.ts', import.meta.url), { type: 'module' });
		let scratch: string | undefined;
		const finish = () => {
			signal.removeEventListener('abort', cancel); worker.terminate();
			if (scratch) {
				const name = scratch;
				void navigator.locks.request(`mesh-scratch:${name}`, async () => {
					const root = await navigator.storage.getDirectory(); await root.removeEntry(name, { recursive: true });
				}).catch(() => {});
			}
		};
		const cancel = () => { finish(); reject(new DOMException('Cancelled; any already saved flags can be recomputed', 'AbortError')); };
		signal.addEventListener('abort', cancel, { once: true });
		worker.onerror = e => { finish(); reject(new Error(e.message || 'Reachability worker failed')); };
		worker.onmessage = (e: MessageEvent<{ progress?: string; scratch?: string; tile?: { geometry: NonNullable<AnalysisResult['geometry']>; key: string }; result?: AnalysisResult; error?: string }>) => {
			if (e.data.tile) {
				const tile = e.data.tile;
				void Promise.resolve().then(() => onTile?.(tile.geometry, tile.key)).then(() => { if (!signal.aborted) worker.postMessage({ previewAck: tile.key }); }).catch(error => { finish(); reject(error); });
				return;
			}
			if (e.data.scratch) { scratch = e.data.scratch; return; }
			if (e.data.progress) { progress(e.data.progress); return; }
			finish();
			if (e.data.result) resolve(e.data.result); else reject(new Error(e.data.error || 'Analysis returned no result'));
		};
		const runtimeUrl = new URL('mesh-engine/_framework/dotnet.js', new URL(import.meta.env.BASE_URL, document.baseURI)).href;
		worker.postMessage({ request, runtimeUrl });
	});
}
