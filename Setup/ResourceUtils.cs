using System.Reflection;

namespace Setup;

public static class ResourceUtils
{
    static byte[] ToByteArray(Stream input)
    {
        using MemoryStream ms = new();
        input.CopyTo(ms);
        return ms.ToArray();
    }

    static async Task<byte[]> ToByteArrayAsync(Stream input)
    {
        using MemoryStream ms = new();
        await input.CopyToAsync(ms);
        return ms.ToArray();
    }

    public static byte[] GetEmbeddedResource(string resourceName, Assembly? assembly = null)
    {
        var executingAssembly = assembly ?? Assembly.GetCallingAssembly();
        return ToByteArray(executingAssembly.GetManifestResourceStream(resourceName)!)!;
    }

    public static async Task<byte[]> GetEmbeddedResourceAsync(string resourceName, Assembly? assembly = null)
    {
        var executingAssembly = assembly ?? Assembly.GetCallingAssembly();
        return await ToByteArrayAsync(executingAssembly.GetManifestResourceStream(resourceName)!);
    }
}