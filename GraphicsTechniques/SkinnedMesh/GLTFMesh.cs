using WebGpuSharp;

class GLTFMesh
{
    public string Name { get; }
    public GLTFPrimitive[] Primitives { get; }

    public GLTFMesh(string name, GLTFPrimitive[] primitives)
    {
        Name = name;
        Primitives = primitives;
    }

    public void BuildRenderPipeline(
        Device device,
        string vertexShader,
        string fragmentShader,
        TextureFormat colorFormat,
        TextureFormat depthFormat,
        BindGroupLayout[] bgLayouts)
    {
        for (int i = 0; i < Primitives.Length; i++)
        {
            Primitives[i].BuildRenderPipeline(
                device,
                vertexShader,
                fragmentShader,
                colorFormat,
                depthFormat,
                bgLayouts,
                $"PrimitivePipeline{i}"
            );
        }
    }

    public void Render(RenderPassEncoder renderPassEncoder, BindGroup[] bindGroups)
    {
        foreach (var primitive in Primitives)
        {
            primitive.Render(renderPassEncoder, bindGroups);
        }
    }
}
