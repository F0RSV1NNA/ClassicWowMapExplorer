import {expect,test} from 'bun:test';
import {transportLayout,transportPreviewPoint,type TransportModel}from '../src/mesh/transport-catalog';
import {editsForTile,newProject}from '../src/mesh/authoring';
import {encodeBakeInput}from '../src/mesh/engine-api';
const model=(fdid:number,w:number,h:number):TransportModel=>({fdid,name:'ship',min:[-h/2,-w/2,-4],max:[h/2,w/2,20]});
test('transport shelves keep asymmetric and oversized models apart',()=>{
 const slots=transportLayout([model(1,400,100),model(2,300,500),model(3,2800,900),model(4,200,80)],12000,7000,1000);
 for(const slot of slots)expect(slot.y+slot.model.min[2]).toBe(100);
 for(let a=0;a<slots.length;a++)for(let b=a+1;b<slots.length;b++){
  const p=slots[a],q=slots[b];
  const overlapX=p.x+p.model.max[1]>q.x+q.model.min[1]&&q.x+q.model.max[1]>p.x+p.model.min[1];
  const overlapZ=p.z+p.model.max[0]>q.z+q.model.min[0]&&q.z+q.model.max[0]>p.z+p.model.min[0];
  expect(overlapX&&overlapZ).toBe(false);
 }
});
test('staged overlay matches Mappster negative-XY local frame with a display-only height offset',()=>{
 // Raw WMO point (10,20,6) is native Mappster (-10,-20,6).
 expect(transportPreviewPoint({x:1000,y:125,z:2000},-10,-20,6)).toEqual([1020,131,2010]);
});
test('whole instance edits include remote floors instead of clipping them to sentinel job tile',()=>{
 const p=newProject('test');p.maps[33]={Roads:[{X:-2000,Y:-3000,Z:24,Radius:2,HeightBand:.75,Kind:0}],OffMesh:[]};
 expect(editsForTile(p,{mapId:33,wdt:1,x:32,y:32,kind:'global-wmo'}).Map.Roads.length).toBe(1);
 expect(editsForTile(p,{mapId:33,wdt:1,x:32,y:32}).Map.Roads.length).toBe(0);
});
test('WMO bake input retains placement as source data and contains no display offset',()=>{
 const wmo={fdid:116306,position:[0,0,0] as [number,number,number],rotation:[0,0,0] as [number,number,number],scale:1,doodadSet:0,transport:true};
 const data=encodeBakeInput({tile:{mapId:116306,wdt:0,x:32,y:32},positions:new Float32Array(),indices:new Uint32Array(),classes:new Uint8Array(),origin:[0,0,0],liquidLayers:0,raw:{files:[],tiles:[],liquidBanks:[],wmo}});
 const v=new DataView(data.buffer),length=v.getInt32(28,true),json=JSON.parse(new TextDecoder().decode(data.subarray(32,32+length)));
 expect(json.Source).toEqual(wmo);expect(json.Source.display).toBeUndefined();
});
