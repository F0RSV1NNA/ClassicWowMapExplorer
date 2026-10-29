using System.Numerics;
using System.Text.Json;
using DotRecast.Detour;
using MapExtract;
internal static class CoverageAudit
{
    readonly record struct Tri(Vector3 A,Vector3 B,Vector3 C);
    internal static void Write(SourceSurfaceFit source,SourceBodyClearance body,DtMeshData data,string output,float clearance)
    {
        const float spacing=.15f;const int binSize=2;
        float[] bounds=(Environment.GetEnvironmentVariable("EXPLORER_MESH_AUDIT_BOUNDS")??"-8940,-235,-8840,-115,78,110").Split(',').Select(v=>float.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if(bounds.Length!=6)throw new ArgumentException("EXPLORER_MESH_AUDIT_BOUNDS requires minX,minY,maxX,maxY,minZ,maxZ");
        var bins=new Dictionary<(int,int),List<Tri>>();
        int Bin(float n)=>(int)MathF.Floor(n/binSize);
        Vector3 Read(float[] v,int i)=>new(v[i*3+2],v[i*3],v[i*3+1]);
        for(int p=0;p<data.detailMeshes.Length;p++){
            var poly=data.polys[p];if(poly.GetPolyType()!=0||poly.GetArea() is not (NavBake.AreaGround or NavBake.AreaRoad or NavBake.AreaSwimFloor))continue;
            var dm=data.detailMeshes[p];Vector3 V(int i)=>i<poly.vertCount?Read(data.verts,poly.verts[i]):Read(data.detailVerts,dm.vertBase+i-poly.vertCount);
            for(int t=0;t<dm.triCount;t++){
                int at=(dm.triBase+t)*4;var f=new Tri(V(data.detailTris[at]),V(data.detailTris[at+1]),V(data.detailTris[at+2]));
                for(int x=Bin(MathF.Min(f.A.X,MathF.Min(f.B.X,f.C.X)));x<=Bin(MathF.Max(f.A.X,MathF.Max(f.B.X,f.C.X)));x++)
                for(int y=Bin(MathF.Min(f.A.Y,MathF.Min(f.B.Y,f.C.Y)));y<=Bin(MathF.Max(f.A.Y,MathF.Max(f.B.Y,f.C.Y)));y++){
                    if(!bins.TryGetValue((x,y),out var list))bins[(x,y)]=list=new();list.Add(f);
                }
            }
        }
        static float Height(Tri f,float x,float y){
            double d=((double)f.B.Y-f.C.Y)*(f.A.X-f.C.X)+((double)f.C.X-f.B.X)*(f.A.Y-f.C.Y);if(Math.Abs(d)<1e-12)return float.NaN;
            double u=(((double)f.B.Y-f.C.Y)*(x-f.C.X)+((double)f.C.X-f.B.X)*(y-f.C.Y))/d,v=(((double)f.C.Y-f.A.Y)*(x-f.C.X)+((double)f.A.X-f.C.X)*(y-f.C.Y))/d;
            return u>=-1e-7&&v>=-1e-7&&u+v<=1.0000001?(float)(u*f.A.Z+v*f.B.Z+(1-u-v)*f.C.Z):float.NaN;
        }
        var seen=new HashSet<(int,int,int)>();var failures=new List<object>();int checkedSamples=0,missing=0,buried=0,raised=0;
        foreach(var face in source.AuditFloors()){
            var f=new Tri(face.A,face.B,face.C);
            if(MathF.Max(f.A.X,MathF.Max(f.B.X,f.C.X))< bounds[0]||MathF.Min(f.A.X,MathF.Min(f.B.X,f.C.X))> bounds[2]||
               MathF.Max(f.A.Y,MathF.Max(f.B.Y,f.C.Y))< bounds[1]||MathF.Min(f.A.Y,MathF.Min(f.B.Y,f.C.Y))> bounds[3])continue;
            float minz=MathF.Min(f.A.Z,MathF.Min(f.B.Z,f.C.Z)),maxz=MathF.Max(f.A.Z,MathF.Max(f.B.Z,f.C.Z));if(maxz<bounds[4]||minz>bounds[5])continue;
            int x0=(int)MathF.Ceiling(MathF.Min(f.A.X,MathF.Min(f.B.X,f.C.X))/spacing),x1=(int)MathF.Floor(MathF.Max(f.A.X,MathF.Max(f.B.X,f.C.X))/spacing);
            int y0=(int)MathF.Ceiling(MathF.Min(f.A.Y,MathF.Min(f.B.Y,f.C.Y))/spacing),y1=(int)MathF.Floor(MathF.Max(f.A.Y,MathF.Max(f.B.Y,f.C.Y))/spacing);
            for(int ix=x0;ix<=x1;ix++)for(int iy=y0;iy<=y1;iy++){
                float x=ix*spacing,y=iy*spacing,z=Height(f,x,y);if(!float.IsFinite(z)||z<bounds[4]||z>bounds[5]||!seen.Add((ix,iy,(int)MathF.Round(z*100))))continue;
                float eligible=source.Height(new(x,y,z+clearance));
                if(!float.IsFinite(eligible)||MathF.Abs(eligible-z-clearance)>.03f||body.Hits(x,y,z)||body.HitsRejected(x,y,z))continue;
                checkedSamples++;float nav=float.NaN,gap=1.8f;
                if(bins.TryGetValue((Bin(x),Bin(y)),out var candidates))foreach(var tri in candidates){float h=Height(tri,x,y),delta=MathF.Abs(h-z-clearance);if(delta<gap){nav=h;gap=delta;}}
                string? state=null;if(!float.IsFinite(nav)){state="missing";missing++;}else if(nav<z-.015f){state="buried";buried++;}else if(nav>z+clearance+.35f){state="raised";raised++;}
                if(state!=null)failures.Add(new{x,y,z,nav=float.IsFinite(nav)?(float?)nav:null,state});
            }
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new{spacing,checkedSamples,missing,buried,raised,failures}));
        Console.WriteLine(JsonSerializer.Serialize(new{audit=output,checkedSamples,missing,buried,raised}));
    }
}
