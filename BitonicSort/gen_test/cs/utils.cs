using System;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using WebGpuSharp;
using WebGpuSharp.FFI;

namespace BitonicSort.GenTest
{
    public struct BindGroupCluster
    {
        public BindGroup[] BindGroups;
        public BindGroupLayout BindGroupLayout;
    }

    public static class Utils
    {
        public static BindGroupCluster CreateBindGroupCluster(
            int[] bindings,
            ShaderStage[] visibilities,
            string[] resourceTypes,
            object[] resourceLayouts,
            BindGroupEntry[][] resources,
            string label,
            Device device
        )
        {
            var layoutEntries = new BindGroupLayoutEntry[bindings.Length];
            for (int i = 0; i < bindings.Length; i++)
            {
                var entry = new BindGroupLayoutEntry
                {
                    Binding = (uint)bindings[i],
                    Visibility = visibilities[i % visibilities.Length]
                };

                switch (resourceTypes[i])
                {
                    case "buffer":
                        entry.Buffer = (BufferBindingLayout)resourceLayouts[i];
                        break;
                    case "texture":
                        entry.Texture = (TextureBindingLayout)resourceLayouts[i];
                        break;
                    case "sampler":
                        entry.Sampler = (SamplerBindingLayout)resourceLayouts[i];
                        break;
                    case "storageTexture":
                        entry.StorageTexture = (StorageTextureBindingLayout)resourceLayouts[i];
                        break;
                }
                layoutEntries[i] = entry;
            }

            var bindGroupLayout = device.CreateBindGroupLayout(new BindGroupLayoutDescriptor
            {
                Label = $"{label}.bindGroupLayout",
                Entries = layoutEntries
            });

            var bindGroups = new BindGroup[resources.Length];
            for (int i = 0; i < resources.Length; i++)
            {
                var groupEntries = new BindGroupEntry[resources[0].Length];
                for (int j = 0; j < resources[0].Length; j++)
                {
                    groupEntries[j] = resources[i][j];
                    groupEntries[j].Binding = (uint)j;
                }

                bindGroups[i] = device.CreateBindGroup(new BindGroupDescriptor
                {
                    Label = $"{label}.bindGroup{i}",
                    Layout = bindGroupLayout,
                    Entries = groupEntries
                });
            }

            return new BindGroupCluster
            {
                BindGroups = bindGroups,
                BindGroupLayout = bindGroupLayout
            };
        }

        public const string FullscreenTexturedQuadWGSL = @"
@group(0) @binding(0) var mySampler : sampler;
@group(0) @binding(1) var myTexture : texture_2d<f32>;

struct VertexOutput {
  @builtin(position) Position : vec4f,
  @location(0) fragUV : vec2f,
}

@vertex
fn vert_main(@builtin(vertex_index) VertexIndex : u32) -> VertexOutput {
  const pos = array(
    vec2( 1.0,  1.0),
    vec2( 1.0, -1.0),
    vec2(-1.0, -1.0),
    vec2( 1.0,  1.0),
    vec2(-1.0, -1.0),
    vec2(-1.0,  1.0),
  );

  const uv = array(
    vec2(1.0, 0.0),
    vec2(1.0, 1.0),
    vec2(0.0, 1.0),
    vec2(1.0, 0.0),
    vec2(0.0, 1.0),
    vec2(0.0, 0.0),
  );

  var output : VertexOutput;
  output.Position = vec4(pos[VertexIndex], 0.0, 1.0);
  output.fragUV = uv[VertexIndex];
  return output;
}

@fragment
fn frag_main(@location(0) fragUV : vec2f) -> @location(0) vec4f {
  return textureSample(myTexture, mySampler, fragUV);
}
";
    }

    public abstract class Base2DRendererClass
    {
        public abstract void SwitchBindGroup(string name);
        public abstract void StartRun(CommandEncoder commandEncoder, params object[] args);

        public RenderPassColorAttachment[]? ColorAttachments;
        public RenderPassDepthStencilAttachment? DepthStencilAttachment;

        public RenderPipeline? Pipeline;
        public Dictionary<string, BindGroup> BindGroupMap = new();
        public BindGroup? CurrentBindGroup;
        public string? CurrentBindGroupName;

        public void ExecuteRun(
            CommandEncoder commandEncoder,
            RenderPassDescriptor renderPassDescriptor,
            RenderPipeline pipeline,
            BindGroup[] bindGroups
        )
        {
            var passEncoder = commandEncoder.BeginRenderPass(renderPassDescriptor);
            passEncoder.SetPipeline(pipeline);
            for (int i = 0; i < bindGroups.Length; i++)
            {
                passEncoder.SetBindGroup((uint)i, bindGroups[i]);
            }
            passEncoder.Draw(6, 1, 0, 0);
            passEncoder.End();
        }

        public void SetUniformArguments<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] T>(
            Device device,
            WebGpuSharp.Buffer uniformBuffer,
            T instance,
            string[] keys
        )
        {
            var type = typeof(T);
            for (int i = 0; i < keys.Length; i++)
            {
                float val = 0;
                var prop = type.GetProperty(keys[i]);
                if (prop != null)
                {
                    val = Convert.ToSingle(prop.GetValue(instance));
                }
                else
                {
                    var field = type.GetField(keys[i]);
                    if (field != null)
                    {
                        val = Convert.ToSingle(field.GetValue(instance));
                    }
                }
                device.GetQueue().WriteBuffer(uniformBuffer, (ulong)(i * 4), new float[] { val });
            }
        }

        public RenderPipeline Create2DRenderPipeline(
            Device device,
            string label,
            BindGroupLayout[] bgLayouts,
            string code,
            TextureFormat presentationFormat
        )
        {
            return device.CreateRenderPipeline(new RenderPipelineDescriptor
            {
                Label = $"{label}.pipeline",
                Layout = device.CreatePipelineLayout(new PipelineLayoutDescriptor
                {
                    BindGroupLayouts = bgLayouts
                }),
                Vertex = new VertexState
                {
                    Module = device.CreateShaderModuleWGSL(new ShaderModuleWGSLDescriptor
                    {
                        Code = Utils.FullscreenTexturedQuadWGSL
                    }),
                    EntryPoint = "vert_main"
                },
                Fragment = new FragmentState
                {
                    Module = device.CreateShaderModuleWGSL(new ShaderModuleWGSLDescriptor
                    {
                        Code = code
                    }),
                    Targets = new[]
                    {
                        new ColorTargetState
                        {
                            Format = presentationFormat
                        }
                    },
                    EntryPoint = "frag_main"
                },
                Primitive = new PrimitiveState
                {
                    Topology = PrimitiveTopology.TriangleList,
                    CullMode = CullMode.None
                }
            });
        }
    }
}
