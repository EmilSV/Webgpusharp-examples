using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const uint TileDim = 128;
const uint BatchSize = 4;

var assembly = Assembly.GetExecutingAssembly();
var blurWGSL = ResourceUtils.GetEmbeddedResource("ImageBlur.shaders.blur.wgsl", assembly);
var fullscreenTexturedQuadWGSL = ResourceUtils.GetEmbeddedResource("ImageBlur.shaders.fullscreenTexturedQuad.wgsl", assembly);
using var imageStream = assembly.GetManifestResourceStream("ImageBlur.assets.Di-3d.png")
	?? throw new InvalidOperationException("Image asset 'Di-3d.png' not found.");
var sourceImageData = ResourceUtils.LoadImage(imageStream);
var windowWidth = (int)sourceImageData.Width;
var windowHeight = (int)sourceImageData.Height;
var settings = new BlurSettings();

CommandBuffer DrawGui(GuiContext guiContext, Surface surface, out bool filterSizeChanged)
{
	filterSizeChanged = false;

	guiContext.NewFrame();
	ImGui.SetNextWindowBgAlpha(0.75f);
	ImGui.SetNextWindowPos(new(16, 16));
	ImGui.SetNextWindowSize(new(280, 140));
	ImGui.Begin("Image Blur", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);

	int filterSize = settings.FilterSize;
	filterSizeChanged = ImGui.SliderInt("Filter Size", ref settings.FilterSize, 1, 33);
	ImGui.SliderInt("Iterations", ref settings.Iterations, 1, 10);

	ImGui.End();
	guiContext.EndFrame();

	return guiContext.Render(surface)!.Value!;
}

