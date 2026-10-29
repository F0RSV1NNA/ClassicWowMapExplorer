using DotRecast.Detour;
namespace MapExtract;
internal sealed partial class SourceSurfaceFit
{
    // Keep Recast's continuous walking envelope and its boundary samples.
    // Fitting/lifting either side independently must not split a shared vertex.
    int StitchDetailBoundaries(DtMeshData data)
    {
        const double tolerance=.0015,binSize=.004;
        var bins=new Dictionary<(int X,int Y),List<List<(float[] Values,int At,int Owner)>>>();
        var neighbours=Enumerable.Range(0,data.header.polyCount).Select(p=>data.polys[p].neis.Where(n=>n>0&&(n&0x8000)==0).Select(n=>n-1).ToHashSet()).ToArray();
        int changed=0;
        void Add(float[] values,int at,int owner){
            double x=values[at+2],y=values[at];int bx=(int)Math.Floor(x/binSize),by=(int)Math.Floor(y/binSize);
            List<(float[] Values,int At,int Owner)>? group=null;
            for(int dx=-1;dx<=1&&group==null;dx++)for(int dy=-1;dy<=1&&group==null;dy++){
                if(!bins.TryGetValue((bx+dx,by+dy),out var candidates))continue;
                foreach(var candidate in candidates){var first=candidate[0];
                    // Nearby disconnected floors must never be welded together.
                    if(!candidate.Any(v=>v.Owner==owner||neighbours[v.Owner].Contains(owner)||(v.Values==values&&v.At==at)))continue;
                    if(Math.Abs(first.Values[first.At+2]-x)>tolerance||Math.Abs(first.Values[first.At]-y)>tolerance)continue;
                    float min=candidate.Min(v=>v.Values[v.At+1]),max=candidate.Max(v=>v.Values[v.At+1]);
                    if(Math.Max(max,values[at+1])-Math.Min(min,values[at+1])>rise)continue;
                    group=candidate;break;
                }
            }
            if(group==null){group=new();if(!bins.TryGetValue((bx,by),out var cell))bins[(bx,by)]=cell=new();cell.Add(group);}
            group.Add((values,at,owner));
        }
        var detail=new HashSet<int>();
        for(int p=0;p<data.detailMeshes.Length;p++){
            var poly=data.polys[p];if(poly.GetPolyType()!=0)continue;
            for(int i=0;i<poly.vertCount;i++)Add(data.verts,poly.verts[i]*3,p);
            var dm=data.detailMeshes[p];for(int i=0;i<dm.vertCount;i++)if(detail.Add(dm.vertBase+i))Add(data.detailVerts,(dm.vertBase+i)*3,p);
        }
        foreach(var candidates in bins.Values)foreach(var group in candidates){
            float height=group.Max(v=>v.Values[v.At+1]);
            foreach(var point in group)if(point.Values[point.At+1]!=height){point.Values[point.At+1]=height;changed++;}
        }
        return changed;
    }
}
