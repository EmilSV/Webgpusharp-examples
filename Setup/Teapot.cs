namespace Setup;

public static class Teapot
{
    public static async Task<SimpleMeshBinReader.Mesh> LoadMeshAsync()
    {
        var assembly = typeof(Teapot).Assembly;
        using var stream = assembly.GetManifestResourceStream("Setup.assets.teapotData.bin")!;
        return await SimpleMeshBinReader.LoadData(stream);
    }
}