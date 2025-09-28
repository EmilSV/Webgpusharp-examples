using WebGpuSharp;

class BitonicDisplayRenderer : Base2DRendererClass
{
    public BindGroupCluster ComputeBGDescript { get; }
    public BitonicDisplayRenderer(
        Device device,
        TextureFormat presentationFormat,
        ManagedRenderPassDescriptor renderPassDescriptor,
        BindGroupCluster computeBGDescript,
        string label)
    {
        // RenderPassDescriptor = renderPassDescriptor;
        // ComputeBGDescript = computeBGDescript;

        // var uniformBuffer = device.CreateBuffer(new()
        // {
        //     Size = sizeof(uint),
        //     Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        // });

        // var bgCluster = BindGroupCluster.CreateBindGroupCluster(
        //     [0],
        //     [ShaderStage.Fragment],
        //     [ResourceType.Buffer],
        //     [new BufferBindingLayout()
        //     {
        //         Type = BufferBindingType.Uniform,
        //     }],
        //     [[uniformBuffer]],
        //     label,
        //     device
        // );

        // this.CurrentBindGroup = bgCluster.BindGroups[0];

        // this.Pipeline = Create2DRenderPipeline(
        //     device,
        //     label,
        //     [computeBGDescript.BindGroupLayout,bgCluster.BindGroupLayout],
        //     ShadersResources.BitonicDisplayWgsl.Value,
        //     presentationFormat
        // );

    }

    public override void StartRun(CommandEncoder commandEncoder, params object[] args)
    {
        throw new NotImplementedException();
    }

    public override void SwitchBindGroup(string name)
    {
        throw new NotImplementedException();
    }
}