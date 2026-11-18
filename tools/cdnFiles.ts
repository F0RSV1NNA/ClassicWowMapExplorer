import type {Plugin}from 'vite';
import {allowedCdnHost}from '../src/casc/cdn';
/** Browser workers use the same-origin bridge for CDN hosts without CORS headers. */
export function cdnFiles():Plugin{return {name:'casc-cdn-files',configureServer(server){
    server.middlewares.use(async(req,res,next)=>{
        if(!req.url?.startsWith('/__cdn/'))return next();
        const path=req.url.slice(7),slash=path.indexOf('/'),host=path.slice(0,slash),resource=path.slice(slash);
        if(!['GET','HEAD'].includes(req.method??'')||!allowedCdnHost(host)||!/^\/[a-zA-Z0-9_./-]+$/.test(resource)||resource.includes('..')){res.statusCode=400;res.end();return;}
        try{
            const headers:Record<string,string>={};if(req.headers.range)headers.Range=req.headers.range;
            const response=await fetch(`https://${host}${resource}`,{method:req.method,headers,redirect:'error',signal:AbortSignal.timeout(45000)});
            res.statusCode=response.status;
            for(const h of ['Content-Length','Content-Type','Content-Range']){const v=response.headers.get(h);if(v)res.setHeader(h,v);}
            if(req.method==='HEAD'){res.end();return;}
            res.end(new Uint8Array(await response.arrayBuffer()));
        }catch(e){res.statusCode=502;res.end(String(e));}
    });
}};}
