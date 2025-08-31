using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using WebGpuSharp.FFI;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;

const int VIEWPORT_SIZE = 200;
const double VIEWPORT_GRID_SIZE = 4;
double viewportGridStride = Math.Floor(VIEWPORT_SIZE / VIEWPORT_GRID_SIZE);
uint viewportSize = (uint)(viewportGridStride - 2);

const int WINDOW_WIDTH = 1100;
const int WINDOW_HEIGHT = 600;

FilterMode[] filterModes = [FilterMode.Nearest, FilterMode.Linear];


static Matrix4x4 Scale(Matrix4x4 m, Vector3 s)
{
    m.Scale(s);
    return m;
}

PlanSettings initConfig = new()
{
    FlangeLogSize = 1.0f,
    HighlightFlange = false,
    Animation = 0.1f
};
var config = initConfig;

SamplerDescriptorSettings initSamplerDescriptor = new()
{
    AddressModeU = AddressMode.ClampToEdge,
    AddressModeV = AddressMode.ClampToEdge,
    MagFilter = FilterMode.Linear,
    MinFilter = FilterMode.Linear,
    MipmapFilter = MipmapFilterMode.Linear,
    LodMinClamp = 0,
    LodMaxClamp = 4,
    MaxAnisotropy = 1
};

SamplerDescriptorSettings samplerDescriptor = initSamplerDescriptor;

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}


CommandBuffer DrawGui(GuiContext guiContext, Surface surface)
{
    guiContext.NewFrame();

    ImGui.SetNextWindowPos(new(600, 0));
    ImGui.SetNextWindowSize(new(500, 600));
    ImGui.Begin("Sampler Parameters",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoCollapse
    );

    if (ImGui.TreeNode("Presets"))
    {
        if (ImGui.Button("Reset to Initial"))
        {
            config = initConfig;
            samplerDescriptor = initSamplerDescriptor;
        }

        if (ImGui.Button("Checkered floor"))
        {
            config.FlangeLogSize = 10f;
            samplerDescriptor.AddressModeU = AddressMode.Repeat;
            samplerDescriptor.AddressModeV = AddressMode.Repeat;
        }

        if (ImGui.Button("Smooth (linear)"))
        {
            samplerDescriptor.MagFilter = FilterMode.Linear;
            samplerDescriptor.MinFilter = FilterMode.Linear;
            samplerDescriptor.MipmapFilter = MipmapFilterMode.Linear;
        }

        if (ImGui.Button("Crunchy (nearest)"))
        {
            samplerDescriptor.MagFilter = FilterMode.Nearest;
            samplerDescriptor.MinFilter = FilterMode.Nearest;
            samplerDescriptor.MipmapFilter = MipmapFilterMode.Nearest;
        }
        ImGui.TreePop();
    }

    if (ImGui.TreeNode("Plane settings"))
    {
        ImGui.SliderFloat("size = 2**", ref config.FlangeLogSize, 0f, 10.0f);
        ImGui.Checkbox("Highlight Flange", ref config.HighlightFlange);
        ImGui.SliderFloat("Animation", ref config.Animation, 0f, 0.5f);
        ImGui.TreePop();
    }

    if (ImGui.TreeNode("GPUSamplerDescriptor"))
    {
        ReadOnlySpan<AddressMode> addressModes = [AddressMode.ClampToEdge, AddressMode.Repeat, AddressMode.MirrorRepeat];
        ImGuiUtils.EnumDropdown("Address Mode U", ref samplerDescriptor.AddressModeU, addressModes);
        ImGuiUtils.EnumDropdown("Address Mode V", ref samplerDescriptor.AddressModeV, addressModes);

        ReadOnlySpan<FilterMode> filterModes = [FilterMode.Nearest, FilterMode.Linear];
        ImGuiUtils.EnumDropdown("Mag Filter", ref samplerDescriptor.MagFilter, filterModes);
        ImGuiUtils.EnumDropdown("Min Filter", ref samplerDescriptor.MinFilter, filterModes);
        ReadOnlySpan<MipmapFilterMode> mipmapFilterModes = [MipmapFilterMode.Nearest, MipmapFilterMode.Linear];
        ImGuiUtils.EnumDropdown("Mipmap Filter", ref samplerDescriptor.MipmapFilter, mipmapFilterModes);

        if (ImGui.SliderFloat("Lod Min Clamp", ref samplerDescriptor.LodMinClamp, 0f, 4f))
        {
            if (samplerDescriptor.LodMaxClamp < samplerDescriptor.LodMinClamp)
            {
                samplerDescriptor.LodMaxClamp = samplerDescriptor.LodMinClamp;
            }
        }

        if (ImGui.SliderFloat("Lod Max Clamp", ref samplerDescriptor.LodMaxClamp, 0f, 4f))
        {
            if (samplerDescriptor.LodMinClamp > samplerDescriptor.LodMaxClamp)
            {
                samplerDescriptor.LodMinClamp = samplerDescriptor.LodMaxClamp;
            }
        }

        if (ImGui.TreeNode("Max Anisotropy (set only if all \"linear\")"))
        {
            int maxAnisotropy = samplerDescriptor.MaxAnisotropy;
            const int MaxAnisotropy = 16;
            if (ImGui.SliderInt("Max Anisotropy", ref maxAnisotropy, 1, MaxAnisotropy))
            {
                samplerDescriptor.MaxAnisotropy = (ushort)maxAnisotropy;
            }
            ImGui.TreePop();
        }
    }
    ImGui.End();

    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}


