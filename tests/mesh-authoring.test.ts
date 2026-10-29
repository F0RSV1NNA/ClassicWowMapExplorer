import { expect, test } from 'bun:test';
import { dab, EditHistory, editsForTile, editFingerprint, emptyEdits, newProject, readProject } from '../src/mesh/authoring';
import { encodeBakeInput } from '../src/mesh/engine-api';
import type { MeshInput } from '../src/mesh/types';

test('stroke fingerprints include affected tile borders and ignore distant maps/tiles',()=>{
	const p=newProject('test');p.maps[0]=emptyEdits();p.maps[0].Roads.push(dab({mapId:0,x:-532,y:-100,z:12},3,.5,0));
	expect(editFingerprint(editsForTile(p,{mapId:0,wdt:1,x:32,y:32}))).not.toBe('');
	expect(editFingerprint(editsForTile(p,{mapId:0,wdt:1,x:32,y:33}))).not.toBe('');
	expect(editFingerprint(editsForTile(p,{mapId:0,wdt:1,x:40,y:40}))).toBe('');
	expect(editFingerprint(editsForTile(p,{mapId:1,wdt:1,x:32,y:32}))).toBe('');
	p.global.RoadOff.push(123);expect(editFingerprint(editsForTile(p,{mapId:1,wdt:1,x:32,y:32}))).not.toBe('');
});
test('a drag is one undo operation and a new edit discards redo',()=>{
	const h=new EditHistory(newProject('test')),next=structuredClone(h.project);next.maps[0]=emptyEdits();
	for(let i=0;i<5;i++)next.maps[0].Roads.push(dab({mapId:0,x:i,y:0,z:0},2,.75,0));
	h.commit(next);h.undo();expect(h.project.maps[0]).toBeUndefined();h.redo();expect(h.project.maps[0].Roads).toHaveLength(5);
	h.undo();const other=structuredClone(h.project);other.global.RoadOn.push(17);h.commit(other);expect(h.canRedo).toBe(false);
});
test('portable edits validate product, height reach and stable connection IDs',()=>{
	const p=newProject('test');p.maps[0]={Roads:[dab({mapId:0,x:1,y:2,z:3},2,.5,3)],OffMesh:[]};
	expect(readProject(JSON.stringify(p),'test')).toEqual(p);
	expect(()=>readProject(JSON.stringify(p),'other')).toThrow();
	p.maps[0].Roads[0].HeightBand=Infinity;expect(()=>readProject(JSON.stringify(p),'test')).toThrow();
});
test('authoring tail is explicit and preserves legacy geometry input when absent',()=>{
	const input:MeshInput={tile:{mapId:0,wdt:1,x:32,y:32},origin:[0,0,0],positions:new Float32Array(),indices:new Uint32Array(),classes:new Uint8Array(),liquidLayers:0};
	const legacy=encodeBakeInput(input);input.edits={Map:emptyEdits(),Global:{RoadOn:[123],RoadOff:[]}};
	const encoded=encodeBakeInput(input);expect(Array.from(encoded.slice(0,legacy.length))).toEqual(Array.from(legacy));
	const length=new DataView(encoded.buffer).getInt32(legacy.length,true);expect(encoded.length).toBe(legacy.length+4+length);
	expect(JSON.parse(new TextDecoder().decode(encoded.subarray(legacy.length+4)))).toEqual(input.edits);
});
