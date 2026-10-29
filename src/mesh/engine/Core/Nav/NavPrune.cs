using DotRecast.Detour;

namespace MapExtract;

/// Marks the polygons of a finished map that nothing can walk to.
///
/// This cannot be done while baking, and that is the whole reason it is a separate pass. A
/// tile is baked on its own, so flooding inside one only ever sees that tile's own links: a
/// tile whose ground arrives from its neighbours has almost nothing in its own largest
/// component and nearly all of it would be called stranded. Reachability is a property of
/// the assembled map, so this runs over what was written, floods once, and rewrites.
///
/// The result is a flag rather than a deletion. "Unreachable" is not a permanent property of
/// a polygon - it is relative to the rest of the mesh as currently baked, and to this agent.
/// Add one off-mesh connection and a whole rooftop becomes reachable; change the agent's
/// climb and so does the answer. A flag can be recomputed by running this again. A deleted
/// polygon needs the map baked from scratch.
public static class NavPrune
{
    public readonly record struct Report(int Tiles, int Polys, int Components, int Largest,
                                         int Stranded, int Rewritten, string Error)
    {
        public double StrandedPercent => Polys > 0 ? Stranded * 100.0 / Polys : 0;
    }

    /// Flood the whole map and set FlagStranded outside the largest component.
    ///
    /// With `drop`, a stranded polygon also loses its area flags, so no query filter's
    /// include mask can match it however the consumer is configured - the belt to
    /// FlagStranded's braces, for a build being shipped to something that might not know
    /// to exclude it.
    /// Polygons a component needs before it counts as a place rather than a scrap.
    /// 0 restores the old "only the largest component survives" rule.
    public static int MinComponent =
        int.TryParse(Environment.GetEnvironmentVariable("MIN_PIECE"), out int mp) ? mp : 1000;

    public static Report Run(string dir, int mapId, bool drop = false, int? minComponent = null)
    {
        var (mesh, tiles, polys, err) = NavWriter.LoadMesh(dir, mapId);
        if (mesh == null) return new Report(0, 0, 0, 0, 0, 0, err);

        var (comp, sizes, mainland) = Flood(mesh, dir, mapId);
        if (mainland < 0) return new Report(tiles, 0, 0, 0, 0, 0, "no polygons");
        // 0 means the old rule - nothing but the largest component survives - rather than
        // "no floor", which would flag nothing at all.
        int minComp = minComponent ?? MinComponent;
        if (minComp <= 0) minComp = int.MaxValue;

        int stranded = 0;
        var touched = new HashSet<DtMeshTile>();

        // Recomputed from scratch rather than OR-ed onto what is there. A tile may already
        // carry marks from an earlier run over a different set of tiles, and those have to
        // go, or a second pass could only ever add.
        foreach (var (refs, id) in comp)
        {
            if (mesh.GetTileAndPolyByRef(refs, out var tile, out var poly).Failed()) continue;
            int before = poly.flags;

            poly.flags &= ~NavBake.FlagStranded;
            // NavBake's own mapping, not a copy of it.
            //
            // There was a second copy here, and it defaulted unknown areas to FlagGround.
            // When the bake started disowning polygons it found buried inside the ground,
            // this handed every one of them a ground flag straight back - so a single-tile
            // bake looked fixed and the assembled map, which is the only thing that runs
            // this pass, was not. Two switches over the same enum will always drift.
            poly.flags |= NavBake.FlagOfArea(poly.GetArea());   // undo any earlier drop

            // A piece big enough to be somewhere is not "unreachable", it is SEPARATE.
            //
            // The old rule was "not in the largest component", which assumes a map is one
            // landmass with debris round it. Kalimdor is not: Teldrassil is reached by ship
            // and Darnassus by a portal from Rut'theran, so the whole night elf starting
            // zone - 105,578 polygons - came out pink, and a bot could not path from one
            // side of Darnassus to the other.
            //
            // Size separates the two cleanly and without guessing. Kalimdor has 139,653
            // components; 133 of them have a thousand polygons or more and every one of
            // those is a place, while the remaining 139,520 are slivers and ledges. What
            // the flag is for is telling a consumer "there is nothing here for you", and
            // that is true of a scrap and false of a city.
            //
            // Being separate still means no route to it, which the pathfinder reports by
            // failing to find one. That is the honest answer, and better than pretending
            // the ground is not there.
            if (id != mainland && sizes[id] < minComp)
            {
                poly.flags |= NavBake.FlagStranded;
                if (drop) poly.flags &= ~NavBake.FlagsAnySurface;
                stranded++;
            }
            if (poly.flags != before) touched.Add(tile);
        }

        int rewritten = 0;
        foreach (var tile in touched)
        {
            var h = tile.data.header;
            // The filename indices, derived from the tile's own grid position exactly as the
            // bake derived them - not parsed back out of the filename.
            float wyc = NavBake.WorldOrigin + (h.x + 0.5f) * NavBake.AdtSize;
            float wxc = NavBake.WorldOrigin + (h.y + 0.5f) * NavBake.AdtSize;
            NavWriter.WriteTile(dir, mapId,
                new NavBake.NavTile(NavBake.RowOf(wxc), NavBake.ColOf(wyc), h.x, h.y,
                                    tile.data, h.polyCount, 0, h.vertCount));
            rewritten++;
        }

        return new Report(tiles, polys, sizes.Count(n => n > 0), sizes[mainland],
                          stranded, rewritten, "");
    }

