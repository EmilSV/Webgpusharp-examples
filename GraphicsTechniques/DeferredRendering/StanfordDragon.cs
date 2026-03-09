
using Setup;

static class StanfordDragon
{
    public static async Task<SimpleMeshBinReader.Mesh> LoadMeshAsync()
    {
        var assembly = typeof(StanfordDragon).Assembly;
        using var stream = assembly.GetManifestResourceStream("DeferredRendering.assets.stanfordDragonData.bin")!;
        return await SimpleMeshBinReader.LoadData(stream);
    }
}