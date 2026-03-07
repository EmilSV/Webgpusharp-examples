using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;


static class GridUtils
{
    public record SkinnedGridBuffers(
        GPUBuffer Positions,
        GPUBuffer Joints,
        GPUBuffer Weights,
        GPUBuffer Indices
    );

    // Uses constant grid data to create appropriately sized GPU Buffers for our skinned grid
    public static SkinnedGridBuffers CreateSkinnedGridBuffers(Device device)
    {
        var positionsBuffer = CreateVertexBuffer(device, GridData.GridVertices);
        var jointsBuffer = CreateVertexBuffer(device, GridData.GridJoints);
        var weightsBuffer = CreateVertexBuffer(device, GridData.GridWeights);

        var indicesBuffer = device.CreateBuffer(new()
        {
            Size = (ulong)(sizeof(ushort) * GridData.GridIndices.Length),
            Usage = BufferUsage.Index,
            MappedAtCreation = true,
        });

        indicesBuffer.GetMappedRange<ushort>(data => GridData.GridIndices.CopyTo(data));
        indicesBuffer.Unmap();

        return new SkinnedGridBuffers(positionsBuffer, jointsBuffer, weightsBuffer, indicesBuffer);
    }

    private static GPUBuffer CreateVertexBuffer(Device device, float[] data)
    {
        var buffer = device.CreateBuffer(new()
        {
            Size = (ulong)(sizeof(float) * data.Length),
            Usage = BufferUsage.Vertex,
            MappedAtCreation = true,
        });
        buffer.GetMappedRange<float>(span => data.CopyTo(span));
        buffer.Unmap();
        return buffer;
    }

    private static GPUBuffer CreateVertexBuffer(Device device, uint[] data)
    {
        var buffer = device.CreateBuffer(new()
        {
            Size = (ulong)(sizeof(uint) * data.Length),
            Usage = BufferUsage.Vertex,
            MappedAtCreation = true,
        });
        buffer.GetMappedRange<uint>(span => data.CopyTo(span));
        buffer.Unmap();
        return buffer;
    }

    public static RenderPipeline CreateSkinnedGridRenderPipeline(
        Device device,
        TextureFormat presentationFormat,
        byte[] vertexShader,
        byte[] fragmentShader,
        BindGroupLayout[] bgLayouts)
    {
        var pipeline = device.CreateRenderPipelineSync(new()
        {
            Label = "SkinnedGridRenderer",
            Layout = device.CreatePipelineLayout(new()
            {
                Label = "SkinnedGridRenderer.pipelineLayout",
                BindGroupLayouts = bgLayouts,
            }),
            Vertex = new()
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = vertexShader,
                }),
                Buffers =
                [
                    // Vertex Positions (positions)
                    new VertexBufferLayout
                    {
                        ArrayStride = sizeof(float) * 2,
                        Attributes =
                        [
                            new VertexAttribute
                            {
                                Format = VertexFormat.Float32x2,
                                Offset = 0,
                                ShaderLocation = 0,
                            }
                        ],
                    },
                    // Bone Indices (joints)
                    new VertexBufferLayout
                    {
                        ArrayStride = sizeof(uint) * 4,
                        Attributes =
                        [
                            new VertexAttribute
                            {
                                Format = VertexFormat.Uint32x4,
                                Offset = 0,
                                ShaderLocation = 1,
                            }
                        ],
                    },
                    // Bone Weights (weights)
                    new VertexBufferLayout
                    {
                        ArrayStride = sizeof(float) * 4,
                        Attributes =
                        [
                            new VertexAttribute
                            {
                                Format = VertexFormat.Float32x4,
                                Offset = 0,
                                ShaderLocation = 2,
                            }
                        ],
                    },
                ],
            },
            Fragment = new()
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = fragmentShader,
                }),
                Targets =
                [
                    new ColorTargetState
                    {
                        Format = presentationFormat,
                    }
                ],
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.LineList,
            },
        });
        return pipeline;
    }
}
