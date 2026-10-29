namespace MapExtract;

/// Resolves DB2 tables to FileDataIDs from the community listfile.
///
/// Without this, finding a table means scanning the fdid range and guessing from
/// layout hashes — which collides constantly (an audio table matched the
/// GameObjectDisplayInfo definition cleanly). db2index.csv is the dbfilesclient
/// slice of the listfile: ~2000 lines, 56KB.
public static class Names
{
    static Dictionary<string, int>? _db2;

    static Dictionary<string, int> Index
    {
        get
        {
            if (_db2 != null) return _db2;
            _db2 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), @"E:\Hacking\wowmaps" })
            {
                var path = Path.Combine(dir, "db2index.csv");
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    int semi = line.IndexOf(';');
                    if (semi <= 0 || !int.TryParse(line.AsSpan(0, semi), out int fdid)) continue;
                    var name = line[(semi + 1)..].Trim();
                    // accept both the stripped form and a raw listfile line, so a
                    // regenerated index works whether or not the path was trimmed
                    int slash = name.LastIndexOf('/');
                    if (slash >= 0) name = name[(slash + 1)..];
                    if (name.EndsWith(".db2", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    _db2[name] = fdid;
                }
                break;
            }
            return _db2;
        }
    }

    /// FileDataID for a DB2 table, or the fallback when the index is missing.
    public static int Db2(string table, int fallback = 0) =>
        Index.TryGetValue(table, out int f) ? f : fallback;

    public static bool Available => Index.Count > 0;
    public static int Count => Index.Count;

    const string ListfileUrl =
        "https://github.com/wowdev/wow-listfile/releases/latest/download/community-listfile.csv";

    /// Rebuild db2index.csv and transports.csv from the community listfile.
    ///
    /// The listfile is ~146 MB and belongs to wowdev, not us, so it is streamed and
    /// filtered on the fly rather than downloaded, committed or kept. Generating the
    /// indexes here rather than with shell commands keeps them in lockstep with the
    /// parser above — a mismatched prefix would silently fall back to hardcoded ids.
    public static async Task<(int Db2, int Transports)> Rebuild(string dir, Action<string>? log = null)
    {
        log?.Invoke("downloading listfile…");

        var db2 = new List<string>();
        var transports = new List<string>();
        var tilesets = new List<string>();
        long bytes = 0;
        int lines = 0;

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        using (var stream = await http.GetStreamAsync(ListfileUrl))
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                bytes += line.Length + 1;
                if (++lines % 250_000 == 0) log?.Invoke($"  {lines:N0} lines, {bytes / 1048576.0:F0} MB…");

                int semi = line.IndexOf(';');
                if (semi <= 0) continue;
                var path = line[(semi + 1)..].Trim();

                if (path.StartsWith("dbfilesclient/", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(".db2", StringComparison.OrdinalIgnoreCase))
                {
                    db2.Add($"{line[..semi]};{path["dbfilesclient/".Length..]}");
                }
                else if (path.StartsWith("world/wmo/transports/", StringComparison.OrdinalIgnoreCase) &&
                         path.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase) &&
                         !IsWmoGroupFile(path))
                {
                    transports.Add($"{line[..semi]};{path["world/wmo/transports/".Length..]}");
                }
                // Terrain textures. A modern ADT names its ground textures by FileDataID
                // (MDID), not by path, so this is the only way to tell cobblestone from grass.
                else if (path.StartsWith("tileset/", StringComparison.OrdinalIgnoreCase))
                {
                    tilesets.Add($"{line[..semi]};{path["tileset/".Length..]}");
                }
            }
        }

        // a caller passing "C:\path\" loses the closing quote to the backslash, so the
        // argument can arrive with a stray one attached
        dir = dir.Trim().TrimEnd('"');
        Directory.CreateDirectory(dir);
        await File.WriteAllLinesAsync(Path.Combine(dir, "db2index.csv"), db2);
        await File.WriteAllLinesAsync(Path.Combine(dir, "transports.csv"), transports);
        await File.WriteAllLinesAsync(Path.Combine(dir, "tilesets.csv"), tilesets);
        _db2 = null;                     // force a reload next lookup
        _tileset = null;
        return (db2.Count, transports.Count);
    }

    static Dictionary<int, string>? _tileset;

    // Explorer supplies its pinned embedded snapshot directly. Baking must not
    // write shared files beside the executable or depend on that folder's access.
    internal static void LoadTilesets(TextReader reader)
    {
        var names = new Dictionary<int, string>();
        while (reader.ReadLine() is { } line)
        {
            int semi = line.IndexOf(';');
            if (semi > 0 && int.TryParse(line.AsSpan(0, semi), out int id)) names[id] = line[(semi + 1)..].Trim();
        }
        _tileset = names;
    }

    /// The tileset path for a terrain texture FileDataID, e.g. "elwynn/elwynndirtbase2.blp",
    /// or null when it is not in tilesets.csv.
    public static string? Tileset(int fdid)
    {
        if (_tileset == null)
        {
            _tileset = [];
            foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var path = Path.Combine(dir, "tilesets.csv");
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    int semi = line.IndexOf(';');
                    if (semi > 0 && int.TryParse(line.AsSpan(0, semi), out int f))
                        _tileset[f] = line[(semi + 1)..].Trim();
                }
                break;
            }
        }
        return _tileset.TryGetValue(fdid, out var n) ? n : null;
    }

    /// WMO group files are "<name>_000.wmo"; only the root is a placeable model.
    static bool IsWmoGroupFile(string path)
    {
        if (path.Length < 8) return false;
        var tail = path.AsSpan(path.Length - 8);          // _NNN.wmo
        return tail[0] == '_' && char.IsAsciiDigit(tail[1]) &&
               char.IsAsciiDigit(tail[2]) && char.IsAsciiDigit(tail[3]);
    }
}
