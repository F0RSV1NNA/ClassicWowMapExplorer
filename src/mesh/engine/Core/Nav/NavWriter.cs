using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace MapExtract;

/// Writes baked tiles in TrinityCore's mmap layout.
///
///   {mapId:000}.mmap              dtNavMeshParams for the whole map
///   {mapId:000}{tx:00}{ty:00}.mmtile   header + one dtMeshData blob
///
/// The tile payload is Detour's own C-struct serialisation (DtMeshDataWriter with
/// cCompatibility), which is byte-identical to what a C++ dtNavMesh expects, so these
/// load in anything that reads TrinityCore mmaps.
///
/// One difference worth knowing: TrinityCore puts exactly one Detour tile per ADT.
/// We subdivide, because a dense ADT overruns Recast's 16-bit polymesh vertex limit.
/// So a single ADT produces several .mmtile files, named by Detour tile index.
public static class NavWriter
{
    const uint MmapMagic = 0x4D4D4150;      // 'MMAP'
    const uint DtVersion = 7;               // DT_NAVMESH_VERSION
    const uint MmapVersion = 16;            // matches the mmaps in C:\cmods\mmaps

    public static string MapFile(int mapId) => $"{mapId:0000}.mmap";
    public static string TileFile(int mapId, int tx, int tz) => $"{mapId:0000}_{tx:00}_{tz:00}.mmtile";

    /// magic + version + dtNavMeshParams + padding = 40 bytes, same as a TC .mmap.
    public static void WriteMapParams(string dir, int mapId, DtNavMeshParams p)
    {
        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, MapFile(mapId)));
        using var w = new BinaryWriter(fs);
        w.Write(MmapMagic);
        w.Write(MmapVersion);
        w.Write(p.orig.X); w.Write(p.orig.Y); w.Write(p.orig.Z);
        w.Write(p.tileWidth);
        w.Write(p.tileHeight);
        w.Write(p.maxTiles);
        w.Write(p.maxPolys);
        w.Write(0);                          // padding, as TC has
    }

    /// One tile. Returns the bytes written.
    public static int WriteTile(string dir, int mapId, NavBake.NavTile tile, bool usesLiquids = true)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, TileFile(mapId, tile.Row, tile.Col));

        // serialise the tile first: the header needs its size
        byte[] payload;
        using (var ms = new MemoryStream())
        {
            using var bw = new BinaryWriter(ms);
            new DtMeshDataWriter().Write(bw, tile.Data, RcByteOrder.LITTLE_ENDIAN, true);
            payload = ms.ToArray();
        }

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(MmapMagic);
        w.Write(DtVersion);
        w.Write(MmapVersion);
        w.Write((uint)payload.Length);
        w.Write(usesLiquids);
        w.Write(new byte[3]);                 // padding to 4-byte alignment
        w.Write(payload);
        return payload.Length + 20;
    }

    /// Read the files back and assemble a real navmesh, proving the output is loadable
    /// rather than merely written. Returns (tiles loaded, polygons, any error).
    /// Every .mmtile of a map, assembled into a live navmesh.
    ///
    /// This is the same work LoadBack has always done - it built exactly this and then threw
    /// it away, keeping only the counts. Handing the mesh back instead lets the viewer draw a
    /// navmesh that was baked in some earlier session, which until now it could not: the
    /// overlay could only ever show a bake still sitting in memory from this run.
    public static (DtNavMesh? Mesh, int Tiles, int Polys, string Error) LoadMesh(string dir, int mapId)
    {
        var mapPath = Path.Combine(dir, MapFile(mapId));
        if (!File.Exists(mapPath)) return (null, 0, 0, "no .mmap file");

        DtNavMeshParams p;
        using (var fs = File.OpenRead(mapPath))
        using (var br = new BinaryReader(fs))
        {
            if (br.ReadUInt32() != MmapMagic) return (null, 0, 0, "bad .mmap magic");
            br.ReadUInt32();                 // version
            p = new DtNavMeshParams
            {
                orig = new RcVec3f(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                tileWidth = br.ReadSingle(),
                tileHeight = br.ReadSingle(),
                maxTiles = br.ReadInt32(),
                maxPolys = br.ReadInt32(),
            };
        }

        var navMesh = new DtNavMesh();
        if (navMesh.Init(in p, 6).Failed()) return (null, 0, 0, "navmesh init failed");

        int tiles = 0, polys = 0;
        var reader = new DtMeshDataReader();

        foreach (var file in Directory.GetFiles(dir, $"{mapId:0000}_*.mmtile"))
        {
            using var fs = File.OpenRead(file);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != MmapMagic) return (null, tiles, polys, $"bad magic in {Path.GetFileName(file)}");
            br.ReadUInt32();                       // dt version
            br.ReadUInt32();                       // mmap version
            br.ReadUInt32();                       // payload size
            br.ReadBoolean(); br.ReadBytes(3);     // usesLiquids + padding

            DtMeshData data;
            try { data = reader.Read(br, 6); }
            catch (Exception e) { return (null, tiles, polys, $"{Path.GetFileName(file)}: {e.Message}"); }

            if (navMesh.AddTile(data, 0, 0, out _).Failed())
                return (null, tiles, polys, $"AddTile rejected {Path.GetFileName(file)}");

            tiles++;
            polys += data.header?.polyCount ?? 0;
        }
        return (navMesh, tiles, polys, "");
    }

    /// Read the tiles one at a time, without building a navmesh.
    ///
    /// `LoadMesh` puts every tile into one `DtNavMesh`, and for a continent that is several
    /// gigabytes before anything is drawn - DotRecast's `DtPoly` is a CLASS holding two
    /// `int[]` fields, so a map with millions of polygons becomes millions of small objects,
    /// each with its own headers. Eastern Kingdoms is 736 tiles and 2.0 GB on disk, and
    /// opening it in the viewer throws OutOfMemoryException before a triangle is uploaded.
    ///
    /// Drawing needs the detail triangles and their flags, not the graph. Those can be taken
    /// a tile at a time and released, which keeps the resident cost at one tile.
    public static IEnumerable<DtMeshData> StreamTiles(string dir, int mapId)
    {
        var reader = new DtMeshDataReader();
        foreach (var file in Directory.GetFiles(dir, $"{mapId:0000}_*.mmtile"))
        {
            DtMeshData data;
            using (var fs = File.OpenRead(file))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadUInt32() != MmapMagic) continue;
                br.ReadUInt32();                       // dt version
                br.ReadUInt32();                       // mmap version
                br.ReadUInt32();                       // payload size
                br.ReadBoolean(); br.ReadBytes(3);     // usesLiquids + padding
                data = reader.Read(br, 6);
            }
            yield return data;
        }
    }

    /// Do the tiles on disk load back into Detour cleanly? The check written after a bake,
    /// which wants the verdict and not the mesh.
    public static (int Tiles, int Polys, string Error) LoadBack(string dir, int mapId)
    {
        var (_, tiles, polys, err) = LoadMesh(dir, mapId);
        return (tiles, polys, err);
    }

    public static (int Files, long Bytes) WriteAll(string dir, int mapId, NavBake.Result r)
    {
        WriteMapParams(dir, mapId, r.MeshParams);
        long total = 0;
        foreach (var t in r.Tiles) total += WriteTile(dir, mapId, t);
        return (r.Tiles.Count, total);
    }
}
