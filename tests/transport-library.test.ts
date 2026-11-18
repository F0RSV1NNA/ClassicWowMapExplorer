import {expect,test} from 'bun:test';
import {selectTransportLibrary} from '../src/mesh/transport-library';
import type {CascStorage} from '../src/casc/storage';
import type {ProductInfo} from '../src/casc/config';
import models from '../src/mesh/transport-models.json';
const roots=models.filter(m=>!/_\d{3}(?:_lod\d+)?\.wmo$/i.test(m.name));
const storage=(product:string,count:number)=>({stats:{product},status:(id:number)=>roots.slice(0,count).some(m=>m.fdid===id)?'ok':'unknown'}) as CascStorage;
const products=(...names:string[])=>async()=>names.map(product=>({product})) as ProductInfo[];

test('Classic world and complete Retail transport assets remain separate sources',async()=>{
 const world=storage('wow_classic_beta',10),retail=storage('wow',95),opened:string[]=[];
 const result=await selectTransportLibrary(world,products('wow_classic_beta','wow'),async p=>{opened.push(p);return retail;});
 expect(result.storage).toBe(retail);expect(world.stats.product).toBe('wow_classic_beta');
 expect(world.status(116306)).toBe('ok');expect(world.status(2438938)).toBe('unknown');
 expect(result.storage.status(2438938)).toBe('ok');expect(opened).toEqual(['wow']);
 expect(result.candidates).toEqual([{product:'wow_classic_beta',mapped:10},{product:'wow',mapped:95}]);
});

test('released wowf wins over the stale beta when it has equal or better coverage',async()=>{
 const world=storage('wow_classic_beta',10),released=storage('wowf',10);
 const result=await selectTransportLibrary(world,products('wowf'),async()=>released);
 expect(result.storage).toBe(released);expect(result.reason).toContain('wowf');
});

test('Retail is not chosen merely because its name is wow when Forever has more models',async()=>{
 const world=storage('wowf',95),retail=storage('wow',10);
 const result=await selectTransportLibrary(world,products('wow','wowf'),async()=>retail);
 expect(result.storage).toBe(world);
});

test('an unavailable secondary build preserves the world and reports its failure',async()=>{
 const world=storage('wow_classic_beta',10);
 const result=await selectTransportLibrary(world,products('wow'),async()=>{throw Error('missing encoding');});
 expect(result.storage).toBe(world);expect(result.error).toContain('wow: missing encoding');
});

test('missing installed reference products do not trigger unpinned online build substitution',async()=>{
 const world=storage('wowf',10);
 const result=await selectTransportLibrary(world,products('wowf'),async()=>{throw Error('must not open');});
 expect(result.storage).toBe(world);expect(result.error).toBeUndefined();
});
