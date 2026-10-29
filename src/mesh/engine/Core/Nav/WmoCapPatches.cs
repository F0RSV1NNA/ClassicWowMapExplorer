using System.Numerics;
namespace MapExtract;

// Classify complete coplanar collision patches, before voxel merging loses their
// identity. A narrow raised shelf is not a floor; a landing joined to a real ramp is.
internal static class WmoCapPatches
{
    readonly record struct Face(int Triangle,Vector3 A,Vector3 B,Vector3 C,Vector3 Normal,bool Wmo);
    internal static HashSet<int> Detect(Extractor.Soup soup,NavBake.Settings settings,bool localObstaclesOnly=false)
    {
        var faces=new List<Face>();var parents=new List<int>();
        var copies=new Dictionary<int,List<int>>();
        var unique=new Dictionary<(Vector3,Vector3,Vector3),int>();
        var edges=new Dictionary<(Vector3,Vector3),List<int>>();
        for(int t=0;t<soup.TriCount;t++){
            if(t>=soup.TriKind.Count||soup.TriKind[t]==Extractor.Soup.KindDoodad)continue;
            bool wmo=soup.TriKind[t]==Extractor.Soup.KindWmo;
            var a=soup.Verts[soup.Indices[t*3]];var b=soup.Verts[soup.Indices[t*3+1]];var c=soup.Verts[soup.Indices[t*3+2]];
            var n=Vector3.Cross(b-a,c-a);if(n.LengthSquared()<1e-10f)continue;n=Vector3.Normalize(n);
            if(n.Z<MathF.Cos(settings.AgentMaxSlope*MathF.PI/180))continue;
            // Neighbour ADTs can carry the same WMO placement. Count a physical
            // face once when measuring patch boundaries, but reject every copy.
            var ka=a;var kb=b;var kc=c;
            if(After(ka,kb))(ka,kb)=(kb,ka);
            if(After(kb,kc))(kb,kc)=(kc,kb);
            if(After(ka,kb))(ka,kb)=(kb,ka);
            var key=(ka,kb,kc);
            if(wmo&&unique.TryGetValue(key,out int existing)){
                if(!copies.TryGetValue(existing,out var aliases))copies[existing]=aliases=new(){faces[existing].Triangle};
                aliases.Add(t);continue;
            }
            int id=faces.Count;faces.Add(new(t,a,b,c,n,wmo));parents.Add(id);
            if(wmo)unique[key]=id;
            if(wmo){Add(a,b,id);Add(b,c,id);Add(c,a,id);}
        }
        int Root(int i){while(parents[i]!=i){parents[i]=parents[parents[i]];i=parents[i];}return i;}
        void Add(Vector3 a,Vector3 b,int id){bool flip=a.X>b.X||(a.X==b.X&&(a.Y>b.Y||(a.Y==b.Y&&a.Z>b.Z)));var key=flip?(b,a):(a,b);if(!edges.TryGetValue(key,out var list))edges[key]=list=new();list.Add(id);}
        foreach(var ids in edges.Values)for(int a=0;a<ids.Count;a++)for(int b=a+1;b<ids.Count;b++){
            var p=faces[ids[a]];var q=faces[ids[b]];
            if(Vector3.Dot(p.Normal,q.Normal)>.9999f)parents[Root(ids[a])]=Root(ids[b]);
        }
        var rampJoins=new Dictionary<int,HashSet<int>>();var perimeter=new Dictionary<int,float>();
        var patchBoundaries=new Dictionary<int,List<(Vector3 A,Vector3 B)>>();
        foreach(var (edge,ids) in edges){
            var groups=ids.Where(i=>faces[i].Wmo).GroupBy(Root);
            var ramps=ids.Where(i=>faces[i].Normal.Z<.98f&&HeightSpan(faces[i])>settings.CellHeight*2.01f&&RampRun(faces[i])>=settings.AgentRadius*2).ToArray();
            foreach(var group in groups){if(ramps.Length>0&&group.Any(i=>faces[i].Normal.Z>=.98f)){
                if(!rampJoins.TryGetValue(group.Key,out var joins))rampJoins[group.Key]=joins=new();joins.UnionWith(ramps);
            }
            bool positive=false,negative=false;
            foreach(int i in group){var f=faces[i];var center=(f.A+f.B+f.C)/3;
                double side=((double)edge.Item2.X-edge.Item1.X)*(center.Y-edge.Item1.Y)-((double)edge.Item2.Y-edge.Item1.Y)*(center.X-edge.Item1.X);
                positive|=side>0;negative|=side<0;
            }
            if(!(positive&&negative)){
                perimeter[group.Key]=perimeter.GetValueOrDefault(group.Key)+Vector2.Distance(new(edge.Item1.X,edge.Item1.Y),new(edge.Item2.X,edge.Item2.Y));
                if(!patchBoundaries.TryGetValue(group.Key,out var boundaryEdges))patchBoundaries[group.Key]=boundaryEdges=new();boundaryEdges.Add(edge);
            }}
        }
        var bins=new Dictionary<(int,int),List<int>>();const float binSize=4;
        for(int i=0;i<faces.Count;i++){var f=faces[i];
            for(int x=(int)MathF.Floor(MathF.Min(f.A.X,MathF.Min(f.B.X,f.C.X))/binSize);x<=(int)MathF.Floor(MathF.Max(f.A.X,MathF.Max(f.B.X,f.C.X))/binSize);x++)
            for(int y=(int)MathF.Floor(MathF.Min(f.A.Y,MathF.Min(f.B.Y,f.C.Y))/binSize);y<=(int)MathF.Floor(MathF.Max(f.A.Y,MathF.Max(f.B.Y,f.C.Y))/binSize);y++){
                if(!bins.TryGetValue((x,y),out var list))bins[(x,y)]=list=new();list.Add(i);
            }
        }
        var undersides=new Dictionary<(int,int),List<Face>>();
        var walls=new Dictionary<(int,int),List<(Vector3 A,Vector3 B,Vector3 C,Vector2 Normal)>>();
        for(int t=0;t<soup.TriCount;t++)if(t<soup.TriKind.Count&&soup.TriKind[t]==Extractor.Soup.KindWmo){
            var a=soup.Verts[soup.Indices[t*3]];var b=soup.Verts[soup.Indices[t*3+1]];var c=soup.Verts[soup.Indices[t*3+2]];
            var n=Vector3.Cross(b-a,c-a);if(n.LengthSquared()<1e-8f)continue;
            if(n.Z < -MathF.Cos(settings.AgentMaxSlope*MathF.PI/180)*n.Length()){
                var underside=new Face(t,a,b,c,Vector3.Normalize(n),true);
                for(int x=(int)MathF.Floor(MathF.Min(a.X,MathF.Min(b.X,c.X))/binSize);x<=(int)MathF.Floor(MathF.Max(a.X,MathF.Max(b.X,c.X))/binSize);x++)
                for(int y=(int)MathF.Floor(MathF.Min(a.Y,MathF.Min(b.Y,c.Y))/binSize);y<=(int)MathF.Floor(MathF.Max(a.Y,MathF.Max(b.Y,c.Y))/binSize);y++){
                    if(!undersides.TryGetValue((x,y),out var list))undersides[(x,y)]=list=new();list.Add(underside);
                }
            }
            if(MathF.Abs(n.Z)>n.Length()*MathF.Cos(settings.AgentMaxSlope*MathF.PI/180))continue;
            var wall=(a,b,c,Vector2.Normalize(new(n.X,n.Y)));
            for(int x=(int)MathF.Floor(MathF.Min(a.X,MathF.Min(b.X,c.X))/binSize);x<=(int)MathF.Floor(MathF.Max(a.X,MathF.Max(b.X,c.X))/binSize);x++)
            for(int y=(int)MathF.Floor(MathF.Min(a.Y,MathF.Min(b.Y,c.Y))/binSize);y<=(int)MathF.Floor(MathF.Max(a.Y,MathF.Max(b.Y,c.Y))/binSize);y++){
                if(!walls.TryGetValue((x,y),out var list))walls[(x,y)]=list=new();list.Add(wall);
            }
        }
        var rejected=new HashSet<int>();var furniture=new HashSet<int>();float probe=settings.AgentRadius*2+settings.CellSize;
        // Architectural bases can contain a bevel, a shelf and then the wall.
        // Looking only one body width away mistakes the first bevel for an
        // unbacked ramp. This is evidence of a wall, not an erosion distance.
        float wallProbe=settings.AgentHeight+settings.AgentRadius+settings.CellSize;
        bool BackedByWall(int[] group,Vector3 center)
        {
            for(int x=(int)MathF.Floor((center.X-wallProbe)/binSize);x<=(int)MathF.Floor((center.X+wallProbe)/binSize);x++)
            for(int y=(int)MathF.Floor((center.Y-wallProbe)/binSize);y<=(int)MathF.Floor((center.Y+wallProbe)/binSize);y++)
            if(walls.TryGetValue((x,y),out var nearby))foreach(var wall in nearby){
                // A moulding's backing wall often starts at the TOP of its
                // bevel, not at the bevel centroid. Test the standing body band.
                // Collision walls can be tessellated into short vertical bands;
                // one triangle need not span an entire character's height.
                if(MathF.Max(wall.A.Z,MathF.Max(wall.B.Z,wall.C.Z))<center.Z+settings.AgentMaxClimb+settings.CellHeight||MathF.Min(wall.A.Z,MathF.Min(wall.B.Z,wall.C.Z))>center.Z+settings.AgentHeight-.05f)continue;
                var n=wall.Normal;float offset=Vector2.Dot(new(center.X-wall.A.X,center.Y-wall.A.Y),n);if(MathF.Abs(offset)>wallProbe)continue;
                var tangent=new Vector2(-n.Y,n.X);float at=Vector2.Dot(new(center.X,center.Y),tangent);
                var projections=new[]{wall.A,wall.B,wall.C}.Select(p=>Vector2.Dot(new(p.X,p.Y),tangent));if(at<projections.Min()||at>projections.Max())continue;
                // Incline width is checked in its own gradient frame below.
                // A differently angled wall must not invalidate that check.
                // Flat landings still need the wall-relative width guard.
                if(faces[group[0]].Normal.Z<.98f)return true;
                var patch=group.SelectMany(i=>new[]{faces[i].A,faces[i].B,faces[i].C}).Select(p=>Vector2.Dot(new(p.X,p.Y),n));
                if(patch.Max()-patch.Min()<settings.AgentRadius*2)return true;
            }return false;
        }
        var patches=Enumerable.Range(0,faces.Count).Where(i=>faces[i].Wmo).ToLookup(Root);
        var standingRoom=new Dictionary<int,bool>();
        bool StandingRoom(int root){if(!standingRoom.TryGetValue(root,out bool value))standingRoom[root]=value=HasStandingRoom(patches[root].ToArray(),root);return value;}
        double Area(int i){var f=faces[i];return Math.Abs(((double)f.B.X-f.A.X)*(f.C.Y-f.A.Y)-((double)f.B.Y-f.A.Y)*(f.C.X-f.A.X))*.5;}
        bool Majority(IEnumerable<int> group,Func<Face,bool> test){
            double total=0,yes=0;foreach(int i in group){double a=Area(i);total+=a;if(test(faces[i]))yes+=a;}
            return total>0&&yes>total*.5;
        }
        var floorFootprints=new Dictionary<int,bool>();
        foreach(var group in patches){
            void Reject(){foreach(int i in group){if(copies.TryGetValue(i,out var aliases))rejected.UnionWith(aliases);else rejected.Add(faces[i].Triangle);}}
            void Trace(string reason){
                if(Environment.GetEnvironmentVariable("EXPLORER_CAP_TRACE")!="1")return;
                var points=group.SelectMany(i=>new[]{faces[i].A,faces[i].B,faces[i].C}).ToArray();
                if(points.Min(v=>v.Z)>120||points.Max(v=>v.Z)<80)return;
                Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{capGroup=group.Key,reason,standing=StandingRoom(group.Key),joins=rampJoins.GetValueOrDefault(group.Key)?.Select(i=>new{triangle=faces[i].Triangle,room=StandingRoom(Root(i)),over=LowerBroadRamp(faces[i])}),triangles=group.SelectMany(i=>copies.TryGetValue(i,out var aliases)?(IEnumerable<int>)aliases:new[]{faces[i].Triangle}),min=new[]{points.Min(v=>v.X),points.Min(v=>v.Y),points.Min(v=>v.Z)},max=new[]{points.Max(v=>v.X),points.Max(v=>v.Y),points.Max(v=>v.Z)}}));
            }
            // A table can be broad enough to stand on. A suspended slab over an
            // actual WMO floor with no character-sized gap is furniture, not a
            // second storey. Require both underside and lower floor evidence;
            // terrain under a building and ordinary solid landings do not qualify.
            if(faces[group.First()].Normal.Z>=.98f&&Majority(group,SuspendedOverFloor)){
                Trace("suspended-furniture");furniture.UnionWith(group);Reject();continue;
            }
            if(rampJoins.TryGetValue(group.Key,out var joins)&&
               (joins.Any(i=>StandingRoom(Root(i)))||
                (joins.Select(Root).Distinct().Count()==1&&joins.Any(i=>!LowerBroadRamp(faces[i]))))){Trace("ramp-join");continue;}
            if(!perimeter.TryGetValue(group.Key,out float boundary)||boundary<=0){Trace("no-perimeter");continue;}
            float area=group.Sum(i=>Vector3.Cross(faces[i].B-faces[i].A,faces[i].C-faces[i].A).Z*.5f);
            var first=faces[group.First()];
            bool narrowIncline=false;
            if(first.Normal.Z<.98f){
                var tangent=Vector2.Normalize(new(-first.Normal.Y,first.Normal.X));
                var across=group.SelectMany(i=>new[]{faces[i].A,faces[i].B,faces[i].C}).Select(p=>Vector2.Dot(new(p.X,p.Y),tangent));
                narrowIncline=across.Max()-across.Min()<settings.AgentRadius*2;
            }
            // Area/perimeter alone accepts long ledges narrower than a body.
            // Use it only as a cheap broad-patch exit, then measure actual width.
            // A closed ring of trim has a large bounding box but nowhere wide
            // enough to stand. Test clearance to its actual boundary, not its box.
            if(!narrowIncline&&(2*area/boundary>=settings.AgentRadius*2||StandingRoom(group.Key))){Trace("broad");continue;}
            if(first.Normal.Z>=.98f&&group.Count(i=>RampUnderTread(faces[i]))*2>group.Count()){Trace("supported-treads");continue;}
            if(first.Normal.Z<.98f){
                // A connected moulding can run under windows and wall piers.
                // Its first triangle may face a window opening, so it cannot
                // stand in for the backing evidence of the complete patch.
                var patchIds=group.ToArray();
                bool backing=patchIds.Any(i=>BackedByWall(patchIds,(faces[i].A+faces[i].B+faces[i].C)/3));
                // A bent/U-shaped railing can have a wide bounding projection
                // while every usable strip remains thin. Broad standing room
                // was already checked above; length alone is not an exemption.
                bool overRamp=patchIds.Any(i=>LowerBroadRamp(faces[i]));
                if(!backing&&!overRamp){Trace("incline");continue;}
            }
            if(group.Count(i=>StepSequence((faces[i].A+faces[i].B+faces[i].C)/3))*2>group.Count()){Trace("steps");continue;}
            // A low open step is part of the walking envelope even when its
            // narrow rim has no full standing disk. Backed wall footings are not.
            var groupIds=group.ToArray();
            if(first.Normal.Z>=.98f&&!groupIds.Any(i=>BackedByWall(groupIds,(faces[i].A+faces[i].B+faces[i].C)/3))
                &&Majority(group,LowOpenStep)){Trace("low-open-step");continue;}
            double supported=0,total=0;
            foreach(int i in group){var f=faces[i];var center=(f.A+f.B+f.C)/3;bool lower=false;total++;
                for(int dir=0;dir<8&&!lower;dir++){
                    float angle=dir*MathF.PI/4,x=center.X+MathF.Cos(angle)*probe,y=center.Y+MathF.Sin(angle)*probe;
                    if(!bins.TryGetValue(((int)MathF.Floor(x/binSize),(int)MathF.Floor(y/binSize)),out var candidates))continue;
                    foreach(int j in candidates){if(Root(j)==group.Key)continue;var q=faces[j];
                        float d=(q.B.Y-q.C.Y)*(q.A.X-q.C.X)+(q.C.X-q.B.X)*(q.A.Y-q.C.Y);if(MathF.Abs(d)<1e-9f)continue;
                        float u=((q.B.Y-q.C.Y)*(x-q.C.X)+(q.C.X-q.B.X)*(y-q.C.Y))/d,v=((q.C.Y-q.A.Y)*(x-q.C.X)+(q.A.X-q.C.X)*(y-q.C.Y))/d;
                        if(u<0||v<0||u+v>1)continue;
                        float top=MathF.Max(center.Z,MathF.Max(f.A.Z,MathF.Max(f.B.Z,f.C.Z)));
                        float drop=top-(u*q.A.Z+v*q.B.Z+(1-u-v)*q.C.Z);
                        // Sub-cell lips still bend contour corners. Taller trim
                        // reached through stacked bevels also needs rejection.
                        if(drop>settings.CellHeight*.5f&&drop<=settings.AgentHeight+settings.CellHeight&&(q.Wmo||BackedByWall(groupIds,center))){lower=true;break;}
                    }
                }
                if(lower)supported++;
            }
            if(supported*2>total){Trace("rejected");foreach(int i in group){
                if(copies.TryGetValue(i,out var aliases))rejected.UnionWith(aliases);else rejected.Add(faces[i].Triangle);
            }}else Trace("no-lower-floor");
        }
        // Rejected tabletop bevels must not survive as ramps around its edge.
        // Follow only connected eligible source faces with suspended-floor
        // evidence; do not flood into the room floor or a genuine landing.
        var pending=new Queue<int>(furniture);
        var adjacent=new Dictionary<int,HashSet<int>>();
        foreach(var ids in edges.Values)foreach(int a in ids)foreach(int b in ids)if(a!=b){
            if(!adjacent.TryGetValue(a,out var list))adjacent[a]=list=new();list.Add(b);
        }
        while(pending.TryDequeue(out int at))if(adjacent.TryGetValue(at,out var neighbours))foreach(int next in neighbours){
            if(furniture.Contains(next)||!(SuspendedOverFloor(faces[next])||(faces[next].Normal.Z<.98f&&OverCloseFloor(faces[next]))))continue;
            furniture.Add(next);pending.Enqueue(next);
            if(copies.TryGetValue(next,out var aliases))rejected.UnionWith(aliases);else rejected.Add(faces[next].Triangle);
        }
        bool OverCloseFloor(Face face){
            var center=(face.A+face.B+face.C)/3;
            if(!bins.TryGetValue(((int)MathF.Floor(center.X/binSize),(int)MathF.Floor(center.Y/binSize)),out var nearby))return false;
            return nearby.Any(i=>{var f=faces[i];float drop=center.Z-At(f,center.X,center.Y);return f.Wmo&&f.Normal.Z>=.98f&&drop>settings.CellHeight*.5f&&drop<settings.AgentHeight;});
        }
        // Coplanar patch width is not floor width: a gently tessellated cave
        // and a low floor border can consist of individually narrow patches.
        // Verify a standing disk against the surrounding exposed source instead.

        bool FloorFootprint(Face face){
            if(floorFootprints.TryGetValue(face.Triangle,out bool value))return value;
            var center=(face.A+face.B+face.C)/3;
            for(int d=0;d<8;d++){
                float angle=d*MathF.PI/4,x=center.X+MathF.Cos(angle)*settings.AgentRadius,y=center.Y+MathF.Sin(angle)*settings.AgentRadius;
                float expected=face.A.Z-(face.Normal.X*(x-face.A.X)+face.Normal.Y*(y-face.A.Y))/face.Normal.Z;
                if(!bins.TryGetValue(((int)MathF.Floor(x/binSize),(int)MathF.Floor(y/binSize)),out var nearby)){floorFootprints[face.Triangle]=false;return false;}
                bool supported=false;
                foreach(int i in nearby){var f=faces[i];if(!f.Wmo||Vector3.Dot(f.Normal,face.Normal)<.95f)continue;
                    float h=At(f,x,y);if(!float.IsFinite(h)||MathF.Abs(h-expected)>settings.CellHeight||!ClearBody(x,y,h))continue;
                    if(nearby.Any(j=>{float above=At(faces[j],x,y)-h;return above>.03f&&above<settings.AgentHeight;}))continue;
                    supported=true;break;
                }
                if(!supported){floorFootprints[face.Triangle]=false;return false;}
            }
            return floorFootprints[face.Triangle]=true;
        }
        bool LowOpenStep(Face face)
        {
            var center=(face.A+face.B+face.C)/3;int open=0;
            float maxStep=MathF.Min(settings.AgentMaxClimb,settings.CellHeight*2.01f);
            for(int dir=0;dir<8;dir++){
                float angle=dir*MathF.PI/4,x=center.X+MathF.Cos(angle)*probe,y=center.Y+MathF.Sin(angle)*probe;
                if(!bins.TryGetValue(((int)MathF.Floor(x/binSize),(int)MathF.Floor(y/binSize)),out var nearby))continue;
                foreach(int i in nearby){var f=faces[i];if(!f.Wmo||f.Normal.Z<.98f||!(StandingRoom(Root(i))||FloorFootprint(f)))continue;
                    float h=At(f,x,y),drop=center.Z-h;
                    if(MathF.Abs(drop)<=maxStep&&ClearStepApproach(center,x,y,h)){open|=1<<dir;break;}
                }
            }
            return (open&(open>>4))!=0;
        }
        static float At(Face f,float x,float y){
            double d=((double)f.B.Y-f.C.Y)*(f.A.X-f.C.X)+((double)f.C.X-f.B.X)*(f.A.Y-f.C.Y);if(Math.Abs(d)<1e-12)return float.NaN;
            double u=(((double)f.B.Y-f.C.Y)*(x-f.C.X)+((double)f.C.X-f.B.X)*(y-f.C.Y))/d;
            double v=(((double)f.C.Y-f.A.Y)*(x-f.C.X)+((double)f.A.X-f.C.X)*(y-f.C.Y))/d;
            return u>=0&&v>=0&&u+v<=1?(float)(u*f.A.Z+v*f.B.Z+(1-u-v)*f.C.Z):float.NaN;
        }
        bool SuspendedOverFloor(Face face)
        {
            var p=(face.A+face.B+face.C)/3;var key=((int)MathF.Floor(p.X/binSize),(int)MathF.Floor(p.Y/binSize));
            if(!undersides.TryGetValue(key,out var below)||!bins.TryGetValue(key,out var floor))return false;
            float At(Face f){
                double d=((double)f.B.Y-f.C.Y)*(f.A.X-f.C.X)+((double)f.C.X-f.B.X)*(f.A.Y-f.C.Y);if(Math.Abs(d)<1e-12)return float.NaN;
                double u=(((double)f.B.Y-f.C.Y)*(p.X-f.C.X)+((double)f.C.X-f.B.X)*(p.Y-f.C.Y))/d;
                double v=(((double)f.C.Y-f.A.Y)*(p.X-f.C.X)+((double)f.A.X-f.C.X)*(p.Y-f.C.Y))/d;
                return u>=0&&v>=0&&u+v<=1?(float)(u*f.A.Z+v*f.B.Z+(1-u-v)*f.C.Z):float.NaN;
            }
            foreach(var bottom in below){float h=At(bottom),thickness=p.Z-h;
                if(!(thickness>.03f&&thickness<=settings.AgentMaxClimb))continue;
                foreach(int i in floor){var f=faces[i];if(!f.Wmo||f.Normal.Z<.98f)continue;
                    float lower=At(f),gap=h-lower;
                    if(gap>settings.CellHeight*.5f&&p.Z-lower<settings.AgentHeight)return true;
                }
            }
            return false;
        }
        bool LowerBroadRamp(Face face)
        {
            var center=(face.A+face.B+face.C)/3;
            if(!bins.TryGetValue(((int)MathF.Floor(center.X/binSize),(int)MathF.Floor(center.Y/binSize)),out var nearby))return false;
            foreach(int i in nearby){var q=faces[i];
                if(!q.Wmo||q.Normal.Z>=.98f||RampRun(q)<settings.AgentRadius*2||RampWidth(q)<settings.AgentRadius*2)continue;
                float d=(q.B.Y-q.C.Y)*(q.A.X-q.C.X)+(q.C.X-q.B.X)*(q.A.Y-q.C.Y);if(MathF.Abs(d)<1e-9f)continue;
                float u=((q.B.Y-q.C.Y)*(center.X-q.C.X)+(q.C.X-q.B.X)*(center.Y-q.C.Y))/d;
                float v=((q.C.Y-q.A.Y)*(center.X-q.C.X)+(q.A.X-q.C.X)*(center.Y-q.C.Y))/d;
                if(u<0||v<0||u+v>1)continue;
                float drop=center.Z-(u*q.A.Z+v*q.B.Z+(1-u-v)*q.C.Z);
                if(drop>settings.CellHeight*.5f&&drop<=settings.AgentHeight+settings.CellHeight)return true;
            }
            return false;
        }
        bool HasStandingRoom(int[] group,int root)
        {
            if(!patchBoundaries.TryGetValue(root,out var boundary))return false;
            var vertices=group.SelectMany(i=>new[]{faces[i].A,faces[i].B,faces[i].C}).Distinct().ToArray();
            var candidates=group.Select(i=>(faces[i].A+faces[i].B+faces[i].C)/3).Prepend(vertices.Aggregate(Vector3.Zero,(sum,v)=>sum+v/vertices.Length));
            foreach(var point in candidates){
                bool inside=group.Any(i=>{
                    var f=faces[i];float d=(f.B.Y-f.C.Y)*(f.A.X-f.C.X)+(f.C.X-f.B.X)*(f.A.Y-f.C.Y);if(MathF.Abs(d)<1e-9f)return false;
                    float u=((f.B.Y-f.C.Y)*(point.X-f.C.X)+(f.C.X-f.B.X)*(point.Y-f.C.Y))/d,v=((f.C.Y-f.A.Y)*(point.X-f.C.X)+(f.A.X-f.C.X)*(point.Y-f.C.Y))/d;
                    return u>=0&&v>=0&&u+v<=1;
                });
                if(!inside)continue;bool clear=true;
                foreach(var (a,b) in boundary){
                    double dx=b.X-a.X,dy=b.Y-a.Y,len=dx*dx+dy*dy;
                    double t=len>0?Math.Clamp((((double)point.X-a.X)*dx+((double)point.Y-a.Y)*dy)/len,0,1):0;
                    if(Math.Pow(point.X-a.X-t*dx,2)+Math.Pow(point.Y-a.Y-t*dy,2)<settings.AgentRadius*settings.AgentRadius-.00001){clear=false;break;}
                }
                if(clear)return true;
            }
            return false;
        }
        bool StepSequence(Vector3 center)
        {
            for(int dir=0;dir<8;dir++){
                float previous=center.Z;int up=0,down=0;
                for(int step=1;step<=MathF.Ceiling(probe*2/settings.CellSize);step++){
                    float angle=dir*MathF.PI/4,x=center.X+MathF.Cos(angle)*step*settings.CellSize,y=center.Y+MathF.Sin(angle)*step*settings.CellSize;
                    if(!bins.TryGetValue(((int)MathF.Floor(x/binSize),(int)MathF.Floor(y/binSize)),out var candidates))break;
                    var levels=new List<float>();
                    foreach(int j in candidates){var q=faces[j];if(q.Normal.Z<.98f)continue;
                        float d=(q.B.Y-q.C.Y)*(q.A.X-q.C.X)+(q.C.X-q.B.X)*(q.A.Y-q.C.Y);if(MathF.Abs(d)<1e-9f)continue;
                        float u=((q.B.Y-q.C.Y)*(x-q.C.X)+(q.C.X-q.B.X)*(y-q.C.Y))/d,v=((q.C.Y-q.A.Y)*(x-q.C.X)+(q.A.X-q.C.X)*(y-q.C.Y))/d;
                        if(u>=0&&v>=0&&u+v<=1)levels.Add(u*q.A.Z+v*q.B.Z+(1-u-v)*q.C.Z);
                    }
                    float next=float.NaN,gap=float.MaxValue;
                    foreach(float level in levels){float delta=MathF.Abs(level-previous);
                        if(delta>MathF.Min(settings.AgentMaxClimb+settings.CellHeight,MathF.Max(.5f,settings.CellHeight*2.01f))||delta>=gap||levels.Any(h=>h-level>.03f&&h-level<settings.AgentHeight))continue;
                        next=level;gap=delta;
                    }
                    if(!float.IsFinite(next)||!ClearBody(x,y,next))break;
                    // Oscillation across floor/trim/floor is not a staircase.
                    if(next>previous+settings.CellHeight*.5f){down=0;if(++up>=2)return true;}
                    else if(next<previous-settings.CellHeight*.5f){up=0;if(++down>=2)return true;}
                    previous=next;
                }
            }
            return false;
        }
        bool RampUnderTread(Face tread)
        {
            var center=(tread.A+tread.B+tread.C)/3;
            if(!bins.TryGetValue(((int)MathF.Floor(center.X/binSize),(int)MathF.Floor(center.Y/binSize)),out var candidates))return false;
            foreach(int i in candidates){var ramp=faces[i];
                if(!ramp.Wmo||ramp.Normal.Z>=.98f||HeightSpan(ramp)<=settings.CellHeight*2.01f||RampRun(ramp)<settings.AgentRadius*2||RampWidth(ramp)<settings.AgentRadius*2||!StandingRoom(Root(i)))continue;
                float det=(ramp.B.Y-ramp.C.Y)*(ramp.A.X-ramp.C.X)+(ramp.C.X-ramp.B.X)*(ramp.A.Y-ramp.C.Y);
                if(MathF.Abs(det)<1e-9f)continue;
                float u=((ramp.B.Y-ramp.C.Y)*(center.X-ramp.C.X)+(ramp.C.X-ramp.B.X)*(center.Y-ramp.C.Y))/det;
                float v=((ramp.C.Y-ramp.A.Y)*(center.X-ramp.C.X)+(ramp.A.X-ramp.C.X)*(center.Y-ramp.C.Y))/det;
                if(u<0||v<0||u+v>1)continue;
                float gap=center.Z-(u*ramp.A.Z+v*ramp.B.Z+(1-u-v)*ramp.C.Z);
                // Smooth collision ramps often lie slightly ABOVE the visible
                // flat treads. Both representations describe the same stairs.
                if(MathF.Abs(gap)<=settings.CellHeight*2.01f)return true;
            }
            return false;
        }
        bool ClearBody(float x,float y,float floor)
        {
            if(!walls.TryGetValue(((int)MathF.Floor(x/binSize),(int)MathF.Floor(y/binSize)),out var nearby))return true;
            foreach(var wall in nearby){
                var polygon=new List<Vector3>{wall.A,wall.B,wall.C};
                foreach(var (height,sign) in new[]{(floor+MathF.Min(settings.AgentMaxClimb,settings.AgentRadius+settings.CellHeight*.5f),1f),(floor+settings.AgentHeight-.05f,-1f)}){
                    var clipped=new List<Vector3>();
                    for(int i=0;i<polygon.Count;i++){
                        var a=polygon[i];var b=polygon[(i+1)%polygon.Count];float da=(a.Z-height)*sign,db=(b.Z-height)*sign;
                        if(da>=0)clipped.Add(a);if((da>=0)!=(db>=0))clipped.Add(a+(b-a)*(da/(da-db)));
                    }
                    polygon=clipped;
                }
                for(int i=0;i<polygon.Count;i++){
                    var a=polygon[i];var b=polygon[(i+1)%polygon.Count];double dx=b.X-a.X,dy=b.Y-a.Y,length=dx*dx+dy*dy;
                    double t=length>0?Math.Clamp((((double)x-a.X)*dx+((double)y-a.Y)*dy)/length,0,1):0;
                    if(Math.Pow(x-a.X-t*dx,2)+Math.Pow(y-a.Y-t*dy,2)<settings.AgentRadius*settings.AgentRadius)return false;
                }
            }
            return true;
        }
        bool ClearStepApproach(Vector3 center,float x,float y,float floor)
        {
            int steps=Math.Max(1,(int)MathF.Ceiling(probe/settings.CellSize));
            for(int i=0;i<=steps;i++){
                float t=(float)i/steps;
                if(!ClearBody(center.X+(x-center.X)*t,center.Y+(y-center.Y)*t,floor))return false;
            }
            return true;
        }
        bool LowFloorTransition(Face face){
            var center=(face.A+face.B+face.C)/3;float step=MathF.Min(settings.AgentMaxClimb,settings.CellHeight*2.01f);int support=0,lower=0;
            for(int dir=0;dir<8;dir++){
                float angle=dir*MathF.PI/4,x=center.X+MathF.Cos(angle)*probe,y=center.Y+MathF.Sin(angle)*probe;
                if(!bins.TryGetValue(((int)MathF.Floor(x/binSize),(int)MathF.Floor(y/binSize)),out var nearby))continue;
                foreach(int i in nearby){var floor=faces[i];if(!floor.Wmo||floor.Normal.Z<.98f)continue;
                    float h=At(floor,x,y),drop=center.Z-h;if(!float.IsFinite(h)||MathF.Abs(drop)>step||!ClearStepApproach(center,x,y,h))continue;
                    // Require a real exposed broad floor, not another little
                    // cap belonging to the same piece of furniture/window trim.
                    if(nearby.Any(j=>{float above=At(faces[j],x,y)-h;return above>.03f&&above<settings.AgentHeight;}))continue;
                    support|=1<<dir;if(drop>.03f)lower|=1<<dir;break;
                }
            }
            // A floor threshold has floor on both sides. Several probes on
            // one side of a wall/railing do not establish a through passage.
            return (support&(support>>4))!=0&&lower!=0;
        }
        if(localObstaclesOnly){
            var allowed=new HashSet<int>();
            for(int i=0;i<faces.Count;i++){
                var f=faces[i];
                if(!f.Wmo||!rejected.Contains(f.Triangle))continue;
                // A broad coplanar floor must never be rejected wholesale for
                // a narrow branch. Evaluate individual low steps separately.
                if(FloorFootprint(f))continue;
                if(f.Normal.Z>=.98f){if(StandingRoom(Root(i))||LowOpenStep(f))continue;}
                else if(HeightSpan(f)<=2*settings.CellHeight+.0001f&&(FloorFootprint(f)||LowFloorTransition(f)))continue;
                allowed.Add(f.Triangle);
                if(copies.TryGetValue(i,out var aliases))allowed.UnionWith(aliases);
            }
            return allowed;
        }
        return rejected;
    }
    static float RampRun(Face f)
    {
        var gradient=Vector2.Normalize(new(f.Normal.X,f.Normal.Y));
        var projected=new[]{f.A,f.B,f.C}.Select(p=>Vector2.Dot(new(p.X,p.Y),gradient));return projected.Max()-projected.Min();
    }
    static float RampWidth(Face f)
    {
        var tangent=Vector2.Normalize(new(-f.Normal.Y,f.Normal.X));
        var projected=new[]{f.A,f.B,f.C}.Select(p=>Vector2.Dot(new(p.X,p.Y),tangent));return projected.Max()-projected.Min();
    }
    static float HeightSpan(Face f)=>MathF.Max(f.A.Z,MathF.Max(f.B.Z,f.C.Z))-MathF.Min(f.A.Z,MathF.Min(f.B.Z,f.C.Z));
    static bool After(Vector3 a,Vector3 b)=>a.X>b.X||(a.X==b.X&&(a.Y>b.Y||(a.Y==b.Y&&a.Z>b.Z)));
}
