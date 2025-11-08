using System;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

namespace Cornell;

/// <summary>
/// Implements the radiosity solver that accumulates lightmaps via compute shaders.
/// </summary>
public sealed class Radiosity
{
	public const TextureFormat LightmapFormat = TextureFormat.RGBA16Float;
	public const uint LightmapWidth = 256;
	public const uint LightmapHeight = 256;

	private const uint PhotonsPerWorkgroup = 256;
	private const uint WorkgroupsPerFrame = 1024;
	private const uint PhotonEnergy = 100_000;
	private const uint AccumulationMeanMax = 0x10000000;
	private const uint AccumulationToLightmapWorkgroupSizeX = 16;
	private const uint AccumulationToLightmapWorkgroupSizeY = 16;

	private readonly Device _device;
	private readonly Queue _queue;
	private readonly Common _common;
	private readonly Scene _scene;
	private readonly GPUBuffer _accumulationBuffer;
	private readonly GPUBuffer _uniformBuffer;
	private readonly BindGroup _bindGroup;
	private readonly ComputePipeline _radiosityPipeline;
	private readonly ComputePipeline _accumulationToLightmapPipeline;
	private readonly uint _totalLightmapTexels;
	private readonly uint _quadCount;

	private double _accumulationMean;

	public Radiosity(Device device, Common common, Scene scene, string radiosityShaderSource, string commonShaderSource)
	{
		_device = device;
		_queue = device.GetQueue();
		_common = common;
		_scene = scene;
		_quadCount = (uint)scene.QuadCount;

		Lightmap = device.CreateTexture(new()
		{
			Label = "Radiosity.lightmap",
			Size = new Extent3D(LightmapWidth, LightmapHeight, _quadCount),
			Format = LightmapFormat,
			Usage = TextureUsage.TextureBinding | TextureUsage.StorageBinding,
		});

		ulong accumulationBufferSize = LightmapWidth * LightmapHeight * (ulong)_quadCount * 16ul;
		_accumulationBuffer = device.CreateBuffer(new()
		{
			Label = "Radiosity.accumulationBuffer",
			Size = accumulationBufferSize,
			Usage = BufferUsage.Storage,
		});

		_uniformBuffer = device.CreateBuffer(new()
		{
			Label = "Radiosity.uniformBuffer",
			Size = 8 * sizeof(float),
			Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
		});

		_totalLightmapTexels = LightmapWidth * LightmapHeight * _quadCount;

		var bindGroupLayout = device.CreateBindGroupLayout(new()
		{
			Label = "Radiosity.bindGroupLayout",
			Entries =
			[
				new()
				{
					Binding = 0,
					Visibility = ShaderStage.Compute,
					Buffer = new BufferBindingLayout
					{
						Type = BufferBindingType.Storage,
					},
				},
				new()
				{
					Binding = 1,
					Visibility = ShaderStage.Compute,
					StorageTexture = new StorageTextureBindingLayout
					{
						Access = StorageTextureAccess.WriteOnly,
						Format = LightmapFormat,
						ViewDimension = TextureViewDimension.D2Array,
					},
				},
				new()
				{
					Binding = 2,
					Visibility = ShaderStage.Compute,
					Buffer = new BufferBindingLayout
					{
						Type = BufferBindingType.Uniform,
					},
				},
			],
		});

		_bindGroup = device.CreateBindGroup(new()
		{
			Label = "Radiosity.bindGroup",
			Layout = bindGroupLayout,
			Entries =
			[
				new()
				{
					Binding = 0,
					Buffer = _accumulationBuffer,
					Size = _accumulationBuffer.GetSize(),
				},
				new()
				{
					Binding = 1,
					TextureView = Lightmap.CreateView(),
				},
				new()
				{
					Binding = 2,
					Buffer = _uniformBuffer,
					Size = _uniformBuffer.GetSize(),
				},
			],
		});

		string combinedShaderSource = radiosityShaderSource + common.ShaderSource;
		var shaderModule = device.CreateShaderModuleWGSL(new()
		{
			Code = System.Text.Encoding.UTF8.GetBytes(combinedShaderSource),
		});

		var pipelineLayout = device.CreatePipelineLayout(new()
		{
			Label = "Radiosity.pipelineLayout",
			BindGroupLayouts = [common.UniformBindGroupLayout, bindGroupLayout],
		});

		_radiosityPipeline = device.CreateComputePipeline(new()
		{
			Label = "Radiosity.compute",
			Layout = pipelineLayout,
			Compute = new ComputeState
			{
				Module = shaderModule,
				EntryPoint = "radiosity",
				Constants =
				[
					new ConstantEntry("PhotonsPerWorkgroup", PhotonsPerWorkgroup),
					new ConstantEntry("PhotonEnergy", PhotonEnergy),
				],
			},
		});

		_accumulationToLightmapPipeline = device.CreateComputePipeline(new()
		{
			Label = "Radiosity.accumulationToLightmap",
			Layout = pipelineLayout,
			Compute = new ComputeState
			{
				Module = shaderModule,
				EntryPoint = "accumulation_to_lightmap",
				Constants =
				[
					new ConstantEntry("AccumulationToLightmapWorkgroupSizeX", AccumulationToLightmapWorkgroupSizeX),
					new ConstantEntry("AccumulationToLightmapWorkgroupSizeY", AccumulationToLightmapWorkgroupSizeY),
				],
			},
		});
	}

	public Texture Lightmap { get; }

	public void Run(CommandEncoder commandEncoder)
	{
		_accumulationMean += (double)(PhotonsPerWorkgroup * WorkgroupsPerFrame) * PhotonEnergy / _totalLightmapTexels;

		double accumulationToLightmapScale = 1.0 / _accumulationMean;
		double accumulationBufferScale = _accumulationMean > 2.0 * AccumulationMeanMax ? 0.5 : 1.0;
		_accumulationMean *= accumulationBufferScale;

		Span<float> uniformData = stackalloc float[8];
		uniformData[0] = (float)accumulationToLightmapScale;
		uniformData[1] = (float)accumulationBufferScale;
		uniformData[2] = _scene.LightWidth;
		uniformData[3] = _scene.LightHeight;
		uniformData[4] = _scene.LightCenter.X;
		uniformData[5] = _scene.LightCenter.Y;
		uniformData[6] = _scene.LightCenter.Z;
		uniformData[7] = 0f;

		_queue.WriteBuffer(_uniformBuffer, 0, uniformData);

		var passEncoder = commandEncoder.BeginComputePass();
		passEncoder.SetBindGroup(0, _common.UniformBindGroup);
		passEncoder.SetBindGroup(1, _bindGroup);
		passEncoder.SetPipeline(_radiosityPipeline);
		passEncoder.DispatchWorkgroups(WorkgroupsPerFrame);

		passEncoder.SetPipeline(_accumulationToLightmapPipeline);
		passEncoder.DispatchWorkgroups(
			DivRoundUp(LightmapWidth, AccumulationToLightmapWorkgroupSizeX),
			DivRoundUp(LightmapHeight, AccumulationToLightmapWorkgroupSizeY),
			_quadCount);
		passEncoder.End();
	}

	private static uint DivRoundUp(uint value, uint divisor) => (value + divisor - 1) / divisor;
}
