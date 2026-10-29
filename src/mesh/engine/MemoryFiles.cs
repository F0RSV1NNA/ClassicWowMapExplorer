// Adapt Mappster's two CASC read operations to files already read by Explorer's CASC worker.
// This is an in-memory source adapter, not a native CASC implementation or external process.
namespace CASCLib;
public sealed class CASCHandler(Dictionary<int, byte[]> files)
{
    public bool FileExists(int id) => files.ContainsKey(id);
    public Stream OpenFile(int id) => new MemoryStream(files[id], false);
}
