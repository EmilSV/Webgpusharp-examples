using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;
using static Setup.SetupWebGPU;

const int WIDTH = 640;
const int HEIGHT = 480;

var settings = new Settings();
var asm = Assembly.GetExecutingAssembly();
var solidColorLitWGSL = ResourceUtils.GetEmbeddedResource("Wireframe.shaders.solidColorLit.wgsl", asm);
var wireframeWGSL = ResourceUtils.GetEmbeddedResource("Wireframe.shaders.wireframe.wgsl", asm);
var teapotMesh = await Teapot.LoadMeshAsync();

CommandBuffer DrawGUI(
	GuiContext guiContext,
	Surface surface,
	out bool rebuildLitPipeline,
	out bool lineUniformsChanged)
{
	rebuildLitPipeline = false;
	lineUniformsChanged = false;

	guiContext.NewFrame();
	ImGui.SetNextWindowBgAlpha(0.3f);
	ImGui.Begin("Settings", ImGuiWindowFlags.NoCollapse);

	var barycentricModeChanged = ImGui.Checkbox("barycentricCoordinatesBased", ref settings.BarycentricCoordinatesBased);
	ImGui.Checkbox("lines", ref settings.Lines);
	ImGui.Checkbox("models", ref settings.Models);
	ImGui.Checkbox("animate", ref settings.Animate);

	if (settings.BarycentricCoordinatesBased)
	{
		lineUniformsChanged |= ImGui.SliderFloat("thickness", ref settings.Thickness, 0.0f, 10.0f);
		lineUniformsChanged |= ImGui.SliderFloat("alphaThreshold", ref settings.AlphaThreshold, 0.0f, 1.0f);
	}
	else
	{
		rebuildLitPipeline |= ImGui.SliderInt("depthBias", ref settings.DepthBias, -3, 3);
		rebuildLitPipeline |= ImGui.SliderFloat("depthBiasSlopeScale", ref settings.DepthBiasSlopeScale, -1.0f, 1.0f);
	}

	if (barycentricModeChanged && settings.BarycentricCoordinatesBased)
	{
		lineUniformsChanged = true;
	}

	ImGui.End();
	guiContext.EndFrame();
	return guiContext.Render(surface)!.Value!;
}

