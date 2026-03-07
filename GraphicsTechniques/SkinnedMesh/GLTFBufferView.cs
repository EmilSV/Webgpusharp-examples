using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

class GLTFBufferView
{
    public int ByteLength { get; }
    public int ByteStride { get; }
    public byte[] View { get; }
    public bool NeedsUpload { get; set; }
    public GPUBuffer? GpuBuffer { get; private set; }
    public BufferUsage Usage { get; private set; }

    public GLTFBufferView(ReadOnlySpan<byte> buffer, BufferView view)
    {
        ByteLength = view.ByteLength;
        ByteStride = view.ByteStride ?? 0;

        int viewOffset = view.ByteOffset ?? 0;
        View = buffer.Slice(viewOffset, ByteLength).ToArray();
        NeedsUpload = false;
        Usage = 0;
    }

    public void AddUsage(BufferUsage usage)
    {
        Usage |= usage;
    }

    private static uint AlignTo(uint val, uint align)
    {
        return (val + align - 1) / align * align;
    }


    public void Upload(Device device)
    {
        // Note: must align to 4 byte size when mapped at creation is true
        var buf = device.CreateBuffer(new()
        {
            Size = AlignTo((uint)View.Length, 4),
            Usage = Usage,
            MappedAtCreation = true,
        });
        buf.GetMappedRange(i => View.CopyTo(i));
        buf.Unmap();
        GpuBuffer = buf;
        NeedsUpload = false;
    }
}
