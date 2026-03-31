using System.Runtime.InteropServices;
using Setup;

internal unsafe static class StbImage
{
    [DllImport("stb_image", CallingConvention = CallingConvention.Cdecl, EntryPoint = "stbi_load_from_memory")]
    public static extern byte* stbi_load_from_memory(byte* buffer, int len, int* x, int* y, int* channels_in_file, int desired_channels);

    [DllImport("stb_image", CallingConvention = CallingConvention.Cdecl, EntryPoint = "stbi_image_free")]
    public static extern void stbi_image_free(void* retval_from_stbi_load);

    public static ImageData LoadImagePng(Stream stream)
    {
        using MemoryStream memoryStream = new();
        stream.CopyTo(memoryStream);
        if (!memoryStream.TryGetBuffer(out ArraySegment<byte> buffer))
        {
            throw new InvalidOperationException("Failed to get buffer from memory stream.");
        }
        fixed (byte* pImageData = buffer.Array)
        {
            int width, height, channels;
            byte* pPixels = stbi_load_from_memory(pImageData, buffer.Count, &width, &height, &channels, 4);
            if (pPixels == null)
            {
                throw new InvalidOperationException("Failed to load image.");
            }

            byte[] managedPixels = new Span<byte>(pPixels, width * height * 4).ToArray();
            stbi_image_free(pPixels);

            return new ImageData
            (
                data: managedPixels,
                width: (uint)width,
                height: (uint)height
            );
        }
    }
}