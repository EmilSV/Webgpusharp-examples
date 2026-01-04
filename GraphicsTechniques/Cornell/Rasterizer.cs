using System.Text;
using Setup;
using WebGpuSharp;

namespace Cornell;

/// <summary>
/// Implements the rasterization path for the Cornell box scene.
/// </summary>
public sealed class Rasterizer
{

	public static Lazy<byte[]> Wgsl = new(
		() => ResourceUtils.GetEmbeddedResource($"Cornell.shaders.rasterizer.wgsl", typeof(Rasterizer).Assembly)
	);

	public static Lazy<string> WgslString = new(
		() => Encoding.UTF8.GetString(Wgsl.Value)
	);

	private class RenderPassDescriptorData
	{
		public RenderPassColorAttachment ColorAttachments;
		public RenderPassDepthStencilAttachment DepthStencilAttachment;
	}

	private readonly Common _common;
	private readonly Scene _scene;
	private readonly RenderPipeline _pipeline;
	private readonly BindGroup _bindGroup;

	private readonly RenderPassDescriptorData _renderPassDescriptorData;


	public Rasterizer(
		Device device,
		Common common,
		Scene scene,
		Radiosity radiosity,
		Texture framebuffer)
	{
		_common = common;
		_scene = scene;

		var depthTexture = device.CreateTexture(new()
		{
			Label = "RasterizerRenderer.depthTexture",
			Size = new(framebuffer.GetWidth(), framebuffer.GetHeight()),
			Format = TextureFormat.Depth24Plus,
			Usage = TextureUsage.RenderAttachment,
		});

		_renderPassDescriptorData = new RenderPassDescriptorData()
		{
			ColorAttachments = new()
			{
				View = framebuffer.CreateView(),
				ClearValue = new(0.1, 0.2, 0.3, 1.0),
				LoadOp = LoadOp.Clear,
				StoreOp = StoreOp.Store,
			},
			DepthStencilAttachment = new()
			{
				View = depthTexture.CreateView(),
				DepthClearValue = 1f,
				DepthLoadOp = LoadOp.Clear,
				DepthStoreOp = StoreOp.Store,
			}
		};

		var bindGroupLayout = device.CreateBindGroupLayout(new()
		{
			Label = "RasterizerRenderer.bindGroupLayout",
			Entries =
			[
				new()
				{
					Binding = 0,
					Visibility = ShaderStage.Fragment | ShaderStage.Compute,
					Texture = new TextureBindingLayout
					{
						ViewDimension = TextureViewDimension.D2Array,
					},
				},
				new()
				{
					Binding = 1,
					Visibility = ShaderStage.Fragment | ShaderStage.Compute,
					Sampler = new SamplerBindingLayout(),
				},
			],
		});


		_bindGroup = device.CreateBindGroup(new()
		{
			Label = "RasterizerRenderer.bindGroup",
			Layout = bindGroupLayout,
			Entries =
			[
				new()
				{
					// lightmap
					Binding = 0,
					TextureView = radiosity.Lightmap.CreateView(),
				},
				new()
				{
					// sampler
					Binding = 1,
					Sampler = device.CreateSampler(new()
					{
						AddressModeU = AddressMode.ClampToEdge,
						AddressModeV = AddressMode.ClampToEdge,
						MagFilter = FilterMode.Linear,
						MinFilter = FilterMode.Linear,
					}),
				},
			],
		});

		var mod = device.CreateShaderModuleWGSL("RasterizerRenderer.module", new()
		{
			Code = Wgsl.Value.Concat(Common.Wgsl.Value).ToArray(),
		});


		_pipeline = device.CreateRenderPipelineSync(new()
		{
			Label = "Rasterizer.pipeline",
			Layout = device.CreatePipelineLayout(new()
			{
				BindGroupLayouts = [common.UniformBindGroupLayout, bindGroupLayout],
			}),
			Vertex = new()
			{
				Module = mod,
				Buffers = scene.VertexBufferLayout,
			},
			Fragment = new()
			{
				Module = mod,
				Targets = [new() { Format = framebuffer.GetFormat() }],
			},
			Primitive = new()
			{
				Topology = PrimitiveTopology.TriangleList,
				CullMode = CullMode.Back,
			},
			DepthStencil = new()
			{
				DepthWriteEnabled = OptionalBool.True,
				DepthCompare = CompareFunction.Less,
				Format = TextureFormat.Depth24Plus,
			},
		});
	}

	public void Run(CommandEncoder commandEncoder)
	{
		var renderPassDescriptor = new RenderPassDescriptor
		{
			ColorAttachments = [_renderPassDescriptorData.ColorAttachments],
			DepthStencilAttachment = _renderPassDescriptorData.DepthStencilAttachment,
		};

		var pass = commandEncoder.BeginRenderPass(renderPassDescriptor);
		pass.SetPipeline(_pipeline);
		pass.SetVertexBuffer(0, _scene.Vertices);
		pass.SetIndexBuffer(_scene.Indices, IndexFormat.Uint16);
		pass.SetBindGroup(0, _common.UniformBindGroup);
		pass.SetBindGroup(1, _bindGroup);
		pass.DrawIndexed(_scene.IndexCount);
		pass.End();
	}
}
