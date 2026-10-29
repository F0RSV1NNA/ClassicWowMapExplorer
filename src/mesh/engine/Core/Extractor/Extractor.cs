using System.Numerics;
using CASCLib;

namespace MapExtract;

/// Builds a world-space collision triangle soup for one ADT tile.
public sealed class Extractor(CASCHandler casc, Func<ushort, LiquidClass> classify)
{
    readonly Dictionary<uint, (Wmo.Mesh Solid, Wmo.Mesh Liquid, uint GroupLiquid, Wmo.Root Root)> _wmoCache = [];
    readonly Dictionary<uint, Wmo.Mesh> _m2Cache = [];

    public int SkippedGroups { get; private set; }
    public int SkippedGroupTris { get; private set; }

    public sealed class Soup
    {
        public List<Vector3> Verts = [];
        public List<int> Indices = [];
        public List<Vector3> LiquidVerts = [];
        public List<int> LiquidIndices = [];
        public List<LiquidClass> LiquidTriClass = [];
        public int TerrainTris, WmoTris, DoodadTris, InteriorTris;

        /// One flag per triangle: is it the ADT heightmap, as opposed to a WMO, its
        /// interior, or a doodad?
        ///
        /// The counts above cannot answer this once two tiles have been folded together,
        /// because Append concatenates each tile's terrain and its objects in turn and the
        /// ranges interleave. The bake needs to tell them apart to know what "under the
        /// ground" means: below the heightmap is nowhere, while below it and under a roof
        /// is a cave and perfectly reachable.
        public List<bool> TriTerrain = [];

        /// What each triangle came from. Terrain and WMO surfaces are things to stand on;
        /// a doodad is a prop, and the navmesh must never lie on one - a bot routed onto a
        /// crate or a rock gets stuck on it. Telling them apart needs a per-triangle
        /// answer, because TriTerrain only separates the heightmap from everything else
        /// and a WMO floor has to stay walkable.
        ///
        /// Doodads placed INSIDE a WMO count as doodads. They are props in a room.
        public const byte KindTerrain = 0, KindWmo = 1, KindDoodad = 2;
        public List<byte> TriKind = [];

        /// Fill TriTerrain from the counts, once a tile's soup is complete. Terrain is
        /// always emitted first, so the split is a prefix.
        public Soup Seal()
        {
            TriTerrain.Clear();
            TriKind.Clear();
            // Emission order is terrain, then WMOs, then the doodads inside them, then
            // the doodads on the ground, so every category is a contiguous range.
            int wmoEnd = TerrainTris + WmoTris;
            for (int t = 0; t < TriCount; t++)
            {
                TriTerrain.Add(t < TerrainTris);
                TriKind.Add(t < TerrainTris ? KindTerrain : t < wmoEnd ? KindWmo : KindDoodad);
            }
            return this;
        }
        public int TriCount => Indices.Count / 3;
        public int LiquidTris => LiquidIndices.Count / 3;

        /// Fold in only the triangles overlapping a world-space XY rect.
        ///
        /// A neighbouring ADT is loaded purely to give Recast a border ring about
        /// 1.3 yd wide. Appending all 533 yd of it is ~400x more geometry than the
        /// border needs, and on the densest ground that reached 13.5M triangles and
        /// killed the process. Keeping the band costs nothing and bakes the same.
        public void AppendClipped(Soup other, float minX, float minY, float maxX, float maxY)
        {
            Filter(other.Verts, other.Indices, Verts, Indices, null, null,
                   other.TriTerrain, TriTerrain, other.TriKind, TriKind, minX, minY, maxX, maxY);
            Filter(other.LiquidVerts, other.LiquidIndices, LiquidVerts, LiquidIndices,
                   other.LiquidTriClass, LiquidTriClass, null, null, null, null,
                   minX, minY, maxX, maxY);
        }

