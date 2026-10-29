using System.Numerics;
using System.Text;

namespace MapExtract;

/// M2s are chunked since Legion: the legacy MD20 blob lives inside an MD21 chunk,
/// and every offset in its header is relative to the start of that blob.
public static class M2
{
    // MD20 header, version >= 264 (no ofsViews field).
    const int OfsBoundingTriangles = 0xD8;
    const int OfsBoundingVertices  = 0xE0;

    public static Wmo.Mesh ReadCollision(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        byte[] data = ms.ToArray();
        if (data.Length < 8) throw new InvalidDataException("Truncated collision model header.");

        int md20 = 0;
        if (Encoding.ASCII.GetString(data, 0, 4) == "MD21") md20 = 8;
        else if (Encoding.ASCII.GetString(data, 0, 4) != "MD20") throw new InvalidDataException("Unsupported collision model header.");

        if (md20 + 0xE8 > data.Length) throw new InvalidDataException("Truncated collision model arrays.");

        int nTris = BitConverter.ToInt32(data, md20 + OfsBoundingTriangles);
        int oTris = BitConverter.ToInt32(data, md20 + OfsBoundingTriangles + 4);
        int nVerts = BitConverter.ToInt32(data, md20 + OfsBoundingVertices);
        int oVerts = BitConverter.ToInt32(data, md20 + OfsBoundingVertices + 4);

        // Most doodads carry no collision mesh at all; that is the filter we want.
        if (nTris < 0 || nVerts < 0 || nTris % 3 != 0) throw new InvalidDataException("Invalid collision model counts.");
        if (nTris == 0) return Wmo.Mesh.Empty;
        if (nVerts == 0 || !InRange((long)md20 + oTris, (long)nTris * 2, data.Length) ||
            !InRange((long)md20 + oVerts, (long)nVerts * 12, data.Length)) throw new InvalidDataException("Collision model arrays outside source file.");

        var verts = new List<Vector3>(nVerts);
        for (int i = 0; i < nVerts; i++)
        {
            int o = md20 + oVerts + i * 12;
            var v = new Vector3(BitConverter.ToSingle(data, o),
                                  BitConverter.ToSingle(data, o + 4),
                                  BitConverter.ToSingle(data, o + 8));
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new InvalidDataException("Nonfinite collision model vertex.");
            verts.Add(v);
        }

        var idx = new List<int>(nTris);
        for (int i = 0; i < nTris; i++)
        {
            int v = BitConverter.ToUInt16(data, md20 + oTris + i * 2);
            if (v >= nVerts) throw new InvalidDataException("Collision model index outside vertex array.");
            idx.Add(v);
        }

        return new Wmo.Mesh(verts, idx);
    }

    static bool InRange(long start, long len, int total) => start >= 0 && len >= 0 && start + len <= total;
}
