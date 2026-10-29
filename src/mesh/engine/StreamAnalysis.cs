using System.Runtime.InteropServices.JavaScript;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using MapExtract;

public partial class MeshEngine
{
    [JSExport]
    public static void BeginStreamWindow(int map)
    {
        EndAnalysis();
        GC.Collect();
        BeginAnalysis(map);
    }

    static DtMeshData ReadAnalysisTile(int row, int col)
    {
        using var r = new BinaryReader(File.OpenRead(AnalysisPath(NavWriter.TileFile(analysisMap, row, col))));
        if (r.ReadUInt32() != 0x4D4D4150) throw new InvalidDataException("Bad tile magic");
        r.BaseStream.Position = 20;
        return new DtMeshDataReader().Read(r, 6);
    }

    // A tile's exact directed link lists, extracted from a neighbourhood assembled by
    // Detour itself. File insertion order must match the complete map's insertion order.
    [JSExport]
    public static byte[] StreamGraph(int row, int col)
    {
        var (mesh, _, _, error) = NavWriter.LoadMesh(analysisDir!, analysisMap);
        if (mesh == null || error.Length != 0) throw new InvalidDataException(error);
        DtMeshTile? target = null;
        foreach (var tile in NavPrune.All(mesh))
        {
            var h = tile.data.header;
            int tr = NavBake.RowOf(NavBake.WorldOrigin + (h.y + .5f) * NavBake.AdtSize);
            int tc = NavBake.ColOf(NavBake.WorldOrigin + (h.x + .5f) * NavBake.AdtSize);
            if (tr == row && tc == col) target = tile;
        }
        if (target == null) throw new InvalidDataException("Requested graph tile not found");
        int count = target.data.header.polyCount;
        var edges = new List<(int Tile, int Poly)>();
        var offsets = new int[count + 1];
        var centers = new float[count * 4];
        var liquids = new Dictionary<(int, int), LiquidMap.Tile?>();
        for (int p = 0; p < count; p++)
        {
            var poly = target.data.polys[p];
            var c = NavPrune.Centre(target, poly);
            var key = (NavBake.RowOf(c.X), NavBake.ColOf(c.Y));
            if (!liquids.TryGetValue(key, out var liquid)) liquids[key] = liquid = LiquidMap.Read(analysisDir!, analysisMap, key.Item1, key.Item2);
            float surface = liquid?.SurfaceAt(c.X, c.Y) ?? float.NaN;
            centers[p * 4] = c.X; centers[p * 4 + 1] = c.Y; centers[p * 4 + 2] = c.Z;
            centers[p * 4 + 3] = float.IsNaN(surface)||poly.GetArea()==NavBake.AreaBlocked ? float.MinValue : surface;
            offsets[p] = edges.Count;
            if(poly.GetArea()==NavBake.AreaBlocked)continue;
            for (int k = poly.firstLink; k != DtDetour.DT_NULL_LINK; k = target.links[k].next)
            {
                long refs = target.links[k].refs;
                if (refs == 0 || mesh.GetTileAndPolyByRef(refs, out var t, out var neighbor).Failed()) continue;
                if(neighbor.GetArea()==NavBake.AreaBlocked)continue;
                var h = t.data.header;
                int nr = NavBake.RowOf(NavBake.WorldOrigin + (h.y + .5f) * NavBake.AdtSize);
                int nc = NavBake.ColOf(NavBake.WorldOrigin + (h.x + .5f) * NavBake.AdtSize);
                if ((uint)nr > 65535 || (uint)nc > 65535) throw new InvalidDataException("Tile coordinate outside graph encoding");
                edges.Add(((nr << 16) | nc, neighbor.index));
            }
        }
        offsets[count] = edges.Count;
        using var bytes = new MemoryStream(); using var w = new BinaryWriter(bytes);
        w.Write(count); w.Write(edges.Count);
        foreach (float f in centers) w.Write(f);
        foreach (int n in offsets) w.Write(n);
        foreach (var e in edges) { w.Write(e.Tile); w.Write(e.Poly); }
        return bytes.ToArray();
    }