        static void Filter(List<Vector3> srcV, List<int> srcI, List<Vector3> dstV, List<int> dstI,
                           List<LiquidClass>? srcC, List<LiquidClass>? dstC,
                           List<bool>? srcT, List<bool>? dstT,
                           List<byte>? srcK, List<byte>? dstK,
                           float minX, float minY, float maxX, float maxY)
        {
            var remap = new Dictionary<int, int>();
            for (int t = 0; t < srcI.Count / 3; t++)
            {
                int a = srcI[t * 3], b = srcI[t * 3 + 1], c = srcI[t * 3 + 2];
                Vector3 va = srcV[a], vb = srcV[b], vc = srcV[c];

                // triangle AABB vs rect: a vertex test would drop long triangles
                // that cross the band without landing a corner in it
                if (MathF.Max(va.X, MathF.Max(vb.X, vc.X)) < minX) continue;
                if (MathF.Min(va.X, MathF.Min(vb.X, vc.X)) > maxX) continue;
                if (MathF.Max(va.Y, MathF.Max(vb.Y, vc.Y)) < minY) continue;
                if (MathF.Min(va.Y, MathF.Min(vb.Y, vc.Y)) > maxY) continue;

                dstI.Add(Map(a)); dstI.Add(Map(b)); dstI.Add(Map(c));
                if (srcC != null && dstC != null && t < srcC.Count) dstC.Add(srcC[t]);
                if (dstT != null) dstT.Add(srcT != null && t < srcT.Count && srcT[t]);
                // An unknown source is treated as a doodad, so anything that loses its
                // provenance is left out of the navmesh rather than quietly walked on.
                if (dstK != null) dstK.Add(srcK != null && t < srcK.Count ? srcK[t] : KindDoodad);
            }

            int Map(int idx)
            {
                if (remap.TryGetValue(idx, out int mapped)) return mapped;
                mapped = dstV.Count;
                dstV.Add(srcV[idx]);
                remap[idx] = mapped;
                return mapped;
            }
        }

        /// Fold another tile in. Tiles are already in world space, so this is a
        /// concatenation with index offsets and nothing else.
        public void Append(Soup other)
        {
            int vb = Verts.Count, lb = LiquidVerts.Count;
            Verts.AddRange(other.Verts);
            foreach (int i in other.Indices) Indices.Add(i + vb);
            TriTerrain.AddRange(other.TriTerrain);
            TriKind.AddRange(other.TriKind);
            LiquidVerts.AddRange(other.LiquidVerts);
            foreach (int i in other.LiquidIndices) LiquidIndices.Add(i + lb);
            LiquidTriClass.AddRange(other.LiquidTriClass);
            TerrainTris += other.TerrainTris; WmoTris += other.WmoTris;
            InteriorTris += other.InteriorTris; DoodadTris += other.DoodadTris;
        }
    }

    /// All transforms below were derived empirically against MODF extents, inter-chunk
    /// edge continuity and doodad uprightness; see notes before changing them.
    /// Roll, then pitch, then yaw: Rz(rot.Z) * Rx(rot.X) * Ry(rot.Y). Yaw comes last.
    ///
    /// Settled by --rotsearch over all 48 combinations of axis assignment, sign and order.
    /// Against MODF extents this scores 0.0001-0.0007 at every tilt magnitude, matching
    /// the 0.0003 floor that untilted placements set. The runners-up sit two orders of
    /// magnitude worse: Rz*Ry*Rx, the long-standing default here, reaches 0.0860 at 60
    /// degrees of tilt. Comparing candidates against each other had made this look like a
    /// coin flip for months — they were all wrong, which is what --extents exposed by
    /// scoring each against zero instead.
    static Matrix4x4 Rotation(Vector3 deg)
    {
        var rx = Matrix4x4.CreateRotationX(deg.X * MathF.PI / 180f);
        var ry = Matrix4x4.CreateRotationY(deg.Y * MathF.PI / 180f);
        var rz = Matrix4x4.CreateRotationZ(deg.Z * MathF.PI / 180f);
        return rz * rx * ry;
    }

