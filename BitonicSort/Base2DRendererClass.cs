using System;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using WebGpuSharp;
using WebGpuSharp.FFI;
using Setup;

abstract class Base2DRendererClass
{
    private static readonly Lazy<byte[]> FullscreenTexturedQuadWGSL = new(() =>
    {
        return ResourceUtils.GetEmbeddedResource("BitonicSort.shaders.fullscreenTexturedQuad.wgsl");
    });

    public void ExecuteRun(
        CommandEncoder commandEncoder,
        MangedRenderPassDescriptor renderPassDescriptor,
        RenderPipeline pipeline,
        ReadOnlySpan<BindGroup> bindGroups
    )
    {
        var passEncoder = commandEncoder.BeginRenderPass(new RenderPassDescriptor
        {
            Label = renderPassDescriptor.Label,
            ColorAttachments = renderPassDescriptor.ColorAttachments,
            DepthStencilAttachment = renderPassDescriptor.DepthStencilAttachment,
            OcclusionQuerySet = renderPassDescriptor.OcclusionQuerySet,
            TimestampWrites = renderPassDescriptor.TimestampWrites,
        });
        passEncoder.SetPipeline(pipeline);
        for (int i = 0; i < bindGroups.Length; i++)
        {
            passEncoder.SetBindGroup((uint)i, bindGroups[i]);
        }
        passEncoder.Draw(6, 1, 0, 0);
        passEncoder.End();
    }

    public RenderPipeline Create2DRenderPipeline(
        Device device,
        string label,
        BindGroupLayout[] bgLayouts,
        WGPURefText code,
        TextureFormat presentationFormat
    )
    {
        return device.CreateRenderPipeline(new()
        {
            Label = $"{label}.pipeline",
            Layout = device.CreatePipelineLayout(new()
            {
                BindGroupLayouts = bgLayouts
            }),
            Vertex = new()
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = FullscreenTexturedQuadWGSL.Value
                })
            },
            Fragment = new()
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = code
                }),
                Targets =
                [
                    new()
                    {
                        Format = presentationFormat
                    }
                ],
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
                CullMode = CullMode.None
            }
        });
    }
}