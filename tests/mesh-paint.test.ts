import {test,expect,mock} from 'bun:test';
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

test('prop collision is both pickable and paintable at its transformed surface height',async()=>{
 mock.module('../src/viewer/perf',()=>({perf:{}}));
 mock.module('../src/viewer/audioOut',()=>({setHeard(){},audioContext(){return null;},newAudio(){},setLevel(){},stopAudio(){}}));
 const {ObjectManager}=await import('../src/viewer/objects');
 const {MeshBVH}=await import('three-mesh-bvh');
 const geometry=new THREE.BufferGeometry().setAttribute('position',new THREE.Float32BufferAttribute([-2,2,-2, 0,2,2, 2,2,-2],3));
 geometry.boundsTree=new MeshBVH(geometry);
 const material=new THREE.MeshBasicMaterial({side:THREE.DoubleSide});
 const mesh=new THREE.Mesh(geometry,material),matrix=new THREE.Matrix4().makeTranslation(10,5,0);
 const manager=Object.create(ObjectManager.prototype) as InstanceType<typeof ObjectManager>;
 Object.assign(manager,{models:new Map([[1,{kind:'m2',collider:{geometry,mesh,matrices:[matrix],spheres:[10,7,0,3]}}]]),sweepRay:new THREE.Raycaster(),hitNormal:new THREE.Vector3()});
 const decoration=new THREE.BufferGeometry().setAttribute('position',new THREE.Float32BufferAttribute([8,9,-2, 10,9,2, 12,9,-2],3));
 decoration.boundsTree=new MeshBVH(decoration);
 const decorativeMesh=new THREE.InstancedMesh(decoration,material,1);decorativeMesh.updateMatrixWorld();
 Object.assign(manager,{models:new Map([[1,{kind:'m2',collider:{geometry,mesh,matrices:[matrix],spheres:[10,7,0,3]}}],[2,{kind:'m2',geometry:decoration,mesh:decorativeMesh,grounds:[]} ]])});
 const hit=manager.castBuildings(v(10,10,0),v(0,-1,0),10,true,true);
 expect(hit?.distance).toBeCloseTo(3,5);
 const faces:THREE.Triangle[]=[];
 manager.paintSurfaces(new THREE.Box3(v(9,6.5,-1),v(11,7.5,1)),(a,b,c)=>faces.push(new THREE.Triangle(a,b,c)));
 expect(faces.length).toBe(1);
 // Nearby terrain must not steal the selected prop's paint seed.
 faces.push(face([8,6.8,-2],[10,6.8,2],[12,6.8,-2]));
 const painted=floorPaintFaces(faces,v(10,7,0),.75,.75);
 expect(painted.length).toBeGreaterThan(0);
 for(let i=1;i<painted.length;i+=3)expect(painted[i]).toBe(7);
 geometry.dispose();decoration.dispose();material.dispose();
});
