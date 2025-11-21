using Setup;
using WebGpuSharp;

public struct BitonicDisplayRenderArgs
{
    public uint Highlight;
}

class BitonicDisplayRenderer : Base2DRendererClass
{
    private Lazy<byte[]> BitonicDisplayFragWGSL = new(() =>
    {
        return ResourceUtils.GetEmbeddedResource("BitonicSort.shaders.bitonicDisplay.frag.wgsl", typeof(BitonicDisplayRenderer).Assembly);
    });

    public (BindGroup[] bindGroups, BindGroupLayout layout) computeBindGroupsAndLayout;
    private WebGpuSharp.Buffer _uniformBuffer;
    private Queue _queue;
    private BindGroupLayout _bindGroupLayout;
    private BindGroup _bindGroup;
    private RenderPipeline _pipeline;
    private MangedRenderPassDescriptor _renderPassDescriptor;

    public BitonicDisplayRenderer(
        Device device,
        TextureFormat presentationFormat,
        MangedRenderPassDescriptor renderPassDescriptor,
        (BindGroup[] bindGroups, BindGroupLayout layout) computeBindGroupsAndLayout,
        string label
    )
    {
        _queue = device.GetQueue();
        _renderPassDescriptor = renderPassDescriptor;

        _uniformBuffer = device.CreateBuffer(new BufferDescriptor
        {
            Size = sizeof(uint),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });

        _bindGroupLayout = device.CreateBindGroupLayout(new BindGroupLayoutDescriptor
        {
            Label = $"{label}.bindGroupLayout",
            Entries =
            [
                new()
                {
                    Binding = 0,
                    Visibility = ShaderStage.Fragment,
                    Buffer = new BufferBindingLayout
                    {
                        Type = BufferBindingType.Uniform
                    }
                }
            ]
        });

        _bindGroup = device.CreateBindGroup(new BindGroupDescriptor
        {
            Label = $"{label}.bindGroup0",
            Layout = _bindGroupLayout,
            Entries =
            [
                new()
                {
                    Binding = 0,
                    Buffer = _uniformBuffer
                }
            ]
        });

        _pipeline = Create2DRenderPipeline(
            device,
            label,
            [computeBindGroupsAndLayout.layout, _bindGroupLayout],
            BitonicDisplayFragWGSL.Value,
            presentationFormat
        );
    }

    public void SetArguments(BitonicDisplayRenderArgs args)
    {
        _queue.WriteBuffer(_uniformBuffer, 0, args.Highlight);
    }

    public void StartRun(CommandEncoder commandEncoder, BitonicDisplayRenderArgs args)
    {
        SetArguments(args);
        ExecuteRun(
                commandEncoder,
                _renderPassDescriptor,
                _pipeline!,
                [computeBindGroupsAndLayout.bindGroups[0], _bindGroup]
            );
    }
}


