using System.Reflection;
using WebGpuSharp;

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

    public static Stream? GetEmbeddedResourceStream(string resourceName, Assembly? assembly = null)
    {
        var executingAssembly = assembly ?? Assembly.GetCallingAssembly();
        return executingAssembly.GetManifestResourceStream(resourceName)!;
    }

    public static async Task<byte[]> GetEmbeddedResourceAsync(string resourceName, Assembly? assembly = null)
    {
        var executingAssembly = assembly ?? Assembly.GetCallingAssembly();
        return await ToByteArrayAsync(executingAssembly.GetManifestResourceStream(resourceName)!);
    }

    public static ImageData LoadImage(Stream stream)
    {
        //Loading image using ImageSharp
        // we are using version 2 as it have the open source license
        // should be remove in the future when we have a better solution
        using var image = SixLabors.ImageSharp.Image.Load(stream);
        using var rgba32Image = image.CloneAs<SixLabors.ImageSharp.PixelFormats.Rgba32>();
        int size = rgba32Image.Width * rgba32Image.Height * 4;
        byte[] data = new byte[size];
        rgba32Image.CopyPixelDataTo(data);
        return new ImageData(data, (uint)rgba32Image.Width, (uint)rgba32Image.Height);
    }

    public static void CopyExternalImageToTexture(
        Queue queue, ImageData source,
        Texture texture, uint width, uint height)
    {
        queue.WriteTexture(
            destination: new()
            {
                Texture = texture,
            },
            data: source.Data,
            dataLayout: new()
            {
                BytesPerRow = 4u * source.Width,
                RowsPerImage = source.Height,
            },
            writeSize: new(width, height)
        );
    }

    public static void CopyExternalImageToTexture(
        Queue queue, ImageData source, Texture texture)
    {
        queue.WriteTexture(
            destination: new()
            {
                Texture = texture,
            },
            data: source.Data,
            dataLayout: new()
            {
                BytesPerRow = 4u * source.Width,
                RowsPerImage = source.Height,
            },
            writeSize: new(source.Width, source.Height)
        );
    }
}