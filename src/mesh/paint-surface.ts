import * as THREE from 'three';

/** Keep the selected connected floor/stairs; tall vertical walls cannot carry paint onto their caps. */
export function floorPaintFaces(faces:readonly THREE.Triangle[],center:THREE.Vector3,radius:number,band:number):number[]{
 const accepted=faces.filter(face=>{const n=face.getNormal(new THREE.Vector3());const h=Math.max(face.a.y,face.b.y,face.c.y)-Math.min(face.a.y,face.b.y,face.c.y);return Math.abs(n.y)>.5||h<=.4;});
 const vertices=new Map<string,number[]>(),keys:string[][]=[];
 for(let i=0;i<accepted.length;i++){const row=[accepted[i].a,accepted[i].b,accepted[i].c].map(v=>[v.x,v.y,v.z].map(n=>Math.round(n*100)).join(':'));keys.push(row);for(const key of row){const list=vertices.get(key)??[];list.push(i);vertices.set(key,list);}}
 let seed=-1,best=.35*.35;for(let i=0;i<accepted.length;i++){const d=accepted[i].closestPointToPoint(center,new THREE.Vector3()).distanceToSquared(center);if(d<best){best=d;seed=i;}}
 if(seed<0)return [];
 const seen=new Set<number>([seed]),queue=[seed];for(let i=0;i<queue.length;i++)for(const key of keys[queue[i]])for(const next of vertices.get(key)!)if(!seen.has(next)){seen.add(next);queue.push(next);}
 const output:number[]=[];
 // Clip on CPU so huge floor triangles don't get rasterized once per brush dab.
 const planes:[THREE.Vector3,number][]=[[new THREE.Vector3(0,1,0),center.y+band],[new THREE.Vector3(0,-1,0),-center.y+band]];
 for(let i=0;i<24;i++){const a=(i+.5)/24*Math.PI*2,n=new THREE.Vector3(Math.cos(a),0,Math.sin(a));planes.push([n,n.dot(center)+radius*Math.cos(Math.PI/24)]);}
 for(const id of queue){let polygon=[accepted[id].a,accepted[id].b,accepted[id].c];for(const [normal,limit] of planes){const clipped:THREE.Vector3[]=[];for(let i=0;i<polygon.length;i++){const a=polygon[i],b=polygon[(i+1)%polygon.length],da=normal.dot(a)-limit,db=normal.dot(b)-limit;if(da<=0)clipped.push(a);if((da<0&&db>0)||(da>0&&db<0))clipped.push(a.clone().lerp(b,da/(da-db)));}polygon=clipped;if(polygon.length<3)break;}
  for(let i=1;i+1<polygon.length;i++)for(const v of [polygon[0],polygon[i],polygon[i+1]])output.push(v.x,v.y,v.z);
 }
 return output;
}
