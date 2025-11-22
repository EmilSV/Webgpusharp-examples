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

    public BitonicDisplayRenderer(
        Device device,
        TextureFormat presentationFormat,
        BindGroupLayout computeLayout)
    {
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

        _pipeline = device.CreateRenderPipeline(new()
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

    public void Render(CommandEncoder commandEncoder, TextureView targetView, BindGroup computeBindGroup, DisplayMode displayMode)
    {
        var uniforms = new FragmentUniforms
        {
            Highlight = displayMode == DisplayMode.Elements ? 0u : 1u,
        };
        _queue.WriteBuffer(_fragmentUniformBuffer, uniforms);

        var renderPassDescriptor = new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new()
                {
                    View = targetView,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = new(0.1f, 0.4f, 0.5f, 1f),
                }
            ]
        };

        var pass = commandEncoder.BeginRenderPass(renderPassDescriptor);
        pass.SetPipeline(_pipeline);
        pass.SetBindGroup(0, computeBindGroup);
        pass.SetBindGroup(1, _fragmentBindGroup);
        pass.Draw(6);
        pass.End();
    }
}
