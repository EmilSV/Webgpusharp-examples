using System.Text;
using WebGpuSharp;

class GLTFPrimitive
{
    public GLTFRenderMode Topology { get; }
    public RenderPipeline? RenderPipeline { get; private set; }
    private readonly Dictionary<string, GLTFAccessor> _attributeMap;
    private readonly List<string> _attributes;

    public GLTFPrimitive(
        GLTFRenderMode topology,
        Dictionary<string, GLTFAccessor> attributeMap,
        List<string> attributes)
    {
        Topology = topology;
        _attributeMap = attributeMap;
        _attributes = attributes;

        foreach (var kvp in _attributeMap)
        {
            kvp.Value.View.NeedsUpload = true;
            if (kvp.Key == "INDICES")
            {
                kvp.Value.View.AddUsage(BufferUsage.Index);
                continue;
            }
            kvp.Value.View.AddUsage(BufferUsage.Vertex);
        }
    }

    public void BuildRenderPipeline(
        Device device,
        string vertexShader,
        string fragmentShader,
        TextureFormat colorFormat,
        TextureFormat depthFormat,
        BindGroupLayout[] bgLayouts,
        string label)
    {
        // Build vertex input shader string
        var vertexInputShaderString = new StringBuilder("struct VertexInput {\n");
        var vertexBuffers = new List<VertexBufferLayout>();

        for (int idx = 0; idx < _attributes.Count; idx++)
        {
            var attr = _attributes[idx];
            var vertexFormat = _attributeMap[attr].VertexType;
            var attrString = attr.ToLower().Replace("_0", "");
            vertexInputShaderString.Append($"\t@location({idx}) {attrString}: {ConvertGPUVertexFormatToWGSLFormat(vertexFormat)},\n");

            vertexBuffers.Add(new VertexBufferLayout
            {
                ArrayStride = (ulong)_attributeMap[attr].ByteStride,
                Attributes =
                [
                    new VertexAttribute
                    {
                        Format = _attributeMap[attr].VertexType,
                        Offset = (ulong)_attributeMap[attr].ByteOffset,
                        ShaderLocation = (uint)idx,
                    }
                ],
            });
        }
        vertexInputShaderString.Append('}');

        var vertexState = new VertexState
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexInputShaderString.ToString() + vertexShader
            }),
            Buffers = [.. vertexBuffers],
        };

        var fragmentState = new FragmentState
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexInputShaderString.ToString() + fragmentShader
            }),
            Targets = [new ColorTargetState { Format = colorFormat }],
        };

        var primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList };
        if (Topology == GLTFRenderMode.TriangleStrip)
        {
            primitive.Topology = PrimitiveTopology.TriangleStrip;
            primitive.StripIndexFormat = _attributeMap["INDICES"].VertexType switch
            {
                VertexFormat.Uint16 => IndexFormat.Uint16,
                VertexFormat.Uint32 => IndexFormat.Uint32,
                _ => throw new Exception("Unsupported index format for triangle strip"),
            };
        }

        var layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts = bgLayouts,
            Label = $"{label}.pipelineLayout",
        });

        var rpDescript = new RenderPipelineDescriptor
        {
            Layout = layout,
            Label = $"{label}.pipeline",
            Vertex = vertexState,
            Fragment = fragmentState,
            Primitive = primitive,
            DepthStencil = new()
            {
                Format = depthFormat,
                DepthWriteEnabled = OptionalBool.True,
                DepthCompare = CompareFunction.Less,
            },
        };

        RenderPipeline = device.CreateRenderPipelineSync(rpDescript);
    }

    private static string ConvertGPUVertexFormatToWGSLFormat(VertexFormat vertexFormat)
    {
        return vertexFormat switch
        {
            VertexFormat.Float32 => "f32",
            VertexFormat.Float32x2 => "vec2f",
            VertexFormat.Float32x3 => "vec3f",
            VertexFormat.Float32x4 => "vec4f",
            VertexFormat.Uint32 => "u32",
            VertexFormat.Uint32x2 => "vec2u",
            VertexFormat.Uint32x3 => "vec3u",
            VertexFormat.Uint32x4 => "vec4u",
            VertexFormat.Uint8x2 => "vec2u",
            VertexFormat.Uint8x4 => "vec4u",
            VertexFormat.Uint16x4 => "vec4u",
            VertexFormat.Uint16x2 => "vec2u",
            _ => "f32"
        };
    }

    public void Render(RenderPassEncoder renderPassEncoder, BindGroup[] bindGroups)
    {
        if (RenderPipeline == null) return;

        renderPassEncoder.SetPipeline(RenderPipeline);

        for (int idx = 0; idx < bindGroups.Length; idx++)
        {
            renderPassEncoder.SetBindGroup((uint)idx, bindGroups[idx]);
        }

        for (int idx = 0; idx < _attributes.Count; idx++)
        {
            var attr = _attributes[idx];
            renderPassEncoder.SetVertexBuffer(
                (uint)idx,
                _attributeMap[attr].View.GpuBuffer!,
                (ulong)_attributeMap[attr].ByteOffset,
                (ulong)_attributeMap[attr].ByteLength
            );
        }

        if (_attributeMap.ContainsKey("INDICES"))
        {
            renderPassEncoder.SetIndexBuffer(
                _attributeMap["INDICES"].View.GpuBuffer!,
                _attributeMap["INDICES"].VertexType == VertexFormat.Uint16 ? IndexFormat.Uint16 : IndexFormat.Uint32,
                (ulong)_attributeMap["INDICES"].ByteOffset,
                (ulong)_attributeMap["INDICES"].ByteLength
            );
            renderPassEncoder.DrawIndexed((uint)_attributeMap["INDICES"].Count);
        }
        else
        {
            renderPassEncoder.Draw((uint)_attributeMap["POSITION"].Count);
        }
    }
}
