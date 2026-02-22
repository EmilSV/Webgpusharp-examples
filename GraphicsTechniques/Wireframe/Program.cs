using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

const int WIDTH = 600;
const int HEIGHT = 600;
const TextureFormat depthFormat = TextureFormat.Depth24Plus;

var settings = new Settings();
var asm = Assembly.GetExecutingAssembly();
var solidColorLitWGSL = ResourceUtils.GetEmbeddedResource("Wireframe.shaders.solidColorLit.wgsl", asm);
var wireframeWGSL = ResourceUtils.GetEmbeddedResource("Wireframe.shaders.wireframe.wgsl", asm);
var teapotMesh = await Teapot.LoadMeshAsync();

CommandBuffer DrawGUI(
	GuiContext guiContext,
	Surface surface,
	out bool rebuildLitPipeline,
	out bool updateThickness)
{
	rebuildLitPipeline = false;
	updateThickness = false;

	guiContext.NewFrame();
	ImGui.SetNextWindowBgAlpha(0.3f);
	ImGui.Begin("Settings", ImGuiWindowFlags.NoCollapse);

	var barycentricModeChanged = ImGui.Checkbox("barycentricCoordinatesBased", ref settings.BarycentricCoordinatesBased);
	ImGui.Checkbox("lines", ref settings.Lines);
	ImGui.Checkbox("models", ref settings.Models);
	ImGui.Checkbox("animate", ref settings.Animate);

	if (settings.BarycentricCoordinatesBased)
	{
		updateThickness |= ImGui.SliderFloat("thickness", ref settings.Thickness, 0.0f, 10.0f);
		updateThickness |= ImGui.SliderFloat("alphaThreshold", ref settings.AlphaThreshold, 0.0f, 1.0f);
	}
	else
	{
		rebuildLitPipeline |= ImGui.SliderInt("depthBias", ref settings.DepthBias, -3, 3);
		rebuildLitPipeline |= ImGui.SliderFloat("depthBiasSlopeScale", ref settings.DepthBiasSlopeScale, -1.0f, 1.0f);
	}

	if (barycentricModeChanged && settings.BarycentricCoordinatesBased)
	{
		updateThickness = true;
	}

	ImGui.End();
	guiContext.EndFrame();
	return guiContext.Render(surface)!.Value!;
}

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

