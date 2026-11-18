import type { CascStorage } from '../casc/storage';
import type { ProductInfo } from '../casc/config';
import catalog from './transport-models.json';
const ids=catalog.filter(m=>!/\_\d{3}(?:_lod\d+)?\.wmo$/i.test(m.name)).map(m=>m.fdid);

/** Score the actual catalog mappings, not a product label. Never alter world storage. */
export async function selectTransportLibrary(
    world:CascStorage,
    products:()=>Promise<ProductInfo[]>,
    open:(product:string)=>Promise<CascStorage>,
):Promise<{storage:CascStorage;reason:string;error?:string;candidates:{product:string;mapped:number}[]}>{
    const coverage=(s:CascStorage)=>ids.filter(id=>{const state=s.status(id);return state==='ok'||state==='not-local';}).length;
    const candidates=[{product:world.stats.product,mapped:coverage(world)}];
    const installed=await products(),errors:string[]=[];
    let storage=world,best=candidates[0].mapped;
    // Prefer the released Forever build on ties; Retail provides the larger reference catalog.
    for(const product of ['wowf','wow']){
        if(product===world.stats.product||!installed.some(p=>p.product===product))continue;
        try{
            const other=await open(product),mapped=coverage(other);candidates.push({product,mapped});
            if(mapped>best||(mapped===best&&product==='wowf')){storage=other;best=mapped;}
        }catch(e){errors.push(`${product}: ${e instanceof Error?e.message:String(e)}`);}
    }
    return {storage,candidates,reason:`Transport library ${storage.stats.product}: ${best}/${ids.length} catalog roots mapped; world remains ${world.stats.product}`,error:errors.length?errors.join('; '):undefined};
}
