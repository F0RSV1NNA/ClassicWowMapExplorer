using System.Numerics;

namespace MapExtract;

/// The water itself, as a volume the navmesh does not try to describe.
///
/// A navmesh is a surface. Swimming is not: you can be anywhere in the water column, move
/// diagonally through it, and leave from any point on the shore. Two surfaces with edges
/// between them cannot express that, and trying made the route take the same hardcoded dive
/// every time, which is both ugly and wrong.
///
/// So water comes out of the navmesh and into its own layer, which is what TrinityCore does:
/// its `.map` files carry a per-ADT liquid height grid (`map_liquidHeader` — a flat
/// `liquidLevel` when the body is level, a sub-rect grid when it is not, plus the type), and
/// `GetLiquidStatus` asks it per position rather than walking a graph. Movement inside is
/// then free, bounded above by this surface and below by the navmesh's own submerged ground.
///
///     {mapId:0000}_{row:00}_{col:00}.mliq   beside the .mmtile files
///
/// Header, then either nothing (level body) or width*height floats. The level case is the
/// common one — an ocean or a lake is flat — and costs 32 bytes for a whole ADT.
public static class LiquidMap
{
    public const uint Magic = 0x51494C4D;        // "MLIQ"
    /// 2 added a kind byte per wet cell. A version 1 file has no way to say which of its
    /// cells are lava, so it is rejected rather than guessed at.
    public const int Version = 2;

    /// How finely the surface is sampled. Four yards matched the client's own liquid quads,
    /// on the reasoning that a finer grid would only interpolate between values it already
    /// has - which is true of the HEIGHTS and false of the OUTLINE. A pool a few yards
    /// across, on a sheet rotated off the axes, cannot have its edge described at 4 yd: the
    /// cell is the error, and the water visibly floods the floor around the basin.
    ///
    /// One yard, not the half yard the pool work briefly used. The overlay merges runs of
    /// equal-height cells into single quads, so a flat ocean costs a couple of triangles per
    /// row whatever the cell size - but the GRID itself still costs disk, one bit per cell
    /// plus a byte per wet cell, and Eastern Kingdoms has 619 liquid layers. Halving the
    /// cell quadruples that for a 12% tighter pool edge.
    public const float Cell = 1f;

    /// No liquid in this cell. NaN rather than a sentinel height, because any height you
    /// pick is somewhere a real lake could sit.
    public const float None = float.NaN;

    public static string FileName(int mapId, int row, int col) =>
        $"{mapId:0000}_{row:00}_{col:00}.mliq";

    public enum Kind : byte { None = 0, Water = 1, Ocean = 2, Magma = 3, Slime = 4 }

    public static Kind KindOf(LiquidClass c) => c switch
    {
        LiquidClass.Ocean => Kind.Ocean,
        LiquidClass.Magma => Kind.Magma,
        LiquidClass.Slime => Kind.Slime,
        LiquidClass.Unknown => Kind.None,
        _ => Kind.Water,
    };

    public sealed class Tile
    {
        /// World corner of the grid, and its size in cells.
        public float OriginX, OriginY;
        public int Width, Height;
        /// The cell size this tile was WRITTEN with, not whatever the constant says now.
        /// The file has always carried it; reading it back means the default can change
        /// without silently misplacing every sheet baked before the change.
        public float CellSize = Cell;

        /// Which cells hold liquid at all. Always present: without it a level body would
        /// have to claim the whole tile is water, and a lake in the middle of an ADT would
        /// drown the fields around it.
        public bool[] Wetness = [];
        /// How many of them there are.
        public int Wet;

        /// One surface height per cell, NaN where dry. Null when Level is set, because a
        /// flat body needs no grid — which is the usual case.
        public float[]? Heights;
        /// The single height of a level body.
        public float Level;
        public bool IsLevel => Heights == null;

        /// Which liquids appear anywhere in this tile, and the dominant one.
        public Kind Dominant;
        public byte KindMask;

        /// The kind in each cell. Per cell and not per tile, because an ADT can hold a pond
        /// and a lava pool at once, and calling the whole tile "mostly water" would paint
        /// the lava blue — which is the one mistake in this file that could get something
        /// killed.
        public Kind[] Kinds = [];

        /// What is in this cell: water, magma, slime, or nothing.
        public Kind KindAt(float worldX, float worldY)
        {
            int cx = (int)MathF.Floor((worldX - OriginX) / CellSize);
            int cy = (int)MathF.Floor((worldY - OriginY) / CellSize);
            if (cx < 0 || cy < 0 || cx >= Width || cy >= Height) return Kind.None;
            int i = cy * Width + cx;
            return i < Kinds.Length ? Kinds[i] : Kind.None;
        }