return Run("Wireframe", WIDTH, HEIGHT, async runContext =>
{
	var instance = runContext.GetInstance();
	var surface = runContext.GetSurface();
	var guiContext = runContext.GetGuiContext();
	var startTimestamp = Stopwatch.GetTimestamp();

	var adapter = await instance.RequestAdapterAsync(new()
	{
		CompatibleSurface = surface,
		FeatureLevel = FeatureLevel.Core,
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

	var modelData = await Models.ModelData.Value;

	Model[] models = [
		CreateVertexAndIndexBuffer(device, modelData.Teapot),
		CreateVertexAndIndexBuffer(device, modelData.Sphere),
		CreateVertexAndIndexBuffer(device, modelData.Jewel),
		CreateVertexAndIndexBuffer(device, modelData.Rock)
	];

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

	RenderPipeline litPipeline;
	void RebuildLitPipeline()
	{
		litPipeline = device.CreateRenderPipelineSync(new()
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
						ArrayStride = (uint)Unsafe.SizeOf<Vertex>(),
						Attributes =
						[
							new()
							{
								ShaderLocation = 0,
								Offset = (ulong)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position)),
								Format = VertexFormat.Float32x3,
							},
							new()
							{
								ShaderLocation = 1,
								Offset = (ulong)Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal)),
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
				// Applying a depth bias can prevent aliasing from z-fighting with the
				// wireframe lines. The depth bias has to be applied to the lit meshes
				// rather that the wireframe because depthBias isn't considered when
				// drawing line or point primitives.
				DepthBias = settings.DepthBias,
				DepthBiasSlopeScale = settings.DepthBiasSlopeScale,
				Format = depthFormat,
			},
		});
	}

	RebuildLitPipeline();

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
		Layout = null, //auto,
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
						},
						Alpha = new()
						{
							SrcFactor = BlendFactor.One,
							DstFactor = BlendFactor.OneMinusSrcAlpha,
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

		// Note: We're making one lineUniformBuffer per object.
		// This is only because stride might be different per object.
		// In this sample stride is the same across all objects so
		// we could have made just a single shared uniform buffer for
		// these settings.
		var lineUniformBuffer = device.CreateBuffer(new()
		{
			Size = (ulong)Marshal.SizeOf<LineUniforms>(),
			Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
		});

		var model = models[Random.Shared.Next(models.Length)];

		var litBindGroup = device.CreateBindGroup(new()
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
		});

		// We're creating 2 bindGroups, one for each pipeline.
		// We could create just one since they are identical. To do
		// so we'd have to manually create a bindGroupLayout.
		var wireframeBindGroup = device.CreateBindGroup(new()
		{
			Layout = wireframePipeline.GetBindGroupLayout(0),
			Entries =
			[
				new() { Binding = 0, Buffer = uniformBuffer },
				new() { Binding = 1, Buffer = model.VertexBuffer },
				new() { Binding = 2, Buffer = model.IndexBuffer },
				new() { Binding = 3, Buffer = lineUniformBuffer },
			],
		});

		var barycentricCoordinatesBasedWireframeBindGroup = device.CreateBindGroup(new()
		{
			Layout = barycentricWireframePipeline.GetBindGroupLayout(0),
			Entries =
			[
				new() { Binding = 0, Buffer = uniformBuffer },
				new() { Binding = 1, Buffer = model.VertexBuffer },
				new() { Binding = 2, Buffer = model.IndexBuffer },
				new() { Binding = 3, Buffer = lineUniformBuffer },
			],
		});

		var objectInfo = new ObjectInfo
		{
			UniformBuffer = uniformBuffer,
			LineUniformBuffer = lineUniformBuffer,
			Uniforms = new Uniforms
			{
				Color = new(Random.Shared.NextSingle(), Random.Shared.NextSingle(), Random.Shared.NextSingle(), 1f),
			},
			LineUniforms = new LineUniforms
			{
				Stride = 6,
				Thickness = settings.Thickness,
				AlphaThreshold = settings.AlphaThreshold,
			},
			LitBindGroup = litBindGroup,
			WireframeBindGroups = [
				wireframeBindGroup,
				barycentricCoordinatesBasedWireframeBindGroup,
			],
			Model = model,
		};

		objectInfos.Add(objectInfo);
	}

	void UpdateThickness()
	{
		foreach (var info in objectInfos)
		{
			info.LineUniforms.Thickness = settings.Thickness;
			info.LineUniforms.AlphaThreshold = settings.AlphaThreshold;
			queue.WriteBuffer(info.LineUniformBuffer, info.LineUniforms);
		}
	}

	UpdateThickness();

	Texture? depthTexture = null;
	TextureView? depthView = null;

	float time = 0.0f;

	runContext.OnFrame += () =>
	{
		if (settings.Animate)
		{
			time = (float)Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
		}


		if (depthTexture == null || depthTexture.GetWidth() != renderWidth || depthTexture.GetHeight() != renderHeight)
		{
			depthTexture?.Destroy();
			depthTexture = device.CreateTexture(new()
			{
				Size = new(renderWidth, renderHeight),
				Format = depthFormat,
				Usage = TextureUsage.RenderAttachment,
			});
			depthView = depthTexture.CreateView();
		}

		const float fov = 60.0f * MathF.PI / 180.0f;
		float aspect = renderWidth / (float)renderHeight;
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(
			fieldOfView: fov,
			aspectRatio: aspect,
			nearPlaneDistance: 0.1f,
			farPlaneDistance: 1000.0f
		);

		var view = Matrix4x4.CreateLookAt(
			cameraPosition: new(-300, 0, 300),
			cameraTarget: Vector3.Zero,
			cameraUpVector: Vector3.UnitY
		);

		var viewProjection = view * projection;

		var guiCommandBuffer = DrawGUI(guiContext, surface, out var rebuildLitPipeline, out var updateThickness);

		if (rebuildLitPipeline)
		{
			RebuildLitPipeline();
		}

		if (updateThickness)
		{
			UpdateThickness();
		}

		var surfaceTexture = surface.GetCurrentTexture()!.Texture!;
		var surfaceTextureView = surfaceTexture.CreateView();

		// make a command encoder to start encoding commands
		var encoder = device.CreateCommandEncoder();

		// make a render pass encoder to encode render specific commands
		var pass = encoder.BeginRenderPass(new()
		{
			Label = "wireframe pass",
			ColorAttachments =
			[
				new()
				{
					View = surfaceTextureView,
					ClearValue = new(0.3f, 0.3f, 0.3f, 1f),
					LoadOp = LoadOp.Clear,
					StoreOp = StoreOp.Store,
				},
			],
			DepthStencilAttachment = new()
			{
				View = depthView!,
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

			info.Uniforms.WorldViewProjectionMatrix = world * viewProjection;
			info.Uniforms.WorldMatrix = world;

			queue.WriteBuffer(info.UniformBuffer, info.Uniforms);

			if (settings.Models)
			{
				pass.SetVertexBuffer(0, info.Model.VertexBuffer);
				pass.SetIndexBuffer(info.Model.IndexBuffer, info.Model.IndexFormat);
				pass.SetBindGroup(0, info.LitBindGroup);
				pass.DrawIndexed(info.Model.VertexCount);
			}
		}

		if (settings.Lines)
		{
			var linePipeline = settings.BarycentricCoordinatesBased
				? barycentricWireframePipeline
				: wireframePipeline;
			var lineCountMultiplier = settings.BarycentricCoordinatesBased ? 1u : 2u;

			var (bindGroupNdx, countMult, pipeline) = settings.BarycentricCoordinatesBased
				? (1, 1u, barycentricWireframePipeline)
				: (0, 2u, wireframePipeline);

			pass.SetPipeline(linePipeline);

			foreach (var info in objectInfos)
			{
				pass.SetBindGroup(0, info.WireframeBindGroups[bindGroupNdx]);
				pass.Draw(info.Model.VertexCount * lineCountMultiplier);
			}
		}

		pass.End();

		queue.Submit([encoder.Finish(), guiCommandBuffer]);
		surface.Present();
	};
});