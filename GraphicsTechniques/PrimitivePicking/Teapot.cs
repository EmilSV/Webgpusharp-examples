using Setup;

static class Teapot
{
    public static async Task<SimpleMeshBinReader.Mesh> LoadMeshAsync()
    {
        var assembly = typeof(Teapot).Assembly;
        using var stream = assembly.GetManifestResourceStream("PrimitivePicking.assets.teapotData.bin")!;
        return await SimpleMeshBinReader.LoadData(stream);
    }
}