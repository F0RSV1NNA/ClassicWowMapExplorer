using System.Numerics;

namespace MapExtract;

// Voxel columns conservatively cover whole cells. At oblique walls that can
// remove clear stair/ramp margins a second time. Confirm those wall hits against
// the actual collision triangles in the standing body's height band.
internal sealed class SourceBodyClearance
{
    const float BinSize=4;
    readonly record struct Face(Vector3 A,Vector3 B,Vector3 C,float MinZ,float MaxZ,bool Rejected);
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
            var f=new Face(a,b,c,MathF.Min(a.Z,MathF.Min(b.Z,c.Z)),MathF.Max(a.Z,MathF.Max(b.Z,c.Z)),rejected);
            // Inflate bins by the body radius so each query reads one bin.
            for(int x=Bin(MathF.Min(a.X,MathF.Min(b.X,c.X))-radius);x<=Bin(MathF.Max(a.X,MathF.Max(b.X,c.X))+radius);x++)
            for(int y=Bin(MathF.Min(a.Y,MathF.Min(b.Y,c.Y))-radius);y<=Bin(MathF.Max(a.Y,MathF.Max(b.Y,c.Y))+radius);y++){
                if(!bins.TryGetValue((x,y),out var list))bins[(x,y)]=list=new();list.Add(f);
            }
        }
    }
    static int Bin(float value)=>(int)MathF.Floor(value/BinSize);
    internal bool Hits(float x,float y,float floor)=>Hits(x,y,floor,step,false);
    internal bool HitsRejected(float x,float y,float floor)=>Hits(x,y,floor,foot,true);
    bool Hits(float x,float y,float floor,float lower,bool rejectedOnly)
    {
        if(!bins.TryGetValue((Bin(x),Bin(y)),out var faces))return false;
        Span<Vector3> a=stackalloc Vector3[8],b=stackalloc Vector3[8];
        foreach(var face in faces){
            if((rejectedOnly&&!face.Rejected)||face.MaxZ<=floor+lower||face.MinZ>=floor+height)continue;
            a[0]=face.A;a[1]=face.B;a[2]=face.C;
            int count=Clip(a,3,b,floor+lower,1);count=Clip(b,count,a,floor+height,-1);
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
    static int Clip(ReadOnlySpan<Vector3> input,int count,Span<Vector3> output,float height,float sign)
    {
        int n=0;for(int i=0;i<count;i++){
            var a=input[i];var b=input[(i+1)%count];float da=(a.Z-height)*sign,db=(b.Z-height)*sign;
            if(da>=0)output[n++]=a;
            if((da>=0)!=(db>=0))output[n++]=a+(b-a)*(da/(da-db));
        }return n;
    }
}
