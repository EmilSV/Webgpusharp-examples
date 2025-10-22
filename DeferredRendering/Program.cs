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
using static WebGpuSharp.WebGpuUtil;

const int MAX_NUM_LIGHTS = 1024;
const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = (float)WIDTH / HEIGHT;


var lightExtentMin = new Vector3(-50, -30, -50);
var lightExtentMax = new Vector3(50, 50, 50);

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}

var settings = new Settings();
var asm = Assembly.GetExecutingAssembly();
var fragmentDeferredRenderingWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.fragmentDeferredRendering.wgsl")!);
var fragmentGBuffersDebugViewWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.fragmentGBuffersDebugView.wgsl")!);
var fragmentWriteGBuffersWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.fragmentWriteGBuffers.wgsl")!);
var lightUpdateWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.lightUpdate.wgsl")!);
var vertexTextureQuadWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.vertexTextureQuad.wgsl")!);
var vertexWriteGBuffersWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.vertexWriteGBuffers.wgsl")!);
var mesh = await StanfordDragon.LoadMeshAsync();

CommandBuffer DrawGUI(GuiContext guiContext, Surface surface, out bool numLightsChanged)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(340, 0));
    ImGui.SetNextWindowSize(new(300, 80));
    ImGui.Begin("Settings",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize
    );

    ImGuiUtils.EnumDropdown("mode", ref settings.Mode);
    numLightsChanged = ImGui.SliderInt("numLights", ref settings.NumLights, 1, MAX_NUM_LIGHTS);

    ImGui.End();
    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}


