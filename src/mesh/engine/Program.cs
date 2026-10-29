using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using MapExtract;

// Browser adapter. Core source and reviewed Explorer extensions are tracked in provenance.json.
public partial class MeshEngine
{
    public static void Main() { }
    [JSExport] public static string Version() => "explorer-recast-2026.3.14";

    [JSExport]
    public static byte[] Bake(byte[] input)
    {
        using var reader = new BinaryReader(new MemoryStream(input));
        if (reader.ReadUInt32() != 0x31424D45) throw new InvalidDataException("Unsupported Explorer bake input");
        int map = reader.ReadInt32(), x = reader.ReadInt32(), y = reader.ReadInt32();
        var origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var soup = new Extractor.Soup();
        ReadGeometry(reader, origin, soup, false);
        ReadGeometry(reader, origin, soup, true);
        var edits=ReadEdits(reader);
        var roads=RoadMask.Build(_=>null,[],edits.Map,edits.Global);
        return BakeSoup(soup, map, x, y,roads,edits.Map);
    }

    [JSExport]
    public static byte[] BakeRaw(byte[] input) => BakeRawMode(input, false);

    [JSExport]
    public static byte[] BakeRawLowMemory(byte[] input) => BakeRawMode(input, true);

    static byte[] BakeRawMode(byte[] input, bool lowMemory)
    {
        Stage(lowMemory ? "Reading source archive (low-memory recovery)" : "Reading source archive");
        var prepared = PrepareRaw(input, lowMemory);
        input = null!;
        // Drop source-file copies and neighbour extraction temporaries before Recast.
        GC.Collect();
        return BakeSoup(prepared.Soup, prepared.Map, prepared.X, prepared.Y, prepared.Roads,prepared.Edits);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (Extractor.Soup Soup, RoadMask Roads, int Map, int X, int Y, MapEdits.Map Edits) PrepareRaw(byte[] input, bool lowMemory, bool texturesOnly=false)
    {
        using var r = new BinaryReader(new MemoryStream(input));
        if (r.ReadUInt32() != 0x32524D45) throw new InvalidDataException("Unsupported raw source input");
        int map = r.ReadInt32(), x = r.ReadInt32(), y = r.ReadInt32();
        int count = r.ReadInt32();
        var files = new Dictionary<int, byte[]>();
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadInt32(), size = r.ReadInt32();
            if (size < 0 || size > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid source file size");
            files.Add(id, r.ReadBytes(size));
        }
        var banks = new Dictionary<ushort, LiquidClass>();
        count = r.ReadInt32();
        for (int i = 0; i < count; i++) banks.Add(r.ReadUInt16(), (LiquidClass)r.ReadByte());
        var source = lowMemory ? null : new Extractor(new CASCLib.CASCHandler(files), id => banks.GetValueOrDefault(id, LiquidClass.Unknown));
        var tiles = new List<TileFiles>();
        count = r.ReadInt32();
        for (int i = 0; i < count; i++) tiles.Add(new TileFiles(r.ReadInt32(), r.ReadInt32(), r.ReadUInt32(), r.ReadUInt32(), 0, r.ReadUInt32(), 0));
        var edits=ReadEdits(r);
        var clip = NavBake.Clip.ForAdt(y, x);
        var soup = new Extractor.Soup();
        // Same extraction and clipping operations as Mappster, without JS geometry transforms.
        foreach (var tile in (texturesOnly?Enumerable.Empty<TileFiles>():tiles.OrderBy(t => t.Y).ThenBy(t => t.X)))
        {
            Stage($"Extracting source {tile.X}_{tile.Y}" + (lowMemory ? " (low-memory recovery)" : ""));
            AppendSource(files, banks, source, tile, soup, clip, x, y);
            // The no-inline scope releases the large part and, in recovery, its model
            // cache before advancing. Geometry, order and clipping remain identical.
            if (lowMemory) GC.Collect();
        }
        using (var resource = typeof(MeshEngine).Assembly.GetManifestResourceStream("engine.tilesets.csv")
            ?? typeof(MeshEngine).Assembly.GetManifestResourceStream("check.tilesets.csv")
            ?? throw new InvalidDataException("Missing bundled Mappster tileset names"))
        using (var names = new StreamReader(resource)) Names.LoadTilesets(names);
        var roads = RoadMask.Build(id => files.GetValueOrDefault(id), tiles, edits.Map,edits.Global);
        return (soup, roads, map, x, y,edits.Map);
    }

    [JSExport]
    public static string InspectRoad(byte[] input,double x,double y)
    {
        var p=PrepareRaw(input,true,true);var (id,strength)=p.Roads.TextureAt((float)x,(float)y);
        using var stream=new MemoryStream();using(var w=new Utf8JsonWriter(stream))
        { w.WriteStartObject();w.WriteNumber("id",id);w.WriteNumber("strength",strength);w.WriteString("name",Names.Tileset(id));w.WriteBoolean("road",p.Roads.IsRoad((float)x,(float)y));
          w.WriteStartArray("layers");foreach(var layer in p.Roads.LayersAt((float)x,(float)y)){w.WriteStartObject();w.WriteNumber("id",layer.Id);w.WriteNumber("weight",layer.Weight);w.WriteString("name",Names.Tileset(layer.Id));w.WriteBoolean("automatic",RoadMask.RoadByName(Names.Tileset(layer.Id)));w.WriteEndObject();}w.WriteEndArray();w.WriteEndObject(); }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    static BakeEdits ReadEdits(BinaryReader reader)
    {
        if(reader.BaseStream.Position==reader.BaseStream.Length)return new();
        int length=reader.ReadInt32();
        if(length<0||length>16*1024*1024||length!=reader.BaseStream.Length-reader.BaseStream.Position)throw new InvalidDataException("Invalid authoring payload");
        var edits=JsonSerializer.Deserialize(reader.ReadBytes(length),EngineJson.Default.BakeEdits)??throw new InvalidDataException("Missing authoring data");
        if(edits.Map.Roads.Count>100000||edits.Map.OffMesh.Count>10000)throw new InvalidDataException("Authoring limit exceeded");
        foreach(var s in edits.Map.Roads)
            if(!float.IsFinite(s.X)||!float.IsFinite(s.Y)||!float.IsFinite(s.Z)||!float.IsFinite(s.Radius)||s.Radius<.25f||s.Radius>100||!float.IsFinite(s.HeightBand)||s.HeightBand<.1f||s.HeightBand>10||!Enum.IsDefined(s.Kind))throw new InvalidDataException("Invalid brush stroke");
        foreach(var link in edits.Map.OffMesh)
            if(!float.IsFinite(link.Ax)||!float.IsFinite(link.Ay)||!float.IsFinite(link.Az)||!float.IsFinite(link.Bx)||!float.IsFinite(link.By)||!float.IsFinite(link.Bz)||!float.IsFinite(link.Radius)||link.Radius<.1f||link.Radius>20||link.Mode is not ("walk" or "portal" or "elevator"))throw new InvalidDataException("Invalid authoring link");
        return edits;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void AppendSource(Dictionary<int, byte[]> files, Dictionary<ushort, LiquidClass> banks,
        Extractor? cached, TileFiles tile, Extractor.Soup soup, NavBake.Clip clip, int x, int y)
    {
        var source = cached ?? new Extractor(new CASCLib.CASCHandler(files), id => banks.GetValueOrDefault(id, LiquidClass.Unknown));
        var part = source.BuildTile(tile);
        if (tile.X == y && tile.Y == x) soup.Append(part);
        else soup.AppendClipped(part, clip.MinX - 8, clip.MinY - 8, clip.MaxX + 8, clip.MaxY + 8);
    }

    [JSImport("globalThis.meshBakeStage")]
    static partial void PostStage(string text, double managedMB);

    static void Stage(string text)
    {
        if (OperatingSystem.IsBrowser())
            try { PostStage(text, GC.GetTotalMemory(false) / 1048576.0); }
            catch { /* Optional worker telemetry, absent in native/reference harnesses. */ }
        else if (Environment.GetEnvironmentVariable("EXPLORER_MESH_PROGRESS") == "1")
            Console.Error.WriteLine($"{text}; managed heap {GC.GetTotalMemory(false) / 1048576} MB");
    }

    static byte[] BakeSoup(Extractor.Soup soup, int map, int x, int y, RoadMask? roads = null,MapEdits.Map? edits=null)
    {
        var settings = new NavBake.Settings();
        if(edits!=null)settings.OffMeshLinks=[.. edits.OffMesh.Select(l=>new OffMeshLinks.Link(map,l.A,l.B,l.Radius,l.Bidirectional,l.Mode,l.UserId))];
        var clip = NavBake.Clip.ForAdt(y, x); // Explorer X is WoW-Y column; Mappster X is WoW-X row.
        Stage($"Recast bake: {soup.TriCount:N0} collision triangles");
        var result = NavBake.Bake(soup, settings, clip: clip, roads: roads);
        if (!result.Ok || result.FailedTiles != 0)
            throw new InvalidOperationException($"{result.Message}; {result.FailMessage}; failed tiles: {result.FailedTiles}");

        // The browser's virtual filesystem keeps the ORIGINAL writers unchanged.
        var dir = Path.Combine(Path.GetTempPath(), "mesh-bake-" + Guid.NewGuid().ToString("N"));
        try
        {
            Stage("Writing native tiles");
            NavWriter.WriteAll(dir, map, result);
            if(edits?.OffMesh.Count>0)
            {
                var owned=new MapEdits.Map{OffMesh=[..edits.OffMesh.Where(l=>l.Ax>=clip.MinX&&l.Ax<clip.MaxX&&l.Ay>=clip.MinY&&l.Ay<clip.MaxY)]};
                if(owned.OffMesh.Count>0)File.WriteAllText(Path.Combine(dir,$"{map:0000}_{y:00}_{x:00}.links.json"),JsonSerializer.Serialize(owned,EngineJson.Default.Map));
            }
            Stage("Building liquid volume");
            var liquid = LiquidMap.Build(soup, clip);
            if (liquid != null) LiquidMap.Write(dir, map, y, x, liquid);
            Stage("Validating native tiles");
            var check = NavWriter.LoadBack(dir, map);
            if (check.Error.Length != 0 || check.Tiles != result.Tiles.Count || check.Polys != result.PolyCount)
                throw new InvalidDataException($"Mappster output loadback failed: {check.Error}");
            Stage("Serializing mesh and preview");
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var file in Directory.GetFiles(dir).Order())
                {
                    using var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.NoCompression).Open();
                    entry.Write(File.ReadAllBytes(file));
                }
                using (var preview = new BinaryWriter(zip.CreateEntry("preview.bin", CompressionLevel.NoCompression).Open()))
                {
                    preview.Write(result.DebugAreas.Count);
                    for (int t = 0; t < result.DebugAreas.Count; t++)
                    {
                        preview.Write(result.DebugAreas[t]);
                        for (int k = 0; k < 3; k++)
                        {
                            var v = result.DebugVerts[result.DebugIndices[t * 3 + k]];
                            preview.Write(v.X); preview.Write(v.Y); preview.Write(v.Z);
                        }
                    }
                }
                using (var preview = new BinaryWriter(zip.CreateEntry("liquid-preview.bin", CompressionLevel.NoCompression).Open()))
                {
                    preview.Write(soup.LiquidTris);
                    for (int t = 0; t < soup.LiquidTris; t++)
                    {
                        preview.Write(soup.LiquidTriClass[t] switch { LiquidClass.Water => 9, LiquidClass.Ocean => 6, LiquidClass.Magma => 8, LiquidClass.Slime => 7, _ => 0 });
                        for (int k = 0; k < 3; k++)
                        {
                            var v = soup.LiquidVerts[soup.LiquidIndices[t * 3 + k]];
                            preview.Write(v.X); preview.Write(v.Y); preview.Write(v.Z);
                        }
                    }
                }
                using var metadata = zip.CreateEntry("engine.json").Open();
                using var json = new Utf8JsonWriter(metadata);
                json.WriteStartObject();
                json.WriteString("pipeline", Version());
                json.WriteNumber("milliseconds", result.TotalMs);
                json.WritePropertyName("settings");
                JsonSerializer.Serialize(json, settings, EngineJson.Default.Settings);
                json.WriteStartObject("metrics");
                WriteMetrics(json, result);
                json.WriteEndObject();
                json.WriteEndObject();
            }
            return output.ToArray();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    static void ReadGeometry(BinaryReader r, Vector3 origin, Extractor.Soup soup, bool liquid)
    {
        int verts = r.ReadInt32(), indices = r.ReadInt32();
        if (verts < 0 || indices < 0 || indices % 3 != 0 ||
            (long)verts * 12 + (long)indices * 4 + indices / 3 > r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException("Invalid geometry dimensions");
        var positions = liquid ? soup.LiquidVerts : soup.Verts;
        var triangles = liquid ? soup.LiquidIndices : soup.Indices;
        for (int i = 0; i < verts; i++)
        {
            float a = r.ReadSingle(), b = r.ReadSingle(), c = r.ReadSingle();
            if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c)) throw new InvalidDataException("Nonfinite geometry");
            positions.Add(new Vector3(c + origin.Z, a + origin.X, b + origin.Y));
        }
        for (int i = 0; i < indices; i++)
        {
            int v = r.ReadInt32();
            if ((uint)v >= verts) throw new InvalidDataException("Triangle index outside geometry");
            triangles.Add(v);
        }
        for (int t = 0; t < indices / 3; t++)
        {
            byte kind = r.ReadByte();
            if (liquid)
            {
                if (kind < 1 || kind > 4) throw new InvalidDataException("Unknown liquid classification");
                soup.LiquidTriClass.Add((LiquidClass)(kind - 1));
            }
            else
            {
                if (kind > Extractor.Soup.KindDoodad) throw new InvalidDataException("Unknown collision source");
                soup.TriKind.Add(kind); soup.TriTerrain.Add(kind == Extractor.Soup.KindTerrain);
                if (kind == 0) soup.TerrainTris++; else if (kind == 1) soup.WmoTris++; else soup.DoodadTris++;
            }
        }
    }

    static void WriteMetrics(Utf8JsonWriter w, NavBake.Result r)
    {
        w.WriteNumber("polygons", r.PolyCount); w.WriteNumber("tiles", r.TileCount);
        w.WriteNumber("regionsSplit", r.RegionsSplit); w.WriteNumber("pinholesFilled", r.Pinholes);
        w.WriteNumber("detailRetriangulated", r.DetailRetriangulated); w.WriteNumber("detailSpikes", r.DetailSpikes);
        w.WriteNumber("detailBlades", r.DetailBlades); w.WriteNumber("detailClamped", r.DetailOutOfBand);
        w.WriteNumber("longDetailEdges", r.LongDetailEdges); w.WriteNumber("regionSplitGrid", r.WidestSplitGrid);
        w.WriteNumber("regionsUnwound", r.RegionsUnwound); w.WriteNumber("tinyRegionCells", r.TinyRegionCells); w.WriteNumber("areaIslandCellsRestored", r.AreaIslandCellsRestored);
        w.WriteNumber("indoorCells", r.IndoorCells);
        w.WriteNumber("trimCells", r.TrimCells); w.WriteNumber("roadCells", r.RoadCells);
        w.WriteNumber("railingCells", r.RailingCells); w.WriteNumber("asymmetricLinks", r.OneWayLinks);
        w.WriteNumber("doodadSpansReblocked", r.DoodadSpansReblocked);
        w.WriteNumber("sourceSurfaceVertices", r.SourceSurfaceVertices);
        w.WriteNumber("planarDetailVerticesRemoved", r.PlanarDetailVerticesRemoved);
        w.WriteNumber("sourceWmoCapsBlocked",r.SourceWmoCapsBlocked);
        w.WriteNumber("solidWallCells",r.SolidWallCells);
        w.WriteNumber("raisedWmoCapsBlocked", r.RaisedWmoCapsBlocked);
        w.WriteNumber("swimPolygons", r.SwimPolys); w.WriteNumber("submergedPolygons", r.SubmergedPolys);
        w.WriteNumber("hazardPolygons", r.UnderHazardPolys); w.WriteNumber("waterlineCuts", r.WaterlineCuts);
        w.WriteNumber("coarserContours", r.CoarserContours); w.WriteNumber("monotoneTiles", r.MonotoneTiles);
        w.WriteNumber("splitGaveUp", r.SplitGaveUp); w.WriteNumber("swimBuildFellBack", r.SwimBuildFellBack);
        w.WriteString("fallbackWhy", r.FallbackWhy);
        w.WriteStartObject("stages"); w.WriteNumber("rasterize", r.RasterizeMs);
        w.WriteNumber("build", r.BuildMs); w.WriteNumber("detour", r.DetourMs); w.WriteEndObject();
    }
}

[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(NavBake.Settings))]
[JsonSerializable(typeof(BakeEdits))]
[JsonSerializable(typeof(MapEdits.Map))]
[JsonSerializable(typeof(MapEdits.Link), TypeInfoPropertyName="AuthoringLink")]
[JsonSerializable(typeof(List<MapEdits.Link>), TypeInfoPropertyName="AuthoringLinks")]
[JsonSerializable(typeof(OffMeshLinks.Link), TypeInfoPropertyName="NavigationLink")]
[JsonSerializable(typeof(List<OffMeshLinks.Link>), TypeInfoPropertyName="NavigationLinks")]
internal partial class EngineJson : JsonSerializerContext { }

public sealed class BakeEdits
{
    public MapEdits.Map Map { get; set; }=new();
    public MapEdits.Global Global { get; set; }=new();
}
