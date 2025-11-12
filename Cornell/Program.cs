using System.Reflection;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;

using Cornell;

const int WindowWidth = 1280;
const int WindowHeight = 720;

var assembly = Assembly.GetExecutingAssembly();
string LoadShader(string fileName) => Encoding.UTF8.GetString(ResourceUtils.GetEmbeddedResource($"Cornell.shaders.{fileName}", assembly));

var commonWGSL = LoadShader("common.wgsl");
var radiosityWGSL = LoadShader("radiosity.wgsl");
var rasterizerWGSL = LoadShader("rasterizer.wgsl");
var raytracerWGSL = LoadShader("raytracer.wgsl");
var tonemapperWGSL = LoadShader("tonemapper.wgsl");

var rendererLabels = Enum.GetNames<RendererMode>();
var settings = new CornellSettings();

return Run("Cornell Box", WindowWidth, WindowHeight, async (instance, surface, guiContext, onFrame) =>
{
	var adapter = await instance.RequestAdapterAsync(new()
	{
		CompatibleSurface = surface,
		FeatureLevel = FeatureLevel.Compatibility,
	}) ?? throw new InvalidOperationException("Failed to acquire a compatible WebGPU adapter.");

	var surfaceCapabilities = surface.GetCapabilities(adapter) ?? throw new InvalidOperationException("Unable to query surface capabilities.");
	var presentationFormat = surfaceCapabilities.Formats[0];
	bool surfaceSupportsStorage = (surfaceCapabilities.Usages & TextureUsage.StorageBinding) != 0;

	FeatureName[]? requiredFeatures = null;
	if (presentationFormat == TextureFormat.BGRA8Unorm)
	{
		if (adapter.HasFeature(FeatureName.BGRA8UnormStorage))
		{
			requiredFeatures = new[] { FeatureName.BGRA8UnormStorage };
		}
		else
		{
			bool supportsRgba8 = false;
			foreach (var format in surfaceCapabilities.Formats)
			{
				if (format == TextureFormat.RGBA8Unorm)
				{
					supportsRgba8 = true;
					break;
				}
			}

			if (supportsRgba8)
			{
				presentationFormat = TextureFormat.RGBA8Unorm;
			}
			else
			{
				throw new InvalidOperationException("Adapter does not support BGRA8Unorm storage and no RGBA8Unorm fallback is available.");
			}
		}
	}

	if (!adapter.GetLimits(out var adapterLimits))
	{
		throw new InvalidOperationException("Unable to query adapter limits.");
	}

	const uint RequiredWorkgroupSize = 256;
	if (adapterLimits.MaxComputeWorkgroupSizeX < RequiredWorkgroupSize || adapterLimits.MaxComputeInvocationsPerWorkgroup < RequiredWorkgroupSize)
	{
		throw new InvalidOperationException($"Adapter compute limits too low. Requires {RequiredWorkgroupSize} for MaxComputeWorkgroupSizeX and MaxComputeInvocationsPerWorkgroup.");
	}

	var device = await adapter.RequestDeviceAsync(new()
	{
		RequiredFeatures = requiredFeatures,
		RequiredLimits = LimitsDefaults.GetDefaultLimits() with
		{
			MaxComputeWorkgroupSizeX = RequiredWorkgroupSize,
			MaxComputeInvocationsPerWorkgroup = RequiredWorkgroupSize,
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

	guiContext.SetupIMGUI(device, presentationFormat);

	var surfaceUsage = TextureUsage.RenderAttachment;
	if (surfaceSupportsStorage)
	{
		surfaceUsage |= TextureUsage.StorageBinding;
	}

	surface.Configure(new()
	{
		Width = (uint)WindowWidth,
		Height = (uint)WindowHeight,
		Usage = surfaceUsage,
		Format = presentationFormat,
		Device = device,
		PresentMode = PresentMode.Fifo,
		AlphaMode = CompositeAlphaMode.Auto,
	});

	var framebuffer = device.CreateTexture(new()
	{
		Label = "Cornell.framebuffer",
		Size = new Extent3D((uint)WindowWidth, (uint)WindowHeight, 1),
		Format = TextureFormat.RGBA16Float,
		Usage = TextureUsage.RenderAttachment | TextureUsage.StorageBinding | TextureUsage.TextureBinding,
	});

	var scene = new Scene(device);
	var common = new Common(device, scene.QuadBuffer, commonWGSL);
	var radiosity = new Radiosity(device, common, scene);
	var rasterizer = new Rasterizer(device, common, scene, radiosity, framebuffer);
	var raytracer = new Raytracer(device, common, radiosity, framebuffer, raytracerWGSL, commonWGSL);
	var tonemapper = new Tonemapper(device, framebuffer, presentationFormat);
	Texture? tonemapPresentationTexture = surfaceSupportsStorage ? null : device.CreateTexture(new()
	{
		Label = "Cornell.tonemapOutput",
		Size = new Extent3D((uint)WindowWidth, (uint)WindowHeight, 1),
		Format = presentationFormat,
		Usage = TextureUsage.TextureBinding | TextureUsage.StorageBinding,
	});
	SurfaceBlitter? surfaceBlitter = surfaceSupportsStorage ? null : new SurfaceBlitter(device, presentationFormat);

	float framebufferAspect = framebuffer.GetWidth() / (float)framebuffer.GetHeight();

	onFrame(() =>
	{
		DrawGui(guiContext, rendererLabels, settings);

		var surfaceTexture = surface.GetCurrentTexture();
		if (surfaceTexture.Status is not (SurfaceGetCurrentTextureStatus.SuccessOptimal or SurfaceGetCurrentTextureStatus.SuccessSuboptimal))
		{
			return;
		}

		var outputTexture = surfaceTexture.Texture ?? throw new InvalidOperationException("Failed to acquire surface texture.");
		var outputView = outputTexture.CreateView() ?? throw new InvalidOperationException("Failed to create surface texture view.");

		var commandEncoder = device.CreateCommandEncoder();

		common.Update(settings.RotateCamera, framebufferAspect);
		radiosity.Run(commandEncoder);

		switch (settings.Renderer)
		{
			case RendererMode.Rasterizer:
				rasterizer.Run(commandEncoder);
				break;
			case RendererMode.Raytracer:
				raytracer.Run(commandEncoder);
				break;
		}

		var tonemapTarget = surfaceSupportsStorage ? outputTexture : tonemapPresentationTexture!;
		tonemapper.Run(commandEncoder, tonemapTarget);

		if (!surfaceSupportsStorage)
		{
			surfaceBlitter!.Blit(commandEncoder, tonemapTarget, outputView);
		}

		RenderGuiOverlay(commandEncoder, outputView);

		var commandBuffer = commandEncoder.Finish();
		queue.Submit([commandBuffer]);
		surface.Present();
	});
});

static void DrawGui(GuiContext guiContext, string[] rendererLabels, CornellSettings settings)
{
	guiContext.NewFrame();

	ImGui.SetNextWindowBgAlpha(0.85f);
	ImGui.SetNextWindowPos(new(16, 16), ImGuiCond.FirstUseEver);
	ImGui.SetNextWindowSize(new(260, 110), ImGuiCond.FirstUseEver);

	ImGui.Begin("Cornell Controls", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);

	int rendererIndex = (int)settings.Renderer;
	if (ImGui.Combo("Renderer", ref rendererIndex, rendererLabels, rendererLabels.Length))
	{
		settings.Renderer = (RendererMode)rendererIndex;
	}

	bool rotateCamera = settings.RotateCamera;
	if (ImGui.Checkbox("Rotate Camera", ref rotateCamera))
	{
		settings.RotateCamera = rotateCamera;
	}

	ImGui.End();

	guiContext.EndFrame();
}

static void RenderGuiOverlay(CommandEncoder commandEncoder, TextureView outputView)
{
	ImGui.Render();

	var renderPassDescriptor = new RenderPassDescriptor
	{
		ColorAttachments =
		[
			new RenderPassColorAttachment
			{
				View = outputView,
				LoadOp = LoadOp.Load,
				StoreOp = StoreOp.Store,
			},
		],
	};

	var passEncoder = commandEncoder.BeginRenderPass(renderPassDescriptor);
	ImGui_Impl_WebGPUSharp.RenderDrawData(ImGui.GetDrawData(), passEncoder);
	passEncoder.End();
}

enum RendererMode
{
	Rasterizer,
	Raytracer,
}

sealed class CornellSettings
{
	public RendererMode Renderer { get; set; } = RendererMode.Rasterizer;
	public bool RotateCamera { get; set; } = true;
}

sealed class SurfaceBlitter
{
	private readonly Device _device;
	private readonly Sampler _sampler;
	private readonly BindGroupLayout _layout;
	private readonly RenderPipeline _pipeline;

	private const string BlitShader = """
struct VertexOutput {
	@builtin(position) position : vec4f,
	@location(0) uv : vec2f,
};

@vertex
fn vs_main(@builtin(vertex_index) vertexIndex : u32) -> VertexOutput {
	var positions = array<vec2f, 3>(
		vec2f(-1.0, -3.0),
		vec2f(3.0, 1.0),
		vec2f(-1.0, 1.0)
	);
	var uvs = array<vec2f, 3>(
		vec2f(0.0, 2.0),
		vec2f(2.0, 0.0),
		vec2f(0.0, 0.0)
	);

	var output : VertexOutput;
	output.position = vec4f(positions[vertexIndex], 0.0, 1.0);
	output.uv = uvs[vertexIndex];
	return output;
}

@group(0) @binding(0) var blitSampler : sampler;
@group(0) @binding(1) var blitTexture : texture_2d<f32>;

@fragment
fn fs_main(input : VertexOutput) -> @location(0) vec4f {
	return textureSample(blitTexture, blitSampler, input.uv);
}
""";

	public SurfaceBlitter(Device device, TextureFormat surfaceFormat)
	{
		_device = device;
		_sampler = device.CreateSampler(new()
		{
			AddressModeU = AddressMode.ClampToEdge,
			AddressModeV = AddressMode.ClampToEdge,
			MagFilter = FilterMode.Linear,
			MinFilter = FilterMode.Linear,
		})!;

		_layout = device.CreateBindGroupLayout(new()
		{
			Entries =
			[
				new BindGroupLayoutEntry
				{
					Binding = 0,
					Visibility = ShaderStage.Fragment,
					Sampler = new SamplerBindingLayout(),
				},
				new BindGroupLayoutEntry
				{
					Binding = 1,
					Visibility = ShaderStage.Fragment,
					Texture = new TextureBindingLayout
					{
						ViewDimension = TextureViewDimension.D2,
					},
				},
			],
		})!;

		var shaderModule = device.CreateShaderModuleWGSL(new() { Code = Encoding.UTF8.GetBytes(BlitShader) })!;

		var pipelineLayout = device.CreatePipelineLayout(new()
		{
			BindGroupLayouts = [_layout],
		})!;

		_pipeline = device.CreateRenderPipeline(new()
		{
			Layout = pipelineLayout,
			Vertex = ref InlineInit(new VertexState
			{
				Module = shaderModule,
				EntryPoint = "vs_main",
			}),
			Fragment = new FragmentState
			{
				Module = shaderModule,
				EntryPoint = "fs_main",
				Targets = [new ColorTargetState { Format = surfaceFormat }],
			},
			Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList },
		})!;
	}

	public void Blit(CommandEncoder commandEncoder, Texture source, TextureView destinationView)
	{
		var sourceView = source.CreateView() ?? throw new InvalidOperationException("Failed to create blit source view.");
		var bindGroup = _device.CreateBindGroup(new()
		{
			Layout = _layout,
			Entries =
			[
				new BindGroupEntry
				{
					Binding = 0,
					Sampler = _sampler,
				},
				new BindGroupEntry
				{
					Binding = 1,
					TextureView = sourceView,
				},
			],
		})!;

		var passDescriptor = new RenderPassDescriptor
		{
			ColorAttachments =
			[
				new RenderPassColorAttachment
				{
					View = destinationView,
					LoadOp = LoadOp.Clear,
					ClearValue = new Color(0, 0, 0, 1),
					StoreOp = StoreOp.Store,
				},
			],
		};

		var pass = commandEncoder.BeginRenderPass(passDescriptor);
		pass.SetPipeline(_pipeline);
		pass.SetBindGroup(0, bindGroup);
		pass.Draw(3);
		pass.End();
	}
}
