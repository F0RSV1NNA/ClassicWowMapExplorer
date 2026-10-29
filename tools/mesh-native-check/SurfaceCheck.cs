using System.Numerics;
using System.Reflection;
using System.Text.Json;
using MapExtract;

// Compare actual captured collision and saved Detour detail, without game files.
static class SurfaceCheck
{
    public static void Seams(string file,float[]? bounds=null)
    {
        using var stream=File.OpenRead(file);stream.Position=20;
        var data=new DotRecast.Detour.Io.DtMeshDataReader().Read(new BinaryReader(stream),6);
        Vector3 Read(float[] values,int i)=>new(values[i*3+2],values[i*3],values[i*3+1]);
        var triangles=new List<(Vector3 A,Vector3 B,Vector3 C)>[data.header.polyCount];
        for(int p=0;p<triangles.Length;p++){
            triangles[p]=new();var poly=data.polys[p];if(poly.GetPolyType()!=0)continue;var dm=data.detailMeshes[p];
            Vector3 Vertex(int i)=>i<poly.vertCount?Read(data.verts,poly.verts[i]):Read(data.detailVerts,dm.vertBase+i-poly.vertCount);
            for(int t=0;t<dm.triCount;t++){int at=(dm.triBase+t)*4;triangles[p].Add((Vertex(data.detailTris[at]),Vertex(data.detailTris[at+1]),Vertex(data.detailTris[at+2])));}
        }
        bool InScope(int p)=>bounds==null||triangles[p].Any(t=>new[]{t.A,t.B,t.C}.Any(v=>v.X>=bounds[0]&&v.X<=bounds[2]&&v.Y>=bounds[1]&&v.Y<=bounds[3]));
        double Height(int p,Vector3 q){
            double interiorHeight=double.NaN;
            foreach(var (a,b,c) in triangles[p]){
                double det=((double)b.Y-c.Y)*(a.X-c.X)+((double)c.X-b.X)*(a.Y-c.Y);if(det==0)continue;
                double u=(((double)b.Y-c.Y)*(q.X-c.X)+((double)c.X-b.X)*(q.Y-c.Y))/det,v=(((double)c.Y-a.Y)*(q.X-c.X)+((double)a.X-c.X)*(q.Y-c.Y))/det;
                if(u>=-1e-8&&v>=-1e-8&&u+v<=1.00000001)interiorHeight=u*a.Z+v*b.Z+(1-u-v)*c.Z;
            }
            // The stored float XY can sit a fraction of a millimetre outside
            // the exact coarse edge. Evaluate its nearest detail edge there.
            double nearest=.0015,height=double.NaN;
            foreach(var (a,b,c) in triangles[p])foreach(var (u,v) in new[]{(a,b),(b,c),(c,a)}){
                double dx=v.X-u.X,dy=v.Y-u.Y,len=dx*dx+dy*dy;if(len==0)continue;
                double t=Math.Clamp((((double)q.X-u.X)*dx+((double)q.Y-u.Y)*dy)/len,0,1);
                double distance=Math.Sqrt(Math.Pow(q.X-u.X-t*dx,2)+Math.Pow(q.Y-u.Y-t*dy,2));
                if(distance<nearest){nearest=distance;height=u.Z+t*(v.Z-u.Z);}
            }return double.IsFinite(height)?height:interiorHeight;
        }
        bool HorizontalFootprint(int p){
            var poly=data.polys[p];if(poly.GetPolyType()!=0)return false;
            var a=Read(data.verts,poly.verts[0]);double area=0;
            for(int i=1;i+1<poly.vertCount;i++){var b=Read(data.verts,poly.verts[i]);var c=Read(data.verts,poly.verts[i+1]);area+=((double)b.X-a.X)*(c.Y-a.Y)-((double)b.Y-a.Y)*(c.X-a.X);}
            return area!=0;
        }
        int checkedEdges=0,gaps=0,missing=0,verticalStepEdges=0;double worst=0;
        for(int p=0;p<triangles.Length;p++){
            if(!InScope(p))continue;
            var poly=data.polys[p];for(int e=0;e<poly.vertCount;e++){
                int other=poly.neis[e]-1;if(other<=p||other>=triangles.Length)continue;
                // Vertical Recast step connectors have zero XY area. This is a
                // floor coverage test; report them separately, not as empty ground.
                if(!HorizontalFootprint(p)||!HorizontalFootprint(other)){verticalStepEdges++;continue;}
                var a=Read(data.verts,poly.verts[e]);var b=Read(data.verts,poly.verts[(e+1)%poly.vertCount]);checkedEdges++;
                foreach(float t in new[]{.1f,.25f,.5f,.75f,.9f}){
                    var point=Vector3.Lerp(a,b,t);double ah=Height(p,point),bh=Height(other,point);
                    if(!double.IsFinite(ah)||!double.IsFinite(bh)){missing++;if(missing<20)Console.Error.WriteLine($"missing p={p} other={other} point={point} heights={ah},{bh}");continue;}
                    double difference=Math.Abs(ah-bh);worst=Math.Max(worst,difference);if(difference>.01){gaps++;if(gaps<20)Console.Error.WriteLine($"gap p={p} other={other} point={point} heights={ah},{bh}");}
                }
            }
        }
        int internalGaps=0;double worstInternal=0;
        for(int p=0;p<triangles.Length;p++){
            if(!InScope(p))continue;
            var points=triangles[p].SelectMany(t=>new[]{t.A,t.B,t.C}).Distinct().ToArray();
            foreach(var point in points)foreach(var (a,b,c) in triangles[p])foreach(var (u,v) in new[]{(a,b),(b,c),(c,a)}){
                double dx=v.X-u.X,dy=v.Y-u.Y,len=dx*dx+dy*dy;if(len<1e-10)continue;
                double t=(((double)point.X-u.X)*dx+((double)point.Y-u.Y)*dy)/len;
                if(t<=.00001||t>=.99999)continue;
                double distance=Math.Abs(dx*(point.Y-u.Y)-dy*(point.X-u.X))/Math.Sqrt(len);if(distance>.0001)continue;
                double delta=Math.Abs(point.Z-(u.Z+t*(v.Z-u.Z)));worstInternal=Math.Max(worstInternal,delta);
                if(delta>.02){internalGaps++;if(internalGaps<8)Console.Error.WriteLine($"internal gap p={p} point={point} difference={delta}");}
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new{bounds,checkedEdges,gaps,missing,worst,verticalStepEdges,internalGaps,worstInternal}));
        if(gaps>0||missing>0||internalGaps>0)throw new Exception("Navigation detail surfaces do not meet across linked polygon edges or internal tread edges");
    }
    public static void WalkRoute(string[] args)
    {
        var loaded=NavWriter.LoadMesh(args[1],0);if(loaded.Mesh==null||loaded.Error.Length!=0)throw new Exception(loaded.Error);
        var query=new DotRecast.Detour.DtNavMeshQuery(loaded.Mesh);var filter=new DotRecast.Detour.DtQueryDefaultFilter();
        var start=new DotRecast.Core.Numerics.RcVec3f(float.Parse(args[3]),float.Parse(args[4]),float.Parse(args[2]));
        var end=new DotRecast.Core.Numerics.RcVec3f(float.Parse(args[6]),float.Parse(args[7]),float.Parse(args[5]));
        var extent=new DotRecast.Core.Numerics.RcVec3f(2,3,2);
        var a=query.FindNearestPoly(start,extent,filter,out long first,out var from,out _);
        var b=query.FindNearestPoly(end,extent,filter,out long last,out var to,out _);
        if(a.Failed()||b.Failed()||first==0||last==0)throw new Exception("Route endpoint missing from query tree");
        long[] path=new long[8192];var status=query.FindPath(first,last,from,to,filter,path,out int count,path.Length);
        if(status.Failed()||count==0||path[count-1]!=last)throw new Exception($"Incomplete Abbey route: {status}, polygons={count}");
        Console.WriteLine(JsonSerializer.Serialize(new {status="complete",polygons=count,start=new[]{from.Z,from.X,from.Y},end=new[]{to.Z,to.X,to.Y}}));
    }
    public static void Components(string file,string output)
    {
        using var stream=File.OpenRead(file);stream.Position=20;var data=new DotRecast.Detour.Io.DtMeshDataReader().Read(new BinaryReader(stream),6);
        int[] groups=Enumerable.Repeat(-1,data.header.polyCount).ToArray();int id=0;
        for(int p=0;p<groups.Length;p++)if(groups[p]<0){var queue=new Queue<int>();queue.Enqueue(p);groups[p]=id;while(queue.Count>0){int n=queue.Dequeue();var poly=data.polys[n];for(int e=0;e<poly.vertCount;e++){int link=poly.neis[e];if(link==0||(link&0x8000)!=0)continue;int other=link-1;if(groups[other]<0){groups[other]=id;queue.Enqueue(other);}}}id++;}
        var rows=Enumerable.Range(0,groups.Length).Select(p=>new {group=groups[p],vertices=Enumerable.Range(0,data.polys[p].vertCount).Select(i=>{int v=data.polys[p].verts[i]*3;return new[]{data.verts[v+2],data.verts[v],data.verts[v+1]};}).ToArray()});
        File.WriteAllText(output,JsonSerializer.Serialize(rows));Console.WriteLine($"{id} components, sizes: "+string.Join(",",groups.GroupBy(x=>x).OrderByDescending(g=>g.Count()).Take(8).Select(g=>$"{g.Key}:{g.Count()}")));
    }
    public static void ExportPreview(string tile,string output)
    {
        MeshEngine.BeginAnalysis(0);MeshEngine.PutAnalysisFile("0000_48_32.mmtile",File.ReadAllBytes(tile));
        var bytes=MeshEngine.PreviewAnalysisTile(48,32);MeshEngine.EndAnalysis();File.WriteAllBytes(output,bytes);
        Console.WriteLine(JsonSerializer.Serialize(new{triangles=BitConverter.ToInt32(bytes),output}));
    }