    /// The flag a polygon of this area should carry, so `drop` can be undone.

    /// Connected components over the whole map, with swimming counted as a connection.
    ///
    /// Detour links two polygons only where you can WALK between them, so a lake bed is
    /// never linked to the water over it - the wall between them is a cliff. Flooding the
    /// links alone therefore calls every seabed unreachable, and paints half a lake pink.
    ///
    /// Whether that is a lie depends entirely on what the consumer can do, and this has now
    /// been both. While the plan was to route dives through the mesh, joining them here was
    /// dishonest: it claimed reachable what Detour could not path to. With water moved into
    /// the .mliq layer the consumer swims freely instead of routing, so a floor under
    /// reachable water genuinely is reachable, and refusing to join them would be the lie.
    /// The union is back, and this time it matches what the thing reading the mesh can do.
    ///
    /// Returns each polygon's component id, the component sizes, and which is the mainland.
    // Experimental switches, to find out which joining rule carries a piece to the mainland.
    static readonly bool SkipDisowned = Environment.GetEnvironmentVariable("FLOOD_SKIP_DISOWNED") == "1";
    static readonly bool NoWaterUnion = Environment.GetEnvironmentVariable("FLOOD_NO_WATER") == "1";

    /// Buried or basement: kept for the viewer, carries no walkable flag.
    public static bool Disowned(DtPoly p) => p.GetPolyType() == 0 && (p.flags & NavBake.FlagsAnySurface) == 0;

    public static (Dictionary<long, int> Comp, List<int> Sizes, int Mainland) Flood(
        DtNavMesh mesh, string? dir = null, int mapId = 0)
    {
        var comp = new Dictionary<long, int>();
        var raw = new List<int>();
        var stack = new Stack<long>();

        foreach (var tile in All(mesh))
        {
            long bas = mesh.GetPolyRefBase(tile);
            for (int p = 0; p < tile.data.header.polyCount; p++)
            {
                long start = bas | (long)(uint)p;
                if (comp.ContainsKey(start)) continue;

                int id = raw.Count, n = 0;
                comp[start] = id;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    long cur = stack.Pop();
                    n++;
                    if (mesh.GetTileAndPolyByRef(cur, out var ct, out var cp).Failed()) continue;
                    if(cp.GetArea()==NavBake.AreaBlocked)continue;
                    if (SkipDisowned && Disowned(cp)) continue;
                    for (int k = cp.firstLink; k != DtDetour.DT_NULL_LINK; k = ct.links[k].next)
                    {
                        long nb = ct.links[k].refs;
                        if (nb == 0 || comp.ContainsKey(nb)) continue;
                        if(mesh.GetTileAndPolyByRef(nb,out _,out var blocked).Succeeded()&&blocked.GetArea()==NavBake.AreaBlocked)continue;
                        if (SkipDisowned && mesh.GetTileAndPolyByRef(nb, out _, out var np).Succeeded() &&
                            Disowned(np)) continue;
                        comp[nb] = id;
                        stack.Push(nb);
                    }
                }
                raw.Add(n);
            }
        }
        if (raw.Count == 0) return (comp, raw, -1);

