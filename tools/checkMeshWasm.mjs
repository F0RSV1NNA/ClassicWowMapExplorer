// Focused runtime check: execute the actual browser WebAssembly under Node, not a JS mock.
import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { unzipSync, zipSync, strToU8 } from 'fflate';
const [input, output, mode] = process.argv.slice(2);
if (!input || !output) throw Error('Expected input and output paths');
const url = pathToFileURL(resolve('public/mesh-engine/_framework/dotnet.js'));
const { dotnet } = await import(url.href);
const runtime = await dotnet.withDiagnosticTracing(false).create();
const api = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
console.log(api.MeshEngine.Version());
const bytes = new Uint8Array(await readFile(input));
let result = new DataView(bytes.buffer).getUint32(0, true) === 0x32524D45 ? api.MeshEngine.BakeRaw(bytes) : api.MeshEngine.Bake(bytes);
if (mode === 'reachability') {
	const files = unzipSync(result), engine = api.MeshEngine;
	engine.BeginAnalysis(0);
	const names = Object.keys(files).filter(n => /\.(mmap|mmtile|mliq)$/.test(n));
	for (const name of names) engine.PutAnalysisFile(name, files[name]);
	const first = JSON.parse(engine.Analyze(1000));
	const preserved = engine.Analyze(1);
	const report = engine.Analyze(1000);
	// Rewritten is a write counter, not part of the connectivity verdict.
	const comparable = r => JSON.stringify({ ...r, Rewritten: 0 });
	if (comparable(first) !== comparable(JSON.parse(report))) throw Error('Reachability recomputation differs');
	const flagged = Object.fromEntries(names.map(n => [n, engine.GetAnalysisFile(n)]));
	flagged['flagged-preview.bin'] = engine.PreviewAnalysisTile(32, 32);
	flagged['report.json'] = strToU8(report); flagged['preserved.json'] = strToU8(preserved);
	result = zipSync(flagged); engine.EndAnalysis();
}
await writeFile(output, result);
console.log(`Actual WASM bake: ${result.byteLength} bytes`);
