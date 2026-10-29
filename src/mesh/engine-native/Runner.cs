using System.Diagnostics;

public static class ExplorerMeshRunner
{
    public static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Expected input and output files."); return 2; }
        if (!Environment.Is64BitProcess) { Console.Error.WriteLine("The Explorer bake engine requires x64."); return 3; }
        Environment.SetEnvironmentVariable("EXPLORER_MESH_PROGRESS", "1");
        try
        {
            var input = File.ReadAllBytes(args[0]);
            if (input.Length < 4) throw new InvalidDataException("Missing bake input header");
            var output = BitConverter.ToUInt32(input) switch
            {
                0x32524D45 => MeshEngine.BakeRawLowMemory(input),
                0x31424D45 => MeshEngine.Bake(input),
                _ => throw new InvalidDataException("Unsupported bake input"),
            };
            File.WriteAllBytes(args[1], output);
            Console.Error.WriteLine($"Native x64 bake complete; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