Matrix4x4[] matrices = [
    // Row 1: Scale by 2
    Scale(Matrix4x4.CreateRotationZ(MathF.PI / 16f), new(2,2,1)),
    Scale(Matrix4x4.Identity, new(2,2,1)),
    Scale(Matrix4x4.CreateRotationX(-MathF.PI * 0.3f), new(2,2,1)),
    Scale(Matrix4x4.CreateRotationX(-MathF.PI * 0.42f), new(2,2,1)),
    // Row 2: scale by 1
    Matrix4x4.CreateRotationZ(MathF.PI / 16f),
    Matrix4x4.Identity,
    Matrix4x4.CreateRotationX(-MathF.PI * 0.3f),
    Matrix4x4.CreateRotationX(-MathF.PI * 0.42f),
    // Row 3: Scale by 0.9
    Scale(Matrix4x4.CreateRotationZ(MathF.PI / 16f), new(0.9f,0.9f,1)),
    Scale(Matrix4x4.Identity, new(0.9f,0.9f,1)),
    Scale(Matrix4x4.CreateRotationX(-MathF.PI * 0.3f), new(0.9f,0.9f,1)),
    Scale(Matrix4x4.CreateRotationX(-MathF.PI * 0.42f), new(0.9f,0.9f,1)),
    // Row 4: Scale by 0.3
    Scale(Matrix4x4.CreateRotationZ(MathF.PI / 16f), new(0.3f,0.3f,1)),
    Scale(Matrix4x4.Identity, new(0.3f,0.3f,1)),
    Scale(Matrix4x4.CreateRotationX(-MathF.PI * 0.3f), new(0.3f,0.3f,1)),
];

var asm = Assembly.GetExecutingAssembly();
var showTextureWGSL = ToBytes(asm.GetManifestResourceStream("SamplerParameters.shaders.showTexture.wgsl")!);
var texturedSquareWGSL = ToBytes(asm.GetManifestResourceStream("SamplerParameters.shaders.texturedSquare.wgsl")!);

