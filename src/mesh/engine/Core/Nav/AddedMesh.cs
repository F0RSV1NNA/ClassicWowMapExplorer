using System.Numerics;
using DotRecast.Recast;

namespace MapExtract;

// Explicit authoring overrides are independent of automatic obstacle policy.
// They reuse upward source collision in the painted height band; they never
// create a horizontal disc in empty space or affect an unpainted storey.
internal sealed class AddedMesh
{
    const float BinSize=4;
    readonly record struct Face(Vector3 A,Vector3 B,Vector3 C);
    readonly Dictionary<(int,int),List<Face>> bins=new();
    readonly RoadMask roads;
    readonly float clearance,gap;
    internal AddedMesh(Extractor.Soup soup,RoadMask roads,NavBake.Settings settings)
    {
        this.roads=roads;clearance=settings.CellHeight;gap=settings.AgentMaxClimb+2*clearance;
        var strokes=roads.AddedMeshStrokes.ToArray();float slope=MathF.Cos(settings.AgentMaxSlope*MathF.PI/180);
        for(int t=0;t<soup.TriCount;t++){
            var a=soup.Verts[soup.Indices[t*3]];var b=soup.Verts[soup.Indices[t*3+1]];var c=soup.Verts[soup.Indices[t*3+2]];
            var n=Vector3.Cross(b-a,c-a);if(n.LengthSquared()<1e-10f||n.Z<n.Length()*slope)continue;
            float x0=MathF.Min(a.X,MathF.Min(b.X,c.X)),x1=MathF.Max(a.X,MathF.Max(b.X,c.X));
            float y0=MathF.Min(a.Y,MathF.Min(b.Y,c.Y)),y1=MathF.Max(a.Y,MathF.Max(b.Y,c.Y));
            float z0=MathF.Min(a.Z,MathF.Min(b.Z,c.Z)),z1=MathF.Max(a.Z,MathF.Max(b.Z,c.Z));
            if(!strokes.Any(s=>s.X+s.Radius>=x0&&s.X-s.Radius<=x1&&s.Y+s.Radius>=y0&&s.Y-s.Radius<=y1&&s.Z+s.HeightBand>=z0&&s.Z-s.HeightBand<=z1))continue;
            for(int x=Bin(x0);x<=Bin(x1);x++)for(int y=Bin(y0);y<=Bin(y1);y++){
                if(!bins.TryGetValue((x,y),out var list))bins[(x,y)]=list=new();list.Add(new(a,b,c));
            }
        }
    }
    static int Bin(float value)=>(int)MathF.Floor(value/BinSize);
    internal bool TryFloor(float x,float y,float expected,out float height,float? maxGap=null)
    {
        height=float.NaN;float best=maxGap??gap;
        if(!bins.TryGetValue((Bin(x),Bin(y)),out var faces))return false;
        foreach(var f in faces){
            double d=((double)f.B.Y-f.C.Y)*(f.A.X-f.C.X)+((double)f.C.X-f.B.X)*(f.A.Y-f.C.Y);if(Math.Abs(d)<1e-12)continue;
            double u=(((double)f.B.Y-f.C.Y)*(x-f.C.X)+((double)f.C.X-f.B.X)*(y-f.C.Y))/d;
            double v=(((double)f.C.Y-f.A.Y)*(x-f.C.X)+((double)f.A.X-f.C.X)*(y-f.C.Y))/d;
            if(u< -1e-6||v< -1e-6||u+v>1.000001)continue;
            float h=(float)(u*f.A.Z+v*f.B.Z+(1-u-v)*f.C.Z),delta=MathF.Abs(h+clearance-expected);
            if(delta>best||roads.StrokeAt(x,y,h)!=MapEdits.StrokeKind.AddMesh)continue;
            height=h;best=delta;
        }
        return float.IsFinite(height);
    }
    internal void RestoreRaster(RcHeightfield solid,int walkableHeight)
    {
        for(int ci=0;ci<solid.spans.Length;ci++){
            float x=solid.bmin.Z+(ci/solid.width+.5f)*solid.cs,y=solid.bmin.X+(ci%solid.width+.5f)*solid.cs;
            if(!bins.ContainsKey((Bin(x),Bin(y))))continue;
            for(var s=solid.spans[ci];s!=null;s=s.next){
                if((s.next?.smin??int.MaxValue)-s.smax<walkableHeight)continue;
                if(TryFloor(x,y,solid.bmin.Y+s.smax*solid.ch,out _,solid.ch*2.01f))s.area=NavBake.AreaGround;
            }
        }
    }
    internal bool[] RestoreCompact(RcCompactHeightfield field)
    {
        var forced=new bool[field.spanCount];
        for(int z=0;z<field.height;z++)for(int x=0;x<field.width;x++){
            float wx=field.bmin.Z+(z+.5f)*field.cs,wy=field.bmin.X+(x+.5f)*field.cs;
            if(!bins.ContainsKey((Bin(wx),Bin(wy))))continue;
            var c=field.cells[x+z*field.width];for(int i=c.index;i<c.index+c.count;i++)
                if(TryFloor(wx,wy,field.bmin.Y+field.spans[i].y*field.ch,out _,field.ch*2.01f)){
                    field.areas[i]=NavBake.AreaGround;forced[i]=true;
                }
        }
        return forced;
    }
    internal static void PreserveSmallRegions(RcCompactHeightfield field,bool[] forced)
    {
        int next=field.maxRegions+1;int[] dx=[-1,0,1,0],dz=[0,1,0,-1];
        for(int z=0;z<field.height;z++)for(int x=0;x<field.width;x++){
            var c=field.cells[x+z*field.width];for(int i=c.index;i<c.index+c.count;i++){
                if(!forced[i]||field.areas[i]==0||field.spans[i].reg!=0)continue;
                if(next>=0x8000)throw new InvalidOperationException("Added mesh exceeds the region identifier limit");
                int reg=next++;var queue=new Queue<(int X,int Z,int Id)>();Assign(i);queue.Enqueue((x,z,i));
                while(queue.Count>0){var p=queue.Dequeue();for(int d=0;d<4;d++){
                    int con=(field.spans[p.Id].con>>(d*6))&0x3f;if(con==0x3f)continue;
                    int nx=p.X+dx[d],nz=p.Z+dz[d];if(nx<0||nz<0||nx>=field.width||nz>=field.height)continue;
                    int id=field.cells[nx+nz*field.width].index+con;
                    if(!forced[id]||field.areas[id]==0||field.spans[id].reg!=0)continue;
                    Assign(id);queue.Enqueue((nx,nz,id));
                }}
                void Assign(int id){var s=RcCompactSpanBuilder.NewBuilder(ref field.spans[id]);s.reg=reg;field.spans[id]=s.Build();}
            }
        }
        if(next>field.maxRegions+1)field.maxRegions=next;
    }
}