        var uf = new int[raw.Count];
        for (int i = 0; i < uf.Length; i++) uf[i] = i;

        // Everything in one body of water is mutually reachable, because you swim.
        //
        // This used to union a submerged floor with the liquid POLYGONS above it, which only
        // worked while water was also in the navmesh. It is the .mliq layer that describes
        // the water now, so the rule is stated against that instead: find the bodies, decide
        // which polygons lie in each, and join them. A lake bed and the shore that dips into
        // the same lake end up in one component whether or not a water polygon ever existed.
        //
        // And it answers the question properly. Ground under water is reachable unless the
        // body it sits in touches nothing reachable - so a pool stranded up a hillside stays
        // stranded, while an ordinary lake bed does not.
        if (dir != null && !NoWaterUnion)
        {
            var body = LiquidBodies(dir, mapId, out int bodyCount);
            if (bodyCount > 0)
            {
                var firstIn = new int[bodyCount];
                Array.Fill(firstIn, -1);
                var surfaceCache = new Dictionary<(int, int), LiquidMap.Tile?>();

                foreach (var (refs, id) in comp)
                {
                    if (mesh.GetTileAndPolyByRef(refs, out var t, out var poly).Failed()) continue;
                    if(poly.GetArea()==NavBake.AreaBlocked)continue;
                    var c = Centre(t, poly);
                    var key = ((int)MathF.Floor(c.X / LiquidMap.Cell),
                               (int)MathF.Floor(c.Y / LiquidMap.Cell));
                    if (!body.TryGetValue(key, out int b)) continue;

                    // In the water or at its edge. A polygon well above the surface is a
                    // bridge or a cliff top and has nothing to do with the lake.
                    if (c.Z > LiquidSurface(surfaceCache, dir, mapId, c) +
                              NavBake.FlagStrandedSurfaceSlack) continue;

                    if (firstIn[b] < 0) firstIn[b] = id;
                    else Union(uf, firstIn[b], id);
                }
            }
        }

        var sizes = new List<int>(new int[raw.Count]);
        foreach (var (_, id) in comp) sizes[Find(uf, id)]++;
        var merged = new Dictionary<long, int>(comp.Count);
        foreach (var (refs, id) in comp) merged[refs] = Find(uf, id);

