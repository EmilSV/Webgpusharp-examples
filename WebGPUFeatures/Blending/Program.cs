using System.Numerics;
using System.Reflection;
using System.Text;
using GuiSetup;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;
using static Setup.SetupWebGPU;

const int WINDOW_WIDTH = 1200;
const int WINDOW_HEIGHT = 720;
const int DEMO_TEXTURE_SIZE = 300;
const int CHECKER_SIZE = 32;

string[] alphaModeNames = ["opaque", "premultiplied"];
CompositeAlphaMode[] alphaModes = [CompositeAlphaMode.Opaque, CompositeAlphaMode.Premultiplied];
string[] textureSetNames = ["premultiplied alpha", "un-premultiplied alpha"];

string[] operationNames = ["add", "subtract", "reverse-subtract", "min", "max"];
BlendOperation[] operationValues = [
	BlendOperation.Add,
	BlendOperation.Subtract,
	BlendOperation.ReverseSubtract,
	BlendOperation.Min,
	BlendOperation.Max,
];

string[] factorNames = [
	"zero",
	"one",
	"src",
	"one-minus-src",
	"src-alpha",
	"one-minus-src-alpha",
	"dst",
	"one-minus-dst",
	"dst-alpha",
	"one-minus-dst-alpha",
	"src-alpha-saturated",
	"constant",
	"one-minus-constant",
];

BlendFactor[] factorValues = [
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

PresetDefinition[] presets = [
	new("default (copy)",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.Zero }),
	new("premultiplied blend (source-over)",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha }),
	new("un-premultiplied blend",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha }),
	new("destination-over",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.One }),
	new("source-in",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.Zero }),
	new("destination-in",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.SrcAlpha }),
	new("source-out",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.Zero }),
	new("destination-out",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.OneMinusSrcAlpha }),
	new("source-atop",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha }),
	new("destination-atop",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.SrcAlpha }),
	new("additive (lighten)",
		new() { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One }),
];

string[] presetNames = presets.Select(preset => preset.Name).ToArray();

Settings settings = new()
{
	AlphaModeIndex = 1,
	TextureSetIndex = 0,
	PresetIndex = 1,
};

BlendComponentState color = new()
{
	Operation = BlendOperation.Add,
	SrcFactor = BlendFactor.One,
	DstFactor = BlendFactor.OneMinusSrc,
};

BlendComponentState alpha = new()
{
	Operation = BlendOperation.Add,
	SrcFactor = BlendFactor.One,
	DstFactor = BlendFactor.OneMinusSrc,
};

DemoColor constant = new([1.0f, 0.5f, 0.25f], 1.0f);
ClearSettings clear = new([0.0f, 0.0f, 0.0f], 0.0f, true);

ApplyPreset();

CommandBuffer DrawGui(DearImGuiContext guiContext, Surface surface, ref bool sourcePipelineDirty)
{
	guiContext.NewFrame();

	ImGui.SetNextWindowPos(new(WINDOW_WIDTH - 360, 10), ImGuiCond.Once);
	ImGui.SetNextWindowSize(new(350, WINDOW_HEIGHT - 20), ImGuiCond.Once);
	ImGui.Begin("Blending",
		ImGuiWindowFlags.NoCollapse |
		ImGuiWindowFlags.NoResize
	);

	if (ComboFromLabels("canvas alphaMode", alphaModeNames, ref settings.AlphaModeIndex))
	{
	}

	if (ComboFromLabels("texture data", textureSetNames, ref settings.TextureSetIndex))
	{
	}

	if (ComboFromLabels("preset", presetNames, ref settings.PresetIndex))
	{
		ApplyPreset();
		sourcePipelineDirty = true;
	}

	if (ImGui.TreeNode("color"))
	{
		sourcePipelineDirty |= DrawBlendComponentControls(ref color);
		ImGui.TreePop();
	}

	if (ImGui.TreeNode("alpha"))
	{
		sourcePipelineDirty |= DrawBlendComponentControls(ref alpha);
		ImGui.TreePop();
	}

	if (ImGui.TreeNode("constant"))
	{
		Vector3 constantColor = ToVector3(constant.Color);
		if (ImGui.ColorEdit3("color", ref constantColor))
		{
			FromVector3(constantColor, constant.Color);
		}

		if (ImGui.SliderFloat("alpha", ref constant.Alpha, 0.0f, 1.0f))
		{
		}

		ImGui.TreePop();
	}

	if (ImGui.TreeNode("clear color"))
	{
		Vector3 clearColor = ToVector3(clear.Color);
		if (ImGui.Checkbox("premultiply", ref clear.Premultiply))
		{
		}

		if (ImGui.SliderFloat("clear alpha", ref clear.Alpha, 0.0f, 1.0f))
		{
		}

		if (ImGui.ColorEdit3("clear color", ref clearColor))
		{
			FromVector3(clearColor, clear.Color);
		}

		ImGui.TreePop();
	}

	ImGui.End();
	guiContext.EndFrame();
	return guiContext.Render(surface)!.Value!;
}

