import {md5}from './md5';
import type {FileSource}from './source';
import type {ProductInfo}from './config';
import {toHex}from './reader';
export interface CdnEntry{archive:number;offset:number;size:number}
/** Same v1 footer/block format as CASCLib's IndexHandlerBase. */
export function findCdnEntry(bytes:Uint8Array,key:Uint8Array,archive=-1):CdnEntry|null{
    if(bytes.length<20)throw Error('Truncated CDN index');
    const v=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength),f=bytes.length-20;
    const offsetBytes=bytes[f+4],count=v.getUint32(f+8,true),width=20+offsetBytes;
    if(bytes[f]!==1||bytes[f+1]||bytes[f+2]||bytes[f+3]!==4||![0,4,5,6].includes(offsetBytes)||bytes[f+5]!==4||bytes[f+6]!==16||bytes[f+7]!==8)throw Error('Unsupported CDN index footer');
    if(count*width>f)throw Error('Truncated CDN index entries');
    let at=0;
    for(let i=0;i<count;i++){
        if(4096-at%4096<width)at+=4096-at%4096;
        if(at+width>f)throw Error('CDN index entry outside data');
        if(key.every((n,k)=>bytes[at+k]===n)){
            const size=v.getUint32(at+16,false);
            const groupBytes=offsetBytes>4?offsetBytes-4:0;
            const index=groupBytes===2?v.getUint16(at+20,false):groupBytes===1?bytes[at+20]:archive;
            return {archive:index,size,offset:offsetBytes?v.getUint32(at+20+groupBytes,false):0};
        }
        at+=width;
    }
    return null;
}
export function allowedCdnHost(host:string):boolean{return /^(?:[a-z0-9-]+\.)*(?:blizzard\.com|battle\.net)$/.test(host)||host==='blzddist1-a.akamaihd.net';}
export async function cdnFetch(host:string,path:string,range?:[number,number]):Promise<Uint8Array>{
    if(!allowedCdnHost(host)||!/^\/[a-zA-Z0-9_./-]+$/.test(path)||path.includes('..'))throw Error('Invalid CDN resource');
    const remote=`https://${host}${path}`;
    const proxy=typeof location!=='undefined'&&['127.0.0.1','localhost'].includes(location.hostname);
    const url=proxy?`${location.origin}/__cdn/${host}${path}`:remote;
    const cache=typeof caches!=='undefined'?await caches.open('map-explorer-cdn-v1').catch(()=>null):null;
    const cacheKey=remote+(range?`?range=${range[0]}-${range[1]}`:'');
    const saved=await cache?.match(cacheKey);if(saved)return new Uint8Array(await saved.arrayBuffer());
    const response=await fetch(url,{headers:range?{Range:`bytes=${range[0]}-${range[1]}`}:{},signal:AbortSignal.timeout(45000)});
    if(!response.ok)throw Error(`CDN ${host}: HTTP ${response.status} for ${path}`);
    if(range&&(response.status!==206||response.headers.get('Content-Range')?.split('/')[0]!==`bytes ${range[0]}-${range[1]}`))throw Error('CDN did not honor requested archive range');
    const bytes=new Uint8Array(await response.arrayBuffer());
    if(range&&bytes.length!==range[1]-range[0]+1)throw Error('Truncated CDN archive range');
    if(cache)await cache.put(cacheKey,new Response(bytes)).catch(()=>{});
    return bytes;
}
export function verifyCdnEncoding(bytes:Uint8Array,key:Uint8Array):Uint8Array{
    if(bytes.length<8||bytes[0]!==0x42||bytes[1]!==0x4c||bytes[2]!==0x54||bytes[3]!==0x45)throw Error('CDN response is not BLTE');
    const header=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength).getUint32(4,false);
    if(header>bytes.length)throw Error('Truncated CDN BLTE header');
    const hash=md5(header?bytes.subarray(0,header):bytes);
    if(!hash.subarray(0,9).every((n,i)=>n===key[i]))throw Error('CDN encoding hash mismatch');
    return bytes;
}
export class CdnReader{
    private metadata:Promise<{hosts:string[];path:string}>|null=null;
    private config:Promise<Map<string,string[]>>|null=null;
    private indexes=new Map<string,Promise<Uint8Array>>();
    constructor(private source:FileSource,private info:ProductInfo,private request=cdnFetch){}
    private async setup(){
        return this.metadata??=(async()=>{
            if(this.info.cdnPath&&this.info.cdnHosts?.length)return {path:this.info.cdnPath.replace(/^\/+|\/+$/g,''),hosts:this.info.cdnHosts.filter(allowedCdnHost)};
            const text=new TextDecoder().decode(await this.request('us.version.battle.net',`/${this.info.product}/cdns`));
            const lines=text.trim().split(/\r?\n/).filter(l=>l&&!l.startsWith('#')),cols=lines[0].split('|').map(c=>c.split('!')[0]);
            const row=(lines.slice(1).find(l=>l.split('|')[0]==='us')??lines[1])?.split('|');
            if(!row)throw Error('CDN region metadata unavailable');
            return {hosts:(row[cols.indexOf('Hosts')]??'').split(' ').filter(allowedCdnHost),path:row[cols.indexOf('Path')]};
        })();
    }
    private async get(kind:'data'|'config',key:string,range?:[number,number]){
        if(!/^[0-9a-f]{32}(?:\.index)?$/.test(key))throw Error('Invalid CDN content key');
        const {hosts,path}=await this.setup();let last:unknown=new Error('No CDN hosts for selected product');
        for(const host of hosts)try{return await this.request(host,`/${path}/${kind}/${key.slice(0,2)}/${key.slice(2,4)}/${key}`,range);}catch(e){last=e;}
        throw last;
    }
    private async cdnConfig(){
        return this.config??=(async()=>{
            const key=this.info.cdnKey;if(!key)throw Error('Selected build has no CDN config identity');
            let bytes:Uint8Array;
            try{const file=await this.source.openFile(['Data','config',key.slice(0,2),key.slice(2,4),key]);bytes=await file.read(0,file.size);}catch{bytes=await this.get('config',key);}
            const out=new Map<string,string[]>();
            for(const line of new TextDecoder().decode(bytes).split(/\r?\n/)){const at=line.indexOf('=');if(at>0&&!line.startsWith('#'))out.set(line.slice(0,at).trim(),line.slice(at+1).trim().split(/\s+/));}
            return out;
        })();
    }
    private index(id:string){
        let p=this.indexes.get(id);if(!p){p=(async()=>{try{const file=await this.source.openFile(['Data','indices',id+'.index']);return await file.read(0,file.size);}catch{return this.get('data',id+'.index');}})();this.indexes.set(id,p);}return p;
    }
    async read(key:Uint8Array):Promise<Uint8Array>{
        const hex=toHex(key);
        try{return verifyCdnEncoding(await this.get('data',hex),key);}catch{/* Archived encodings need the selected build's archive index. */}
        const config=await this.cdnConfig(),archives=config.get('archives')??[],group=config.get('archive-group')?.[0];
        if(group){try{const e=findCdnEntry(await this.index(group),key);if(e&&archives[e.archive])return verifyCdnEncoding(await this.get('data',archives[e.archive],[e.offset,e.offset+e.size-1]),key);}catch{/* Older builds may have only per-archive indexes. */}}
        for(let i=0;i<archives.length;i++){
            const e=findCdnEntry(await this.index(archives[i]),key,i);
            if(e)return verifyCdnEncoding(await this.get('data',archives[i],[e.offset,e.offset+e.size-1]),key);
        }
        throw Error(`Encoding ${hex} is absent from selected build CDN indexes`);
    }
}
