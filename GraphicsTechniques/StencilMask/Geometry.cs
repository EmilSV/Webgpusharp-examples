using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;
/// <summary>
/// Represents Geometry like a cube, a sphere, a torus
/// </summary>
class Geometry
{
    public required GPUBuffer VertexBuffer { get; init; }
    public required GPUBuffer IndexBuffer { get; init; }
    public required IndexFormat IndexFormat { get; init; }
    public required int NumVertices { get; init; }
}
