import type { MeshInput, MeshQuality } from './types';
import provenance from './engine/provenance.json?raw';
import { editFingerprint } from './authoring';

const engineProvenance = JSON.parse(provenance);
const safeCoreUpgrades=engineProvenance.successfulTileCompatibility??{};
export const ENGINE_SOURCE = JSON.stringify({ core: engineProvenance.files, adapters: engineProvenance.adapterFiles });

/** Native runner scheduling/extraction mode changes do not change geometry identity.
 * Every Core/Program/extraction setting remains part of the comparison. */
export function sameMeshEngineSource(left:unknown,right:unknown):boolean {
    if(left===right)return typeof left==='string';
    if(typeof left!=='string'||typeof right!=='string')return false;
    try{
        const normalize=(text:string)=>{
            const value=JSON.parse(text);
            if(!value.core||!value.adapters)throw Error('Missing engine identity');
            delete value.adapters['../engine-native/Runner.cs'];
            for(const [file,upgrade]of Object.entries(safeCoreUpgrades)as [string,{from:string;to:string}][])
                if(engineProvenance.files[file]===upgrade.to&&value.core[file]===upgrade.from)value.core[file]=upgrade.to;
            return JSON.stringify(value);
        };
        return normalize(left)===normalize(right);
    }catch{return false;}
}

export function tileMetadata(input: MeshInput, product: string) {
	return {
		format: 'map-explorer-mappster', version: 3, product,
		sourceBuild: input.sourceBuild, engineSource: ENGINE_SOURCE, files: null as Record<string, number> | null,
		authoringFingerprint: editFingerprint(input.edits),
		tile: input.tile, origin: input.origin, coordinateSystem: input.raw?.wmo?.transport ? 'mappster-modf-180-local' : 'local-native-Y-Z-X',
		settings: null as Record<string, unknown> | null, liquidLayers: input.liquidLayers,
		performance: null as { sourceMs: number; bakeMs: number } | null,
		lowMemoryRecovery: false,
		executionBackend: 'wasm' as 'wasm' | 'native-x64',
        nativeRequestStats:undefined as {transportRetries:number;queueWaits:number}|undefined,
		pipeline: 'explorer-recast-2026.3.19', quality: null as MeshQuality | null,
		liquidFormat: 'MLIQ v2; 1-yard coverage grid, highest surface per column',
		liquidClassification: 'LiquidType.SoundBank resolved through pinned WoWDBDefs layout hash; unknown layouts/types fail',
		limitations: ['Complete dataset still requires Cursed runtime acceptance'],
	};
}
