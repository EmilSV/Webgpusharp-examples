using System.Text;
using WebGpuSharp;

namespace Cornell;

/// <summary>
/// Implements the rasterization path for the Cornell box scene.
/// </summary>
public sealed class Rasterizer
{
	private readonly Common _common;
	private readonly Scene _scene;
	private readonly TextureView _framebufferView;
	private readonly TextureView _depthView;
	private readonly TextureFormat _framebufferFormat;
	private readonly RenderPipeline _pipeline;
	private readonly BindGroup _bindGroup;

	public Rasterizer(Device device, Common common, Scene scene, Radiosity radiosity, Texture framebuffer, string rasterizerShaderSource, string commonShaderSource)
	{
		_common = common;
		_scene = scene;

		_framebufferView = framebuffer.CreateView();
		_framebufferFormat = framebuffer.GetFormat();

		var depthTexture = device.CreateTexture(new()
		{
			Label = "Rasterizer.depth",
			Size = new Extent3D(framebuffer.GetWidth(), framebuffer.GetHeight(), 1),
			Format = TextureFormat.Depth24Plus,
			Usage = TextureUsage.RenderAttachment,
		});
		_depthView = depthTexture.CreateView();

		var bindGroupLayout = device.CreateBindGroupLayout(new()
		{
			Label = "Rasterizer.bindGroupLayout",
			Entries =
			[
				new BindGroupLayoutEntry
				{
					Binding = 0,
					Visibility = ShaderStage.Fragment | ShaderStage.Compute,
					Texture = new TextureBindingLayout
					{
						ViewDimension = TextureViewDimension.D2Array,
					},
				},
				new BindGroupLayoutEntry
				{
					Binding = 1,
					Visibility = ShaderStage.Fragment | ShaderStage.Compute,
					Sampler = new SamplerBindingLayout(),
				},
			],
		});

		var sampler = device.CreateSampler(new()
		{
			AddressModeU = AddressMode.ClampToEdge,
			AddressModeV = AddressMode.ClampToEdge,
			MagFilter = FilterMode.Linear,
			MinFilter = FilterMode.Linear,
		});

		_bindGroup = device.CreateBindGroup(new()
		{
			Label = "Rasterizer.bindGroup",
			Layout = bindGroupLayout,
			Entries =
			[
				new BindGroupEntry
				{
					Binding = 0,
					TextureView = radiosity.Lightmap.CreateView(),
				},
				new BindGroupEntry
				{
					Binding = 1,
					Sampler = sampler,
				},
			],
		});

		string shaderSource = rasterizerShaderSource + commonShaderSource;
		var shaderModule = device.CreateShaderModuleWGSL(new()
		{
			Code = Encoding.UTF8.GetBytes(shaderSource),
		});

		var vertexState = new VertexState
		{
			Module = shaderModule,
			Buffers = scene.VertexBufferLayout,
		};

		_pipeline = device.CreateRenderPipeline(new()
		{
			Label = "Rasterizer.pipeline",
			Layout = device.CreatePipelineLayout(new()
			{
				BindGroupLayouts = [common.UniformBindGroupLayout, bindGroupLayout],
			}),
			Vertex = ref vertexState,
			Fragment = new FragmentState
			{
				Module = shaderModule,
				Targets = [new ColorTargetState { Format = _framebufferFormat }],
			},
			Primitive = new PrimitiveState
			{
				Topology = PrimitiveTopology.TriangleList,
				CullMode = CullMode.Back,
			},
			DepthStencil = new DepthStencilState
			{
				DepthWriteEnabled = OptionalBool.True,
				DepthCompare = CompareFunction.Less,
				Format = TextureFormat.Depth24Plus,
			},
		});
	}

	public void Run(CommandEncoder commandEncoder)
	{
		var colorAttachment = new RenderPassColorAttachment
		{
			View = _framebufferView,
			ClearValue = new Color(0.1, 0.2, 0.3, 1.0),
			LoadOp = LoadOp.Clear,
			StoreOp = StoreOp.Store,
		};

		var colorAttachments = new RenderPassColorAttachment[] { colorAttachment };

		var renderPassDescriptor = new RenderPassDescriptor
		{
			ColorAttachments = colorAttachments,
			DepthStencilAttachment = new RenderPassDepthStencilAttachment
			{
				View = _depthView,
				DepthClearValue = 1f,
				DepthLoadOp = LoadOp.Clear,
				DepthStoreOp = StoreOp.Store,
			},
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
