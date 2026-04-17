using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using GuiSetup;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using GPUBuffer = WebGpuSharp.Buffer;

const int WIDTH = 600;
const int HEIGHT = 600;

var executingAssembly = Assembly.GetExecutingAssembly();

var texturedQuadWgsl = ResourceUtils.GetEmbeddedResource("Blending.shaders.texturedQuad.wgsl", executingAssembly);
var bgImageData = ResourceUtils.LoadImagePngFromManifestResource(executingAssembly, "Blending.assets.background.png");
var srcImageData = ResourceUtils.LoadImagePngFromManifestResource(executingAssembly, "Blending.assets.sourceImage.png");
var dstImageData = ResourceUtils.LoadImagePngFromManifestResource(executingAssembly, "Blending.assets.destinationImage.png");

static ImageData PreMultiplyAlpha(ImageData source)
{
	var data = (byte[])source.Data.Clone();
	for (int i = 0; i < data.Length; i += 4)
	{
		float a = data[i + 3] / 255f;
		data[i + 0] = (byte)(data[i + 0] * a);
		data[i + 1] = (byte)(data[i + 1] * a);
		data[i + 2] = (byte)(data[i + 2] * a);
	}
	return new ImageData(data, source.Width, source.Height);
}

Dictionary<PresetType, (BlendComponent Color, BlendComponent? Alpha)> presets = new()
{
	[PresetType.Default] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.Zero }, null),
	[PresetType.PremultipliedBlend] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha }, null),
	[PresetType.UnpremultipliedBlend] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha }, null),
	[PresetType.DestinationOver] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.One }, null),
	[PresetType.SourceIn] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.Zero }, null),
	[PresetType.DestinationIn] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.SrcAlpha }, null),
	[PresetType.SourceOut] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.Zero }, null),
	[PresetType.DestinationOut] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.OneMinusSrcAlpha }, null),
	[PresetType.SourceAtop] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha }, null),
	[PresetType.DestinationAtop] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.SrcAlpha }, null),
	[PresetType.Additive] = (new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One }, null),
};

PresetType currentPreset = PresetType.PremultipliedBlend;
BlendComponent colorBlend = presets[currentPreset].Color;
BlendComponent alphaBlend = presets[currentPreset].Alpha ?? colorBlend;
Vector3 constantColor = new(1f, 0.5f, 0.25f);
float constantAlpha = 1f;
Vector3 clearColor = Vector3.Zero;
float clearAlpha = 0f;
bool premultiplyClearColor = true;
TextureSetType currentTextureSet = TextureSetType.PremultipliedAlpha;

void ApplyPreset()
{
	var (pc, pa) = presets[currentPreset];
	colorBlend = pc;
	alphaBlend = pa ?? pc;
}


static void MakeBlendComponentValid(ref BlendComponent bc)
{
	if (bc.Operation == BlendOperation.Min || bc.Operation == BlendOperation.Max)
	{
		bc.SrcFactor = BlendFactor.One;
		bc.DstFactor = BlendFactor.One;
	}
}

CommandBuffer DrawGui(
   DearImGuiContext guiContext,
   Surface surface
)
{
	static bool BlendComponentCombo(string label, ref BlendComponent bc)
	{
		ReadOnlySpan<BlendOperation> allowedOps = [
			BlendOperation.Add,
			BlendOperation.Subtract,
			BlendOperation.ReverseSubtract,
			BlendOperation.Min,
			BlendOperation.Max
		];
		ReadOnlySpan<BlendFactor> allowedFactors = [
			BlendFactor.Zero,
			BlendFactor.One,
			BlendFactor.Src,
			BlendFactor.OneMinusSrc,
			BlendFactor.SrcAlpha,
			BlendFactor.OneMinusSrcAlpha,
			BlendFactor.Dst,
			BlendFactor.OneMinusDst,
			BlendFactor.DstAlpha,
			BlendFactor.OneMinusDstAlpha,
			BlendFactor.SrcAlphaSaturated,
			BlendFactor.Constant,
			BlendFactor.OneMinusConstant,
		];

		bool changed = false;
		changed |= ImGuiUtils.EnumDropdown($"operation##{label}", ref bc.Operation, allowedOps);
		changed |= ImGuiUtils.EnumDropdown($"srcFactor##{label}", ref bc.SrcFactor, allowedFactors);
		changed |= ImGuiUtils.EnumDropdown($"dstFactor##{label}", ref bc.DstFactor, allowedFactors);
		return changed;
	}

	guiContext.NewFrame();
	ImGui.SetNextWindowBgAlpha(0.85f);
	ImGui.SetNextWindowPos(new(410, 10));
	ImGui.SetNextWindowSize(new(340, 0));
	ImGui.Begin("Blending", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize);

	if (ImGuiUtils.EnumDropdown("preset", ref currentPreset))
	{
		ApplyPreset();
	}

	ImGuiUtils.EnumDropdown("texture data", ref currentTextureSet);

	if (ImGui.CollapsingHeader("color", ImGuiTreeNodeFlags.DefaultOpen))
	{
		BlendComponentCombo("color", ref colorBlend);
	}
	if (ImGui.CollapsingHeader("alpha", ImGuiTreeNodeFlags.DefaultOpen))
	{
		BlendComponentCombo("alpha", ref alphaBlend);
	}
	if (ImGui.CollapsingHeader("constant", ImGuiTreeNodeFlags.DefaultOpen))
	{
		ImGui.ColorEdit3("color##constant", ref constantColor);
		ImGui.SliderFloat("alpha##constant", ref constantAlpha, 0f, 1f);
	}
	if (ImGui.CollapsingHeader("clear color", ImGuiTreeNodeFlags.DefaultOpen))
	{
		ImGui.Checkbox("premultiply##clear", ref premultiplyClearColor);
		ImGui.ColorEdit3("color##clear", ref clearColor);
		ImGui.SliderFloat("alpha##clear", ref clearAlpha, 0f, 1f);
	}

	ImGui.End();
	guiContext.EndFrame();

	return guiContext.Render(surface)!.Value!;
}

