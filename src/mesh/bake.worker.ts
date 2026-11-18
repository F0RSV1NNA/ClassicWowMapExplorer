import {requestNativeBake} from './native-request';
import { encodeBakeInput, decodeBakeOutput } from './engine-api';
import type { MeshInput } from './types';

self.addEventListener('unhandledrejection', (event) => postMessage({ error: `${String(event.reason?.stack ?? event.reason)}; last stage: ${lastStage}` }));

// Keep onmessage unset: .NET 10 uses it to distinguish standalone workers from its pthread workers.
let lastStage = 'Loading mesh engine';
(self as unknown as { meshBakeStage: (stage: string, managedMB: number) => void }).meshBakeStage = (stage, managedMB) => {
	lastStage = `${stage}; managed heap ${managedMB.toFixed(0)} MB`;
	postMessage({ progress: lastStage });
};
self.addEventListener('message', async (event: MessageEvent<{ input: MeshInput; runtimeUrl: string; lowMemory?: boolean; preferNative?:boolean; inspect?: {x:number;y:number} }>) => {
	let encoded: Uint8Array | undefined;
    try {
		if (!event.data.inspect && event.data.preferNative) {
			let native = false;
			try {
				const capability = await fetch(new URL('/__mesh-native', event.data.runtimeUrl), { cache: 'no-store' });
				if (capability.ok) { const info = await capability.json(); native = info.available === true && info.version === 1; }
			} catch { /* Browser-only hosting retains the low-memory WASM path. */ }
			if (native) {
				lastStage = event.data.lowMemory ? 'Explorer native x64 low-memory recovery' : 'Explorer native x64 bake';
				postMessage({ progress: lastStage }); encoded = encodeBakeInput(event.data.input);
                const reply=await requestNativeBake(new URL('/__mesh-bake',event.data.runtimeUrl),encoded,!!event.data.lowMemory,text=>postMessage({progress:text}));
                const result=decodeBakeOutput(reply.bytes,event.data.input);
                result.nativeRequestStats={transportRetries:reply.transportRetries,queueWaits:reply.queueWaits};
				result.executionBackend = 'native-x64';
				postMessage({ result }, { transfer: Object.values(result.files).map(f => f.buffer) }); return;
			}
		}
		postMessage({ progress: 'Loading mesh engine' });
		const { dotnet } = await import(/* @vite-ignore */ event.data.runtimeUrl);
		postMessage({ progress: 'Starting mesh engine' });
		const runtime = await dotnet.withDiagnosticTracing(false).create();
		postMessage({ progress: 'Preparing mesh generation' });
		const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
		postMessage({ progress: 'Baking navigation mesh' });
		encoded = encodeBakeInput(event.data.input);
		if(event.data.inspect){postMessage({inspection:JSON.parse(exports.MeshEngine.InspectRoad(encoded,event.data.inspect.x,event.data.inspect.y))});return;}
		const bytes = event.data.input.raw ? event.data.lowMemory ? exports.MeshEngine.BakeRawLowMemory(encoded) : exports.MeshEngine.BakeRaw(encoded) : exports.MeshEngine.Bake(encoded);
		const result = decodeBakeOutput(bytes, event.data.input);
		const transfer = Object.values(result.files).map((f) => f.buffer);
		postMessage({ result }, { transfer });
	} catch (e) { postMessage({ error: `${(e as Error).message}; last stage: ${lastStage}`,failedSource:encoded },{transfer:encoded?[encoded.buffer]:[]}); }
});
