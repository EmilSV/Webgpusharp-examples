using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;
/// <summary>
/// Per object data.
/// </summary>
class ObjectInfo
{
    public Uniform UniformValues;
    public required GPUBuffer UniformBuffer { get; init; }
    public required BindGroup BindGroup { get; init; }
    public required Geometry Geometry { get; init; }
}
