using System.Reflection;
using SkiaSharp;
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
        uint width;
        uint height;

        // Is kind of overkill to use skia for png loading but it is simple and works
        // Decode the image from the stream
        using var bitmap = SKBitmap.Decode(stream);
        // Convert to RGBA if necessary
        if (bitmap.ColorType != SKColorType.Rgba8888)
        {
            using var rgbaBitmap = new SKBitmap(
                width: bitmap.Width,
                height: bitmap.Height,
                colorType: SKColorType.Rgba8888,
                alphaType: bitmap.AlphaType
            );
            bitmap.CopyTo(rgbaBitmap, SKColorType.Rgba8888);

            width = (uint)bitmap.Width;
            height = (uint)bitmap.Height;

            return new(rgbaBitmap.Bytes, width, height);
        }

        width = (uint)bitmap.Width;
        height = (uint)bitmap.Height;
        // Return byte array
        return new(bitmap.Bytes, width, height);
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
}