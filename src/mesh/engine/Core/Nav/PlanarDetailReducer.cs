using DotRecast.Detour;
namespace MapExtract;

// Remove interior detail samples only when the COMPLETE existing surface is
// planar. Keep every boundary sample, polygon, flag and portal unchanged.
internal static class PlanarDetailReducer
{
    internal static int Reduce(DtMeshData data)
    {
        var vertices=new List<float>();var triangles=new List<int>();int removed=0;
        for(int p=0;p<data.detailMeshes.Length;p++){
            var poly=data.polys[p];var dm=data.detailMeshes[p];int oldBase=dm.vertBase,oldTris=dm.triBase;
            var output=new List<(int A,int B,int C,int Flags)>();
            for(int t=0;t<dm.triCount;t++){int at=(oldTris+t)*4;output.Add((data.detailTris[at],data.detailTris[at+1],data.detailTris[at+2],data.detailTris[at+3]));}
            (double X,double Y,double Z) Point(int id){var v=id<poly.vertCount?data.verts:data.detailVerts;int at=(id<poly.vertCount?poly.verts[id]:oldBase+id-poly.vertCount)*3;return(v[at],v[at+1],v[at+2]);}
            double Cross(int a,int b,int c){var u=Point(a);var v=Point(b);var w=Point(c);return(v.X-u.X)*(w.Z-u.Z)-(v.Z-u.Z)*(w.X-u.X);}
            List<(int A,int B,int C,int Flags)> Simplify(List<(int A,int B,int C,int Flags)> patch)
            {
            if(patch.Count>1){
                var edges=new Dictionary<(int,int),(int Count,int A,int B,int Flag)>();
                foreach(var t in patch){int[] ids=[t.A,t.B,t.C];for(int e=0;e<3;e++){int a=ids[e],b=ids[(e+1)%3];var key=a<b?(a,b):(b,a);var old=edges.GetValueOrDefault(key);edges[key]=(old.Count+1,a,b,(t.Flags>>(2*e))&3);}}
                var boundary=edges.Values.Where(e=>e.Count==1).ToArray();var next=new Dictionary<int,int>();bool valid=edges.Values.All(e=>e.Count<=2);
                foreach(var e in boundary)if(!next.TryAdd(e.A,e.B))valid=false;
                var hull=new List<int>();
                if(valid&&boundary.Length>=3){int start=boundary[0].A,at=start;do{hull.Add(at);if(!next.TryGetValue(at,out at)){valid=false;break;}}while(at!=start&&hull.Count<=boundary.Length);valid&=at==start&&hull.Count==boundary.Length;}
                else valid=false;
                if(valid){
                    var seed=patch.FirstOrDefault(t=>Math.Abs(Cross(t.A,t.B,t.C))>1e-10);var a=Point(seed.A);var b=Point(seed.B);var c=Point(seed.C);
                    double nx=(b.Y-a.Y)*(c.Z-a.Z)-(b.Z-a.Z)*(c.Y-a.Y),ny=(b.Z-a.Z)*(c.X-a.X)-(b.X-a.X)*(c.Z-a.Z),nz=(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
                    valid=Math.Abs(ny)>1e-10;
                    foreach(int id in patch.SelectMany(t=>new[]{t.A,t.B,t.C}).Distinct()){
                        var q=Point(id);if(!double.IsFinite(q.Y)||Math.Abs(nx*(q.X-a.X)+ny*(q.Y-a.Y)+nz*(q.Z-a.Z))>Math.Abs(ny)*.0001)valid=false;
                    }
                    double sign=Math.Sign(Cross(seed.A,seed.B,seed.C));
                    // Restrict to a single convex footprint. Holes/concavity and
                    // nonplanar relief keep the complete original triangulation.
                    for(int i=0;i<hull.Count;i++)if(Cross(hull[(i+hull.Count-1)%hull.Count],hull[i],hull[(i+1)%hull.Count])*sign< -1e-10)valid=false;
                    var reduced=new List<(int A,int B,int C,int Flags)>();var ring=new List<int>(hull);
                    int Flags(int x,int y){var key=x<y?(x,y):(y,x);return edges.TryGetValue(key,out var e)&&e.Count==1?e.Flag:0;}
                    void Add(int x,int y,int z)=>reduced.Add((x,y,z,Flags(x,y)|(Flags(y,z)<<2)|(Flags(z,x)<<4)));
                    while(valid&&ring.Count>3){bool cut=false;
                        for(int i=0;i<ring.Count;i++){
                            int x=ring[(i+ring.Count-1)%ring.Count],y=ring[i],z=ring[(i+1)%ring.Count];if(Cross(x,y,z)*sign<=1e-10)continue;
                            if(ring.Any(q=>q!=x&&q!=y&&q!=z&&Cross(x,y,q)*sign>=-1e-10&&Cross(y,z,q)*sign>=-1e-10&&Cross(z,x,q)*sign>=-1e-10))continue;
                            Add(x,y,z);ring.RemoveAt(i);cut=true;break;
                        }
                        if(!cut)valid=false;
                    }
                    if(valid&&ring.Count==3&&Cross(ring[0],ring[1],ring[2])*sign>1e-10){Add(ring[0],ring[1],ring[2]);if(reduced.Count<patch.Count)patch=reduced;}
                }
            }
                return patch;
            }
            if(poly.GetPolyType()==0){
                output=Simplify(output);
                var edgeCounts=new Dictionary<(int,int),int>();
                foreach(var t in output){int[] ids=[t.A,t.B,t.C];for(int e=0;e<3;e++){int a=ids[e],b=ids[(e+1)%3];var key=a<b?(a,b):(b,a);edgeCounts[key]=edgeCounts.GetValueOrDefault(key)+1;}}
                var boundaryIds=edgeCounts.Where(e=>e.Value==1).SelectMany(e=>new[]{e.Key.Item1,e.Key.Item2}).ToHashSet();
                var candidates=output.SelectMany(t=>new[]{t.A,t.B,t.C}).Where(i=>i>=poly.vertCount&&!boundaryIds.Contains(i)).Distinct().Order().ToArray();
                foreach(int id in candidates){
                    var fan=output.Where(t=>t.A==id||t.B==id||t.C==id).ToList();if(fan.Count<3)continue;
                    var reduced=Simplify(fan);
                    if(reduced.Count>=fan.Count||reduced.Any(t=>t.A==id||t.B==id||t.C==id))continue;
                    output.RemoveAll(t=>t.A==id||t.B==id||t.C==id);output.AddRange(reduced);
                }
            }
            var used=output.SelectMany(t=>new[]{t.A,t.B,t.C}).Where(i=>i>=poly.vertCount).Distinct().Order().ToArray();
            var remap=new Dictionary<int,int>();int baseVert=vertices.Count/3;
            foreach(int id in used){remap[id]=poly.vertCount+remap.Count;int at=(oldBase+id-poly.vertCount)*3;vertices.AddRange([data.detailVerts[at],data.detailVerts[at+1],data.detailVerts[at+2]]);}
            int Map(int i)=>i<poly.vertCount?i:remap[i];
            data.detailMeshes[p]=new(baseVert,triangles.Count/4,checked((byte)used.Length),checked((byte)output.Count));
            foreach(var t in output)triangles.AddRange([Map(t.A),Map(t.B),Map(t.C),t.Flags]);
        }
        removed=data.detailVerts.Length/3-vertices.Count/3;
        data.detailVerts=vertices.ToArray();data.detailTris=triangles.ToArray();
        data.header.detailVertCount=vertices.Count/3;data.header.detailTriCount=triangles.Count/4;
        return removed;
    }
}
