using System.Text;

namespace MapExtract;

/// The ground textures painted on one ADT, decoded from its split _tex0 file.
///
/// WoW has no road objects. A road is a texture - dirt, cobblestone - painted onto the
/// terrain, and this file is where the paint lives: per map chunk, a list of up to four
/// texture layers (MCLY) and, for every layer above the base, a 64x64 alpha map (MCAL) saying
/// how strongly it is painted at each point. A tile is 16x16 chunks, so 1024x1024 texels at
/// about 0.52 yd each.
///
/// Textures are named by FileDataID (MDID) in modern files, resolved through tilesets.csv.
public sealed class Tex0
{
    public const int Texels = 1024;          // per tile side
    public const int ChunkTexels = 64;

    /// FileDataID of each texture this tile references, indexed by MCLY textureId.
    public List<int> Textures = [];

    /// The dominant texture at each texel, as an index into Textures; -1 where there is no
    /// chunk. Row-major, [ty * Texels + tx], chunk order as stored in the file.
    public short[] Dominant = new short[Texels * Texels];

    /// How strongly the dominant texture is painted there, 0..255.
    public byte[] Strength = new byte[Texels * Texels];

    /// Each chunk's layers - texture index and alpha map - kept so a caller can ask how much
    /// of a SET of textures is painted at a spot, not only which single texture wins. Road
    /// paint is blended with dirt at its edges and through its middle, so "does road win?"
    /// flips cell by cell and speckles the road with holes; "how much road is here?" does
    /// not. Null for chunks the file has no layers for.
    public (int[] Tex, byte[]?[] Alpha)?[] Chunks = new (int[], byte[]?[])?[256];

    /// How much of the textures flagged in `wanted` is painted at each texel, 0..255, using
    /// the same blend as `Dominant`: each layer over the base, the base showing through
    /// whatever the layers above leave uncovered.
    public byte[] Coverage(bool[] wanted)
    {
        var o = new byte[Texels * Texels];
        for (int index = 0; index < 256; index++)
        {
            if (Chunks[index] is not { } ch) continue;
            var (tex, alpha) = ch;
            int layers = tex.Length;
            int cx = index % 16, cy = index / 16;
            for (int y = 0; y < ChunkTexels; y++)
                for (int x = 0; x < ChunkTexels; x++)
                {
                    int i = y * ChunkTexels + x;
                    int rest = 255, sum = 0;
                    for (int l = layers - 1; l >= 1; l--)
                    {
                        int a = alpha[l] is { } am ? am[i] : 0;
                        int w = a * rest / 255;
                        rest -= w;
                        if (tex[l] >= 0 && tex[l] < wanted.Length && wanted[tex[l]]) sum += w;
                    }
                    if (tex[0] >= 0 && tex[0] < wanted.Length && wanted[tex[0]]) sum += rest;
                    o[(cy * ChunkTexels + y) * Texels + cx * ChunkTexels + x] = (byte)Math.Clamp(sum, 0, 255);
                }
        }
        return o;
    }

    public static Tex0? Parse(byte[] d)
    {
        var t = new Tex0();
        Array.Fill(t.Dominant, (short)-1);
        int chunk = 0;
        int pos = 0;

        while (pos + 8 <= d.Length)
        {
            string magic = Magic(d, pos);
            int size = BitConverter.ToInt32(d, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > d.Length) break;

            if (magic == "MDID")
                for (int i = 0; i + 4 <= size; i += 4) t.Textures.Add(BitConverter.ToInt32(d, body + i));
            else if (magic == "MCNK")
            {
                if (chunk < 256) t.Chunk(d, body, size, chunk);
                chunk++;
            }
            pos = body + size;
        }
        return chunk == 0 ? null : t;
    }

    static string Magic(byte[] d, int at)
        => new(new[] { (char)d[at + 3], (char)d[at + 2], (char)d[at + 1], (char)d[at] });

    void Chunk(byte[] d, int start, int size, int index)
    {
        // Sub-chunks: MCLY (16 bytes a layer), MCSH, MCAL, MCMT.
        int mcly = -1, mclySize = 0, mcal = -1, mcalSize = 0;
        for (int p = start; p + 8 <= start + size;)
        {
            string m = Magic(d, p);
            int s = BitConverter.ToInt32(d, p + 4);
            if (m == "MCLY") { mcly = p + 8; mclySize = s; }
            else if (m == "MCAL") { mcal = p + 8; mcalSize = s; }
            p += 8 + Math.Max(0, s);
        }
        if (mcly < 0) return;

        int layers = Math.Min(4, mclySize / 16);
        if (layers == 0) return;

        var tex = new int[layers];
        var alpha = new byte[layers][];
        for (int l = 0; l < layers; l++)
        {
            int at = mcly + l * 16;
            tex[l] = BitConverter.ToInt32(d, at);
            uint flags = BitConverter.ToUInt32(d, at + 4);
            int offset = BitConverter.ToInt32(d, at + 8);
            if (l == 0 || mcal < 0) continue;              // the base layer has no alpha map

            // Where the next layer's alpha starts tells an uncompressed map's format apart:
            // 4096 bytes is 8-bit, 2048 is 4-bit. No need to consult the WDT header.
            int end = mcalSize;
            for (int n = l + 1; n < layers; n++)
            {
                int no = BitConverter.ToInt32(d, mcly + n * 16 + 8);
                if (no > offset) { end = no; break; }
            }
            alpha[l] = (flags & 0x200) != 0
                ? Rle(d, mcal + offset, mcal + mcalSize)
                : end - offset >= 4096 ? Raw8(d, mcal + offset) : Raw4(d, mcal + offset);
        }
        Chunks[index] = (tex, alpha);

        int cx = index % 16, cy = index / 16;
        for (int y = 0; y < ChunkTexels; y++)
            for (int x = 0; x < ChunkTexels; x++)
            {
                int i = y * ChunkTexels + x;
                // The base shows through wherever the layers over it do not cover it.
                int rest = 255, best = 0, bestW = -1;
                for (int l = layers - 1; l >= 1; l--)
                {
                    int a = alpha[l] is { } am ? am[i] : 0;
                    int w = a * rest / 255;
                    rest -= w;
                    if (w > bestW) { bestW = w; best = l; }
                }
                if (rest > bestW) { bestW = rest; best = 0; }

                int gx = cx * ChunkTexels + x, gy = cy * ChunkTexels + y;
                Dominant[gy * Texels + gx] = (short)tex[best];
                Strength[gy * Texels + gx] = (byte)Math.Clamp(bestW, 0, 255);
            }
    }

    static byte[] Rle(byte[] d, int at, int limit)
    {
        var o = new byte[4096];
        int n = 0;
        while (n < 4096 && at < limit)
        {
            byte b = d[at++];
            int count = b & 0x7F;
            if ((b & 0x80) != 0) { byte v = at < limit ? d[at++] : (byte)0; for (int i = 0; i < count && n < 4096; i++) o[n++] = v; }
            else for (int i = 0; i < count && n < 4096 && at < limit; i++) o[n++] = d[at++];
        }
        return o;
    }

    static byte[] Raw8(byte[] d, int at)
    {
        var o = new byte[4096];
        Array.Copy(d, at, o, 0, Math.Min(4096, d.Length - at));
        return o;
    }

    static byte[] Raw4(byte[] d, int at)
    {
        var o = new byte[4096];
        for (int i = 0; i < 2048 && at + i < d.Length; i++)
        {
            o[i * 2] = (byte)((d[at + i] & 0x0F) * 17);
            o[i * 2 + 1] = (byte)((d[at + i] >> 4) * 17);
        }
        return o;
    }
}
