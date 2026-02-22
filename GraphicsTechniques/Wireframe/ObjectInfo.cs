using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

class ObjectInfo
{
	public required Uniforms Uniforms;
	public required GPUBuffer UniformBuffer;
	public required LineUniforms LineUniforms;
	public required GPUBuffer LineUniformBuffer;
	public required BindGroup LitBindGroup;
	public required BindGroup[] WireframeBindGroups;
	public required Model Model;
}
