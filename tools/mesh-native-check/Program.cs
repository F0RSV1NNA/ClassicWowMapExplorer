using System.IO.Compression;
using MapExtract;
using System.Numerics;

public static class NativeCheck
{
    public static void Main(string[] args)
    {
        if(args.Length==6&&args[0]=="source-export"){
            var prepared=((Extractor.Soup Soup,RoadMask Roads,int Map,int X,int Y,MapEdits.Map Edits))typeof(MeshEngine).GetMethod("PrepareRaw",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,[File.ReadAllBytes(args[1]),true,false])!;
            float x=float.Parse(args[2]),y=float.Parse(args[3]),radius=float.Parse(args[4]);
            var caps=WmoCapPatches.Detect(prepared.Soup,new NavBake.Settings(),localObstaclesOnly:true);
            var faces=new List<object>();
            for(int t=0;t<prepared.Soup.TriCount;t++){
                var a=prepared.Soup.Verts[prepared.Soup.Indices[t*3]];var b=prepared.Soup.Verts[prepared.Soup.Indices[t*3+1]];var c=prepared.Soup.Verts[prepared.Soup.Indices[t*3+2]];
                if(MathF.Min(a.X,MathF.Min(b.X,c.X))>x+radius||MathF.Max(a.X,MathF.Max(b.X,c.X))<x-radius||MathF.Min(a.Y,MathF.Min(b.Y,c.Y))>y+radius||MathF.Max(a.Y,MathF.Max(b.Y,c.Y))<y-radius)continue;
                faces.Add(new{A=a,B=b,C=c,Kind=prepared.Soup.TriKind[t],triangle=t,rejected=caps.Contains(t)});
            }
            File.WriteAllText(args[5],System.Text.Json.JsonSerializer.Serialize(new{source=faces},new System.Text.Json.JsonSerializerOptions{IncludeFields=true}));
            Console.WriteLine($"Exported {faces.Count} source faces to {args[5]}");return;
        }
        if(args.Length==6&&args[0]=="source-point"){
            var prepared=((Extractor.Soup Soup,RoadMask Roads,int Map,int X,int Y,MapEdits.Map Edits))typeof(MeshEngine).GetMethod("PrepareRaw",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,[File.ReadAllBytes(args[1]),true,false])!;
            float x=float.Parse(args[2]),y=float.Parse(args[3]),z=float.Parse(args[4]),raster=float.Parse(args[5]);
            var settings=new NavBake.Settings();var caps=WmoCapPatches.Detect(prepared.Soup,settings,localObstaclesOnly:true);
            var source=new SourceSurfaceFit(prepared.Soup,settings,null,caps);var body=new SourceBodyClearance(prepared.Soup,settings,caps);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{x,y,z,raster,sourceHeight=source.Height(new(x,y,z+settings.CellHeight)),wallFooting=source.IsWallFooting(x,y,raster),bodyHit=body.Hits(x,y,z),rejectedHit=body.HitsRejected(x,y,z)},new System.Text.Json.JsonSerializerOptions{NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));return;
        }
        if(args.Length==4&&args[0]=="audit-capture"){
            var prepared=((Extractor.Soup Soup,RoadMask Roads,int Map,int X,int Y,MapEdits.Map Edits))typeof(MeshEngine).GetMethod("PrepareRaw",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,[File.ReadAllBytes(args[1]),true,false])!;
            var settings=new NavBake.Settings();var caps=WmoCapPatches.Detect(prepared.Soup,settings,localObstaclesOnly:true);
            using var stream=File.OpenRead(args[2]);stream.Position=20;
            var data=new DotRecast.Detour.Io.DtMeshDataReader().Read(new BinaryReader(stream),6);
            CoverageAudit.Write(new SourceSurfaceFit(prepared.Soup,settings,null,caps),new SourceBodyClearance(prepared.Soup,settings,caps),data,args[3],settings.CellHeight);return;
        }
        if(args.Length==3&&args[0]=="snapshot-tile"){
            using var stream=File.OpenRead(args[1]);stream.Position=20;
            var data=new DotRecast.Detour.Io.DtMeshDataReader().Read(new BinaryReader(stream),6);
            MeshSnapshots.Write(data,args[2]);return;
        }
        if(args.Length==6&&args[0]=="surface-seams"){SurfaceCheck.Seams(args[1],args.Skip(2).Select(v=>float.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray());return;}
        if(args.Length==2&&args[0]=="surface-seams"){SurfaceCheck.Seams(args[1]);return;}
        if(args.Length==8&&args[0]=="walk-route"){SurfaceCheck.WalkRoute(args);return;}
        if(args.Length==1&&args[0]=="mesh-quality"){MeshQualityChecks.Run();return;}
        if(args.Length==1&&args[0]=="collision-detail"){AuditChecks.CollisionDetail();return;}
        if(args.Length==3&&args[0] is "local-source" or "raw-source")Directory.CreateDirectory(args[2]);
        if(args.Length==3&&args[0] is ("local-source" or "raw-source")&&Environment.GetEnvironmentVariable("EXPLORER_MESH_STAGES")=="1"){
            SourceSurfaceFit.StageObserver=(name,data)=>{
                File.WriteAllText(Path.Combine(args[2],$"heights-{name}.json"),System.Text.Json.JsonSerializer.Serialize(new{coarse=data.verts,detail=data.detailVerts}));
                MeshSnapshots.Write(data,Path.Combine(args[2],$"surface-{name}.json"));
            };
            NavBake.ContoursObserver=cset=>{
                var contours=cset.conts.Select(c=>new{c.reg,c.area,verts=Enumerable.Range(0,c.nverts).Select(i=>new{X=cset.bmin.Z+c.verts[i*4+2]*cset.cs,Y=cset.bmin.X+c.verts[i*4]*cset.cs,Z=cset.bmin.Y+c.verts[i*4+1]*cset.ch,flags=c.verts[i*4+3]}).ToArray()}).ToArray();
                File.WriteAllText(Path.Combine(args[2],"contours.json"),System.Text.Json.JsonSerializer.Serialize(contours));
            };
            float[] stageBounds=(Environment.GetEnvironmentVariable("EXPLORER_MESH_STAGE_BOUNDS")??"-8940,-235,-8590,-110,78,120").Split(',').Select(v=>float.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if(stageBounds.Length!=6)throw new ArgumentException("EXPLORER_MESH_STAGE_BOUNDS requires minX,minY,maxX,maxY,minZ,maxZ");
            int stage=0;NavBake.StageObserver=(name,chf)=>{
                var rows=new List<string>{"x,y,z,area,links,region"};
                for(int z=0;z<chf.height;z++)for(int x=0;x<chf.width;x++){
                    float wx=chf.bmin.Z+(z+.5f)*chf.cs,wy=chf.bmin.X+(x+.5f)*chf.cs;
                    if(wx<stageBounds[0]||wy<stageBounds[1]||wx>stageBounds[2]||wy>stageBounds[3])continue;
                    var c=chf.cells[x+z*chf.width];for(int i=c.index;i<c.index+c.count;i++){
                        float h=chf.bmin.Y+chf.spans[i].y*chf.ch;if(h<stageBounds[4]||h>stageBounds[5])continue;
                        rows.Add($"{wx},{wy},{h},{chf.areas[i]},{chf.spans[i].con},{chf.spans[i].reg}");
                    }
                }
                File.WriteAllLines(Path.Combine(args[2],$"stage-{stage++}-{name.Replace(' ','_')}.csv"),rows);
            };
        }
        if(args.Length==3&&args[0]=="raw-source"){
            byte[] bytes=MeshEngine.BakeRaw(File.ReadAllBytes(args[1]));
            File.WriteAllBytes(Path.Combine(args[2],"bake.zip"),bytes);
            using var archive=new ZipArchive(new MemoryStream(bytes));
            foreach(var entry in archive.Entries){using var stream=entry.Open();using var target=File.Create(Path.Combine(args[2],Path.GetFileName(entry.FullName)));stream.CopyTo(target);}
            return;
        }
        if(args.Length==3&&args[0]=="components"){SurfaceCheck.Components(args[1],args[2]);return;}
        if(args.Length==1&&args[0]=="wmo-collision"){AuditChecks.WmoCollisionFlags();return;}
        if(args.Length==1&&args[0]=="audit-guards"){AuditChecks.Run();return;}
        if(args.Length==2&&args[0]=="authoring"){AuditChecks.Authoring(args[1]);return;}
        if(args.Length==1&&args[0]=="surface-rules"){AuditChecks.SurfaceRules();return;}
        if(args.Length==3&&args[0]=="export-preview"){SurfaceCheck.ExportPreview(args[1],args[2]);return;}
        if(args.Length==3&&args[0]=="compare-tiles"){SurfaceCheck.CompareTiles(args[1],args[2]);return;}
        if(args.Length==3&&args[0]=="local-source"){AuditChecks.LocalSource(args[1],args[2]);return;}
        if(args.Length==4&&args[0]=="surface-refit"){SurfaceCheck.Refit(args[1],args[2],args[3]);return;}
        if(args.Length==1 && args[0]=="surface-heights")
        {
            var solid=new DotRecast.Recast.RcHeightfield(2,2,new(0,0,0),new(2,20,2),1,1,0);
            for(int i=0;i<4;i++)solid.spans[i]=new(){smin=0,smax=i==1?4:1,area=NavBake.AreaGround};
            var chf=DotRecast.Recast.RcCompacts.BuildCompactHeightfield(new DotRecast.Core.RcContext(),2,4,solid);
            void Tag(DotRecast.Recast.RcCompactHeightfield field){for(int i=0;i<field.spanCount;i++){var tag=DotRecast.Recast.RcCompactSpanBuilder.NewBuilder(ref field.spans[i]);tag.reg=1;field.spans[i]=tag.Build();}}
            Tag(chf);
            int floor=chf.cells[0].index,ledge=chf.cells[1].index;
            chf.areas[ledge]=0;
            if(NavContours.GetCornerHeight(0,0,floor,1,chf,out _)!=1)throw new Exception("Removed ledge raised a walkable floor corner");
            var diagonalSolid=new DotRecast.Recast.RcHeightfield(2,2,new(0,0,0),new(2,20,2),1,1,0);
            for(int i=0;i<4;i++)diagonalSolid.spans[i]=new(){smin=0,smax=i==3?4:1,area=NavBake.AreaGround};
            var diagonalField=DotRecast.Recast.RcCompacts.BuildCompactHeightfield(new DotRecast.Core.RcContext(),2,4,diagonalSolid);
            Tag(diagonalField);
            diagonalField.areas[diagonalField.cells[1].index]=0;diagonalField.areas[diagonalField.cells[2].index]=0;
            for(int dir=0;dir<4;dir++)if(NavContours.GetCornerHeight(0,0,diagonalField.cells[0].index,dir,diagonalField,out _)!=1)throw new Exception("Removed intermediate cells allowed a diagonal raised surface to lift the floor corner");
            chf.areas[ledge]=NavBake.AreaGround;
            var unregioned=DotRecast.Recast.RcCompactSpanBuilder.NewBuilder(ref chf.spans[ledge]);unregioned.reg=0;chf.spans[ledge]=unregioned.Build();
            if(NavContours.GetCornerHeight(0,0,floor,1,chf,out _)!=1)throw new Exception("Unemitted region raised a floor corner");
            Tag(chf);
            if(NavContours.GetCornerHeight(0,0,floor,1,chf,out _)!=4)throw new Exception("Live step height was flattened");
            var soup=new Extractor.Soup();
            void Floor(float x,float height,float slope=0)
            {
                int start=soup.Verts.Count;
                soup.Verts.AddRange([new(x,0,height),new(x+8,0,height+8*slope),new(x+8,8,height+8*slope),new(x,8,height)]);
                soup.Indices.AddRange([start,start+1,start+2,start,start+2,start+3]);soup.WmoTris+=2;
            }
            Floor(0,0);Floor(0,8);Floor(20,1,.5f);Floor(40,0);Floor(40,.6f);soup.Seal();
            var settings=new NavBake.Settings();var fit=new SourceSurfaceFit(soup,settings,null);
            void Check(Vector3 p,float surface)
            {
                float result=fit.Height(p);
                if(!float.IsFinite(result)||MathF.Abs(result-surface-settings.CellHeight)>.0001f)throw new Exception("Collision fit selected the wrong surface: "+p+" -> "+result);
            }
            Check(new(2,2,.8f),0);Check(new(2,2,8.8f),8);Check(new(24,2,3.8f),3);
            Check(new(42,2,.3f),.6f);
            if(!float.IsNaN(fit.Height(new(2,2,4))))throw new Exception("Collision fit jumped between storeys");
            var poly=new DotRecast.Detour.DtPoly(0,3){vertCount=3};poly.verts[0]=0;poly.verts[1]=1;poly.verts[2]=2;poly.SetArea(NavBake.AreaGround);
            var data=new DotRecast.Detour.DtMeshData {header=new(){vertCount=3,polyCount=1,bmin=new(0,-1,0),bmax=new(10,10,10),bvNodeCount=1,bvQuantFactor=1},
                verts=[1,.8f,1, 2,.8f,1, 1,.8f,2],polys=[poly],detailMeshes=[],detailVerts=[],
                bvTree=[new(){bmin=new(0,0,0),bmax=new(3,3,3)},null!]};
            if(fit.Apply(data)!=3||MathF.Abs(data.verts[1]-settings.CellHeight)>.0001f||data.verts[0]!=1)throw new Exception("Export fitting did not preserve XY while correcting height");
            var detail=new DotRecast.Recast.RcPolyMeshDetail {nmeshes=1,nverts=3,ntris=1,meshes=[0,3,0,1],verts=[0,0,0, 1,0,0, 0,1,.01f],tris=[0,1,2,0]};
            var cull=typeof(NavBake).GetMethod("CullDetailBlades",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
            int removed=(int)cull.Invoke(null,[detail,.02f,new DotRecast.Core.Numerics.RcVec3f(-10,-10,-10),new DotRecast.Core.Numerics.RcVec3f(10,10,10)])!;
            if(removed!=0||detail.ntris!=1)throw new Exception("Small positive-area ground triangle was deleted");
            detail.verts=[0,0,0, 16,0,16.000002f, 16.000002f,0,16.000004f];
            removed=(int)cull.Invoke(null,[detail,.02f,new DotRecast.Core.Numerics.RcVec3f(-20,-10,-20),new DotRecast.Core.Numerics.RcVec3f(20,10,20)])!;
            if(removed!=0||detail.ntris!=1)throw new Exception("Float cancellation deleted a nonzero ground footprint");
            Console.WriteLine("Removed ledges excluded; live steps preserved; source fitting follows flat/ramped/stacked floors without floor switching; small-triangle coverage preserved.");return;
        }
        if (args.Length == 1 && args[0] == "blocked-props")
        {
            foreach(bool reverse in new[]{false,true})foreach(int footing in new[]{NavBake.AreaGround,NavBake.AreaSwimFloor,SurfaceClearance.WmoTop}){
                var field=new DotRecast.Recast.RcHeightfield(1,1,new(0,0,0),new(1,3,1),1,.1f,0);
                float[] vertices=[.05f,.1f,.05f,.95f,.1f,.05f,.05f,.1f,.95f, .05f,.2f,.05f,.05f,.2f,.95f,.95f,.2f,.05f];
                DotRecast.Recast.RcRasterizations.RasterizeTriangles(new DotRecast.Core.RcContext(),vertices,
                    reverse?[3,4,5,0,1,2]:[0,1,2,3,4,5],reverse?[NavBake.AreaWmoUnderside,footing]:[footing,NavBake.AreaWmoUnderside],2,field,4);
                if(field.spans[0].area!=footing)throw new Exception("Underside provenance overwrote real terrain/WMO/swim footing");
            }
            foreach(bool reverse in new[]{false,true})foreach(int riserArea in new[]{NavBake.AreaNull,NavBake.AreaWmoSolid,NavBake.AreaDoodadSolid}){
                var field=new DotRecast.Recast.RcHeightfield(1,1,new(0,0,0),new(1,3,1),1,.1f,0);
                float[] vertices=[.05f,.1f,.05f,.95f,.1f,.05f,.05f,.1f,.95f, .05f,.1f,.05f,.95f,.4f,.05f,.05f,.4f,.95f];
                DotRecast.Recast.RcRasterizations.RasterizeTriangles(new DotRecast.Core.RcContext(),vertices,
                    reverse?[3,4,5,0,1,2]:[0,1,2,3,4,5],
                    reverse?[riserArea,SurfaceClearance.WmoTop]:[SurfaceClearance.WmoTop,riserArea],2,field,4);
                int expected=riserArea==NavBake.AreaNull?SurfaceClearance.WmoTop:riserArea;
                if(field.spans[0].area!=expected)throw new Exception("Climb-bounded merging lost a legal riser or resurrected an explicit cap/prop");
            }
            foreach(bool reverse in new[]{false,true})
            {
                var merged = new DotRecast.Recast.RcHeightfield(1,1,new(0,0,0),new(1,3,1),1,1,0);
                float[] vertices=[.05f,.1f,.05f, .95f,.1f,.05f, .05f,.1f,.95f,
                                  .05f,.2f,.05f, .95f,.2f,.05f, .05f,.2f,.95f];
                DotRecast.Recast.RcRasterizations.RasterizeTriangles(new DotRecast.Core.RcContext(),vertices,
                    reverse ? [3,4,5,0,1,2] : [0,1,2,3,4,5],
                    reverse ? [NavBake.AreaDoodadSolid,NavBake.AreaGround] : [NavBake.AreaGround,NavBake.AreaDoodadSolid],2,merged,4);
                if(merged.spans[0].area!=NavBake.AreaDoodadSolid || merged.spans[0].next!=null)
                    throw new Exception("Terrain overwrote blocked-prop provenance during span merging");
            }
            var solid = new DotRecast.Recast.RcHeightfield(3, 1, new(0,0,0), new(3,30,1), 1, 1, 0);
            for(int i=0;i<3;i++) solid.spans[i] = new DotRecast.Recast.RcSpan {
                smin=0,smax=1,area=NavBake.AreaGround,next=new() {smin=2,smax=i==2?20:3,area=0}
            };
            int restored=NavBake.FilterLowObstaclesKeepingBlockedProps(new DotRecast.Core.RcContext(),4,solid,new HashSet<long>{2, (2L<<20)|2});
            if(restored!=1 || solid.spans[0].next!.area!=0)throw new Exception("Short blocked prop was made walkable");
            if(solid.spans[1].next!.area!=NavBake.AreaGround)throw new Exception("Ordinary low-obstacle filtering was changed");
            if(solid.spans[2].next!.area!=0 || solid.spans[0].area!=NavBake.AreaGround)throw new Exception("High prop or terrain classification changed");
            foreach(bool reverse in new[]{false,true}){
                var field=new DotRecast.Recast.RcHeightfield(1,1,new(0,0,0),new(1,3,1),1,.1f,0);
                float[] vertices=[.05f,.1f,.05f,.95f,.1f,.05f,.05f,.1f,.95f,.05f,.4f,.05f,.95f,.4f,.05f,.05f,.4f,.95f];
                DotRecast.Recast.RcRasterizations.RasterizeTriangles(new DotRecast.Core.RcContext(),vertices,
                    reverse?[3,4,5,0,1,2]:[0,1,2,3,4,5],reverse?[NavBake.AreaWmoSolid,62]:[62,NavBake.AreaWmoSolid],2,field,0);
                if(field.spans[0].next!.area!=NavBake.AreaWmoSolid)throw new Exception("Lower floor relabelled higher steep WMO top");
                field.spans[0].area=NavBake.AreaGround;field.spans[0].next!.area=0;
                NavBake.FilterLowObstaclesKeepingBlockedProps(new DotRecast.Core.RcContext(),4,field,null,new HashSet<long>{field.spans[0].next!.smin});
                if(field.spans[0].next!.area!=0)throw new Exception("Steep WMO top was promoted back to walkable");
            }
            Console.WriteLine("Overlapping terrain/prop rasterization preserves blocking in both orders; blocked low/high props remain solid and unwalkable; ordinary low steps and terrain are preserved.");return;
        }
        if ((args.Length == 7 || args.Length == 8) && args[0] == "surface-check") { SurfaceCheck.Run(args); return; }
        if (args.Length == 1 && args[0] == "contour-winding")
        {
            var outer = new List<int> { 0,0,0,1, 0,0,6,1, 8,0,6,1, 8,0,0,1 };
            var hole = new List<int> { 0,0,0,1, 8,0,0,1, 8,0,6,1, 0,0,6,1 };
            foreach (var raw in new[] { outer, hole })
            {
                var collapsed = new List<int>();
                NavContours.PreserveContourWinding(raw, collapsed, 20, 0, 0);
                if (collapsed.Count < 12 || Math.Sign(NavContours.SignedContourArea(collapsed)) != Math.Sign(NavContours.SignedContourArea(raw))) throw new Exception("Collapsed contour was not repaired");
                var reversed = raw.AsEnumerable().Chunk(4).Reverse().SelectMany(v => v).ToList();
                NavContours.PreserveContourWinding(raw, reversed, 20, 0, 0);
                if (Math.Sign(NavContours.SignedContourArea(reversed)) != Math.Sign(NavContours.SignedContourArea(raw))) throw new Exception("Reversed contour was not repaired");
            }
            var unchanged = outer.ToList();
            NavContours.PreserveContourWinding(outer, unchanged, 20, 0, 0);
            if (!unchanged.SequenceEqual(outer)) throw new Exception("Valid contour changed");
            var portal = outer.ToList(); portal[3] |= DotRecast.Recast.RcRecast.RC_BORDER_VERTEX;
            var repaired = new List<int>();
            NavContours.PreserveContourWinding(portal, repaired, 20, 0, 0);
            if ((repaired[3] & DotRecast.Recast.RcRecast.RC_BORDER_VERTEX) == 0 || (repaired[7] & DotRecast.Recast.RcRecast.RC_BORDER_VERTEX) != 0)
                throw new Exception("Raw-boundary recovery changed border-vertex flags");
            for (int i = 3; i < repaired.Count; i += 4)
                if ((repaired[i] & DotRecast.Recast.RcRecast.RC_CONTOUR_REG_MASK) != 1) throw new Exception("Portal neighbour lost");
            var contours = new DotRecast.Recast.RcContourSet();
            DotRecast.Recast.RcContour Rectangle(int x, int size, bool isHole, int area = 11)
            {
                var points = new List<int> { x,0,0,0, x,0,size,0, x+size,0,size,0, x+size,0,0,0 };
                if (isHole) points = points.Chunk(4).Reverse().SelectMany(v => v).ToList();
                return new() { reg=1, area=area, nverts=4, verts=points.ToArray() };
            }
            contours.conts.Add(Rectangle(0, 10, false));
            contours.conts.Add(Rectangle(20, 10, false));
            var h1=Rectangle(2, 3, true); var h2=Rectangle(22, 3, true);
            // Move holes strictly inside their outlines in both horizontal axes.
            for(int i=2;i<h1.verts.Length;i+=4){h1.verts[i]+=2;h2.verts[i]+=2;}
            contours.conts.Add(h1);contours.conts.Add(h2);
            NavContours.MergeContourHoles(new DotRecast.Core.RcContext(), contours);
            if(h1.nverts!=0 || h2.nverts!=0 || contours.conts[0].nverts!=10 || contours.conts[1].nverts!=10)
                throw new Exception("Holes were not merged into both separate region outlines");
            Console.WriteLine("Collapsed/reversed outline and hole repairs, portal flags, and multiple-outline hole assignment passed; valid contour unchanged."); return;
        }
        if (args.Length == 2 && args[0] == "water-stress") { MakeWaterStress(args[1]); return; }
        if (args.Length < 2) throw new ArgumentException("Expected input and output paths");
        var input = File.ReadAllBytes(args[0]);
        var output = args.Length > 2 && args[2] == "reachability-archive" ? input : BitConverter.ToUInt32(input) == 0x32524D45 ? MeshEngine.BakeRaw(input) : MeshEngine.Bake(input);
        if (args.Length > 2 && args[2].StartsWith("reachability"))
        {
            File.WriteAllBytes(Path.ChangeExtension(args[1], ".unflagged.zip"), output);
            MeshEngine.BeginAnalysis(0);
            using var original = new ZipArchive(new MemoryStream(output));
            var names = original.Entries.Where(e => e.Name.EndsWith(".mmap") || e.Name.EndsWith(".mmtile") || e.Name.EndsWith(".mliq")).Select(e => e.Name).ToArray();
            foreach (var name in names)
            {
                using var file = original.GetEntry(name)!.Open(); using var bytes = new MemoryStream();
                file.CopyTo(bytes); MeshEngine.PutAnalysisFile(name, bytes.ToArray());
            }
            var report = MeshEngine.Analyze(1000);
            var preserved = MeshEngine.Analyze(1);
            var recomputed = MeshEngine.Analyze(1000);
            if (report != recomputed) throw new InvalidDataException("Reachability recomputation differs");
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            {
                foreach (var name in names) { using var stream = zip.CreateEntry(name).Open(); stream.Write(MeshEngine.GetAnalysisFile(name)); }
                using (var stream = zip.CreateEntry("flagged-preview.bin").Open()) stream.Write(MeshEngine.PreviewAnalysisTile(32, 32));
                using (var stream = new StreamWriter(zip.CreateEntry("report.json").Open())) stream.Write(recomputed);
                using (var stream = new StreamWriter(zip.CreateEntry("preserved.json").Open())) stream.Write(preserved);
            }
            output = archive.ToArray(); MeshEngine.EndAnalysis();
        }
        File.WriteAllBytes(args[1], output);
    }

    static void MakeWaterStress(string dir)
    {
        Directory.CreateDirectory(dir);
        for (int row = 32; row < 36; row++) for (int col = 32; col < 36; col++)
        {
            var clip = NavBake.Clip.ForAdt(row, col);
            float x = clip.MinX + 10, y = clip.MinY + 10;
            var soup = new Extractor.Soup { TerrainTris = 2 };
            soup.Verts.AddRange([new Vector3(x,y,3), new Vector3(x+12,y,3), new Vector3(x+12,y+12,3), new Vector3(x,y+12,3)]);
            soup.Indices.AddRange([0,1,2,0,2,3]); soup.Seal();
            var bake = NavBake.Bake(soup, new NavBake.Settings(), clip: clip);
            if (!bake.Ok) throw new InvalidDataException(bake.Message);
            NavWriter.WriteAll(dir, 0, bake);
            int width = (int)MathF.Ceiling((clip.MaxX - clip.MinX) / LiquidMap.Cell), height = (int)MathF.Ceiling((clip.MaxY - clip.MinY) / LiquidMap.Cell);
            var tile = new LiquidMap.Tile { OriginX = clip.MinX, OriginY = clip.MinY, Width = width, Height = height,
                Wetness = Enumerable.Repeat(true, width * height).ToArray(), Wet = width * height, Level = 8,
                Kinds = Enumerable.Repeat(LiquidMap.Kind.Water, width * height).ToArray(), Dominant = LiquidMap.Kind.Water, KindMask = 1 };
            LiquidMap.Write(dir, 0, row, col, tile);
        }
    }
}
