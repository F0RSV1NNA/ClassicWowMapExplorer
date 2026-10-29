import type { MeshInput, MeshQuality } from './types';
import provenance from './engine/provenance.json?raw';
import { editFingerprint } from './authoring';

const engineProvenance = JSON.parse(provenance);
export const ENGINE_SOURCE = JSON.stringify({ core: engineProvenance.files, adapters: engineProvenance.adapterFiles });

export function tileMetadata(input: MeshInput, product: string) {
	return {
		format: 'map-explorer-mappster', version: 3, product,
		sourceBuild: input.sourceBuild, engineSource: ENGINE_SOURCE, files: null as Record<string, number> | null,
		authoringFingerprint: editFingerprint(input.edits),
		tile: input.tile, origin: input.origin, coordinateSystem: 'local-native-Y-Z-X',
		settings: null as Record<string, unknown> | null, liquidLayers: input.liquidLayers,
		performance: null as { sourceMs: number; bakeMs: number } | null,
		lowMemoryRecovery: false,
		executionBackend: 'wasm' as 'wasm' | 'native-x64',
		pipeline: 'explorer-recast-2026.3.8', quality: null as MeshQuality | null,
		liquidFormat: 'MLIQ v2; 1-yard coverage grid, highest surface per column; not yet verified in CURSED',
		liquidClassification: 'LiquidType.SoundBank resolved through pinned WoWDBDefs layout hash; unknown layouts/types fail',
		limitations: ['Transport-local baking is not implemented', 'Portal/elevator links require a runtime action consumer', 'Real-map output still needs visual acceptance against Mappster'],
	};
}
