using DotRecast.Recast;

namespace MapExtract;

// Collision provenance is needed here: a narrow raised WMO cap is trim, while
// the same shape in terrain may be the only natural path along a ridge.
internal static class SurfaceClearance
{
    static readonly (int X,int Z)[] ProbeAxes = [(1,0),(0,1),(1,1),(1,-1)];
    internal const int WmoBevelTop = 58;
    internal const int WmoTop = 59; // structural footing outranks bevels, but not rejected caps (60)

    internal static int BlockRaisedCaps(RcHeightfield solid, IReadOnlySet<long> wmo, NavBake.Settings settings,
                                       Action<int,RcSpan>? onBlocked=null, IReadOnlySet<long>? bevels=null, IReadOnlySet<long>? structural=null)
    {
        int reach = Math.Max(1, (int)MathF.Ceiling((settings.AgentRadius * 2 + solid.cs) / solid.cs));
        int backingReach=Math.Max(reach,(int)MathF.Ceiling((settings.AgentHeight+settings.AgentRadius)/solid.cs));
        float tolerance = solid.ch * 1.1f;
        int legacyReach=Math.Max(1,(int)MathF.Ceiling(settings.AgentRadius/solid.cs));
        var blocked = new List<(int Column,RcSpan Span)>();
        foreach (long key in wmo)
        {
            int column=(int)(key>>20), x=column%solid.width, z=column/solid.width;
            for (var span=solid.spans[column]; span!=null; span=span.next)
            {
                if(span.smin!=(int)(key&0xfffff)||span.area!=NavBake.AreaGround)continue;
                float height=span.smax*solid.ch;
                // Across a thin cap, one side is lower floor and the other is
                // lower floor or a solid wall. A stair has a higher walkable
                // continuation instead; it must never be mistaken for trim.
                // Inspect the next tread before a longer probe can see past it
                // into the stairwell wall. These decisions use the original field.
                bool reject=false;
                for(int axis=0;axis<2;axis++)
                {
                    var (dx,dz)=ProbeAxes[axis];
                    int a=LegacySide(x-dx*legacyReach,z-dz*legacyReach,height),b=LegacySide(x+dx*legacyReach,z+dz*legacyReach,height);
                    if((a==1&&(b==1||b==2))||(b==1&&a==2)){reject=true;break;}
                }
                // Source-labelled bevels cannot masquerade as stair support;
                // narrow shelves can now be checked indoors without a roof exception.
                if(!reject)foreach(var (dx,dz) in ProbeAxes)
                {
                    int a=Side(x,z,-dx,-dz,height,reach),b=Side(x,z,dx,dz,height,reach);
                    if((a==1&&(b==1||b==2))||(b==1&&a==2)){reject=true;break;}
                }
                // A layered wall base can be wider than one body diameter.
                // Search farther for its backing wall, but never use this
                // longer window to remove a freestanding raised platform.
                if(!reject)foreach(var (dx,dz) in ProbeAxes){
                    int a=Side(x,z,-dx,-dz,height,backingReach),b=Side(x,z,dx,dz,height,backingReach);
                    if((a==1&&b==2)||(a==2&&b==1)){reject=true;break;}
                }
                if(reject&&((bevels?.Contains(key)??false)||!HasStepContinuation(x,z,height)))blocked.Add((column,span));
            }
        }
        // Evaluate against the original field, never against earlier decisions.
        foreach(var (column,span) in blocked){onBlocked?.Invoke(column,span);span.area=NavBake.AreaNull;}
        return blocked.Count;

        bool HasStepContinuation(int x,int z,float height)
        {
            foreach(var (dx,dz) in ProbeAxes)for(int sign=-1;sign<=1;sign+=2)
            {
                float previous=height;int descents=0,ascents=0;
                for(int step=1;step<=reach*2;step++)
                {
                    int nx=x+dx*step*sign,nz=z+dz*step*sign;
                    if(nx<0||nz<0||nx>=solid.width||nz>=solid.height)break;
                    float best=float.NaN,distance=float.MaxValue;
                    for(var span=solid.spans[nx+nz*solid.width];span!=null;span=span.next)
                    {
                        if(span.area==NavBake.AreaNull||((span.next?.smin??int.MaxValue)-span.smax)*solid.ch<settings.AgentHeight)continue;
                        if(structural!=null&&!structural.Contains(((long)(nx+nz*solid.width)<<20)|(uint)span.smin))continue;
                        float top=span.smax*solid.ch,gap=MathF.Abs(top-previous);
                        if(gap<=Math.Min(settings.AgentMaxClimb+solid.ch,Math.Max(.5f,solid.ch*2.01f))&&gap<distance){best=top;distance=gap;}
                    }
                    if(!float.IsFinite(best))break;
                    if(best>previous+solid.ch*.5f){descents=0;if(++ascents>=2)return true;}
                    if(best<previous-solid.ch*.5f){ascents=0;if(++descents>=2)return true;}
                    previous=best;
                }
            }
            return false;
        }
        int LegacySide(int x,int z,float height)
        {
            if(x<0||z<0||x>=solid.width||z>=solid.height)return 0;
            bool lower=false,wall=false;
            for(var span=solid.spans[x+z*solid.width];span!=null;span=span.next)
            {
                float top=span.smax*solid.ch,bottom=span.smin*solid.ch;
                if(span.area!=NavBake.AreaNull&&((span.next?.smin??int.MaxValue)-span.smax)*solid.ch>=settings.AgentHeight)
                {
                    if(top>=height-tolerance&&top<=height+settings.AgentMaxClimb)return 0;
                    if(height-top>Math.Max(.35f,tolerance)&&height-top<=settings.AgentMaxClimb+solid.ch)lower=true;
                }
                if(bottom<=height+tolerance&&top>=height+settings.AgentHeight)wall=true;
            }
            return wall?2:lower?1:0;
        }
        // 0: traversable continuation or no evidence; 1: lower floor; 2: wall.
        int Side(int x,int z,int dx,int dz,float height,int distance)
        {
            // Follow a same-height shelf out to its actual edge. A one-radius
            // sample can land on the same bevel and miss the lower floor/wall.
            int steps=Math.Max(1,(int)MathF.Floor(distance/MathF.Sqrt(dx*dx+dz*dz)));
            for(int step=1;step<=steps;step++)
            {
                int nx=x+dx*step,nz=z+dz*step;
                if(nx<0||nz<0||nx>=solid.width||nz>=solid.height)return 0;
                bool lower=false,wall=false,same=false;
                for(var s=solid.spans[nx+nz*solid.width];s!=null;s=s.next)
                {
                    float top=s.smax*solid.ch,bottom=s.smin*solid.ch;
                    float gap=((s.next?.smin??int.MaxValue)-s.smax)*solid.ch;
                    if(s.area!=NavBake.AreaNull&&gap>=settings.AgentHeight)
                    {
                        // Follow rising support too: a bevel into a wall is not a
                        // staircase merely because its first neighbour is higher.
                        // Genuine stairs/ramps continue across the probe window.
                        if(top>=height-Math.Max(.35f,tolerance)&&top<=height+settings.AgentMaxClimb+solid.ch)same=true;
                        float drop=height-top;
                        if(drop>Math.Max(.35f,tolerance)&&drop<=Math.Max(settings.AgentMaxClimb+solid.ch,settings.AgentHeight*2))lower=true;
                    }
                    if(bottom<=height+tolerance&&top>=height+settings.AgentHeight)wall=true;
                }
                if(wall)return 2;
                if(lower&&!same)return 1;
                if(!same)return 0;
            }
            return 0;
        }
    }
}

