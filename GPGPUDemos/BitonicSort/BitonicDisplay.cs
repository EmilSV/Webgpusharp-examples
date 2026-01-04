using System.Runtime.CompilerServices;
using Setup;
using WebGpuSharp;
using Buffer = WebGpuSharp.Buffer;

namespace BitonicSort;

internal sealed class BitonicDisplayRenderer
{
    private static Lazy<byte[]> _fullscreenTexturedQuadWGSL =
        new(() => ResourceUtils.GetEmbeddedResource("BitonicSort.shaders.fullscreenTexturedQuad.wgsl", typeof(BitonicDisplayRenderer).Assembly));

    private static Lazy<byte[]> _bitonicDisplayFragmentWGSL =
        new(() => ResourceUtils.GetEmbeddedResource("BitonicSort.shaders.bitonicDisplay.frag.wgsl", typeof(BitonicDisplayRenderer).Assembly));

    private readonly Queue _queue;
    private readonly RenderPipeline _pipeline;
    private readonly Buffer _fragmentUniformBuffer;
    private readonly BindGroup _fragmentBindGroup;
    private readonly ManagedRenderPassDescriptor _renderPassDescriptor;
    private readonly BindGroup _computeBindGroup;

    public BitonicDisplayRenderer(
        Device device,
        TextureFormat presentationFormat,
        ManagedRenderPassDescriptor renderPassDescriptor,
        BindGroup computeBindGroup,
        BindGroupLayout computeLayout)
    {
        _renderPassDescriptor = renderPassDescriptor;
        _computeBindGroup = computeBindGroup;
        _queue = device.GetQueue();
        _fragmentUniformBuffer = device.CreateBuffer(new()
        {
            Size = (ulong)Unsafe.SizeOf<FragmentUniforms>(),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
        });

        var fragmentUniformLayout = device.CreateBindGroupLayout(new()
        {
            Entries =
            [
                new()
                {
                    Binding = 0,
                    Visibility = ShaderStage.Fragment,
                    Buffer = new()
                    {
                        Type = BufferBindingType.Uniform,
                    }
                }
            ]
        });

        _fragmentBindGroup = device.CreateBindGroup(new()
        {
            Layout = fragmentUniformLayout,
            Entries =
            [
                new()
                {
                    Binding = 0,
                    Buffer = _fragmentUniformBuffer,
                }
            ]
        });

        var pipelineLayout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts = [computeLayout, fragmentUniformLayout]
        });

        var vertexModule = device.CreateShaderModuleWGSL(new() { Code = _fullscreenTexturedQuadWGSL.Value });
        var fragmentModule = device.CreateShaderModuleWGSL(new() { Code = _bitonicDisplayFragmentWGSL.Value });

        _pipeline = device.CreateRenderPipelineSync(new()
        {
            Layout = pipelineLayout,
            Vertex = new()
            {
                Module = vertexModule,
            },
            Fragment = new()
            {
                Module = fragmentModule,
                Targets =
                [
                    new()
                    {
                        Format = presentationFormat,
                    }
                ]
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
                CullMode = CullMode.None,
            },
        });
    }

    private void SetArguments(FragmentUniforms uniforms)
    {
        _queue.WriteBuffer(_fragmentUniformBuffer, uniforms);
    }

    public void Render(CommandEncoder commandEncoder, TextureView targetView, FragmentUniforms args)
    {
        SetArguments(args);

        _renderPassDescriptor.ColorAttachments[0].View = targetView;

        var renderPassDescriptor = new RenderPassDescriptor
        {
            Label = _renderPassDescriptor.Label,
            ColorAttachments = _renderPassDescriptor.ColorAttachments,
            DepthStencilAttachment = _renderPassDescriptor.DepthStencilAttachment,
            OcclusionQuerySet = _renderPassDescriptor.OcclusionQuerySet,
            TimestampWrites = _renderPassDescriptor.TimestampWrites,
        };

        var pass = commandEncoder.BeginRenderPass(renderPassDescriptor);
        pass.SetPipeline(_pipeline);
        pass.SetBindGroup(0, _computeBindGroup);
        pass.SetBindGroup(1, _fragmentBindGroup);
        pass.Draw(6);
        pass.End();
    }
}
