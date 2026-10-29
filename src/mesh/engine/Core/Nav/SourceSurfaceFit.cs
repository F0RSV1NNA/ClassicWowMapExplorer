using System.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;

namespace MapExtract;

// Explorer extension: final Detour vertices use the original collision surface,
// not an arbitrary nearby voxel column. Coarse XY, topology, areas and links are retained;
// only verified buried ramps receive one bounded detail repair.
internal sealed partial class SourceSurfaceFit
{
    [ThreadStatic] internal static Action<string,DtMeshData>? StageObserver;
    [ThreadStatic] internal static Action<SourceSurfaceFit,DtMeshData>? CoverageObserver;
    internal void ObserveCoverage(DtMeshData data)=>CoverageObserver?.Invoke(this,data);
    internal IEnumerable<(Vector3 A,Vector3 B,Vector3 C)> AuditFloors()=>bins.Values.SelectMany(f=>f).Distinct().Where(f=>f.Walkable&&f.Kind==Extractor.Soup.KindWmo).Select(f=>(f.A,f.B,f.C));
    const float BinSize = 8;
    readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, byte Kind = 0,bool Walkable=true);
    readonly Dictionary<(int X, int Y), List<Face>> bins = new();
    readonly Dictionary<(int X,int Y),List<Face>> rampBins = new();
    readonly Dictionary<(int X,int Y),List<Face>> footingWalls = new();
    readonly Dictionary<Vector3, (float Height,int Layer)> cache = new();
    readonly Dictionary<Face,int> wmoFaces = new();
    readonly List<int> parents = new();
    readonly record struct BlockedPatch(float MinX,float MinY,float MaxX,float MaxY);
    readonly Dictionary<Face,List<BlockedPatch>> blockedCaps = new();
    readonly float clearance, drop, rise, bodyHeight,edgeReach,footingReach,agentRadius;

    public SourceSurfaceFit(Extractor.Soup soup, NavBake.Settings settings, NavBake.Clip? clip,IReadOnlySet<int>? sourceCaps=null)
    {
        clearance = settings.CellHeight;
        edgeReach=MathF.Min(settings.AgentRadius,settings.CellSize*(settings.EdgeMaxError+MathF.Sqrt(.5f)));
        drop = settings.AgentMaxClimb + 2 * settings.CellHeight;
        rise = settings.AgentMaxClimb + 2 * settings.CellHeight;
        bodyHeight = settings.AgentHeight;
        agentRadius=settings.AgentRadius;
        footingReach=settings.AgentRadius*2+settings.CellSize;
        float slope = MathF.Cos(settings.AgentMaxSlope * MathF.PI / 180);
        var seen = new HashSet<Face>();
        var edges=new Dictionary<(Vector3,Vector3),int>();
        for (int t = 0; t < soup.TriCount; t++)
        {
            byte kind=t<soup.TriKind.Count?soup.TriKind[t]:(byte)(t<soup.TerrainTris?0:1);
            var a=soup.Verts[soup.Indices[t*3]];var b=soup.Verts[soup.Indices[t*3+1]];var c=soup.Verts[soup.Indices[t*3+2]];
            var normal = Vector3.Cross(b-a,c-a);
            if(kind==Extractor.Soup.KindWmo&&normal.LengthSquared()>1e-9f&&MathF.Abs(normal.Z)<normal.Length()*slope){
                var wall=new Face(a,b,c,kind,false);
                for(int x=Bin(MathF.Min(a.X,MathF.Min(b.X,c.X))-footingReach);x<=Bin(MathF.Max(a.X,MathF.Max(b.X,c.X))+footingReach);x++)
                for(int y=Bin(MathF.Min(a.Y,MathF.Min(b.Y,c.Y))-footingReach);y<=Bin(MathF.Max(a.Y,MathF.Max(b.Y,c.Y))+footingReach);y++){
                    if(!footingWalls.TryGetValue((x,y),out var list))footingWalls[(x,y)]=list=new();list.Add(wall);
                }
            }
            if(MathF.Abs(normal.Z)<1e-9f)continue;
            bool walkable=normal.Z>0&&normal.Z>=slope*normal.Length()&&sourceCaps?.Contains(t)!=true&&
                          !(settings.BlockDoodads&&kind==Extractor.Soup.KindDoodad);
            var f=new Face(a,b,c,kind,walkable);
            if(!seen.Add(f))continue;
            float x0=MathF.Min(f.A.X,MathF.Min(f.B.X,f.C.X)), x1=MathF.Max(f.A.X,MathF.Max(f.B.X,f.C.X));
            float y0=MathF.Min(f.A.Y,MathF.Min(f.B.Y,f.C.Y)), y1=MathF.Max(f.A.Y,MathF.Max(f.B.Y,f.C.Y));
            if (clip is {} bounds) { x0=MathF.Max(x0,bounds.MinX-1);x1=MathF.Min(x1,bounds.MaxX+1);y0=MathF.Max(y0,bounds.MinY-1);y1=MathF.Min(y1,bounds.MaxY+1); }
            if(x0>x1||y0>y1)continue;
            if(walkable&&kind==Extractor.Soup.KindWmo)
            {
                int id=parents.Count;parents.Add(id);wmoFaces.Add(f,id);
                Join(f.A,f.B,id);Join(f.B,f.C,id);Join(f.C,f.A,id);
            }
            for(int x=Bin(x0);x<=Bin(x1);x++)for(int y=Bin(y0);y<=Bin(y1);y++)
            {
                if(!bins.TryGetValue((x,y),out var faces))bins[(x,y)]=faces=new();
                faces.Add(f);
                if(walkable&&kind==Extractor.Soup.KindWmo&&MathF.Max(f.A.Z,MathF.Max(f.B.Z,f.C.Z))-MathF.Min(f.A.Z,MathF.Min(f.B.Z,f.C.Z))>settings.CellHeight*2.01f){if(!rampBins.TryGetValue((x,y),out var ramps))rampBins[(x,y)]=ramps=new();ramps.Add(f);}
            }
        }
        void Join(Vector3 a,Vector3 b,int id)
        {
            bool flip=a.X>b.X||(a.X==b.X&&(a.Y>b.Y||(a.Y==b.Y&&a.Z>b.Z)));
            var edge=flip?(b,a):(a,b);
            if(edges.TryGetValue(edge,out int other))parents[Root(id)]=Root(other);
            else edges[edge]=id;
        }
    }

    static int Bin(float v) => (int)MathF.Floor(v/BinSize);

    // A footing may share one coplanar patch with the entire building floor.
    // Classify the exposed strip locally, using the actual wall and an uncovered
    // lower floor on the SAME side of that wall. Terrain beneath a room is never
    // evidence: it has no standing headroom. Do not widen voxel erosion globally.
    internal bool IsWallFooting(float x,float y,float rasterHeight)
    {
        if(!footingWalls.TryGetValue((Bin(x),Bin(y)),out var walls)||
           !bins.TryGetValue((Bin(x),Bin(y)),out var supports))return false;
        float floor=float.NaN,gap=float.MaxValue;
        foreach(var f in supports){
            if(!f.Walkable||f.Kind!=Extractor.Soup.KindWmo)continue;
            var n=Vector3.Normalize(Vector3.Cross(f.B-f.A,f.C-f.A));if(n.Z<.98f)continue;
            float h=HeightAt(f,x,y),d=rasterHeight-h;
            if(d>=-.01f&&d<=clearance*2.01f&&d<gap){floor=h;gap=d;}
        }
        if(!float.IsFinite(floor))return false;
        foreach(var f in supports){
            if(!f.Walkable||f.Kind!=Extractor.Soup.KindWmo)continue;
            var n=Vector3.Normalize(Vector3.Cross(f.B-f.A,f.C-f.A));if(n.Z>=.98f)continue;
            // A short moulding bevel is not evidence of a usable staircase.
            // Require a genuine rising continuation before exempting a nearby
            // wall base; tangent length alone also accepts long narrow trim.
            float heightSpan=MathF.Max(f.A.Z,MathF.Max(f.B.Z,f.C.Z))-MathF.Min(f.A.Z,MathF.Min(f.B.Z,f.C.Z));
            if(heightSpan<=clearance*2.01f)continue;
            var along=Vector2.Normalize(new Vector2(n.X,n.Y));
            float ra=Vector2.Dot(new(f.A.X,f.A.Y),along),rb=Vector2.Dot(new(f.B.X,f.B.Y),along),rc=Vector2.Dot(new(f.C.X,f.C.Y),along);
            if(MathF.Max(ra,MathF.Max(rb,rc))-MathF.Min(ra,MathF.Min(rb,rc))<footingReach-clearance)continue;
            var across=Vector2.Normalize(new Vector2(-n.Y,n.X));
            float a=Vector2.Dot(new(f.A.X,f.A.Y),across),b=Vector2.Dot(new(f.B.X,f.B.Y),across),c=Vector2.Dot(new(f.C.X,f.C.Y),across);
            if(MathF.Max(a,MathF.Max(b,c))-MathF.Min(a,MathF.Min(b,c))<footingReach-clearance)continue;
            float ramp=HeightAt(f,x,y);
            if(!float.IsFinite(ramp)){
                double nearest=double.PositiveInfinity;
                ReadOnlySpan<Vector3> vertices=[f.A,f.B,f.C];
                for(int e=0;e<3;e++){var p=vertices[e];var q=vertices[(e+1)%3];double dx=q.X-p.X,dy=q.Y-p.Y,l=dx*dx+dy*dy;
                    double t=l>0?Math.Clamp((((double)x-p.X)*dx+((double)y-p.Y)*dy)/l,0,1):0;
                    nearest=Math.Min(nearest,Math.Pow(x-p.X-t*dx,2)+Math.Pow(y-p.Y-t*dy,2));
                }
                if(nearest<=edgeReach*edgeReach)ramp=f.A.Z-(n.X*(x-f.A.X)+n.Y*(y-f.A.Y))/n.Z;
            }
            if(MathF.Abs(ramp-floor)<=clearance*2.01f)return false;
        }
        foreach(var wall in walls){
            if(MathF.Max(wall.A.Z,MathF.Max(wall.B.Z,wall.C.Z))<=floor+rise-clearance||
               MathF.Min(wall.A.Z,MathF.Min(wall.B.Z,wall.C.Z))>=floor+bodyHeight)continue;
            float bx=Math.Clamp(x,MathF.Min(wall.A.X,MathF.Min(wall.B.X,wall.C.X)),MathF.Max(wall.A.X,MathF.Max(wall.B.X,wall.C.X)));
            float by=Math.Clamp(y,MathF.Min(wall.A.Y,MathF.Min(wall.B.Y,wall.C.Y)),MathF.Max(wall.A.Y,MathF.Max(wall.B.Y,wall.C.Y)));
            if((bx-x)*(bx-x)+(by-y)*(by-y)>footingReach*footingReach)continue;
            // Clip to the character's body above legal stepping height: a stair
            // riser or the vertical edge of this very lip is not a backing wall.
            var polygon=new List<Vector3>{wall.A,wall.B,wall.C};
            foreach(var (height,sign) in new[]{(floor+rise-clearance,1f),(floor+bodyHeight,-1f)}){
                var clipped=new List<Vector3>();
                for(int i=0;i<polygon.Count;i++){var a=polygon[i];var b=polygon[(i+1)%polygon.Count];float da=(a.Z-height)*sign,db=(b.Z-height)*sign;
                    if(da>=0)clipped.Add(a);if((da>=0)!=(db>=0))clipped.Add(a+(b-a)*(da/(da-db)));
                }polygon=clipped;
            }
            Vector2 closest=default;double best=double.PositiveInfinity;
            for(int i=0;i<polygon.Count;i++){var a=polygon[i];var b=polygon[(i+1)%polygon.Count];double dx=b.X-a.X,dy=b.Y-a.Y,l=dx*dx+dy*dy;
                double t=l>0?Math.Clamp((((double)x-a.X)*dx+((double)y-a.Y)*dy)/l,0,1):0;
                double qx=a.X+t*dx,qy=a.Y+t*dy,d=(qx-x)*(qx-x)+(qy-y)*(qy-y);
                if(d<best){best=d;closest=new((float)qx,(float)qy);}
            }
            if(best<.0025||best>footingReach*footingReach)continue;
            var away=Vector2.Normalize(new Vector2(x,y)-closest);
            var point=new Vector3(x+away.X*footingReach,y+away.Y*footingReach,floor+clearance);
            // Direct support only: edge extrapolation must not invent the lower
            // floor used to reject this one. Reject only a genuinely exposed edge.
            if(!bins.TryGetValue((Bin(point.X),Bin(point.Y)),out var lower))continue;
            foreach(var f in lower){if(!f.Walkable||Blocked(f,point.X,point.Y))continue;
                float h=HeightAt(f,point.X,point.Y),delta=floor-h;
                // A sub-voxel change between floor planes is a threshold,
                // not evidence of an elevated wall footing.
                if(!(delta>clearance+.001f&&delta<=bodyHeight))continue;
                if(lower.Any(q=>{float above=HeightAt(q,point.X,point.Y)-h;return above>.03f&&above<bodyHeight;}))continue;
                // Sloping continuation is a real approach/ramp, not a floor
                // beside a wall base. Keep its tread representation intact.
                var normal=Vector3.Normalize(Vector3.Cross(f.B-f.A,f.C-f.A));if(normal.Z<.98f)continue;
                // Terrain exposed through a nearby gap in the WMO footprint
                // does not make a fully supported indoor floor a wall footing.
                // A lower WMO floor remains separate stair/trim evidence.
                if(f.Kind==Extractor.Soup.KindTerrain&&BroadFlatFloor(x,y,floor,8))return false;
                // A small transition onto a broad, flat exposed floor is a
                // walkable threshold. A nearby stair tread or narrow shelf is
                // not: it cannot support this footprint at one floor height.
                if(delta<=clearance*2.01f&&BroadFlatFloor(point.X,point.Y,h))return false;
                return true;
            }
        }
        return false;
    }

    bool BroadFlatFloor(float x,float y,float height,int minimum=6)
    {
        int supported=0;
        for(int d=0;d<8;d++){
            float angle=d*MathF.PI/4,px=x+MathF.Cos(angle)*agentRadius,py=y+MathF.Sin(angle)*agentRadius;
            if(!bins.TryGetValue((Bin(px),Bin(py)),out var nearby))continue;
            // Geometric support under a nearby upper floor is not standing
            // room. Otherwise a lower stair layer can masquerade as a broad
            // landing and regain height authority over the real upper floor.
            if(minimum==8&&nearby.Any(f=>{float above=HeightAt(f,px,py)-height;return above>.03f&&above<bodyHeight;}))continue;
            if(nearby.Any(f=>f.Walkable&&f.Kind==Extractor.Soup.KindWmo&&
                !Blocked(f,px,py)&&MathF.Abs(HeightAt(f,px,py)-height)<.03f))supported++;
        }
        return supported>=minimum;
    }

    internal void BlockCap(RcHeightfield field,int column,RcSpan span)
    {
        // A rejected cap must also lose authority over final floor heights.
        // Otherwise a contour vertex on its edge gets projected back upward.
        float x=field.bmin.Z+(column/field.width+.5f)*field.cs;
        float y=field.bmin.X+(column%field.width+.5f)*field.cs;
        float height=field.bmin.Y+span.smax*field.ch;
        BlockFloor(x,y,height,field.cs,field.ch);
    }
    internal bool HasWmoSupport(float x,float y,float height,float tolerance)
    {
        if(!bins.TryGetValue((Bin(x),Bin(y)),out var faces))return false;
        return faces.Any(f=>f.Walkable&&f.Kind==Extractor.Soup.KindWmo&&MathF.Abs(height-HeightAt(f,x,y))<=tolerance);
    }
    internal bool HasLowerFloor(float x,float y,float height,float minDrop,float maxDrop)
    {
        if(!bins.TryGetValue((Bin(x),Bin(y)),out var faces))return false;
        return faces.Any(f=>f.Walkable&&f.Kind==Extractor.Soup.KindWmo&&height-HeightAt(f,x,y)>minDrop&&height-HeightAt(f,x,y)<maxDrop);
    }
    internal void BlockFloor(float x,float y,float height,float cellSize,float cellHeight)
    {
        if(!bins.TryGetValue((Bin(x),Bin(y)),out var faces))return;
        var matches=new List<(Face Face,float Gap)>();
        foreach(var f in faces)
        {
            if(!f.Walkable||f.Kind!=Extractor.Soup.KindWmo)continue;
            float d=(f.B.Y-f.C.Y)*(f.A.X-f.C.X)+(f.C.X-f.B.X)*(f.A.Y-f.C.Y);
            if(MathF.Abs(d)<1e-9f)continue;
            float a=((f.B.Y-f.C.Y)*(x-f.C.X)+(f.C.X-f.B.X)*(y-f.C.Y))/d;
            float b=((f.C.Y-f.A.Y)*(x-f.C.X)+(f.A.X-f.C.X)*(y-f.C.Y))/d;
            if(a<-.00001f||b<-.00001f||a+b>1.00001f)continue;
            float z=a*f.A.Z+b*f.B.Z+(1-a-b)*f.C.Z;
            float gap=MathF.Abs(z-height);
            if(gap<=cellHeight*2.01f)matches.Add((f,gap));
        }
        // Only revoke the closest cap layer, not a lower floor within two voxels.
        // The old broad tolerance could remove that floor's height authority too.
        if(matches.Count>0){float closest=matches.Min(m=>m.Gap);if(closest<=cellHeight*1.15f)foreach(var match in matches)if(match.Gap<=closest+.01f){
            if(!blockedCaps.TryGetValue(match.Face,out var patches))blockedCaps[match.Face]=patches=new();
            float half=cellSize*.501f;patches.Add(new(x-half,y-half,x+half,y+half));
        }}
        cache.Clear();
    }

    bool Blocked(Face face,float x,float y)=>blockedCaps.TryGetValue(face,out var patches)&&patches.Any(p=>x>=p.MinX&&x<=p.MaxX&&y>=p.MinY&&y<=p.MaxY);

    int Root(int id)
    {
        while(parents[id]!=id){parents[id]=parents[parents[id]];id=parents[id];}
        return id;
    }

    internal float Height(Vector3 point)=>Sample(point).Height;

    (float Height,int Layer) Sample(Vector3 point)
    {
        if(cache.TryGetValue(point,out var saved))return saved;
        float best=float.NaN, gap=float.MaxValue;int layer=int.MinValue;
        var hits=new List<(float Height,int Layer,bool Walkable)>();
        if(bins.TryGetValue((Bin(point.X),Bin(point.Y)),out var faces))foreach(var f in faces)
        {
            float d=(f.B.Y-f.C.Y)*(f.A.X-f.C.X)+(f.C.X-f.B.X)*(f.A.Y-f.C.Y);
            if(MathF.Abs(d)<1e-9f)continue;
            float a=((f.B.Y-f.C.Y)*(point.X-f.C.X)+(f.C.X-f.B.X)*(point.Y-f.C.Y))/d;
            float b=((f.C.Y-f.A.Y)*(point.X-f.C.X)+(f.A.X-f.C.X)*(point.Y-f.C.Y))/d;
            if(a < -1e-5f || b < -1e-5f || a+b > 1.00001f)continue;
            bool walkable=f.Walkable&&!Blocked(f,point.X,point.Y);
            hits.Add((a*f.A.Z+b*f.B.Z+(1-a-b)*f.C.Z,walkable&&f.Kind==Extractor.Soup.KindWmo?Root(wmoFaces[f])+1:-1,walkable));
        }
        foreach(var hit in hits)
        {
            if(!hit.Walkable)continue;
            float height=hit.Height;
            // Terrain underneath a nearby WMO floor is not a usable lower floor:
            // the character cannot fit in that gap. Do not project down into it.
            if(hits.Any(above=>above.Height-height>.03f&&above.Height-height<bodyHeight))continue;
            float target=height+clearance;
            float delta=target-point.Z;
            if(delta < -drop || delta > rise || MathF.Abs(delta)>=gap)continue;
            best=target;gap=MathF.Abs(delta);layer=hit.Layer;
        }
        // A contour corner can fall just beyond the collision footprint by the
        // contour simplification budget. Do not leave its stale wall voxel height
        // or drop it to terrain under a nearby built floor. Extend only the nearest
        // eligible WMO plane within that bounded horizontal edge band.
        // A directly supported terrain sample within voxel error wins over an
        // extrapolated WMO plane. Otherwise a nearby footing extends sideways
        // into clear ground and creates exactly the exterior wall-base ramp.
        if(gap>clearance*2.01f)foreach(var candidate in EdgeSupports(point)){
            float delta=candidate.Height-point.Z;
            // Extrapolation may recover a floor edge within voxel error; it
            // cannot promote that edge onto a higher nearby obstacle/storey.
            if(delta < -drop || delta > clearance || MathF.Abs(delta)>=gap)continue;
            // A directly covered building floor remains authoritative within a
            // climb. Edge extrapolation may rescue a different storey, but must
            // not pull an ordinary landing upward onto nearby trim.
            if(layer>0 && gap<=rise-2*clearance && !candidate.Ramp)continue;
            best=candidate.Height;gap=MathF.Abs(delta);layer=candidate.Layer;
        }
        return cache[point]=(best,layer);
    }

    static float HeightAt(Face f,float x,float y)
    {
        float d=(f.B.Y-f.C.Y)*(f.A.X-f.C.X)+(f.C.X-f.B.X)*(f.A.Y-f.C.Y);
        if(MathF.Abs(d)<1e-9f)return float.NaN;
        float a=((f.B.Y-f.C.Y)*(x-f.C.X)+(f.C.X-f.B.X)*(y-f.C.Y))/d;
        float b=((f.C.Y-f.A.Y)*(x-f.C.X)+(f.A.X-f.C.X)*(y-f.C.Y))/d;
        return a>=-1e-5f&&b>=-1e-5f&&a+b<=1.00001f?a*f.A.Z+b*f.B.Z+(1-a-b)*f.C.Z:float.NaN;
    }

    IEnumerable<(float Height,int Layer,bool Ramp)> EdgeSupports(Vector3 point)
    {
        var seen=new HashSet<Face>();
        for(int bx=Bin(point.X-edgeReach);bx<=Bin(point.X+edgeReach);bx++)for(int by=Bin(point.Y-edgeReach);by<=Bin(point.Y+edgeReach);by++)
        if(bins.TryGetValue((bx,by),out var faces))foreach(var f in faces){
            if(!f.Walkable||f.Kind!=Extractor.Soup.KindWmo||!seen.Add(f)||float.IsFinite(HeightAt(f,point.X,point.Y)))continue;
            Vector3 nearest=default;float distance=float.MaxValue;
            ReadOnlySpan<Vector3> vertices=[f.A,f.B,f.C];
            for(int e=0;e<3;e++){
                var a=vertices[e];var b=vertices[(e+1)%3];var ab=b-a;
                float length=ab.X*ab.X+ab.Y*ab.Y;if(length<1e-12f)continue;
                float t=Math.Clamp(((point.X-a.X)*ab.X+(point.Y-a.Y)*ab.Y)/length,0,1);
                var q=a+t*ab;float d=(q.X-point.X)*(q.X-point.X)+(q.Y-point.Y)*(q.Y-point.Y);
                if(d<distance){nearest=q;distance=d;}
            }
            if(distance>edgeReach*edgeReach||Blocked(f,nearest.X,nearest.Y))continue;
            var normal=Vector3.Cross(f.B-f.A,f.C-f.A);
            float height=f.A.Z-(normal.X*(point.X-f.A.X)+normal.Y*(point.Y-f.A.Y))/normal.Z+clearance;
            // Verify the source edge itself is a valid floor at this storey.
            bool roof=false;
            if(bins.TryGetValue((Bin(nearest.X),Bin(nearest.Y)),out var overhead))foreach(var other in overhead){
                float above=HeightAt(other,nearest.X,nearest.Y)-nearest.Z;
                if(above>.03f&&above<bodyHeight){roof=true;break;}
            }
            if(roof)continue;
            if(bins.TryGetValue((Bin(point.X),Bin(point.Y)),out var covers)&&covers.Any(other=>{
                float above=HeightAt(other,point.X,point.Y)-(height-clearance);
                return above>.03f&&above<bodyHeight;
            }))continue;
            float span=MathF.Max(f.A.Z,MathF.Max(f.B.Z,f.C.Z))-MathF.Min(f.A.Z,MathF.Min(f.B.Z,f.C.Z));
            var across=Vector2.Normalize(new Vector2(-normal.Y,normal.X));
            float wa=Vector2.Dot(new(f.A.X,f.A.Y),across),wb=Vector2.Dot(new(f.B.X,f.B.Y),across),wc=Vector2.Dot(new(f.C.X,f.C.Y),across);
            bool ramp=span>clearance*2.01f&&MathF.Max(wa,MathF.Max(wb,wc))-MathF.Min(wa,MathF.Min(wb,wc))>=footingReach-clearance;
            yield return (height,Root(wmoFaces[f])+1,ramp);
        }
    }

    public int Apply(DtMeshData data)
    {
        StageObserver?.Invoke("input",data);
        var used=new bool[data.header.vertCount];
        int moved=0;float down=0,up=0;
        bool Surface(int p) => data.polys[p].GetPolyType()==0 && data.polys[p].GetArea() is NavBake.AreaGround or NavBake.AreaRoad or NavBake.AreaSwimFloor or NavBake.AreaBlocked;
        void Fit(float[] verts,int v,float? ownerHeight=null,bool terrainOwner=false,float ownerMin=float.NegativeInfinity,float ownerMax=float.PositiveInfinity)
        {
            int at=v*3;var original=new Vector3(verts[at+2],verts[at],verts[at+1]);
            float height;
            if(ownerHeight is {} expected){
                var owned=Sample(new(original.X,original.Y,expected));var direct=Sample(original);
                height=owned.Height;
                // A bad detail sample can be closer to terrain UNDER a stair
                // than to the stair itself. Its polygon, not that bad sample,
                // supplies the storey. Preserve verified terrain relief on
                // terrain polygons and verified nearby detail in the same band.
                if(float.IsFinite(direct.Height)&&
                   ((float.IsFinite(height)&&MathF.Abs(direct.Height-height)<=rise-clearance)||
                    (terrainOwner&&direct.Layer==-1)||
                    (!float.IsFinite(height)&&direct.Layer>0&&direct.Height>=ownerMin&&direct.Height<=ownerMax)))height=direct.Height;
                if(!float.IsFinite(height))height=expected;
            }else height=Height(original);
            if(!float.IsFinite(height) || height<data.header.bmin.Y || height>data.header.bmax.Y || MathF.Abs(height-original.Z)<1e-4f)return;
            verts[at+1]=height;moved++;
            down=MathF.Max(down,original.Z-height);up=MathF.Max(up,height-original.Z);
        }
        for(int p=0;p<data.header.polyCount;p++)if(Surface(p))
        {
            var poly=data.polys[p];for(int i=0;i<poly.vertCount;i++)used[poly.verts[i]]=true;
        }
        for(int i=0;i<used.Length;i++)if(used[i])Fit(data.verts,i);
        for(int p=0;p<data.header.polyCount;p++)if(Surface(p)&&data.detailMeshes!=null&&p<data.detailMeshes.Length){
            var poly=data.polys[p];var detail=data.detailMeshes[p];
            Vector3 Coarse(int i){int at=poly.verts[i]*3;return new(data.verts[at+2],data.verts[at],data.verts[at+1]);}
            bool terrainOwner=true;float min=float.PositiveInfinity,max=float.NegativeInfinity;
            for(int i=0;i<poly.vertCount;i++){var q=Coarse(i);terrainOwner&=Sample(q).Layer==-1;min=MathF.Min(min,q.Z);max=MathF.Max(max,q.Z);}
            for(int i=0;i<detail.vertCount;i++){
                int at=(detail.vertBase+i)*3;double x=data.detailVerts[at+2],y=data.detailVerts[at];float? expected=null;var a=Coarse(0);
                for(int t=1;t+1<poly.vertCount;t++){
                    var b=Coarse(t);var c=Coarse(t+1);
                    double det=((double)b.X-a.X)*(c.Y-a.Y)-((double)b.Y-a.Y)*(c.X-a.X);if(Math.Abs(det)<1e-12)continue;
                    double u=((x-a.X)*(c.Y-a.Y)-(y-a.Y)*(c.X-a.X))/det,v=(((double)b.X-a.X)*(y-a.Y)-((double)b.Y-a.Y)*(x-a.X))/det;
                    if(u<-.001||v<-.001||u+v>1.001)continue;
                    expected=(float)(a.Z+u*(b.Z-a.Z)+v*(c.Z-a.Z));break;
                }
                Fit(data.detailVerts,detail.vertBase+i,expected,terrainOwner,min-clearance,max+clearance);
            }
        }
        StageObserver?.Invoke("fit",data);
        // A fitted triangle can still chord across an unsampled terrain ridge.
        // Linear surfaces attain their largest separation at the vertices of
        // their XY intersection. Check those exactly and conservatively lift
        // shared vertices, never cutting triangles or moving between storeys.
        // One bounded repair of verified below-collision WMO ramps only.
        // Flat floors, caps and downward differences never trigger refinement.
        moved+=RepairBuriedRampDetail(data,ref up,ref down);
        StageObserver?.Invoke("ramp-repair",data);
        var lifts=new float[data.header.vertCount+data.detailVerts.Length/3];
        var visited=new HashSet<Face>();
        Vector3 Position(int id)
        {
            var v=id<data.header.vertCount?data.verts:data.detailVerts;
            int at=(id<data.header.vertCount?id:id-data.header.vertCount)*3;
            return new(v[at+2],v[at],v[at+1]);
        }
        for(int p=0;p<data.header.polyCount;p++)if(Surface(p)&&data.detailMeshes!=null&&p<data.detailMeshes.Length)
        {
            var poly=data.polys[p];var dm=data.detailMeshes[p];
            int Id(int local)=>local<poly.vertCount?poly.verts[local]:data.header.vertCount+dm.vertBase+local-poly.vertCount;
            for(int t=0;t<dm.triCount;t++)
            {
                int at=(dm.triBase+t)*4;
                int ia=Id(data.detailTris[at]),ib=Id(data.detailTris[at+1]),ic=Id(data.detailTris[at+2]);
                var tri=new Face(Position(ia),Position(ib),Position(ic));
                float minX=MathF.Min(tri.A.X,MathF.Min(tri.B.X,tri.C.X)),maxX=MathF.Max(tri.A.X,MathF.Max(tri.B.X,tri.C.X));
                float minY=MathF.Min(tri.A.Y,MathF.Min(tri.B.Y,tri.C.Y)),maxY=MathF.Max(tri.A.Y,MathF.Max(tri.B.Y,tri.C.Y));
                float minZ=MathF.Min(tri.A.Z,MathF.Min(tri.B.Z,tri.C.Z));
                float lift=0;visited.Clear();
                for(int x=Bin(minX);x<=Bin(maxX);x++)for(int y=Bin(minY);y<=Bin(maxY);y++)
                    if(bins.TryGetValue((x,y),out var faces))foreach(var face in faces)
                    {
                        if(!face.Walkable||!visited.Add(face)||MathF.Max(face.A.Z,MathF.Max(face.B.Z,face.C.Z))<=minZ)continue;
                        // WMO floors need the same interior coverage check as
                        // terrain. Only extend a WMO plane across a triangle
                        // whose corners already belong to that floor layer;
                        // a small higher trim cap must not lift nearby ground.
                        bool foreignLayer=face.Kind!=Extractor.Soup.KindTerrain&&!SameFloor(tri,face);
                        // Small residuals include flat treads as well as ramps.
                        float deficit=MaximumDeficit(tri,face,blockedCaps.ContainsKey(face)?(x,y)=>Blocked(face,x,y):null);
                        if(foreignLayer&&deficit>clearance*.5f)continue;
                        if(deficit>0&&deficit<=drop)lift=MathF.Max(lift,deficit+.01f);
                    }
                if(lift<=0)continue;
                lifts[ia]=MathF.Max(lifts[ia],lift);lifts[ib]=MathF.Max(lifts[ib],lift);lifts[ic]=MathF.Max(lifts[ic],lift);
            }
        }
        float terrainRise=0;
        for(int id=0;id<lifts.Length;id++)if(lifts[id]>0)
        {
            var v=id<data.header.vertCount?data.verts:data.detailVerts;
            int at=(id<data.header.vertCount?id:id-data.header.vertCount)*3+1;
            v[at]+=lifts[id];terrainRise=MathF.Max(terrainRise,lifts[id]);moved++;
        }
        up+=terrainRise;
        StageObserver?.Invoke("envelope",data);
        // Preserve Recast's continuous walking envelope; collision treads are not separate navigation surfaces.
        // This must also cover fallback detail: a connected coarse edge cannot
        // carry independent height profiles on its two incident polygons.
        moved+=StitchDetailBoundaries(data);
        moved+=SynchronizeDetailEdges(data);
        StageObserver?.Invoke("weld",data);
        up+=rise;
        down+=drop+clearance;
        float ceiling=data.header.bmax.Y;
        for(int i=1;i<data.verts.Length;i+=3)ceiling=MathF.Max(ceiling,data.verts[i]);
        for(int i=1;i<data.detailVerts.Length;i+=3)ceiling=MathF.Max(ceiling,data.detailVerts[i]);
        data.header.bmax=new(data.header.bmax.X,ceiling,data.header.bmax.Z);
        // Conservative expansion keeps every corrected vertex inside query bounds.
        // The tree layout and escape indexes remain valid; horizontal bounds stay exact.
        if(data.bvTree!=null && moved>0)
        {
            int lower=(int)MathF.Ceiling(down*data.header.bvQuantFactor)+1;
            int upper=(int)MathF.Ceiling(up*data.header.bvQuantFactor)+1;
            for(int i=0;i<Math.Min(data.header.bvNodeCount,data.bvTree.Length);i++)
            {
                var node=data.bvTree[i];
                if(node==null)continue;
                node.bmin=new(node.bmin.X,Math.Max(0,node.bmin.Y-lower),node.bmin.Z);
                node.bmax=new(node.bmax.X,Math.Min(65535,node.bmax.Y+upper),node.bmax.Z);
            }
        }
        return moved;
    }

    readonly record struct DetailFace(int A,int B,int C,int Flags);
    readonly record struct Edge(Vector3 A,Vector3 B);
    static Edge EdgeOf(Vector3 a,Vector3 b)
    {
        bool flip=a.X>b.X||(a.X==b.X&&(a.Y>b.Y||(a.Y==b.Y&&a.Z>b.Z)));
        return flip?new(b,a):new(a,b);
    }
    int RepairBuriedRampDetail(DtMeshData data,ref float up,ref float down)
    {
        if(data.detailMeshes==null||data.detailTris==null||data.detailVerts==null)return 0;
        int count=Math.Min(data.detailMeshes.Length,data.polys.Length),added=0;
        var vertices=new List<Vector3>[count];var faces=new List<DetailFace>[count];var active=new bool[count];
        Vector3 Read(float[] array,int index)=>new(array[index*3+2],array[index*3],array[index*3+1]);
        for(int p=0;p<count;p++)
        {
            var poly=data.polys[p];var detail=data.detailMeshes[p];vertices[p]=new();faces[p]=new();
            for(int i=0;i<poly.vertCount;i++)vertices[p].Add(Read(data.verts,poly.verts[i]));
            for(int i=0;i<detail.vertCount;i++)vertices[p].Add(Read(data.detailVerts,detail.vertBase+i));
            for(int i=0;i<detail.triCount;i++){int at=(detail.triBase+i)*4;faces[p].Add(new(data.detailTris[at],data.detailTris[at+1],data.detailTris[at+2],data.detailTris[at+3]));}
            active[p]=poly.GetPolyType()==0&&poly.GetArea() is NavBake.AreaGround or NavBake.AreaRoad or NavBake.AreaSwimFloor or NavBake.AreaBlocked;
        }
        float maxUp=0,maxDown=0;
        bool Target(Vector3 point,out Vector3 fitted)
        {
            float height=Height(point),delta=height-point.Z;fitted=point;
            if(!OnRamp(point,height))return false;
            if(!float.IsFinite(height)||height<data.header.bmin.Y||height>data.header.bmax.Y||delta<=.04f||delta>rise||delta< -drop)return false;
            fitted.Z=height;maxUp=MathF.Max(maxUp,delta);maxDown=MathF.Max(maxDown,-delta);return true;
        }
        for(int pass=0;pass<1;pass++)
        {
            var mids=new Dictionary<Edge,Vector3>();
            for(int p=0;p<count;p++)if(active[p])foreach(var tri in faces[p])
            {
                ReadOnlySpan<int> ids=[tri.A,tri.B,tri.C];
                for(int edge=0;edge<3;edge++)
                {
                    var key=EdgeOf(vertices[p][ids[edge]],vertices[p][ids[(edge+1)%3]]);
                    if(!mids.ContainsKey(key)&&Target((key.A+key.B)*.5f,out var point))mids[key]=point;
                }
            }
            // A detail index is one byte. If a polygon cannot split an edge,
            // forbid that edge globally so its neighbour cannot create a crack.
            var denied=new HashSet<Edge>();
            for(int p=0;p<count;p++)if(active[p])
            {
                var needed=new HashSet<Edge>();int centers=0,growth=0;
                foreach(var tri in faces[p])
                {
                    ReadOnlySpan<int> ids=[tri.A,tri.B,tri.C];bool split=false;int splitEdges=0;
                    for(int e=0;e<3;e++){var key=EdgeOf(vertices[p][ids[e]],vertices[p][ids[(e+1)%3]]);if(mids.ContainsKey(key)){needed.Add(key);split=true;splitEdges++;}}
                    if(split){centers++;growth+=2+splitEdges;}
                }
                if(vertices[p].Count+needed.Count+centers>255||faces[p].Count+growth>255)denied.UnionWith(needed);
            }
            foreach(var key in denied)mids.Remove(key);
            bool changed=false;
            for(int p=0;p<count;p++)if(active[p])
            {
                var verts=vertices[p];var output=new List<DetailFace>();var localMids=new Dictionary<Edge,int>();
                var reservedEdges=new HashSet<Edge>();int reservedCenters=0,finalTriangles=faces[p].Count;
                foreach(var face in faces[p]){
                    ReadOnlySpan<int> ids=[face.A,face.B,face.C];int splits=0;
                    for(int edge=0;edge<3;edge++){var key=EdgeOf(verts[ids[edge]],verts[ids[(edge+1)%3]]);if(mids.ContainsKey(key)){reservedEdges.Add(key);splits++;}}
                    if(splits>0){reservedCenters++;finalTriangles+=2+splits;}
                }
                int reserve=reservedEdges.Count+reservedCenters;
                foreach(var tri in faces[p])
                {
                    ReadOnlySpan<int> ids=[tri.A,tri.B,tri.C];bool split=false;
                    for(int e=0;e<3;e++)if(mids.ContainsKey(EdgeOf(verts[ids[e]],verts[ids[(e+1)%3]]))){split=true;break;}
                    var center=(verts[tri.A]+verts[tri.B]+verts[tri.C])/3;
                    bool fitCenter=Target(center,out var fitted);
                    if(!fitCenter&&RampInterior(verts[tri.A],verts[tri.B],verts[tri.C],out var rampPoint)){center=rampPoint;fitCenter=Target(center,out fitted);}
                    if(!split&&(!fitCenter||verts.Count+reserve+1>255||finalTriangles+2>255)){output.Add(tri);continue;}
                    var boundary=new List<(int Id,int Flag)>();
                    for(int e=0;e<3;e++)
                    {
                        int flag=(tri.Flags>>(e*2))&3;boundary.Add((ids[e],flag));var key=EdgeOf(verts[ids[e]],verts[ids[(e+1)%3]]);
                        if(!mids.TryGetValue(key,out var mid))continue;
                        if(!localMids.TryGetValue(key,out int id)){id=verts.Count;verts.Add(mid);localMids[key]=id;added++;reserve--;}
                        boundary.Add((id,flag));
                    }
                    int ci=verts.Count;verts.Add(fitCenter?fitted:center);added++;changed=true;if(split)reserve--;else finalTriangles+=2;
                    for(int e=0;e<boundary.Count;e++)output.Add(new(boundary[e].Id,boundary[(e+1)%boundary.Count].Id,ci,boundary[e].Flag));
                }
                faces[p]=output;
            }
            if(!changed)break;
        }
        if(added==0)return 0;
        var detailVerts=new List<float>();var detailTris=new List<int>();
        for(int p=0;p<count;p++)
        {
            int baseVertex=detailVerts.Count/3,baseTriangle=detailTris.Count/4,n=data.polys[p].vertCount;
            for(int i=n;i<vertices[p].Count;i++){var point=vertices[p][i];detailVerts.Add(point.Y);detailVerts.Add(point.Z);detailVerts.Add(point.X);}
            foreach(var tri in faces[p])detailTris.AddRange([tri.A,tri.B,tri.C,tri.Flags]);
            data.detailMeshes[p]=new(baseVertex,baseTriangle,checked((byte)(vertices[p].Count-n)),checked((byte)faces[p].Count));
        }
        data.detailVerts=detailVerts.ToArray();data.detailTris=detailTris.ToArray();
        data.header.detailVertCount=detailVerts.Count/3;data.header.detailTriCount=detailTris.Count/4;
        up+=maxUp;down+=maxDown;return added;
    }

    bool RampInterior(Vector3 a,Vector3 b,Vector3 c,out Vector3 point)
    {
        point=default;float best=clearance*.5f;var nav=new Face(a,b,c);var normal=Vector3.Cross(b-a,c-a);
        if(MathF.Abs(normal.Z)<1e-8f)return false;
        int x0=Bin(MathF.Min(a.X,MathF.Min(b.X,c.X))),x1=Bin(MathF.Max(a.X,MathF.Max(b.X,c.X)));
        int y0=Bin(MathF.Min(a.Y,MathF.Min(b.Y,c.Y))),y1=Bin(MathF.Max(a.Y,MathF.Max(b.Y,c.Y)));
        HashSet<Face>? seen=null;
        for(int x=x0;x<=x1;x++)for(int y=y0;y<=y1;y++)if(rampBins.TryGetValue((x,y),out var ramps))foreach(var ramp in ramps)
        {
            seen??=new();if(!seen.Add(ramp)||!ContinuousRamp(ramp))continue;
            var polygon=new List<Vector3>{ramp.A,ramp.B,ramp.C};ReadOnlySpan<Vector3> corners=[a,b,c];
            for(int edge=0;edge<3&&polygon.Count>=3;edge++)
            {
                var origin=corners[edge];var direction=corners[(edge+1)%3]-origin;var clipped=new List<Vector3>();
                float Side(Vector3 p)=>(direction.X*(p.Y-origin.Y)-direction.Y*(p.X-origin.X))*MathF.Sign(normal.Z);
                for(int i=0;i<polygon.Count;i++){
                    var p=polygon[i];var q=polygon[(i+1)%polygon.Count];float dp=Side(p),dq=Side(q);
                    if(dp>=0)clipped.Add(p);if((dp<0&&dq>0)||(dp>0&&dq<0))clipped.Add(Vector3.Lerp(p,q,dp/(dp-dq)));
                }
                polygon=clipped;
            }
            if(polygon.Count<3)continue;
            var center=Vector3.Zero;foreach(var p in polygon)center+=p;center/=polygon.Count;
            float signedArea=0;for(int i=0;i<polygon.Count;i++){var p=polygon[i]-center;var q=polygon[(i+1)%polygon.Count]-center;signedArea+=p.X*q.Y-q.X*p.Y;}
            if(MathF.Abs(signedArea)<.0001f)continue;
            center.Z=a.Z-(normal.X*(center.X-a.X)+normal.Y*(center.Y-a.Y))/normal.Z;
            float target=Height(center),delta=target-center.Z;
            if(!float.IsFinite(target)||delta<=best||delta>rise||delta< -drop)continue;
            point=center;best=delta;
        }
        return best>clearance*.5f;
    }

    bool OnRamp(Vector3 point,float target)
    {
        if(!rampBins.TryGetValue((Bin(point.X),Bin(point.Y)),out var faces))return false;
        foreach(var f in faces){
            if(!ContinuousRamp(f)||Blocked(f,point.X,point.Y))continue;
            float d=(f.B.Y-f.C.Y)*(f.A.X-f.C.X)+(f.C.X-f.B.X)*(f.A.Y-f.C.Y);
            if(MathF.Abs(d)<1e-8f)continue;
            float u=((f.B.Y-f.C.Y)*(point.X-f.C.X)+(f.C.X-f.B.X)*(point.Y-f.C.Y))/d;
            float v=((f.C.Y-f.A.Y)*(point.X-f.C.X)+(f.A.X-f.C.X)*(point.Y-f.C.Y))/d;
            if(u>=0&&v>=0&&u+v<=1&&MathF.Abs(u*f.A.Z+v*f.B.Z+(1-u-v)*f.C.Z+clearance-target)<=clearance*2.01f)return true;
        }
        return false;
    }

    bool ContinuousRamp(Face face)
    {
        if(face.Kind!=Extractor.Soup.KindWmo)return false;
        if(MathF.Max(face.A.Z,MathF.Max(face.B.Z,face.C.Z))-MathF.Min(face.A.Z,MathF.Min(face.B.Z,face.C.Z))<=clearance*2.01f)return false;
        var center=(face.A+face.B+face.C)/3;center.Z+=clearance;
        var support=Sample(center);
        // A smooth collision ramp and its overlaid flat treads are separate
        // source patches, but describe the same walkable staircase. Requiring
        // identical patch IDs prevented the buried-tread repair from running.
        return support.Layer>0&&float.IsFinite(support.Height)&&MathF.Abs(support.Height-center.Z)<=clearance*2.01f;
    }

    bool SameFloor(Face nav,Face support)
    {
        if(support.Kind!=Extractor.Soup.KindWmo)return false;
        int layer=Root(wmoFaces[support])+1;
        bool Same(Vector3 point){
            var sample=Sample(point);if(sample.Layer==layer)return true;
            if(sample.Layer<=0||!rampBins.TryGetValue((Bin(point.X),Bin(point.Y)),out var nearby)||!nearby.Contains(support))return false;
            var n=Vector3.Cross(support.B-support.A,support.C-support.A);
            float plane=support.A.Z-(n.X*(point.X-support.A.X)+n.Y*(point.Y-support.A.Y))/n.Z+clearance;
            return float.IsFinite(sample.Height)&&MathF.Abs(sample.Height-plane)<=clearance*2.01f;
        }
        return Same(nav.A)&&Same(nav.B)&&Same(nav.C);
    }

    static float MaximumDeficit(Face nav,Face terrain,Func<float,float,bool>? excluded=null)
    {
        float maximum=float.NegativeInfinity;
        static bool Contains(Face f,float x,float y)
        {
            float d=(f.B.Y-f.C.Y)*(f.A.X-f.C.X)+(f.C.X-f.B.X)*(f.A.Y-f.C.Y);
            if(MathF.Abs(d)<1e-9f)return false;
            float a=((f.B.Y-f.C.Y)*(x-f.C.X)+(f.C.X-f.B.X)*(y-f.C.Y))/d;
            float b=((f.C.Y-f.A.Y)*(x-f.C.X)+(f.A.X-f.C.X)*(y-f.C.Y))/d;
            return a>=-1e-5f&&b>=-1e-5f&&a+b<=1.00001f;
        }
        static float Height(Face f,float x,float y)
        {
            var n=Vector3.Cross(f.B-f.A,f.C-f.A);
            return MathF.Abs(n.Z)<1e-9f?float.NaN:f.A.Z-(n.X*(x-f.A.X)+n.Y*(y-f.A.Y))/n.Z;
        }
        void Compare(float x,float y){if(excluded?.Invoke(x,y)!=true)maximum=MathF.Max(maximum,Height(terrain,x,y)-Height(nav,x,y));}
        ReadOnlySpan<Vector3> a=[nav.A,nav.B,nav.C],b=[terrain.A,terrain.B,terrain.C];
        foreach(var v in a)if(Contains(terrain,v.X,v.Y))Compare(v.X,v.Y);
        foreach(var v in b)if(Contains(nav,v.X,v.Y))Compare(v.X,v.Y);
        for(int i=0;i<3;i++)for(int j=0;j<3;j++)
        {
            var p=a[i];var r=a[(i+1)%3]-p;var q=b[j];var s=b[(j+1)%3]-q;
            float den=r.X*s.Y-r.Y*s.X;if(MathF.Abs(den)<1e-9f)continue;
            float u=((q.X-p.X)*s.Y-(q.Y-p.Y)*s.X)/den,v=((q.X-p.X)*r.Y-(q.Y-p.Y)*r.X)/den;
            if(u>=0&&u<=1&&v>=0&&v<=1)Compare(p.X+u*r.X,p.Y+u*r.Y);
        }
        return float.IsFinite(maximum)?maximum:0;
    }
}
