import type { MeshBake, MeshInput } from './types';
export interface RoadInspection { road:boolean; layers:{id:number;name:string|null;weight:number;automatic:boolean}[] }

/** Each job owns its WASM heap. Termination cancels immediately and releases that heap. */
export function runBake(input: MeshInput, signal: AbortSignal, onProgress: (stage: string) => void = () => {}, lowMemory = false): Promise<MeshBake> {
    return runJob<MeshBake>(input,signal,onProgress,lowMemory);
}
export function runRoadInspection(input: MeshInput, x:number,y:number,signal:AbortSignal):Promise<RoadInspection>{return runJob<RoadInspection>(input,signal,()=>{},false,{x,y});}
function runJob<T>(input:MeshInput,signal:AbortSignal,onProgress:(stage:string)=>void,lowMemory:boolean,inspect?:{x:number;y:number}):Promise<T>{
	return new Promise((resolve, reject) => {
		if (signal.aborted) { reject(new DOMException('Bake cancelled', 'AbortError')); return; }
		const worker = new Worker(new URL('./bake.worker.ts', import.meta.url), { type: 'module' });
		let lastProgress = 'Starting bake worker';
		const finish = () => { signal.removeEventListener('abort', cancel); worker.terminate(); };
		const cancel = () => { finish(); reject(new DOMException('Bake cancelled', 'AbortError')); };
		signal.addEventListener('abort', cancel, { once: true });
		worker.onerror = (e) => { finish(); reject(new Error(`${e.message || 'Bake worker failed'}; last stage: ${lastProgress}`)); };
		worker.onmessage = (e: MessageEvent<{ result?: T; inspection?:T; error?: string; progress?: string }>) => {
			if (e.data.progress) { lastProgress = e.data.progress; onProgress(e.data.progress); return; }
			finish();
			if (e.data.result || e.data.inspection) resolve((e.data.result??e.data.inspection)!);
			else reject(new Error(e.data.error || 'Bake worker returned no result'));
		};
		const transfer = [input.positions.buffer, input.indices.buffer, input.classes.buffer];
		if (input.liquids) transfer.push(input.liquids.positions.buffer, input.liquids.indices.buffer, input.liquids.kinds.buffer);
		if (input.raw) transfer.push(...input.raw.files.map((f) => f.bytes.buffer));
		const runtimeUrl = new URL('mesh-engine/_framework/dotnet.js', new URL(import.meta.env.BASE_URL, document.baseURI)).href;
		worker.postMessage({ input, runtimeUrl, lowMemory, inspect }, transfer);
	});
}
