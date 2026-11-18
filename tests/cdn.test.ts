import {expect,test}from 'bun:test';
import {createHash}from 'node:crypto';
import {CdnReader,findCdnEntry,verifyCdnEncoding,allowedCdnHost,cdnFetch}from '../src/casc/cdn';
import {md5}from '../src/casc/md5';
import type {FileSource}from '../src/casc/source';
const encoded=new Uint8Array([0x42,0x4c,0x54,0x45,0,0,0,0,0x4e,1,2,3]);
const key=new Uint8Array(createHash('md5').update(encoded).digest());
const archive='a'.repeat(32),group='b'.repeat(32),config='c'.repeat(32);
function index(offsetBytes=6){
 const b=new Uint8Array(4096+20),v=new DataView(b.buffer);b.set(key);v.setUint32(16,encoded.length);if(offsetBytes===6)v.setUint16(20,0);v.setUint32(20+offsetBytes-4,1234);
 b.set([1,0,0,4,offsetBytes,4,16,8],4096);v.setUint32(4104,1,true);return b;
}
const info={product:'wow_classic_beta',version:'pinned',branch:'',active:true,buildKey:'d'.repeat(32),cdnKey:config,cdnPath:'tpr/wow',cdnHosts:['level3.blizzard.com']};
const file=(bytes:Uint8Array)=>({size:bytes.length,read:async(o:number,n:number)=>bytes.subarray(o,o+n)});
test('CASC MD5 agrees with independent platform implementation across block boundaries',()=>{
 for(const length of [0,1,55,56,64,65,511]){const b=new Uint8Array(length).map((_,i)=>i*7);expect(md5(b)).toEqual(new Uint8Array(createHash('md5').update(b).digest()));}
});
test('archive/group indexes retain archive number, byte offset and encoded length',()=>{
 expect(findCdnEntry(index(),key)).toEqual({archive:0,offset:1234,size:encoded.length});
 expect(findCdnEntry(index(4),key,7)).toEqual({archive:7,offset:1234,size:encoded.length});
 expect(()=>findCdnEntry(index().subarray(0,20),key)).toThrow();
});
test('loose CDN retrieval uses the selected encoding and does not require another build',async()=>{
 const source={openFile:async()=>{throw Error('not local');}}as unknown as FileSource,requests:string[]=[];
 const reader=new CdnReader(source,info,async(host,path)=>{requests.push(host+path);return encoded;});
 expect(await reader.read(key)).toEqual(encoded);expect(requests).toHaveLength(1);expect(requests[0]).toContain('/tpr/wow/data/');
});
test('archived CDN retrieval uses pinned config and exact group-index byte range',async()=>{
 const source={openFile:async(path:string[])=>{
  if(path[1]==='config')return file(new TextEncoder().encode(`archives = ${archive}\narchive-group = ${group}`));
  if(path[2]===group+'.index')return file(index());throw Error('not local');
 }}as unknown as FileSource;
 const requests:{path:string;range?:[number,number]}[]=[];
 const reader=new CdnReader(source,info,async(_host,path,range)=>{requests.push({path,range});if(!range)throw Error('404 loose');return encoded;});
 expect(await reader.read(key)).toEqual(encoded);expect(requests[1].path).toEndWith(archive);expect(requests[1].range).toEqual([1234,1234+encoded.length-1]);
});
test('wrong CDN payload is rejected rather than used as another model',()=>{
 const wrong=encoded.slice();wrong[wrong.length-1]^=1;expect(()=>verifyCdnEncoding(wrong,key)).toThrow('hash mismatch');
});
test('CDN bridge hosts are limited to the content service and metadata domains',()=>{
 expect(allowedCdnHost('level3.blizzard.com')).toBe(true);expect(allowedCdnHost('us.version.battle.net')).toBe(true);
 expect(allowedCdnHost('blizzard.com.evil.example')).toBe(false);expect(allowedCdnHost('127.0.0.1')).toBe(false);
});
test('ignored range responses are rejected before archive data can be decoded',async()=>{
 const previous=globalThis.fetch;globalThis.fetch=(async()=>new Response(encoded,{status:200}))as unknown as typeof fetch;
 try{await expect(cdnFetch('level3.blizzard.com','/tpr/wow/data/aa/aa/'+archive,[10,21])).rejects.toThrow('did not honor');}
 finally{globalThis.fetch=previous;}
});
