using System;
using System.Text;
using WebGpuSharp;

namespace Cornell;

/// <summary>
/// Runs the tonemapping compute pass to convert the linear framebuffer into the swapchain format.
/// </summary>
public sealed class Tonemapper
{
	private const uint WorkgroupSizeX = 16;
	private const uint WorkgroupSizeY = 16;

	private readonly Device _device;
	private readonly TextureView _inputView;
	private readonly uint _width;
	private readonly uint _height;
	private readonly BindGroupLayout _bindGroupLayout;
	private readonly ComputePipeline _pipeline;

	public Tonemapper(Device device, Texture input, TextureFormat outputFormat, string tonemapperShaderSource, string commonShaderSource)
	{
		_device = device;
		_inputView = input.CreateView();
		_width = input.GetWidth();
		_height = input.GetHeight();

		_bindGroupLayout = device.CreateBindGroupLayout(new()
		{
			Label = "Tonemapper.bindGroupLayout",
			Entries =
			[
				new BindGroupLayoutEntry
				{
					Binding = 0,
					Visibility = ShaderStage.Compute,
					Texture = new TextureBindingLayout
					{
						ViewDimension = TextureViewDimension.D2,
					},
				},
				new BindGroupLayoutEntry
				{
					Binding = 1,
					Visibility = ShaderStage.Compute,
					StorageTexture = new StorageTextureBindingLayout
					{
						Access = StorageTextureAccess.WriteOnly,
						Format = outputFormat,
						ViewDimension = TextureViewDimension.D2,
					},
				},
			],
		});

		string shaderSource = tonemapperShaderSource.Replace("{OUTPUT_FORMAT}", ToWgslFormat(outputFormat)) + commonShaderSource;
		var shaderModule = device.CreateShaderModuleWGSL(new()
		{
			Code = Encoding.UTF8.GetBytes(shaderSource),
		});

		_pipeline = device.CreateComputePipeline(new()
		{
			Label = "Tonemapper.pipeline",
			Layout = device.CreatePipelineLayout(new()
			{
				BindGroupLayouts = [_bindGroupLayout],
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

	public void Run(CommandEncoder commandEncoder, Texture outputTexture)
	{
		var bindGroup = _device.CreateBindGroup(new()
		{
			Layout = _bindGroupLayout,
			Entries =
			[
				new BindGroupEntry
				{
					Binding = 0,
					TextureView = _inputView,
				},
				new BindGroupEntry
				{
					Binding = 1,
					TextureView = outputTexture.CreateView(),
				},
			],
		});

		var pass = commandEncoder.BeginComputePass();
		pass.SetPipeline(_pipeline);
		pass.SetBindGroup(0, bindGroup);
		pass.DispatchWorkgroups(DivRoundUp(_width, WorkgroupSizeX), DivRoundUp(_height, WorkgroupSizeY));
		pass.End();
	}

	private static string ToWgslFormat(TextureFormat format) => format.ToString().ToLowerInvariant();
	private static uint DivRoundUp(uint value, uint divisor) => (value + divisor - 1) / divisor;
}