        int mainland = 0;
        for (int i = 1; i < sizes.Count; i++) if (sizes[i] > sizes[mainland]) mainland = i;
        return (merged, sizes, mainland);
    }

    /// Every liquid body on the map, as a set of world cells, from the .mliq layer.
    ///
    /// A body is a run of wet cells that touch and sit at about the same height, so a lake
    /// partway up a hill is not joined to the sea it happens to drain into. Cells are keyed
    /// in world space, so a lake crossing an ADT boundary is still one body.
    static Dictionary<(int X, int Y), int> LiquidBodies(string dir, int mapId, out int count)
    {
        const float Cell = LiquidMap.Cell;
        const float SameBody = 2f;              // a body is level to about this

        var top = new Dictionary<(int, int), float>();
        foreach (var path in Directory.GetFiles(dir, $"{mapId:0000}_*.mliq"))
        {
            var name = Path.GetFileNameWithoutExtension(path).Split('_');
            if (name.Length != 3 || !int.TryParse(name[1], out int row) || !int.TryParse(name[2], out int col))
                continue;
            var tile = LiquidMap.Read(dir, mapId, row, col);
            if (tile == null) continue;

            for (int cy = 0; cy < tile.Height; cy++)
                for (int cx = 0; cx < tile.Width; cx++)
                {
                    int i = cy * tile.Width + cx;
                    if (i >= tile.Wetness.Length || !tile.Wetness[i]) continue;
                    float z = tile.IsLevel ? tile.Level : tile.Heights![i];
                    if (float.IsNaN(z)) continue;
                    var key = ((int)MathF.Floor((tile.OriginX + cx * Cell) / Cell),
                               (int)MathF.Floor((tile.OriginY + cy * Cell) / Cell));
                    if (!top.TryGetValue(key, out float have) || z > have) top[key] = z;
                }
        }

        var body = new Dictionary<(int X, int Y), int>(top.Count);
        int next = 0;
        var stack = new Stack<(int X, int Y)>();
        foreach (var (start, _) in top)
        {
            if (body.ContainsKey(start)) continue;
            int id = next++;
            body[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var at = stack.Pop();
                float z = top[at];
                foreach (var nb in new[] { (at.X + 1, at.Y), (at.X - 1, at.Y),
                                           (at.X, at.Y + 1), (at.X, at.Y - 1) })
                {
                    if (body.ContainsKey(nb) || !top.TryGetValue(nb, out float nz)) continue;
                    if (MathF.Abs(nz - z) > SameBody) continue;
                    body[nb] = id;
                    stack.Push(nb);
                }
            }
        }
        count = next;
        return body;
    }

    /// A polygon's centre in world space (Z-up).
    public static System.Numerics.Vector3 Centre(DtMeshTile t, DtPoly poly)
    {
        float x = 0, y = 0, z = 0;
        for (int i = 0; i < poly.vertCount; i++)
        {
            int v = poly.verts[i] * 3;
            y += t.data.verts[v]; z += t.data.verts[v + 1]; x += t.data.verts[v + 2];
        }
        return new System.Numerics.Vector3(x / poly.vertCount, y / poly.vertCount, z / poly.vertCount);
    }

    /// The liquid surface over a point, from the tile that covers it.
    ///
    /// Cached per ADT. The first cut read the file again for every polygon on the map,
    /// which is tens of thousands of opens of the same handful of files.
    static float LiquidSurface(Dictionary<(int, int), LiquidMap.Tile?> cache, string dir,
                               int mapId, System.Numerics.Vector3 p)
    {
        var key = (NavBake.RowOf(p.X), NavBake.ColOf(p.Y));
        if (!cache.TryGetValue(key, out var tile))
            cache[key] = tile = LiquidMap.Read(dir, mapId, key.Item1, key.Item2);
        float z = tile?.SurfaceAt(p.X, p.Y) ?? float.NaN;
        return float.IsNaN(z) ? float.MinValue : z;
    }

    /// The world cells a polygon covers at its corners.
    static IEnumerable<(int, int)> Cells(DtMeshTile t, DtPoly poly, float cell)
    {
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < poly.vertCount; i++)
        {
            int v = poly.verts[i] * 3;
            var key = ((int)MathF.Floor(t.data.verts[v] / cell),
                       (int)MathF.Floor(t.data.verts[v + 2] / cell));
            if (seen.Add(key)) yield return key;
        }
    }

    static int Find(int[] uf, int i) { while (uf[i] != i) { uf[i] = uf[uf[i]]; i = uf[i]; } return i; }
    static void Union(int[] uf, int a, int b)
    { a = Find(uf, a); b = Find(uf, b); if (a != b) uf[b] = a; }

    public static IEnumerable<DtMeshTile> All(DtNavMesh mesh)
    {
        for (int i = 0; i < mesh.GetMaxTiles(); i++)
        {
            var t = mesh.GetTile(i);
            if (t?.data?.header != null) yield return t;
        }
    }

}
