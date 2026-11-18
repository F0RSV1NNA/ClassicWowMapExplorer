import {test,expect}from 'bun:test';
import {requestNativeBake,type NativeFetch}from '../src/mesh/native-request';
const url=new URL('http://127.0.0.1/__mesh-bake'),input=new Uint8Array([1,2,3]);
const wait=async()=>{};
test('a busy native server is retried with the complete same input',async()=>{
 let count=0;const bodies:unknown[]=[];
 const fetcher=(async(_url,options)=>{bodies.push(options?.body);return ++count===1?new Response('busy',{status:429}):new Response(new Uint8Array([9]));})as NativeFetch;
 const result=await requestNativeBake(url,input,false,()=>{},fetcher,wait);
 expect(count).toBe(2);expect(result.bytes).toEqual(new Uint8Array([9]));expect(result.queueWaits).toBe(1);expect(bodies[0]).toBe(bodies[1]);
});
test('transient connection resets recover without hiding engine failures',async()=>{
 let count=0;
 const fetcher=(async()=>{if(++count<3)throw new TypeError('Failed to fetch');return new Response(new Uint8Array([8]));})as NativeFetch;
 expect((await requestNativeBake(url,input,false,()=>{},fetcher,wait)).transportRetries).toBe(2);
 count=0;const failure=(async()=>{count++;return new Response('Shared-edge detail limit',{status:500});})as NativeFetch;
 await expect(requestNativeBake(url,input,false,()=>{},failure,wait)).rejects.toThrow('Shared-edge detail limit');expect(count).toBe(1);
});
test('connection retries are bounded and cancellation is not retried',async()=>{
 let count=0;const failure=(async()=>{count++;throw new TypeError('Failed to fetch');})as NativeFetch;
 await expect(requestNativeBake(url,input,false,()=>{},failure,wait)).rejects.toThrow('Failed to fetch');expect(count).toBe(4);
 count=0;const abort=(async()=>{count++;throw new DOMException('cancelled','AbortError');})as NativeFetch;
 await expect(requestNativeBake(url,input,false,()=>{},abort,wait)).rejects.toThrow('cancelled');expect(count).toBe(1);
});


function streamResponse(kind:number,payload:Uint8Array,heartbeat=true){
 const frame=new Uint8Array((heartbeat?5:0)+5+payload.length);let at=heartbeat?5:0;
 frame[at]=kind;new DataView(frame.buffer).setUint32(at+1,payload.length,true);frame.set(payload,at+5);
 let i=0;return new Response(new ReadableStream<Uint8Array>({pull(controller){if(i===frame.length){controller.close();return;}controller.enqueue(frame.subarray(i,++i));}}),{headers:{'Content-Type':'application/x-explorer-bake-stream'}});
}
test('streamed heartbeats and split frame headers preserve result bytes',async()=>{
 const messages:string[]=[];const fetcher=(async()=>streamResponse(1,new Uint8Array([4,5,6])))as NativeFetch;
 const result=await requestNativeBake(url,input,false,m=>messages.push(m),fetcher,wait);
 expect(result.bytes).toEqual(new Uint8Array([4,5,6]));expect(messages).toContain('Native x64 bake running');
});
test('reported streamed geometry errors are never retried as network failures',async()=>{
 let calls=0;const fetcher=(async()=>{calls++;return streamResponse(2,new TextEncoder().encode('detail geometry failed'));})as NativeFetch;
 await expect(requestNativeBake(url,input,false,()=>{},fetcher,wait)).rejects.toThrow('detail geometry failed');expect(calls).toBe(1);
});
test('a truncated streamed result is retried with the original source input',async()=>{
 let calls=0;const fetcher=(async()=>++calls===1?new Response(new Uint8Array([1,9]),{headers:{'Content-Type':'application/x-explorer-bake-stream'}}):streamResponse(1,new Uint8Array([7])))as NativeFetch;
 const result=await requestNativeBake(url,input,false,()=>{},fetcher,wait);expect(result.bytes).toEqual(new Uint8Array([7]));expect(result.transportRetries).toBe(1);
});
