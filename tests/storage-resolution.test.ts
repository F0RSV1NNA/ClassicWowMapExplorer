import {expect,test}from 'bun:test';
import {CascStorage,EncryptedError}from '../src/casc/storage';
const key=(n:number)=>new Uint8Array(16).fill(n);
function fixture(contents:Uint8Array[],encodings:Map<Uint8Array,Uint8Array[]>,local:Set<Uint8Array>){
 const reads:Uint8Array[]=[];
 const storage=Reflect.construct(CascStorage,[
  {index:{find:(k:Uint8Array)=>local.has(k)?{archive:0,offset:0,size:1}:null},read:async(k:Uint8Array)=>{reads.push(k);return {data:new Uint8Array([k[0]]),encryptedKeys:[]};}},
  {lookupAll:(k:Uint8Array)=>encodings.get(k)??[]},
  {getContentKeys:()=>contents},{}
 ])as CascStorage;
 return {storage,reads};
}
test('file presence and reads use an installed alternate encoding when the first is absent',async()=>{
 const c=key(1),first=key(2),installed=key(3),{storage,reads}=fixture([c],new Map([[c,[first,installed]]]),new Set([installed]));
 expect(storage.status(116306)).toBe('ok');expect(await storage.readFile(116306)).toEqual(new Uint8Array([3]));expect(reads).toEqual([installed]);
});
test('file lookup searches later content variants as well as alternate encodings',async()=>{
 const a=key(1),b=key(2),missing=key(3),installed=key(4),{storage,reads}=fixture([a,b],new Map([[a,[missing]],[b,[missing,installed]]]),new Set([installed]));
 expect(storage.status(116306)).toBe('ok');await storage.readFile(116306);expect(reads).toEqual([installed]);
});
test('missing encoding and absent local data remain distinct inventory states',()=>{
 const c=key(1),e=key(2);
 expect(fixture([],new Map(),new Set()).storage.status(1)).toBe('unknown');
 expect(fixture([c],new Map(),new Set()).storage.status(1)).toBe('no-encoding');
 expect(fixture([c],new Map([[c,[e]]]),new Set()).storage.status(1)).toBe('not-local');
});

test('an unavailable decryption key on the first local encoding does not hide a readable alternate',async()=>{
 const c=key(1),a=key(2),b=key(3),reads:number[]=[];
 const storage=Reflect.construct(CascStorage,[
  {index:{find:()=>({})},read:async(k:Uint8Array)=>{reads.push(k[0]);if(k[0]===2)throw new EncryptedError('unknown');return {data:new Uint8Array([99]),encryptedKeys:[]};}},
  {lookupAll:()=>[a,b]},{getContentKeys:()=>[c]},{}
 ])as CascStorage;
 expect(await storage.readFile(116306)).toEqual(new Uint8Array([99]));expect(reads).toEqual([2,3]);
});