return Run("Sampler Parameters", WINDOW_WIDTH, WINDOW_HEIGHT, async (instance, surface, guiContext, onFrame) =>
{
    var adapter = await instance.RequestAdapterAsync(new() { CompatibleSurface = surface });
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
    var queue = device.GetQueue();
    var surfaceCaps = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCaps.Formats[0];

    guiContext.SetupIMGUI(device, surfaceFormat);

    surface.Configure(new()
    {
        Width = WINDOW_WIDTH,
        Height = WINDOW_HEIGHT,

        Usage = TextureUsage.RenderAttachment,
        Format = surfaceFormat,
        Device = device,
        PresentMode = PresentMode.Fifo,
        AlphaMode = CompositeAlphaMode.Auto,
    });



    var lowResTexture = device.CreateTexture(new()
    {
        Size = new(VIEWPORT_SIZE, VIEWPORT_SIZE),
        Format = surfaceFormat,
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding
    });
    var lowResView = lowResTexture.CreateView();

    var pixelBlitWGSL = ToBytes(asm.GetManifestResourceStream("SamplerParameters.shaders.pixelBlitNearest.wgsl")!);
    var pixelBlitModule = device.CreateShaderModuleWGSL(new() { Code = pixelBlitWGSL });

    var blitPipeline = device.CreateRenderPipeline(new()
    {
        Layout = null,
        Vertex = ref InlineInit(new VertexState
        {
            Module = pixelBlitModule,
            EntryPoint = "vs"
        }),
        Fragment = new FragmentState
        {
            Module = pixelBlitModule,
            EntryPoint = "fs",
            Targets = [new() { Format = surfaceFormat }]
        },
        Primitive = new() { Topology = PrimitiveTopology.TriangleList },
    });

    var nearestSampler = device.CreateSampler(new()
    {
        MagFilter = FilterMode.Nearest,
        MinFilter = FilterMode.Nearest,
        MipmapFilter = MipmapFilterMode.Nearest,
    });

    var blitBindGroup = device.CreateBindGroup(new()
    {
        Layout = blitPipeline.GetBindGroupLayout(0),
        Entries = [
            new(){ Binding = 0, Sampler = nearestSampler },
            new(){ Binding = 1, TextureView = lowResView },
        ]
    });


    // Set up a texture with 4 mip levels, each containing a differently-colored
    // checkerboard with 1x1 pixels (so when rendered the checkerboards are
    // different sizes). This is different from a normal mipmap where each level
    // would look like a lower-resolution version of the previous one.
    // Level 0 is 16x16 white/black
    // Level 1 is 8x8 blue/black
    // Level 2 is 4x4 yellow/black
    // Level 3 is 2x2 pink/black
    const int TEXTURE_MIP_LEVELS = 4;
    const int TEXTURE_BASE_SIZE = 16;
    var checkerboard = device.CreateTexture(new()
    {
        Format = TextureFormat.RGBA8Unorm,
        Usage = TextureUsage.CopyDst | TextureUsage.TextureBinding,
        Size = new(TEXTURE_BASE_SIZE, TEXTURE_BASE_SIZE),
        MipLevelCount = TEXTURE_MIP_LEVELS
    });
    var checkerboardView = checkerboard.CreateView();

    (byte r, byte g, byte b, byte a)[] colorForLevel = [
        (255, 255, 255, 255), // white
        (30, 136, 229, 255), // blue
        (255, 193, 7, 255), // yellow
        (216, 27, 96, 255), // pink
    ];

    (byte r, byte g, byte b, byte a) defaultColor = (0, 0, 0, 255);

    for (uint mipLevel = 0; mipLevel < TEXTURE_MIP_LEVELS; ++mipLevel)
    {
        var size = (uint)Math.Pow(2, TEXTURE_MIP_LEVELS - mipLevel); // 16, 8, 4, 2
        var data = new (byte r, byte g, byte b, byte a)[size * size];
        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                data[y * size + x] = ((x + y) % 2) != 0 ?
                    colorForLevel[mipLevel] :
                    defaultColor;
            }
        }

        device.GetQueue().WriteTexture(
            destination: new()
            {
                Texture = checkerboard,
                MipLevel = mipLevel,

            },
            data: data,
            dataLayout: new()
            {
                RowsPerImage = WebGPU_FFI.COPY_STRIDE_UNDEFINED,
                BytesPerRow = size * 4,
            },
            writeSize: new Extent3D(size, size)
        );
    }

    var showTextureModule = device.CreateShaderModuleWGSL(new()
    {
        Code = showTextureWGSL
    });

    var showTexturePipeline = device.CreateRenderPipeline(new()
    {
        Layout = null, // Autogenerate the pipeline layout,
        Vertex = ref InlineInit(new VertexState()
        {
            Module = showTextureModule,
        }),
        Fragment = new FragmentState()
        {
            Module = showTextureModule,
            Targets = [
                new()
                {
                    Format = surfaceFormat
                }
            ]
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList
        },
    });

    var showTextureBG = device.CreateBindGroup(new()
    {
        Layout = showTexturePipeline.GetBindGroupLayout(0),
        Entries = [
            new()
            {
                Binding = 0,
                TextureView = checkerboardView,
            }
        ]
    });

    var showLowResBG = device.CreateBindGroup(new()
    {
        Layout = showTexturePipeline.GetBindGroupLayout(0),
        Entries = [
            new()
            {
                Binding = 0,
                TextureView = lowResView
            }
        ]
    });

    var texturedSquareModule = device.CreateShaderModuleWGSL(new()
    {
        Code = texturedSquareWGSL
    });

    var texturedSquarePipeline = device.CreateRenderPipeline(new()
    {
        Layout = null, // Autogenerate the pipeline layout,
        Vertex = ref InlineInit(new VertexState()
        {
            Module = texturedSquareModule,
            Constants = [
                new()
                {
                    Key = "textureBaseSize",
                    Value = TEXTURE_BASE_SIZE,
                },
                new()
                {
                    Key = "viewportSize",
                    Value = viewportSize,
                }
            ]
        }),
        Fragment = new FragmentState()
        {
            Module = texturedSquareModule,
            Targets = [
                new()
                {
                    Format = surfaceFormat
                }
            ]
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList
        },
    });
    var texturedSquareBGL = texturedSquarePipeline.GetBindGroupLayout(0);

    var bufConfig = device.CreateBuffer(new()
    {
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
        Size = 128,
    });

    void UpdateConfigBuffer()
    {
        float t = (float)(Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).TotalSeconds * 0.5);
        queue.WriteBuffer(bufConfig, 64, (
            MathF.Cos(t) * config.Animation,
            MathF.Sin(t) * config.Animation,
            MathF.Pow(2, config.FlangeLogSize - 1) / 2f,
            config.HighlightFlange ? 1f : 0f
        ));
    }

    const float cameraDist = 3;
    Matrix4x4 viewProj = Matrix4x4.CreatePerspectiveFieldOfView(
        fieldOfView: 2f * MathF.Atan(1f / cameraDist),
        aspectRatio: 1f,
        nearPlaneDistance: 0.1f,
        farPlaneDistance: 100f
    );
    viewProj.Translate(new(0, 0, -cameraDist));
    queue.WriteBuffer(bufConfig, 0, viewProj);

    var bufMatrices = device.CreateBuffer(new()
    {
        Usage = BufferUsage.Storage,
        Size = (ulong)(matrices.Length * Unsafe.SizeOf<Matrix4x4>()),
        MappedAtCreation = true
    });

    bufMatrices.GetMappedRange<Matrix4x4>(data =>
    {
        matrices.AsSpan().CopyTo(data);
    });
    bufMatrices.Unmap();

    onFrame(() =>
    {
        UpdateConfigBuffer();

        var sampler = device.CreateSampler(new()
        {
            AddressModeU = samplerDescriptor.AddressModeU,
            AddressModeV = samplerDescriptor.AddressModeV,
            MagFilter = samplerDescriptor.MagFilter,
            MinFilter = samplerDescriptor.MinFilter,
            MipmapFilter = samplerDescriptor.MipmapFilter,
            LodMinClamp = samplerDescriptor.LodMinClamp,
            LodMaxClamp = samplerDescriptor.LodMaxClamp,
            MaxAnisotropy =
                samplerDescriptor.MinFilter == FilterMode.Linear &&
                samplerDescriptor.MagFilter == FilterMode.Linear &&
                samplerDescriptor.MipmapFilter == MipmapFilterMode.Linear
                ? samplerDescriptor.MaxAnisotropy
                : (ushort)1
        });

        var bindGroup = device.CreateBindGroup(new()
        {
            Layout = texturedSquareBGL,
            Entries = [
                new()
                {
                    Binding = 0,
                    Buffer = bufConfig,
                },
                new()
                {
                    Binding = 1,
                    Buffer = bufMatrices
                },
                new()
                {
                    Binding = 2,
                    Sampler = sampler
                },
                new()
                {
                    Binding = 3,
                    TextureView = checkerboardView
                }
            ]
        });

        var commandEncoder = device.CreateCommandEncoder();
        // FIRST PASS: render scene into lowResView (200x200)
        {
            var renderPassDescriptor = new RenderPassDescriptor
            {
                ColorAttachments = [
                    new()
                    {
                        View = lowResView,
                        ClearValue = new(0.2f, 0.2f, 0.2f, 1.0f),
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                    }
                ]
            };
            var pass = commandEncoder.BeginRenderPass(renderPassDescriptor);
            pass.SetPipeline(texturedSquarePipeline);
            pass.SetBindGroup(0, bindGroup);
            for (uint i = 0; i < Math.Pow(VIEWPORT_GRID_SIZE, 2) - 1; ++i)
            {
                uint vpX = (uint)(viewportGridStride * (i % VIEWPORT_GRID_SIZE) + 1);
                uint vpY = (uint)(viewportGridStride * Math.Floor(i / VIEWPORT_GRID_SIZE) + 1);
                pass.SetViewport(vpX, vpY, viewportSize, viewportSize, 0, 1);
                pass.Draw(6, 1, 0, i);
            }
            // Show texture contents
            pass.SetPipeline(showTexturePipeline);
            pass.SetBindGroup(0, showTextureBG);
            uint lastViewport = (uint)((VIEWPORT_GRID_SIZE - 1) * viewportGridStride + 1);
            pass.SetViewport(lastViewport, lastViewport, 32, 32, 0, 1);
            pass.Draw(6, 1, 0, 0);
            pass.SetViewport(lastViewport + 32, lastViewport, 16, 16, 0, 1);
            pass.Draw(6, 1, 0, 1);
            pass.SetViewport(lastViewport + 32, lastViewport + 16, 8, 8, 0, 1);
            pass.Draw(6, 1, 0, 2);
            pass.SetViewport(lastViewport + 32, lastViewport + 24, 4, 4, 0, 1);
            pass.Draw(6, 1, 0, 3);
            pass.End();
        }
        // SECOND PASS: upscale to window (600x600) without smoothing (nearest / texel fetch)
        var swapView = surface.GetCurrentTexture().Texture!.CreateView();
        {
            var rp2 = new RenderPassDescriptor
            {
                ColorAttachments = [
                    new()
                    {
                        View = swapView,
                        ClearValue = new(0.2f, 0.2f, 0.2f, 1.0f),
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                    }
                ]
            };
            var pass2 = commandEncoder.BeginRenderPass(rp2);
            pass2.SetPipeline(blitPipeline);
            pass2.SetBindGroup(0, blitBindGroup);
            pass2.SetViewport(0, 0, 600, 600, 0, 1);
            pass2.Draw(6, 1, 0, 0);
            pass2.End();
        }

        var guiCommandBuffer = DrawGui(guiContext, surface);

        // Submit
        queue.Submit([commandEncoder.Finish(), guiCommandBuffer]);
        surface.Present();
    });
});


struct PlanSettings
{
    public float FlangeLogSize;
    public bool HighlightFlange;
    public float Animation;
}

struct SamplerDescriptorSettings
{
    public AddressMode AddressModeU;
    public AddressMode AddressModeV;
    public FilterMode MagFilter;
    public FilterMode MinFilter;
    public MipmapFilterMode MipmapFilter;
    public float LodMinClamp;
    public float LodMaxClamp;
    public ushort MaxAnisotropy;
}