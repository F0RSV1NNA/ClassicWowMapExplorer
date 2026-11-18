import {MeshSource}from '../src/mesh/source';
import type {MapExplorer}from '../src/explorer/maps';
import {expect,test}from 'bun:test';
import {EncryptedError,StorageChangedError}from '../src/casc/storage';
import {transportModels,transportCatalog,transportInventorySummary,isTransportRootName}from '../src/mesh/transport-catalog';
const chunk=(tag:string,data:Buffer)=>{const h=Buffer.alloc(8);h.write(tag.split('').reverse().join(''));h.writeUInt32LE(data.length,4);return Buffer.concat([h,data]);};
function fixture(){
 const header=Buffer.alloc(64);header.writeUInt32LE(1,4);[-20,-10,0,20,10,15].forEach((n,i)=>header.writeFloatLE(n,36+i*4));
 const id=Buffer.alloc(4);id.writeUInt32LE(2);const root=Buffer.concat([chunk('MOHD',header),chunk('GFID',id)]);
 const db=Buffer.alloc(208);db.write('WDC5');
 const vertices=Buffer.alloc(36);[10,20,6,50,20,6,50,45,6].forEach((n,i)=>vertices.writeFloatLE(n,i*4));
 const indices=Buffer.alloc(6);[0,1,2].forEach((n,i)=>indices.writeUInt16LE(n,i*2));
 const group=chunk('MOGP',Buffer.concat([Buffer.alloc(68),chunk('MOVT',vertices),chunk('MOVI',indices),chunk('MOPY',Buffer.from([8,0]))]));
 const files=new Map([[116306,root],[2,group]]);
 const reads:number[]=[];
 const storage={stats:{product:'test',version:'1',buildName:'fixture'},status:(id:number)=>files.has(id)?'ok':'unknown',readFile:async(id:number)=>{reads.push(id);const b=files.get(id);if(!b)throw Error('missing '+id);return b;},readFileWithStatus:async()=>({data:db})};
 const wmo={fdid:116306,position:[3000,12,2000],rotation:[0,0,0],scale:1,doodadSet:0,nameSet:0,min:[0,0,0],max:[10,10,10]};
 const maps={storage,wdt:async()=>({tiles:[],globalWmo:wmo})}as unknown as MapExplorer;
 return {maps,reads};
}
test('global WMO inventory is one complete-instance job and extracts model dependencies without ADTs',async()=>{
 const {maps,reads}=fixture(),source=new MeshSource(maps);
 const jobs=await source.tiles(0);expect(jobs).toEqual([{mapId:0,wdt:775971,x:32,y:32,kind:'global-wmo'}]);
 const input=await source.extract(jobs[0]);
 expect(input.raw!.tiles).toEqual([]);expect(input.raw!.wmo!.position).toEqual([3000,12,2000]);expect(input.raw!.wmo!.transport).toBe(false);
 expect(reads).toContain(116306);expect(reads).toContain(2);
});
test('transport extraction uses zero placement and FDID identity independently of world WDTs',async()=>{
 const {maps}=fixture();maps.wdt=async()=>{throw Error('transport must not read a WDT');};
 const input=await new MeshSource(maps).transport(116306);
 expect(input.tile.mapId).toBe(116306);expect(input.raw!.wmo).toEqual({fdid:116306,position:[0,0,0],rotation:[0,0,0],scale:1,doodadSet:0,transport:true});
 expect(input.raw!.files.map(f=>f.id)).toEqual([116306,2]);
});
test('transport catalog filters absent roots and preserves actual model bounds',async()=>{
 const {maps}=fixture();const models=await transportModels(maps.storage);
 expect(models).toHaveLength(1);expect(models[0].fdid).toBe(116306);expect(models[0].min).toEqual([-20,-10,0]);expect(models[0].max).toEqual([20,10,15]);
});