        public int Cells => Width * Height;

        /// The liquid surface over a world position, or NaN where there is none.
        public float SurfaceAt(float worldX, float worldY)
        {
            int cx = (int)MathF.Floor((worldX - OriginX) / CellSize);
            int cy = (int)MathF.Floor((worldY - OriginY) / CellSize);
            if (cx < 0 || cy < 0 || cx >= Width || cy >= Height) return None;
            int i = cy * Width + cx;
            if (i >= Wetness.Length || !Wetness[i]) return None;
            return IsLevel ? Level : Heights![i];
        }
    }

    /// Does the triangle touch this cell at all, seen from above?
    ///
    /// This grid used to fill each triangle's axis-aligned bounding box. A liquid triangle
    /// is half of a quad, so its box is the WHOLE quad - and a WMO sheet is usually rotated,
    /// which makes the box larger than the quad again. That is the water spreading past its
    /// pool: at (2167.7, 1009.2) the sheet read 11.5 yd square over a basin a few yards
    /// across, and the file was never wrong - its tile flags are right.
    ///
    /// Overlap rather than "covers the centre", because this grid is what gets DRAWN, and a
    /// centre test leaves holes along the edge of a pool and disagrees with the finer grid
    /// the bake marks swim floor from - which showed up as swim floor with no liquid over it
    /// at all. Separating axes is exact and cheap at this size. Paired with a 2 yd cell it
    /// trims the outline without opening the sheet.
    static bool Touches(float minX, float minY, float maxX, float maxY,
                        Vector3 a, Vector3 b, Vector3 c)
    {
        if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < minX) return false;
        if (MathF.Min(a.X, MathF.Min(b.X, c.X)) > maxX) return false;
        if (MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < minY) return false;
        if (MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) > maxY) return false;

        // The triangle's own three edge normals.
        Span<float> vx = [a.X, b.X, c.X];
        Span<float> vy = [a.Y, b.Y, c.Y];
        for (int i = 0, j = 2; i < 3; j = i++)
        {
            float ex = vx[i] - vx[j], ey = vy[i] - vy[j];
            float nx = -ey, ny = ex;                       // outward-ish normal

            float t0 = float.MaxValue, t1 = float.MinValue;
            for (int k = 0; k < 3; k++)
            {
                float d = nx * vx[k] + ny * vy[k];
                t0 = MathF.Min(t0, d); t1 = MathF.Max(t1, d);
            }

            float r0 = float.MaxValue, r1 = float.MinValue;
            for (int k = 0; k < 4; k++)
            {
                float px = (k & 1) == 0 ? minX : maxX;
                float py = (k & 2) == 0 ? minY : maxY;
                float d = nx * px + ny * py;
                r0 = MathF.Min(r0, d); r1 = MathF.Max(r1, d);
            }

            if (t1 < r0 || r1 < t0) return false;
        }
        return true;
    }

    /// Sample a tile's liquid from the triangles the extractor already read.
    ///
    /// Highest surface wins where two overlap, which is the same rule the bake uses to
    /// decide what a floor is under: a bridge over a pond leaves the pond's surface intact,
    /// and a pond on a roof sits above the sea.

    public static Tile? Build(Extractor.Soup soup, NavBake.Clip rect)
    {
        if (soup.LiquidTris == 0) return null;

        int w = (int)MathF.Ceiling((rect.MaxX - rect.MinX) / Cell);
        int h = (int)MathF.Ceiling((rect.MaxY - rect.MinY) / Cell);
        if (w <= 0 || h <= 0) return null;

        var heights = new float[w * h];
        Array.Fill(heights, None);
        var kinds = new Kind[w * h];
        var seen = new int[5];

        for (int t = 0; t < soup.LiquidTris; t++)
        {
            var a = soup.LiquidVerts[soup.LiquidIndices[t * 3]];
            var b = soup.LiquidVerts[soup.LiquidIndices[t * 3 + 1]];
            var c = soup.LiquidVerts[soup.LiquidIndices[t * 3 + 2]];
            var kind = KindOf(t < soup.LiquidTriClass.Count ? soup.LiquidTriClass[t] : LiquidClass.Water);
            if (kind == Kind.None) continue;
            float z = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));

            int x0 = (int)MathF.Floor((MathF.Min(a.X, MathF.Min(b.X, c.X)) - rect.MinX) / Cell);
            int x1 = (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - rect.MinX) / Cell);
            int y0 = (int)MathF.Floor((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - rect.MinY) / Cell);
            int y1 = (int)MathF.Floor((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - rect.MinY) / Cell);

            for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                {
                    if (!Touches(rect.MinX + x * Cell, rect.MinY + y * Cell,
                                 rect.MinX + (x + 1) * Cell, rect.MinY + (y + 1) * Cell,
                                 a, b, c)) continue;
                    int i = y * w + x;
                    if (!float.IsNaN(heights[i]) && heights[i] >= z) continue;
                    heights[i] = z;
                    kinds[i] = kind;
                }
        }

        int filled = 0;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < heights.Length; i++)
        {
            if (float.IsNaN(heights[i])) continue;
            filled++;
            lo = MathF.Min(lo, heights[i]);
            hi = MathF.Max(hi, heights[i]);
            seen[(int)kinds[i]]++;
        }
        if (filled == 0) return null;

        byte mask = 0;
        for (int k = 1; k < seen.Length; k++) if (seen[k] > 0) mask |= (byte)(1 << (k - 1));
        var dominant = (Kind)Array.IndexOf(seen, seen.Skip(1).Max());

        var wetness = new bool[heights.Length];
        for (int i = 0; i < heights.Length; i++) wetness[i] = !float.IsNaN(heights[i]);

        var tile = new Tile
        {
            OriginX = rect.MinX, OriginY = rect.MinY, Width = w, Height = h,
            Dominant = dominant, KindMask = mask, Wetness = wetness, Kinds = kinds,
        };

        // Level across the WET cells, not across the whole tile.
        //
        // The first cut asked whether every cell in the grid had the same height, which no
        // coastal tile can ever satisfy - a quarter of it is water and the rest is dry - so
        // the optimisation never once fired and every tile stored a full grid. What matters
        // is whether the water is flat, and almost always it is: an ocean, a lake, a pool.
        const float Flat = 0.05f;
        tile.Wet = filled;
        if (hi - lo <= Flat) tile.Level = (lo + hi) * 0.5f;
        else tile.Heights = heights;
        return tile;
    }

    /// Coverage always, heights only where the water is not level, and then only for the
    /// cells that actually hold any. A quarter-wet coastal tile stored a full 17,956-float
    /// grid before this — 70 KB of mostly NaN.
    public static int Write(string dir, int mapId, int row, int col, Tile tile)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName(mapId, row, col));
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(Magic);
        w.Write(Version);
        w.Write(tile.OriginX);
        w.Write(tile.OriginY);
        w.Write(Cell);
        w.Write(tile.Width);
        w.Write(tile.Height);
        w.Write((byte)tile.Dominant);
        w.Write(tile.KindMask);
        w.Write(tile.IsLevel);
        w.Write(tile.Level);
        w.Write(tile.Wet);

        for (int i = 0; i < tile.Wetness.Length; i += 8)
        {
            byte bits = 0;
            for (int b = 0; b < 8 && i + b < tile.Wetness.Length; b++)
                if (tile.Wetness[i + b]) bits |= (byte)(1 << b);
            w.Write(bits);
        }

        if (tile.Heights != null)
            for (int i = 0; i < tile.Heights.Length; i++)
                if (tile.Wetness[i]) w.Write(tile.Heights[i]);

        // one byte per wet cell, so a lava pool beside a pond stays lava
        for (int i = 0; i < tile.Wetness.Length; i++)
            if (tile.Wetness[i]) w.Write((byte)(i < tile.Kinds.Length ? tile.Kinds[i] : Kind.Water));

        return (int)fs.Length;
    }

    public static Tile? Read(string dir, int mapId, int row, int col)
    {
        var path = Path.Combine(dir, FileName(mapId, row, col));
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);
        if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) return null;

        var tile = new Tile { OriginX = r.ReadSingle(), OriginY = r.ReadSingle() };
        tile.CellSize = r.ReadSingle();
        tile.Width = r.ReadInt32();
        tile.Height = r.ReadInt32();
        tile.Dominant = (Kind)r.ReadByte();
        tile.KindMask = r.ReadByte();
        bool level = r.ReadBoolean();
        tile.Level = r.ReadSingle();
        tile.Wet = r.ReadInt32();

        int cells = tile.Width * tile.Height;
        var wetness = new bool[cells];
        for (int i = 0; i < cells; i += 8)
        {
            byte bits = r.ReadByte();
            for (int b = 0; b < 8 && i + b < cells; b++) wetness[i + b] = (bits & (1 << b)) != 0;
        }
        tile.Wetness = wetness;

        if (!level)
        {
            var heights = new float[cells];
            Array.Fill(heights, None);
            for (int i = 0; i < cells; i++) if (wetness[i]) heights[i] = r.ReadSingle();
            tile.Heights = heights;
        }

        var kinds = new Kind[cells];
        for (int i = 0; i < cells; i++) if (wetness[i]) kinds[i] = (Kind)r.ReadByte();
        tile.Kinds = kinds;
        return tile;
    }
}
