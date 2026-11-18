/** Salsa20/20, little-endian words, 64-bit nonce/counter. Used by BLTE mode S. */
export function salsa20(input:Uint8Array,key:Uint8Array,nonce:Uint8Array):Uint8Array{
    if((key.length!==16&&key.length!==32)||nonce.length!==8)throw Error('Invalid Salsa20 key or nonce size');
    const word=(b:Uint8Array,i:number)=>(b[i]|b[i+1]<<8|b[i+2]<<16|b[i+3]<<24)>>>0;
    const constants=new TextEncoder().encode(key.length===16?'expand 16-byte k':'expand 32-byte k');
    const state=new Uint32Array(16);
    for(let i=0;i<4;i++){state[i*5]=word(constants,i*4);state[1+i]=word(key,i*4);state[11+i]=word(key,(key.length===32?16:0)+i*4);}
    state[6]=word(nonce,0);state[7]=word(nonce,4);
    const out=new Uint8Array(input.length),rot=(v:number,n:number)=>(v<<n)|(v>>>(32-n));
    for(let at=0;at<input.length;at+=64){
        const x=state.slice();
        const quarter=(a:number,b:number,c:number,d:number)=>{x[b]^=rot((x[a]+x[d])|0,7);x[c]^=rot((x[b]+x[a])|0,9);x[d]^=rot((x[c]+x[b])|0,13);x[a]^=rot((x[d]+x[c])|0,18);};
        for(let round=0;round<10;round++){
            quarter(0,4,8,12);quarter(5,9,13,1);quarter(10,14,2,6);quarter(15,3,7,11);
            quarter(0,1,2,3);quarter(5,6,7,4);quarter(10,11,8,9);quarter(15,12,13,14);
        }
        for(let i=0;i<16;i++){const n=(x[i]+state[i])>>>0;for(let j=0;j<4&&at+i*4+j<input.length;j++)out[at+i*4+j]=input[at+i*4+j]^(n>>>(j*8));}
        state[8]=(state[8]+1)>>>0;if(!state[8])state[9]=(state[9]+1)>>>0;
    }
    return out;
}