    public static void CompareTiles(string first,string second)
    {
        DotRecast.Detour.DtMeshData Read(string file){using var stream=File.OpenRead(file);stream.Position=20;return new DotRecast.Detour.Io.DtMeshDataReader().Read(new BinaryReader(stream),6);}
        var a=Read(first);var b=Read(second);float largest=0;int differences=0;
        void Compare(float[] x,float[] y){if(x.Length!=y.Length)throw new Exception("Vertex counts differ");for(int i=0;i<x.Length;i++){
            if(!float.IsFinite(x[i])||!float.IsFinite(y[i]))throw new Exception("Non-finite vertex");float delta=MathF.Abs(x[i]-y[i]);if(delta>.002f)throw new Exception($"Backend vertex divergence {delta}");if(delta>0)differences++;largest=MathF.Max(largest,delta);
        }}
        Compare(a.verts,b.verts);Compare(a.detailVerts,b.detailVerts);a.verts=b.verts;a.detailVerts=b.detailVerts;
        var options=new JsonSerializerOptions{IncludeFields=true};
        if(JsonSerializer.Serialize(a,options)!=JsonSerializer.Serialize(b,options))throw new Exception("Backend topology/flags/bounds differ");
        Console.WriteLine(JsonSerializer.Serialize(new{status="pass",differences,maxCoordinateDelta=largest,tolerance=.002f,polygons=b.header.polyCount,triangles=b.header.detailTriCount}));
    }