return Run("Wireframe", WIDTH, HEIGHT, async runContext =>
{
	var instance = runContext.GetInstance();
	var surface = runContext.GetSurface();
	var guiContext = runContext.GetGuiContext();
	var startTimestamp = Stopwatch.GetTimestamp();

	var adapter = await instance.RequestAdapterAsync(new()
	{
		CompatibleSurface = surface,
		FeatureLevel = FeatureLevel.Compatibility,
	});

	if (adapter?.GetLimits() is not { MaxStorageBuffersPerShaderStage: >= 2 })
	{
		Console.Error.WriteLine("Device does not support required limits: MaxStorageBuffersPerShaderStage >= 2");
		Environment.Exit(1);
	}

	var device = await adapter.RequestDeviceAsync(new()
	{
		RequiredLimits = new Limits
		{
			MaxStorageBuffersPerShaderStage = 2,
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
	var surfaceCapabilities = surface.GetCapabilities(adapter)!;
	var surfaceFormat = surfaceCapabilities.Formats[0];

	guiContext.SetupIMGUI(device, surfaceFormat);

	var devicePixelRatio = runContext.GetDevicePixelRatio();
	var renderWidth = (uint)Math.Max(1, (int)MathF.Round(WIDTH * devicePixelRatio));
	var renderHeight = (uint)Math.Max(1, (int)MathF.Round(HEIGHT * devicePixelRatio));

	surface.Configure(new()
	{
		Width = renderWidth,
		Height = renderHeight,
		Usage = TextureUsage.RenderAttachment,
		Format = surfaceFormat,
		Device = device,
		PresentMode = PresentMode.Fifo,
		AlphaMode = CompositeAlphaMode.Auto,
	});

	const TextureFormat depthFormat = TextureFormat.Depth24Plus;

	var modelGeometries = new[]
	{
		Models.ConvertMeshToTypedArrays(teapotMesh, 1.5f),
		Models.CreateSphereTypedArrays(20),
		Models.FlattenNormals(Models.CreateSphereTypedArrays(20, 5, 3)),
		Models.FlattenNormals(Models.CreateSphereTypedArrays(20, 32, 16, 0.1f)),
	};

	var models = new List<Model>(modelGeometries.Length);
	foreach (var geometry in modelGeometries)
	{
		models.Add(CreateVertexAndIndexBuffer(device, queue, geometry));
	}

	var litModule = device.CreateShaderModuleWGSL(new() { Code = solidColorLitWGSL });
	var wireframeModule = device.CreateShaderModuleWGSL(new() { Code = wireframeWGSL });

	var litBindGroupLayout = device.CreateBindGroupLayout(new()
	{
		Label = "lit bind group layout",
		Entries =
		[
			new()
			{
				Binding = 0,
				Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
				Buffer = new(),
			},
		],
	});

	RenderPipeline CreateLitPipeline()
	{
		return device.CreateRenderPipelineSync(new()
		{
			Label = "lit pipeline",
			Layout = device.CreatePipelineLayout(new()
			{
				BindGroupLayouts = [litBindGroupLayout],
			}),
			Vertex = new()
			{
				Module = litModule,
				Buffers =
				[
					new()
					{
						ArrayStride = 6 * sizeof(float),
						Attributes =
						[
							new()
							{
								ShaderLocation = 0,
								Offset = 0,
								Format = VertexFormat.Float32x3,
							},
							new()
							{
								ShaderLocation = 1,
								Offset = 3 * sizeof(float),
								Format = VertexFormat.Float32x3,
							},
						],
					},
				],
			},
			Fragment = new()
			{
				Module = litModule,
				Targets = [new() { Format = surfaceFormat }],
			},
			Primitive = new()
			{
				CullMode = CullMode.Back,
			},
			DepthStencil = new()
			{
				DepthWriteEnabled = OptionalBool.True,
				DepthCompare = CompareFunction.Less,
				DepthBias = settings.DepthBias,
				DepthBiasSlopeScale = settings.DepthBiasSlopeScale,
				Format = depthFormat,
			},
		});
	}

	var litPipeline = CreateLitPipeline();

	var wireframePipeline = device.CreateRenderPipelineSync(new()
	{
		Label = "wireframe pipeline",
		Layout = null,
		Vertex = new()
		{
			Module = wireframeModule,
			EntryPoint = "vsIndexedU32",
		},
		Fragment = new()
		{
			Module = wireframeModule,
			EntryPoint = "fs",
			Targets = [new() { Format = surfaceFormat }],
		},
		Primitive = new()
		{
			Topology = PrimitiveTopology.LineList,
		},
		DepthStencil = new()
		{
			DepthWriteEnabled = OptionalBool.True,
			DepthCompare = CompareFunction.LessEqual,
			Format = depthFormat,
		},
	});

	var barycentricWireframePipeline = device.CreateRenderPipelineSync(new()
	{
		Label = "barycentric coordinates based wireframe pipeline",
		Layout = null,
		Vertex = new()
		{
			Module = wireframeModule,
			EntryPoint = "vsIndexedU32BarycentricCoordinateBasedLines",
		},
		Fragment = new()
		{
			Module = wireframeModule,
			EntryPoint = "fsBarycentricCoordinateBasedLines",
			Targets =
			[
				new()
				{
					Format = surfaceFormat,
					Blend = new()
					{
						Color = new()
						{
							SrcFactor = BlendFactor.One,
							DstFactor = BlendFactor.OneMinusSrcAlpha,
							Operation = BlendOperation.Add,
						},
						Alpha = new()
						{
							SrcFactor = BlendFactor.One,
							DstFactor = BlendFactor.OneMinusSrcAlpha,
							Operation = BlendOperation.Add,
						},
					},
				},
			],
		},
		Primitive = new()
		{
			Topology = PrimitiveTopology.TriangleList,
		},
		DepthStencil = new()
		{
			DepthWriteEnabled = OptionalBool.True,
			DepthCompare = CompareFunction.LessEqual,
			Format = depthFormat,
		},
	});

	var objectInfos = new List<ObjectInfo>(capacity: 200);
	for (int i = 0; i < 200; i++)
	{
		var uniformBuffer = device.CreateBuffer(new()
		{
			Size = (ulong)Marshal.SizeOf<Uniforms>(),
			Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
		});

		var lineUniformBuffer = device.CreateBuffer(new()
		{
			Size = (ulong)Marshal.SizeOf<LineUniforms>(),
			Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
		});

		var model = models[Random.Shared.Next(models.Count)];
		var objectInfo = new ObjectInfo
		{
			Model = model,
			UniformBuffer = uniformBuffer,
			LineUniformBuffer = lineUniformBuffer,
			Uniforms = new Uniforms
			{
				Color = RandColor(),
			},
			LineUniforms = new LineUniforms
			{
				Stride = 6,
				Thickness = settings.Thickness,
				AlphaThreshold = settings.AlphaThreshold,
			},
			LitBindGroup = device.CreateBindGroup(new()
			{
				Layout = litBindGroupLayout,
				Entries =
				[
					new()
					{
						Binding = 0,
						Buffer = uniformBuffer,
					},
				],
			}),
			WireframeBindGroup = device.CreateBindGroup(new()
			{
				Layout = wireframePipeline.GetBindGroupLayout(0),
				Entries =
				[
					new() { Binding = 0, Buffer = uniformBuffer },
					new() { Binding = 1, Buffer = model.VertexBuffer },
					new() { Binding = 2, Buffer = model.IndexBuffer },
					new() { Binding = 3, Buffer = lineUniformBuffer },
				],
			}),
			BarycentricWireframeBindGroup = device.CreateBindGroup(new()
			{
				Layout = barycentricWireframePipeline.GetBindGroupLayout(0),
				Entries =
				[
					new() { Binding = 0, Buffer = uniformBuffer },
					new() { Binding = 1, Buffer = model.VertexBuffer },
					new() { Binding = 2, Buffer = model.IndexBuffer },
					new() { Binding = 3, Buffer = lineUniformBuffer },
				],
			}),
		};

		queue.WriteBuffer(objectInfo.LineUniformBuffer, objectInfo.LineUniforms);
		objectInfos.Add(objectInfo);
	}

	var depthTexture = device.CreateTexture(new()
	{
		Size = new(renderWidth, renderHeight),
		Format = depthFormat,
		Usage = TextureUsage.RenderAttachment,
	});
	var depthView = depthTexture.CreateView();

	var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
		fieldOfView: (60.0f * MathF.PI) / 180.0f,
		aspectRatio: renderWidth / (float)renderHeight,
		nearPlaneDistance: 0.1f,
		farPlaneDistance: 1000.0f
	);
	var viewMatrix = Matrix4x4.CreateLookAt(
		cameraPosition: new(-300, 0, 300),
		cameraTarget: Vector3.Zero,
		cameraUpVector: Vector3.UnitY
	);

	var viewProjection = projectionMatrix * viewMatrix;

	float time = 0.0f;

	runContext.OnFrame += () =>
	{
		var guiCommandBuffer = DrawGUI(guiContext, surface, out var rebuildLitPipeline, out var lineUniformsChanged);
		if (rebuildLitPipeline)
		{
			litPipeline = CreateLitPipeline();
		}

		if (lineUniformsChanged)
		{
			foreach (var info in objectInfos)
			{
				info.LineUniforms.Thickness = settings.Thickness;
				info.LineUniforms.AlphaThreshold = settings.AlphaThreshold;
				queue.WriteBuffer(info.LineUniformBuffer, info.LineUniforms);
			}
		}

		if (settings.Animate)
		{
			time = (float)Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
		}

		var surfaceTexture = surface.GetCurrentTexture()!.Texture!;
		var colorView = surfaceTexture.CreateView();

		var encoder = device.CreateCommandEncoder();
		var pass = encoder.BeginRenderPass(new()
		{
			Label = "wireframe pass",
			ColorAttachments =
			[
				new()
				{
					View = colorView,
					ClearValue = new(0.3f, 0.3f, 0.3f, 1f),
					LoadOp = LoadOp.Clear,
					StoreOp = StoreOp.Store,
				},
			],
			DepthStencilAttachment = new()
			{
				View = depthView,
				DepthClearValue = 1f,
				DepthLoadOp = LoadOp.Clear,
				DepthStoreOp = StoreOp.Store,
			},
		});



		pass.SetPipeline(litPipeline);
		for (int i = 0; i < objectInfos.Count; i++)
		{
			var info = objectInfos[i];
			var world = Matrix4x4.Identity;
			world.Translate(new(0, 0, MathF.Sin(i * 3.721f + time * 0.1f) * 200f));
			world.RotateX(i * 4.567f);
			world.RotateY(i * 2.967f);
			world.Translate(new(0, 0, MathF.Sin(i * 9.721f + time * 0.1f) * 200f));
			world.RotateX(time * 0.53f + i);

			info.Uniforms.WorldViewProjectionMatrix = viewProjection * world;
			info.Uniforms.WorldMatrix = world;
			queue.WriteBuffer(info.UniformBuffer, info.Uniforms);

			if (!settings.Models)
			{
				continue;
			}

			pass.SetVertexBuffer(0, info.Model.VertexBuffer);
			pass.SetIndexBuffer(info.Model.IndexBuffer, info.Model.IndexFormat);
			pass.SetBindGroup(0, info.LitBindGroup);
			pass.DrawIndexed(info.Model.VertexCount);
		}

		if (settings.Lines)
		{
			var linePipeline = settings.BarycentricCoordinatesBased
				? barycentricWireframePipeline
				: wireframePipeline;
			var lineCountMultiplier = settings.BarycentricCoordinatesBased ? 1u : 2u;
			pass.SetPipeline(linePipeline);

			foreach (var info in objectInfos)
			{
				pass.SetBindGroup(0, settings.BarycentricCoordinatesBased
					? info.BarycentricWireframeBindGroup
					: info.WireframeBindGroup
				);
				pass.Draw(info.Model.VertexCount * lineCountMultiplier);
			}
		}

		pass.End();

		queue.Submit([encoder.Finish(), guiCommandBuffer]);
		surface.Present();
	};
});

static Model CreateVertexAndIndexBuffer(Device device, ModelGeometry geometry)
{
	var queue = device.GetQueue();

	var vertexBuffer = device.CreateBuffer(new()
	{
		Size = geometry.Vertices.GetSizeInBytes(),
		Usage = BufferUsage.Vertex | BufferUsage.Storage | BufferUsage.CopyDst,
	});
	queue.WriteBuffer(vertexBuffer, 0, geometry.Vertices);

	var indexBuffer = device.CreateBuffer(new()
	{
		Size = geometry.Indices.GetSizeInBytes(),
		Usage = BufferUsage.Index | BufferUsage.Storage | BufferUsage.CopyDst,
	});
	queue.WriteBuffer(indexBuffer, 0, geometry.Indices);

	return new Model
	{
		VertexBuffer = vertexBuffer,
		IndexBuffer = indexBuffer,
		IndexFormat = IndexFormat.Uint32,
		VertexCount = (uint)geometry.Indices.Length,
	};
}

static Vector4 RandColor()
{
	return new Vector4(Random.Shared.NextSingle(), Random.Shared.NextSingle(), Random.Shared.NextSingle(), 1f);
}

class Settings
{
	public bool BarycentricCoordinatesBased = false;
	public float Thickness = 2f;
	public float AlphaThreshold = 0.5f;
	public bool Animate = true;
	public bool Lines = true;
	public int DepthBias = 1;
	public float DepthBiasSlopeScale = 0.5f;
	public bool Models = true;
}

class Model
{
	public required GPUBuffer VertexBuffer;
	public required GPUBuffer IndexBuffer;
	public required IndexFormat IndexFormat;
	public required uint VertexCount;
}

class ObjectInfo
{
	public required Uniforms Uniforms;
	public required GPUBuffer UniformBuffer;
	public required LineUniforms LineUniforms;
	public required GPUBuffer LineUniformBuffer;
	public required BindGroup LitBindGroup;
	public required BindGroup WireframeBindGroup;
	public required BindGroup BarycentricWireframeBindGroup;
	public required Model Model;
}

struct ModelGeometry
{
	public required Vertex[] Vertices;
	public required uint[] Indices;
}

[StructLayout(LayoutKind.Sequential)]
struct Uniforms
{
	public Matrix4x4 WorldViewProjectionMatrix;
	public Matrix4x4 WorldMatrix;
	public Vector4 Color;
}

[StructLayout(LayoutKind.Sequential)]
struct LineUniforms
{
	public uint Stride;
	public float Thickness;
	public float AlphaThreshold;
	public float Padding;
}
