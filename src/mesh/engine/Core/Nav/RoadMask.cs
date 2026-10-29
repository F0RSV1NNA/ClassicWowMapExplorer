namespace MapExtract;

/// Where the roads are, read from the ground textures and corrected by hand.
///
/// A road in WoW is paint: a texture layer on the terrain. Most zones have a dedicated one -
/// elwynncobblestonebase, barrensroad01, durotarroad - that traces the road network cleanly.
/// Dirt does not count: the same dirt is painted over fields and clearings and would mark
/// half the map (document.md 44). So a texture is a road when its name says so unless the
/// person marking says otherwise, and brush strokes add road, remove it, or erase the mesh
/// outright, on top of that.
public sealed class RoadMask
{
    const float Tile = 533.33333f;
    const float Texel = Tile / Tex0.Texels;

    /// Road paint needed at a texel for it to count, out of 255.
    ///
    /// Asking which texture DOMINATES flips cell by cell wherever road is blended half and
    /// half with dirt - along its edges and in speckles through its middle - which left the
    /// baked roads full of pinholes and ground polygons. How much road paint is there is a
    /// smooth quantity, and a third of full strength is a road you would walk on.
    const int Threshold = 96;

    /// Texels of closing (fill gaps and holes up to about twice this) and then of opening
    /// (drop specks smaller than about twice this). 0.52 yd a texel.
    const int Close = 3, Open = 1;

    readonly Dictionary<(int, int), Tex0> _tiles = [];
    readonly Dictionary<(int, int), bool[]> _road = [];         // per tile, per texel
    readonly List<MapEdits.Stroke> _strokes;

    /// Strokes by 16 yd cell, in stroke order. The bake asks about every cell of every tile,
    /// millions of times, and a zone's worth of brush work is hundreds of strokes.
    const float Bucket = 16f;
    readonly Dictionary<(int, int), List<int>> _near = [];

    public int Tiles => _tiles.Count;

