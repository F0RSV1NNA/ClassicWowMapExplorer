using System.Numerics;
using System.Text.Json;

namespace MapExtract;

/// Everything a person marks by hand in the viewer, per map, plus the choices that are not
/// per map. Written only by the viewer - nothing here is meant to be opened in an editor;
/// adding and removing is done in the viewer's EDIT panel.
///
///     edits\0000.json      one file per map, beside the executable
///     edits\global.json    road-texture choices
///
/// The bake reads the same files, so what is marked in the viewer is what gets baked.
public static class MapEdits
{
    /// A connection that is not walkable surface - a portal, a lift, a teleport pad. Both
    /// ends on the same map; a crossing with a loading screen is a different navmesh, and a
    /// boat ride is the transport's own mesh.
    public sealed class Link
    {
        public float Ax { get; set; }
        public float Ay { get; set; }
        public float Az { get; set; }
        public float Bx { get; set; }
        public float By { get; set; }
        public float Bz { get; set; }
        public float Radius { get; set; } = 2f;
        public bool Bidirectional { get; set; } = true;
        public string Note { get; set; } = "";
        public string Mode { get; set; } = "walk";
        public int UserId { get; set; }

        public Vector3 A => new(Ax, Ay, Az);
        public Vector3 B => new(Bx, By, Bz);
    }

    public enum StrokeKind { PaintRoad = 0, RemoveRoad = 1, EraseMesh = 2, Block = 3, Restore = 4 }

    /// A dab of the brush. Later strokes win over earlier ones where they overlap, so a
    /// remove after a paint takes it back out.
    ///
    /// A stroke acts on the surface it was placed on - within `StrokeBand` of its height -
    /// and not on whatever else lies in the same column. Painting a bridge deck marks the
    /// deck, not the river bed under it; erasing a balcony leaves the floor below.
    public sealed class Stroke
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Radius { get; set; } = 3f;
        public StrokeKind Kind { get; set; }
        public float HeightBand { get; set; } = .75f;

        /// Files written before `Kind` existed marked a road removal as Erase = true.
        /// Read so that meaning is kept - an old eraser stroke must not start deleting mesh.
        public bool Erase { get; set; }

        public StrokeKind Effective => Erase && Kind == StrokeKind.PaintRoad ? StrokeKind.RemoveRoad : Kind;
    }

    /// Yards above or below a stroke's height that it still reaches.
    public const float StrokeBand = 3f;

    public sealed class Map
    {
        public List<Link> OffMesh { get; set; } = [];
        public List<Stroke> Roads { get; set; } = [];
    }

    /// Road-texture overrides by FileDataID, on top of the automatic name rule. Global,
    /// because a texture is a road or not wherever it is painted.
    public sealed class Global
    {
        public List<int> RoadOn { get; set; } = [];
        public List<int> RoadOff { get; set; } = [];
    }

    /// Beside the executable, but NOT inside mmaps: a map bake from the command line deletes
    /// that folder, and these are hours of hand work.
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "edits");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static string MapPath(int mapId) => Path.Combine(Folder, $"{mapId:0000}.json");
    static string GlobalPath => Path.Combine(Folder, "global.json");

    public static Map Load(int mapId) => Read<Map>(MapPath(mapId)) ?? new Map();
    public static Global LoadGlobal() => Read<Global>(GlobalPath) ?? new Global();

    public static void Save(int mapId, Map m) => Write(MapPath(mapId), m);
    public static void SaveGlobal(Global g) => Write(GlobalPath, g);

    static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch { return null; }
    }

    static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Folder);
        // Written aside and moved into place, so a crash mid-write cannot leave half a file
        // where an afternoon of marking used to be.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// The links in the form the bake takes.
    public static List<OffMeshLinks.Link> Links(int mapId) =>
        [.. Load(mapId).OffMesh.Select(l => new OffMeshLinks.Link(mapId, l.A, l.B, l.Radius, l.Bidirectional))];
}
