import { mkdir,readFile,writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { unzipSync } from 'fflate';
import { encodeBakeInput } from '../src/mesh/engine-api';
import type { MeshInput,WmoBakeSource } from '../src/mesh/types';
const dir='.cache/wmo-transport';await mkdir(dir,{recursive:true});
const chunk=(tag:string,bytes:Buffer)=>{const h=Buffer.alloc(8);h.write(tag.split('').reverse().join(''));h.writeUInt32LE(bytes.length,4);return Buffer.concat([h,bytes]);};
const mohd=Buffer.alloc(64);mohd.writeUInt32LE(1,4);
[10,20,6,50,45,6].forEach((n,i)=>mohd.writeFloatLE(n,36+i*4));
const gfid=Buffer.alloc(4);gfid.writeUInt32LE(900002);
const root=Buffer.concat([chunk('MOHD',mohd),chunk('GFID',gfid)]);
const movt=Buffer.alloc(4*12);[10,20,6,50,20,6,50,45,6,10,45,6].forEach((n,i)=>movt.writeFloatLE(n,i*4));
const movi=Buffer.alloc(12);[0,1,2,0,2,3].forEach((n,i)=>movi.writeUInt16LE(n,i*2));
const header=Buffer.alloc(68);header.writeUInt32LE(8,8);
const group=chunk('MOGP',Buffer.concat([header,chunk('MOVT',movt),chunk('MOVI',movi),chunk('MOPY',Buffer.from([8,0,8,0]))]));
const make=(mapId:number,wmo:WmoBakeSource):MeshInput=>({tile:{mapId,wdt:0,x:32,y:32,kind:wmo.transport?undefined:'global-wmo'},positions:new Float32Array(),indices:new Uint32Array(),classes:new Uint8Array(),origin:[0,0,0],liquidLayers:0,raw:{files:[{id:wmo.fdid,bytes:root},{id:900002,bytes:group}],tiles:[],liquidBanks:[],wmo}});
const cases=[{name:'transport',input:make(116306,{fdid:116306,position:[0,0,0],rotation:[0,0,0],scale:1,doodadSet:0,transport:true}),centre:[-30,-32.5,6]},
{name:'instance',input:make(33,{fdid:900001,position:[3000,12,2000],rotation:[0,0,0],scale:2,doodadSet:0,transport:false}),centre:[-2060,-3065,24]}];
const crossing=make(116306,{fdid:116306,position:[0,0,0],rotation:[0,0,0],scale:1,doodadSet:0,transport:true});
const centred=Buffer.from(group);
// MOVT begins after MOGP chunk+header and MOVT subchunk header.
[-20,-10,6,20,-10,6,20,10,6,-20,10,6].forEach((n,i)=>centred.writeFloatLE(n,84+i*4));
crossing.raw!.files[1].bytes=centred;
cases.push({name:'crossing-deck',input:crossing,centre:[1,1,6]});
const evidence:any[]=[];
for(const c of cases){
 const path=`${dir}/${c.name}.input`;await writeFile(path,encodeBakeInput(c.input));
 for(const backend of ['native','wasm']){
  const args=backend==='native'?['run','--project','tools/mesh-native-check/check.csproj','-c','Release','--',path,`${dir}/${c.name}.${backend}.zip`]:['tools/checkMeshWasm.mjs',path,`${dir}/${c.name}.${backend}.zip`];
  const cmd=backend==='native'?'dotnet':'node';const r=spawnSync(cmd,args,{encoding:'utf8'});
  await writeFile(`${dir}/${c.name}.${backend}.log`,r.stdout+r.stderr);
  if(r.status!==0)throw Error(`${c.name} ${backend} exit ${r.status}: ${r.stdout}${r.stderr}`);
 }
 const n=unzipSync(await readFile(`${dir}/${c.name}.native.zip`)),w=unzipSync(await readFile(`${dir}/${c.name}.wasm.zip`));
 const hashes:Record<string,string>={};
 for(const [name,bytes]of Object.entries(n))if(name!=='engine.json'){
  if(!w[name]||!Buffer.from(bytes).equals(Buffer.from(w[name])))throw Error(`${c.name}: native/WASM differ: ${name}`);
  hashes[name]=createHash('sha256').update(bytes).digest('hex');
 }
 const view=new DataView(n['preview.bin'].buffer,n['preview.bin'].byteOffset,n['preview.bin'].byteLength),count=view.getInt32(0,true);
 if(!count)throw Error('Empty bake');let hit=false;
 for(let t=0;t<count;t++){
  const pts=Array.from({length:3},(_,k)=>Array.from({length:3},(_,axis)=>view.getFloat32(8+t*40+k*12+axis*4,true)));
  if(pts.every(p=>Math.abs(p[2]-c.centre[2])<.6)){
   const [a,b,d]=pts, [x,y]=c.centre;
   const cross=(p:number[],q:number[]) => (q[0]-p[0])*(y-p[1])-(q[1]-p[1])*(x-p[0]);
   const s=[cross(a,b),cross(b,d),cross(d,a)];if(s.every(v=>v>=-.001)||s.every(v=>v<=.001))hit=true;
  }
 }
 if(!hit)throw Error(`${c.name}: expected native centre not covered ${c.centre}`);
 if(c.name==='crossing-deck'&&Object.keys(n).filter(f=>f.endsWith('.mmtile')).length!==4)throw Error('Origin-crossing deck must emit all four local tiles');
 if(c.name==='instance'&&Object.keys(n).filter(f=>f.endsWith('.mmtile')).some(f=>f.includes('_32_32')))throw Error('Instance incorrectly clipped to input tile');
 evidence.push({case:c.name,exit:0,centre:c.centre,hashes});console.log(c.name,'PASS',Object.keys(hashes));
}
// Fail closed on a misplaced transport and missing collision dependencies.
for(const [name,mutate]of [['placed-transport',(i:MeshInput)=>{i.raw!.wmo!.position=[1,0,0];}],['missing-group',(i:MeshInput)=>{i.raw!.files.pop();}]] as const){
 const i=structuredClone(cases[0].input);mutate(i);const file=`${dir}/${name}.input`;await writeFile(file,encodeBakeInput(i));
 const r=spawnSync('dotnet',['run','--no-build','--project','tools/mesh-native-check/check.csproj','-c','Release','--',file,`${dir}/${name}.zip`],{encoding:'utf8'});
 if(r.status===0)throw Error(`${name} unexpectedly succeeded`);
 evidence.push({case:name,expectedFailure:true,exit:r.status,output:r.stdout+r.stderr});console.log(name,'correctly rejected',r.status);
}
await writeFile(`${dir}/evidence.json`,JSON.stringify(evidence,null,2));