test('instance collision preview retains geometry outside the sentinel terrain tile',async()=>{
 const {maps}=fixture(),source=new MeshSource(maps),jobs=await source.tiles(0);
 const input=await source.extract(jobs[0],true);
 expect(input.indices.length).toBe(3);
 // Relative Recast order is native (Y,Z,X), with the WDT placement retained.
 expect(input.positions[0]).toBeCloseTo(-3020,2);expect(input.positions[1]).toBeCloseTo(18,3);expect(input.positions[2]).toBeCloseTo(-2010,2);
});

test('group and LOD files including reported 1497960 are never treated as roots',()=>{
 expect(isTransportRootName('transport_zeppelin/transport_zeppelin_000_lod1.wmo')).toBe(false);
 expect(isTransportRootName('transportship_001.wmo')).toBe(false);
 expect(isTransportRootName('transportship_001_lod12.WMO')).toBe(false);
 expect(isTransportRootName('wmo_elevators/7wd_warden_bridge02_transport.wmo')).toBe(true);
});
test('invalid, truncated and unreadable candidates do not discard valid transport roots',async()=>{
 const {maps}=fixture(),root=await maps.storage.readFile(116306),reads:number[]=[],skips:number[]=[];
 const badBounds=Buffer.from(root);badBounds.writeFloatLE(NaN,8+36);
 const present=new Set([116298,116300,116306,116310,116315,116317,1497960]);
 maps.storage.status=(id)=>present.has(id)?'ok':'unknown';
 maps.storage.readFile=async(id)=>{
  reads.push(id);if(id===116300)throw Error('unavailable encrypted file');
  if(id===116298)return chunk('MOGP',Buffer.alloc(68));
  if(id===116310)return badBounds;
  if(id===116315)return root.subarray(0,35);
  return root;
 };
 const models=await transportModels(maps.storage,(id)=>skips.push(id));
 expect(models.map(m=>m.fdid)).toEqual([116306,116317]);
 expect(reads).not.toContain(1497960);
 expect(skips).toEqual([116298,116300,116310,116315]);
});
test('catalog does not mask a changed game-data source as an optional missing model',async()=>{
 const {maps}=fixture();maps.storage.readFile=async()=>{throw new StorageChangedError('fixture');};
 await expect(transportModels(maps.storage,()=>{})).rejects.toThrow('game was updated');
});

test('inventory distinguishes files absent from the build, non-local files and encrypted files',async()=>{
 const {maps}=fixture(),read=maps.storage.readFile.bind(maps.storage);
 maps.storage.status=(id)=>id===116298?'not-local':id===116300?'no-encoding':id===116306||id===116310?'ok':'unknown';
 maps.storage.readFile=async(id)=>{if(id===116310)throw new EncryptedError('test-key');return read(id);};
 const report=await transportCatalog(maps.storage,()=>{});
 expect(report.models.map(m=>m.fdid)).toEqual([116306]);expect(report.entries).toHaveLength(143);
 expect(report.entries.find(m=>m.fdid===116298)?.state).toBe('not-local');
 expect(report.entries.find(m=>m.fdid===116300)?.state).toBe('no-encoding');
 expect(report.entries.find(m=>m.fdid===116310)?.state).toBe('encrypted');
 expect(report.entries.find(m=>m.fdid===1497960)?.state).toBe('group-lod');
 expect(transportInventorySummary(report)).toContain('1 placed / 95 root candidates');
 expect(report.sourceBuild.product).toBe('test');
});

test('transport catalog attempts a non-local root when CDN retrieval is available',async()=>{
 const {maps}=fixture();Object.defineProperty(maps.storage,'remoteAvailable',{value:true});
 maps.storage.status=(id)=>id===116306?'not-local':'unknown';
 const report=await transportCatalog(maps.storage,()=>{});
 expect(report.models.map(m=>m.fdid)).toEqual([116306]);expect(report.entries.find(e=>e.fdid===116306)?.state).toBe('ready');
});