return Run("Blending", WINDOW_WIDTH, WINDOW_HEIGHT, async runContext =>
{
	var instance = runContext.GetInstance();
	var surface = runContext.GetSurface();
	var guiContext = runContext.CreateGuiContext<DearImGuiContext>();
	var executingAssembly = Assembly.GetExecutingAssembly();

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
	var devicePixelRatio = runContext.GetDevicePixelRatio();
	var surfaceWidth = (uint)MathF.Round(WINDOW_WIDTH * devicePixelRatio);
	var surfaceHeight = (uint)MathF.Round(WINDOW_HEIGHT * devicePixelRatio);

	guiContext.SetupIMGUI(device, surfaceFormat);

	var shaderModule = device.CreateShaderModuleWGSL(new()
	{
		Code = ResourceUtils.GetEmbeddedResource("Blending.shaders.texturedQuad.wgsl", executingAssembly),
	});

	var bindGroupLayout = device.CreateBindGroupLayout(new()
	{
		Entries = [
			new()
			{
				Binding = 0,
				Visibility = ShaderStage.Fragment,
				Sampler = new(),
			},
			new()
			{
				Binding = 1,
				Visibility = ShaderStage.Fragment,
				Texture = new(),
			},
			new()
			{
				Binding = 2,
				Visibility = ShaderStage.Vertex,
				Buffer = new(),
			},
		],
	});

	var pipelineLayout = device.CreatePipelineLayout(new()
	{
		BindGroupLayouts = [bindGroupLayout],
	});

	var opaquePipeline = CreateOpaquePipeline(device, pipelineLayout, shaderModule, surfaceFormat);
	RenderPipeline sourcePipeline = CreateSourcePipeline(device, pipelineLayout, shaderModule, surfaceFormat, color, alpha);
	bool sourcePipelineDirty = false;

	var sampler = device.CreateSampler(new()
	{
		MagFilter = FilterMode.Linear,
		MinFilter = FilterMode.Linear,
		MipmapFilter = MipmapFilterMode.Linear,
	})!;

	var checkerboardImage = CreateCheckerboardImage(WINDOW_WIDTH, WINDOW_HEIGHT, CHECKER_SIZE);
	var sourceImage = CreateSourceImage(DEMO_TEXTURE_SIZE);
	var destinationImage = CreateDestinationImage(DEMO_TEXTURE_SIZE);

	var checkerboardQuad = CreateQuadResources(device, queue, bindGroupLayout, sampler, checkerboardImage, "checkerboard");

	var premultipliedTextureSet = new TextureSetState(
		CreateQuadResources(device, queue, bindGroupLayout, sampler, PremultiplyImage(sourceImage), "source-premultiplied"),
		CreateQuadResources(device, queue, bindGroupLayout, sampler, PremultiplyImage(destinationImage), "destination-premultiplied")
	);

	var unpremultipliedTextureSet = new TextureSetState(
		CreateQuadResources(device, queue, bindGroupLayout, sampler, sourceImage, "source-unpremultiplied"),
		CreateQuadResources(device, queue, bindGroupLayout, sampler, destinationImage, "destination-unpremultiplied")
	);

	TextureSetState[] textureSets = [premultipliedTextureSet, unpremultipliedTextureSet];

	runContext.OnFrame += () =>
	{
		surface.Configure(new()
		{
			Width = surfaceWidth,
			Height = surfaceHeight,
			Usage = TextureUsage.RenderAttachment,
			Format = surfaceFormat,
			Device = device,
			PresentMode = PresentMode.Fifo,
			AlphaMode = alphaModes[settings.AlphaModeIndex],
		});

		var guiCommandBuffer = DrawGui(guiContext, surface, ref sourcePipelineDirty);

		sourcePipelineDirty |= MakeBlendComponentValid(ref color);
		sourcePipelineDirty |= MakeBlendComponentValid(ref alpha);
		if (sourcePipelineDirty)
		{
			sourcePipeline = CreateSourcePipeline(device, pipelineLayout, shaderModule, surfaceFormat, color, alpha);
			sourcePipelineDirty = false;
		}

		var logicalSurfaceWidth = surfaceWidth / devicePixelRatio;
		var logicalSurfaceHeight = surfaceHeight / devicePixelRatio;

		UpdateQuadUniform(queue, checkerboardQuad, logicalSurfaceWidth, logicalSurfaceHeight, 0.0f, 0.0f, checkerboardQuad.Width, checkerboardQuad.Height);

		var textureSet = textureSets[settings.TextureSetIndex];
		UpdateQuadUniform(queue, textureSet.Destination, logicalSurfaceWidth, logicalSurfaceHeight, 0.0f, 0.0f, textureSet.Destination.Width, textureSet.Destination.Height);
		UpdateQuadUniform(queue, textureSet.Source, logicalSurfaceWidth, logicalSurfaceHeight, 0.0f, 0.0f, textureSet.Source.Width, textureSet.Source.Height);

		var surfaceTexture = surface.GetCurrentTexture().Texture!;
		var surfaceView = surfaceTexture.CreateView();
		var commandEncoder = device.CreateCommandEncoder(new());

		var clearValue = BuildClearColor(clear);
		var renderPass = commandEncoder.BeginRenderPass(new()
		{
			ColorAttachments = [
				new()
				{
					View = surfaceView,
					ClearValue = clearValue,
					LoadOp = LoadOp.Clear,
					StoreOp = StoreOp.Store,
				},
			],
		});

		renderPass.SetPipeline(opaquePipeline);
		renderPass.SetBindGroup(0, checkerboardQuad.BindGroup);
		renderPass.Draw(6);

		renderPass.SetBindGroup(0, textureSet.Destination.BindGroup);
		renderPass.Draw(6);

		renderPass.SetPipeline(sourcePipeline);
		renderPass.SetBindGroup(0, textureSet.Source.BindGroup);
		renderPass.SetBlendConstant(new Color
		{
			R = constant.Color[0],
			G = constant.Color[1],
			B = constant.Color[2],
			A = constant.Alpha,
		});
		renderPass.Draw(6);
		renderPass.End();

		queue.Submit([commandEncoder.Finish(), guiCommandBuffer]);

		if (!OperatingSystem.IsBrowser())
		{
			surface.Present();
		}
	};
});

