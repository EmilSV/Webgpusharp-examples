using GPUBuffer = WebGpuSharp.Buffer;
/// <summary>
/// Per scene data.
/// </summary>
class Scene
{
    public required List<ObjectInfo> ObjectInfos { get; init; }
    public required GPUBuffer SharedUniformBuffer { get; init; }
    public SharedUniforms SharedUniformValues;
}