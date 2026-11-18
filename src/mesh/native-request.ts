export type NativeFetch=(input:RequestInfo|URL,init?:RequestInit)=>Promise<Response>;

export class NativeEngineError extends Error {}
async function readNativeStream(response:Response,progress:(text:string)=>void):Promise<Uint8Array>{
    const reader=response.body?.getReader();if(!reader)throw new TypeError('Native stream has no body');
    let pending=new Uint8Array(),offset=0;
    async function read(size:number){
        const out=new Uint8Array(size);let filled=0;
        while(filled<size){
            if(offset===pending.length){const next=await reader!.read();if(next.done)throw new TypeError('Native result stream was interrupted');pending=next.value;offset=0;}
            const count=Math.min(size-filled,pending.length-offset);out.set(pending.subarray(offset,offset+count),filled);offset+=count;filled+=count;
        }
        return out;
    }
    try{
        for(;;){
            const header=await read(5),kind=header[0],size=new DataView(header.buffer).getUint32(1,true);
            if(size>1024**3)throw new NativeEngineError('Native result exceeds stream limit');
            if(kind===0){if(size)throw new NativeEngineError('Malformed native heartbeat');progress('Native x64 bake running');continue;}
            if(kind!==1&&kind!==2)throw new NativeEngineError('Unknown native result frame');
            const bytes=await read(size);if(kind===2)throw new NativeEngineError(new TextDecoder().decode(bytes));
            return bytes;
        }
    }finally{await reader.cancel().catch(()=>{});reader.releaseLock();}
}

/** Transport retries never turn a reported engine/geometry failure into a silent fallback. */
export async function requestNativeBake(url:URL,encoded:Uint8Array,lowMemory:boolean,progress:(text:string)=>void,
    fetcher:NativeFetch=fetch,wait:(ms:number)=>Promise<void>=ms=>new Promise(resolve=>setTimeout(resolve,ms))
):Promise<{bytes:Uint8Array;transportRetries:number;queueWaits:number}>{
    let transportRetries=0,queueWaits=0;
    const body=new Uint8Array(encoded);
    for(;;){
        let response:Response;
        try{response=await fetcher(url,{method:'POST',headers:{'Content-Type':'application/octet-stream','X-Explorer-Mesh':'1','X-Explorer-Stream':'1','X-Explorer-Low-Memory':lowMemory?'1':'0'},body});}
        catch(error){
            if(error instanceof NativeEngineError||(error as Error).name==='AbortError'||transportRetries>=3)throw error;
            transportRetries++;progress(`Retrying native request after connection failure (${transportRetries}/3)`);
            await wait(500*2**(transportRetries-1));continue;
        }
        if(response.status===429){await response.text();queueWaits++;progress('Waiting for a native bake slot / available memory');await wait(1000);continue;}
        if(!response.ok)throw Error(await response.text());
        // A reset while receiving the result is a transport failure as well.
        try{return {bytes:response.headers.get('Content-Type')?.includes('application/x-explorer-bake-stream')?await readNativeStream(response,progress):new Uint8Array(await response.arrayBuffer()),transportRetries,queueWaits};}
        catch(error){
            if(error instanceof NativeEngineError||(error as Error).name==='AbortError'||transportRetries>=3)throw error;
            transportRetries++;progress(`Retrying interrupted native result (${transportRetries}/3)`);await wait(500*2**(transportRetries-1));
        }
    }
}