static void ApplyBlendComponent(in BlendComponentState source, ref BlendComponentState destination)
{
	destination.Operation = source.Operation;
	destination.SrcFactor = source.SrcFactor;
	destination.DstFactor = source.DstFactor;
}

void ApplyPreset()
{
	var preset = presets[settings.PresetIndex];
	ApplyBlendComponent(preset.Color, ref color);
	ApplyBlendComponent(preset.Alpha ?? preset.Color, ref alpha);
}

bool DrawBlendComponentControls(ref BlendComponentState state)
{
	bool changed = false;
	changed |= ComboFromValues("operation", operationNames, operationValues, ref state.Operation);
	changed |= ComboFromValues("srcFactor", factorNames, factorValues, ref state.SrcFactor);
	changed |= ComboFromValues("dstFactor", factorNames, factorValues, ref state.DstFactor);
	return changed;
}

bool ComboFromLabels(string label, string[] labels, ref int index)
{
	return ImGui.Combo(label, ref index, labels, labels.Length);
}

bool ComboFromValues<T>(string label, string[] labels, T[] values, ref T current)
	where T : struct, Enum
{
	int index = Array.IndexOf(values, current);
	if (index < 0)
	{
		index = 0;
	}

	if (!ImGui.Combo(label, ref index, labels, labels.Length))
	{
		return false;
	}

	current = values[index];
	return true;
}

