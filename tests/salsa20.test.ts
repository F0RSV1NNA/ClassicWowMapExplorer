import {expect,test}from 'bun:test';
import fixtures from './fixtures/casclib-salsa20.json';
import {salsa20}from '../src/casc/salsa20';
import {fromHex}from '../src/casc/reader';
import {decodeBlte}from '../src/casc/blte';
for(const f of fixtures)test(`Salsa20 matches independent CASCLib ${f.key.length*4}-bit key, chunk ${f.index}`,()=>{
 expect(salsa20(fromHex(f.plain),fromHex(f.key),fromHex(f.nonce))).toEqual(fromHex(f.cipher));
});
test('BLTE decrypts a known-key second chunk, applying the chunk index to its IV',()=>{
 const f=fixtures.find(f=>f.key.length===32&&f.index===1)!;
 const encrypted=Uint8Array.from([0x45,8,...fromHex('E07E107F1390A3DF').reverse(),4,1,2,3,4,0x53,...fromHex(f.cipher)]);
 const blocks=[new Uint8Array([0x4e,99]),encrypted],size=12+24*2,out=new Uint8Array(size+blocks.reduce((n,b)=>n+b.length,0)),v=new DataView(out.buffer);
 out.set([0x42,0x4c,0x54,0x45]);v.setUint32(4,size);out[8]=15;out[11]=2;let at=size;
 blocks.forEach((b,i)=>{v.setUint32(12+i*24,b.length);v.setUint32(16+i*24,i===0?1:f.plain.length/2-1);out.set(b,at);at+=b.length;});
 expect(decodeBlte(out).data).toEqual(new Uint8Array([99,...fromHex(f.plain).subarray(1)]));
});
