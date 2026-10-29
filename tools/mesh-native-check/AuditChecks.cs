using System.Numerics;
using DotRecast.Core;
using DotRecast.Recast;
using MapExtract;
using System.Reflection;

internal static class AuditChecks
{
    internal static void Authoring(string directory)
    {
        Directory.CreateDirectory(directory);
        var edits=new BakeEdits();
        edits.Map.Roads.AddRange([
            new(){X=-24,Y=-24,Z=10,Radius=5,HeightBand=.5f,Kind=MapEdits.StrokeKind.PaintRoad},
            new(){X=-10,Y=-10,Z=10,Radius=3,HeightBand=.5f,Kind=MapEdits.StrokeKind.Block},
            new(){X=-10,Y=-24,Z=10,Radius=3,HeightBand=.5f,Kind=MapEdits.StrokeKind.EraseMesh},
            new(){X=-10,Y=-24,Z=10,Radius=1.5f,HeightBand=.5f,Kind=MapEdits.StrokeKind.Restore}]);
        edits.Map.OffMesh.AddRange([
            new(){Ax=-24,Ay=-24,Az=10,Bx=-6,By=-6,Bz=10,Mode="portal",UserId=7001,Note="fixture portal"},
            new(){Ax=-24,Ay=-6,Az=10,Bx=-6,By=-24,Bz=10,Mode="walk",UserId=7002}]);
        using var input=new MemoryStream();using(var w=new BinaryWriter(input,System.Text.Encoding.UTF8,true))
        {
            w.Write(0x31424D45);w.Write(0);w.Write(32);w.Write(32);w.Write(0f);w.Write(0f);w.Write(0f);
            w.Write(8);w.Write(12);
            foreach(float z in new[]{10f,14f})foreach(var p in new[]{(-30f,-30f),(-2f,-30f),(-2f,-2f),(-30f,-2f)}){w.Write(p.Item2);w.Write(z);w.Write(p.Item1);}
            foreach(int i in new[]{0,1,2,0,2,3,4,5,6,4,6,7})w.Write(i);
            w.Write(new byte[4]);w.Write(0);w.Write(0);
            byte[] json=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(edits,EngineJson.Default.BakeEdits);w.Write(json.Length);w.Write(json);
        }
        File.WriteAllBytes(Path.Combine(directory,"authoring.input"),input.ToArray());
        byte[] output=MeshEngine.Bake(input.ToArray());File.WriteAllBytes(Path.Combine(directory,"authoring.zip"),output);
        using(var zip=new System.IO.Compression.ZipArchive(new MemoryStream(output)))
        {
            using(var preview=new BinaryReader(zip.GetEntry("preview.bin")!.Open()))
            {
                bool restored=false,cutCovered=false;int triangles=preview.ReadInt32();
                for(int t=0;t<triangles;t++)
                {
                    preview.ReadInt32();var v=new System.Numerics.Vector3[3];for(int j=0;j<3;j++)v[j]=new(preview.ReadSingle(),preview.ReadSingle(),preview.ReadSingle());
                    if(v.Any(p=>p.Z>12))continue;
                    bool Contains(float x,float y){float Cross(System.Numerics.Vector3 a,System.Numerics.Vector3 b)=>(b.X-a.X)*(y-a.Y)-(b.Y-a.Y)*(x-a.X);float a=Cross(v[0],v[1]),b=Cross(v[1],v[2]),c=Cross(v[2],v[0]);return(a>=0&&b>=0&&c>=0)||(a<=0&&b<=0&&c<=0);}
                    restored|=Contains(-10,-24);cutCovered|=Contains(-10,-21.5f);
                }
                if(!restored||cutCovered)throw new Exception("Cut/restore coverage did not match the authored strokes");
            }
            foreach(var entry in zip.Entries.Where(e=>e.Name.EndsWith(".mmap")||e.Name.EndsWith(".mmtile")||e.Name.EndsWith(".links.json")))
            {using var source=entry.Open();using var file=File.Create(Path.Combine(directory,entry.Name));source.CopyTo(file);}
        }
        var loaded=NavWriter.LoadMesh(directory,0);if(loaded.Error.Length!=0||loaded.Mesh==null)throw new Exception(loaded.Error);
        bool road=false,blocked=false,walk=false,portal=false,upper=false;
        foreach(var tile in NavPrune.All(loaded.Mesh))foreach(var p in tile.data.polys.Take(tile.data.header.polyCount))
        {
            if(p.GetPolyType()!=0){if(p.GetArea()==NavBake.AreaTransport){portal=true;if(p.flags!=0)throw new Exception("Portal became ordinary walking");}else walk|=(p.flags&NavBake.FlagGround)!=0;continue;}
            float height=Enumerable.Range(0,p.vertCount).Average(i=>tile.data.verts[p.verts[i]*3+1]);
            if(height>13){upper=true;if(p.GetArea() is NavBake.AreaRoad or NavBake.AreaBlocked)throw new Exception("Brush reached the other storey");}
            else{road|=p.GetArea()==NavBake.AreaRoad;if(p.GetArea()==NavBake.AreaBlocked){blocked=true;if(p.flags!=0)throw new Exception("Manual block retained walking flags");}}
        }
        if(!road||!blocked||!walk||!portal||!upper||!File.Exists(Path.Combine(directory,"0000_32_32.links.json")))throw new Exception("Missing authored surface, link, upper floor or action metadata");
        var report=NavPrune.Run(directory,0);if(report.Error.Length!=0)throw new Exception(report.Error);
        var again=NavWriter.LoadMesh(directory,0);
        foreach(var t in NavPrune.All(again.Mesh!))foreach(var p in t.data.polys.Take(t.data.header.polyCount))
            if(p.GetArea() is NavBake.AreaBlocked or NavBake.AreaTransport && (p.flags&NavBake.FlagsAnySurface)!=0)throw new Exception("Reachability restored permission on a manual block/action link");
        MeshEngine.BeginAnalysis(0);
        foreach(string f in Directory.GetFiles(directory).Where(f=>f.EndsWith(".mmap")||f.EndsWith(".mmtile")))MeshEngine.PutAnalysisFile(Path.GetFileName(f),File.ReadAllBytes(f));
        byte[] graph=MeshEngine.StreamGraph(32,32);int count=BitConverter.ToInt32(graph);
        foreach(var t in NavPrune.All(again.Mesh!))for(int p=0;p<t.data.header.polyCount;p++)
            if(t.data.polys[p].GetArea()==NavBake.AreaBlocked&&BitConverter.ToInt32(graph,8+count*16+p*4)!=BitConverter.ToInt32(graph,8+count*16+(p+1)*4))throw new Exception("Streamed reachability crossed a manually blocked polygon");
        MeshEngine.EndAnalysis();
        var mask=RoadMask.Build(_=>null,[],new(),new());
        var texture=new Tex0();texture.Textures.AddRange([11,22]);texture.Chunks[0]=(new[]{0,1},new byte[]?[]{null,Enumerable.Repeat((byte)128,4096).ToArray()});
        var textureTiles=(Dictionary<(int,int),Tex0>)typeof(RoadMask).GetField("_tiles",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(mask)!;textureTiles[(32,32)]=texture;
        var layers=mask.LayersAt(-.1f,-.1f).ToDictionary(l=>l.Id,l=>l.Weight);
        if(layers[11]!=127||layers[22]!=128)throw new Exception("Texture inspector did not expose all blended layers");
        if(RoadMask.RoadByName("roads/craterrock.blp")||RoadMask.RoadByName("road/grass.blp")||!RoadMask.RoadByName("durotarroad01.blp"))throw new Exception("Road texture basename/crater classification failed");
        Console.WriteLine("Authoring passed: JSON roundtrip, road paint, cut/restore, persistent blocks, protected upper floor, walking/action links, metadata, loadback, both reachability paths and blended texture inspection.");
    }
    internal static void LocalSource(string json,string output)
    {
        using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
        var soup=new Extractor.Soup();
        foreach(var triangle in doc.RootElement.GetProperty("source").EnumerateArray())
        {
            int start=soup.Verts.Count;
            foreach(string key in new[]{"A","B","C"})
            {
                var p=triangle.GetProperty(key);soup.Verts.Add(new(p.GetProperty("X").GetSingle(),p.GetProperty("Y").GetSingle(),p.GetProperty("Z").GetSingle()));
            }
            soup.Indices.AddRange([start,start+1,start+2]);byte kind=triangle.GetProperty("Kind").GetByte();
            soup.TriKind.Add(kind);soup.TriTerrain.Add(kind==0);
            if(kind==0)soup.TerrainTris++;else if(kind==1)soup.WmoTris++;else soup.DoodadTris++;
        }
        if(Environment.GetEnvironmentVariable("EXPLORER_MESH_STAGES")=="1"){var caps=WmoCapPatches.Detect(soup,new NavBake.Settings(),localObstaclesOnly:true);Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"source-caps.json"),System.Text.Json.JsonSerializer.Serialize(caps.Select(t=>new {A=soup.Verts[soup.Indices[t*3]],B=soup.Verts[soup.Indices[t*3+1]],C=soup.Verts[soup.Indices[t*3+2]]}),new System.Text.Json.JsonSerializerOptions{IncludeFields=true}));}
        var settings=new NavBake.Settings();
        if(float.TryParse(Environment.GetEnvironmentVariable("EXPLORER_CHECK_CELL_HEIGHT"),out float cell))settings.CellHeight=cell;
        if(float.TryParse(Environment.GetEnvironmentVariable("EXPLORER_CHECK_MAX_CLIMB"),out float climb))settings.AgentMaxClimb=climb;
        if(Environment.GetEnvironmentVariable("EXPLORER_COVERAGE_AUDIT")=="1"){
            var caps=WmoCapPatches.Detect(soup,settings,localObstaclesOnly:true);var body=new SourceBodyClearance(soup,settings,caps);
            SourceSurfaceFit.CoverageObserver=(source,data)=>CoverageAudit.Write(source,body,data,Path.Combine(output,"coverage-audit.json"),settings.CellHeight);
        }
        var result=NavBake.Bake(soup,settings);
        if(!result.Ok)throw new Exception(result.Message);
        NavWriter.WriteAll(output,0,result);
        File.WriteAllText(Path.Combine(output,"preview.json"),System.Text.Json.JsonSerializer.Serialize(new {verts=result.DebugVerts,indices=result.DebugIndices,areas=result.DebugAreas},new System.Text.Json.JsonSerializerOptions{IncludeFields=true}));
        var check=NavWriter.LoadBack(output,0);if(check.Error.Length!=0)throw new Exception(check.Error);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {result.PolyCount,result.TileCount,result.PlanarDetailVerticesRemoved,result.RaisedWmoCapsBlocked,result.IndoorCells,result.TrimCells,result.RailingCells,result.SwimBuildFellBack,result.FailMessage,check.Error}));
    }

    internal static void WmoCollisionFlags()
    {
        byte[] flags=[0x00,0x04,0x08,0x0c,0x10,0x20,0x24,0x28,0x2c,0x40,0x80,0x22];
        int[] kept=[2,3,5,7,8,11];
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,System.Text.Encoding.ASCII,true);
        void Chunk(string name,Action<BinaryWriter> write){using var bytes=new MemoryStream();using(var body=new BinaryWriter(bytes,System.Text.Encoding.ASCII,true))write(body);writer.Write(System.Text.Encoding.ASCII.GetBytes(new string(name.Reverse().ToArray())));writer.Write((uint)bytes.Length);writer.Write(bytes.ToArray());}
        Chunk("MOPY",w=>{foreach(byte flag in flags){w.Write(flag);w.Write((byte)0xff);}});
        Chunk("MOVI",w=>{for(int i=0;i<flags.Length*3;i++)w.Write((ushort)i);});
        Chunk("MOVT",w=>{for(int t=0;t<flags.Length;t++)foreach(var v in new[]{new Vector3(t*2,0,0),new Vector3(t*2+1,0,0),new Vector3(t*2,1,0)}){w.Write(v.X);w.Write(v.Y);w.Write(v.Z);}});
        stream.Position=0;var group=Wmo.ReadGroup(stream);
        if(!group.Solid.Indices.SequenceEqual(kept.SelectMany(t=>new[]{t*3,t*3+1,t*3+2})))throw new Exception("Physical WMO extraction omitted explicit collision or retained nonphysical detail/hints");
        Console.WriteLine("WMO extraction passed: hidden collision, detail+collision override, rendered physical faces and camera-only flags; hint/detail/nonphysical faces excluded.");
    }

    internal static void CollisionDetail()
    {
        const float x0=-8910,y0=-218,z0=81.94f;
        var soup=new Extractor.Soup();
        void Triangle(Vector3 a,Vector3 b,Vector3 c){int i=soup.Verts.Count;soup.Verts.AddRange([a,b,c]);soup.Indices.AddRange([i,i+1,i+2]);soup.WmoTris++;}
        Vector3 V(float x,float y,float z)=>new(x0+x,y0+y,z0+z);
        Triangle(V(0,0,0),V(4,0,0),V(4,2,0));Triangle(V(0,0,0),V(4,2,0),V(0,2,0));
        // Duplicate the lower collision plane using a different diagonal.
        Triangle(V(0,0,0),V(4,0,0),V(0,2,0));Triangle(V(4,0,0),V(4,2,0),V(0,2,0));
        Triangle(V(2,0,.5f),V(4,0,.5f),V(4,2,.5f));Triangle(V(2,0,.5f),V(4,2,.5f),V(2,2,.5f));soup.Seal();
        var settings=new NavBake.Settings();var fit=new SourceSurfaceFit(soup,settings,null);
        var poly=new DotRecast.Detour.DtPoly(0,6){vertCount=4,flags=NavBake.FlagGround};poly.SetArea(NavBake.AreaGround);for(int i=0;i<4;i++)poly.verts[i]=i;
        var data=new DotRecast.Detour.DtMeshData{header=new(){polyCount=1,vertCount=4,detailMeshCount=1,detailTriCount=2,bmin=new(y0,80,x0),bmax=new(y0+2,85,x0+4),bvQuantFactor=1},
            verts=[y0,z0+.266f,x0, y0,z0+.766f,x0+4, y0+2,z0+.766f,x0+4, y0+2,z0+.266f,x0],polys=[poly],detailMeshes=[new(0,0,0,2)],detailVerts=[],detailTris=[0,1,2,0,0,2,3,0],bvTree=[]};
        fit.Apply(data);
        if(data.header.polyCount!=1||data.detailMeshes[0].triCount!=2)throw new Exception("Source fitting replaced the continuous Recast step envelope");
        var detail=data.detailMeshes[0];double area=0;
        Vector3 Read(int local){var values=local<poly.vertCount?data.verts:data.detailVerts;int at=(local<poly.vertCount?poly.verts[local]:detail.vertBase+local-poly.vertCount)*3;return new(values[at+2],values[at],values[at+1]);}
        var triangles=new List<(Vector3 A,Vector3 B,Vector3 C)>();
        for(int t=0;t<detail.triCount;t++){
            int at=(detail.triBase+t)*4;var a=Read(data.detailTris[at]);var b=Read(data.detailTris[at+1]);var c=Read(data.detailTris[at+2]);triangles.Add((a,b,c));
            area+=Math.Abs(((double)b.X-a.X)*(c.Y-a.Y)-((double)b.Y-a.Y)*(c.X-a.X))*.5;
        }
        if(Math.Abs(area-8)>.002)throw new Exception("Continuous step envelope lost floor coverage");
        for(float x=.1f;x<4;x+=.2f){bool covered=false;float floor=z0+(x<2?0:.5f);
            foreach(var (a,b,c) in triangles){
                double det=((double)b.Y-c.Y)*(a.X-c.X)+((double)c.X-b.X)*(a.Y-c.Y);
                double u=(((double)b.Y-c.Y)*(x0+x-c.X)+((double)c.X-b.X)*(y0+1-c.Y))/det,v=(((double)c.Y-a.Y)*(x0+x-c.X)+((double)a.X-c.X)*(y0+1-c.Y))/det;
                if(u<-.0001||v<-.0001||u+v>1.0001)continue;
                double height=u*a.Z+v*b.Z+(1-u-v)*c.Z;
                if(height<floor-.01||height>floor+.9)throw new Exception("Continuous step envelope misses collision height");covered=true;
            }
            if(!covered)throw new Exception("Disconnected walking envelope between stair treads");
        }
        Console.WriteLine("Continuous step envelope passed: original Recast topology and coverage retained, legal step crossed without separate tread surfaces.");
        var weld=typeof(SourceSurfaceFit).GetMethod("StitchDetailBoundaries",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var lower=new DotRecast.Detour.DtPoly(0,3){vertCount=3};var upper=new DotRecast.Detour.DtPoly(1,3){vertCount=3};
        for(int i=0;i<3;i++){lower.verts[i]=i;upper.verts[i]=i+3;}
        var disconnected=new DotRecast.Detour.DtMeshData{header=new(){polyCount=2,vertCount=6},polys=[lower,upper],
            verts=[0,0,0, 1,0,0, 0,0,1, 0,.12f,0, 1,.12f,0, 0,.12f,1],
            detailMeshes=[new(0,0,1,0),new(1,0,0,0)],detailVerts=[.5f,.7f,0],detailTris=[]};
        weld.Invoke(fit,[disconnected]);
        if(disconnected.verts[1]!=0||disconnected.verts[10]!=.12f)throw new Exception("Boundary weld joined disconnected floors");
        if(disconnected.detailVerts[1]!=.7f)throw new Exception("Boundary weld flattened a curved edge sample");
        lower.neis[0]=2;upper.neis[0]=1;weld.Invoke(fit,[disconnected]);
        if(disconnected.verts[1]!=.12f)throw new Exception("Adjacent coincident floor samples were not reconciled");
        Console.WriteLine("Boundary weld passed: connected copies agree, disconnected floors and curved sampling remain intact.");
    }

    internal static void SurfaceRules()
    {
        static float DetailHeight(DotRecast.Detour.DtMeshData mesh,float x,float y){
            float result=float.NaN;for(int p=0;p<mesh.detailMeshes.Length;p++){
                var poly=mesh.polys[p];var dm=mesh.detailMeshes[p];
                Vector3 Read(int local){var values=local<poly.vertCount?mesh.verts:mesh.detailVerts;int at=(local<poly.vertCount?poly.verts[local]:dm.vertBase+local-poly.vertCount)*3;return new(values[at+2],values[at],values[at+1]);}
                for(int t=0;t<dm.triCount;t++){
                    int at=(dm.triBase+t)*4;var a=Read(mesh.detailTris[at]);var b=Read(mesh.detailTris[at+1]);var c=Read(mesh.detailTris[at+2]);
                    float det=(b.Y-c.Y)*(a.X-c.X)+(c.X-b.X)*(a.Y-c.Y);if(Math.Abs(det)<1e-8)continue;
                    float u=((b.Y-c.Y)*(x-c.X)+(c.X-b.X)*(y-c.Y))/det,v=((c.Y-a.Y)*(x-c.X)+(a.X-c.X)*(y-c.Y))/det;
                    if(u>=-1e-5&&v>=-1e-5&&u+v<=1.00001f){float z=u*a.Z+v*b.Z+(1-u-v)*c.Z;result=float.IsFinite(result)?MathF.Max(result,z):z;}
                }
            }return result;
        }
        var settings=new NavBake.Settings{AgentRadius=.5f,AgentHeight=2,AgentMaxClimb=1};
        var ceilingSoup=new Extractor.Soup();
        ceilingSoup.Verts.AddRange([new(0,0,0),new(20,0,0),new(20,20,0),new(0,20,0),new(5,5,.8f),new(5,15,.8f),new(15,15,.8f),new(15,5,.8f)]);
        ceilingSoup.Indices.AddRange([0,1,2,0,2,3,4,5,6,4,6,7]);ceilingSoup.WmoTris=4;ceilingSoup.Seal();
        var ceilingBake=NavBake.Bake(ceilingSoup,settings);
        if(!ceilingBake.Ok||ceilingBake.DebugVerts.Any(v=>v.Z>.55f))throw new Exception("A down-facing WMO ceiling was promoted to walking surface");
        DotRecast.Recast.RcHeightfield Field(bool stairs=false)
        {
            var field=new RcHeightfield(9,9,new(0,0,0),new(3,10,3),.25f,.1f,0);
            for(int z=0;z<9;z++)for(int x=0;x<9;x++)field.spans[x+z*9]=new(){smin=0,smax=stairs?10+x*2:10,area=NavBake.AreaGround};
            return field;
        }
        var physical=Field();physical.spans[2+4*9].smax=50;physical.spans[2+4*9].area=NavBake.AreaNull;
        var clearField=RcCompacts.BuildCompactHeightfield(new RcContext(),20,10,physical);var beforeClear=(int[])clearField.areas.Clone();
        int near=clearField.cells[3+4*9].index,far=clearField.cells[6+4*9].index;
        NavBake.ErodeSolidIndoorWalls(clearField,physical,Enumerable.Repeat(true,clearField.spanCount).ToArray(),beforeClear,settings);
        if(clearField.areas[near]!=0||beforeClear[near]!=0||clearField.areas[far]==0)throw new Exception("Body wall clearance removed open floor or left a restorable wall overlap");
        var trueStairs=Field(true);var stairField=RcCompacts.BuildCompactHeightfield(new RcContext(),20,10,trueStairs);
        if(NavBake.ErodeSolidIndoorWalls(stairField,trueStairs,Enumerable.Repeat(true,stairField.spanCount).ToArray(),(int[])stairField.areas.Clone(),settings)!=0)throw new Exception("Legal treads were treated as body-height walls");
        foreach(var mode in new[]{"enclosed","protected","open"}){
            var hole=RcCompacts.BuildCompactHeightfield(new RcContext(),20,10,Field());int at=hole.cells[4+4*9].index;
            hole.areas[at]=0;var protect=new bool[hole.spanCount];protect[at]=mode=="protected";
            if(mode=="open"){
                var span=hole.spans[at];
                var builder=RcCompactSpanBuilder.NewBuilder(ref hole.spans[at]);builder.con=(span.con&~63)|63;hole.spans[at]=builder.Build();
            }
            NavBake.FillPinholes(hole,4,out _,protect);
            if((hole.areas[at]!=0)!=(mode=="enclosed"))throw new Exception("Pinhole fill either restored intentional clearance/open boundaries or lost a truly enclosed floor hole: "+mode);
        }
        var capSoup=new Extractor.Soup();
        void Tri(Vector3 a,Vector3 b,Vector3 c){int n=capSoup.Verts.Count;capSoup.Verts.AddRange([a,b,c]);capSoup.Indices.AddRange([n,n+1,n+2]);capSoup.TriKind.Add(Extractor.Soup.KindWmo);capSoup.WmoTris++;}
        // One coplanar floor extends through a wall into an exterior footing.
        // The lower terrain is exposed outside and buried inside. Local
        // classification must distinguish those two sides of the same patch.
        Tri(new(-6,-6,0),new(6,-6,0),new(6,6,0));Tri(new(-6,-6,0),new(6,6,0),new(-6,6,0));
        capSoup.TriKind[0]=capSoup.TriKind[1]=Extractor.Soup.KindTerrain;
        Tri(new(-5,-5,1),new(1,-5,1),new(1,5,1));Tri(new(-5,-5,1),new(1,5,1),new(-5,5,1));
        Tri(new(0,-5,1),new(0,5,1),new(0,5,5));Tri(new(0,-5,1),new(0,5,5),new(0,-5,5));capSoup.Seal();
        var footing=new SourceSurfaceFit(capSoup,settings,null);
        if(!footing.IsWallFooting(.5f,0,1.2f)||footing.IsWallFooting(-.5f,0,1.2f)||footing.IsWallFooting(-3,0,1.2f))
            throw new Exception("Local wall-footing rejection either retained the exterior curb or removed the real interior floor");
        // Broad tabletops are not second floors. An underside over a real
        // nearby WMO floor provides the additional evidence a landing lacks.
        capSoup=new Extractor.Soup();
        Tri(new(-5,-5,0),new(5,-5,0),new(5,5,0));Tri(new(-5,-5,0),new(5,5,0),new(-5,5,0));
        Tri(new(-2,-2,1.2f),new(2,-2,1.2f),new(2,2,1.2f));Tri(new(-2,-2,1.2f),new(2,2,1.2f),new(-2,2,1.2f));
        Tri(new(-2,-2,.8f),new(2,2,.8f),new(2,-2,.8f));Tri(new(-2,-2,.8f),new(-2,2,.8f),new(2,2,.8f));capSoup.Seal();
        var tableCaps=WmoCapPatches.Detect(capSoup,settings);
        if(!tableCaps.IsSupersetOf(new[]{2,3})||tableCaps.Overlaps(new[]{0,1}))throw new Exception("Broad suspended tabletop was retained or its underlying room floor was removed");
        capSoup=new Extractor.Soup();
        Tri(new(-5,-5,0),new(5,-5,0),new(5,5,0));Tri(new(-5,-5,0),new(5,5,0),new(-5,5,0));
        Tri(new(-.2f,-2,.8f),new(.2f,-2,.8f),new(.2f,2,.8f));Tri(new(-.2f,-2,.8f),new(.2f,2,.8f),new(-.2f,2,.8f));
        // A two-yard square landing has real standing room and must survive.
        Tri(new(2,-1,.8f),new(4,-1,.8f),new(4,1,.8f));Tri(new(2,-1,.8f),new(4,1,.8f),new(2,1,.8f));
        capSoup.Seal();var caps=WmoCapPatches.Detect(capSoup,settings);
        if(!caps.SetEquals(new[]{2,3}))throw new Exception("Source cap classification deleted a floor/landing or retained a narrow raised shelf");
        foreach(int triangle in new[]{2,3}){
            var a=capSoup.Verts[capSoup.Indices[triangle*3]];var b=capSoup.Verts[capSoup.Indices[triangle*3+1]];var c=capSoup.Verts[capSoup.Indices[triangle*3+2]];
            Tri(b,c,a);
        }
        if(!WmoCapPatches.Detect(capSoup,settings).SetEquals(new[]{2,3,6,7}))throw new Exception("Duplicate neighbouring collision hid a raised shelf's patch boundary");
        // Even a narrow landing is valid when it joins a continuous ramp.
        Tri(new(-.2f,2,.8f),new(.2f,2,.8f),new(.2f,4,0));
        caps=WmoCapPatches.Detect(capSoup,settings);
        if(caps.Contains(2)||caps.Contains(3))throw new Exception("Ramp-connected landing was classified as furniture");
        capSoup=new Extractor.Soup();
        for(int step=0;step<5;step++){
            float x=step*.4f,z=step*.4f;
            Tri(new(x,0,z),new(x+.4f,0,z),new(x+.4f,2,z));Tri(new(x,0,z),new(x+.4f,2,z),new(x,2,z));
        }
        capSoup.Seal();
        if(WmoCapPatches.Detect(capSoup,settings).Count!=0)throw new Exception("Actual flat stair treads were classified as raised shelves");
        capSoup=new Extractor.Soup();
        Tri(new(0,-1.5f,0),new(4,-1.5f,2),new(4,1.5f,2));Tri(new(0,-1.5f,0),new(4,1.5f,2),new(0,1.5f,0));
        Tri(new(0,0,1),new(4,0,3),new(4,.3f,3));Tri(new(0,0,1),new(4,.3f,3),new(0,.3f,1));
        Tri(new(4,0,3),new(4.3f,0,3),new(4,.3f,3));capSoup.Seal();
        var railingCaps=WmoCapPatches.Detect(capSoup,settings);
        if(!railingCaps.IsSupersetOf(new[]{2,3,4})||railingCaps.Contains(0)||railingCaps.Contains(1))throw new Exception("Long sloping railing/attached cap was protected as a ramp, or the real wide ramp was removed");
        capSoup=new Extractor.Soup();
        Tri(new(0,-1.5f,0),new(4,-1.5f,2),new(4,1.5f,2));Tri(new(0,-1.5f,0),new(4,1.5f,2),new(0,1.5f,0));
        void Strip(float x0,float x1,float y0,float y1){
            Tri(new(x0,y0,1+x0*.5f),new(x1,y0,1+x1*.5f),new(x1,y1,1+x1*.5f));
            Tri(new(x0,y0,1+x0*.5f),new(x1,y1,1+x1*.5f),new(x0,y1,1+x0*.5f));
        }
        Strip(0,3.7f,0,.3f);Strip(0,3.7f,.8f,1.1f);
        Strip(3.7f,4,0,.3f);Strip(3.7f,4,.3f,.8f);Strip(3.7f,4,.8f,1.1f);capSoup.Seal();
        var bentRail=WmoCapPatches.Detect(capSoup,settings);
        if(!bentRail.IsSupersetOf(Enumerable.Range(2,10))||bentRail.Contains(0)||bentRail.Contains(1))throw new Exception("A bent railing's wide bounds were mistaken for usable ramp standing room");
        int center=4+4*9;
        var cap=Field();cap.spans[center].smax=16;
        if(SurfaceClearance.BlockRaisedCaps(cap,new HashSet<long>{(long)center<<20},settings)!=1||cap.spans[center].area!=0)throw new Exception("Raised WMO cap remained walkable");
        var falseStair=Field();falseStair.spans[center].smax=16;falseStair.spans[5+4*9].smax=18;
        if(SurfaceClearance.BlockRaisedCaps(falseStair,new HashSet<long>{(long)center<<20},settings)!=1)throw new Exception("Single higher shelf falsely protected a wall cap as stairs");
        var stairs=Field(true);
        if(SurfaceClearance.BlockRaisedCaps(stairs,new HashSet<long>{(long)center<<20},settings)!=0)throw new Exception("A stair tread was removed as trim");
        var natural=Field();natural.spans[center].smax=16;
        if(SurfaceClearance.BlockRaisedCaps(natural,new HashSet<long>(),settings)!=0)throw new Exception("Terrain was classified as a WMO cap");
        var wall=Field();wall.spans[center].smax=16;wall.spans[6+4*9]=new(){smin=0,smax=50,area=0};
        if(SurfaceClearance.BlockRaisedCaps(wall,new HashSet<long>{(long)center<<20},settings)!=1)throw new Exception("Wall-base trim remained walkable");
        // Fixed-radius probes stop inside this shelf; look through it to floor/wall.
        var shelf=new RcHeightfield(15,9,new(0,0,0),new(4,10,3),.25f,.1f,0);
        for(int z=0;z<9;z++)for(int x=0;x<15;x++)shelf.spans[x+z*15]=new(){smin=0,smax=x>=10?50:x>=5?16:10,area=x>=10?0:NavBake.AreaGround};
        int shelfCenter=7+4*15;
        if(SurfaceClearance.BlockRaisedCaps(shelf,new HashSet<long>{(long)shelfCenter<<20},settings)!=1)throw new Exception("Broad wall-base shelf survived fixed-distance cap probing");
        var platform=new RcHeightfield(31,31,new(0,0,0),new(8,10,8),.25f,.1f,0);
        for(int z=0;z<31;z++)for(int x=0;x<31;x++)platform.spans[x+z*31]=new(){smin=0,smax=x>=8&&x<=20&&z>=8&&z<=20?16:10,area=NavBake.AreaGround};
        if(SurfaceClearance.BlockRaisedCaps(platform,new HashSet<long>{(14L+14*31)<<20},settings)!=0)throw new Exception("Long backing-wall search removed a freestanding standing platform");
        // A finite staircase terminates at a shaft wall. Probing past its next
        // tread must not turn that valid rising tread into a wall-backed shelf.
        var flight=new RcHeightfield(9,9,new(0,0,0),new(3,10,3),.25f,.1f,0);
        for(int z=0;z<9;z++)for(int x=0;x<9;x++){
            bool shaft=x>=6||z<=2||z>=6;
            flight.spans[x+z*9]=new(){smin=0,smax=shaft?50:x<=2?10:10+(x-2)*4,area=shaft?0:NavBake.AreaGround,
                next=shaft?null:new RcSpan{smin=60,smax=62,area=0}};
        }
        if(SurfaceClearance.BlockRaisedCaps(flight,new HashSet<long>{(long)center<<20},settings)!=0)throw new Exception("Stair tread in a roofed shaft was removed after looking past its rising continuation");
        // Deep outdoor treads need a longer continuation window than thin trim.
        var deepSteps=new RcHeightfield(31,9,new(0,0,0),new(8,20,3),.25f,.1f,0);
        var stepKeys=new HashSet<long>();
        for(int z=0;z<9;z++)for(int x=0;x<31;x++){
            int column=x+z*31;bool end=x>=26;
            deepSteps.spans[column]=new(){smin=0,smax=end?120:10+Math.Max(0,(x-3)/3+1)*4,area=end?0:NavBake.AreaGround};
            if(!end)stepKeys.Add((long)column<<20);
        }
        int deepCenter=23+4*31;
        SurfaceClearance.BlockRaisedCaps(deepSteps,stepKeys,settings,structural:stepKeys);
        if(deepSteps.spans[deepCenter].area==0)throw new Exception("Deep exterior treads lost continuation and disconnected the entrance");
        // Outdoor wall shelves have terrain below, not another WMO floor.
        capSoup=new Extractor.Soup();
        Tri(new(-5,-5,0),new(5,-5,0),new(5,5,0));Tri(new(-5,-5,0),new(5,5,0),new(-5,5,0));
        capSoup.TriKind[0]=capSoup.TriKind[1]=Extractor.Soup.KindTerrain;capSoup.WmoTris=0;capSoup.TerrainTris=2;
        Tri(new(-.4f,-3,.8f),new(.4f,-3,.8f),new(.4f,3,.8f));Tri(new(-.4f,-3,.8f),new(.4f,3,.8f),new(-.4f,3,.8f));
        Tri(new(.4f,-3,.8f),new(.4f,-3,4),new(.4f,3,4));Tri(new(.4f,-3,.8f),new(.4f,3,4),new(.4f,3,.8f));
        capSoup.Seal();
        if(!WmoCapPatches.Detect(capSoup,settings).IsSupersetOf(new[]{2,3}))throw new Exception("An outdoor wall shelf was retained because its lower support is terrain");
        // A backing wall can be beyond the first bevel and shelf, and angled
        // relative to the moulding. It is still a short terminal incline.
        capSoup=new Extractor.Soup();
        Tri(new(-4,-4,0),new(4,-4,0),new(4,4,0));Tri(new(-4,-4,0),new(4,4,0),new(-4,4,0));
        Tri(new(0,-3,.4f),new(.5f,-3,.9f),new(.5f,3,.9f));Tri(new(0,-3,.4f),new(.5f,3,.9f),new(0,3,.4f));
        Tri(new(1.5f,-3,1),new(2.8f,3,1),new(2.8f,3,4));Tri(new(1.5f,-3,1),new(2.8f,3,4),new(1.5f,-3,4));capSoup.Seal();
        if(!WmoCapPatches.Detect(capSoup,settings).IsSupersetOf(new[]{2,3}))throw new Exception("Layered angled wall base was accepted as an unbacked ramp");
        // A conservative voxel wall hit needs actual cylinder/triangle contact.
        // Keep real wall clearance, but not an extra cell-wide stair cutout.
        capSoup=new Extractor.Soup();
        Tri(new(0,-3,0),new(0,3,0),new(0,3,4));Tri(new(0,-3,0),new(0,3,4),new(0,-3,4));capSoup.Seal();
        var exactBody=new SourceBodyClearance(capSoup,settings);
        if(!exactBody.Hits(.4f,0,0)||exactBody.Hits(.55f,0,0)||exactBody.Hits(.1f,0,5))throw new Exception("Source body clearance lost wall contact, inflated it, or crossed storeys");
        capSoup.TriKind[0]=capSoup.TriKind[1]=Extractor.Soup.KindTerrain;
        if(!new SourceBodyClearance(capSoup,settings).Hits(.4f,0,0))throw new Exception("Physical terrain wall was ignored by source clearance");
        capSoup.TriKind.Clear();
        if(!new SourceBodyClearance(capSoup,settings).Hits(.4f,0,0))throw new Exception("Missing provenance silently disabled physical wall clearance");
        capSoup=new Extractor.Soup();Tri(new(0,-3,0),new(0,3,0),new(0,3,.8f));capSoup.Seal();
        if(new SourceBodyClearance(capSoup,settings).Hits(.1f,0,0))throw new Exception("A legal stair riser was treated as a full-height wall");
        capSoup=new Extractor.Soup();
        Tri(new(-.5f,-2,.6f),new(.5f,-2,.6f),new(.5f,2,.6f));Tri(new(-.5f,-2,.6f),new(.5f,2,.6f),new(-.5f,2,.6f));
        Tri(new(-5,-5,0),new(5,-5,0),new(0,5,0));capSoup.Seal();
        var rejectedLow=new HashSet<int>{0,1};var capBody=new SourceBodyClearance(capSoup,settings,rejectedLow);
        if(!capBody.HitsRejected(0,0,0)||capBody.HitsRejected(1.05f,0,0)||capBody.Hits(0,0,0))throw new Exception("Rejected low-cap clearance lost its footprint, added voxel inflation, or changed ordinary climb rules");
        if(!float.IsNaN(new SourceSurfaceFit(capSoup,new NavBake.Settings(),null,rejectedLow).Height(new(0,0,.3f))))throw new Exception("Rejected solid cap disappeared from source-fit headroom");
        capSoup.TriKind[0]=capSoup.TriKind[1]=Extractor.Soup.KindDoodad;
        if(!float.IsNaN(new SourceSurfaceFit(capSoup,new NavBake.Settings(),null).Height(new(0,0,.3f))))throw new Exception("Blocked prop disappeared from source-fit headroom");
        // A wall starts above its inclined base, rather than at the bevel's centroid.
        capSoup=new Extractor.Soup();
        Tri(new(-4,-4,0),new(4,-4,0),new(4,4,0));Tri(new(-4,-4,0),new(4,4,0),new(-4,4,0));
        Tri(new(0,-3,.4f),new(.6f,-3,1),new(.6f,3,1));Tri(new(0,-3,.4f),new(.6f,3,1),new(0,3,.4f));
        Tri(new(.6f,-3,1),new(.6f,-3,4),new(.6f,3,4));Tri(new(.6f,-3,1),new(.6f,3,4),new(.6f,3,1));capSoup.Seal();
        if(!WmoCapPatches.Detect(capSoup,settings).IsSupersetOf(new[]{2,3}))throw new Exception("Wall-backed bevel survived because its wall begins above the centroid");
        // The smooth collision ramp is slightly above a visible flat tread.
        capSoup=new Extractor.Soup();
        Tri(new(-4,-4,0),new(6,-4,0),new(6,4,0));Tri(new(-4,-4,0),new(6,4,0),new(-4,4,0));
        Tri(new(0,-1.5f,0),new(4,-1.5f,2),new(4,1.5f,2));Tri(new(0,-1.5f,0),new(4,1.5f,2),new(0,1.5f,0));
        Tri(new(.8f,-1.5f,.4f),new(1.6f,-1.5f,.4f),new(1.6f,1.5f,.4f));Tri(new(.8f,-1.5f,.4f),new(1.6f,1.5f,.4f),new(.8f,1.5f,.4f));capSoup.Seal();
        if(WmoCapPatches.Detect(capSoup,settings).Overlaps(new[]{4,5}))throw new Exception("Tread overlaid by a smooth collision ramp was rejected as trim");
        // A closed trim ring has a large bounding box, but no standing room.
        capSoup=new Extractor.Soup();
        Tri(new(-5,-5,0),new(5,-5,0),new(5,5,0));Tri(new(-5,-5,0),new(5,5,0),new(-5,5,0));
        Vector3[] outside=[new(-3,-3,.6f),new(3,-3,.6f),new(3,3,.6f),new(-3,3,.6f)];
        Vector3[] inside=[new(-2.4f,-2.4f,.6f),new(2.4f,-2.4f,.6f),new(2.4f,2.4f,.6f),new(-2.4f,2.4f,.6f)];
        for(int i=0;i<4;i++){int j=(i+1)%4;Tri(outside[i],outside[j],inside[j]);Tri(outside[i],inside[j],inside[i]);}capSoup.Seal();
        if(!WmoCapPatches.Detect(capSoup,settings).IsSupersetOf(Enumerable.Range(2,8)))throw new Exception("Closed narrow trim ring was treated as a broad standing floor");
        Tri(inside[1],new(3,-6,2),outside[1]);Tri(outside[2],new(3,6,2),inside[2]);capSoup.Seal();
        if(!WmoCapPatches.Detect(capSoup,settings).IsSupersetOf(Enumerable.Range(2,8)))throw new Exception("Narrow staircase trim acquired landing status merely by joining two sloping railings");
        // A blocked .4-yard cap and lower WMO floor share the old two-voxel band.
        foreach(bool coversCenter in new[]{true,false})
        {
            var layerSoup=new Extractor.Soup();
            layerSoup.Verts.AddRange([new(0,0,0),new(2,0,0),new(0,2,0),new(coversCenter?0:.2f,0,.4f),new(coversCenter?1:.26f,0,.4f),new(coversCenter?0:.2f,coversCenter?1:.26f,.4f)]);
            layerSoup.Indices.AddRange([0,1,2,3,4,5]);layerSoup.WmoTris=2;layerSoup.Seal();
            var layers=new SourceSurfaceFit(layerSoup,new NavBake.Settings(),null);
            var layerField=new RcHeightfield(1,1,new(0,0,0),new(1,2,1),.26666665f,.26666665f,0);
            var capSpan=new RcSpan{smin=0,smax=2,area=NavBake.AreaGround};layerField.spans[0]=capSpan;
            layers.BlockCap(layerField,0,capSpan);
            float lower=layers.Height(new(.1333333f,.1333333f,.3f));
            if(coversCenter?!float.IsNaN(lower):(!float.IsFinite(lower)||MathF.Abs(lower-.26666665f)>.0001f))
                throw new Exception("Rejected-cap fitting either ignored solid headroom or discarded adjacent clear floor");
        }
        var edgeSoup=new Extractor.Soup();
        edgeSoup.Verts.AddRange([new(-10,-10,9),new(10,-10,9),new(0,10,9),new(0,0,10),new(4,0,10),new(0,4,10)]);
        edgeSoup.Indices.AddRange([0,1,2,3,4,5]);edgeSoup.TerrainTris=1;edgeSoup.WmoTris=1;edgeSoup.Seal();
        var edgeFit=new SourceSurfaceFit(edgeSoup,new NavBake.Settings(),null);
        float nearEdgeHeight=edgeFit.Height(new(2.1f,2.1f,10.8f));
        if(!float.IsFinite(nearEdgeHeight)||MathF.Abs(nearEdgeHeight-10.266666f)>.0001f)throw new Exception("A near-edge WMO floor fell through to terrain or retained a high voxel corner");
        if(MathF.Abs(edgeFit.Height(new(3,3,10.8f))-10.266666f)<.0001f)throw new Exception("Floor-edge fitting bridged a large unsupported gap");
        for(int i=3;i<6;i++){var point=edgeSoup.Verts[i];point.Z=9.5f;edgeSoup.Verts[i]=point;}
        var lowLipFit=new SourceSurfaceFit(edgeSoup,new NavBake.Settings(),null);
        float terrainAtLip=lowLipFit.Height(new(2.1f,2.1f,9.65f));
        if(!float.IsFinite(terrainAtLip)||MathF.Abs(terrainAtLip-9.266666f)>.0001f)throw new Exception("Extrapolated building lip overrode directly supported terrain");
        var treadSoup=new Extractor.Soup();
        treadSoup.Verts.AddRange([new(0,0,0),new(4,0,0),new(0,4,0),new(.8f,.8f,.35f),new(1.2f,.8f,.35f),new(.8f,1.2f,.35f)]);
        treadSoup.Indices.AddRange([0,1,2,3,4,5]);treadSoup.WmoTris=2;treadSoup.Seal();
        var treadPoly=new DotRecast.Detour.DtPoly(0,3){vertCount=3};treadPoly.verts[0]=0;treadPoly.verts[1]=1;treadPoly.verts[2]=2;treadPoly.SetArea(NavBake.AreaGround);
        var treadData=new DotRecast.Detour.DtMeshData{header=new(){vertCount=3,polyCount=1,bmin=new(-1,-1,-1),bmax=new(5,5,5)},verts=[0,.26666665f,0,0,.26666665f,4,4,.26666665f,0],polys=[treadPoly],detailMeshes=[new(0,0,0,1)],detailVerts=[],detailTris=[0,1,2,21]};
        new SourceSurfaceFit(treadSoup,new NavBake.Settings(),null).Apply(treadData);
        float treadHeight=DetailHeight(treadData,.9f,.9f),approachHeight=DetailHeight(treadData,.2f,.2f);
        if(!float.IsFinite(treadHeight)||!float.IsFinite(approachHeight)||treadHeight<.35f||treadHeight>.35f+settings.CellHeight+.02f||approachHeight<0||approachHeight>.35f+settings.CellHeight+.02f)throw new Exception($"Small tread lacks a bounded continuous walking envelope: tread={treadHeight}, approach={approachHeight}");
        // Rejecting one raster cell must not revoke a whole long stair/ramp face.
        var rampSoup=new Extractor.Soup();rampSoup.Verts.AddRange([new(0,0,0),new(4,0,2),new(0,4,0)]);rampSoup.Indices.AddRange([0,1,2]);rampSoup.WmoTris=1;rampSoup.Seal();
        var rampFit=new SourceSurfaceFit(rampSoup,new NavBake.Settings(),null);
        var smallField=new RcHeightfield(1,1,new(0,0,0),new(1,2,1),.26666665f,.26666665f,0);var smallCap=new RcSpan{smin=0,smax=1,area=NavBake.AreaGround};smallField.spans[0]=smallCap;
        rampFit.BlockCap(smallField,0,smallCap);
        float remoteHeight=rampFit.Height(new(2,1,1.3f));
        if(!float.IsFinite(remoteHeight)||MathF.Abs(remoteHeight-1.26666665f)>.0001f)throw new Exception("One blocked cell poisoned the height authority of an entire ramp face");
        // The whole coarse triangle initially lies on ground, but one edge
        // crosses the start of a real ramp. Detail must carry its height/area.
        var transitionSoup=new Extractor.Soup();transitionSoup.Verts.AddRange([new(0,0,0),new(4,0,0),new(0,4,0),new(1,0,0),new(3,0,1),new(3,2,1)]);
        transitionSoup.Indices.AddRange([0,1,2,3,4,5]);transitionSoup.WmoTris=2;transitionSoup.Seal();
        var transitionPoly=new DotRecast.Detour.DtPoly(0,3){vertCount=3};transitionPoly.verts[0]=0;transitionPoly.verts[1]=1;transitionPoly.verts[2]=2;transitionPoly.SetArea(NavBake.AreaGround);
        var layeredSoup=new Extractor.Soup();layeredSoup.Verts.AddRange([new(0,0,0),new(4,0,0),new(0,4,0),new(0,0,4),new(4,0,4),new(0,4,4)]);
        layeredSoup.Indices.AddRange([0,1,2,3,4,5]);layeredSoup.TerrainTris=1;layeredSoup.WmoTris=1;layeredSoup.Seal();
        var ownedDetail=new DotRecast.Detour.DtMeshData{header=new(){vertCount=3,polyCount=1,bmin=new(-1,-1,-1),bmax=new(6,6,6)},verts=[0,4.2666667f,0,0,4.2666667f,4,4,4.2666667f,0],polys=[transitionPoly],detailMeshes=[new(0,0,1,3)],detailVerts=[1,1.4f,1],detailTris=[0,1,3,1,1,2,3,1,2,0,3,1]};
        new SourceSurfaceFit(layeredSoup,new NavBake.Settings(),null).Apply(ownedDetail);
        float ownedHeight=DetailHeight(ownedDetail,1,1);
        if(!float.IsFinite(ownedHeight)||MathF.Abs(ownedHeight-4.2666667f)>.001f)throw new Exception("A detail sample fell onto terrain below its owning WMO polygon");
        var hillSoup=new Extractor.Soup();hillSoup.Verts.AddRange([new(0,0,0),new(12,0,0),new(0,12,0),new(4,4,3)]);
        hillSoup.Indices.AddRange([0,1,3,1,2,3,2,0,3]);hillSoup.TerrainTris=3;hillSoup.Seal();
        ownedDetail.verts=[0,.26666665f,0,0,.26666665f,12,12,.26666665f,0];ownedDetail.detailVerts=[4,3.2666667f,4];ownedDetail.header.bmax=new(14,6,14);
        new SourceSurfaceFit(hillSoup,new NavBake.Settings(),null).Apply(ownedDetail);
        float hillHeight=DetailHeight(ownedDetail,4,4);
        if(!float.IsFinite(hillHeight)||MathF.Abs(hillHeight-3.2666667f)>.001f)throw new Exception("Polygon storey guidance flattened verified terrain relief");
        var transitionData=new DotRecast.Detour.DtMeshData{header=new(){vertCount=3,polyCount=1,bmin=new(-1,-1,-1),bmax=new(5,5,5)},verts=[0,.26666665f,0,0,.26666665f,4,4,.26666665f,0],polys=[transitionPoly],detailMeshes=[new(0,0,0,1)],detailVerts=[],detailTris=[0,1,2,21]};
        new SourceSurfaceFit(transitionSoup,new NavBake.Settings(),null).Apply(transitionData);
        if(transitionData.detailMeshes[0].triCount<=1||transitionData.detailMeshes[0].triCount>8)throw new Exception("Buried ramp detail was not repaired within one bounded pass");
        var poly=transitionPoly;
        var data=transitionData;
        var trimSoup=new Extractor.Soup();trimSoup.Verts.AddRange([new(0,0,0),new(4,0,0),new(0,4,0),new(.8f,.8f,.8f),new(1.2f,.8f,.8f),new(.8f,1.2f,.8f)]);
        trimSoup.Indices.AddRange([0,1,2,3,4,5]);trimSoup.WmoTris=2;trimSoup.Seal();
        data.verts=[0,.26666665f,0,0,.26666665f,4,4,.26666665f,0];
        new SourceSurfaceFit(trimSoup,new NavBake.Settings(),null).Apply(data);
        if(MathF.Abs(data.verts[1]-.26666665f)>.0001f)throw new Exception("An unrelated trim cap raised the whole ground triangle");
        var rejectedSoup=new Extractor.Soup();rejectedSoup.Verts.AddRange([new(0,0,0),new(4,0,0),new(0,4,0),new(.9f,.9f,1.6f),new(1.4f,.9f,1.6f),new(.9f,1.4f,1.6f)]);
        rejectedSoup.Indices.AddRange([0,1,2,3,4,5]);rejectedSoup.WmoTris=2;rejectedSoup.Seal();
        var rejectedFit=new SourceSurfaceFit(rejectedSoup,new NavBake.Settings(),null);
        var rejectedField=Field();rejectedField.spans[center].smax=16;
        SurfaceClearance.BlockRaisedCaps(rejectedField,new HashSet<long>{(long)center<<20},settings,(col,span)=>rejectedFit.BlockCap(rejectedField,col,span));
        if(!float.IsNaN(rejectedFit.Height(new(1.125f,1.125f,.8f))))throw new Exception("Rejected solid cap incorrectly permits standing on floor underneath it");
        float adjacentFloor=rejectedFit.Height(new(.5f,.5f,.3f));
        if(!float.IsFinite(adjacentFloor)||MathF.Abs(adjacentFloor-.26666665f)>.0001f)throw new Exception("Rejected cap removed height support from adjacent clear floor");
        var corridor=new RcHeightfield(9,9,new(0,0,0),new(3,10,3),.25f,.1f,0);
        for(int z=0;z<9;z++)for(int x=3;x<=5;x++)corridor.spans[x+z*9]=new(){smin=0,smax=10,area=NavBake.AreaGround};
        var compact=RcCompacts.BuildCompactHeightfield(new RcContext(),20,10,corridor);
        var narrow=typeof(NavBake).GetMethod("DropNarrowIndoorStrips",BindingFlags.NonPublic|BindingFlags.Static)!;
        int dropped=(int)narrow.Invoke(null,[compact,Enumerable.Repeat(true,compact.spanCount).ToArray(),1.07f])!;
        if(dropped!=0)throw new Exception("A narrow corridor without a raised lower floor was deleted");
        var regionField=RcCompacts.BuildCompactHeightfield(new RcContext(),20,10,Field());
        for(int i=0;i<regionField.spanCount;i++)
        {
            var builder=RcCompactSpanBuilder.NewBuilder(ref regionField.spans[i]);builder.reg=i==center?2:1;regionField.spans[i]=builder.Build();
        }
        var prune=typeof(NavBake).GetMethod("DropTinyRegions",BindingFlags.NonPublic|BindingFlags.Static)!;
        if((int)prune.Invoke(null,[regionField,6])! != 0||regionField.areas[center]==0)throw new Exception("Connected tiny floor fragment was deleted");
        var isolated=RcCompactSpanBuilder.NewBuilder(ref regionField.spans[center]);isolated.con=0xffffff;regionField.spans[center]=isolated.Build();
        if((int)prune.Invoke(null,[regionField,6])! != 1)throw new Exception("Isolated tiny noise no longer removed");
        Console.WriteLine("Surface rules passed: caps/wall trim blocked; treads, terrain and narrow corridors preserved; interior WMO collision covered without lifting ground onto unrelated trim.");
    }
    internal static void Run()
    {
        // Delaunay's fourth slot is temporary, not an exported boundary bitmask.
        float[] square=[0,0,0, 0,0,4, 4,0,4, 4,0,0, 2,0,2];
        int[] hull=[0,1,2,3];
        var tris=new List<int>();
        RcMeshDetails.DelaunayHull(new RcContext(),5,square,4,hull,tris);
        if(!NavBake.ValidateDetailCoverage(square,hull,tris)) throw new Exception("Complete Delaunay disk rejected");
        if(tris.Any(v=>v<0)||Enumerable.Range(0,tris.Count/4).Sum(t=>System.Numerics.BitOperations.PopCount((uint)tris[t*4+3]))!=4)
            throw new Exception("Detail boundary bits are not exactly the four outer edges");
        var missing=tris.Skip(4).ToList();
        if(NavBake.ValidateDetailCoverage(square,hull,missing)) throw new Exception("Partial coverage accepted");
        var doubled=tris.Concat(tris).ToList();
        if(NavBake.ValidateDetailCoverage(square,hull,doubled)) throw new Exception("Overlapping duplicate faces accepted");
        var reversed=tris.ToList();(reversed[0],reversed[1])=(reversed[1],reversed[0]);
        if(NavBake.ValidateDetailCoverage(square,hull,reversed)) throw new Exception("Folded winding accepted");

        // An indoor vertex must not tighten the following outdoor vertex's limit.
        var solid=new RcHeightfield(2,1,new(0,0,0),new(2,20,1),1,1,0);
        for(int i=0;i<2;i++)solid.spans[i]=new(){smin=0,smax=1,area=NavBake.AreaGround};
        solid.spans[0].next=new(){smin=5,smax=6,area=0};
        var chf=RcCompacts.BuildCompactHeightfield(new RcContext(),2,1,solid);
        var mesh=new RcPolyMesh {npolys=1,nverts=3,nvp=3,bmin=new(0,0,0),ch=1,cs=1,
            verts=[0,1,0, 2,1,0, 0,1,1],polys=[0,1,2,0xffff,0xffff,0xffff]};
        var detail=new RcPolyMeshDetail {nmeshes=1,nverts=2,meshes=[0,2,0,0],verts=[.5f,0,.5f, 1.5f,0,.5f]};
        var clamp=typeof(NavBake).GetMethod("ClampDetailToPolygon",BindingFlags.Static|BindingFlags.NonPublic)!;
        clamp.Invoke(null,[mesh,detail,chf,solid,2f,.1f,null]);
        if(detail.verts[1]<.9f||detail.verts[4]!=0)throw new Exception("Indoor clamp contaminated outdoor floor bounds");
        var floorSoup=new Extractor.Soup();floorSoup.Verts.AddRange([new(-5,-5,0),new(5,-5,0),new(0,5,0)]);
        floorSoup.Indices.AddRange([0,1,2]);floorSoup.WmoTris=1;floorSoup.Seal();
        detail.verts[1]=0;
        clamp.Invoke(null,[mesh,detail,chf,solid,2f,.1f,new SourceSurfaceFit(floorSoup,new NavBake.Settings(),null)]);
        if(detail.verts[1]!=0)throw new Exception("Indoor hull clamp raised collision-verified recessed floor detail");
        // A malformed obstacle must never quietly become an empty mesh.
        var model=new byte[0xe8];System.Text.Encoding.ASCII.GetBytes("MD20").CopyTo(model,0);
        if(M2.ReadCollision(new MemoryStream(model)).TriCount!=0)throw new Exception("Genuinely noncolliding prop rejected");
        BitConverter.GetBytes(3).CopyTo(model,0xd8);BitConverter.GetBytes(1).CopyTo(model,0xe0);
        BitConverter.GetBytes(int.MaxValue).CopyTo(model,0xdc);
        bool badModel=false;
        try{M2.ReadCollision(new MemoryStream(model));}catch(InvalidDataException){badModel=true;}
        if(!badModel)throw new Exception("Malformed collision silently vanished");
        Names.LoadTilesets(new StringReader("123;test/road.blp\n"));
        if(Names.Tileset(123)!="test/road.blp")throw new Exception("Embedded texture snapshot not loaded");
        Console.WriteLine("Audit guards: complete coverage, correct boundary flags, missing/duplicate/folded rejection, independent floor limits, corrupt collision rejection, and in-memory texture snapshot passed.");
    }
}
