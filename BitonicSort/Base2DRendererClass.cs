using System.Reflection.Emit;
using WebGpuSharp;

abstract class Base2DRendererClass
{
    static Lazy<byte[]> FullscreenTexturedQuad = new(() =>
    {
        var assembly = typeof(Base2DRendererClass).Assembly;
        using var stream = assembly.GetManifestResourceStream("BitonicSort.fullscreenTexturedQuad.wgsl");
        if (stream == null)
        {
            throw new Exception("Could not find embedded resource 'BitonicSort.fullscreenTexturedQuad.wgsl'");
        }
        using MemoryStream ms = new();
        stream.CopyTo(ms);
        return ms.ToArray();
    });

    public ManagedRenderPassDescriptor? RenderPassDescriptor;
    public RenderPipeline? Pipeline;
    public Dictionary<string, BindGroup> BindGroups = [];
    public BindGroup? CurrentBindGroup;
    public string? CurrentBindGroupName;

    public abstract void SwitchBindGroup(string name);
    public abstract void StartRun(CommandEncoder commandEncoder, params object[] args);

    public virtual void ExecuteRun(
        CommandEncoder commandEncoder,
        in RenderPassDescriptor renderPassDescriptor,
        RenderPipeline pipeline,
        ReadOnlySpan<BindGroup> bindGroups)
    {
        var passEncoder = commandEncoder.BeginRenderPass(in renderPassDescriptor);
        passEncoder.SetPipeline(pipeline);
        for (int i = 0; i < bindGroups.Length; i++)
        {
            passEncoder.SetBindGroup((uint)i, bindGroups[i]);
        }
        passEncoder.Draw(6, 1, 0, 0);
        passEncoder.End();
    }

    public virtual RenderPipeline Create2DRenderPipeline(
        Device device,
        string label,
        ReadOnlySpan<BindGroupLayout> bgLayouts,
        WGPURefText code,
        TextureFormat presentationFormat)
    {
        return device.CreateRenderPipeline(new()
        {
            Label = $"{label}.pipeline",
            Layout = device.CreatePipelineLayout(new()
            {
                BindGroupLayouts = bgLayouts
            }),
            Vertex = ref WebGpuUtil.InlineInit(new VertexState()
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = FullscreenTexturedQuad.Value,
                }),
            }),
            Fragment = new()
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = code,
                }),
                Targets = [
                    new()
                    {
                        Format = presentationFormat
                    }
                ]
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
                CullMode = CullMode.None
            }
        });
    }
}

sealed class ManagedRenderPassDescriptor
{
    public string? Label;
    public required RenderPassColorAttachment[] ColorAttachments;
    public RenderPassDepthStencilAttachment? DepthStencilAttachment;
    public QuerySet? OcclusionQuerySet;
    public PassTimestampWrites? TimestampWrites;

    public RenderPassDescriptor ToRenderPassDescriptor()
    {
        return new RenderPassDescriptor
        {
            label = Label,
            ColorAttachments = ColorAttachments,
            DepthStencilAttachment = DepthStencilAttachment,
            OcclusionQuerySet = OcclusionQuerySet,
            TimestampWrites = TimestampWrites
        };
    }
}