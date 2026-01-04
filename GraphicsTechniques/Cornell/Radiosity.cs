using System;
using System.Runtime.CompilerServices;
using Setup;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

namespace Cornell;

/// <summary>
/// Implements the radiosity solver that accumulates lightmaps via compute shaders.
/// </summary>
public sealed class Radiosity
{
	private static Lazy<byte[]> _radiosityWGSL = new(
		() => ResourceUtils.GetEmbeddedResource($"Cornell.shaders.radiosity.wgsl", typeof(Radiosity).Assembly)
	);

	// The output lightmap format and dimensions	
	public const TextureFormat LightmapFormat = TextureFormat.RGBA16Float;
	public const uint LightmapWidth = 256;
	public const uint LightmapHeight = 256;

	// The output lightmap.
	public Texture Lightmap { get; }

	/// <summary>
	/// Number of photons emitted per workgroup.
	/// This is equal to the workgroup size (one photon per invocation)
	/// </summary>
	private const uint PhotonsPerWorkgroup = 256;
	/// <summary>
	/// Number of radiosity workgroups dispatched per frame.
	/// </summary>
	private const uint WorkgroupsPerFrame = 1024;
	private const uint PhotonsPerFrame = PhotonsPerWorkgroup * WorkgroupsPerFrame;

	/// <summary>
	///Maximum value that can be added to the 'accumulation' buffer, per photon,
	///across all texels.
	/// </summary>
	private const uint PhotonEnergy = 100_000;

	/// <summary>
	/// The maximum value of 'accumulationAverage' before all values in
	/// accumulation' are reduced to avoid integer overflows.
	/// </summary>
	private const uint AccumulationMeanMax = 0x10000000;

	private const uint AccumulationToLightmapWorkgroupSizeX = 16;
	private const uint AccumulationToLightmapWorkgroupSizeY = 16;

	private readonly Queue _queue;
	private readonly Common _common;
	private readonly Scene _scene;
	private readonly GPUBuffer _accumulationBuffer;
	private readonly GPUBuffer _uniformBuffer;
	private readonly BindGroup _bindGroup;
	private readonly ComputePipeline _radiosityPipeline;
	private readonly ComputePipeline _accumulationToLightmapPipeline;

	/// <summary>
	/// The total number of lightmap texels for all quads.
	/// </summary>
	private readonly uint _totalLightmapTexels;

	private double _accumulationMean;