    public static void Refit(string input,string archive,string output)
    {
        var prepared=((Extractor.Soup Soup,RoadMask Roads,int Map,int X,int Y,MapEdits.Map Edits))typeof(MeshEngine).GetMethod("PrepareRaw",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[File.ReadAllBytes(input),true,false])!;
        using var zip=System.IO.Compression.ZipFile.OpenRead(archive);
        var tile=zip.Entries.Single(e=>e.Name.EndsWith(".mmtile"));
        using var bytes=new MemoryStream();using(var stream=tile.Open())stream.CopyTo(bytes);bytes.Position=20;
        var data=new DotRecast.Detour.Io.DtMeshDataReader().Read(new BinaryReader(bytes),6);
        var fit=new SourceSurfaceFit(prepared.Soup,new NavBake.Settings(),NavBake.Clip.ForAdt(prepared.Y,prepared.X));
        int adjusted=fit.Apply(data);Directory.CreateDirectory(output);
        foreach(var entry in zip.Entries.Where(e=>e.Name.EndsWith(".mmap"))){using var stream=entry.Open();using var file=File.Create(Path.Combine(output,entry.Name));stream.CopyTo(file);}
        NavWriter.WriteTile(output,prepared.Map,new NavBake.NavTile(prepared.Y,prepared.X,data.header.x,data.header.y,data,data.header.polyCount,0,data.header.vertCount));
        var check=NavWriter.LoadBack(output,prepared.Map);if(check.Error.Length!=0)throw new Exception(check.Error);
        Console.WriteLine(JsonSerializer.Serialize(new{adjusted,check.Tiles,check.Polys,output}));
    }
    record Tri(Vector3 A, Vector3 B, Vector3 C, int Kind);
    static bool Height(Tri t, float x, float y, out float z)
    {
        float d = (t.B.Y-t.C.Y)*(t.A.X-t.C.X)+(t.C.X-t.B.X)*(t.A.Y-t.C.Y);
        z = 0; if (MathF.Abs(d)<1e-8f) return false;
        float a=((t.B.Y-t.C.Y)*(x-t.C.X)+(t.C.X-t.B.X)*(y-t.C.Y))/d;
        float b=((t.C.Y-t.A.Y)*(x-t.C.X)+(t.A.X-t.C.X)*(y-t.C.Y))/d;
        if(a < -1e-5f || b < -1e-5f || a+b > 1.00001f) return false;
        z=a*t.A.Z+b*t.B.Z+(1-a-b)*t.C.Z; return true;
    }
    public static void Run(string[] args)
    {
        float x=float.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture), y=float.Parse(args[4],System.Globalization.CultureInfo.InvariantCulture), z=float.Parse(args[5],System.Globalization.CultureInfo.InvariantCulture);
        float radius=args.Length>7?float.Parse(args[7],System.Globalization.CultureInfo.InvariantCulture):35;
        bool Near(Tri t) => MathF.Min(t.A.X,MathF.Min(t.B.X,t.C.X))<=x+radius && MathF.Max(t.A.X,MathF.Max(t.B.X,t.C.X))>=x-radius && MathF.Min(t.A.Y,MathF.Min(t.B.Y,t.C.Y))<=y+radius && MathF.Max(t.A.Y,MathF.Max(t.B.Y,t.C.Y))>=y-radius;
        var prepared=((Extractor.Soup Soup,RoadMask Roads,int Map,int X,int Y,MapEdits.Map Edits))typeof(MeshEngine).GetMethod("PrepareRaw",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[File.ReadAllBytes(args[1]),true,false])!;
        var source=new List<Tri>();
        for(int i=0;i<prepared.Soup.Indices.Count;i+=3) {
            var t=new Tri(prepared.Soup.Verts[prepared.Soup.Indices[i]],prepared.Soup.Verts[prepared.Soup.Indices[i+1]],prepared.Soup.Verts[prepared.Soup.Indices[i+2]],prepared.Soup.TriKind[i/3]);
            if(Near(t)) source.Add(t);
        }
        source=source.Distinct().ToList();
        var floorsAtSource=source.Where(t=>t.Kind!=2 && MathF.Abs(Vector3.Normalize(Vector3.Cross(t.B-t.A,t.C-t.A)).Z)>=MathF.Cos(50*MathF.PI/180)).ToList();
        MeshEngine.BeginAnalysis(prepared.Map);
        MeshEngine.PutAnalysisFile(Path.GetFileName(args[2]),File.ReadAllBytes(args[2]));
        using var r=new BinaryReader(new MemoryStream(MeshEngine.PreviewAnalysisTile(prepared.Y,prepared.X)));
        var mesh=new List<Tri>(); int count=r.ReadInt32();
        Vector3 V()=>new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
        for(int i=0;i<count;i++){int area=r.ReadInt32();r.ReadInt32();var t=new Tri(V(),V(),V(),area);if(Near(t))mesh.Add(t);}
        MeshEngine.EndAnalysis();
        var samples=new List<object>(); int missing=0, buried=0, compared=0; float worst=0;
        for(float a=x-radius;a<=x+radius;a+=.5f)for(float b=y-radius;b<=y+radius;b+=.5f){
            var floors=floorsAtSource.Select(t=>Height(t,a,b,out float h)?h:float.NaN).Where(h=>float.IsFinite(h) && h<=z+1).ToArray();
            if(floors.Length==0)continue;float ground=floors.OrderBy(h=>MathF.Abs(h-z)).First();
            var heights=mesh.Where(t=>t.Kind==NavBake.AreaGround || t.Kind==NavBake.AreaRoad).Select(t=>Height(t,a,b,out float h)?h:float.NaN).Where(h=>float.IsFinite(h)&&MathF.Abs(h-ground)<3).ToArray();
            if(heights.Length==0){missing++;continue;}float nav=heights.OrderBy(h=>MathF.Abs(h-ground)).First(),delta=nav-ground;compared++;worst=MathF.Min(worst,delta);if(delta<-.05)buried++;
            if(delta<-.2)samples.Add(new{ x=a,y=b,ground,nav,delta });
        }
        var options=new JsonSerializerOptions{IncludeFields=true};
        File.WriteAllText(args[6],JsonSerializer.Serialize(new{center=new{x,y,z},source,mesh,compared,missing,buried,worst,samples},options));
        Console.WriteLine(JsonSerializer.Serialize(new{source=source.Count,mesh=mesh.Count,compared,missing,buried,worst,output=args[6]}));
    }
}