return Run("Image Blur", windowWidth, windowHeight, async runContext =>
{
	var instance = runContext.GetInstance();
	var surface = runContext.GetSurface();
	var guiContext = runContext.GetGuiContext();

	var adapter = await instance.RequestAdapterAsync(new()
	{
		CompatibleSurface = surface,
		FeatureLevel = FeatureLevel.Compatibility,
	});

	var device = await adapter.RequestDeviceAsync(new()
	{
		UncapturedErrorCallback = (type, message) =>
		{
			var messageString = Encoding.UTF8.GetString(message);
			Console.Error.WriteLine($"Uncaptured error: {type} {messageString}");
		},
		DeviceLostCallback = (reason, message) =>
		{
			var messageString = Encoding.UTF8.GetString(message);
			Console.Error.WriteLine($"Device lost: {reason} {messageString}");
		},
	});

	var queue = device.GetQueue();

	var surfaceCapabilities = surface.GetCapabilities(adapter)!;
	var surfaceFormat = surfaceCapabilities.Formats[0];

	guiContext.SetupIMGUI(device, surfaceFormat);

	surface.Configure(new()
	{
		Width = (uint)windowWidth,
		Height = (uint)windowHeight,
		Usage = TextureUsage.RenderAttachment,
		Format = surfaceFormat,
		Device = device,
		PresentMode = PresentMode.Fifo,
		AlphaMode = CompositeAlphaMode.Auto,
	});

	var blurModule = device.CreateShaderModuleWGSL(new() { Code = blurWGSL });
	var fullscreenModule = device.CreateShaderModuleWGSL(new() { Code = fullscreenTexturedQuadWGSL });

	var blurPipeline = device.CreateComputePipeline(new()
	{
		Layout = null,
		Compute = new()
		{
			Module = blurModule
		},
	});

	var fullscreenPipeline = device.CreateRenderPipeline(new()
	{
		Layout = null,
		Vertex = new()
		{
			Module = fullscreenModule,
		},
		Fragment = new()
		{
			Module = fullscreenModule,
			Targets = [new() { Format = surfaceFormat }],
		},
		Primitive = new()
		{
			Topology = PrimitiveTopology.TriangleList,
		},
	});

	var sampler = device.CreateSampler(new()
	{
		MagFilter = FilterMode.Linear,
		MinFilter = FilterMode.Linear,
	});

	var srcWidth = sourceImageData.Width;
	var srcHeight = sourceImageData.Height;

	var imageTexture = device.CreateTexture(new()
	{
		Size = new(sourceImageData.Width, sourceImageData.Height, 1),
		Format = TextureFormat.RGBA8Unorm,
		Usage =
		TextureUsage.TextureBinding |
		TextureUsage.CopyDst |
		TextureUsage.RenderAttachment,
	});

	ResourceUtils.CopyExternalImageToTexture(queue, sourceImageData, imageTexture);

	var textures = Enumerable.Range(0, 2).Select(_ => device.CreateTexture(new()
	{
		Size = new(sourceImageData.Width, sourceImageData.Height),
		Format = TextureFormat.RGBA8Unorm,
		Usage =
			TextureUsage.CopyDst |
			TextureUsage.StorageBinding |
			TextureUsage.TextureBinding,
	})).ToArray();

	var imageTextureView = imageTexture.CreateView();

	// A buffer with 0 in it. Binding this buffer is used to set `flip` to 0
	var buffer0 = device.CreateBuffer(new()
	{
		Size = sizeof(uint),
		Usage = BufferUsage.Uniform,
		MappedAtCreation = true,
	});
	buffer0.GetMappedRange<uint>(data => data[0] = 0);
	buffer0.Unmap();

	// A buffer with 1 in it. Binding this buffer is used to set `flip` to 1
	var buffer1 = device.CreateBuffer(new()
	{
		Size = sizeof(uint),
		MappedAtCreation = true,
		Usage = BufferUsage.Uniform,
	});
	buffer1.GetMappedRange<uint>(data => data[0] = 1);
	buffer1.Unmap();

	var blurParamsBuffer = device.CreateBuffer(new()
	{
		Size = (ulong)Unsafe.SizeOf<BlurParams>(),
		Usage = BufferUsage.CopyDst | BufferUsage.Uniform,
	});

	var computeConstants = device.CreateBindGroup(new()
	{
		Layout = blurPipeline.GetBindGroupLayout(0),
		Entries =
		[
			new()
			{
				Binding = 0,
				Sampler = sampler,
			},
			new()
			{
				Binding = 1,
				Buffer = blurParamsBuffer,
			},
		],
	});

	var computeBindGroup0 = device.CreateBindGroup(new()
	{
		Layout = blurPipeline.GetBindGroupLayout(1),
		Entries =
		[
			new()
			{
				Binding = 1,
				TextureView = imageTextureView,
			},
			new()
			{
				Binding = 2,
				TextureView = textures[0].CreateView(),
			},
			new()
			{
				Binding = 3,
				Buffer = buffer0,
			},
		],
	});

	var computeBindGroup1 = device.CreateBindGroup(new()
	{
		Layout = blurPipeline.GetBindGroupLayout(1),
		Entries =
		[
			new()
			{
				Binding = 1,
				TextureView = textures[0].CreateView(),
			},
			new()
			{
				Binding = 2,
				TextureView = textures[1].CreateView(),
			},
			new()
			{
				Binding = 3,
				Buffer = buffer1,
			},
		],
	});

	var computeBindGroup2 = device.CreateBindGroup(new()
	{
		Layout = blurPipeline.GetBindGroupLayout(1),
		Entries =
		[
			new()
			{
				Binding = 1,
				TextureView = textures[1].CreateView(),
			},
			new()
			{
				Binding = 2,
				TextureView = textures[0].CreateView(),
			},
			new()
			{
				Binding = 3,
				Buffer = buffer0,
			},
		],
	});

	var showResultBindGroup = device.CreateBindGroup(new()
	{
		Layout = fullscreenPipeline.GetBindGroupLayout(0),
		Entries =
		[
			new()
			{
				Binding = 0,
				Sampler = sampler,
			},
			new()
			{
				Binding = 1,
				TextureView = textures[1].CreateView(),
			},
		],
	});


	uint blockDim = 0;

	void UpdateSettings()
	{
		blockDim = TileDim - (uint)settings.FilterSize;
		queue.WriteBuffer(blurParamsBuffer, new BlurParams
		{
			FilterDim = settings.FilterSize + 1,
			BlockDim = blockDim,
		});
	}

	UpdateSettings();

	static uint DivRoundUp(uint value, uint divisor) => (value + divisor - 1) / divisor;

	runContext.OnFrame += () =>
	{
		var commandEncoder = device.CreateCommandEncoder();

		var computePass = commandEncoder.BeginComputePass();
		computePass.SetPipeline(blurPipeline);
		computePass.SetBindGroup(0, computeConstants);

		computePass.SetBindGroup(1, computeBindGroup0);
		computePass.DispatchWorkgroups(DivRoundUp(srcWidth, blockDim), DivRoundUp(srcHeight, BatchSize));

		computePass.SetBindGroup(1, computeBindGroup1);
		computePass.DispatchWorkgroups(DivRoundUp(srcHeight, blockDim), DivRoundUp(srcWidth, BatchSize));

		for (int i = 0; i < settings.Iterations - 1; ++i)
		{
			computePass.SetBindGroup(1, computeBindGroup2);
			computePass.DispatchWorkgroups(DivRoundUp(srcWidth, blockDim), DivRoundUp(srcHeight, BatchSize));

			computePass.SetBindGroup(1, computeBindGroup1);
			computePass.DispatchWorkgroups(DivRoundUp(srcHeight, blockDim), DivRoundUp(srcWidth, BatchSize));
		}

		computePass.End();

		RenderPassDescriptor renderPassDescriptor = new()
		{
			ColorAttachments =
			[
				new()
				{
					View = surface.GetCurrentTexture().Texture!.CreateView(),
					ClearValue = new(0f, 0f, 0f, 1f),
					LoadOp = LoadOp.Clear,
					StoreOp = StoreOp.Store,
				},
			],
		};

		var renderPass = commandEncoder.BeginRenderPass(renderPassDescriptor);
		renderPass.SetPipeline(fullscreenPipeline);
		renderPass.SetBindGroup(0, showResultBindGroup);
		renderPass.Draw(6);
		renderPass.End();

		var commandBuffer = commandEncoder.Finish();

		var guiCommandBuffer = DrawGui(guiContext, surface, out var filterSizeChanged);

		if (filterSizeChanged)
		{
			UpdateSettings();
		}

		queue.Submit([commandBuffer, guiCommandBuffer]);

		surface.Present();
	};
});


struct BlurParams
{
	public int FilterDim;
	public uint BlockDim;
}


sealed class BlurSettings
{
	public int FilterSize = 15;
	public int Iterations = 2;
}