static bool MakeBlendComponentValid(ref BlendComponentState component)
{
	if (component.Operation is not BlendOperation.Min and not BlendOperation.Max)
	{
		return false;
	}

	bool changed = false;
	if (component.SrcFactor != BlendFactor.One)
	{
		component.SrcFactor = BlendFactor.One;
		changed = true;
	}

	if (component.DstFactor != BlendFactor.One)
	{
		component.DstFactor = BlendFactor.One;
		changed = true;
	}

	return changed;
}

static RenderPipeline CreateOpaquePipeline(Device device, PipelineLayout pipelineLayout, ShaderModule shaderModule, TextureFormat surfaceFormat)
{
	return device.CreateRenderPipelineSync(new()
	{
		Layout = pipelineLayout,
		Vertex = new()
		{
			Module = shaderModule,
			EntryPoint = "vs",
		},
		Fragment = new()
		{
			Module = shaderModule,
			EntryPoint = "fs",
			Targets = [
				new()
				{
					Format = surfaceFormat,
				},
			],
		},
		Primitive = new()
		{
			Topology = PrimitiveTopology.TriangleList,
		},
	})!;
}

static RenderPipeline CreateSourcePipeline(Device device, PipelineLayout pipelineLayout, ShaderModule shaderModule, TextureFormat surfaceFormat, BlendComponentState colorState, BlendComponentState alphaState)
{
	return device.CreateRenderPipelineSync(new()
	{
		Layout = pipelineLayout,
		Vertex = new()
		{
			Module = shaderModule,
			EntryPoint = "vs",
		},
		Fragment = new()
		{
			Module = shaderModule,
			EntryPoint = "fs",
			Targets = [
				new()
				{
					Format = surfaceFormat,
					Blend = new()
					{
						Color = new BlendComponent
						{
							Operation = colorState.Operation,
							SrcFactor = colorState.SrcFactor,
							DstFactor = colorState.DstFactor,
						},
						Alpha = new BlendComponent
						{
							Operation = alphaState.Operation,
							SrcFactor = alphaState.SrcFactor,
							DstFactor = alphaState.DstFactor,
						},
					},
				},
			],
		},
		Primitive = new()
		{
			Topology = PrimitiveTopology.TriangleList,
		},
	})!;
}

