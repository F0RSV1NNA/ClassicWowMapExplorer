using DotRecast.Detour;
using System.Numerics;
using System.Text.Json;
internal static class MeshSnapshots
{
    internal static void Write(DtMeshData data,string file){
        var verts=new List<Vector3>();var indices=new List<int>();var areas=new List<int>();
        for(int p=0;p<data.detailMeshes.Length;p++){
            var poly=data.polys[p];if(poly.GetPolyType()!=0)continue;var dm=data.detailMeshes[p];
            for(int t=0;t<dm.triCount;t++){int at=(dm.triBase+t)*4;
                for(int k=0;k<3;k++){int id=data.detailTris[at+k];var values=id<poly.vertCount?data.verts:data.detailVerts;int v=(id<poly.vertCount?poly.verts[id]:dm.vertBase+id-poly.vertCount)*3;
                    indices.Add(verts.Count);verts.Add(new(values[v+2],values[v],values[v+1]));}
                areas.Add(poly.GetArea());
            }
        }
        File.WriteAllText(file,JsonSerializer.Serialize(new{verts,indices,areas},new JsonSerializerOptions{IncludeFields=true}));
    }
}