return Run("Blending", WIDTH, HEIGHT, async runContext =>
{
	var instance = runContext.GetInstance();
	var surface = runContext.GetSurface();
	var guiContext = runContext.CreateGuiContext<DearImGuiContext>();

	var adapter = (await instance.RequestAdapterAsync(new()
	{
		CompatibleSurface = surface,
	}))!;

	var device = (await adapter.RequestDeviceAsync(new()
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
	}))!;

	var queue = device.GetQueue();
	var surfaceCapabilities = surface.GetCapabilities(adapter)!;
	var surfaceFormat = surfaceCapabilities.Formats[0];

	guiContext.SetupIMGUI(device, surfaceFormat);

	surface.Configure(new()
	{
		Width = WIDTH,
		Height = HEIGHT,
		Usage = TextureUsage.RenderAttachment,
		Format = surfaceFormat,
		Device = device,
		PresentMode = PresentMode.Fifo,
		AlphaMode = CompositeAlphaMode.Auto,
	});

	var module = device.CreateShaderModuleWGSL(new()
	{
		Code = texturedQuadWgsl,
	});


	var bindGroupLayout = device.CreateBindGroupLayout(new()
	{
		Entries =
		[
			new() { Binding = 0, Visibility = ShaderStage.Fragment, Sampler = new() },
			new() { Binding = 1, Visibility = ShaderStage.Fragment, Texture = new() },
			new() { Binding = 2, Visibility = ShaderStage.Vertex, Buffer = new() },
		],
	});


	var pipelineLayout = device.CreatePipelineLayout(new()
	{
		BindGroupLayouts = [bindGroupLayout],
	});


	var srcImageDataPremultiplied = PreMultiplyAlpha(srcImageData);
	var dstImageDataPremultiplied = PreMultiplyAlpha(dstImageData);

	Texture CreateTextureFromImageData(ImageData imageData)
	{
		var texture = device.CreateTexture(new()
		{
			Format = TextureFormat.RGBA8Unorm,
			Size = new(imageData.Width, imageData.Height),
			Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.RenderAttachment,
		});
		ResourceUtils.CopyExternalImageToTexture(queue, imageData, texture, imageData.Width, imageData.Height);
		return texture;
	}

	var bgTexture = CreateTextureFromImageData(bgImageData);
	var srcTextureUnpremultiplied = CreateTextureFromImageData(srcImageData);
	var dstTextureUnpremultiplied = CreateTextureFromImageData(dstImageData);
	var srcTexturePremultiplied = CreateTextureFromImageData(srcImageDataPremultiplied);
	var dstTexturePremultiplied = CreateTextureFromImageData(dstImageDataPremultiplied);

	var sampler = device.CreateSampler(new()
	{
		MagFilter = FilterMode.Linear,
		MinFilter = FilterMode.Linear,
		MipmapFilter = MipmapFilterMode.Linear,
	});

	GPUBuffer CreateUniformBuffer()
	{
		return device.CreateBuffer(new()
		{
			Label = "uniforms for quad",
			Size = (ulong)Unsafe.SizeOf<Matrix4x4>(),
			Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
		});
	}

	var bgUniformBuffer = CreateUniformBuffer();
	var srcUniformBuffer = CreateUniformBuffer();
	var dstUniformBuffer = CreateUniformBuffer();

	var bgBindGroup = device.CreateBindGroup(new()
	{
		Layout = bindGroupLayout,
		Entries =
		[
			new() { Binding = 0, Sampler = sampler },
			new() { Binding = 1, TextureView = bgTexture.CreateView() },
			new() { Binding = 2, Buffer = bgUniformBuffer },
		],
	});

	var srcBindGroupUnpremultipliedAlpha = device.CreateBindGroup(new()
	{
		Layout = bindGroupLayout,
		Entries =
		[
			new() { Binding = 0, Sampler = sampler },
			new() { Binding = 1, TextureView = srcTextureUnpremultiplied.CreateView() },
			new() { Binding = 2, Buffer = srcUniformBuffer },
		],
	});

	var dstBindGroupUnpremultipliedAlpha = device.CreateBindGroup(new()
	{
		Layout = bindGroupLayout,
		Entries =
		[
			new() { Binding = 0, Sampler = sampler },
			new() { Binding = 1, TextureView = dstTextureUnpremultiplied.CreateView() },
			new() { Binding = 2, Buffer = dstUniformBuffer },
		],
	});

	var srcBindGroupPremultipliedAlpha = device.CreateBindGroup(new()
	{
		Layout = bindGroupLayout,
		Entries =
		[
			new() { Binding = 0, Sampler = sampler },
			new() { Binding = 1, TextureView = srcTexturePremultiplied.CreateView() },
			new() { Binding = 2, Buffer = srcUniformBuffer },
		],
	});

	var dstBindGroupPremultipliedAlpha = device.CreateBindGroup(new()
	{
		Layout = bindGroupLayout,
		Entries =
		[
			new() { Binding = 0, Sampler = sampler },
			new() { Binding = 1, TextureView = dstTexturePremultiplied.CreateView() },
			new() { Binding = 2, Buffer = dstUniformBuffer },
		],
	});

	var textureSets = new Dictionary<TextureSetType, (Texture SrcTex, Texture DstTex, BindGroup SrcBG, BindGroup DstBG)>
	{
		[TextureSetType.PremultipliedAlpha] = (srcTexturePremultiplied, dstTexturePremultiplied, srcBindGroupPremultipliedAlpha, dstBindGroupPremultipliedAlpha),
		[TextureSetType.UnpremultipliedAlpha] = (srcTextureUnpremultiplied, dstTextureUnpremultiplied, srcBindGroupUnpremultipliedAlpha, dstBindGroupUnpremultipliedAlpha),
	};

	// background pipeline: plain copy — fills the canvas with the background image
	var bgPipeline = device.CreateRenderPipelineSync(new()
	{
		Label = "background quad pipeline",
		Layout = pipelineLayout,
		Vertex = new() { Module = module },
		Fragment = new()
		{
			Module = module,
			Targets = [new() { Format = surfaceFormat }],
		},
	});

	var dstPipeline = device.CreateRenderPipelineSync(new()
	{
		Label = "hardcoded textured quad pipeline",
		Layout = pipelineLayout,
		Vertex = new() { Module = module },
		Fragment = new()
		{
			Module = module,
			Targets =
			[
				new()
				{
					Format = surfaceFormat
				},
			],
		},
	});

	// composite pipeline: source-over — blends the intermediate result over the background
	var compositePipeline = device.CreateRenderPipelineSync(new()
	{
		Label = "composite pipeline",
		Layout = pipelineLayout,
		Vertex = new() { Module = module },
		Fragment = new()
		{
			Module = module,
			Targets =
			[
				new()
				{
					Format = surfaceFormat,
					Blend = new BlendState
					{
						Color = new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
						Alpha = new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
					},
				},
			],
		},
	});

	// Offscreen texture: dst+src are blended here, isolated from the background
	var intermediateTexture = device.CreateTexture(new()
	{
		Format = surfaceFormat,
		Size = new(WIDTH, HEIGHT),
		Usage = TextureUsage.TextureBinding | TextureUsage.RenderAttachment,
	});
	var intermediateUniformBuffer = CreateUniformBuffer();
	var intermediateBindGroup = device.CreateBindGroup(new()
	{
		Layout = bindGroupLayout,
		Entries =
		[
			new() { Binding = 0, Sampler = sampler },
			new() { Binding = 1, TextureView = intermediateTexture.CreateView() },
			new() { Binding = 2, Buffer = intermediateUniformBuffer },
		],
	});

	ApplyPreset();

	void UpdateUniform(GPUBuffer buffer, uint texW, uint texH)
	{
		var proj = Matrix4x4.CreateOrthographicOffCenter(0, WIDTH, HEIGHT, 0, -1f, 1f);
		var scale = Matrix4x4.CreateScale(texW, texH, 1f);
		var matrix = scale * proj;
		queue.WriteBuffer(buffer, 0, matrix);
	}

	runContext.OnFrame += () =>
	{
		MakeBlendComponentValid(ref colorBlend);
		MakeBlendComponentValid(ref alphaBlend);

		// Build src pipeline each frame with current blend settings
		var srcPipeline = device.CreateRenderPipelineSync(new()
		{
			Label = "hardcoded textured quad pipeline",
			Layout = pipelineLayout,
			Vertex = new() { Module = module },
			Fragment = new()
			{
				Module = module,
				Targets =
				[
					new()
					{
						Format = surfaceFormat,
						Blend = new BlendState
						{
							Color = colorBlend,
							Alpha = alphaBlend,
						},
					},
				],
			},
		});

		var (srcTex, dstTex, srcBG, dstBG) = textureSets[currentTextureSet];

		UpdateUniform(bgUniformBuffer, (uint)WIDTH, (uint)HEIGHT);
		UpdateUniform(intermediateUniformBuffer, (uint)WIDTH, (uint)HEIGHT);
		UpdateUniform(dstUniformBuffer, dstTex.GetWidth(), dstTex.GetHeight());
		UpdateUniform(srcUniformBuffer, srcTex.GetWidth(), srcTex.GetHeight());

		var canvasView = surface.GetCurrentTexture().Texture!.CreateView();
		var intermediateView = intermediateTexture.CreateView();

		var encoder = device.CreateCommandEncoder(new() { Label = "render quad encoder" });

		Color clearValue;
		{
			var mult = premultiplyClearColor ? clearAlpha : 1f;
			clearValue = new(
				r: clearColor.X * mult,
				g: clearColor.Y * mult,
				b: clearColor.Z * mult,
				a: clearAlpha
			);
		}

		// Pass 1: render dst+src into the intermediate texture (transparent clear, isolated from background)
		{
			var pass = encoder.BeginRenderPass(new()
			{
				Label = "blend pass",
				ColorAttachments =
				[
					new()
					{
						View = intermediateView,
						ClearValue = clearValue,
						LoadOp = LoadOp.Clear,
						StoreOp = StoreOp.Store,
					},
				],
			});

			pass.SetPipeline(dstPipeline);
			pass.SetBindGroup(0, dstBG);
			pass.Draw(6);

			pass.SetPipeline(srcPipeline);
			pass.SetBindGroup(0, srcBG);
			pass.SetBlendConstant(new Color(constantColor.X, constantColor.Y, constantColor.Z, constantAlpha));
			pass.Draw(6);

			pass.End();
		}

		// Pass 2: clear canvas and draw background
		{
			var pass = encoder.BeginRenderPass(new()
			{
				Label = "background pass",
				ColorAttachments =
				[
					new()
					{
						View = canvasView,
						ClearValue = new Color(0, 0, 0, 0),
						LoadOp = LoadOp.Clear,
						StoreOp = StoreOp.Store,
					},
				],
			});
			pass.SetPipeline(bgPipeline);
			pass.SetBindGroup(0, bgBindGroup);
			pass.Draw(6);
			pass.End();
		}

		// Pass 3: composite the intermediate result over the background
		{
			var pass = encoder.BeginRenderPass(new()
			{
				Label = "composite pass",
				ColorAttachments =
				[
					new()
					{
						View = canvasView,
						LoadOp = LoadOp.Load,
						StoreOp = StoreOp.Store,
					},
				],
			});
			pass.SetPipeline(compositePipeline);
			pass.SetBindGroup(0, intermediateBindGroup);
			pass.Draw(6);
			pass.End();
		}

		// GUI
		var guiCommandBuffer = DrawGui(
			guiContext: guiContext,
			surface: surface
		);

		queue.Submit([encoder.Finish(), guiCommandBuffer]);

		if (!OperatingSystem.IsBrowser())
		{
			surface.Present();
		}
	};
});

enum PresetType
{
	[ImGuiDisplayName("default (copy)")]
	Default,
	[ImGuiDisplayName("premultiplied blend (source-over)")]
	PremultipliedBlend,
	[ImGuiDisplayName("un-premultiplied blend")]
	UnpremultipliedBlend,
	[ImGuiDisplayName("destination-over")]
	DestinationOver,
	[ImGuiDisplayName("source-in")]
	SourceIn,
	[ImGuiDisplayName("destination-in")]
	DestinationIn,
	[ImGuiDisplayName("source-out")]
	SourceOut,
	[ImGuiDisplayName("destination-out")]
	DestinationOut,
	[ImGuiDisplayName("source-atop")]
	SourceAtop,
	[ImGuiDisplayName("destination-atop")]
	DestinationAtop,
	[ImGuiDisplayName("additive (lighten)")]
	Additive,
}

enum TextureSetType
{
	[ImGuiDisplayName("premultiplied alpha")]
	PremultipliedAlpha,
	[ImGuiDisplayName("un-premultiplied alpha")]
	UnpremultipliedAlpha,
}