static GpuQuad CreateQuadResources(Device device, Queue queue, BindGroupLayout bindGroupLayout, Sampler sampler, ImageData imageData, string label)
{
	var texture = device.CreateTexture(new()
	{
		Label = label,
		Format = TextureFormat.RGBA8Unorm,
		Size = new(imageData.Width, imageData.Height, 1),
		Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.RenderAttachment,
	});

	ResourceUtils.CopyExternalImageToTexture(queue, imageData, texture);

	var uniformBuffer = device.CreateBuffer(new()
	{
		Label = $"{label}-uniforms",
		Size = (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<Matrix4x4>(),
		Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
	});

	var bindGroup = device.CreateBindGroup(new()
	{
		Layout = bindGroupLayout,
		Entries = [
			new()
			{
				Binding = 0,
				Sampler = sampler,
			},
			new()
			{
				Binding = 1,
				TextureView = texture.CreateView(),
			},
			new()
			{
				Binding = 2,
				Buffer = uniformBuffer,
			},
		],
	});

	return new GpuQuad(texture, bindGroup, uniformBuffer, imageData.Width, imageData.Height);
}

static void UpdateQuadUniform(Queue queue, GpuQuad quad, float logicalSurfaceWidth, float logicalSurfaceHeight, float x, float y, float width, float height)
{
	var matrix = Matrix4x4.CreateOrthographicOffCenter(0.0f, logicalSurfaceWidth, logicalSurfaceHeight, 0.0f, -1.0f, 1.0f);
	matrix.Translate(new Vector3(x, y, 0.0f));
	matrix.Scale(new Vector3(width, height, 1.0f));
	queue.WriteBuffer(quad.UniformBuffer, matrix);
}

static Color BuildClearColor(ClearSettings clearSettings)
{
	double multiplier = clearSettings.Premultiply ? clearSettings.Alpha : 1.0f;
	return new Color
	{
		R = clearSettings.Color[0] * multiplier,
		G = clearSettings.Color[1] * multiplier,
		B = clearSettings.Color[2] * multiplier,
		A = clearSettings.Alpha,
	};
}

static ImageData PremultiplyImage(ImageData imageData)
{
	byte[] premultiplied = new byte[imageData.Data.Length];
	for (int i = 0; i < imageData.Data.Length; i += 4)
	{
		float alpha = imageData.Data[i + 3] / 255.0f;
		premultiplied[i + 0] = ToByte((imageData.Data[i + 0] / 255.0f) * alpha);
		premultiplied[i + 1] = ToByte((imageData.Data[i + 1] / 255.0f) * alpha);
		premultiplied[i + 2] = ToByte((imageData.Data[i + 2] / 255.0f) * alpha);
		premultiplied[i + 3] = imageData.Data[i + 3];
	}

	return new ImageData(premultiplied, imageData.Width, imageData.Height);
}

static ImageData CreateCheckerboardImage(int width, int height, int cellSize)
{
	byte[] data = new byte[width * height * 4];
	Vector3 dark = new(0x40 / 255.0f, 0x40 / 255.0f, 0x40 / 255.0f);
	Vector3 light = new(0x80 / 255.0f, 0x80 / 255.0f, 0x80 / 255.0f);

	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			bool useLight = ((x / cellSize) + (y / cellSize)) % 2 == 1;
			Vector3 color = useLight ? light : dark;
			int index = (y * width + x) * 4;
			data[index + 0] = ToByte(color.X);
			data[index + 1] = ToByte(color.Y);
			data[index + 2] = ToByte(color.Z);
			data[index + 3] = 255;
		}
	}

	return new ImageData(data, (uint)width, (uint)height);
}

static ImageData CreateSourceImage(int size)
{
	byte[] data = new byte[size * size * 4];
	float radius = size / 3.0f;
	float orbit = size / 6.0f;
	float halfSize = size / 2.0f;

	for (int y = 0; y < size; y++)
	{
		for (int x = 0; x < size; x++)
		{
			float localX = x + 0.5f - halfSize;
			float localY = y + 0.5f - halfSize;

			float red = 0.0f;
			float green = 0.0f;
			float blue = 0.0f;
			float alpha = 0.0f;

			for (int i = 0; i < 3; i++)
			{
				float angle = (float)(Math.PI * 2.0 * i / 3.0);
				float centerX = MathF.Cos(angle) * orbit;
				float centerY = MathF.Sin(angle) * orbit;
				float distance = MathF.Sqrt((localX - centerX) * (localX - centerX) + (localY - centerY) * (localY - centerY));
				float normalized = distance / radius;
				if (normalized >= 1.0f)
				{
					continue;
				}

				float circleAlpha = normalized <= 0.5f ? 1.0f : Clamp01((1.0f - normalized) / 0.5f);
				Vector3 circleColor = HslToRgb(i / 3.0f, 1.0f, 0.5f);

				red = ScreenChannel(red, circleColor.X * circleAlpha);
				green = ScreenChannel(green, circleColor.Y * circleAlpha);
				blue = ScreenChannel(blue, circleColor.Z * circleAlpha);
				alpha = ScreenChannel(alpha, circleAlpha);
			}

			int index = (y * size + x) * 4;
			data[index + 0] = ToByte(red);
			data[index + 1] = ToByte(green);
			data[index + 2] = ToByte(blue);
			data[index + 3] = ToByte(alpha);
		}
	}

	return new ImageData(data, (uint)size, (uint)size);
}

