using System.Text;
using Setup;
using WebGpuSharp;

namespace Cornell;

/// <summary>
/// Raytracer renders the scene using a software ray-tracing compute pipeline.
/// </summary>
public sealed class Raytracer
{
	private static readonly Lazy<byte[]> raytracerShaderWgsl = new(() =>
		ResourceUtils.GetEmbeddedResource(
			"Cornell.shaders.raytracer.wgsl",
			typeof(Raytracer).Assembly
		)
	);

	private static readonly Lazy<string> raytracerShaderStr = new(() =>
		Encoding.UTF8.GetString(raytracerShaderWgsl.Value)
	);


	private readonly Common _common;

	private readonly ComputePipeline _pipeline;
	private readonly BindGroup _bindGroup;

	private const uint WorkgroupSizeX = 16;
	private const uint WorkgroupSizeY = 16;

	private readonly uint _width;
	private readonly uint _height;

	public Raytracer(Device device, Common common, Radiosity radiosity, Texture framebuffer)
	{
		_common = common;
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

		_bindGroup = device.CreateBindGroup(new()
		{
			Label = "rendererBindGroup",
			Layout = bindGroupLayout,
			Entries =
			[
				new()
				{
					Binding = 0,
					TextureView = radiosity.Lightmap.CreateView(),
				},
				new()
				{
					Binding = 1,
					Sampler = device.CreateSampler(new()
					{
						AddressModeU = AddressMode.ClampToEdge,
						AddressModeV = AddressMode.ClampToEdge,
						AddressModeW = AddressMode.ClampToEdge,
						MagFilter = FilterMode.Linear,
						MinFilter = FilterMode.Linear,
					})
				},
				new()
				{
					Binding = 2,
					TextureView = framebuffer.CreateView(),
				},
			],
		});

		_pipeline = device.CreateComputePipelineSync(new()
		{
			Label = "raytracerPipeline",
			Layout = device.CreatePipelineLayout(new()
			{
				BindGroupLayouts = [common.UniformBindGroupLayout, bindGroupLayout],
			}),
			Compute = new()
			{
				Module = device.CreateShaderModuleWGSL(new()
				{
					Code = raytracerShaderWgsl.Value.Concat(Common.Wgsl.Value).ToArray(),
				}),
				Constants =
				[
					new("WorkgroupSizeX", WorkgroupSizeX),
					new("WorkgroupSizeY", WorkgroupSizeY),
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