    // Highest water surface per world cell, exactly as NavPrune.LiquidBodies. Only
    // the target file's footprint is materialised; overlapping input sheets are supplied
    // by the caller. Duplicate boundary cells therefore use the same maximum everywhere.
    [JSExport]
    public static byte[] StreamLiquid(int row, int col)
    {
        var own = LiquidMap.Read(analysisDir!, analysisMap, row, col) ?? throw new InvalidDataException("Missing liquid tile");
        const float cell = LiquidMap.Cell;
        int x0 = (int)MathF.Floor(own.OriginX / cell), y0 = (int)MathF.Floor(own.OriginY / cell);
        int x1 = (int)MathF.Floor((own.OriginX + (own.Width - 1) * cell) / cell);
        int y1 = (int)MathF.Floor((own.OriginY + (own.Height - 1) * cell) / cell);
        int width = checked(x1 - x0 + 1), height = checked(y1 - y0 + 1);
        var heights = new float[checked(width * height)]; Array.Fill(heights, float.NaN);
        foreach (var path in Directory.GetFiles(analysisDir!, $"{analysisMap:0000}_*.mliq"))
        {
            var parts = Path.GetFileNameWithoutExtension(path).Split('_');
            var t = LiquidMap.Read(analysisDir!, analysisMap, int.Parse(parts[1]), int.Parse(parts[2]));
            if (t == null) throw new InvalidDataException("Invalid liquid tile");
            for (int cy = 0; cy < t.Height; cy++) for (int cx = 0; cx < t.Width; cx++)
            {
                int i = cy * t.Width + cx;
                if (!t.Wetness[i]) continue;
                float z = t.IsLevel ? t.Level : t.Heights![i];
                if (float.IsNaN(z)) continue;
                int x = (int)MathF.Floor((t.OriginX + cx * cell) / cell) - x0;
                int y = (int)MathF.Floor((t.OriginY + cy * cell) / cell) - y0;
                if ((uint)x >= width || (uint)y >= height) continue;
                int at = y * width + x;
                if (float.IsNaN(heights[at]) || z > heights[at]) heights[at] = z;
            }
        }
        var labels = new int[heights.Length]; Array.Fill(labels, -1);
        var stack = new Stack<int>(); int bodies = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] >= 0 || float.IsNaN(heights[i])) continue;
            labels[i] = bodies; stack.Push(i);
            while (stack.TryPop(out int at))
            {
                void Visit(int next)
                {
                    if (labels[next] >= 0 || float.IsNaN(heights[next]) || MathF.Abs(heights[next] - heights[at]) > 2f) return;
                    labels[next] = bodies; stack.Push(next);
                }
                int x = at % width;
                if (x > 0) Visit(at - 1); if (x + 1 < width) Visit(at + 1);
                if (at >= width) Visit(at - width); if (at + width < labels.Length) Visit(at + width);
            }
            bodies++;
        }
        using var bytes = new MemoryStream(); using var w = new BinaryWriter(bytes);
        w.Write(x0); w.Write(y0); w.Write(width); w.Write(height); w.Write(bodies);
        foreach (float f in heights) w.Write(f); foreach (int n in labels) w.Write(n);
        return bytes.ToArray();
    }

    [JSExport]
    public static bool ApplyStreamFlags(int row, int col, byte[] stranded)
    {
        var d = ReadAnalysisTile(row, col);
        if (stranded.Length != d.header.polyCount) throw new InvalidDataException("Flag count differs from polygon count");
        bool changed = false;
        for (int p = 0; p < stranded.Length; p++)
        {
            var poly = d.polys[p]; int before = poly.flags;
            poly.flags &= ~NavBake.FlagStranded;
            poly.flags |= NavBake.FlagOfArea(poly.GetArea());
            if (stranded[p] != 0) poly.flags |= NavBake.FlagStranded;
            changed |= before != poly.flags;
        }
        if (changed) NavWriter.WriteTile(analysisDir!, analysisMap, new NavBake.NavTile(row, col, d.header.x, d.header.y, d, d.header.polyCount, 0, d.header.vertCount));
        return changed;
    }
}
