import {test,expect} from 'bun:test';
import * as THREE from 'three';
import {floorPaintFaces} from '../src/mesh/paint-surface';
const v=(x:number,y:number,z:number)=>new THREE.Vector3(x,y,z);
const face=(a:number[],b:number[],c:number[])=>new THREE.Triangle(v(...a as [number,number,number]),v(...b as [number,number,number]),v(...c as [number,number,number]));
test('paint follows connected stairs but cannot climb a tall fence or jump to another floor',()=>{
 const faces=[
 face([0,0,0],[1,0,0],[0,0,1]),face([1,0,0],[1,0,1],[0,0,1]),
 // Stair riser and tread: short risers keep real steps connected.
 face([1,0,0],[1,.25,0],[1,0,1]),face([1,.25,0],[1,.25,1],[1,0,1]),
 face([1,.25,0],[2,.25,0],[1,.25,1]),face([2,.25,0],[2,.25,1],[1,.25,1]),
 // Tall fence wall and cap share vertices, but the wall must not carry paint upward.
 face([0,0,0],[0,1,0],[0,0,1]),face([0,1,0],[0,1,1],[0,0,1]),
 face([0,1,0],[-.1,1,0],[0,1,1]),
 face([0,1.8,0],[1,1.8,0],[0,1.8,1])];
 const result=floorPaintFaces(faces,v(.25,0,.5),3,2),heights=result.filter((_,i)=>i%3===1);
 expect(heights).toContain(.25);expect(heights).not.toContain(1);expect(heights).not.toContain(1.8);
});
test('paint clips large source triangles to the brush without a resampled grid',()=>{
 const result=floorPaintFaces([face([-100,0,-100],[100,0,-100],[0,0,100])],v(0,0,0),2,.75);
 expect(result.length).toBeGreaterThan(0);
 for(let i=0;i<result.length;i+=3){expect(Math.hypot(result[i],result[i+2])).toBeLessThanOrEqual(2.00001);expect(result[i+1]).toBe(0);}
 expect(floorPaintFaces([face([0,3,0],[1,3,0],[0,3,1])],v(0,0,0),3,.75)).toEqual([]);
});
