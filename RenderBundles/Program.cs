using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;
using System.Runtime.InteropServices;

static byte[] ToByteArray(Stream input)
{
    using MemoryStream ms = new();
    input.CopyTo(ms);
    return ms.ToArray();
}

const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = WIDTH / (float)HEIGHT;

bool useRenderBundles = true;
int asteroidCount = 100;

double lastFrameTimeStamp = 0;
double fps = 0;

CommandBuffer DrawGUI(GuiContext guiContext, Surface surface, out bool asteroidCountChanged)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.3f);
    ImGui.Begin("Settings",
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoCollapse
    );
    ImGui.Text($"FPS: {fps:F2}");
    ImGui.Checkbox(nameof(useRenderBundles), ref useRenderBundles);
    asteroidCountChanged = ImGui.SliderInt(nameof(asteroidCount), ref asteroidCount, 1000, 50000);
    ImGui.End();
    guiContext.EndFrame();

    return guiContext.Render(surface)!.Value!;
}

return Run("Render Bundles", WIDTH, HEIGHT, async (instance, surface, guiContext, onFrame) =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();

    var executingAssembly = Assembly.GetExecutingAssembly();

    var meshWGSL = ResourceUtils.GetEmbeddedResource("RenderBundles.shaders.mesh.wgsl", executingAssembly);

    var adapter = await instance.RequestAdapterAsync(new() { CompatibleSurface = surface });
    var device = (
        await adapter.RequestDeviceAsync(
            new()
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
            }
        )
    )!;

    var queue = device.GetQueue();
    var surfaceCapabilities = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCapabilities.Formats[0];

    guiContext.SetupIMGUI(device, surfaceFormat);

    surface.Configure(new()
    {
        Width = WIDTH,
        Height = HEIGHT,
        Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
        Format = surfaceFormat,
        Device = device,
        PresentMode = PresentMode.Fifo,
        AlphaMode = CompositeAlphaMode.Auto,
    });

    var shaderModule = device.CreateShaderModuleWGSL(new()
    {
        Code = meshWGSL
    });

    var pipeline = device.CreateRenderPipeline(new()
    {
        Layout = null,
        Vertex = ref InlineInit(new VertexState()
        {
            Module = shaderModule,
            Buffers = [
                new()
                    {
                        ArrayStride =  SphereMesh.Vertex.VertexStride,
                        Attributes= [
                            new()
                            {
                                // position
                                ShaderLocation = 0,
                                Offset = SphereMesh.Vertex.PositionsOffset,
                                Format = VertexFormat.Float32x3,
                            },
                            new()
                            {
                                // normal
                                ShaderLocation = 1,
                                Offset = SphereMesh.Vertex.NormalOffset,
                                Format = VertexFormat.Float32x3,
                            },
                            new()
                            {
                                // uv
                                ShaderLocation = 2,
                                Offset = SphereMesh.Vertex.UvOffset,
                                Format = VertexFormat.Float32x2,
                            },
                        ]
                    }
            ]
        }),
        Fragment = new FragmentState()
        {
            Module = shaderModule,
            Targets = [
                new(){
                        Format = surfaceFormat
                    }
            ]
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList,
            // Backface culling since the sphere is solid piece of geometry.
            // Faces pointing away from the camera will be occluded by faces
            // pointing toward the camera.
            CullMode = CullMode.Back,
        },

        // Enable depth testing so that the fragment closest to the camera
        // is rendered in front.
        DepthStencil = new DepthStencilState()
        {
            DepthWriteEnabled = OptionalBool.True,
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

    const int uniformBufferSize = 4 * 16; // 4x4 matrix
    var uniformBuffer = device.CreateBuffer(new()
    {
        Size = uniformBufferSize,
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    Texture planetTexture;
    {
        var imageStream = ResourceUtils.GetEmbeddedResourceStream("RenderBundles.assets.img.saturn.jpg", executingAssembly);
        var imageData = ResourceUtils.LoadImage(imageStream!);

        planetTexture = device.CreateTexture(new()
        {
            Size = new Extent3D(imageData.Width, imageData.Height, 1),
            Format = TextureFormat.RGBA8Unorm,
            Usage =
                TextureUsage.TextureBinding |
                TextureUsage.CopyDst |
                TextureUsage.RenderAttachment,
        });

        ResourceUtils.CopyExternalImageToTexture(
            queue: queue,
            source: imageData,
            texture: planetTexture,
            width: imageData.Width,
            height: imageData.Height
        );
    }

    Texture moonTexture;
    {
        var imageStream = ResourceUtils.GetEmbeddedResourceStream("RenderBundles.assets.img.moon.jpg", executingAssembly);
        var imageData = ResourceUtils.LoadImage(imageStream!);

        moonTexture = device.CreateTexture(new()
        {
            Size = new Extent3D(imageData.Width, imageData.Height, 1),
            Format = TextureFormat.RGBA8Unorm,
            Usage =
                TextureUsage.TextureBinding |
                TextureUsage.CopyDst |
                TextureUsage.RenderAttachment,
        });

        ResourceUtils.CopyExternalImageToTexture(
            queue: queue,
            source: imageData,
            texture: moonTexture,
            width: imageData.Width,
            height: imageData.Height
        );
    }

    var sampler = device.CreateSampler(new()
    {
        MagFilter = FilterMode.Linear,
        MinFilter = FilterMode.Linear,
    });

    //helper functions to create the required meshes and bind groups for each sphere.
    Renderable CreateSphereRenderable(
        float radius,
        int widthSegments = 32,
        int heightSegments = 16,
        float randomness = 0)
    {
        var sphereMesh = SphereMesh.Create(
            radius,
            widthSegments,
            heightSegments,
            randomness
        );

        var vertices = device!.CreateBuffer(new()
        {
            Size = sphereMesh.Vertices.AsSpan().GetByteLength(),
            Usage = BufferUsage.Vertex,
            MappedAtCreation = true,
        });

        vertices.GetMappedRange<SphereMesh.Vertex>(data =>
        {
            sphereMesh.Vertices.AsSpan().CopyTo(data);
        });
        vertices.Unmap();

        var indices = device!.CreateBuffer(new()
        {
            Size = MathUtils.RoundToNextMultipleOfFour((ulong)System.Buffer.ByteLength(sphereMesh.Indices)),
            Usage = BufferUsage.Index,
            MappedAtCreation = true,
        });
        indices.GetMappedRange<uint>(data =>
        {
            sphereMesh.Indices.AsSpan().CopyTo(data);
        });
        indices.Unmap();

        return new()
        {
            Vertices = vertices,
            Indices = indices,
            IndexCount = sphereMesh.Indices.Length
        };
    }

    BindGroup CreateSphereBindGroup(Texture texture, Matrix4x4 transform)
    {
        var uniformBufferSize = Marshal.SizeOf<Matrix4x4>();
        var uniformBuffer = device!.CreateBuffer(new()
        {
            Size = (uint)uniformBufferSize,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            MappedAtCreation = true,
        });
        uniformBuffer.GetMappedRange<Matrix4x4>(data =>
        {
            data[0] = transform;
        });
        uniformBuffer.Unmap();

        return device!.CreateBindGroup(new()
        {
            Layout = pipeline!.GetBindGroupLayout(1),
            Entries = [
                new()
                    {
                        Binding = 0,
                        Buffer = uniformBuffer,
                    },
                    new()
                    {
                        Binding = 1,
                        Sampler = sampler,
                    },
                    new()
                    {
                        Binding = 2,
                        TextureView = texture.CreateView(),
                    },
                ],
        });
    }

    var transform = Matrix4x4.Identity;

    var planet = CreateSphereRenderable(1.0f);
    planet.BindGroup = CreateSphereBindGroup(planetTexture, transform);

    List<Renderable> asteroids = [
       CreateSphereRenderable(0.01f, 8, 6, 0.15f),
            CreateSphereRenderable(0.013f, 8, 6, 0.15f),
            CreateSphereRenderable(0.017f, 8, 6, 0.15f),
            CreateSphereRenderable(0.02f, 8, 6, 0.15f),
            CreateSphereRenderable(0.03f, 16, 8, 0.15f),
        ];

    List<Renderable> renderables = [planet];

    void EnsureEnoughAsteroids()
    {
        for (int i = renderables.Count; i <= asteroidCount; ++i)
        {
            // Place copies of the asteroid in a ring.
            var radius = Random.Shared.NextSingle() * 1.7f + 1.25f;
            var angle = Random.Shared.NextSingle() * MathF.PI * 2.0f;
            var x = MathF.Sin(angle) * radius;
            var y = (Random.Shared.NextSingle() - 0.5f) * 0.015f;
            var z = MathF.Cos(angle) * radius;

            var transform = Matrix4x4.Identity;
            transform.Translate(new(x, y, z));
            transform.RotateX(MathF.PI * Random.Shared.NextSingle());
            transform.RotateY(MathF.PI * Random.Shared.NextSingle());
            var asteroid = asteroids[i % asteroids.Count];
            renderables.Add(new()
            {
                Vertices = asteroid.Vertices,
                Indices = asteroid.Indices,
                IndexCount = asteroid.IndexCount,
                BindGroup = CreateSphereBindGroup(moonTexture, transform)
            });
        }
    }
    EnsureEnoughAsteroids();

    var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
        fieldOfView: (float)(2.0 * Math.PI / 5.0),
        aspectRatio: ASPECT,
        nearPlaneDistance: 1f,
        farPlaneDistance: 100.0f
    );

    var frameBindGroup = device.CreateBindGroup(new()
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

    Matrix4x4 GetTransformationMatrix()
    {
        var viewMatrix = Matrix4x4.Identity;
        viewMatrix.Translate(new(0, 0, -4));
        var now = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;

        // Tilt the view matrix so the planet looks like it's off-axis.
        viewMatrix.RotateZ(MathF.PI * 0.1f);
        viewMatrix.RotateX(MathF.PI * 0.1f);
        // Rotate the view matrix slowly so the planet appears to spin.
        viewMatrix.RotateY(now * 0.05f);

        return viewMatrix * projectionMatrix;
    }

    // Render bundles function as partial, limited render passes, so we can use the
    // same code both to render the scene normally and to build the render bundle.
    void RenderScene<T>(T passEncoder)
        where T : IRenderCommands
    {
        passEncoder.SetPipeline(pipeline!);
        passEncoder.SetBindGroup(0, frameBindGroup!);

        // Loop through every renderable object and draw them individually.
        // (Because many of these meshes are repeated, with only the transforms
        // differing, instancing would be highly effective here. This sample
        // intentionally avoids using instancing in order to emulate a more complex
        // scene, which helps demonstrate the potential time savings a render bundle
        // can provide.)
        int count = 0;
        foreach (var renderable in renderables)
        {
            passEncoder.SetBindGroup(1, renderable.BindGroup!);
            passEncoder.SetVertexBuffer(0, renderable.Vertices);
            passEncoder.SetIndexBuffer(renderable.Indices, IndexFormat.Uint32);
            passEncoder.DrawIndexed((uint)renderable.IndexCount);

            if (++count > asteroidCount)
            {
                break;
            }
        }
    }

    // The render bundle can be encoded once and re-used as many times as needed.
    // Because it encodes all of the commands needed to render at the GPU level,
    // those commands will not need to execute the associated JavaScript code upon
    // execution or be re-validated, which can represent a significant time savings.
    //
    // However, because render bundles are immutable once created, they are only
    // appropriate for rendering content where the same commands will be executed
    // every time, with the only changes being the contents of the buffers and
    // textures used. Cases where the executed commands differ from frame-to-frame,
    // such as when using frustrum or occlusion culling, will not benefit from
    // using render bundles as much.
    RenderBundle renderBundle;
    void UpdateRenderBundle()
    {
        var renderBundleEncoder = device.CreateRenderBundleEncoder(new()
        {
            ColorFormats = [surfaceFormat],
            DepthStencilFormat = TextureFormat.Depth24Plus,
        });
        RenderScene(renderBundleEncoder);
        renderBundle = renderBundleEncoder.Finish();
    }
    UpdateRenderBundle();

    onFrame(() =>
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
                }
            ],
            DepthStencilAttachment = new RenderPassDepthStencilAttachment()
            {
                View = depthTexture.CreateView(),
                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            },
        });

        if (useRenderBundles)
        {
            // Executing a bundle is equivalent to calling all of the commands encoded
            // in the render bundle as part of the current render pass.
            passEncoder.ExecuteBundles([renderBundle]);
        }
        else
        {
            // Alternatively, the same render commands can be encoded manually, which
            // can take longer.
            RenderScene(passEncoder);
        }

        passEncoder.End();

        var guiCommanderBuffer = DrawGUI(guiContext, surface, out var asteroidCountChanged);
        if (asteroidCountChanged)
        {
            // If the content of the scene changes the render bundle must be recreated.
            EnsureEnoughAsteroids();
            UpdateRenderBundle();
        }

        device.GetQueue().Submit([commandEncoder.Finish(), guiCommanderBuffer]);
        surface.Present();

        var currentFrameTimeStamp = Stopwatch.GetElapsedTime(startTimeStamp).TotalMilliseconds;

        fps = 1000.0 / (currentFrameTimeStamp - lastFrameTimeStamp);

        lastFrameTimeStamp = currentFrameTimeStamp;
    });
});

class Renderable
{
    public required GPUBuffer Vertices { get; set; }
    public required GPUBuffer Indices { get; set; }
    public BindGroup? BindGroup { get; set; }
    public required int IndexCount { get; set; }
}