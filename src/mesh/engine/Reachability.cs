using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotRecast.Detour.Io;
using MapExtract;

public partial class MeshEngine
{
    static string? analysisDir;
    static int analysisMap;

    [JSExport]
    public static void BeginAnalysis(int map)
    {
        EndAnalysis();
        analysisMap = map;
        analysisDir = Path.Combine(Path.GetTempPath(), "mesh-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(analysisDir);
    }

    static string AnalysisPath(string name)
    {
        if (analysisDir == null || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') ||
            !(name == NavWriter.MapFile(analysisMap) ||
              (name.StartsWith($"{analysisMap:0000}_") && (name.EndsWith(".mmtile") || name.EndsWith(".mliq")))))
            throw new InvalidDataException("Invalid analysis file");
        return Path.Combine(analysisDir, name);
    }

    [JSExport] public static void PutAnalysisFile(string name, byte[] bytes) => File.WriteAllBytes(AnalysisPath(name), bytes);
    [JSExport] public static byte[] GetAnalysisFile(string name) => File.ReadAllBytes(AnalysisPath(name));

    [JSExport]
    public static string Analyze(int minComponent)
    {
        if (analysisDir == null || minComponent < 0) throw new InvalidOperationException("No analysis session or invalid component threshold");
        // Unchanged original pass: includes cross-tile Detour links and liquid-body unions.
        // Keep surface flags: stranded polygons are marked, never deleted or forcibly dropped.
        var report = NavPrune.Run(analysisDir, analysisMap, drop: false, minComponent: minComponent);
        if (report.Error.Length != 0) throw new InvalidDataException(report.Error);
        return JsonSerializer.Serialize(report, ReachabilityJson.Default.Report);
    }

    [JSExport]
    public static byte[] PreviewAnalysisTile(int row, int col)
    {
        var path = AnalysisPath(NavWriter.TileFile(analysisMap, row, col));
        using var file = new BinaryReader(File.OpenRead(path));
        if (file.ReadUInt32() != 0x4D4D4150) throw new InvalidDataException("Bad mesh tile magic"); // NavWriter's private MmapMagic.
        file.BaseStream.Position = 20;
        var d = new DtMeshDataReader().Read(file, 6);
        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);
        w.Write(0); int triangles = 0;
        for (int p = 0; p < d.header.polyCount; p++)
        {
            var poly = d.polys[p];
            if (poly.GetPolyType() != 0) continue;
            void Vertex(int idx, int detailBase)
            {
                var v = idx < poly.vertCount ? d.verts : d.detailVerts;
                int at = idx < poly.vertCount ? poly.verts[idx] * 3 : (detailBase + idx - poly.vertCount) * 3;
                w.Write(v[at + 2]); w.Write(v[at]); w.Write(v[at + 1]);
            }
            void Triangle(int a, int b, int c, int detailBase)
            {
                w.Write(poly.GetArea()); w.Write(poly.flags);
                Vertex(a, detailBase); Vertex(b, detailBase); Vertex(c, detailBase); triangles++;
            }
            if (d.detailMeshes == null || p >= d.detailMeshes.Length)
            {
                for (int k = 2; k < poly.vertCount; k++) Triangle(0, k - 1, k, 0);
                continue;
            }
            var dm = d.detailMeshes[p];
            for (int t = 0; t < dm.triCount; t++)
            {
                int at = (dm.triBase + t) * 4;
                Triangle(d.detailTris[at], d.detailTris[at + 1], d.detailTris[at + 2], dm.vertBase);
            }
        }
        output.Position = 0; w.Write(triangles);
        return output.ToArray();
    }

    [JSExport]
    public static void EndAnalysis()
    {
        if (analysisDir != null && Directory.Exists(analysisDir)) Directory.Delete(analysisDir, true);
        analysisDir = null;
    }
}

[JsonSerializable(typeof(NavPrune.Report))]
internal partial class ReachabilityJson : JsonSerializerContext { }
