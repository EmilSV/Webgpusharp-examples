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

    public static byte[] GetEmbeddedResource(string resourceName)
    {
        var executingAssembly = Assembly.GetExecutingAssembly();
        return ToByteArray(executingAssembly.GetManifestResourceStream(resourceName)!)!;
    }
}