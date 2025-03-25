
namespace Setup.Macos;

internal struct CAMetalLayer
{
    public readonly nint NativePtr;

    public CAMetalLayer(nint ptr)
    {
        NativePtr = ptr;
    }

    public static CAMetalLayer New()
    {
        return s_class.AllocInit<CAMetalLayer>();
    }

    private static readonly ObjectiveCClass s_class = new(nameof(CAMetalLayer));
}