static class ShadersResources
{ 
    public static Lazy<byte[]> BitonicDisplayWgsl = new(() =>
    {
        var assembly = typeof(ShadersResources).Assembly;
        using var stream = assembly.GetManifestResourceStream("BitonicSort.bitonicDisplay.frag.wgsl");
        if (stream == null)
        {
            throw new Exception("Could not find embedded resource 'BitonicSort.bitonicDisplay.frag.wgsl'");
        }
        using MemoryStream ms = new();
        stream.CopyTo(ms);
        return ms.ToArray();
    });
}