	public Radiosity(Device device, Common common, Scene scene)
	{
		uint quadCount = (uint)scene.Quads.Length;

		_queue = device.GetQueue();
		_common = common;
		_scene = scene;

		Lightmap = device.CreateTexture(new()
		{
			Label = "Radiosity.lightmap",
			Size = new(
				width: LightmapWidth,
				height: LightmapHeight,
				depthOrArrayLayers: quadCount
			),
			Format = LightmapFormat,
			Usage = TextureUsage.TextureBinding | TextureUsage.StorageBinding,
		});

		_accumulationBuffer = device.CreateBuffer(new()
		{
			Label = "Radiosity.accumulationBuffer",
			Size = (
				LightmapWidth *
				LightmapHeight *
				quadCount *
				16
			),
			Usage = BufferUsage.Storage,
		});

		_totalLightmapTexels = LightmapWidth * LightmapHeight * quadCount;

		_uniformBuffer = device.CreateBuffer(new()
		{
			Label = "Radiosity.uniformBuffer",
			Size = (ulong)Unsafe.SizeOf<RadiosityUniforms>(),
			Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
		});


		var bindGroupLayout = device.CreateBindGroupLayout(new()
		{
			Label = "Radiosity.bindGroupLayout",
			Entries =
			[
				new()
				{
					// accumulation buffer
					Binding = 0,
					Visibility = ShaderStage.Compute,
					Buffer = new BufferBindingLayout
					{
						Type = BufferBindingType.Storage,
					},
				},
				new()
				{
					// lightmap
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
					// radiosity_uniforms
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
					// accumulation buffer
					Binding = 0,
					Buffer = _accumulationBuffer,
					Size = _accumulationBuffer.GetSize(),
				},
				new()
				{
					// lightmap
					Binding = 1,
					TextureView = Lightmap.CreateView(),
				},
				new()
				{
					// radiosity_uniforms
					Binding = 2,
					Buffer = _uniformBuffer,
					Size = _uniformBuffer.GetSize(),
				},
			],
		});

		var mod = device.CreateShaderModuleWGSL(new()
		{
			Code = (byte[])[.. _radiosityWGSL.Value, .. Common.Wgsl.Value],
		});

		var pipelineLayout = device.CreatePipelineLayout(new()
		{
			Label = "Radiosity.pipelineLayout",
			BindGroupLayouts = [common.UniformBindGroupLayout, bindGroupLayout],
		});

		_radiosityPipeline = device.CreateComputePipelineSync(new()
		{
			Label = "Radiosity.compute",
			Layout = pipelineLayout,
			Compute = new()
			{
				Module = mod,
				EntryPoint = "radiosity",
				Constants =
				[
					new ("PhotonsPerWorkgroup", PhotonsPerWorkgroup),
					new ("PhotonEnergy", PhotonEnergy),
				],
			},
		});

		_accumulationToLightmapPipeline = device.CreateComputePipelineSync(new()
		{
			Label = "Radiosity.accumulationToLightmap",
			Layout = pipelineLayout,
			Compute = new()
			{
				Module = mod,
				EntryPoint = "accumulation_to_lightmap",
				Constants =
				[
					new("AccumulationToLightmapWorkgroupSizeX", AccumulationToLightmapWorkgroupSizeX),
					new("AccumulationToLightmapWorkgroupSizeY", AccumulationToLightmapWorkgroupSizeY),
				],
			},
		});
	}

	public void Run(CommandEncoder commandEncoder)
	{
		// Calculate the new mean value for the accumulation buffer
		_accumulationMean += ((double)PhotonsPerFrame * PhotonEnergy) / _totalLightmapTexels;

		// Calculate the 'accumulation' -> 'lightmap' scale factor from 'accumulationMean'
		double accumulationToLightmapScale = 1.0 / _accumulationMean;
		// If 'accumulationMean' is greater than 'kAccumulationMeanMax', then reduce
		// the 'accumulation' buffer values to prevent u32 overflow.
		double accumulationBufferScale = _accumulationMean > 2.0 * AccumulationMeanMax ? 0.5 : 1.0;
		_accumulationMean *= accumulationBufferScale;

		// Update the radiosity uniform buffer data.
		_queue.WriteBuffer(_uniformBuffer, new RadiosityUniforms
		{
			AccumulationToLightmapScale = (float)accumulationToLightmapScale,
			AccumulationBufferScale = (float)accumulationBufferScale,
			LightWidth = _scene.LightWidth,
			LightHeight = _scene.LightHeight,
			LightCenter = _scene.LightCenter,
		});

		// Dispatch the radiosity workgroups
		var passEncoder = commandEncoder.BeginComputePass();
		passEncoder.SetBindGroup(0, _common.UniformBindGroup);
		passEncoder.SetBindGroup(1, _bindGroup);
		passEncoder.SetPipeline(_radiosityPipeline);
		passEncoder.DispatchWorkgroups(WorkgroupsPerFrame);

		// Then copy the 'accumulation' data to 'lightmap'
		passEncoder.SetPipeline(_accumulationToLightmapPipeline);
		passEncoder.DispatchWorkgroups(
			DivRoundUp(LightmapWidth, AccumulationToLightmapWorkgroupSizeX),
			DivRoundUp(LightmapHeight, AccumulationToLightmapWorkgroupSizeY),
			Lightmap.GetDepthOrArrayLayers()
		);
		passEncoder.End();
	}

	private static uint DivRoundUp(uint value, uint divisor) => (value + divisor - 1) / divisor;
}
