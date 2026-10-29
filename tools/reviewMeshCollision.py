import json,math
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
import argparse
parser=argparse.ArgumentParser(description="Depth-buffered source collision and Detour-stage comparison; requires numpy and Pillow.")
for name in ['source','baseline','candidate','regions','out']:parser.add_argument('--'+name,required=True)
args=parser.parse_args()
raw=json.loads(Path(args.source).read_text())['source']
source=np.array([[[t[k][a] for a in ['X','Y','Z']] for k in ['A','B','C']] for t in raw])
def mesh(folder):
 d=json.loads((Path(folder)/'surface-weld.json').read_text());v=np.array([[p[a] for a in ['X','Y','Z']] for p in d['verts']]);return v[np.array(d['indices']).reshape(-1,3)]
old=mesh(args.baseline);new=mesh(args.candidate)
regions=json.loads(Path(args.regions).read_text())
W,H=620,500;font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',19)
out=Image.new('RGB',(W*3,H*len(regions)),'#101720')
for row,(title,x,y,z,r,ceiling) in enumerate(regions):
 for col,tris in enumerate([source,old,new]):
  # Full per-pixel depth buffer. No painter-order artifacts or interactive viewer.
  depth=np.full((H,W),-np.inf);rgb=np.full((H,W,3),[16,23,32],dtype=np.uint8)
  scale=min(W,H)*.7/(r*2)
  def clip(vs,axis,limit,sign):
   out=[]
   for i,a in enumerate(vs):
    b=vs[(i+1)%len(vs)];da=(a[axis]-limit)*sign;db=(b[axis]-limit)*sign
    if da>=0:out.append(a)
    if (da>=0)!=(db>=0):out.append(a+(b-a)*da/(da-db))
   return out
  chosen=tris[(tris[:,:,0].min(1)<x+r)&(tris[:,:,0].max(1)>x-r)&(tris[:,:,1].min(1)<y+r)&(tris[:,:,1].max(1)>y-r)&(tris[:,:,2].min(1)<ceiling)&(tris[:,:,2].max(1)>z-2)]
  for tri in chosen:
   vs=list(tri)
   for ax,lo,hi in [(0,x-r,x+r),(1,y-r,y+r),(2,z-2,ceiling)]:
    vs=clip(vs,ax,lo,1) if vs else [];vs=clip(vs,ax,hi,-1) if vs else []
   if len(vs)<3:continue
   for n in range(1,len(vs)-1):
    t=np.array([vs[0],vs[n],vs[n+1]]);q=t-[x,y,z]
    projected=np.column_stack([W/2+(q[:,1]-q[:,0])*.7071*scale,H*.67+(q[:,0]+q[:,1])*.354*scale-q[:,2]*scale,(q[:,0]+q[:,1])*.7071+q[:,2]*.5])
    a,b,c=projected;det=(b[1]-c[1])*(a[0]-c[0])+(c[0]-b[0])*(a[1]-c[1])
    if abs(det)<1e-8:continue
    x0=max(0,int(projected[:,0].min()));x1=min(W-1,math.ceil(projected[:,0].max()));y0=max(55,int(projected[:,1].min()));y1=min(H-1,math.ceil(projected[:,1].max()))
    if x1<x0 or y1<y0:continue
    px,py=np.meshgrid(np.arange(x0,x1+1)+.5,np.arange(y0,y1+1)+.5)
    u=((b[1]-c[1])*(px-c[0])+(c[0]-b[0])*(py-c[1]))/det;v=((c[1]-a[1])*(px-c[0])+(a[0]-c[0])*(py-c[1]))/det;w=1-u-v
    h=u*a[2]+v*b[2]+w*c[2];view=depth[y0:y1+1,x0:x1+1];mask=(u>=0)&(v>=0)&(w>=0)&(h>view)
    normal=np.cross(t[1]-t[0],t[2]-t[0]);normal/=max(1e-8,np.linalg.norm(normal));shade=.55+.45*abs(normal[2]);rise=np.clip((t[:,2].mean()-z)/2,0,1)
    color=np.array([145,155,169]) if col==0 else np.array([65+rise*155,205-rise*100,165-rise*20]);color=color*shade
    lines=np.minimum(np.minimum(u,v),w)<.009;colorgrid=np.where(lines[:,:,None],color*.48,color)
    view[mask]=h[mask];rgb[y0:y1+1,x0:x1+1][mask]=colorgrid[mask]
  pic=Image.fromarray(rgb);d=ImageDraw.Draw(pic);d.text((12,8),title+' | '+['Collision','Previous build','Candidate'][col],font=font,fill='white');d.text((12,31),f'WoW {x:.1f}, {y:.1f}; cutaway below Z {ceiling}',font=font,fill='#aabbcc');out.paste(pic,(col*W,row*H))
out.save(args.out);print(args.out)