    /// detailStep 1 is full resolution; higher values decimate terrain for overviews.
    /// objects: WMOs and doodads, which dominate the triangle count on busy tiles.
    public Soup BuildTile(TileFiles tile, int detailStep = 1, bool objects = true)
    {
        var soup = new Soup();

        using (var ts = casc.OpenFile((int)tile.Root))
        {
            var terrain = Adt.ReadTerrain(ts);
            foreach (var chunk in terrain.Chunks)
                Terrain.Emit(chunk, soup.Verts, soup.Indices, transpose: false, detailStep);
            soup.TerrainTris = soup.TriCount;

            if (terrain.Mh2o.Length > 0)
                Liquid.EmitMh2o(terrain.Mh2o, terrain.Chunks, soup.LiquidVerts, soup.LiquidIndices,
                                soup.LiquidTriClass, classify, transpose: false, detailStep);
        }

        if (!objects || tile.Obj0 == 0) return soup.Seal();
        if (!casc.FileExists((int)tile.Obj0)) throw new InvalidDataException($"Missing object placements {tile.Obj0} for tile {tile.X},{tile.Y}.");

        List<DoodadPlacement> doodads;
        List<WmoPlacement> wmos;
        using (var os = casc.OpenFile((int)tile.Obj0)) (doodads, wmos, _) = Obj0.Read(os);

        foreach (var m in wmos) AddWmo(soup, m, Terrain.Origin);
        soup.WmoTris = soup.TriCount - soup.TerrainTris;

        // interior doodads: the global set (0) plus whichever set this instance selects
        foreach (var m in wmos)
        {
            var (_, _, _, root) = LoadWmo(m.NameId);
            var xform = Instance(m.Pos, m.RotDeg, m.Scale, Terrain.Origin);
            foreach (var def in SelectedDoodads(root, m.DoodadSet))
            {
                var mesh = LoadM2(def.Fdid);
                if (mesh.Indices.Count == 0) continue;
                var local = Matrix4x4.CreateScale(def.Scale) *
                            Matrix4x4.CreateFromQuaternion(def.Rot) *
                            Matrix4x4.CreateTranslation(def.Pos);
                Append(soup.Verts, soup.Indices, mesh, local * xform);
            }
        }
        soup.InteriorTris = soup.TriCount - soup.TerrainTris - soup.WmoTris;

        foreach (var d in doodads)
            Append(soup.Verts, soup.Indices, LoadM2(d.NameId), Instance(d.Pos, d.RotDeg, d.Scale, Terrain.Origin));
        soup.DoodadTris = soup.TriCount - soup.TerrainTris - soup.WmoTris - soup.InteriorTris;

        return soup.Seal();
    }

    static IEnumerable<Wmo.DoodadDef> SelectedDoodads(Wmo.Root root, ushort setIndex)
    {
        foreach (int s in setIndex == 0 ? new[] { 0 } : [0, setIndex])
        {
            if (s >= root.Sets.Count) continue;
            var set = root.Sets[s];
            for (uint i = set.Start; i < set.Start + set.Count && i < root.Doodads.Count; i++)
                yield return root.Doodads[(int)i];
        }
    }

    /// Model space -> world, for one placed instance. The axis swap and the world flip
    /// are permutations expressed as matrices so a WMO-local doodad transform composes.
    /// origin is 0 for a WDT global WMO, whose space is already centred on zero.
    static Matrix4x4 Instance(Vector3 pos, Vector3 rotDeg, float scale, float origin)
    {
        var swap = new Matrix4x4(0, 0, 1, 0,
                                 1, 0, 0, 0,
                                 0, 1, 0, 0,
                                 0, 0, 0, 1);
        // (origin - p.Z, origin - p.X, p.Y). Determinant +1 — the sibling that takes
        // p.X into world X is a reflection and mirrors every town layout.
        var flip = new Matrix4x4( 0, -1, 0, 0,
                                  0,  0, 1, 0,
                                 -1,  0, 0, 0,
                                  origin, origin, 0, 1);
        return swap * Matrix4x4.CreateScale(scale) * Rotation(rotDeg) *
               Matrix4x4.CreateTranslation(pos) * flip;
    }

