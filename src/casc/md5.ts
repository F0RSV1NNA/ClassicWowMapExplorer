/** MD5 for CASC content-addressed payload checks (not authentication). */
export function md5(bytes:Uint8Array):Uint8Array{
 const size=Math.ceil((bytes.length+9)/64)*64,padded=new Uint8Array(size);padded.set(bytes);padded[bytes.length]=128;
 const v=new DataView(padded.buffer);v.setUint32(size-8,bytes.length*8,true);v.setUint32(size-4,Math.floor(bytes.length/0x20000000),true);
 const state=[0x67452301,0xefcdab89,0x98badcfe,0x10325476],shifts=[[7,12,17,22],[5,9,14,20],[4,11,16,23],[6,10,15,21]];
 for(let off=0;off<size;off+=64){
  let [a,b,c,d]=state;
  for(let i=0;i<64;i++){
   const group=i>>>4,s=shifts[group][i%4];
   const f=group===0?(b&c)|(~b&d):group===1?(d&b)|(~d&c):group===2?b^c^d:c^(b|~d);
   const g=group===0?i:group===1?(5*i+1)%16:group===2?(3*i+5)%16:7*i%16;
   const sum=(a+f+Math.floor(Math.abs(Math.sin(i+1))*0x100000000)+v.getUint32(off+g*4,true))|0;
   const next=(b+((sum<<s)|(sum>>>(32-s))))|0;a=d;d=c;c=b;b=next;
  }
  state[0]=(state[0]+a)>>>0;state[1]=(state[1]+b)>>>0;state[2]=(state[2]+c)>>>0;state[3]=(state[3]+d)>>>0;
 }
 const out=new Uint8Array(16),view=new DataView(out.buffer);state.forEach((n,i)=>view.setUint32(i*4,n,true));return out;
}
