using System.Numerics;
using MapExtract;
using DotRecast.Detour;
internal static class MeshQualityChecks
{
    internal static void Run()
    {
        var settings=new NavBake.Settings();var soup=new Extractor.Soup();
        void Tri(Vector3 a,Vector3 b,Vector3 c){int at=soup.Verts.Count;soup.Verts.AddRange([a,b,c]);soup.Indices.AddRange([at,at+1,at+2]);soup.TriKind.Add(Extractor.Soup.KindWmo);soup.WmoTris++;}
        void Quad(float x0,float y0,float x1,float y1,float z){Tri(new(x0,y0,z),new(x1,y0,z),new(x1,y1,z));Tri(new(x0,y0,z),new(x1,y1,z),new(x0,y1,z));}
        Quad(-5,-5,5,5,0);Quad(-3,-3,3,-2.6f,.3f);Quad(-3,2.6f,3,3,.3f);Quad(-3,-2.6f,-2.6f,2.6f,.3f);Quad(2.6f,-2.6f,3,2.6f,.3f);soup.Seal();
        if(WmoCapPatches.Detect(soup,settings).Count!=0)throw new Exception("Walkable low open platform rim was cut out");
        soup=new();Quad(-6,-6,6,6,0);Quad(-2,-2,2,2,1.2f);
        Tri(new(-2,-2,.8f),new(2,2,.8f),new(2,-2,.8f));Tri(new(-2,-2,.8f),new(-2,2,.8f),new(2,2,.8f));
        Tri(new(2,-2,1.2f),new(2.4f,-2,.9f),new(2.4f,2,.9f));Tri(new(2,-2,1.2f),new(2.4f,2,.9f),new(2,2,1.2f));soup.Seal();
        var caps=WmoCapPatches.Detect(soup,settings);
        if(!caps.IsSupersetOf(new[]{2,3,6,7})||caps.Overlaps(new[]{0,1}))throw new Exception("Rejected table kept its walkable bevel or deleted the room floor");
        // A short bevel can sit high on a wall. Its own height span is not
        // its height above the walking floor (the remaining window spikes).
        soup=new();Quad(-5,-5,5,5,0);
        Tri(new(0,-2,1.2f),new(.25f,-2,1.4f),new(.25f,2,1.4f));Tri(new(0,-2,1.2f),new(.25f,2,1.4f),new(0,2,1.2f));
        Tri(new(.25f,-2,1.4f),new(.25f,-2,4),new(.25f,2,4));Tri(new(.25f,-2,1.4f),new(.25f,2,4),new(.25f,2,1.4f));soup.Seal();
        var localCaps=WmoCapPatches.Detect(soup,settings,localObstaclesOnly:true);
        if(!localCaps.IsSupersetOf(new[]{2,3})||localCaps.Overlaps(new[]{0,1}))throw new Exception("Short elevated indoor bevel survived or its broad room floor was removed");
        soup=new();Tri(new(0,-2,0),new(0,2,0),new(0,2,.9f));soup.Seal();
        if(new SourceBodyClearance(soup,settings).Hits(.1f,0,0))throw new Exception("Configured legal climb was replaced by a smaller wall-clearance stepping band");
        // Buried rejected geometry must not poison a merged, supported deck.
        // The same-height cap and a genuine cap at the top must stay blocked.
        soup=new();Quad(-2,-2,2,2,4);soup.Seal();
        var deckSource=new SourceSurfaceFit(soup,settings,null);
        var merged=new DotRecast.Recast.RcHeightfield(3,1,new(-1,0,-.5f),new(2,6,.5f),1,1,0);
        var rejected=new DotRecast.Recast.RcHeightfield(3,1,new(-1,0,-.5f),new(2,6,.5f),1,1,0);
        for(int i=0;i<3;i++){
            merged.spans[i]=new(){smin=0,smax=4,area=NavBake.AreaWmoSolid};
            rejected.spans[i]=new(){smin=0,smax=i==0?2:4,area=NavBake.AreaWmoSolid};
        }
        if(NavBake.RestoreCoveredWmoSpans(merged,rejected,deckSource)!=1||merged.spans[0].area!=SurfaceClearance.WmoTop||merged.spans[1].area!=NavBake.AreaWmoSolid||merged.spans[2].area!=NavBake.AreaWmoSolid)
            throw new Exception("Buried support lost its deck or exposed caps became walkable");
        soup=new();soup.Seal();merged.spans[0].area=NavBake.AreaWmoSolid;
        if(NavBake.RestoreCoveredWmoSpans(merged,rejected,new SourceSurfaceFit(soup,settings,null))!=0)
            throw new Exception("Rejected geometry regained walkability without direct source support");
        soup=new();
        Tri(new(-2,-2,2.6f),new(2,-2,2.6f),new(2,2,5.8f));
        Tri(new(-2,-2,2.6f),new(2,2,5.8f),new(-2,2,5.8f));soup.Seal();
        var slopedDeck=new SourceSurfaceFit(soup,settings,null);
        merged.spans[0].smax=5;
        if(slopedDeck.HasWmoSupport(0,-.5f,5,1)||!slopedDeck.HasWmoCellSupport(0,-.5f,5,1,1)||NavBake.RestoreCoveredWmoSpans(merged,rejected,slopedDeck)!=1)
            throw new Exception("Sloped deck high corner was lost by a centre-only raster provenance check");
        // A contour corner just outside an upper WMO floor must not snap to
        // a lower WMO storey when the upper edge matches its original height.
        soup=new();Quad(-5,-5,5,5,0);Quad(-2,-2,0,2,1.4f);soup.Seal();
        float upper=new SourceSurfaceFit(soup,settings,null).Height(new(.1f,0,1.7f));
        if(MathF.Abs(upper-(1.4f+settings.CellHeight))>.001f)throw new Exception("Upper floor edge was replaced by a lower WMO storey");
        float blockedUpper=new SourceSurfaceFit(soup,settings,null,new HashSet<int>{2,3}).Height(new(.1f,0,1.7f));
        if(MathF.Abs(blockedUpper-settings.CellHeight)>.001f)throw new Exception("Rejected upper cap regained source height authority");
        // A direct landing must not be pulled up onto a nearby trim edge.
        soup=new();Quad(-5,-5,5,5,0);Quad(-2,-2,0,2,.6f);soup.Seal();
        if(MathF.Abs(new SourceSurfaceFit(soup,settings,null).Height(new(.1f,0,.9f))-settings.CellHeight)>.001f)
            throw new Exception("Nearby trim displaced a directly supported floor");
        // A low threshold beside a broad lower floor is not a wall footing.
        soup=new();Quad(0,-4,.6f,4,.4f);Quad(.6f,-4,5,4,0);
        Tri(new(0,-4,0),new(0,4,0),new(0,4,4));Tri(new(0,-4,0),new(0,4,4),new(0,-4,4));soup.Seal();
        if(new SourceSurfaceFit(soup,settings,null).IsWallFooting(.3f,0,.6f))throw new Exception("Low broad-floor transition was rejected as wall trim");
        soup=new();Quad(0,-4,.6f,4,1.2f);Quad(.6f,-4,5,4,0);
        Tri(new(0,-4,0),new(0,4,0),new(0,4,4));Tri(new(0,-4,0),new(0,4,4),new(0,-4,4));soup.Seal();
        if(!new SourceSurfaceFit(soup,settings,null).IsWallFooting(.3f,0,1.4f))throw new Exception("Elevated wall footing was preserved as a low floor transition");
        // One contour's winding repair kept a bent shared portal while the
        // neighbour simplified it to a chord. Both must use the same bend.
        var portalSet=new DotRecast.Recast.RcContourSet();
        var chord=new DotRecast.Recast.RcContour{reg=1,area=NavBake.AreaGround,nverts=4,nrverts=5,
            verts=[0,0,0,2,0,0,2,0,-2,0,2,0,-2,0,0,0],
            rverts=[0,0,0,0,1,0,1,2,0,0,2,2,-2,0,2,0,-2,0,0,0]};
        var bend=new DotRecast.Recast.RcContour{reg=2,area=NavBake.AreaGround,nverts=5,nrverts=5,
            verts=[0,0,2,1,1,0,1,1,0,0,0,0,3,0,0,0,3,0,2,0],
            rverts=[0,0,2,0,1,0,1,1,0,0,0,1,3,0,0,0,3,0,2,0]};
        portalSet.conts.Add(chord);portalSet.conts.Add(bend);
        if(NavContours.SynchronizePortalContours(portalSet)!=1||chord.nverts!=5||chord.verts[4]!=1||chord.verts[6]!=1||chord.verts[7]!=2)
            throw new Exception("Asymmetric contour portal left a triangular floor gap");
        if(NavContours.SynchronizePortalContours(portalSet)!=0)throw new Exception("Portal synchronization was not idempotent");
        // Ground surrounded by road is still one continuous walking floor.
        var areaSolid=new DotRecast.Recast.RcHeightfield(3,3,new(0,0,0),new(3,5,3),1,.1f,0);
        for(int i=0;i<9;i++)areaSolid.spans[i]=new(){smin=0,smax=10,area=i==4?NavBake.AreaGround:NavBake.AreaRoad};
        var areaField=DotRecast.Recast.RcCompacts.BuildCompactHeightfield(new DotRecast.Core.RcContext(),20,5,areaSolid);
        for(int i=0;i<areaField.spanCount;i++){var b=DotRecast.Recast.RcCompactSpanBuilder.NewBuilder(ref areaField.spans[i]);b.reg=i==4?0:1;areaField.spans[i]=b.Build();}areaField.maxRegions=2;
        if(NavBake.RestoreConnectedFloorAreaIslands(areaField)!=1||areaField.areas[4]!=NavBake.AreaGround||areaField.spans[4].reg==0)
            throw new Exception("A ground/road label island lost physical floor coverage");
        var areaContours=NavContours.BuildContours(new DotRecast.Core.RcContext(),areaField,1.3f,12,DotRecast.Recast.RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);
        if(!areaContours.conts.Any(c=>c.area==NavBake.AreaGround&&c.nverts>=3))throw new Exception("Retained single-cell floor region was skipped during contour walking");
        var isolated=DotRecast.Recast.RcCompacts.BuildCompactHeightfield(new DotRecast.Core.RcContext(),20,5,areaSolid);
        if(NavBake.RestoreConnectedFloorAreaIslands(isolated)!=0)throw new Exception("Disconnected filtered surfaces were restored without a retained region");
        // Floors behind a wall do not make a low window bevel a through step.
        soup=new();Quad(-5,-5,5,5,0);
        Tri(new(0,-2,.2f),new(.25f,-2,.4f),new(.25f,2,.4f));Tri(new(0,-2,.2f),new(.25f,2,.4f),new(0,2,.2f));
        Tri(new(.25f,-2,.4f),new(.25f,-2,4),new(.25f,2,4));Tri(new(.25f,-2,.4f),new(.25f,2,4),new(.25f,2,.4f));soup.Seal();
        var lowWallCaps=WmoCapPatches.Detect(soup,settings,localObstaclesOnly:true);
        if(!lowWallCaps.IsSupersetOf(new[]{2,3})||lowWallCaps.Overlaps(new[]{0,1}))throw new Exception("Low wall bevel was mistaken for a two-sided floor threshold");
        soup=new();Quad(0,-4,4,4,1.2f);Quad(4,-4,8,4,0);
        Tri(new(2.9f,-4,1.2f),new(2.9f,4,1.2f),new(2.9f,4,4));Tri(new(2.9f,-4,1.2f),new(2.9f,4,4),new(2.9f,-4,4));soup.Seal();soup.TriKind[2]=soup.TriKind[3]=Extractor.Soup.KindTerrain;
        if(new SourceSurfaceFit(soup,settings,null).IsWallFooting(3.3f,0,1.4f))throw new Exception("A broad local floor footprint was deleted as wall footing");
        static DtMeshData Fan(float centerHeight){var poly=new DtPoly(0,6){vertCount=4};for(int i=0;i<4;i++)poly.verts[i]=i;return new(){header=new(){polyCount=1,vertCount=4,detailVertCount=1,detailTriCount=4},
            polys=[poly],verts=[0,0,0,2,0,0,2,0,2,0,0,2],
            detailMeshes=[new(0,0,1,4)],detailVerts=[1,centerHeight,1],detailTris=[0,1,4,1,1,2,4,1,2,3,4,1,3,0,4,1]};}
        var flat=Fan(0);int removed=PlanarDetailReducer.Reduce(flat);
        if(removed!=1||flat.header.detailTriCount!=2||flat.detailVerts.Length!=0)throw new Exception("Redundant planar fan was retained");
        int borders=0;for(int i=3;i<flat.detailTris.Length;i+=4)for(int e=0;e<3;e++)if(((flat.detailTris[i]>>(e*2))&3)==1)borders++;
        if(borders!=4)throw new Exception("Planar reduction changed boundary edge flags");
        var relief=Fan(.05f);if(PlanarDetailReducer.Reduce(relief)!=0||relief.header.detailTriCount!=4)throw new Exception("Planar reduction removed real relief");
        // An edge sample participates in the shared height profile even when
        // coplanar. It must survive while the redundant interior sample goes.
        var boundary=Fan(0);boundary.detailMeshes=[new(0,0,2,5)];boundary.detailVerts=[1,0,0,1,0,1];boundary.detailTris=[0,4,5,1,4,1,5,1,1,2,5,1,2,3,5,1,3,0,5,1];
        boundary.header.detailVertCount=2;boundary.header.detailTriCount=5;
        if(PlanarDetailReducer.Reduce(boundary)!=1||boundary.header.detailVertCount!=1||boundary.detailVerts[0]!=1||boundary.detailVerts[2]!=0)throw new Exception("Planar reduction removed a shared boundary sample");
        // An integrated table replaces its underlying room floor in the WMO.
        // Its disconnected top still has to be rejected from perimeter support.
        soup=new();Quad(0,0,2.25f,.75f,.1f);Quad(0,1.5f,2.25f,2.25f,.1f);
        Quad(0,.75f,.75f,1.5f,.1f);Quad(1.5f,.75f,2.25f,1.5f,.1f);Quad(.75f,.75f,1.5f,1.5f,1.3f);soup.Seal();
        var solid=new DotRecast.Recast.RcHeightfield(9,9,new(0,0,0),new(2.25f,5,2.25f),.25f,.1f,0);
        for(int y=0;y<9;y++)for(int x=0;x<9;x++)solid.spans[x+y*9]=new(){smin=0,smax=x>=3&&x<=5&&y>=3&&y<=5?13:1,area=NavBake.AreaGround};
        var field=DotRecast.Recast.RcCompacts.BuildCompactHeightfield(new DotRecast.Core.RcContext(),2,5,solid);
        int removedCaps=IndoorSupportGraph.RemoveRaisedIslands(field,Enumerable.Repeat(true,field.spanCount).ToArray(),settings,new SourceSurfaceFit(soup,settings,null));
        if(removedCaps!=9||field.areas.Count(a=>a==NavBake.AreaGround)!=72)throw new Exception("Integrated table top survived or its surrounding room floor was removed");
        // A broad landing beside a few lower stair treads is not a table.
        // Perimeter evidence must surround the component, not merely exist twice.
        soup=new();Quad(0,0,3,3,1.3f);Quad(3,0,4,.75f,0);soup.Seal();
        solid=new DotRecast.Recast.RcHeightfield(12,12,new(0,0,0),new(3,5,3),.25f,.1f,0);
        for(int i=0;i<solid.spans.Length;i++)solid.spans[i]=new(){smin=0,smax=13,area=NavBake.AreaGround};
        field=DotRecast.Recast.RcCompacts.BuildCompactHeightfield(new DotRecast.Core.RcContext(),2,5,solid);
        if(IndoorSupportGraph.RemoveRaisedIslands(field,Enumerable.Repeat(true,field.spanCount).ToArray(),settings,new SourceSurfaceFit(soup,settings,null))!=0)
            throw new Exception("Lower treads beside a broad landing removed the landing as furniture");
        // Cave collision is tessellated into narrow patches whose normals vary.
        // The supported standing disk spans those patches; it is not a trim cap.
        soup=new();Quad(-6,-6,6,6,-1);var interiorFaces=new HashSet<int>();
        for(int i=0;i<20;i++){
            float x0=-4+i*.4f,x1=x0+.4f;
            float H(float x)=>.1f*x+.035f*x*x;
            int at=soup.TriCount;
            Tri(new(x0,-4,H(x0)),new(x1,-4,H(x1)),new(x1,4,H(x1)));
            Tri(new(x0,-4,H(x0)),new(x1,4,H(x1)),new(x0,4,H(x0)));
            if(i>=4&&i<=15){interiorFaces.Add(at);interiorFaces.Add(at+1);}
        }
        soup.Seal();
        if(WmoCapPatches.Detect(soup,settings,localObstaclesOnly:true).Overlaps(interiorFaces))
            throw new Exception("Narrow tessellation patches deleted a supported gently sloping floor");
        // A floor patch beside a wall needs one body radius of clearance,
        // not another body radius at each footprint-perimeter probe.
        soup=new();
        for(int i=0;i<16;i++){
            float x0=-3.2f+i*.4f,x1=x0+.4f;
            float H(float x)=>.1f*x+.035f*x*x;
            Tri(new(x0,0,H(x0)),new(x1,0,H(x1)),new(x1,2f,H(x1)));
            Tri(new(x0,0,H(x0)),new(x1,2f,H(x1)),new(x0,2f,H(x0)));
        }
        Quad(-5,-3,5,5,-1);
        Tri(new(-5,0,-1),new(5,0,4),new(-5,0,4));Tri(new(-5,0,-1),new(5,0,-1),new(5,0,4));soup.Seal();
        var nearbyFloorCaps=WmoCapPatches.Detect(soup,settings,localObstaclesOnly:true);
        if(nearbyFloorCaps.Overlaps(Enumerable.Range(8,16)))
            throw new Exception("Footprint perimeter probes inflated a second body radius near a cave wall");
        // A simplified exterior chord must not remove retained WMO floor
        // cells. Recover only the contour that loses support; remain idempotent.
        var coverageSolid=new DotRecast.Recast.RcHeightfield(4,4,new(0,0,0),new(4,4,4),1,.1f,0);
        for(int i=0;i<16;i++)coverageSolid.spans[i]=new(){smin=0,smax=10,area=NavBake.AreaGround};
        var coverageField=DotRecast.Recast.RcCompacts.BuildCompactHeightfield(new DotRecast.Core.RcContext(),20,5,coverageSolid);
        for(int i=0;i<coverageField.spanCount;i++){var b=DotRecast.Recast.RcCompactSpanBuilder.NewBuilder(ref coverageField.spans[i]);b.reg=1;coverageField.spans[i]=b.Build();}
        var rawSquare=new List<int>{0,10,0,0,0,10,4,0,4,10,4,0,4,10,0,0};
        var cutSquare=new List<int>{0,10,0,0,4,10,4,0,4,10,0,0};
        if(NavContours.PreserveFloorCoverage(rawSquare,cutSquare,coverageField,1,NavBake.AreaGround,4,12,DotRecast.Recast.RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES,(x,z,id)=>true)==0)
            throw new Exception("Contour chord discarded supported WMO floor cells");
        for(int x=0;x<4;x++)for(int z=0;z<4;z++)if(!NavContours.ContainsPoint(cutSquare,x+.5,z+.5))throw new Exception("Refined floor contour still loses a retained floor center");
        if(NavContours.PreserveFloorCoverage(rawSquare,cutSquare,coverageField,1,NavBake.AreaGround,4,12,0,(x,z,id)=>true)!=0)
            throw new Exception("Floor contour coverage repair was not idempotent");
        // Hole winding is the reverse case: simplifying an L-shaped hole
        // into its hull must not swallow the retained floor in its notch.
        var holeSolid=new DotRecast.Recast.RcHeightfield(6,6,new(0,0,0),new(6,4,6),1,.1f,0);
        for(int i=0;i<36;i++)holeSolid.spans[i]=new(){smin=0,smax=10,area=NavBake.AreaGround};
        var holeField=DotRecast.Recast.RcCompacts.BuildCompactHeightfield(new DotRecast.Core.RcContext(),20,5,holeSolid);
        var rawHole=new List<int>{1,10,1,0,5,10,1,0,5,10,5,0,3,10,5,0,3,10,3,0,1,10,3,0};
        var cutHole=new List<int>{1,10,1,0,5,10,1,0,5,10,5,0,1,10,3,0};
        for(int x=0;x<6;x++)for(int z=0;z<6;z++){
            int i=holeField.cells[x+z*6].index;var b=DotRecast.Recast.RcCompactSpanBuilder.NewBuilder(ref holeField.spans[i]);b.reg=1;holeField.spans[i]=b.Build();
            if(NavContours.ContainsPoint(rawHole,x+.5,z+.5))holeField.areas[i]=NavBake.AreaNull;
        }
        NavContours.PreserveFloorCoverage(rawHole,cutHole,holeField,1,NavBake.AreaGround,4,12,0,(x,z,id)=>true);
        if(NavContours.ContainsPoint(cutHole,2.5,3.5))
            throw new Exception("Simplified obstacle hole swallowed retained floor in its notch");
        // Two linked quads have unequal boundary sampling. A raised midpoint
        // on one side must be represented on both sides, without flattening it.
        var left=new DtPoly(0,6){vertCount=4};var right=new DtPoly(1,6){vertCount=4};
        for(int i=0;i<4;i++){left.verts[i]=i;right.verts[i]=i+4;}left.neis[1]=2;right.neis[3]=1;
        var seam=new DtMeshData{header=new(){polyCount=2,vertCount=8,detailVertCount=2,detailTriCount=7},polys=[left,right],
            verts=[0,0,0,2,0,0,2,0,2,0,0,2,2,0,0,4,0,0,4,0,2,2,0,2],
            detailMeshes=[new(0,0,2,5),new(2,5,0,2)],detailVerts=[2,.5f,1,1,0,1],
            detailTris=[0,1,5,1,1,4,5,1,4,2,5,1,2,3,5,1,3,0,5,1,0,1,2,5,0,2,3,20]};
        var fit=new SourceSurfaceFit(new Extractor.Soup(),settings,null);
        var sync=typeof(SourceSurfaceFit).GetMethod("SynchronizeDetailEdges",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        sync.Invoke(fit,[seam]);
        foreach(int p in new[]{0,1}){var dm=seam.detailMeshes[p];bool midpoint=false;
            for(int i=0;i<dm.vertCount;i++){int v=(dm.vertBase+i)*3;if(seam.detailVerts[v]==2&&seam.detailVerts[v+2]==1){midpoint=true;if(seam.detailVerts[v+1]!=.5f)throw new Exception("Shared stair profile was flattened");}}
            if(!midpoint)throw new Exception("Shared boundary breakpoint was not carried to both polygons");
            for(int t=0;t<dm.triCount;t++)for(int k=0;k<3;k++)if(seam.detailTris[(dm.triBase+t)*4+k]>=4+dm.vertCount)throw new Exception("Shared-edge split emitted an invalid local index");
        }
        Console.WriteLine("Mesh quality checks passed: low open rim retained, tabletop/bevel rejected, planar fan reduced, boundary samples/flags and real relief retained; unequal shared stair profiles synchronized.");
    }
}
