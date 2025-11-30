using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Cornell;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 1280;
const int HEIGHT = 720;


var parameters = new Parameters();


CommandBuffer DrawGUI(GuiContext guiContext, Surface surface)
{
	guiContext.NewFrame();
	ImGui.SetNextWindowBgAlpha(0.75f);
	ImGui.SetNextWindowPos(new(980, 0));
	ImGui.SetNextWindowSize(new(300, 80));
	ImGui.Begin("Settings",
		ImGuiWindowFlags.NoMove |
		ImGuiWindowFlags.NoResize
	);

	ImGuiUtils.EnumDropdown("renderer", ref parameters.Renderer);
	ImGui.Checkbox("rotateCamera", ref parameters.RotateCamera);

	ImGui.End();
	guiContext.EndFrame();
	return guiContext.Render(surface)!.Value!;
}

return Run("Cornell", WIDTH, HEIGHT, async runContext =>
{
	var startTimeStamp = Stopwatch.GetTimestamp();

	var instance = runContext.GetInstance();
	var surface = runContext.GetSurface();
	var guiContext = runContext.GetGuiContext();

	var adapter = await instance.RequestAdapterAsync(new()
	{
		CompatibleSurface = surface,
		BackendType = BackendType.Vulkan,
		FeatureLevel = FeatureLevel.Compatibility,
		PowerPreference = PowerPreference.HighPerformance,
	});

	var surfaceCapabilities = surface.GetCapabilities(adapter)!;
	var surfaceFormat = surfaceCapabilities.Formats[0];

	var adapterFeatures = adapter.GetFeatures();
	var adapterLimits = adapter.GetLimits()!.Value;
	Debug.Assert(adapterFeatures != null);

	List<FeatureName> requiredFeatures = new();
	if (surfaceFormat == TextureFormat.BGRA8Unorm)
	{
		if (adapterFeatures.Contains(FeatureName.BGRA8UnormStorage))
		{
			requiredFeatures.Add(FeatureName.BGRA8UnormStorage);
		}
		else
		{
			// If the GPU prefers BGRA for presentation but the Adapter
			// doesn't support bgra8unorm-storage (e.g., Compatibility
			// mode), use RGBA8Unorm for both. This will be slower, but will
			// work.
			surfaceFormat = TextureFormat.RGBA8Unorm;
		}
	}

	Debug.Assert(adapterLimits.MaxComputeWorkgroupSizeX >= 256);
	Debug.Assert(adapterLimits.MaxComputeInvocationsPerWorkgroup >= 256);


	var device = await adapter.RequestDeviceAsync(new()
	{
		RequiredFeatures = CollectionsMarshal.AsSpan(requiredFeatures),
		RequiredLimits = new()
		{
			MaxComputeWorkgroupSizeX = 256,
			MaxComputeInvocationsPerWorkgroup = 256,
		},

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

	guiContext.SetupIMGUI(device, surfaceFormat);

	surface.Configure(new SurfaceConfiguration
	{
		Width = WIDTH,
		Height = HEIGHT,
		Device = device,
		Format = surfaceFormat,
		Usage = TextureUsage.RenderAttachment | TextureUsage.StorageBinding,
		PresentMode = PresentMode.Fifo,
		AlphaMode = CompositeAlphaMode.Auto,
	});

	var framebuffer = device.CreateTexture(new()
	{
		Label = "framebuffer",
		Size = new(WIDTH, HEIGHT),
		Format = TextureFormat.RGBA16Float,
		Usage =
			TextureUsage.RenderAttachment |
			TextureUsage.StorageBinding |
			TextureUsage.TextureBinding,
	});

	var scene = new Scene(device);
	var common = new Common(device, scene.QuadBuffer);
	var radiosity = new Radiosity(device, common, scene);
	var rasterizer = new Rasterizer(
		device: device,
		common: common,
		scene: scene,
		radiosity: radiosity,
		framebuffer: framebuffer
	);
	var raytracer = new Raytracer(
		device: device,
		common: common,
		radiosity: radiosity,
		framebuffer: framebuffer
	);


	runContext.OnFrame += () =>
	{
		var surfaceTexture = surface.GetCurrentTexture().Texture!;
		var commandEncoder = device.CreateCommandEncoder();

		common.Update(
			rotateCamera: parameters.RotateCamera,
			aspect: (float)WIDTH / HEIGHT
		);
		radiosity.Run(commandEncoder);

		switch (parameters.Renderer)
		{
			case Parameters.RendererType.Rasterizer:
				rasterizer.Run(commandEncoder);
				break;
			case Parameters.RendererType.Raytracer:
				raytracer.Run(commandEncoder);
				break;
		}

		var tonemapper = new Tonemapper(
			device: device,
			input: framebuffer,
			outputTexture: surfaceTexture
		);

		tonemapper.Run(commandEncoder);

		var guiCommandBuffer = DrawGUI(guiContext, surface);

		queue.Submit([commandEncoder.Finish(), guiCommandBuffer]);
		surface.Present();
	};
});


class Parameters
{
	public enum RendererType
	{
		Rasterizer,
		Raytracer
	}

	public RendererType Renderer = RendererType.Rasterizer;
	public bool RotateCamera = true;
}