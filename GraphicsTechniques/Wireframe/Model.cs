using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

class Model
{
	public required GPUBuffer VertexBuffer;
	public required GPUBuffer IndexBuffer;
	public required IndexFormat IndexFormat;
	public required uint VertexCount;
}
