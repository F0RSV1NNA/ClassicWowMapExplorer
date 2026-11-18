using System.Numerics;

namespace MapExtract;

// Voxel columns conservatively cover whole cells. At oblique walls that can
// remove clear stair/ramp margins a second time. Confirm those wall hits against
// the actual collision triangles in the standing body's height band.
internal sealed class SourceBodyClearance
{
    const float BinSize=4;
    readonly record struct Face(Vector3 A,Vector3 B,Vector3 C,float MinZ,float MaxZ,bool Rejected,bool WmoCap);
    readonly Dictionary<(int,int),List<Face>> bins=new();
    readonly float radius,height,step,foot;
    public SourceBodyClearance(Extractor.Soup soup,NavBake.Settings settings,IReadOnlySet<int>? rejectedCaps=null)
    {
        radius=settings.AgentRadius;height=settings.AgentHeight;
        step=settings.AgentMaxClimb;
        foot=settings.CellHeight*.5f;
        float slope=MathF.Cos(settings.AgentMaxSlope*MathF.PI/180);
        for(int t=0;t<soup.TriCount;t++){
            if(t<soup.TriKind.Count&&soup.TriKind[t]==Extractor.Soup.KindDoodad&&!settings.BlockDoodads)continue;
            var a=soup.Verts[soup.Indices[t*3]];var b=soup.Verts[soup.Indices[t*3+1]];var c=soup.Verts[soup.Indices[t*3+2]];
            var n=Vector3.Cross(b-a,c-a);
            bool rejected=(rejectedCaps?.Contains(t)??false)||(t<soup.TriKind.Count&&soup.TriKind[t]==Extractor.Soup.KindDoodad&&settings.BlockDoodads);
            if(n.LengthSquared()<1e-10f||(!rejected&&MathF.Abs(n.Z)>n.Length()*slope))continue;
            var f=new Face(a,b,c,MathF.Min(a.Z,MathF.Min(b.Z,c.Z)),MathF.Max(a.Z,MathF.Max(b.Z,c.Z)),rejected,(t>=soup.TriKind.Count||soup.TriKind[t]==Extractor.Soup.KindWmo)&&(rejectedCaps?.Contains(t)??false));
            // Inflate bins by the body radius so each query reads one bin.
            for(int x=Bin(MathF.Min(a.X,MathF.Min(b.X,c.X))-radius);x<=Bin(MathF.Max(a.X,MathF.Max(b.X,c.X))+radius);x++)
            for(int y=Bin(MathF.Min(a.Y,MathF.Min(b.Y,c.Y))-radius);y<=Bin(MathF.Max(a.Y,MathF.Max(b.Y,c.Y))+radius);y++){
                if(!bins.TryGetValue((x,y),out var list))bins[(x,y)]=list=new();list.Add(f);
            }
        }
    }
    static int Bin(float value)=>(int)MathF.Floor(value/BinSize);
    internal bool Hits(float x,float y,float floor)=>Hits(x,y,floor,step,false);
    internal bool HitsRejected(float x,float y,float floor,SourceSurfaceFit? coveredBy=null)=>Hits(x,y,floor,foot,true,coveredBy);
    bool Hits(float x,float y,float floor,float lower,bool rejectedOnly,SourceSurfaceFit? coveredBy=null)
    {
        if(!bins.TryGetValue((Bin(x),Bin(y)),out var faces))return false;
        Span<Vector3> a=stackalloc Vector3[8],b=stackalloc Vector3[8];
        bool onRamp=coveredBy?.HasWmoStepTransition(x,y,floor,foot*4.02f)??false;
        foreach(var face in faces){
            // A rejected inclined stair stringer stays nonwalkable, but its
            // legally low side is not a standing-body wall beside the ramp.
            var normal=Vector3.Cross(face.B-face.A,face.C-face.A);
            float faceLower=onRamp&&face.WmoCap&&MathF.Abs(normal.Z)<normal.Length()*.98f?step:lower;
            if((rejectedOnly&&!face.Rejected)||face.MaxZ<=floor+faceLower||face.MinZ>=floor+height)continue;
            a[0]=face.A;a[1]=face.B;a[2]=face.C;
            int count=Clip(a,3,b,floor+faceLower,1);count=Clip(b,count,a,floor+height,-1);
            if(face.WmoCap&&coveredBy!=null&&BuriedByFloor(a[..count],x,y,coveredBy))continue;
            bool positive=false,negative=false;
            for(int i=0;i<count;i++){
                var p=a[i];var q=a[(i+1)%count];
                double dx=q.X-p.X,dy=q.Y-p.Y,length=dx*dx+dy*dy;
                double t=length>0?Math.Clamp((((double)x-p.X)*dx+((double)y-p.Y)*dy)/length,0,1):0;
                if(Math.Pow(x-p.X-t*dx,2)+Math.Pow(y-p.Y-t*dy,2)<radius*radius)return true;
                double side=dx*(y-p.Y)-dy*(x-p.X);positive|=side>1e-8;negative|=side< -1e-8;
            }
            if(count>=3&&positive!=negative)return true;
        }
        return false;
    }
    bool BuriedByFloor(ReadOnlySpan<Vector3> polygon,float x,float y,SourceSurfaceFit source)
    {
        Span<Vector3> a=stackalloc Vector3[16],b=stackalloc Vector3[16];polygon.CopyTo(a);
        int n=ClipAxis(a,polygon.Length,b,0,x-radius,1);n=ClipAxis(b,n,a,0,x+radius,-1);
        n=ClipAxis(a,n,b,1,y-radius,1);n=ClipAxis(b,n,a,1,y+radius,-1);
        if(n<3)return false;
        // Only source-rejected WMO pieces wholly buried beneath an eligible
        // local floor are ignored. Props and exposed/coplanar caps still block.
        for(int i=0;i<n;i++)if(!source.CoversRejected(a[i].X,a[i].Y,a[i].Z))return false;
        return true;
    }