return Run("Deferred Rendering", WIDTH, HEIGHT, async runContext =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();

    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();

    var adapter = await instance.RequestAdapterAsync(new RequestAdapterOptions
    {
        CompatibleSurface = surface,
        FeatureLevel = FeatureLevel.Compatibility,
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
    var queue = device.GetQueue();
    var surfaceCapabilities = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCapabilities.Formats[0];

    guiContext.SetupIMGUI(device, surfaceFormat);

    surface.Configure(new SurfaceConfiguration
    {
        Width = WIDTH,
        Height = HEIGHT,
        Usage = TextureUsage.RenderAttachment,
        Format = surfaceFormat,
        Device = device,
        PresentMode = PresentMode.Fifo,
        AlphaMode = CompositeAlphaMode.Auto,
    });

    Debug.Assert(
        mesh.Positions.Length == mesh.Normals.Length &&
        mesh.Positions.Length == mesh.Uvs.Length
    );

    // Create the model vertex buffer.
    var vertexBuffer = device.CreateBuffer(new BufferDescriptor
    {
        Size = (ulong)(mesh.Positions.Length * Unsafe.SizeOf<VertexArgs>()),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    });
    vertexBuffer.GetMappedRange<VertexArgs>(data =>
    {
        for (int i = 0; i < mesh.Positions.Length; ++i)
        {
            data[i] = new()
            {
                Position = mesh.Positions[i],
                Normal = mesh.Normals[i],
                Uv = mesh.Uvs[i]
            };
        }
    });
    vertexBuffer.Unmap();

    // Create the model index buffer.
    var indexCount = mesh.Triangles.Length * 3;
    var indexBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(indexCount * sizeof(ushort)),
        Usage = BufferUsage.Index,
        MappedAtCreation = true,
    });
    indexBuffer.GetMappedRange<ushort>(data =>
    {
        for (int i = 0; i < mesh.Triangles.Length; ++i)
        {
            data[3 * i + 0] = (ushort)mesh.Triangles[i].X;
            data[3 * i + 1] = (ushort)mesh.Triangles[i].Y;
            data[3 * i + 2] = (ushort)mesh.Triangles[i].Z;
        }
    });
    indexBuffer.Unmap();


    // GBuffer texture render targets
    var gBufferTexture2DFloat16 = device.CreateTexture(new()
    {
        Size = new() { Width = WIDTH, Height = HEIGHT },
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
        Format = TextureFormat.RGBA16Float
    });

    var gBufferTextureAlbedo = device.CreateTexture(new()
    {
        Size = new() { Width = WIDTH, Height = HEIGHT },
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
        Format = TextureFormat.BGRA8Unorm
    });

    var depthTexture = device.CreateTexture(new()
    {
        Size = new() { Width = WIDTH, Height = HEIGHT },
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding
    });


    TextureView[] gBufferTextureViews =
    [
        gBufferTexture2DFloat16.CreateView(),
        gBufferTextureAlbedo.CreateView(),
        depthTexture.CreateView(),
    ];

    VertexBufferLayout[] vertexBuffers =
    [
        new()
        {
            ArrayStride = (uint)Unsafe.SizeOf<VertexArgs>(),
            Attributes =
            [
                new()
                {
                    // position
                    ShaderLocation = 0,
                    Offset = (uint)Marshal.OffsetOf<VertexArgs>(nameof(VertexArgs.Position)),
                    Format = VertexFormat.Float32x3,
                },
                new()
                {
                    // normal
                    ShaderLocation = 1,
                    Offset = (uint)Marshal.OffsetOf<VertexArgs>(nameof(VertexArgs.Normal)),
                    Format = VertexFormat.Float32x3,
                },
                new()
                {
                    // uv
                    ShaderLocation = 2,
                    Offset = (uint)Marshal.OffsetOf<VertexArgs>(nameof(VertexArgs.Uv)),
                    Format = VertexFormat.Float32x2,
                },
            ],
        },
    ];

    PrimitiveState primitive = new()
    {
        Topology = PrimitiveTopology.TriangleList,
        CullMode = CullMode.Back,
    };

    var writeGBuffersPipeline = device.CreateRenderPipeline(new()
    {
        Layout = null!,
        Vertex = ref InlineInit(new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexWriteGBuffersWGSL,
            }),
            Buffers = vertexBuffers,
        }),
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = fragmentWriteGBuffersWGSL,
            }),
            Targets =
            [
                // normal
                new() { Format = TextureFormat.RGBA16Float },
                // albedo
                new() { Format = TextureFormat.BGRA8Unorm },
            ],
        },
        DepthStencil = new()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = TextureFormat.Depth24Plus,
        },
        Primitive = primitive,
    });

    var gBufferTexturesBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.UnfilterableFloat,
                },
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.UnfilterableFloat,
                },
            },
            new()
            {
                Binding = 2,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.UnfilterableFloat,
                },
            },
        ],
    });

    var lightsBufferBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment | ShaderStage.Compute,
                Buffer = new()
                {
                    Type = BufferBindingType.ReadOnlyStorage,
                },
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Fragment | ShaderStage.Compute,
                Buffer = new()
                {
                    Type = BufferBindingType.Uniform,
                },
            },
            new()
            {
                Binding = 2,
                Visibility = ShaderStage.Fragment,
                Buffer = new()
                {
                    Type = BufferBindingType.Uniform,
                },
            },
        ],
    });

    var gBuffersDebugViewPipeline = device.CreateRenderPipeline(new()
    {
        Layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts = [gBufferTexturesBindGroupLayout],
        }),
        Vertex = ref InlineInit(new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexTextureQuadWGSL,
            }),
        }),
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = fragmentGBuffersDebugViewWGSL,
            }),
            Targets =
            [
                new() { Format = surfaceFormat },
            ],
            Constants =
            [
                new( "canvasSizeWidth", WIDTH ),
                new( "canvasSizeHeight", HEIGHT ),
            ],
        },
        Primitive = primitive,
    });



    var deferredRenderPipeline = device.CreateRenderPipeline(new()
    {
        Layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts =
            [
                gBufferTexturesBindGroupLayout,
                lightsBufferBindGroupLayout,
            ],
        }),
        Vertex = ref InlineInit(new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexTextureQuadWGSL,
            }),
        }),
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = fragmentDeferredRenderingWGSL,
            }),
            Targets =
            [
                new() { Format = surfaceFormat },
            ],
        },
        Primitive = primitive,
    });

    var writeGBufferPassDescriptor = new RenderPassDescriptor
    {
        ColorAttachments =
        [
            new()
            {
                View = gBufferTextureViews[0],

                ClearValue = new Color(0.0f, 0.0f, 1.0f, 1.0f),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            },
            new()
            {
                View = gBufferTextureViews[1],

                ClearValue = new Color(0, 0, 0, 1),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            },
        ],
        DepthStencilAttachment = new()
        {
            View = gBufferTextureViews[2],

            DepthClearValue = 1.0f,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
        },
    };

    var textureQuadPassDescriptor = new RenderPassDescriptor
    {
        ColorAttachments =
        [
            new()
            {
                // view is acquired and set in render loop.
                View = null!,

                ClearValue = new Color(0, 0, 0, 1),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            },
        ],
    };



    var configUniformBuffer = device.CreateBuffer(new()
    {
        Size = sizeof(uint),
        MappedAtCreation = true,
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });
    configUniformBuffer.GetMappedRange<uint>(data =>
    {
        data[0] = (uint)settings.NumLights;
    });
    configUniformBuffer.Unmap();

    var modelUniformBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(Unsafe.SizeOf<Matrix4x4>() * 2), // two 4x4 matrix
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var cameraUniformBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(Unsafe.SizeOf<Matrix4x4>() * 2), // two 4x4 matrix
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var sceneUniformBindGroup = device.CreateBindGroup(new()
    {
        Layout = writeGBuffersPipeline.GetBindGroupLayout(0),
        Entries =
        [
            new()
            {
                Binding = 0,
                Buffer = modelUniformBuffer
            },
            new()
            {
                Binding = 1,
                Buffer = cameraUniformBuffer
            },
        ],
    });

    var gBufferTexturesBindGroup = device.CreateBindGroup(new()
    {
        Layout = gBufferTexturesBindGroupLayout,
        Entries =
        [
            new()
            {
                Binding = 0,
                TextureView = gBufferTextureViews[0],
            },
            new()
            {
                Binding = 1,
                TextureView = gBufferTextureViews[1],
            },
            new()
            {
                Binding = 2,
                TextureView = gBufferTextureViews[2],
            },
        ],
    });

    // Lights data are uploaded in a storage buffer
    // which could be updated/culled/etc. with a compute shader
    var extent = lightExtentMax - lightExtentMin;
    var lightsBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(Unsafe.SizeOf<LightData>() * MAX_NUM_LIGHTS),
        Usage = BufferUsage.Storage,
        MappedAtCreation = true,
    });

    // We randomaly populate lights randomly in a box range
    // And simply move them along y-axis per frame to show they are
    // dynamic lightings
    lightsBuffer.GetMappedRange<LightData>(lightData =>
    {
        var random = new Random();
        foreach (ref var light in lightData)
        {
            light.Position = new Vector4(
                random.NextSingle() * extent.X + lightExtentMin.X,
                random.NextSingle() * extent.Y + lightExtentMin.Y,
                random.NextSingle() * extent.Z + lightExtentMin.Z,
                1.0f
            );

            light.Color = new Vector3(
                random.NextSingle() * 2,
                random.NextSingle() * 2,
                random.NextSingle() * 2
            );

            light.Radius = 20.0f;
        }
    });
    lightsBuffer.Unmap();

    var lightExtentBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<LightExtent>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    queue.WriteBuffer(
        lightExtentBuffer,
        0,
        new LightExtent
        {
            Min = new(lightExtentMin, 0),
            Max = new(lightExtentMax, 0)
        }
    );

    var lightUpdateComputePipeline = device.CreateComputePipeline(new()
    {
        Layout = null!,
        Compute = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = lightUpdateWGSL,
            }),
        },
    });

    var lightsBufferBindGroup = device.CreateBindGroup(new()
    {
        Layout = lightsBufferBindGroupLayout,
        Entries =
        [
            new()
            {
                Binding = 0,
                Buffer = lightsBuffer
            },
            new()
            {
                Binding = 1,
                Buffer = configUniformBuffer
            },
            new()
            {
                Binding = 2,
                Buffer = cameraUniformBuffer
            },
        ],
    });

    var lightsBufferComputeBindGroup = device.CreateBindGroup(new()
    {
        Layout = lightUpdateComputePipeline.GetBindGroupLayout(0),
        Entries =
        [
            new()
            {
                Binding = 0,
                Buffer = lightsBuffer
            },
            new()
            {
                Binding = 1,
                Buffer = configUniformBuffer
            },
            new()
            {
                Binding = 2,
                Buffer = lightExtentBuffer
            },
        ],
    });

    // Scene matrices
    var eyePosition = new Vector3(0, 50, -100);
    var upVector = new Vector3(0, 1, 0);
    var origin = Vector3.Zero;

    var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
        fieldOfView: MathF.PI * 2 / 5,
        aspectRatio: ASPECT,
        nearPlaneDistance: 1.0f,
        farPlaneDistance: 2000.0f
    );

    // Move the model so it's centered.
    var modelMatrix = Matrix4x4.CreateTranslation(0, -45, 0);
    queue.WriteBuffer(modelUniformBuffer, modelMatrix);
    var normalModelData = Matrix4x4.Invert(modelMatrix, out var invertedModelMatrix)
        ? Matrix4x4.Transpose(invertedModelMatrix)
        : throw new InvalidOperationException("Could not invert model matrix");

    queue.WriteBuffer(modelUniformBuffer, (ulong)Unsafe.SizeOf<Matrix4x4>(), normalModelData);

    // Rotates the camera around the origin based on time.
    Matrix4x4 GetCameraViewProjMatrix()
    {
        var rad = MathF.PI * (float)(Stopwatch.GetElapsedTime(startTimeStamp).TotalMilliseconds / 5000);
        var rotation = Matrix4x4.CreateTranslation(origin);
        rotation.RotateY(rad);

        var rotatedEyePosition = Vector3.Transform(eyePosition, rotation);
        var viewMatrix = Matrix4x4.CreateLookAt(rotatedEyePosition, origin, upVector);

        return viewMatrix * projectionMatrix;
    }

    runContext.OnFrame += () =>
    {
        var guiCommanderBuffer = DrawGUI(guiContext, surface, out var numLightsChanged);
        if (numLightsChanged)
        {
            queue.WriteBuffer(
                configUniformBuffer,
                0,
                (uint)settings.NumLights
            );
        }

        var cameraViewProj = GetCameraViewProjMatrix();
        queue.WriteBuffer(cameraUniformBuffer, cameraViewProj);

        var cameraInvViewProj = Matrix4x4.Invert(cameraViewProj, out var invViewProj)
            ? invViewProj
            : throw new InvalidOperationException("Could not invert view projection matrix");
        queue.WriteBuffer(cameraUniformBuffer, (ulong)Unsafe.SizeOf<Matrix4x4>(), cameraInvViewProj);

        var commandEncoder = device.CreateCommandEncoder();
        {
            var writeGBufferPassDescriptor = new RenderPassDescriptor
            {
                ColorAttachments =
                [
                    new()
                        {
                            View = gBufferTextureViews[0],

                            ClearValue = new(0.0f, 0.0f, 1.0f, 1.0f),
                            LoadOp = LoadOp.Clear,
                            StoreOp = StoreOp.Store,
                        },
                        new()
                        {
                            View = gBufferTextureViews[1],

                            ClearValue = new(0, 0, 0, 1),
                            LoadOp = LoadOp.Clear,
                            StoreOp = StoreOp.Store,
                        },
                    ],
                DepthStencilAttachment = new()
                {
                    View = gBufferTextureViews[2],

                    DepthClearValue = 1.0f,
                    DepthLoadOp = LoadOp.Clear,
                    DepthStoreOp = StoreOp.Store,
                },
            };
            var gBufferPass = commandEncoder.BeginRenderPass(writeGBufferPassDescriptor);
            gBufferPass.SetPipeline(writeGBuffersPipeline);
            gBufferPass.SetBindGroup(0, sceneUniformBindGroup);
            gBufferPass.SetVertexBuffer(0, vertexBuffer);
            gBufferPass.SetIndexBuffer(indexBuffer, IndexFormat.Uint16);
            gBufferPass.DrawIndexed((uint)indexCount);
            gBufferPass.End();
        }

        // Update lights position
        {
            var lightPass = commandEncoder.BeginComputePass();
            lightPass.SetPipeline(lightUpdateComputePipeline);
            lightPass.SetBindGroup(0, lightsBufferComputeBindGroup);
            lightPass.DispatchWorkgroups((uint)(settings.NumLights / 64));
            lightPass.End();
        }
        {
            var textureQuadPassDescriptor = new RenderPassDescriptor
            {
                ColorAttachments =
                [
                        new()
                        {
                            // view is acquired and set in render loop.
                            View = surface.GetCurrentTexture().Texture!.CreateView(),
                            ClearValue = new Color(0, 0, 0, 1),
                            LoadOp = LoadOp.Clear,
                            StoreOp = StoreOp.Store,
                        },
                    ],
            };

            if (settings.Mode == RenderMode.GBuffersView)
            {
                // GBuffers debug view
                // Left: depth
                // Middle: normal
                // Right: albedo (use uv to mimic a checkerboard texture)
                var debugViewPass = commandEncoder.BeginRenderPass(textureQuadPassDescriptor);
                debugViewPass.SetPipeline(gBuffersDebugViewPipeline);
                debugViewPass.SetBindGroup(0, gBufferTexturesBindGroup);
                debugViewPass.Draw(6);
                debugViewPass.End();
            }
            else
            {
                // Deferred rendering
                var deferredRenderPass = commandEncoder.BeginRenderPass(textureQuadPassDescriptor);
                deferredRenderPass.SetPipeline(deferredRenderPipeline);
                deferredRenderPass.SetBindGroup(0, gBufferTexturesBindGroup);
                deferredRenderPass.SetBindGroup(1, lightsBufferBindGroup);
                deferredRenderPass.Draw(6);
                deferredRenderPass.End();
            }
        }

        queue.Submit([commandEncoder.Finish(), guiCommanderBuffer]);
        surface.Present();
    };

});

enum RenderMode
{
    Rendering,
    GBuffersView,
}

struct VertexArgs
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
}

struct LightData
{
    public Vector4 Position;
    public Vector3 Color;
    public float Radius;
}

struct LightExtent
{
    public Vector4 Min;
    public Vector4 Max;
}

class Settings
{
    public RenderMode Mode = RenderMode.Rendering;
    public int NumLights = 128;
}
