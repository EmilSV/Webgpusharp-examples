using System.Text;
using WebGpuSharp;

namespace Cornell;

/// <summary>
/// Implements the compute-based ray tracer for the Cornell box scene.
/// </summary>
public sealed class Raytracer
{
	private const uint WorkgroupSizeX = 16;
	private const uint WorkgroupSizeY = 16;

	private readonly Common _common;
	private readonly Texture _framebuffer;
	private readonly uint _width;
	private readonly uint _height;
	private readonly ComputePipeline _pipeline;
	private readonly BindGroup _bindGroup;

	public Raytracer(Device device, Common common, Radiosity radiosity, Texture framebuffer, string raytracerShaderSource, string commonShaderSource)
	{
		_common = common;
		_framebuffer = framebuffer;
		_width = framebuffer.GetWidth();
		_height = framebuffer.GetHeight();

		var bindGroupLayout = device.CreateBindGroupLayout(new()
		{
			Label = "Raytracer.bindGroupLayout",
			Entries =
			[
				new BindGroupLayoutEntry
				{
					Binding = 0,
					Visibility = ShaderStage.Fragment | ShaderStage.Compute,
					Texture = new TextureBindingLayout
					{
						ViewDimension = TextureViewDimension.D2Array,
					},
				},
				new BindGroupLayoutEntry
				{
					Binding = 1,
					Visibility = ShaderStage.Fragment | ShaderStage.Compute,
					Sampler = new SamplerBindingLayout(),
				},
				new BindGroupLayoutEntry
				{
					Binding = 2,
					Visibility = ShaderStage.Compute,
					StorageTexture = new StorageTextureBindingLayout
					{
						Access = StorageTextureAccess.WriteOnly,
						Format = framebuffer.GetFormat(),
						ViewDimension = TextureViewDimension.D2,
					},
				},
			],
		});

		var sampler = device.CreateSampler(new()
		{
			AddressModeU = AddressMode.ClampToEdge,
			AddressModeV = AddressMode.ClampToEdge,
			AddressModeW = AddressMode.ClampToEdge,
			MagFilter = FilterMode.Linear,
			MinFilter = FilterMode.Linear,
		});

		_bindGroup = device.CreateBindGroup(new()
		{
			Label = "Raytracer.bindGroup",
			Layout = bindGroupLayout,
			Entries =
			[
				new BindGroupEntry
				{
					Binding = 0,
					TextureView = radiosity.Lightmap.CreateView(),
				},
				new BindGroupEntry
				{
					Binding = 1,
					Sampler = sampler,
				},
				new BindGroupEntry
				{
					Binding = 2,
					TextureView = framebuffer.CreateView(),
				},
			],
		});

		string shaderSource = raytracerShaderSource + commonShaderSource;
		var shaderModule = device.CreateShaderModuleWGSL(new()
		{
			Code = Encoding.UTF8.GetBytes(shaderSource),
		});

		_pipeline = device.CreateComputePipeline(new()
		{
			Label = "Raytracer.pipeline",
			Layout = device.CreatePipelineLayout(new()
			{
				BindGroupLayouts = [common.UniformBindGroupLayout, bindGroupLayout],
			}),
			Compute = new ComputeState
			{
				Module = shaderModule,
				EntryPoint = "main",
				Constants =
				[
					new ConstantEntry("WorkgroupSizeX", WorkgroupSizeX),
					new ConstantEntry("WorkgroupSizeY", WorkgroupSizeY),
				],
			},
		});
	}

	public void Run(CommandEncoder commandEncoder)
	{
		var pass = commandEncoder.BeginComputePass();
		pass.SetPipeline(_pipeline);
		pass.SetBindGroup(0, _common.UniformBindGroup);
		pass.SetBindGroup(1, _bindGroup);
		pass.DispatchWorkgroups(DivRoundUp(_width, WorkgroupSizeX), DivRoundUp(_height, WorkgroupSizeY));
		pass.End();
	}

	private static uint DivRoundUp(uint value, uint divisor) => (value + divisor - 1) / divisor;
}