    internal bool RejectedBuriedInCell(float x,float y,float floor,float size,float bottom,float top)
    {
        if(!bins.TryGetValue((Bin(x),Bin(y)),out var faces))return false;
        Span<Vector3> a=stackalloc Vector3[16],b=stackalloc Vector3[16];bool found=false;float half=size*.5f;
        foreach(var f in faces){
            if(!f.Rejected||f.MaxZ<bottom||f.MinZ>top)continue;
            a[0]=f.A;a[1]=f.B;a[2]=f.C;
            int count=ClipAxis(a,3,b,0,x-half,1);count=ClipAxis(b,count,a,0,x+half,-1);
            count=ClipAxis(a,count,b,1,y-half,1);count=ClipAxis(b,count,a,1,y+half,-1);
            count=ClipAxis(a,count,b,2,bottom,1);count=ClipAxis(b,count,a,2,top,-1);
            if(count<3)continue;found=true;
            for(int i=0;i<count;i++)if(a[i].Z>=floor-.02f)return false;
        }
        return found;
    }
    static int ClipAxis(ReadOnlySpan<Vector3> input,int count,Span<Vector3> output,int axis,float value,float sign)
    {
        int n=0;for(int i=0;i<count;i++){
            var a=input[i];var b=input[(i+1)%count];
            float da=((axis==0?a.X:axis==1?a.Y:a.Z)-value)*sign,db=((axis==0?b.X:axis==1?b.Y:b.Z)-value)*sign;
            if(da>=0)output[n++]=a;if((da>=0)!=(db>=0))output[n++]=a+(b-a)*(da/(da-db));
        }return n;
    }
    static int Clip(ReadOnlySpan<Vector3> input,int count,Span<Vector3> output,float height,float sign)
    {
        int n=0;for(int i=0;i<count;i++){
            var a=input[i];var b=input[(i+1)%count];float da=(a.Z-height)*sign,db=(b.Z-height)*sign;
            if(da>=0)output[n++]=a;
            if((da>=0)!=(db>=0))output[n++]=a+(b-a)*(da/(da-db));
        }return n;
    }
}
