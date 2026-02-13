using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 640;
const int HEIGHT = 480;

var asm = Assembly.GetExecutingAssembly();
var basicVertWGSL = ResourceUtils.GetEmbeddedResource("TextRenderingMsdf.shaders.basic.vert.wgsl", asm);
var vertexPositionColorWGSL = ResourceUtils.GetEmbeddedResource("TextRenderingMsdf.shaders.vertexPositionColor.frag.wgsl", asm);

return Run("Text Rendering (MSDF)", WIDTH, HEIGHT, async runContext =>
{
	var instance = runContext.GetInstance();
	var surface = runContext.GetSurface();

	var adapter = await instance.RequestAdapterAsync(new()
	{
		CompatibleSurface = surface,
		FeatureLevel = FeatureLevel.Core
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
	const TextureFormat DEPTH_FORMAT = TextureFormat.Depth24Plus;

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

	var textRenderer = new MsdfTextRenderer(device, surfaceFormat, DEPTH_FORMAT);
	var font = textRenderer.CreateFontFromResources(
		asm,
		"TextRenderingMsdf.assets.ya-hei-ascii-msdf.json",
		"TextRenderingMsdf.assets."
	);

	static Matrix4x4 GetTextTransform(Vector3 position, Vector3? rotation = null)
	{
		var textTransform = Matrix4x4.Identity;
		textTransform.Translate(position);
		if (rotation is Vector3 rot)
		{
			if (rot.X != 0)
			{
				textTransform.RotateX(rot.X);
			}
			if (rot.Y != 0)
			{
				textTransform.RotateY(rot.Y);
			}
			if (rot.Z != 0)
			{
				textTransform.RotateZ(rot.Z);
			}
		}
		return textTransform;
	}

	var textTransforms = new[]
	{
		GetTextTransform(new (0, 0, 1.1f)),
		GetTextTransform(new (0, 0, -1.1f), new (0, MathF.PI, 0)),
		GetTextTransform(new (1.1f, 0, 0), new (0, MathF.PI / 2f, 0)),
		GetTextTransform(new (-1.1f, 0, 0), new(0, -MathF.PI / 2f, 0)),
		GetTextTransform(new (0, 1.1f, 0), new(-MathF.PI / 2f, 0, 0)),
		GetTextTransform(new (0, -1.1f, 0), new(MathF.PI / 2f, 0, 0)),
	};

	var titleText = textRenderer.FormatText(font, "WebGPU", new()
	{
		Centered = true,
		PixelScale = 1f / 128f,
	});

	var largeText = textRenderer.FormatText(
		font,
		"""
		WebGPU exposes an API for performing operations, such as rendering
		and computation, on a Graphics Processing Unit.

		Graphics Processing Units, or GPUs for short, have been essential
		in enabling rich rendering and computational applications in personal
		computing. WebGPU is an API that exposes the capabilities of GPU
		hardware for the Web. The API is designed from the ground up to
		efficiently map to (post-2014) native GPU APIs. WebGPU is not related
		to WebGL and does not explicitly target OpenGL ES.

		WebGPU sees physical GPU hardware as GPUAdapters. It provides a
		connection to an adapter via GPUDevice, which manages resources, and
		the device's GPUQueues, which execute commands. GPUDevice may have
		its own memory with high-speed access to the processing units.
		GPUBuffer and GPUTexture are the physical resources backed by GPU
		memory. GPUCommandBuffer and GPURenderBundle are containers for
		user-recorded commands. GPUShaderModule contains shader code. The
		other resources, such as GPUSampler or GPUBindGroup, configure the
		way physical resources are used by the GPU.

		GPUs execute commands encoded in GPUCommandBuffers by feeding data
		through a pipeline, which is a mix of fixed-function and programmable
		stages. Programmable stages execute shaders, which are special
		programs designed to run on GPU hardware. Most of the state of a
		pipeline is defined by a GPURenderPipeline or a GPUComputePipeline
		object. The state not included in these pipeline objects is set
		during encoding with commands, such as beginRenderPass() or
		setBlendConstant().
		""",
		new() { PixelScale = 1f / 256f }
	);

	var text = new[]
	{
		textRenderer.FormatText(font, "Front", new()
		{
			Centered = true,
			PixelScale = 1f / 128f,
			Color = new(1, 0, 0, 1),
		}),
		textRenderer.FormatText(font, "Back", new()
		{
			Centered = true,
			PixelScale = 1f / 128f,
			Color = new(0, 1, 1, 1),
		}),
		textRenderer.FormatText(font, "Right", new()
		{
			Centered = true,
			PixelScale = 1f / 128f,
			Color = new(0, 1, 0, 1),
		}),
		textRenderer.FormatText(font, "Left", new()
		{
			Centered = true,
			PixelScale = 1f / 128f,
			Color = new(1, 0, 1, 1),
		}),
		textRenderer.FormatText(font, "Top", new()
		{
			Centered = true,
			PixelScale = 1f / 128f,
			Color = new(0, 0, 1, 1),
		}),
		textRenderer.FormatText(font, "Bottom", new()
		{
			Centered = true,
			PixelScale = 1f / 128f,
			Color = new(1, 1, 0, 1),
		}),
		titleText,
		largeText,
	};

	var verticesBuffer = device.CreateBuffer(new()
	{
		Size = Cube.CubeVertexArray.GetSizeInBytes(),
		Usage = BufferUsage.Vertex,
		MappedAtCreation = true,
	});
	verticesBuffer.GetMappedRange<float>(data => Cube.CubeVertexArray.AsSpan().CopyTo(data));
	verticesBuffer.Unmap();

	var pipeline = device.CreateRenderPipelineSync(new()
	{
		Layout = null,
		Vertex = new()
		{
			Module = device.CreateShaderModuleWGSL(new() { Code = basicVertWGSL }),
			Buffers = [
				new()
				{
					ArrayStride = Cube.CUBE_VERTEX_SIZE,
					Attributes = [
						new()
						{
							ShaderLocation = 0,
							Offset = Cube.CUBE_POSITION_OFFSET,
							Format = VertexFormat.Float32x4,
						},
						new()
						{
							ShaderLocation = 1,
							Offset = Cube.CUBE_UV_OFFSET,
							Format = VertexFormat.Float32x2,
						},
					],
				},
			],
		},
		Fragment = new()
		{
			Module = device.CreateShaderModuleWGSL(new() { Code = vertexPositionColorWGSL }),
			Targets = [new() { Format = surfaceFormat }],
		},
		Primitive = new()
		{
			// Faces pointing away from the camera will be occluded by faces
			// pointing toward the camera.
			CullMode = CullMode.Back,
		},
		DepthStencil = new()
		{
			// Enable depth testing so that the fragment closest to the camera
			// is rendered in front.
			DepthWriteEnabled = OptionalBool.True,
			DepthCompare = CompareFunction.Less,
			Format = DEPTH_FORMAT,
		},
	});

	var depthTexture = device.CreateTexture(new()
	{
		Size = new(renderWidth, renderHeight),
		Format = DEPTH_FORMAT,
		Usage = TextureUsage.RenderAttachment,
	});

	var uniformBuffer = device.CreateBuffer(new()
	{
		Size = (ulong)Unsafe.SizeOf<Matrix4x4>(),
		Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
	});

	var uniformBindGroup = device.CreateBindGroup(new()
	{
		Layout = pipeline.GetBindGroupLayout(0),
		Entries = [
			new()
			{
				Binding = 0,
				Buffer = uniformBuffer,
			},
		],
	});

	var aspect = renderWidth / (float)renderHeight;
	var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
		fieldOfView: (2f * MathF.PI) / 5f,
		aspectRatio: aspect,
		nearPlaneDistance: 1f,
		farPlaneDistance: 100.0f
	);
	var modelViewProjectionMatrix = Matrix4x4.Identity;
	var start = Stopwatch.GetTimestamp();
	Matrix4x4 GetTransformationMatrix()
	{
		var now = (float)(Stopwatch.GetElapsedTime(start).TotalMilliseconds / 5000.0);
		
		var viewMatrix = Matrix4x4.Identity;
		viewMatrix.Translate(new Vector3(0, 0, -5));

		var modelMatrix = Matrix4x4.Identity;
		modelMatrix.Translate(new Vector3(0, 2, -3));
		modelMatrix.Rotate(new Vector3((float)Math.Sin(now), (float)Math.Cos(now), 0), 1f);

		modelViewProjectionMatrix = viewMatrix * projectionMatrix;
		modelViewProjectionMatrix = modelMatrix * modelViewProjectionMatrix;

		textRenderer.UpdateCamera(projectionMatrix, viewMatrix);

		Matrix4x4 textMatrix = Matrix4x4.Identity;
		for (int i = 0; i < textTransforms.Length; ++i)
		{
			var transform = textTransforms[i];
			textMatrix = transform * modelMatrix;
			text[i].SetTransform(textMatrix);
		}

		var crawl = (float)((Stopwatch.GetElapsedTime(start).TotalMilliseconds / 2500.0) % 14.0);
		textMatrix = Matrix4x4.Identity;
		textMatrix.RotateX(-MathF.PI / 8f);
		textMatrix.Translate(new(0, crawl - 3f, 0));
		titleText.SetTransform(textMatrix);
		textMatrix.Translate(new(-3f, -0.1f, 0));
		largeText.SetTransform(textMatrix);

		return modelViewProjectionMatrix;
	}

	runContext.OnFrame += () =>
	{
		var transformationMatrix = GetTransformationMatrix();
		queue.WriteBuffer(uniformBuffer, 0, transformationMatrix);

		var commandEncoder = device.CreateCommandEncoder();
		var passEncoder = commandEncoder.BeginRenderPass(new()
		{
			ColorAttachments = [
				new()
				{
					View = surface.GetCurrentTexture().Texture!.CreateView(),
					ClearValue = new(0f, 0f, 0f, 1f),
					LoadOp = LoadOp.Clear,
					StoreOp = StoreOp.Store,
				},
			],
			DepthStencilAttachment = new()
			{
				View = depthTexture.CreateView(),
				DepthClearValue = 1.0f,
				DepthLoadOp = LoadOp.Clear,
				DepthStoreOp = StoreOp.Store,
			},
		});

		passEncoder.SetPipeline(pipeline);
		passEncoder.SetBindGroup(0, uniformBindGroup);
		passEncoder.SetVertexBuffer(0, verticesBuffer);
		passEncoder.Draw(Cube.CUBE_VERTEX_COUNT, 1, 0, 0);

		textRenderer.Render(passEncoder, text);

		passEncoder.End();
		queue.Submit(commandEncoder.Finish());
		surface.Present();
	};
});
