import { EncryptedError, StorageChangedError, type CascStorage } from '../casc/storage';
import type {RootEvidence} from '../casc/root';
import { chunks } from '../formats/chunks';
import models from './transport-models.json';

export interface TransportModel { fdid: number; name: string; min: [number,number,number]; max: [number,number,number] }
/** Group LODs are not placeable roots, even though their filenames end in .wmo. */
export function isTransportRootName(name:string):boolean {
    return /\.wmo$/i.test(name) && !/_\d{3}(?:_lod\d+)?\.wmo$/i.test(name);
}
export interface TransportInventoryEntry { fdid:number;name:string;state:'ready'|'group-lod'|'unknown'|'no-encoding'|'not-local'|'encrypted'|'unreadable'|'not-root'|'invalid-bounds';detail?:string }
export interface TransportCatalog { models:TransportModel[];entries:TransportInventoryEntry[];sourceBuild:Pick<CascStorage['stats'],'product'|'version'|'buildName'|'buildKey'|'rootContentKey'|'rootSha256'|'readerVersion'>;rootEvidence?:RootEvidence;worldBuild?:CascStorage['stats'];librarySelection?:{reason:string;error?:string;candidates?:{product:string;mapped:number}[]} }
const logSkipped=(id:number,reason:string)=>console.warn(`Skipping transport ${id}: ${reason}`);
/** Record every outcome, so a short catalog never silently looks complete. */
export async function transportCatalog(storage:CascStorage,skipped:(fdid:number,reason:string)=>void=logSkipped):Promise<TransportCatalog>{
    const out:TransportModel[]=[],entries:TransportInventoryEntry[]=[];
    const rootEvidence=storage.inspectRoot?.(models.filter(m=>isTransportRootName(m.name)).map(m=>m.fdid));
    for(const model of models){
        const record=(state:TransportInventoryEntry['state'],detail?:string)=>entries.push({...model,state,detail});
        if(!isTransportRootName(model.name)){record('group-lod');continue;}
        const status=storage.status(model.fdid);
        if(status!=='ok'&&!(status==='not-local'&&storage.remoteAvailable)){
            const raw=rootEvidence?.files[model.fdid];
            record(status,status==='unknown'&&raw?(raw.length?'Present in raw root, excluded by locale selection':'Absent from every block in the selected raw root'):undefined);
            continue;
        }
        try{
            const bytes=await storage.readFile(model.fdid);
            const header=Array.from(chunks(bytes)).find(c=>c.id==='MOHD'&&c.size>=64&&c.offset+c.size<=bytes.length);
            if(!header){record('not-root');skipped(model.fdid,'not a complete WMO root');continue;}
            const v=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength),f=(n:number)=>v.getFloat32(header.offset+n,true);
            const min:[number,number,number]=[f(36),f(40),f(44)],max:[number,number,number]=[f(48),f(52),f(56)];
            if(![...min,...max].every(Number.isFinite)||min.some((n,i)=>n>max[i])){record('invalid-bounds');skipped(model.fdid,'invalid model bounds');continue;}
            out.push({...model,min,max});record('ready');
        }catch(e){
            if(e instanceof StorageChangedError)throw e;
            record(e instanceof EncryptedError?'encrypted':'unreadable',(e as Error).message);
            skipped(model.fdid,(e as Error).message);
        }
    }
    const {product,version,buildName,buildKey,rootContentKey,rootSha256,readerVersion}=storage.stats;
    return {models:out,entries,sourceBuild:{product,version,buildName,buildKey,rootContentKey,rootSha256,readerVersion},rootEvidence};
}
export async function transportModels(storage:CascStorage,skipped:(fdid:number,reason:string)=>void=logSkipped):Promise<TransportModel[]>{return (await transportCatalog(storage,skipped)).models;}
export function transportInventorySummary(catalog:TransportCatalog):string{
    const counts=new Map<string,number>();for(const e of catalog.entries)counts.set(e.state,(counts.get(e.state)??0)+1);
    return `${catalog.librarySelection ? '['+catalog.sourceBuild.product+'] ' : ''}${catalog.models.length} placed / ${catalog.entries.filter(e=>e.state!=='group-lod').length} root candidates. `+
        [...counts].filter(([state])=>state!=='ready'&&state!=='group-lod').map(([state,n])=>`${n} ${state}`).join('; ');
}

export interface TransportSlot { model: TransportModel; x: number; y: number; z: number }
/** Four broad columns/shelves; wide models receive their own row, never overlap neighbours. */
export function transportLayout(models: TransportModel[], centreX: number, centreZ: number, width=2400): TransportSlot[] {
    const out: TransportSlot[]=[]; let x=0,z=0,row=0;
    for (const model of models) {
        // Raw model Y becomes display X, raw model X becomes display Z.
        const w=model.max[1]-model.min[1]+100,h=model.max[0]-model.min[0]+100;
        if(x && x+w>width){x=0;z+=row;row=0;}
        out.push({model,y:100-model.min[2],x:centreX-width/2+x+50-model.min[1],z:centreZ+z+50-model.min[0]});
        x+=w;row=Math.max(row,h);
        if(w>width){x=0;z+=row;row=0;}
    }
    if(out.length){
        const min=Math.min(...out.map(s=>s.z+s.model.min[0])),max=Math.max(...out.map(s=>s.z+s.model.max[0]));
        const shift=(min+max)/2-centreZ;
        for(const slot of out)slot.z-=shift;
    }
    return out;
}
/** Native Mappster transport point -> staged display. The export itself is unchanged. */
export function transportPreviewPoint(slot: Pick<TransportSlot,'x'|'y'|'z'>, x:number,y:number,z:number): [number,number,number] {
    return [slot.x-y,slot.y+z,slot.z-x];
}