    RoadMask(List<MapEdits.Stroke> strokes)
    {
        _strokes = strokes;
        for (int i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            int x0 = (int)MathF.Floor((s.X - s.Radius) / Bucket), x1 = (int)MathF.Floor((s.X + s.Radius) / Bucket);
            int y0 = (int)MathF.Floor((s.Y - s.Radius) / Bucket), y1 = (int)MathF.Floor((s.Y + s.Radius) / Bucket);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (!_near.TryGetValue((x, y), out var l)) _near[(x, y)] = l = [];
                    l.Add(i);
                }
        }
    }

    /// The automatic rule: a texture whose tileset path names a road surface.
    public static bool RoadByName(string? path)
    {
        if (path == null) return false;
        var p = path.Replace('\\','/').Split('/')[^1].ToLowerInvariant();
        if (p.Contains("crater")) return false;
        return p.Contains("road") || p.Contains("cobble") || p.Contains("street") ||
               p.Contains("paved") || p.Contains("flagstone") || p.Contains("footpath") || p.Contains("pathway");
    }

    /// Is this texture a road, by name and by the person's overrides.
    public static bool IsRoadTexture(int fdid, MapEdits.Global g)
    {
        if (g.RoadOff.Contains(fdid)) return false;
        if (g.RoadOn.Contains(fdid)) return true;
        return RoadByName(Names.Tileset(fdid));
    }

    /// The mask for a set of tiles. `read` fetches a file by FileDataID.
    public static RoadMask Build(Func<int, byte[]?> read, IEnumerable<TileFiles> tiles,
                                 MapEdits.Map edits, MapEdits.Global global)
    {
        var m = new RoadMask(edits.Roads);
        foreach (var t in tiles)
        {
            if (t.Tex0 == 0 || read((int)t.Tex0) is not { } bytes) continue;
            if (Tex0.Parse(bytes) is not { } tex) continue;
            m._tiles[(t.X, t.Y)] = tex;

            bool[] wanted = [.. tex.Textures.Select(f => IsRoadTexture(f, global))];
            var road = new bool[Tex0.Texels * Tex0.Texels];
            if (wanted.Any(w => w))
            {
                var cover = tex.Coverage(wanted);
                for (int i = 0; i < road.Length; i++) road[i] = cover[i] >= Threshold;
                // Close, then open: fill the holes and gaps first, then drop what is left
                // too small to be a road.
                road = Erode(Dilate(road, Close), Close);
                road = Dilate(Erode(road, Open), Open);
            }
            m._road[(t.X, t.Y)] = road;
        }
        return m;
    }

    /// The road texture's opinion, with 2D strokes on top - for drawing the preview on the
    /// terrain, where there is only one surface per column.
    public bool IsRoad(float worldX, float worldY)
    {
        bool road = Painted(worldX, worldY);
        return StrokeAt(worldX, worldY, null) switch
        {
            MapEdits.StrokeKind.PaintRoad => true,
            MapEdits.StrokeKind.RemoveRoad or MapEdits.StrokeKind.EraseMesh => false,
            _ => road,
        };
    }

    /// The last stroke over this spot, or null. With a height, only strokes placed within
    /// StrokeBand of it count, so a stroke on a bridge deck does not reach the river bed.
    public MapEdits.StrokeKind? StrokeAt(float worldX, float worldY, float? worldZ)
    {
        var key = ((int)MathF.Floor(worldX / Bucket), (int)MathF.Floor(worldY / Bucket));
        if (!_near.TryGetValue(key, out var near)) return null;
        MapEdits.StrokeKind? kind = null;
        foreach (int i in near)
        {
            var s = _strokes[i];
            float dx = worldX - s.X, dy = worldY - s.Y;
            if (dx * dx + dy * dy > s.Radius * s.Radius) continue;
            if (worldZ is { } z && MathF.Abs(z - s.Z) > s.HeightBand) continue;
            kind = s.Effective;
        }
        return kind;
    }

    /// Only the texture's opinion, smoothed.
    public bool Painted(float worldX, float worldY)
    {
        if (!Locate(worldX, worldY, out var key, out int i)) return false;
        return _road[key][i];
    }

    /// The dominant texture at a position, for diagnostics and the viewer's texture list.
    public (int Fdid, int Strength) TextureAt(float worldX, float worldY)
    {
        if (!Locate(worldX, worldY, out var key, out int i)) return (0, 0);
        var t = _tiles[key];
        short d = t.Dominant[i];
        return d >= 0 && d < t.Textures.Count ? (t.Textures[d], t.Strength[i]) : (0, 0);
    }

    public IEnumerable<(int Id,int Weight)> LayersAt(float worldX,float worldY)
    {
        if(!Locate(worldX,worldY,out var key,out int index))yield break;
        int x=index%Tex0.Texels,y=index/Tex0.Texels;
        if(_tiles[key].Chunks[y/64*16+x/64] is not {} chunk)yield break;
        int pixel=y%64*64+x%64,rest=255;
        var weights=new Dictionary<int,int>();
        for(int l=chunk.Tex.Length-1;l>=0;l--)
        {
            int alpha=l==0?255:chunk.Alpha[l]?[pixel]??0;
            int weight=alpha*rest/255;rest-=weight;
            int tex=chunk.Tex[l];
            if(tex>=0&&tex<_tiles[key].Textures.Count&&weight>0)
            {int id=_tiles[key].Textures[tex];weights[id]=weights.GetValueOrDefault(id)+weight;}
        }
        foreach(var pair in weights.OrderByDescending(p=>p.Value))yield return(pair.Key,pair.Value);
    }

    bool Locate(float worldX, float worldY, out (int, int) key, out int index)
    {
        int row = (int)MathF.Floor(32f - worldX / Tile);
        int col = (int)MathF.Floor(32f - worldY / Tile);
        key = (row, col);
        index = 0;
        if (!_tiles.ContainsKey(key)) return false;

        // Chunk rows run toward -X and columns toward -Y from the tile's max corner, the same
        // as the height grid in Terrain.cs, and the alpha maps follow the chunk. Checked
        // against the ground: rock textures sit on terrain five times steeper than grass
        // this way round (1.125 vs 0.223 on Elwynn 49,31) and barely steeper transposed.
        float maxX = (32 - row) * Tile, maxY = (32 - col) * Tile;
        int gy = (int)((maxX - worldX) / Texel), gx = (int)((maxY - worldY) / Texel);
        if ((uint)gx >= Tex0.Texels || (uint)gy >= Tex0.Texels) return false;
        index = gy * Tex0.Texels + gx;
        return true;
    }

    // Square-window morphology on one tile's texel grid, separable, so each pass is linear
    // in the grid whatever the radius.
    static bool[] Dilate(bool[] a, int r) => AnyInWindow(a, r, true);
    static bool[] Erode(bool[] a, int r)
    {
        var anyFalse = AnyInWindow(a, r, false);
        for (int i = 0; i < anyFalse.Length; i++) anyFalse[i] = !anyFalse[i];
        return anyFalse;
    }

    static bool[] AnyInWindow(bool[] a, int r, bool value)
    {
        const int N = Tex0.Texels;
        var h = new bool[a.Length];
        for (int y = 0; y < N; y++)
        {
            int count = 0;
            for (int x = 0; x < Math.Min(r, N); x++) if (a[y * N + x] == value) count++;
            for (int x = 0; x < N; x++)
            {
                if (x + r < N && a[y * N + x + r] == value) count++;
                if (x - r - 1 >= 0 && a[y * N + x - r - 1] == value) count--;
                h[y * N + x] = count > 0;
            }
        }
        var v = new bool[a.Length];
        for (int x = 0; x < N; x++)
        {
            int count = 0;
            for (int y = 0; y < Math.Min(r, N); y++) if (h[y * N + x]) count++;
            for (int y = 0; y < N; y++)
            {
                if (y + r < N && h[(y + r) * N + x]) count++;
                if (y - r - 1 >= 0 && h[(y - r - 1) * N + x]) count--;
                v[y * N + x] = count > 0;
            }
        }
        return v;
    }
}
