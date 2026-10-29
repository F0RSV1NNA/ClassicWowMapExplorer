using System.Numerics;
using DotRecast.Detour;
namespace MapExtract;
internal sealed partial class SourceSurfaceFit
{
    // Linked coarse polygons must share the SAME piecewise-linear detail edge.
    // Welding coincident vertices alone leaves cracks at unmatched breakpoints.
    int SynchronizeDetailEdges(DtMeshData data)
    {
        int count=data.detailMeshes.Length,changed=0;
        var vertices=new List<Vector3>[count];var triangles=new List<DetailFace>[count];
        var additions=new List<Vector3>[count];
        Vector3 Read(float[] v,int i)=>new(v[i*3+2],v[i*3],v[i*3+1]);
        for(int p=0;p<count;p++){
            var poly=data.polys[p];var dm=data.detailMeshes[p];vertices[p]=new();triangles[p]=new();additions[p]=new();
            for(int i=0;i<poly.vertCount;i++)vertices[p].Add(Read(data.verts,poly.verts[i]));
            for(int i=0;i<dm.vertCount;i++)vertices[p].Add(Read(data.detailVerts,dm.vertBase+i));
            for(int t=0;t<dm.triCount;t++){int at=(dm.triBase+t)*4;triangles[p].Add(new(data.detailTris[at],data.detailTris[at+1],data.detailTris[at+2],data.detailTris[at+3]));}
        }
        static double Parameter(Vector3 q,Vector3 a,Vector3 b){double dx=(double)b.X-a.X,dy=(double)b.Y-a.Y;return (((double)q.X-a.X)*dx+((double)q.Y-a.Y)*dy)/(dx*dx+dy*dy);}
        static bool On(Vector3 q,Vector3 a,Vector3 b,out double t){
            t=Parameter(q,a,b);if(!double.IsFinite(t)||t<-.00001||t>1.00001)return false;
            double x=a.X+t*((double)b.X-a.X),y=a.Y+t*((double)b.Y-a.Y);
            return Math.Pow(q.X-x,2)+Math.Pow(q.Y-y,2)<.0015*.0015;
        }
        for(int p=0;p<count;p++){
            var poly=data.polys[p];if(poly.GetPolyType()!=0)continue;
            for(int e=0;e<poly.vertCount;e++){
                int other=poly.neis[e]-1;if(other<=p||other>=count||(poly.neis[e]&0x8000)!=0)continue;
                var a=vertices[p][e];var b=vertices[p][(e+1)%poly.vertCount];
                if(Vector2.DistanceSquared(new(a.X,a.Y),new(b.X,b.Y))<1e-8)continue;
                // Float coordinates near large world origins can leave a detail
                // sample just off its coarse edge. Canonicalize XY even when
                // the two height profiles already agree at their breakpoints.
                foreach(int owner in new[]{p,other})for(int i=data.polys[owner].vertCount;i<vertices[owner].Count;i++){
                    var q=vertices[owner][i];if(!On(q,a,b,out double t))continue;t=Math.Clamp(t,0,1);
                    var canonical=new Vector3((float)(a.X+t*((double)b.X-a.X)),(float)(a.Y+t*((double)b.Y-a.Y)),q.Z);
                    if(canonical!=q){vertices[owner][i]=canonical;changed++;}
                }
                List<(double T,float H)> Profile(int owner)=>vertices[owner].Where(q=>On(q,a,b,out _)).Select(q=>(Math.Clamp(Parameter(q,a,b),0,1),q.Z)).OrderBy(q=>q.Item1).ToList();
                var left=Profile(p);var right=Profile(other);if(left.Count<2||right.Count<2)continue;
                float At(List<(double T,float H)> profile,double t){
                    for(int i=1;i<profile.Count;i++)if(t<=profile[i].T+.00001){var u=profile[i-1];var v=profile[i];return v.T-u.T<1e-8?MathF.Max(u.H,v.H):(float)(u.H+(v.H-u.H)*Math.Clamp((t-u.T)/(v.T-u.T),0,1));}
                    return profile[^1].H;
                }
                var breaks=left.Concat(right).Select(q=>q.T).DistinctBy(t=>Math.Round(t,6)).Order().ToArray();
                if(!breaks.Any(t=>Math.Abs(At(left,t)-At(right,t))>.001))continue;
                foreach(double t in breaks){
                    float h=MathF.Max(At(left,t),At(right,t));
                    var point=new Vector3((float)(a.X+t*((double)b.X-a.X)),(float)(a.Y+t*((double)b.Y-a.Y)),h);
                    foreach(int owner in new[]{p,other}){
                        bool found=false;
                        for(int i=0;i<vertices[owner].Count;i++){
                            var q=vertices[owner][i];if(!On(q,a,b,out double qt)||Math.Abs(qt-t)>.00001)continue;
                            vertices[owner][i]=point;found=true;changed++;
                        }
                        if(!found)additions[owner].Add(point);
                    }
                }
            }
        }
        if(changed==0)return 0;
        for(int p=0;p<count;p++){
            var verts=vertices[p];var output=new List<DetailFace>();var local=new Dictionary<Vector3,int>();
            int Id(Vector3 point){if(local.TryGetValue(point,out int id))return id;id=verts.Count;verts.Add(point);local[point]=id;return id;}
            foreach(var tri in triangles[p]){
                int[] ids=[tri.A,tri.B,tri.C];var boundary=new List<(int Id,int Flag)>();bool split=false;
                for(int e=0;e<3;e++){
                    int flag=(tri.Flags>>(2*e))&3;boundary.Add((ids[e],flag));
                    var a=verts[ids[e]];var b=verts[ids[(e+1)%3]];
                    var points=additions[p].Where(q=>On(q,a,b,out double t)&&t>.00001&&t<.99999).OrderBy(q=>Parameter(q,a,b)).ToArray();
                    foreach(var point in points){boundary.Add((Id(point),flag));split=true;}
                }
                if(!split){output.Add(tri);continue;}
                int center=verts.Count;verts.Add((verts[tri.A]+verts[tri.B]+verts[tri.C])/3);
                for(int e=0;e<boundary.Count;e++)output.Add(new(boundary[e].Id,boundary[(e+1)%boundary.Count].Id,center,boundary[e].Flag));
            }
            if(verts.Count>255||output.Count>255)throw new InvalidDataException("Shared-edge detail exceeds Detour's byte-index limit");
            triangles[p]=output;
        }
        var dv=new List<float>();var dt=new List<int>();
        for(int p=0;p<count;p++){
            var poly=data.polys[p];int vb=dv.Count/3,tb=dt.Count/4;
            for(int i=0;i<poly.vertCount;i++)data.verts[poly.verts[i]*3+1]=vertices[p][i].Z;
            for(int i=poly.vertCount;i<vertices[p].Count;i++){var v=vertices[p][i];dv.Add(v.Y);dv.Add(v.Z);dv.Add(v.X);}
            foreach(var t in triangles[p])dt.AddRange([t.A,t.B,t.C,t.Flags]);
            data.detailMeshes[p]=new(vb,tb,checked((byte)(vertices[p].Count-poly.vertCount)),checked((byte)triangles[p].Count));
        }
        data.detailVerts=dv.ToArray();data.detailTris=dt.ToArray();data.header.detailVertCount=dv.Count/3;data.header.detailTriCount=dt.Count/4;
        return changed;
    }
}
