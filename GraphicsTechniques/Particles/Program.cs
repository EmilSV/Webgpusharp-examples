using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}

static uint ToUniformBufferSize(uint originalSize)
{
    return originalSize + 16 - (originalSize % 16);
}


const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = (float)WIDTH / HEIGHT;

const int NUM_PARTICLES = 50000;
const int PARTICLE_POSITION_OFFSET = 0;
const int PARTICLE_COLOR_OFFSET = 4 * 4;
var simulationParams = new SimulationParams();

var asm = Assembly.GetExecutingAssembly();
var particleWGSL = ToBytes(asm.GetManifestResourceStream("Particles.shaders.particle.wgsl")!);
var probabilityMapWGSL = ToBytes(asm.GetManifestResourceStream("Particles.shaders.probabilityMap.wgsl")!);
var random = new Random();


CommandBuffer DrawGui(GuiContext guiContext, Surface surface)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(340, 0));
    ImGui.SetNextWindowSize(new(300, 80));
    ImGui.Begin("Particles",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize
    );

    ImGui.Checkbox("Simulate", ref simulationParams.Simulate);
    ImGui.InputFloat("Delta Time", ref simulationParams.DeltaTime);

    ImGui.End();
    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}


return Run("Particles", WIDTH, HEIGHT, async runContext =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();

    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();

    Adapter adapter = await instance.RequestAdapterAsync(new()
    {
        PowerPreference = PowerPreference.HighPerformance,
        CompatibleSurface = surface
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

    var query = device.GetQueue();

    var surfaceCapabilities = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCapabilities.Formats[0];

    guiContext.SetupIMGUI(device, surfaceFormat);

    void configureContext()
    {
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
    }

    var particlesBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(NUM_PARTICLES * Unsafe.SizeOf<Particle>()),
        Usage = BufferUsage.Vertex | BufferUsage.Storage,
    });

    var renderPipeline = device.CreateRenderPipelineSync(new()
    {
        Layout = null, // Auto-layout
        Vertex = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = particleWGSL,
            }),
            Buffers = new[]
            {
                new VertexBufferLayout()
                {
                    ArrayStride = (ulong)Unsafe.SizeOf<Particle>(),
                    StepMode = VertexStepMode.Instance,
                    Attributes = new[]
                    {
                        new VertexAttribute()
                        {
                            ShaderLocation = 0,
                            Offset = PARTICLE_POSITION_OFFSET,
                            Format = VertexFormat.Float32x3,
                        },
                        new VertexAttribute()
                        {
                            ShaderLocation = 1,
                            Offset = (ulong)PARTICLE_COLOR_OFFSET,
                            Format = VertexFormat.Float32x4,
                        },
                    },
                },
                new VertexBufferLayout()
                {
                    ArrayStride = 2 * 4, // vec2f
                    StepMode = VertexStepMode.Vertex,
                    Attributes = new[]
                    {
                        new VertexAttribute()
                        {
                            ShaderLocation = 2,
                            Offset = 0,
                            Format = VertexFormat.Float32x2,
                        },
                    },
                },
            },
        },
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = particleWGSL,
            }),
            Targets =
            [
                new()
                {
                    Format = surfaceFormat,
                    Blend = new()
                    {
                        Color = new()
                        {
                            SrcFactor = BlendFactor.SrcAlpha,
                            DstFactor = BlendFactor.One,
                            Operation = BlendOperation.Add,
                        },
                        Alpha = new()
                        {
                            SrcFactor = BlendFactor.Zero,
                            DstFactor = BlendFactor.One,
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
            DepthWriteEnabled = OptionalBool.False,
            DepthCompare = CompareFunction.Less,
            Format = TextureFormat.Depth24Plus,
        },
    });

    var depthTexture = device.CreateTexture(new()
    {
        Size = new(WIDTH, HEIGHT),
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment,
    });

    var uniformBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<RenderParams>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var uniformBindGroup = device.CreateBindGroup(new()
    {
        Layout = renderPipeline.GetBindGroupLayout(0),
        Entries =
        [
            new()
            {
                Binding = 0,
                Buffer = uniformBuffer
            },
        ],
    });

    Vector2[] vertexData = [
        new(-1.0f, -1.0f), new(+1.0f, -1.0f), new(-1.0f, +1.0f),
        new(-1.0f, +1.0f), new(+1.0f, -1.0f), new(+1.0f, +1.0f),
    ];

    // Quad vertex buffer
    var quadVertexBuffer = device.CreateBuffer(new()
    {
        Size = vertexData.GetSizeInBytes(), // 6x vec2f
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    });
    quadVertexBuffer.GetMappedRange<Vector2>(data => vertexData.AsSpan().CopyTo(data));
    quadVertexBuffer.Unmap();

    // Texture
    bool IsPowerOf2(long v) => (Math.Log2(v) % 1) == 0;
    var imageStream = ResourceUtils.GetEmbeddedResourceStream("Particles.assets.img.webgpu.png", asm);
    var imageData = ResourceUtils.LoadImage(imageStream!);
    Debug.Assert(imageData.Width == imageData.Height, "image must be square");
    Debug.Assert(IsPowerOf2(imageData.Width), "image must be a power of 2");

    // Calculate number of mip levels required to generate the probability map
    int mipLevelCount = (int)(Math.Log2(Math.Max(imageData.Width, imageData.Height)) + 1);
    var texture = device.CreateTexture(new()
    {
        Size = new(imageData.Width, imageData.Height, 1),
        MipLevelCount = (uint)mipLevelCount,
        Format = TextureFormat.RGBA8Unorm,
        Usage =
        TextureUsage.TextureBinding |
        TextureUsage.StorageBinding |
        TextureUsage.CopyDst |
        TextureUsage.RenderAttachment,
    });
    ResourceUtils.CopyExternalImageToTexture(query, imageData, texture);

    //////////////////////////////////////////////////////////////////////////////
    // Probability map generation
    // The 0'th mip level of texture holds the color data and spawn-probability in
    // the alpha channel. The mip levels 1..N are generated to hold spawn
    // probabilities up to the top 1x1 mip level.
    //////////////////////////////////////////////////////////////////////////////
    {
        var probabilityMapImportLevelPipeline = device.CreateComputePipelineSync(new()
        {
            Layout = null!,
            Compute = new()
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = probabilityMapWGSL,
                }),
                EntryPoint = "import_level",
            },
        });
        var probabilityMapExportLevelPipeline = device.CreateComputePipelineSync(new()
        {
            Layout = null!,
            Compute = new()
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = probabilityMapWGSL,
                }),
                EntryPoint = "export_level",
            },
        });

        var probabilityMapUBOBuffer = device.CreateBuffer(new()
        {
            Size = ToUniformBufferSize(sizeof(uint)),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
        });

        var buffer_a = device.CreateBuffer(new()
        {
            Size = texture.GetWidth() * texture.GetHeight() * 4,
            Usage = BufferUsage.Storage,
        });

        var buffer_b = device.CreateBuffer(new()
        {
            Size = buffer_a.GetSize(),
            Usage = BufferUsage.Storage,
        });

        query.WriteBuffer(probabilityMapUBOBuffer, 0, texture.GetWidth());

        var commandEncoder = device.CreateCommandEncoder();
        for (int level = 0; level < mipLevelCount; level++)
        {
            var levelWidth = Math.Max(1, texture.GetWidth() >> level);
            var levelHeight = Math.Max(1, texture.GetHeight() >> level);
            var pipeline =
                level == 0
                    ? probabilityMapImportLevelPipeline.GetBindGroupLayout(0)
                    : probabilityMapExportLevelPipeline.GetBindGroupLayout(0);
            var probabilityMapBindGroup = device.CreateBindGroup(new()
            {
                Layout = pipeline,
                Entries =
                [
                    new()
                    {
                        // ubo
                        Binding = 0,
                        Buffer = probabilityMapUBOBuffer,
                    },
                    new()
                    {
                        // buf_in
                        Binding = 1,
                        Buffer = (level & 1) != 0 ? buffer_a : buffer_b,
                    },
                    new()
                    {
                        // buf_out
                        Binding = 2,
                        Buffer = (level & 1) != 0 ? buffer_b : buffer_a,
                    },
                    new()
                    {
                        // tex_in / tex_out
                        Binding = 3,
                        TextureView = texture.CreateView(new()
                        {
                            Format = TextureFormat.RGBA8Unorm,
                            Dimension = TextureViewDimension.D2,
                            BaseMipLevel = (uint)level,
                            MipLevelCount = 1,
                        }),
                    },
                ],
            });

            if (level == 0)
            {
                var passEncoder = commandEncoder.BeginComputePass();
                passEncoder.SetPipeline(probabilityMapImportLevelPipeline);
                passEncoder.SetBindGroup(0, probabilityMapBindGroup);
                passEncoder.DispatchWorkgroups((uint)Math.Ceiling(levelWidth / 64.0), levelHeight);
                passEncoder.End();
            }
            else
            {
                var passEncoder = commandEncoder.BeginComputePass();
                passEncoder.SetPipeline(probabilityMapExportLevelPipeline);
                passEncoder.SetBindGroup(0, probabilityMapBindGroup);
                passEncoder.DispatchWorkgroups((uint)Math.Ceiling(levelWidth / 64.0), levelHeight);
                passEncoder.End();
            }
        }
        query.Submit([commandEncoder.Finish()]);
    }

    //////////////////////////////////////////////////////////////////////////////
    // Simulation compute pipeline
    //////////////////////////////////////////////////////////////////////////////
    var simulationUBOBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<SimulationUBOParams>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });


    var computePipeline = device.CreateComputePipelineSync(new()
    {
        Layout = null!,
        Compute = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = particleWGSL,
            }),
            EntryPoint = "simulate",
        },
    });

    var computeBindGroup = device.CreateBindGroup(new()
    {
        Layout = computePipeline.GetBindGroupLayout(0),
        Entries =
        [
            new()
            {
                Binding = 0,
                Buffer = simulationUBOBuffer,
            },
            new()
            {
                Binding = 1,
                Buffer = particlesBuffer,
                Offset = 0,
                Size = (ulong)(NUM_PARTICLES * Unsafe.SizeOf<Particle>()),
            },
            new()
            {
                Binding = 2,
                TextureView = texture.CreateView(),
            },
        ],
    });

    var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 2 / 5, ASPECT, 1.0f, 100.0f);
    var view = Matrix4x4.Identity;
    var mvp = Matrix4x4.Identity;

    void Frame()
    {
        query.WriteBuffer(
            simulationUBOBuffer,
            0,
            new SimulationUBOParams
            {
                DeltaTime = simulationParams.Simulate ? simulationParams.DeltaTime : 0.0f,
                BrightnessFactor = simulationParams.BrightnessFactor,
                Seed = new(
                    x: random.NextSingle() * 100,
                    y: random.NextSingle() * 100,
                    z: 1.0f + random.NextSingle(),
                    w: 1.0f + random.NextSingle()
                )
            });

        view = Matrix4x4.CreateTranslation(0, 0, -3);
        view.RotateX(MathF.PI * -0.2f);

        mvp = Matrix4x4.Multiply(view, projection);

        query.WriteBuffer(
            uniformBuffer,
            0,
            new RenderParams()
            {
                ModelViewProjectionMatrix = mvp,
                Right = new(view.M11, view.M21, view.M31),
                Up = new(view.M12, view.M22, view.M32),
            }
        );

        var swapChainTexture = surface.GetCurrentTexture();

        var renderPassDescriptor = new RenderPassDescriptor()
        {
            ColorAttachments =
            [
                new()
                {
                    View = swapChainTexture.Texture!.CreateView(),
                    ClearValue = new Color(0, 0, 0, 1),
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
        };

        var commandEncoder = device.CreateCommandEncoder();
        {
            var passEncoder = commandEncoder.BeginComputePass();
            passEncoder.SetPipeline(computePipeline);
            passEncoder.SetBindGroup(0, computeBindGroup);
            passEncoder.DispatchWorkgroups((uint)Math.Ceiling(NUM_PARTICLES / 64.0));
            passEncoder.End();
        }
        {
            var passEncoder = commandEncoder.BeginRenderPass(renderPassDescriptor);
            passEncoder.SetPipeline(renderPipeline);
            passEncoder.SetBindGroup(0, uniformBindGroup);
            passEncoder.SetVertexBuffer(0, particlesBuffer);
            passEncoder.SetVertexBuffer(1, quadVertexBuffer);
            passEncoder.Draw(6, NUM_PARTICLES, 0, 0);
            passEncoder.End();
        }

        var guiCommandBuffer = DrawGui(guiContext, surface);

        query.Submit([commandEncoder.Finish(), guiCommandBuffer]);
        surface.Present();
    }
    configureContext();
    runContext.OnFrame += Frame;
});

[StructLayout(LayoutKind.Sequential)]
struct Particle
{
    public Vector3 Position;
    public float Lifetime;
    public Vector4 Color;
    public Vector3 Velocity;
    private float _pad0;
}

[StructLayout(LayoutKind.Sequential)]
struct RenderParams
{
    public Matrix4x4 ModelViewProjectionMatrix;
    public Vector3 Right;
    private float _pad0;
    public Vector3 Up;
    private float _pad1;
};

[StructLayout(LayoutKind.Sequential)]
struct SimulationUBOParams
{
    public float DeltaTime;
    public float BrightnessFactor;
    private Vector2 _pad0;
    public Vector4 Seed;
}

class SimulationParams
{
    public bool Simulate = true;
    public float DeltaTime = 0.04f;
    //Right now ToneMappingMode is not supported in dawn native
    public ToneMappingMode ToneMappingMode = ToneMappingMode.Standard;
    //Right now ToneMappingMode is not supported in dawn native
    public float BrightnessFactor = 1.0f;
}