    /// A whole map that is one WMO: dungeons, raids, and most pre-Cata instances.
    public Soup BuildGlobalWmo(WmoPlacement m)
    {
        var soup = new Soup();
        AddWmo(soup, m, origin: 0f);
        soup.WmoTris = soup.TriCount;

        var (_, _, _, root) = LoadWmo(m.NameId);
        var xform = Instance(m.Pos, m.RotDeg, m.Scale, 0f);
        foreach (var def in SelectedDoodads(root, m.DoodadSet))
        {
            var mesh = LoadM2(def.Fdid);
            if (mesh.Indices.Count == 0) continue;
            var local = Matrix4x4.CreateScale(def.Scale) *
                        Matrix4x4.CreateFromQuaternion(def.Rot) *
                        Matrix4x4.CreateTranslation(def.Pos);
            Append(soup.Verts, soup.Indices, mesh, local * xform);
        }
        soup.InteriorTris = soup.TriCount - soup.WmoTris;
        return soup.Seal();
    }

    void AddWmo(Soup soup, WmoPlacement m, float origin)
    {
        var (solid, liquid, groupLiquid, _) = LoadWmo(m.NameId);
        var xform = Instance(m.Pos, m.RotDeg, m.Scale, origin);
        Append(soup.Verts, soup.Indices, solid, xform);

        int before = soup.LiquidIndices.Count / 3;
        Append(soup.LiquidVerts, soup.LiquidIndices, liquid, xform);
        var cls = classify((ushort)groupLiquid);
        for (int i = before; i < soup.LiquidIndices.Count / 3; i++) soup.LiquidTriClass.Add(cls);
    }

    static void Append(List<Vector3> verts, List<int> indices, Wmo.Mesh mesh, Matrix4x4 m)
    {
        if (mesh.Indices.Count == 0) return;
        int b = verts.Count;
        foreach (var v in mesh.Verts) verts.Add(Vector3.Transform(v, m));
        foreach (int i in mesh.Indices) indices.Add(b + i);
    }

    (Wmo.Mesh, Wmo.Mesh, uint, Wmo.Root) LoadWmo(uint fdid)
    {
        if (_wmoCache.TryGetValue(fdid, out var cached)) return cached;

        var verts = new List<Vector3>(); var idx = new List<int>();
        var lverts = new List<Vector3>(); var lidx = new List<int>();
        var root = new Wmo.Root([], default, default, [], [], 0);
        uint groupLiquid = 0;

        if (casc.FileExists((int)fdid))
        {
            using (var rs = casc.OpenFile((int)fdid)) root = Wmo.ReadRoot(rs);
            foreach (uint g in root.GroupIds)
            {
                if (g == 0) continue;
                if (!casc.FileExists((int)g)) throw new InvalidDataException($"Missing collision group {g} for WMO {fdid}.");
                using var gs = casc.OpenFile((int)g);
                var (solid, liquid, gl, flags, _) = Wmo.ReadGroup(gs);

                // Antiportals are invisible occlusion volumes and unreachable groups are
                // sealed off; both would bake as phantom floors and walls. They are ~11% of
                // groups but only 0.04% of triangles, so this is about correctness, not size.
                var f = (Wmo.GroupFlags)flags;
                if ((f & (Wmo.GroupFlags.Antiportal | Wmo.GroupFlags.Unreachable)) != 0)
                {
                    SkippedGroups++;
                    SkippedGroupTris += solid.TriCount;
                    continue;
                }

                if (gl != 0) groupLiquid = gl;
                int b = verts.Count;
                verts.AddRange(solid.Verts);
                foreach (int i in solid.Indices) idx.Add(b + i);
                int lb = lverts.Count;
                lverts.AddRange(liquid.Verts);
                foreach (int i in liquid.Indices) lidx.Add(lb + i);
            }
        }
        else if (fdid != 0) throw new InvalidDataException($"Missing collision WMO {fdid}.");
        return _wmoCache[fdid] = (new Wmo.Mesh(verts, idx), new Wmo.Mesh(lverts, lidx), groupLiquid, root);
    }

    Wmo.Mesh LoadM2(uint fdid)
    {
        if (_m2Cache.TryGetValue(fdid, out var cached)) return cached;
        var mesh = Wmo.Mesh.Empty;
        if (fdid != 0 && casc.FileExists((int)fdid))
        {
            using var s = casc.OpenFile((int)fdid);
            mesh = M2.ReadCollision(s);
        }
        else if (fdid != 0) throw new InvalidDataException($"Missing collision model {fdid}.");
        return _m2Cache[fdid] = mesh;
    }
}
