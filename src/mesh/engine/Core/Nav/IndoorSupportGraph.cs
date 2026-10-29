using DotRecast.Recast;
namespace MapExtract;
// Classify complete connected walking surfaces, not source triangulation
// fragments. A platform reached by legal treads stays with its surrounding floor.
internal static class IndoorSupportGraph
{
    internal static int RemoveRaisedIslands(RcCompactHeightfield field,bool[] indoor,NavBake.Settings settings,SourceSurfaceFit? source)
    {
        if(source==null)return 0;
        float step=settings.IndoorStepCut>0?settings.IndoorStepCut:MathF.Min(settings.AgentMaxClimb,2*field.ch+.0001f);
        float raised=step+field.ch;
        var seen=new bool[field.spanCount];var columns=new int[field.spanCount];
        for(int z=0;z<field.height;z++)for(int x=0;x<field.width;x++){var c=field.cells[x+z*field.width];for(int i=c.index;i<c.index+c.count;i++)columns[i]=x+z*field.width;}
        int removed=0;int[] dx=[-1,0,1,0],dz=[0,1,0,-1];
        for(int seed=0;seed<seen.Length;seed++){
            if(seen[seed]||field.areas[seed]!=NavBake.AreaGround)continue;
            int seedColumn=columns[seed];
            if(!indoor[seed]&&!source.HasWmoSupport(field.bmin.Z+(seedColumn/field.width+.5f)*field.cs,field.bmin.X+(seedColumn%field.width+.5f)*field.cs,field.bmin.Y+field.spans[seed].y*field.ch,field.ch*1.15f))continue;
            var members=new List<int>();var queue=new Queue<int>();queue.Enqueue(seed);seen[seed]=true;
            float low=float.PositiveInfinity,high=float.NegativeInfinity;bool allInside=true;
            while(queue.TryDequeue(out int at)){
                members.Add(at);float height=field.bmin.Y+field.spans[at].y*field.ch;low=MathF.Min(low,height);high=MathF.Max(high,height);allInside&=indoor[at];
                int x=columns[at]%field.width,z=columns[at]/field.width;
                for(int d=0;d<4;d++){int con=(field.spans[at].con>>(d*6))&63;if(con==63)continue;int nx=x+dx[d],nz=z+dz[d];if(nx<0||nz<0||nx>=field.width||nz>=field.height)continue;
                    int next=field.cells[nx+nz*field.width].index+con;if(seen[next]||field.areas[next]!=NavBake.AreaGround)continue;seen[next]=true;queue.Enqueue(next);
                }
            }
            if(high-low>raised)continue;
            if(!allInside){
                int wmo=0;
                foreach(int at in members){int c=columns[at];if(source.HasWmoSupport(field.bmin.Z+(c/field.width+.5f)*field.cs,field.bmin.X+(c%field.width+.5f)*field.cs,field.bmin.Y+field.spans[at].y*field.ch,field.ch*1.15f))wmo++;}
                if(wmo*4<members.Count*3)continue;
            }
            int supported=0;
            foreach(int at in members){int c=columns[at];float x=field.bmin.Z+(c/field.width+.5f)*field.cs,y=field.bmin.X+(c%field.width+.5f)*field.cs,height=field.bmin.Y+field.spans[at].y*field.ch;
                if(source.HasLowerFloor(x,y,height,raised,settings.AgentHeight+field.ch))supported++;
            }
            // Integrated WMO furniture often replaces the floor beneath it.
            // Also inspect its perimeter; absence of an underlying face is not
            // evidence that an isolated tabletop is a walking floor.
            if(supported*4<members.Count*3){
                int perimeterSupport=0,perimeter=0;float reach=2*settings.AgentRadius+field.cs;
                var memberSet=members.ToHashSet();
                foreach(int at in members){
                    int c=columns[at],cx=c%field.width,cz=c/field.width;bool boundary=false;
                    for(int d=0;d<4;d++){
                        int con=(field.spans[at].con>>(d*6))&63,nx=cx+dx[d],nz=cz+dz[d];
                        if(con==63||nx<0||nz<0||nx>=field.width||nz>=field.height||!memberSet.Contains(field.cells[nx+nz*field.width].index+con)){boundary=true;break;}
                    }
                    if(!boundary)continue;perimeter++;
                    float x=field.bmin.Z+(cz+.5f)*field.cs,y=field.bmin.X+(cx+.5f)*field.cs,h=field.bmin.Y+field.spans[at].y*field.ch;
                    bool lower=false;
                    foreach(var offset in new[]{(reach,0f),(-reach,0f),(0f,reach),(0f,-reach)})
                        lower|=source.HasLowerFloor(x+offset.Item1,y+offset.Item2,h,raised,settings.AgentHeight+field.ch);
                    if(lower)perimeterSupport++;
                }
                // A pair of lower stair treads beside a landing is not furniture
                // evidence for the entire landing. Require surrounding floor
                // along most of the actual component boundary.
                if(perimeter==0||perimeterSupport*4<perimeter*3)continue;
            }
            foreach(int at in members){int c=columns[at];float x=field.bmin.Z+(c/field.width+.5f)*field.cs,y=field.bmin.X+(c%field.width+.5f)*field.cs,height=field.bmin.Y+field.spans[at].y*field.ch;
                source.BlockFloor(x,y,height,field.cs,field.ch);field.areas[at]=NavBake.AreaNull;removed++;
            }
        }
        return removed;
    }
}
