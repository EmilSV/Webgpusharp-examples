using System;
using System.Text;
using Setup;
using Utf8StringInterpolation;
using WebGpuSharp;

namespace Cornell;

/// <summary>
/// Runs the tonemapping compute pass to convert the linear framebuffer into the swapchain format.
/// </summary>
public sealed class Tonemapper
{
	private static Lazy<byte[]> _tonemapperWGSL = new(
		() => ResourceUtils.GetEmbeddedResource($"Cornell.shaders.tonemapper.wgsl", typeof(Tonemapper).Assembly)
	);

	private readonly ComputePipeline _pipeline;
	private BindGroup _bindGroup;
	private readonly uint _width;
	private readonly uint _height;

	private const uint WorkgroupSizeX = 16;
	private const uint WorkgroupSizeY = 16;

	public Tonemapper(
		Device device,
		Texture input,
		Texture outputTexture)
	{
		_width = input.GetWidth();
		_height = input.GetHeight();

		var bindGroupLayout = device.CreateBindGroupLayout(new()
		{
			Label = "Tonemapper.bindGroupLayout",
			Entries =
			[
				new()
				{
					// input
					Binding = 0,
					Visibility = ShaderStage.Compute,
					Texture = new()
					{
						ViewDimension = TextureViewDimension.D2,
					},
				},
				new()
				{
					// output
					Binding = 1,
					Visibility = ShaderStage.Compute,
					StorageTexture = new()
					{
						Access = StorageTextureAccess.WriteOnly,
						Format = outputTexture.GetFormat(),
						ViewDimension = TextureViewDimension.D2,
					},
				},
			],
		});
		_bindGroup = device.CreateBindGroup(new()
		{
			Label = "Tonemapper.bindGroup",
			Layout = bindGroupLayout,
			Entries =
			[
				new()
				{
					// input
					Binding = 0,
					TextureView = input.CreateView(),
				},
				new()
				{
					// output
					Binding = 1,
					TextureView = outputTexture.CreateView(),
				},
			],
		});

		string tonemapperShaderSourceStr = Encoding.UTF8.GetString(_tonemapperWGSL.Value);
		string commonShaderSourceStr = Encoding.UTF8.GetString(Common.Wgsl.Value);
		string shaderSource = tonemapperShaderSourceStr.Replace("{OUTPUT_FORMAT}", ToWgslFormat(outputTexture.GetFormat())) + commonShaderSourceStr;
		var mod = device.CreateShaderModuleWGSL(new()
		{
			Code = Encoding.UTF8.GetBytes(shaderSource),
		});


		var pipelineLayout = device.CreatePipelineLayout(new()
		{
			Label = "Tonemapper.pipelineLayout",
			BindGroupLayouts = [bindGroupLayout],
		});

		_pipeline = device.CreateComputePipeline(new()
		{
			Label = "Tonemapper.pipeline",
			Layout = pipelineLayout,
			Compute = new ComputeState
			{
				Module = mod,
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
		pass.SetBindGroup(0, _bindGroup);
		pass.DispatchWorkgroups(
			workgroupCountX: DivRoundUp(_width, WorkgroupSizeX),
			workgroupCountY: DivRoundUp(_height, WorkgroupSizeY)
		);
		pass.End();
	}

	private static string ToWgslFormat(TextureFormat format) => format.ToString().ToLowerInvariant();
	private static uint DivRoundUp(uint value, uint divisor) => (value + divisor - 1) / divisor;
}
