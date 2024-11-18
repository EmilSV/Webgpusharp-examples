using SkiaSharp;

namespace Setup;

public static class ImageUtils
{
    public static byte[] LoadPng(Stream stream, out int width, out int height)
    {
        // Is kind of overkill to use skia for png loading but it is simple and works
        // Decode the image from the stream
        using (var bitmap = SKBitmap.Decode(stream))
        {
            // Convert to RGBA if necessary
            if (bitmap.ColorType != SKColorType.Rgba8888)
            {
                using (var rgbaBitmap = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, bitmap.AlphaType))
                {
                    bitmap.CopyTo(rgbaBitmap);
                    width = bitmap.Width;
                    height = bitmap.Height;
                    return rgbaBitmap.Bytes;
                }
            }

            width = bitmap.Width;
            height = bitmap.Height;
            // Return byte array
            return bitmap.Bytes;
        }
    }
}