static ImageData CreateDestinationImage(int size)
{
	byte[] data = new byte[size * size * 4];
	Vector3[] stops = new Vector3[7];
	for (int i = 0; i <= 6; i++)
	{
		stops[i] = HslToRgb(-i / 6.0f, 1.0f, 0.5f);
	}

	float sin = MathF.Sin(-MathF.PI / 4.0f);
	float cos = MathF.Cos(-MathF.PI / 4.0f);

	for (int y = 0; y < size; y++)
	{
		for (int x = 0; x < size; x++)
		{
			float t = Clamp01((x + y) / (2.0f * (size - 1)));
			Vector3 color = SampleGradient(stops, t);
			float rotatedY = x * sin + y * cos;
			bool transparentStripe = PositiveModulo(rotatedY, 32.0f) < 16.0f;

			int index = (y * size + x) * 4;
			if (transparentStripe)
			{
				data[index + 0] = 0;
				data[index + 1] = 0;
				data[index + 2] = 0;
				data[index + 3] = 0;
				continue;
			}

			data[index + 0] = ToByte(color.X);
			data[index + 1] = ToByte(color.Y);
			data[index + 2] = ToByte(color.Z);
			data[index + 3] = 255;
		}
	}

	return new ImageData(data, (uint)size, (uint)size);
}

static Vector3 SampleGradient(Vector3[] stops, float t)
{
	float scaled = t * (stops.Length - 1);
	int index = Math.Clamp((int)MathF.Floor(scaled), 0, stops.Length - 2);
	float amount = scaled - index;
	return Vector3.Lerp(stops[index], stops[index + 1], amount);
}

static float PositiveModulo(float value, float modulo)
{
	float result = value % modulo;
	return result < 0.0f ? result + modulo : result;
}

static float ScreenChannel(float destination, float source)
{
	return 1.0f - ((1.0f - destination) * (1.0f - source));
}

static Vector3 HslToRgb(float hue, float saturation, float lightness)
{
	hue = hue - MathF.Floor(hue);
	if (saturation <= 0.0f)
	{
		return new(lightness, lightness, lightness);
	}

	float q = lightness < 0.5f
		? lightness * (1.0f + saturation)
		: lightness + saturation - (lightness * saturation);
	float p = 2.0f * lightness - q;
	return new(
		HueToRgb(p, q, hue + 1.0f / 3.0f),
		HueToRgb(p, q, hue),
		HueToRgb(p, q, hue - 1.0f / 3.0f)
	);
}

static float HueToRgb(float p, float q, float t)
{
	if (t < 0.0f)
	{
		t += 1.0f;
	}

	if (t > 1.0f)
	{
		t -= 1.0f;
	}

	if (t < 1.0f / 6.0f)
	{
		return p + ((q - p) * 6.0f * t);
	}

	if (t < 1.0f / 2.0f)
	{
		return q;
	}

	if (t < 2.0f / 3.0f)
	{
		return p + ((q - p) * (2.0f / 3.0f - t) * 6.0f);
	}

	return p;
}

static Vector3 ToVector3(float[] color)
{
	return new(color[0], color[1], color[2]);
}

static void FromVector3(Vector3 value, float[] destination)
{
	destination[0] = value.X;
	destination[1] = value.Y;
	destination[2] = value.Z;
}

static float Clamp01(float value)
{
	return Math.Clamp(value, 0.0f, 1.0f);
}

static byte ToByte(float value)
{
	return (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
}

sealed class Settings
{
	public int AlphaModeIndex;
	public int TextureSetIndex;
	public int PresetIndex;
}

sealed class BlendComponentState
{
	public BlendOperation Operation;
	public BlendFactor SrcFactor;
	public BlendFactor DstFactor;
}

sealed record PresetDefinition(string Name, BlendComponentState Color, BlendComponentState? Alpha = null);

sealed class DemoColor(float[] color, float alpha)
{
	public readonly float[] Color = color;
	public float Alpha = alpha;
}

sealed class ClearSettings(float[] color, float alpha, bool premultiply)
{
	public readonly float[] Color = color;
	public float Alpha = alpha;
	public bool Premultiply = premultiply;
}

readonly record struct GpuQuad(Texture Texture, BindGroup BindGroup, GPUBuffer UniformBuffer, float Width, float Height);

readonly record struct TextureSetState(GpuQuad Source, GpuQuad Destination);
